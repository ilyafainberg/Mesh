using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Talks to the relay: registers the handle (REST) and maintains a SignalR hub connection
/// for sending/receiving <see cref="MeshEnvelope"/>s. SignalR handles transport, framing,
/// keepalive and automatic reconnection; this client adds the device-key auth handshake,
/// end-to-end encryption, and dispatch of inbound messages to the agent and UI.
/// </summary>
public sealed partial class MeshClient :
    IDeviceTopicTransport,
    IOnlineReplicationWakeTransport,
    ITopicEnvelopeTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan GroupKeyCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReplicationActivityQuietPeriod = TimeSpan.FromSeconds(1.5);
    private const int ReplicationBatchSize = 100;
    private readonly AppState state;
    private readonly AgentService agent;
    private readonly ITopicTurnRunner topicTurnRunner;
    private readonly IHttpClientFactory httpFactory;
    private readonly IPushService push;
    private readonly IAppLifecycleState lifecycle;
    private readonly TimeProvider timeProvider;
    private readonly ITopicEnvelopeTransport? topicEnvelopeTransport;
    private readonly TopicControlOutboxDelivery topicControlOutboxDelivery;
    private readonly TopicDurabilityHandler topicDurabilityHandler;
    private readonly TopicRequestOutboxHandler topicRequestOutboxHandler;
    private readonly TopicRequestOutboxDelivery topicRequestOutboxDelivery;
    private readonly TopicAttachmentAssembler attachmentAssembler = new();
    private readonly ConcurrentDictionary<string, ActiveTopicRun> activeTopicRuns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> topicEnvelopeReplay = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> guestAgentGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> inboundTopicExecutionGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> serviceRequestExecutions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> serviceRequestReplay = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> agentRequestExecutions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> agentRequestReplay = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> backgroundTasks = new();
    private long nextBackgroundTaskId;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> keyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> keyCacheUpdated = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim replicationSendGate = new(1, 1);
    private readonly SemaphoreSlim replicationSnapshotSendGate = new(1, 1);
    private readonly SemaphoreSlim replicationDrainGate = new(1, 1);
    private readonly ReplicationActivityTracker replicationActivity = new(ReplicationActivityQuietPeriod);
    private HubConnection? hub;
    private volatile bool authenticated;
    private volatile bool supportsSendResults;
    private volatile bool supportsEphemeralDelivery;
    private volatile bool supportsFanout;
    private volatile bool supportsReplication;
    private volatile bool supportsDeviceRevocation;
    private volatile bool supportsAuthoritativeTopicState;
    private volatile bool supportsAgentHost;
    private volatile bool supportsWakeConnect;
    private readonly SemaphoreSlim onlineFlushGate = new(1, 1);
    private readonly object onlineRecoverySync = new();
    private Task onlineRecoveryTask = Task.CompletedTask;
    private ReplicationConnectionIdentity? pendingOnlineRecoveryIdentity;
    private readonly HashSet<string> pendingOnlineRecoveryTargets = new(StringComparer.Ordinal);
    private bool pendingFullOnlineRecovery;
    private bool onlineRecoveryRunning;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private volatile ReplicationConnectionIdentity? authenticatedReplicationConnectionIdentity;
    private volatile bool wantConnected;   // the user intends to be connected; drives auto-recovery
    private int reconnectScheduled;         // 0/1 guard so only one recovery loop runs at a time
    private readonly TopicDeliveryRetryLoop onlineDeliveryRetry;
    private int shutdownRequested;
    private string? terminalRosterFailureDeviceId;
    private readonly object accountDevicePresenceGate = new();
    private volatile IReadOnlySet<string>? latestAccountOnlineDevices;
    private volatile IReadOnlyDictionary<string, Mesh.Shared.DeviceInfo> latestDeviceDirectory =
        new Dictionary<string, Mesh.Shared.DeviceInfo>(StringComparer.Ordinal);
    private readonly Func<string>? currentPlatformProvider;
    internal ITopicEnvelopeTestFaultScheduler? TopicEnvelopeTestFaultScheduler { get; set; }

    public MeshClient(
        AppState state,
        AgentService agent,
        ITopicTurnRunner topicTurnRunner,
        IHttpClientFactory httpFactory,
        IPushService push,
        IAppLifecycleState lifecycle,
        TimeProvider? timeProvider = null,
        ITopicEnvelopeTransport? topicEnvelopeTransport = null,
        Func<string>? currentPlatformProvider = null)
    {
        this.state = state;
        this.agent = agent;
        this.topicTurnRunner = topicTurnRunner;
        this.httpFactory = httpFactory;
        this.push = push;
        this.lifecycle = lifecycle;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.topicEnvelopeTransport = topicEnvelopeTransport;
        this.currentPlatformProvider = currentPlatformProvider;
        topicControlOutboxDelivery = new TopicControlOutboxDelivery(
            state, topicEnvelopeTransport ?? this, this.timeProvider);
        topicDurabilityHandler = new TopicDurabilityHandler(state, this.timeProvider);
        topicRequestOutboxHandler = new TopicRequestOutboxHandler(
            state, this.timeProvider);
        topicRequestOutboxDelivery = new TopicRequestOutboxDelivery(
            topicRequestOutboxHandler,
            topicEnvelopeTransport ?? this,
            this.timeProvider);
        onlineDeliveryRetry = new TopicDeliveryRetryLoop(
            this.timeProvider,
            TopicTransportPolicy.RemoteAcceptanceRetryInterval,
            AttemptOnlineDeliveryRetryAsync,
            () => wantConnected
                  && ShouldMaintainContinuousTransport
                  && HasLocalDurableWork());
        lifecycle.ForegroundChanged += OnForegroundChanged;
        lifecycle.ForegroundChanged += OnReplicationForegroundChanged;
        state.ActiveAccountChanging += OnActiveAccountChanging;
        replicationActivity.Changed += () => ReplicationStateChanged?.Invoke();
        Microsoft.Maui.Networking.Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
    }

    private void OnActiveAccountChanging()
    {
        Volatile.Write(ref terminalRosterFailureDeviceId, null);
        lock (accountDevicePresenceGate)
        {
            latestAccountOnlineDevices = null;
            latestDeviceDirectory =
                new Dictionary<string, Mesh.Shared.DeviceInfo>(StringComparer.Ordinal);
        }
        var pushIdentity = CapturePushUnregistrationIdentity();
        StopReplicationAsync("active-account-changing").GetAwaiter().GetResult();
        Volatile.Write(ref registeredPushIdentity, null);
        if (pushIdentity is not null)
            TrackBackground(
                UnregisterPushAsync(pushIdentity),
                "push token clear on account change");
    }
    private sealed record ReplicationConnectionIdentity(
        HubConnection Connection,
        string Handle,
        string NormalizedHandle,
        string DeviceId,
        string PublicKey,
        string PrivateKey,
        string RelayUrl);

    private enum ReplicationSendOutcome
    {
        Accepted,
        Deferred,
        TooLarge
    }

    private sealed record ActiveTopicRun(
        string RunId,
        string ThreadId,
        string SourceDeviceId,
        CancellationTokenSource Cancellation)
    {
        public int TerminalSent;
        public int TerminalSending;
        public TopicRunPhase? LastDurablePhase;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    async Task<MeshSendResult?> ITopicEnvelopeTransport.SendAsync(
        string targetDeviceId,
        string kind,
        string plaintext,
        string envelopeId,
        string? pushHint,
        CancellationToken cancellationToken)
    {
        var attempt = new TopicEnvelopeSendAttempt(
            targetDeviceId, kind, plaintext, envelopeId, pushHint);
        return TopicEnvelopeTestFaultScheduler is { } scheduler
            ? await scheduler.SendAsync(
                attempt, SendTopicEnvelopeCoreAsync, cancellationToken).ConfigureAwait(false)
            : await SendTopicEnvelopeCoreAsync(attempt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MeshSendResult?> SendTopicEnvelopeCoreAsync(
        TopicEnvelopeSendAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (topicEnvelopeTransport is not null)
            return await topicEnvelopeTransport.SendAsync(
                attempt.TargetDeviceId,
                attempt.Kind,
                attempt.Plaintext,
                attempt.EnvelopeId,
                attempt.PushHint,
                cancellationToken).ConfigureAwait(false);
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null || !Connected || !IsCurrentReplicationConnectionIdentity(identity))
            return null;
        return await TrySendTargetedTopicEnvelopeCoreAsync(
            identity,
            attempt.TargetDeviceId,
            attempt.Kind,
            attempt.Plaintext,
            attempt.EnvelopeId,
            attempt.PushHint,
            cancellationToken).ConfigureAwait(false);
    }

    public bool Connected => hub?.State == HubConnectionState.Connected && authenticated;
    public bool IsReplicationActive
        => replicationActivity.IsActive
           || CurrentReplicationStatus.Phase is ReplicationPhase.Connecting
               or ReplicationPhase.Synchronizing
               or ReplicationPhase.Bootstrapping;
    public bool SupportsAgentHost => supportsAgentHost;
    public bool SupportsDeviceRevocation => supportsDeviceRevocation;
    public bool SupportsAuthoritativeTopicState => supportsAuthoritativeTopicState;
    public string AgentQuestionKind => supportsAgentHost
        ? MeshKinds.AtomicAgentRequest
        : MeshKinds.Chat;
    public event Action? StateChanged;
    public event Action? ReplicationStateChanged;
    public event Action? AccountDevicePresenceChanged;
    public event Action<string>? Log;

    /// <summary>
    /// This device's stable id, derived from its public signing key. Same derivation the relay uses,
    /// so both agree on the id that targets one specific device (MeshEnvelope.ToDevice). Empty when
    /// this profile has no key yet.
    /// </summary>
    public string MyDeviceId =>
        string.IsNullOrWhiteSpace(state.Profile.PublicKey) ? "" : DeviceProtocol.DeviceId(state.Profile.PublicKey);

    /// <summary>
    /// Give SignalR one immediate transport reconnect. If that fails, let the connection close so
    /// our recovery loop rebuilds the URL and challenge from freshly fetched account authority.
    /// </summary>
    private sealed class FreshAuthorityRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context)
            => context.PreviousRetryCount == 0 ? TimeSpan.Zero : null;
    }

    public async Task<bool> RegisterAsync()
        => await RegisterCurrentDeviceAsync(CancellationToken.None).ConfigureAwait(false)
            == DeviceRosterRegistrationResult.Succeeded;

    private async Task<DeviceRosterRegistrationResult> RegisterCurrentDeviceAsync(
        CancellationToken ct)
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var h = AppState.Norm(p.Handle);
            state.EnsureLocalReplicationAuthority();
            var custody = state.LocalCustodyAuthority(h)
                ?? throw new OnlineReplicationError("No signed local custody authority is available.");
            // Proof of possession: sign the claim with this device's private key so the relay can
            // confirm we control the key we are registering (collision avoidance).
            var sig = IdentityService.Sign(p.PrivateKey, ClaimProtocol.Message(h, p.PublicKey));
            var deviceName = EnsureDeviceName();
            var resp = await http.PostAsJsonAsync($"{p.RelayUrl.TrimEnd('/')}/handles",
                new RegisterHandleRequest(
                    p.Handle,
                    p.PublicKey,
                    p.DisplayName,
                    NullIfBlank(p.RecoveryPublicKey),
                    sig,
                    deviceName,
                    CurrentDevicePlatform,
                    DevicePlatforms.IsDesktop(CurrentDevicePlatform) && agent.IsModelReady,
                    AgentHostEnabled: true,
                    ProtocolVersion: MeshProtocol.Version,
                    CustodyAuthority: custody),
                ct);
            Log?.Invoke($"register {p.Handle}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                Volatile.Write(ref terminalRosterFailureDeviceId, null);
                return DeviceRosterRegistrationResult.Succeeded;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // The handle is claimed by a different device key. If this profile carries the
                // handle's recovery key (for example after a reinstall or profile import), prove
                // ownership and re-authorize this device automatically instead of stranding it.
                if (!string.IsNullOrWhiteSpace(p.RecoveryPrivateKey))
                {
                    Log?.Invoke($"'{p.Handle}' claimed by another device; attempting recovery with the recovery key.");
                    var (ok, err) = await RecoverHandleAsync();
                    if (ok)
                    {
                        Volatile.Write(ref terminalRosterFailureDeviceId, null);
                        Log?.Invoke($"recovered @{p.Handle}: this device is now authorized.");
                        return DeviceRosterRegistrationResult.Succeeded;
                    }
                    Log?.Invoke($"recovery failed for @{p.Handle}: {err}");
                }
                else
                {
                    Log?.Invoke($"'{p.Handle}' is claimed by another identity; link this device or restore your backup.");
                }
                return DeviceRosterRegistrationResult.Rejected;
            }
            var statusCode = (int)resp.StatusCode;
            return statusCode is >= 400 and < 500
                   && resp.StatusCode is not System.Net.HttpStatusCode.RequestTimeout
                   && resp.StatusCode is not System.Net.HttpStatusCode.TooManyRequests
                ? DeviceRosterRegistrationResult.Rejected
                : DeviceRosterRegistrationResult.Unavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OnlineReplicationError ex)
        {
            Log?.Invoke($"register rejected: {ex.Message}");
            return DeviceRosterRegistrationResult.Rejected;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Log?.Invoke($"register rejected: {ex.Message}");
            return DeviceRosterRegistrationResult.Rejected;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"register failed: {ex.Message}");
            return DeviceRosterRegistrationResult.Unavailable;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Returns the device name to register, defaulting it the first time. If the profile has no
    /// device name yet, pick a sensible one (the OS device name where available, else the machine
    /// name) and persist it so the relay can show a friendly label in the device picker.
    /// </summary>
    private string EnsureDeviceName()
    {
        var current = state.Profile.DeviceName;
        if (!string.IsNullOrWhiteSpace(current)) return current;

        var name = "";
        try { name = Microsoft.Maui.Devices.DeviceInfo.Current.Name; } catch { }
        if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(name)) return "";

        state.Mutate(x => x.DeviceName = name);
        return name;
    }

    /// <summary>
    /// Checks whether a handle is already claimed on a relay. Returns true if taken, false if free,
    /// and null if the relay could not be reached (caller should treat null as "unknown" and not
    /// let creation proceed blindly). Used at identity creation to prevent claiming a taken handle.
    /// </summary>
    public async Task<bool?> IsHandleTakenAsync(string handle, string? relayUrl = null)
    {
        var url = (relayUrl ?? state.Profile.RelayUrl).TrimEnd('/');
        var h = AppState.Norm(handle);
        var http = httpFactory.CreateClient("relay");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await http.GetAsync($"{url}/handles/{Uri.EscapeDataString(h)}", cts.Token);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false; // free
            if (resp.IsSuccessStatusCode) return true;                                // claimed
            return null;                                                              // unknown
        }
        catch { return null; }
    }

    /// <summary>
    /// Releases a handle on the relay so its name is free to claim again, authenticated with a
    /// device key registered under it. Best-effort: returns false if the relay rejects it (for
    /// example this device's key was never the registered one) or is unreachable.
    /// </summary>
    public async Task<bool> DeleteHandleAsync(string handle, string privateKey, string publicKey, string? relayUrl = null)
    {
        var url = (relayUrl ?? state.Profile.RelayUrl).TrimEnd('/');
        var h = AppState.Norm(handle);
        if (string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(publicKey)) return false;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var sig = IdentityService.Sign(privateKey, DeleteProtocol.Message(h));
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{url}/handles/{Uri.EscapeDataString(h)}")
            {
                Content = JsonContent.Create(new DeleteHandleRequest(h, publicKey, sig))
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await http.SendAsync(req, cts.Token);
            Log?.Invoke($"delete handle {h}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log?.Invoke($"delete handle failed: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Re-authorizes THIS device under an existing handle using the handle's recovery key (carried
    /// in an imported profile). Used when no other device is available to issue a link invite. The
    /// device signs its own fresh public key with the recovery private key; the relay verifies it
    /// against the recovery public key stored at registration and authorizes this device.
    /// </summary>
    public async Task<(bool ok, string? error)> RecoverHandleAsync()
    {
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.RecoveryPrivateKey))
            return (false, "This profile has no recovery key, so it can't recover a handle on a new device.");
        var http = httpFactory.CreateClient("relay");
        try
        {
            var h = AppState.Norm(p.Handle);
            var sig = IdentityService.Sign(p.RecoveryPrivateKey, RecoveryProtocol.Message(h, p.PublicKey));
            var resp = await http.PostAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/recover",
                new RecoverHandleRequest(h, p.PublicKey, sig));
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"relay {(int)resp.StatusCode}: {body}");
            }
            var authority = await http.GetFromJsonAsync<HandleInfo>(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}");
            if (authority?.CustodyAuthority is null)
                return (false, "The relay did not return signed custody authority after recovery.");
            state.ImportCustodyAuthority(h, authority.CustodyAuthority);
            Volatile.Write(ref terminalRosterFailureDeviceId, null);
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// This (already-authorized) device creates a single-use invite so another device
    /// can join the same handle. Returns the raw code to show as a QR / short code.
    /// </summary>
    public async Task<(bool ok, string? code, string? error)> CreateLinkInviteAsync(TimeSpan? ttl = null)
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var code = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var codeHash = LinkProtocol.HashCode(code);
            var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds();
            var sig = IdentityService.Sign(p.PrivateKey, LinkProtocol.InviteMessage(p.Handle, codeHash, expires));

            var resp = await http.PostAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(AppState.Norm(p.Handle))}/link/invite",
                new LinkInviteRequest(AppState.Norm(p.Handle), p.PublicKey, codeHash, expires, sig));
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"relay {(int)resp.StatusCode}");
            return (true, code, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    /// <summary>
    /// This device redeems an invite to join an existing handle. It keeps its own
    /// keypair (now authorized under the handle) and adopts the handle + display name.
    /// </summary>
    public async Task<(bool ok, string? error)> RedeemLinkAsync(string relayUrl, string handle, string code)
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var h = AppState.Norm(handle);
            var sig = IdentityService.Sign(p.PrivateKey, LinkProtocol.RedeemMessage(h, code));
            var resp = await http.PostAsJsonAsync(
                $"{relayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/link/redeem",
                new LinkRedeemRequest(h, p.PublicKey, code, sig));
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"relay {(int)resp.StatusCode}: {body}");
            }
            var result = await resp.Content.ReadFromJsonAsync<LinkRedeemResponse>();
            var authority = await http.GetFromJsonAsync<HandleInfo>(
                $"{relayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}");
            if (authority?.CustodyAuthority is null)
                return (false, "The relay did not return signed custody authority after device linking.");
            // Adopt the linked identity: this device keeps its own keypair but takes the handle.
            state.Mutate(x =>
            {
                x.Handle = h;
                x.RelayUrl = relayUrl.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(result?.DisplayName)) x.DisplayName = result!.DisplayName!;
            });
            state.ImportCustodyAuthority(h, authority.CustodyAuthority);
            Volatile.Write(ref terminalRosterFailureDeviceId, null);
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task ConnectAsync()
    {
        await using var lease = await EnsureConnectedAsync(
            ConnectionPurpose.Foreground,
            CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task<ReplicationConnectionLease> EnsureConnectedAsync(
        ConnectionPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (MeshProcessContext.IsShuttingDown || Volatile.Read(ref shutdownRequested) != 0)
            return new ReplicationConnectionLease(purpose, isConnected: false);

        if (purpose == ConnectionPurpose.Foreground)
        {
            wantConnected = true;
            if (!ShouldMaintainContinuousTransport)
                return new ReplicationConnectionLease(purpose, isConnected: false);
        }

        var profile = state.Profile;
        if (string.IsNullOrWhiteSpace(profile.Handle) || string.IsNullOrWhiteSpace(profile.RelayUrl))
            return new ReplicationConnectionLease(purpose, isConnected: false);

        var holdsWakeGate = false;
        try
        {
            if (purpose == ConnectionPurpose.BackgroundWake)
            {
                await wakeConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                holdsWakeGate = true;
                Interlocked.Increment(ref backgroundWakeLeaseCount);
            }

            if (!Connected)
                await ConnectCoreAsync(purpose, cancellationToken).ConfigureAwait(false);

            if (!Connected)
            {
                var authentication = connectionAuthentication
                    ?? throw new InvalidOperationException("The relay authentication handshake was not started.");
                var timeout = purpose == ConnectionPurpose.BackgroundWake
                    ? WakeAuthenticationTimeout
                    : TimeSpan.FromSeconds(12);
                using var authBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authBudget.CancelAfter(timeout);
                try
                {
                    await authentication.Task.WaitAsync(authBudget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new OnlineReplicationError("Relay authentication timed out.");
                }
            }

            if (!Connected)
                throw new InvalidOperationException("The relay connection did not authenticate.");
            if (purpose == ConnectionPurpose.BackgroundWake && !supportsWakeConnect)
                throw new OnlineReplicationError("The relay does not support Protocol 9 background wake synchronization.");
            await ArmReplicationAsync(cancellationToken).ConfigureAwait(false);
            if (replicationEngine is null || replicationPoller is null)
                throw new OnlineReplicationError("Protocol 9 replication could not be armed.");

            var leasedConnection = hub;
            return new ReplicationConnectionLease(
                purpose,
                isConnected: true,
                purpose == ConnectionPurpose.BackgroundWake
                    ? () => ReleaseBackgroundWakeLeaseAsync(leasedConnection, allowPromotion: true)
                    : null);
        }
        catch
        {
            if (holdsWakeGate)
                await ReleaseBackgroundWakeLeaseAsync(hub, allowPromotion: false).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ReleaseBackgroundWakeLeaseAsync(
        HubConnection? leasedConnection,
        bool allowPromotion)
    {
        var holdsConnectionGate = false;
        var deferCleanup = false;
        try
        {
            using var gateBudget = new CancellationTokenSource(WakeDisconnectTimeout);
            try
            {
                await connectionGate.WaitAsync(gateBudget.Token).ConfigureAwait(false);
                holdsConnectionGate = true;
            }
            catch (OperationCanceledException) when (gateBudget.IsCancellationRequested)
            {
                TraceTransport("background-release-gate-timeout", "connection gate acquisition timed out");
                deferCleanup = true;
            }

            if (!deferCleanup)
                await CompleteBackgroundWakeReleaseUnderGateAsync(leasedConnection, allowPromotion)
                    .ConfigureAwait(false);
        }
        finally
        {
            if (holdsConnectionGate) connectionGate.Release();
            Interlocked.Decrement(ref backgroundWakeLeaseCount);
            wakeConnectGate.Release();
        }

        if (deferCleanup)
            TrackBackground(
                CompleteDeferredBackgroundWakeReleaseAsync(leasedConnection, allowPromotion),
                "deferred background wake release");
    }

    private async Task CompleteDeferredBackgroundWakeReleaseAsync(
        HubConnection? leasedConnection,
        bool allowPromotion)
    {
        await connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref backgroundWakeLeaseCount) != 0) return;
            await CompleteBackgroundWakeReleaseUnderGateAsync(leasedConnection, allowPromotion)
                .ConfigureAwait(false);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task CompleteBackgroundWakeReleaseUnderGateAsync(
        HubConnection? leasedConnection,
        bool allowPromotion)
    {
        if (leasedConnection is null || !ReferenceEquals(hub, leasedConnection)) return;
        if (allowPromotion && ShouldMaintainContinuousTransport)
        {
            wantConnected = true;
            replicationPoller?.Resume();
            TryRegisterPushToken();
            var identity = authenticatedReplicationConnectionIdentity;
            if (identity is not null)
                WakeOnlineDelivery(identity, "background-wake-promotion");
            return;
        }

        await DisconnectCoreAsync(
            clearConnectionIntent: false,
            timeout: WakeDisconnectTimeout,
            replicationStopReason: "background-wake-release").ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync(ConnectionPurpose purpose, CancellationToken ct)
    {
        var requestedProfile = state.Profile;
        if (string.IsNullOrWhiteSpace(requestedProfile.Handle)
            || string.IsNullOrWhiteSpace(requestedProfile.RelayUrl)) return;
        if (purpose == ConnectionPurpose.Foreground) wantConnected = true;
        if (!ConnectionPurposePolicy.AllowsConnection(
                purpose,
                lifecycle.IsForeground,
                PlatformCaps.IsMobile,
                MeshProcessContext.IsHeadless)) return;

        await connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(
                clearConnectionIntent: false,
                timeout: purpose == ConnectionPurpose.BackgroundWake ? WakeDisconnectTimeout : null,
                replicationStopReason: "connection-replacement")
                .ConfigureAwait(false);
            var p = state.Profile;
            if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.RelayUrl)) return;
            if (purpose == ConnectionPurpose.Foreground) wantConnected = true;
            if (!ConnectionPurposePolicy.AllowsConnection(
                    purpose,
                    lifecycle.IsForeground,
                    PlatformCaps.IsMobile,
                    MeshProcessContext.IsHeadless)) return;
            if (!await DetectRelayCapabilitiesAsync(p.RelayUrl, ct).ConfigureAwait(false))
            {
                StateChanged?.Invoke();
                throw new OnlineReplicationError("The relay is missing required Protocol 9 capabilities.");
            }

            var normHandle = AppState.Norm(p.Handle);
            var rosterReconciliation = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
                p.PublicKey,
                async cancellationToken =>
                {
                    var info = await ((IReplicationMetadataSource)this)
                        .FetchHandleAsync(normHandle, cancellationToken)
                        .ConfigureAwait(false);
                    return info?.DevicePublicKeys;
                },
                async cancellationToken =>
                {
                    return await RegisterCurrentDeviceAsync(cancellationToken).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            if (!rosterReconciliation.Converged)
            {
                var reason = rosterReconciliation.Remediation;
                Log?.Invoke(
                    $"device roster reconciliation {rosterReconciliation.State}: " +
                    $"fetches={rosterReconciliation.FetchAttempts}; registrations={rosterReconciliation.RegistrationAttempts}; " +
                    reason);
                SetReplicationStatus(
                    rosterReconciliation.IsTerminal
                        ? ReplicationPhase.AuthenticationFailed
                        : ReplicationPhase.Failed,
                    reason: reason);
                if (rosterReconciliation.IsTerminal)
                    Volatile.Write(ref terminalRosterFailureDeviceId, MyDeviceId);
                throw new OnlineReplicationError(reason);
            }
            Volatile.Write(ref terminalRosterFailureDeviceId, null);

            var connectDeviceId = MyDeviceId;
            // Read this handle's own relay authority so the connect query and the signed connect
            // challenge both bind the current auth generation and custody head the landed hub verifies
            // (MeshHub.OnConnectedAsync / Authenticate). A genesis handle legitimately reports 0 / "".
            long connectAuthGeneration = 0;
            string connectCustodyHead = "";
            try
            {
                var ownInfo = await ((IReplicationMetadataSource)this)
                    .FetchHandleAsync(normHandle, ct);
                if (ownInfo is null)
                    throw new OnlineReplicationError(
                        "The relay did not return this account's replication authority.");
                connectAuthGeneration = ownInfo.AuthGeneration;
                connectCustodyHead = ownInfo.CustodyHead ?? "";
            }
            catch (OnlineReplicationError)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"replication authority lookup failed: {ex.Message}");
                throw new InvalidOperationException(
                    "Could not establish the account's Protocol 9 authority.",
                    ex);
            }
            this.connectAuthGeneration = connectAuthGeneration;
            this.connectCustodyHead = connectCustodyHead;

            var authenticationReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connectionAuthentication = authenticationReady;
            var url = $"{p.RelayUrl.TrimEnd('/')}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(normHandle)}&protocolVersion={MeshProtocol.Version}&deviceId={Uri.EscapeDataString(connectDeviceId)}&authGeneration={connectAuthGeneration}&custodyHead={Uri.EscapeDataString(connectCustodyHead)}";
            var connection = new HubConnectionBuilder()
                .WithUrl(url)
                .WithAutomaticReconnect(new FreshAuthorityRetry())
                .Build();

            connection.On<HandshakeResponse>(MeshHubProtocol.Handshake, response =>
            {
                if (response.Result == HandshakeResult.Accepted
                    && response.ServerVersion == MeshProtocol.Version)
                    return;
                authenticated = false;
                authenticatedReplicationConnectionIdentity = null;
                authenticationReady.TrySetException(new OnlineReplicationError(
                    response.Error ?? $"Relay protocol mismatch: expected {MeshProtocol.Version}, got {response.ServerVersion}."));
                Log?.Invoke(response.Error ?? $"relay protocol mismatch: expected {MeshProtocol.Version}, got {response.ServerVersion}");
                TrackBackground(connection.StopAsync(), "protocol mismatch disconnect");
            });

            // The relay opens with a nonce challenge; sign the canonical connect string (nonce bound to
            // handle, device, protocol version and current custody authority) so the landed hub can
            // verify current authority, not just replay resistance.
            connection.On<string>(MeshHubProtocol.Challenge, async nonce =>
            {
                try
                {
                    var canonical = ReplicationConnectChallenge.Canonical(
                        nonce,
                        AppState.Norm(state.Profile.Handle),
                        MyDeviceId,
                        MeshProtocol.Version,
                        this.connectAuthGeneration,
                        this.connectCustodyHead);
                    var sig = IdentityService.Sign(state.Profile.PrivateKey, canonical);
                    await connection.SendAsync(MeshHubProtocol.Authenticate, state.Profile.PublicKey, sig);
                }
                catch (Exception ex) { authenticationReady.TrySetException(ex); Log?.Invoke($"auth failed: {ex.Message}"); }
            });

            connection.On<PresenceConfirmed>(MeshHubProtocol.PresenceConfirmed, _ =>
            {
                authenticated = true;
                var identity = CaptureReplicationConnectionIdentity(connection);
                authenticatedReplicationConnectionIdentity = identity;
                Log?.Invoke("hub connected + authenticated");
                authenticationReady.TrySetResult(true);
                StateChanged?.Invoke();
                if (ShouldMaintainContinuousTransport)
                {
                    TryRegisterPushToken();
                    if (identity is not null)
                    {
                        WakeOnlineDelivery(identity, "connection-authenticated");
                        TrackBackground(MaintainOnlineDeliveryAsync(identity), "durable delivery maintenance");
                    }
                }
                // Continuous desktop/mobile-foreground connects arm asynchronously. A background wake is
                // armed by its bounded EnsureConnectedAsync call so it cannot leave a duplicate task behind.
                if (ShouldMaintainContinuousTransport)
                    TrackBackground(ArmReplicationAsync(), "online replication arm");
            });


            connection.On<string>(MeshHubProtocol.Receive, async envelopeJson =>
            {
                MeshEnvelope? env;
                try { env = JsonSerializer.Deserialize<MeshEnvelope>(envelopeJson, Json); }
                catch { return; }
                if (env is null) return;
                try
                {
                    var mode = lifecycle.IsForeground
                        ? InboundProcessingMode.Foreground
                        : InboundProcessingMode.Background;
                    await ProcessInboundAsync(
                        env, mode, authenticatedReplicationConnectionIdentity, supportsReplication, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    TraceTransport("receive-failed", ex.Message);
                }
            });

            // A reconnect re-runs the server's challenge automatically (the handler stays registered),
            // so we just reflect the transient unauthenticated state in the UI.
            connection.Reconnecting += _ =>
            {
                authenticated = false;
                authenticatedReplicationConnectionIdentity = null;
                StateChanged?.Invoke();
                return Task.CompletedTask;
            };
            connection.Reconnected += _ =>
            {
                if (purpose == ConnectionPurpose.Foreground) StartAuthWatchdog(connection);
                StateChanged?.Invoke();
                return Task.CompletedTask;
            };
            connection.Closed += _ =>
            {
                authenticated = false;
                authenticatedReplicationConnectionIdentity = null;
                authenticationReady.TrySetException(new InvalidOperationException("The relay connection closed before authentication completed."));
                StateChanged?.Invoke();
                // SignalR's own auto-reconnect has given up by the time Closed fires. If the user still
                // wants to be connected, keep trying ourselves so a long drop does not strand us offline.
                ScheduleRecovery();
                return Task.CompletedTask;
            };

            hub = connection;
            // Protocol-9 online replication: opaque Relay send + Deliver/PresenceChanged/Wake inbound.
            RegisterOnlineReplicationHandlers(connection);
            try
            {
                await connection.StartAsync(ct).ConfigureAwait(false);
                StateChanged?.Invoke();
                if (purpose == ConnectionPurpose.Foreground) StartAuthWatchdog(connection);
            }
            catch (Exception ex)
            {
                authenticationReady.TrySetException(ex);
                Log?.Invoke($"hub connect failed: {ex.Message}");
                StateChanged?.Invoke();
                ScheduleRecovery();
                if (purpose == ConnectionPurpose.BackgroundWake) throw;
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private sealed record RelayCapabilities(
        int ProtocolVersion,
        bool SendResults,
        bool EphemeralDelivery,
        bool Fanout,
        bool Replication,
        bool SnapshotTransferV2,
        bool DeviceRevocation,
        bool AuthoritativeTopicState,
        bool AgentHost,
        bool OnlineDelivery,
        bool WakeConnect);

    private async Task<RelayCapabilities> ReadRelayCapabilitiesAsync(
        string relayUrl,
        CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient("relay");
        using var response = await http.GetAsync($"{relayUrl.TrimEnd('/')}/health", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("capabilities", out var capabilities))
            return new RelayCapabilities(0, false, false, false, false, false, false, false, false, false, false);
        var protocolVersion = capabilities.TryGetProperty("protocolVersion", out var versionElement)
                              && versionElement.TryGetInt32(out var parsedVersion)
            ? parsedVersion
            : 0;
        if (protocolVersion != MeshProtocol.Version)
            return new RelayCapabilities(protocolVersion, false, false, false, false, false, false, false, false, false, false);
        return new RelayCapabilities(
            protocolVersion,
            capabilities.TryGetProperty("sendResults", out var results)
                && results.ValueKind == JsonValueKind.True,
            capabilities.TryGetProperty("ephemeralDelivery", out var ephemeralDelivery)
                && ephemeralDelivery.ValueKind == JsonValueKind.True,
            capabilities.TryGetProperty("fanout", out var fanout)
                && fanout.ValueKind == JsonValueKind.True,
            capabilities.TryGetProperty("replication", out var replication)
                && replication.ValueKind == JsonValueKind.True,
            true,
            capabilities.TryGetProperty("deviceRevocation", out var deviceRevocation)
                && deviceRevocation.ValueKind == JsonValueKind.True,
            capabilities.TryGetProperty("authoritativeTopicState", out var authoritativeTopicState)
                && authoritativeTopicState.ValueKind == JsonValueKind.True,
            capabilities.TryGetProperty("agentHost", out var agentHostCap)
                && agentHostCap.ValueKind == JsonValueKind.True,
            capabilities.TryGetProperty("onlineDelivery", out var onlineDelivery)
                && onlineDelivery.ValueKind == JsonValueKind.True,
            OnlineReplicationWakeCapabilityPolicy.IsSupported(capabilities));
    }

    private async Task<bool> DetectRelayCapabilitiesAsync(
        string relayUrl,
        CancellationToken ct = default)
    {
        supportsSendResults = false;
        supportsEphemeralDelivery = false;
        supportsFanout = false;
        supportsReplication = false;
        supportsDeviceRevocation = false;
        supportsAuthoritativeTopicState = false;
        supportsAgentHost = false;
        supportsWakeConnect = false;
        try
        {
            var capabilities = await ReadRelayCapabilitiesAsync(relayUrl, ct).ConfigureAwait(false);
            if (capabilities.ProtocolVersion != MeshProtocol.Version)
            {
                Log?.Invoke($"relay protocol mismatch: expected {MeshProtocol.Version}, got {capabilities.ProtocolVersion}");
                return false;
            }
            if (!capabilities.SendResults
                || !capabilities.Replication
                || !capabilities.SnapshotTransferV2
                || !capabilities.OnlineDelivery)
            {
                Log?.Invoke("relay is missing required online replication capabilities");
                return false;
            }
            supportsSendResults = capabilities.SendResults;
            supportsEphemeralDelivery = capabilities.EphemeralDelivery;
            supportsFanout = capabilities.Fanout;
            supportsReplication = capabilities.Replication;
            supportsDeviceRevocation = capabilities.DeviceRevocation;
            supportsAuthoritativeTopicState = capabilities.AuthoritativeTopicState;
            supportsAgentHost = capabilities.AgentHost;
            supportsWakeConnect = capabilities.WakeConnect;
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"relay capability detection failed: {ex.Message}");
            return false;
        }
    }
    /// <summary>
    /// Guards the auth handshake: if the connection is up but the challenge/response never completes
    /// (a mid-handshake hiccup leaves us connected-but-not-authenticated, with no Closed event to
    /// trigger recovery), force a fresh reconnect so we do not sit silently offline.
    /// </summary>
    private void StartAuthWatchdog(HubConnection connection)
    {
        TrackBackground(Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (!ReferenceEquals(hub, connection)) return; // superseded by a newer connection
            if (wantConnected && ShouldMaintainContinuousTransport
                && !authenticated && connection.State == HubConnectionState.Connected)
            {
                Log?.Invoke("auth watchdog: connected but not authenticated, reconnecting");
                ScheduleRecovery();
                try { await connection.StopAsync(); } catch { } // triggers Closed -> recovery loop
            }
        }), "authentication watchdog");
    }

    /// <summary>
    /// Background recovery: while the user wants to be connected but the hub is not connected, keep
    /// reconnecting with backoff. Only one loop runs at a time. This covers the case where SignalR's
    /// automatic reconnect has exhausted and fired Closed (e.g. after a long sleep or network loss).
    /// </summary>
    private void ScheduleRecovery()
    {
        if (MeshProcessContext.IsShuttingDown || Volatile.Read(ref shutdownRequested) != 0
            || !wantConnected || !ShouldMaintainContinuousTransport
            || string.Equals(
                Volatile.Read(ref terminalRosterFailureDeviceId),
                MyDeviceId,
                StringComparison.Ordinal)) return;
        if (Interlocked.Exchange(ref reconnectScheduled, 1) == 1) return;
        TrackBackground(Task.Run(async () =>
        {
            try
            {
                var delay = TimeSpan.FromSeconds(2);
                while (wantConnected && ShouldMaintainContinuousTransport)
                {
                    await Task.Delay(delay, timeProvider, CancellationToken.None);
                    if (!wantConnected) break;
                    if (Connected)
                    {
                        var identity = authenticatedReplicationConnectionIdentity;
                        if (identity is not null)
                            WakeOnlineDelivery(identity, "connection-recovered");
                        break;
                    }
                    if (hub?.State is HubConnectionState.Connecting
                        or HubConnectionState.Reconnecting)
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                        continue;
                    }
                    if (hub?.State == HubConnectionState.Connected)
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                        continue;
                    }

                    try
                    {
                        Log?.Invoke("recovery: reconnecting to relay");
                        await ConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"recovery attempt failed: {ex.Message}");
                    }
                    if (Connected) break;
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
            finally
            {
                Interlocked.Exchange(ref reconnectScheduled, 0);
                if (wantConnected && ShouldMaintainContinuousTransport
                    && !Connected && hub?.State == HubConnectionState.Disconnected)
                    ScheduleRecovery();
            }
        }), "relay recovery");
    }
    private async Task HandleInboundAsync(
        MeshEnvelope env,
        InboundProcessingMode mode,
        ReplicationConnectionIdentity? sessionIdentity,
        bool sessionSupportsReplication,
        CancellationToken ct,
        Action<Func<Task>>? registerPostAcknowledgement = null)
    {
        var from = AppState.Norm(env.From);
        if (env.Kind is MeshKinds.RemoteAgentRequest or MeshKinds.RemoteAgentResponse)
        {
            Log?.Invoke($"dropped retired remote-agent envelope {env.Kind}");
            return;
        }
        if (string.Equals(env.Kind, MeshKinds.AtomicAgentRequest, StringComparison.Ordinal)
            && (!string.Equals(env.ToDevice, MyDeviceId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(env.AgentRequestId)
                || !string.Equals(env.AgentRequestId, env.Id, StringComparison.Ordinal)
                || false))
        {
            Log?.Invoke("dropped atomic agent request with invalid assignment metadata");
            return;
        }
        var isGroupKind = env.Kind is MeshKinds.GroupControl or MeshKinds.GroupMessage or MeshKinds.Fanout;
        var isReplication = false;
        var isTopicKind = env.Kind is MeshKinds.TopicRunRequest
            or MeshKinds.TopicRunUpdate
            or MeshKinds.TopicRunCancel
            or MeshKinds.AttachmentChunk
            or MeshKinds.TopicAttachmentChunk;
        var isOwnDeviceKind = isReplication
            || isTopicKind;
        if (env.Kind == MeshKinds.Fanout
            && env.ToDevice is not null
            && !string.Equals(env.ToDevice, MyDeviceId, StringComparison.Ordinal))
            return;

        // A delivery receipt: mark our matching outgoing line as delivered. Receipts are plaintext
        // (they carry only a message id, no content) and are verified below like any other envelope.
        if (env.Kind == MeshKinds.Receipt)
        {
            var pinnedR = state.FindContact(from)?.SigningKeys.ToList() ?? new List<string>();
            if (pinnedR.Count == 0) pinnedR = (await ResolveDeviceKeysAsync(from)).ToList();
            if (pinnedR.Count == 0)
                throw new InboundRetryException("receipt_keys_unavailable");
            if (!MeshCrypto.VerifyAny(pinnedR, env.Body, env.Signature ?? ""))
                throw new InboundPermanentRejectException("receipt_signature_invalid");
            var msgId = ReceiptProtocol.MessageId(env.Body);
            if (!string.IsNullOrEmpty(msgId)) state.SetLineStatus(msgId!, "delivered");
            return;
        }

        // Client-side verification: check the sender's signature against their pinned signing
        // keys (trust on first use). This defends against a malicious or compromised relay
        // forging or tampering with messages. On first contact we fetch and pin the keys.
        ReplicationConnectionIdentity? inboundOwnDeviceIdentity = null;
        var requireCurrentIdentity = mode == InboundProcessingMode.Foreground;
        var ownDeviceKeysRefreshed = false;
        var ownDeviceDirectoryAvailable = true;
        List<string> pinned;
        if (isOwnDeviceKind)
        {
            inboundOwnDeviceIdentity = sessionIdentity ?? authenticatedReplicationConnectionIdentity;
            if (inboundOwnDeviceIdentity is null)
                throw new InboundRetryException("own_device_identity_unavailable");
            pinned = (await ResolveDeviceKeysAsync(from)).ToList();
            if (!IsReplicationConnectionIdentityUsable(inboundOwnDeviceIdentity, requireCurrentIdentity))
                throw new InboundRetryException("own_device_identity_changed");
            if (isTopicKind)
            {
                var resolution = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
                    pinned,
                    env.FromDevice ?? "",
                    () => RefreshAuthoritativeDeviceKeysAsync(from, ct));
                pinned = resolution.Keys.ToList();
                ownDeviceKeysRefreshed = resolution.Refreshed;
                ownDeviceDirectoryAvailable = resolution.DirectoryAvailable;
                if (!IsReplicationConnectionIdentityUsable(
                        inboundOwnDeviceIdentity,
                        requireCurrentIdentity))
                    throw new InboundRetryException("own_device_identity_changed");
            }
        }
        else
        {
            pinned = state.FindContact(from)?.SigningKeys.ToList() ?? new List<string>();
            if (pinned.Count == 0)
                pinned = (await ResolveDeviceKeysAsync(from)).ToList();
        }
        if (pinned.Count == 0)
        {
            if (isOwnDeviceKind
                && isTopicKind
                && ownDeviceKeysRefreshed
                && ownDeviceDirectoryAvailable)
                throw new InboundPermanentRejectException(
                    "own_device_not_authorized: re-link or recover the sending device");
            throw new InboundRetryException(
                isOwnDeviceKind ? "own_device_keys_unavailable" : "sender_keys_unavailable");
        }
        var signatureValid = MeshCrypto.VerifyAny(pinned, env.Body, env.Signature ?? "");
        if (!signatureValid
            && isOwnDeviceKind
            && inboundOwnDeviceIdentity is not null
            && !ownDeviceKeysRefreshed)
        {
            pinned = (await ResolveDeviceKeysAsync(from, refresh: true)).ToList();
            if (isTopicKind)
                pinned = pinned.Where(key =>
                        string.Equals(DeviceProtocol.DeviceId(key), env.FromDevice, StringComparison.Ordinal))
                    .ToList();
            if (!IsReplicationConnectionIdentityUsable(inboundOwnDeviceIdentity, requireCurrentIdentity))
                throw new InboundRetryException("own_device_identity_changed");
            if (pinned.Count == 0)
                throw new InboundRetryException("own_device_keys_unavailable");
            signatureValid = MeshCrypto.VerifyAny(pinned, env.Body, env.Signature ?? "");
        }
        if (!signatureValid)
        {
            if (isOwnDeviceKind)
                Log?.Invoke($"dropped {env.Kind} from device {env.FromDevice}: signature verification failed");
            // The sender's keys no longer match what we pinned: the contact's identity may have
            // changed (rotation, reinstall, or an impostor). Surface it for re-verification instead
            // of silently dropping, and do not auto-repin (that would defeat trust on first use).
            else
            {
                state.FlagContactKeyChanged(from);
                Log?.Invoke($"identity change: message from @{from} did not match pinned keys (re-verify)");
            }
            throw new InboundPermanentRejectException("signature_invalid");
        }

        if (isTopicKind)
        {
            await HandleInboundTopicAsync(env, from, mode, ct);
            return;
        }

        if (isGroupKind)
        {
            if (env.Kind == MeshKinds.Fanout)
            {
                HandleInboundFanout(env, from);
                return;
            }
            await HandleInboundGroupAsync(env, from);
            return;
        }

        var isAtomicAgentEnvelope = string.Equals(env.Kind, MeshKinds.AtomicAgentRequest, StringComparison.Ordinal)
                                    || string.Equals(env.Kind, MeshKinds.AtomicAgentResponse, StringComparison.Ordinal);
        if (isAtomicAgentEnvelope && !MessageCrypto.IsEncrypted(env.Body))
        {
            Log?.Invoke($"dropped {env.Kind} from @{from}: encryption is required");
            return;
        }

        // Decrypt end-to-end payloads addressed to this device. Plaintext bodies pass through.
        var text = env.Body;
        if (MessageCrypto.IsEncrypted(env.Body))
        {
            var (ok, plain) = MessageCrypto.TryDecrypt(env.Body, state.Profile.PrivateKey, state.Profile.PublicKey);
            if (!ok && isAtomicAgentEnvelope)
            {
                Log?.Invoke($"dropped {env.Kind} from @{from}: this device could not decrypt it");
                return;
            }
            text = ok ? plain! : "[encrypted message this device can't read]";
        }

        // Public service invocation. Handled BEFORE the allow-list gate below: any handle may invoke a
        // public-listed service, so it must not be dropped into the request staging for non-contacts.
        // The answer comes from a hard-sandboxed, service-scoped agent (public KB/Skills/Widgets only,
        // no tools of any kind).
        if (env.Kind == MeshKinds.ServiceRequest)
        {
            await HandleServiceRequestOnceAsync(env, from, text, ct);
            return;
        }
        if (env.Kind == MeshKinds.ServiceResponse)
        {
            var (svcId, answer) = ServiceProtocol.Parse(text);
            // Land the reply in the dedicated service thread (not the provider's person DM), so the
            // consumer can keep a real multi-turn conversation with the service.
            var conv = state.FindConversation(AppState.ServiceKey(from, svcId))
                       ?? state.GetOrCreateServiceConversation(from, svcId, null);
            if (conv.Lines.Any(line => string.Equals(line.Id, env.Id, StringComparison.Ordinal))) return;
            state.ClearAwaiting(conv.Handle);
            state.AddChatLine(conv.Handle, new ChatLine
            {
                Id = env.Id,
                Role = "user",
                Text = answer,
                Via = "agent",
                AddressedToAgent = true
            });
            state.MarkUnread(conv.Handle);
            await PublishLegacyNotificationAsync($"message:{env.Id}", NotificationKind.ServiceResponse, conv.Handle, NotificationRoutes.Messages(conv.Handle), $"{conv.ServiceName} replied", answer, ct);
            return;
        }

        if (env.Kind == MeshKinds.Report)
        {
            // Inbound AI-content report (this device is signed in as the reserved report handle).
            // Render it as a readable message from the reporter so the operator can review it.
            var payload = ReportProtocol.Parse(text);
            var rendered = payload is null ? text : FormatReport(payload);
            if (state.FindConversation(from)?.Lines.Any(line =>
                    string.Equals(line.Id, env.Id, StringComparison.Ordinal)) == true)
                return;
            state.AddChatLine(from, new ChatLine
            {
                Id = env.Id,
                Role = "user",
                Text = rendered,
                Via = "person"
            });
            state.MarkUnread(from);
            await PublishLegacyNotificationAsync($"message:{env.Id}", NotificationKind.Message, from, NotificationRoutes.Messages(from), "New report", rendered, ct);
            return;
        }

        var contact = state.FindContact(from);
        var allowed = contact?.Allowed == true;
        var display = state.DisplayNameFor(from);

        // Blocked contact: drop entirely (no record, no agent, no toast).
        if (contact?.Blocked == true)
        {
            Log?.Invoke($"dropped message from blocked @{from}");
            return;
        }

        // Record the inbound line. Anything routed through an agent (a reply from the peer's
        // agent, or a request their agent addressed to ours) is tagged "agent"; a message a
        // person typed to the human (chat or a direct message) is "person". Chat still engages
        // our guest agent below, but for labeling/history it is treated as person-authored.
        var via = env.Kind is MeshKinds.AgentResponse or MeshKinds.AgentRequest
            or MeshKinds.AtomicAgentRequest or MeshKinds.AtomicAgentResponse
            ? "agent"
            : "person";
        var receiptable = env.Kind is MeshKinds.DirectMessage or MeshKinds.Chat
            or MeshKinds.AgentRequest or MeshKinds.AgentResponse
            or MeshKinds.AtomicAgentRequest or MeshKinds.AtomicAgentResponse;
        if (state.FindConversation(from)?.Lines.Any(line =>
                string.Equals(line.Id, env.Id, StringComparison.Ordinal)) == true)
        {
            if (receiptable) TrackBackground(SendReceiptAsync(from, env.Id), "duplicate delivery receipt");
            if (env.Kind == MeshKinds.AtomicAgentRequest && allowed && agent.IsModelReady)
                await HandleAgentQuestionOnceAsync(env, from, text, display, ct);
            return;
        }
        state.AddChatLine(from, new ChatLine
        {
            Id = env.Id,
            Role = "user",
            Text = text,
            Via = via,
            AddressedToAgent = via == "agent"
        });

        // Acknowledge receipt of any real message so the sender sees "delivered".
        if (receiptable)
            TrackBackground(SendReceiptAsync(from, env.Id), "delivery receipt");

        // A person-to-person message to the human: mark unread and toast the owner (unless muted/DND).
        if (env.Kind == MeshKinds.DirectMessage)
        {
            state.MarkUnread(from);
            await PublishLegacyNotificationAsync(
                $"message:{env.Id}", NotificationKind.Message, from, NotificationRoutes.Messages(from),
                $"Message from {display}", text, ct);
        }

        // A response to this device's own outbound agent request is solicited traffic. It is shown
        // in the conversation but must never create a new contact approval request merely because
        // the responder is not allowed to initiate independent agent questions.
        if (env.Kind is MeshKinds.AgentResponse or MeshKinds.AtomicAgentResponse)
        {
            state.MarkUnread(from);
            StateChanged?.Invoke();
            return;
        }

        if (!allowed)
        {
            // Unknown/!allowed -> drop into request staging, do NOT engage the agent.
            var isNew = false;
            state.Mutate(x =>
            {
                if (!x.Requests.Any(r => r.From == from))
                {
                    x.Requests.Add(new PendingRequest { From = from, Body = text });
                    isNew = true;
                }
            });
            if (isNew)
                await PublishLegacyNotificationAsync(
                    $"request:{from}", NotificationKind.ContactRequest, from, NotificationRoutes.Requests,
                    $"Request from @{from}", text, ct);
            Log?.Invoke($"inbound from @{from} held for approval");
            StateChanged?.Invoke();
            return;
        }

        // Allowed -> guest agent drafts a scoped reply, subject to the daily cost budget.
        if ((env.Kind is MeshKinds.Chat or MeshKinds.AgentRequest or MeshKinds.AtomicAgentRequest)
            && agent.IsModelReady)
            await HandleAgentQuestionOnceAsync(env, from, text, display, ct);
        StateChanged?.Invoke();
    }

    private async Task HandleServiceRequestOnceAsync(
        MeshEnvelope envelope,
        string from,
        string text,
        CancellationToken ct)
    {
        if (serviceRequestReplay.ContainsKey(envelope.Id)) return;
        var execution = serviceRequestExecutions.GetOrAdd(
            envelope.Id,
            _ => new Lazy<Task>(
                () => HandleServiceRequestAsync(envelope, from, text, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var completed = false;
        try
        {
            await execution.Value;
            completed = true;
        }
        finally
        {
            if (completed) RememberReplay(serviceRequestReplay, envelope.Id);
            if (serviceRequestExecutions.TryGetValue(envelope.Id, out var current)
                && ReferenceEquals(current, execution))
                serviceRequestExecutions.TryRemove(envelope.Id, out _);
        }
    }

    private async Task HandleServiceRequestAsync(
        MeshEnvelope envelope,
        string from,
        string text,
        CancellationToken ct)
    {
        if (!PlatformCaps.CanHostServices)
        {
            Log?.Invoke("service request ignored: service hosting is desktop-only");
            return;
        }
        var (serviceId, turns) = ServiceProtocol.ParseRequest(text);
        var svc = state.Profile.PublishedServices.FirstOrDefault(s => s.Id == serviceId);
        if (svc is null || !svc.Published) return;
        if (!agent.IsModelReady) return;

        if (svc.IsBudgetExhausted(from))
        {
            await SendAsync(
                from,
                MeshKinds.ServiceResponse,
                ServiceProtocol.Body(serviceId,
                    "This service has reached its usage budget and is not accepting requests right now."),
                StableEnvelopeId("service.response", envelope.Id),
                toDevice: envelope.FromDevice);
            Log?.Invoke($"service '{serviceId}' refused for @{from}: budget exhausted");
            return;
        }

        if (svc.IsRateLimited(from))
        {
            await SendAsync(
                from,
                MeshKinds.ServiceResponse,
                ServiceProtocol.Body(serviceId,
                    "You have reached this service's daily request limit. Please try again tomorrow."),
                StableEnvelopeId("service.response", envelope.Id),
                toDevice: envelope.FromDevice);
            Log?.Invoke($"service '{serviceId}' refused for @{from}: daily rate limit");
            return;
        }

        if (!state.TryConsumeAgentReply()) return;
        state.Mutate(_ => svc.RecordRequest(from));
        var svcHistory = turns
            .Select(t => new ChatLine
            {
                Role = t.Role == "user" ? "user" : "assistant",
                Text = t.Text,
                Via = "agent"
            })
            .ToList();
        if (svcHistory.Count == 0)
            svcHistory.Add(new ChatLine { Role = "user", Text = "" });

        var reply = await agent.RespondAsServiceAsync(serviceId, from, svcHistory, ct);
        if (ModelReply.IsFailure(reply.Text))
        {
            state.RefundAgentReply();
            Log?.Invoke($"service '{serviceId}' reply to @{from} skipped: model unavailable");
            return;
        }

        if (reply.Tokens > 0)
            state.Mutate(_ => svc.RecordSpend(from, reply.Tokens));
        await SendAsync(
            from,
            MeshKinds.ServiceResponse,
            ServiceProtocol.Body(serviceId, reply.Text),
            StableEnvelopeId("service.response", envelope.Id),
            toDevice: envelope.FromDevice);
    }

    private async Task HandleAgentQuestionAsync(
        MeshEnvelope request,
        string from,
        string text,
        string display,
        CancellationToken ct)
    {
        var gate = guestAgentGates.GetOrAdd(from, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!state.TryConsumeAgentReply())
            {
                Log?.Invoke($"agent reply to @{from} skipped: daily budget reached");
                return;
            }

            var conv = state.GetOrCreateConversation(from);
            var reply = await agent.RespondAsGuestAsync(from, conv.Lines.ToList(), ct);
            if (ModelReply.IsFailure(reply))
            {
                state.RefundAgentReply();
                Log?.Invoke($"agent reply to @{from} skipped: model unavailable");
                return;
            }

            var atomic = string.Equals(request.Kind, MeshKinds.AtomicAgentRequest, StringComparison.Ordinal);
            if (state.RequiresApproval(from))
            {
                var approval = new PendingApproval
                {
                    From = from,
                    RequestBody = text,
                    DraftReply = reply,
                    AgentRequestId = atomic ? request.AgentRequestId : null,
                    FromDevice = atomic ? request.FromDevice : null
                };
                state.Mutate(x => x.Approvals.Add(approval));
                await PublishLegacyNotificationAsync(
                    $"approval:{approval.Id}", NotificationKind.ApprovalRequired, approval.Id,
                    NotificationRoutes.Approvals, "Reply needs your approval",
                    $"Your agent drafted a reply to {display}.", ct);
                Log?.Invoke($"draft reply to @{from} awaiting approval");
                return;
            }

            var line = new ChatLine { Role = "assistant", Text = reply, AddressedToAgent = false };
            state.AddChatLine(from, line);
            await SendAsync(
                from,
                atomic ? MeshKinds.AtomicAgentResponse : MeshKinds.AgentResponse,
                reply,
                line.Id,
                toDevice: atomic ? request.FromDevice : null,
                agentRequestId: atomic ? request.AgentRequestId : null);
        }

        finally
        {
            gate.Release();
        }
    }

    private async Task HandleAgentQuestionOnceAsync(
        MeshEnvelope request,
        string from,
        string text,
        string display,
        CancellationToken ct)
    {
        var key = request.AgentRequestId ?? request.Id;
        if (agentRequestReplay.ContainsKey(key)) return;
        var execution = agentRequestExecutions.GetOrAdd(
            key,
            _ => new Lazy<Task>(
                () => HandleAgentQuestionAsync(request, from, text, display, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var completed = false;
        try
        {
            await execution.Value;
            completed = true;
        }
        finally
        {
            if (completed) RememberReplay(agentRequestReplay, key);
            if (agentRequestExecutions.TryGetValue(key, out var current)
                && ReferenceEquals(current, execution))
                agentRequestExecutions.TryRemove(key, out _);
        }
    }

    private async Task HandleInboundTopicAsync(
        MeshEnvelope env, string from, InboundProcessingMode mode, CancellationToken ct)
    {
        var me = AppState.Norm(state.Profile.Handle);
        if (!string.Equals(from, me, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(env.FromDevice)
            || string.Equals(env.FromDevice, MyDeviceId, StringComparison.Ordinal)
            || !string.Equals(env.ToDevice, MyDeviceId, StringComparison.Ordinal))
            throw new InboundPermanentRejectException("topic_route_invalid");
        if (!MessageCrypto.IsEncrypted(env.Body))
            throw new InboundPermanentRejectException("topic_encryption_required");
        var (decrypted, plaintext) = MessageCrypto.TryDecrypt(
            env.Body, state.Profile.PrivateKey, state.Profile.PublicKey);
        if (!decrypted || plaintext is null)
            throw new InboundPermanentRejectException("topic_decryption_failed");
        if (string.Equals(env.Kind, MeshKinds.TopicRunRequest, StringComparison.Ordinal))
            await EnsureCurrentDeviceCanHostRemoteTopicAsync(ct).ConfigureAwait(false);

        switch (env.Kind)
        {
            case MeshKinds.AttachmentChunk:
            case MeshKinds.TopicAttachmentChunk:
                if (!TopicRunProtocol.TryParseChunk(plaintext, out var chunk))
                    throw new InboundPermanentRejectException("topic_attachment_payload_invalid");
                if (topicEnvelopeReplay.ContainsKey(env.Id))
                {
                    Log?.Invoke($"dropped replayed topic envelope {env.Id}");
                    return;
                }
                if (!attachmentAssembler.TryAdd(env.FromDevice, chunk, out var chunkError))
                {
                    if (InboundAttachmentFailurePolicy.ShouldRetry(chunkError))
                        throw new InboundRetryException("topic_attachment_retry:" + chunkError);
                    throw new InboundPermanentRejectException(
                        "topic_attachment_rejected:" + chunkError);
                }
                RememberReplay(topicEnvelopeReplay, env.Id);
                break;

            case MeshKinds.TopicRunRequest:
                if (!TopicRunProtocol.TryParseRequest(plaintext, out var request))
                    throw new InboundPermanentRejectException("topic_request_payload_invalid");
                if (!string.Equals(request.TargetDeviceId, MyDeviceId, StringComparison.Ordinal)
                    || !string.Equals(AppState.Norm(request.TriggerHandle), me, StringComparison.Ordinal))
                {
                    attachmentAssembler.RejectRun(env.FromDevice, request.RunId);
                    throw new InboundPermanentRejectException("topic_request_route_invalid");
                }
                if (await TryHandlePreCancelledInboundRequestAsync(request, env.FromDevice, ct))
                    return;
                if (!EnsureInboundTopicContext(request))
                {
                    throw new InboundRetryException("topic_context_unavailable");
                }

                MeshDb.InboundTopicRunItem record;
                try
                {
                    record = topicDurabilityHandler.AcceptRequest(
                        request, env.FromDevice);
                }
                catch (InvalidOperationException ex) when (
                    string.Equals(
                        ex.Message,
                        "topic_request_identity_conflict",
                        StringComparison.Ordinal))
                {
                    throw new InboundPermanentRejectException("topic_request_identity_conflict");
                }
                if (string.Equals(record.State, InboundTopicRunStates.Accepted, StringComparison.Ordinal))
                {
                    var accepted = TopicAcceptancePolicy.Create(
                        record.Request,
                        record.AcceptedAt);
                    if (!await SendTargetedTopicEnvelopeAsync(
                            record.SourceDeviceId,
                            MeshKinds.TopicRunUpdate,
                            TopicRunProtocol.UpdateBody(accepted),
                            ct))
                        throw new InboundRetryException("topic_acceptance_delivery_failed");
                }
                if (record.State is InboundTopicRunStates.Completed
                    or InboundTopicRunStates.Failed
                    or InboundTopicRunStates.Cancelled
                    or InboundTopicRunStates.Interrupted)
                {
                    if (!TopicRunProtocol.TryParseUpdate(record.TerminalUpdateJson, out var terminal)
                        || !await SendTargetedTopicEnvelopeAsync(
                            record.SourceDeviceId,
                            MeshKinds.TopicRunUpdate,
                            TopicRunProtocol.UpdateBody(terminal),
                            ct,
                            PushHintProtocol.ForTopicRunPhase(terminal.Phase)))
                        throw new InboundRetryException("topic_terminal_delivery_failed");
                    return;
                }
                if (string.Equals(record.State, InboundTopicRunStates.Running, StringComparison.Ordinal)
                    && !activeTopicRuns.ContainsKey(record.RunId))
                {
                    QueueInterruptedInboundRun(record, "remote_execution_interrupted");
                    return;
                }
                if (!TryStartInboundTopicRun(record.Request, record.SourceDeviceId))
                    throw new InvalidOperationException("The inbound topic run could not be scheduled.");
                break;

            case MeshKinds.TopicRunUpdate:
                if (!TopicRunProtocol.TryParseUpdate(plaintext, out var update))
                    throw new InboundPermanentRejectException("topic_update_payload_invalid");
                if (TopicControlProtocol.IsReceipt(update))
                {
                    HandleTopicControlReceipt(update, env.FromDevice, env.Id);
                    RememberReplay(topicEnvelopeReplay, env.Id);
                    return;
                }
                var requiresReceipt =
                    TopicControlProtocol.RequiresPersistenceReceipt(update);
                var receivedControl = requiresReceipt
                    ? state.GetReceivedTopicControl(env.Id)
                    : null;
                if (receivedControl is not null)
                {
                    if (!ReceivedControlMatches(
                            receivedControl, env.FromDevice, update, plaintext))
                        throw new InboundPermanentRejectException(
                            "topic_control_identity_conflict");
                    if (!await SendTopicControlReceiptAsync(
                            update, env.FromDevice, ct))
                        throw new InboundRetryException(
                            "topic_control_receipt_delivery_failed");
                    RememberReplay(topicEnvelopeReplay, env.Id);
                    return;
                }
                if (topicEnvelopeReplay.ContainsKey(env.Id) && !requiresReceipt)
                {
                    Log?.Invoke($"dropped replayed topic envelope {env.Id}");
                    return;
                }
                if (!state.IsExpectedTopicRunCorrelation(
                        update, env.FromDevice, allowRetained: requiresReceipt))
                {
                    throw new InboundPermanentRejectException("topic_update_not_correlated");
                }
                if (mode == InboundProcessingMode.Background && TopicRunBackgroundPolicy.ShouldDefer(update))
                {
                    if (!state.SaveDeferredTopicRunUpdate(env.Id, update))
                        throw new InboundRetryException("topic_update_defer_persistence_failed");
                    RememberReplay(topicEnvelopeReplay, env.Id);
                    return;
                }
                if (requiresReceipt)
                {
                    var persistence = topicDurabilityHandler.HandleControl(
                        update, env.FromDevice, env.Id);
                    if (persistence == RemoteTopicUpdatePersistenceResult.IdentityConflict)
                        throw new InboundPermanentRejectException(
                            "topic_control_identity_conflict");
                    if (persistence == RemoteTopicUpdatePersistenceResult.NotCorrelated)
                        throw new InboundPermanentRejectException(
                            "topic_update_not_correlated");
                    if (persistence == RemoteTopicUpdatePersistenceResult.PersistenceFailed)
                        throw new InboundRetryException(
                            "topic_update_persistence_failed");
                    if (!await SendTopicControlReceiptAsync(
                            update, env.FromDevice, ct))
                        throw new InboundRetryException(
                            "topic_control_receipt_delivery_failed");
                }
                else if (topicDurabilityHandler.HandleUpdate(
                             update, env.FromDevice, env.Id)
                         is not (RemoteTopicUpdatePersistenceResult.Applied
                             or RemoteTopicUpdatePersistenceResult.Ignored
                             or RemoteTopicUpdatePersistenceResult.Duplicate))
                {
                    throw new InboundRetryException("topic_update_persistence_failed");
                }
                if (update.Phase is TopicRunPhase.Completed or TopicRunPhase.Failed or TopicRunPhase.Cancelled)
                    state.DeleteDeferredTopicRunUpdates(update.RunId);
                RememberReplay(topicEnvelopeReplay, env.Id);
                StateChanged?.Invoke();
                break;

            case MeshKinds.TopicRunCancel:
                if (!TopicRunProtocol.TryParseCancel(plaintext, out var cancel))
                    throw new InboundPermanentRejectException("topic_cancel_payload_invalid");
                if (topicEnvelopeReplay.ContainsKey(env.Id))
                {
                    Log?.Invoke($"dropped replayed topic envelope {env.Id}");
                    return;
                }
                await HandleInboundTopicCancellationAsync(cancel, env.FromDevice, ct);
                RememberReplay(topicEnvelopeReplay, env.Id);
                break;
        }
    }

    private void HandleTopicControlReceipt(
        TopicRunUpdatePayload receipt,
        string sourceDeviceId,
        string envelopeId)
    {
        var persistence = topicDurabilityHandler.HandleReceipt(
            receipt, sourceDeviceId);
        if (persistence == TopicControlReceiptPersistenceResult.NotCorrelated)
            throw new InboundPermanentRejectException(
                "topic_control_receipt_not_correlated");
        if (persistence == TopicControlReceiptPersistenceResult.IdentityConflict)
            throw new InboundPermanentRejectException(
                "topic_control_receipt_identity_conflict");
        TraceTransport(
            "topic-control-persisted",
            $"run={AppState.StableDiagnosticId(receipt.RunId)}"
            + $";envelope={AppState.StableDiagnosticId(envelopeId)}"
            + $";result={persistence.ToString().ToLowerInvariant()}");
    }

    private async Task<bool> SendTopicControlReceiptAsync(
        TopicRunUpdatePayload control,
        string targetDeviceId,
        CancellationToken ct)
    {
        var receipt = TopicControlProtocol.CreateReceipt(control);
        return await SendTargetedTopicEnvelopeAsync(
            targetDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(receipt),
            ct).ConfigureAwait(false);
    }

    private static bool ReceivedControlMatches(
        MeshDb.ReceivedTopicControlItem existing,
        string sourceDeviceId,
        TopicRunUpdatePayload update,
        string plaintext)
        => string.Equals(
               existing.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
           && string.Equals(existing.RunId, update.RunId, StringComparison.Ordinal)
           && string.Equals(existing.ThreadId, update.ThreadId, StringComparison.Ordinal)
           && string.Equals(
               existing.ControlKind,
               TopicControlProtocol.ControlPurpose(update),
               StringComparison.Ordinal)
           && string.Equals(existing.UpdateJson, plaintext, StringComparison.Ordinal);

    private async Task<bool> TryHandlePreCancelledInboundRequestAsync(
        TopicRunRequestPayload request,
        string sourceDeviceId,
        CancellationToken ct)
    {
        var cancellation = state.GetInboundTopicCancellation(request.RunId);
        if (cancellation is null) return false;
        if (!string.Equals(cancellation.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
            || !string.Equals(cancellation.ThreadId, request.ThreadId, StringComparison.Ordinal)
            || !TopicRunProtocol.TryParseUpdate(cancellation.TerminalUpdateJson, out var cancelled))
            throw new InboundPermanentRejectException("topic_cancellation_identity_conflict");

        var now = timeProvider.GetUtcNow();
        _ = state.TryAcceptInboundTopicRun(new MeshDb.InboundTopicRunItem(
            request.RunId,
            sourceDeviceId,
            request,
            InboundTopicRunStates.Cancelled,
            cancellation.CreatedAt,
            now,
            cancellation.TerminalUpdateJson));
        var record = state.GetInboundTopicRun(request.RunId)
                     ?? throw new InboundRetryException("topic_cancellation_persistence_failed");
        if (!string.Equals(record.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
            || !string.Equals(record.Request.ThreadId, request.ThreadId, StringComparison.Ordinal)
            || !string.Equals(
                TopicRunProtocol.RequestBody(record.Request),
                TopicRunProtocol.RequestBody(request),
                StringComparison.Ordinal))
            throw new InboundPermanentRejectException("topic_request_identity_conflict");
        var terminal = TopicRunProtocol.TryParseUpdate(record.TerminalUpdateJson, out var winner)
            ? winner
            : PersistInboundTopicTerminal(
                request.RunId, InboundTopicRunStates.Cancelled, cancelled, sourceDeviceId);
        if (!await SendTargetedTopicEnvelopeAsync(
                sourceDeviceId,
                MeshKinds.TopicRunUpdate,
                TopicRunProtocol.UpdateBody(terminal),
                ct,
                PushHintProtocol.ForTopicRunPhase(terminal.Phase)))
            throw new InboundRetryException("topic_cancellation_delivery_failed");
        return true;
    }

    private async Task HandleInboundTopicCancellationAsync(
        TopicRunCancelPayload cancel,
        string sourceDeviceId,
        CancellationToken ct)
    {
        if (activeTopicRuns.TryGetValue(cancel.RunId, out var activeRun)
            && string.Equals(activeRun.ThreadId, cancel.ThreadId, StringComparison.Ordinal)
            && string.Equals(activeRun.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal))
        {
            await activeRun.SendGate.WaitAsync(ct);
            try
            {
                var record = state.GetInboundTopicRun(cancel.RunId)
                             ?? throw new InboundRetryException("topic_cancellation_persistence_failed");
                var proposed = new TopicRunUpdatePayload(
                    cancel.RunId,
                    cancel.ThreadId,
                    TopicRunPhase.Cancelled,
                    Status: "Cancelled",
                    Timestamp: timeProvider.GetUtcNow(),
                    TriggerLineId: record.Request.TriggerLineId);
                var terminal = PersistInboundTopicTerminal(
                    cancel.RunId,
                    InboundTopicRunStates.Cancelled,
                    proposed,
                    sourceDeviceId);
                activeRun.Cancellation.Cancel();
                if (!await SendTargetedTopicEnvelopeAsync(
                        sourceDeviceId,
                        MeshKinds.TopicRunUpdate,
                        TopicRunProtocol.UpdateBody(terminal),
                        ct,
                        PushHintProtocol.ForTopicRunPhase(terminal.Phase)))
                    throw new InboundRetryException("topic_cancellation_delivery_failed");
                Volatile.Write(ref activeRun.TerminalSent, 1);
            }
            finally
            {
                activeRun.SendGate.Release();
            }
            return;
        }

        var pending = state.GetInboundTopicRun(cancel.RunId);
        if (pending is not null)
        {
            if (!string.Equals(pending.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
                || !string.Equals(pending.Request.ThreadId, cancel.ThreadId, StringComparison.Ordinal))
                throw new InboundPermanentRejectException("topic_cancellation_identity_conflict");
            var proposed = new TopicRunUpdatePayload(
                cancel.RunId,
                cancel.ThreadId,
                TopicRunPhase.Cancelled,
                Status: "Cancelled",
                Timestamp: timeProvider.GetUtcNow(),
                TriggerLineId: pending.Request.TriggerLineId);
            var terminal = TopicRunProtocol.TryParseUpdate(pending.TerminalUpdateJson, out var winner)
                ? winner
                : PersistInboundTopicTerminal(
                    cancel.RunId, InboundTopicRunStates.Cancelled, proposed, sourceDeviceId);
            if (!await SendTargetedTopicEnvelopeAsync(
                    sourceDeviceId,
                    MeshKinds.TopicRunUpdate,
                    TopicRunProtocol.UpdateBody(terminal),
                    ct,
                    PushHintProtocol.ForTopicRunPhase(terminal.Phase)))
                throw new InboundRetryException("topic_cancellation_delivery_failed");
            return;
        }

        var cancelled = new TopicRunUpdatePayload(
            cancel.RunId,
            cancel.ThreadId,
            TopicRunPhase.Cancelled,
            Status: "Cancelled",
            Timestamp: timeProvider.GetUtcNow());
        var item = new MeshDb.InboundTopicCancellationItem(
            cancel.RunId,
            sourceDeviceId,
            cancel.ThreadId,
            TopicRunProtocol.UpdateBody(cancelled),
            timeProvider.GetUtcNow());
        if (!state.SaveInboundTopicCancellation(item))
            throw new InboundRetryException("topic_cancellation_persistence_failed");
        var persisted = state.GetInboundTopicCancellation(cancel.RunId);
        if (persisted is null
            || !TopicRunProtocol.TryParseUpdate(persisted.TerminalUpdateJson, out var terminalUpdate))
            throw new InboundRetryException("topic_cancellation_persistence_failed");
        if (!await SendTargetedTopicEnvelopeAsync(
                sourceDeviceId,
                MeshKinds.TopicRunUpdate,
                TopicRunProtocol.UpdateBody(terminalUpdate),
                ct,
                PushHintProtocol.ForTopicRunPhase(terminalUpdate.Phase)))
            throw new InboundRetryException("topic_cancellation_delivery_failed");
    }
    private async Task ExecuteInboundTopicRunAsync(
        TopicRunRequestPayload request,
        ActiveTopicRun active)
    {
        var executionGate = inboundTopicExecutionGates.GetOrAdd(
            request.ThreadId, static _ => new SemaphoreSlim(1, 1));
        var enteredExecutionGate = false;
        try
        {
            await executionGate.WaitAsync(active.Cancellation.Token);
            enteredExecutionGate = true;
            await SendProgressUpdateAsync(active, new TopicRunUpdatePayload(
                active.RunId,
                active.ThreadId,
                TopicRunPhase.Queued,
                Status: "Queued",
                Timestamp: timeProvider.GetUtcNow(),
                TriggerLineId: request.TriggerLineId));
            var attachments = await FetchInboundAttachmentsAsync(request, active);
            var draft = new TopicTurnDraft(
                request.RunId,
                request.ThreadId,
                request.TriggerLineId,
                AppState.Norm(request.TriggerHandle),
                request.TriggerText,
                request.TriggerAt,
                request.TurnMode,
                request.TargetDeviceId,
                request.WidgetId,
                request.WidgetContext,
                attachments);
            var progress = new OrderedAsyncProgress<TopicRunUpdatePayload>(
                update => TopicControlProtocol.IsTerminal(update)
                    ? Task.CompletedTask
                    : SendProgressUpdateAsync(active, CorrelateUpdate(active, update)),
                ex => Log?.Invoke($"topic progress {active.RunId} failed: {ex.Message}"));
            TopicRunCompletion completion;
            try
            {
                completion = await topicTurnRunner.ExecuteAsync(
                    draft,
                    progress,
                    active.Cancellation.Token,
                    _ =>
                    {
                        if (!state.SetInboundTopicRunState(
                                active.RunId, InboundTopicRunStates.Running))
                            throw new InvalidOperationException(
                                "The inbound topic run state could not be persisted.");
                        return Task.CompletedTask;
                    });
            }
            finally
            {
                await progress.CompleteAsync();
            }
            var resultLine = completion.Phase == TopicRunPhase.Completed
                ? state.Profile.OwnThreads
                    .FirstOrDefault(thread => string.Equals(
                        thread.Id, active.ThreadId, StringComparison.Ordinal))
                    ?.Lines.LastOrDefault(line =>
                        string.Equals(line.Role, "assistant", StringComparison.Ordinal)
                        && string.Equals(
                            line.ReplyToLineId, request.TriggerLineId, StringComparison.Ordinal))
                : null;
            var terminalUpdate = new TopicRunUpdatePayload(
                active.RunId,
                active.ThreadId,
                completion.Phase,
                Error: completion.Error,
                FailureCode: completion.FailureCode,
                Timestamp: completion.CompletedAt == default
                    ? timeProvider.GetUtcNow()
                    : completion.CompletedAt,
                TriggerLineId: request.TriggerLineId,
                Result: resultLine is null
                    ? null
                    : new TopicRunResultPayload(
                        resultLine.Id,
                        resultLine.Text,
                        resultLine.At,
                        resultLine.ModelId,
                        resultLine.Reasoning));
            await SendTerminalOnceAsync(active, terminalUpdate, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            var cancelledUpdate = new TopicRunUpdatePayload(
                active.RunId,
                active.ThreadId,
                TopicRunPhase.Cancelled,
                Status: "Cancelled",
                Timestamp: timeProvider.GetUtcNow(),
                TriggerLineId: request.TriggerLineId);
            await SendTerminalOnceAsync(active, cancelledUpdate, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"topic run {active.RunId} failed: {ex.Message}");
            var failedUpdate = new TopicRunUpdatePayload(
                active.RunId,
                active.ThreadId,
                TopicRunPhase.Failed,
                Error: "The remote device could not complete this run.",
                FailureCode: "remote_execution_failed",
                Timestamp: timeProvider.GetUtcNow(),
                TriggerLineId: request.TriggerLineId);
            await SendTerminalOnceAsync(active, failedUpdate, CancellationToken.None);
        }
        finally
        {
            if (enteredExecutionGate) executionGate.Release();
            activeTopicRuns.TryRemove(active.RunId, out _);
            attachmentAssembler.RejectRun(active.SourceDeviceId, active.RunId);
            active.Cancellation.Dispose();
            StartNextInboundTopicRun(active.ThreadId);
        }
    }
    // Retrieves a topic run's attachments. Senders upload each attachment to blob storage and carry an
    // encrypted pointer in the request, which we download and decrypt here. The legacy chunk-staging
    // path remains only for a request that somehow arrives without pointers.
    private async Task<IReadOnlyList<ChatAttachment>> FetchInboundAttachmentsAsync(
        TopicRunRequestPayload request,
        ActiveTopicRun active)
    {
        var manifest = request.Attachments ?? Array.Empty<TopicRunAttachment>();
        if (manifest.Count > 0 && manifest.All(item => AttachmentProtocol.IsValidBlobId(item.BlobId)))
        {
            var downloaded = new List<ChatAttachment>(manifest.Count);
            foreach (var item in manifest)
            {
                var attachment = await DownloadAttachmentAsync(
                    new AttachmentPointer(
                        item.BlobId!, item.Name, item.MimeType, item.Length, item.Key!, item.Sha256!),
                    active.Cancellation.Token);
                if (attachment is null)
                    throw new InvalidDataException(
                        $"Attachment '{item.Name}' could not be retrieved from storage.");
                downloaded.Add(attachment);
            }
            return downloaded;
        }

        return await attachmentAssembler.WaitForAsync(
            active.SourceDeviceId,
            request.RunId,
            request.Attachments,
            request.AttachmentIds,
            active.Cancellation.Token);
    }

    private TopicRunUpdatePayload CorrelateUpdate(
        ActiveTopicRun active,
        TopicRunUpdatePayload update)
        => update with
        {
            RunId = active.RunId,
            ThreadId = active.ThreadId,
            Timestamp = update.Timestamp == default ? timeProvider.GetUtcNow() : update.Timestamp
        };

    private async Task SendProgressUpdateAsync(
        ActiveTopicRun active,
        TopicRunUpdatePayload update)
    {
        if (update.Phase is TopicRunPhase.Completed
            or TopicRunPhase.Failed
            or TopicRunPhase.Cancelled)
        {
            await SendTerminalOnceAsync(active, update, CancellationToken.None);
            return;
        }
        if (Volatile.Read(ref active.TerminalSent) != 0
            || Volatile.Read(ref active.TerminalSending) != 0)
            return;
        await active.SendGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref active.TerminalSent) != 0
                || Volatile.Read(ref active.TerminalSending) != 0)
                return;

            if (!supportsEphemeralDelivery)
            {
                if (await SendTargetedTopicEnvelopeAsync(
                        active.SourceDeviceId,
                        MeshKinds.TopicRunUpdate,
                        TopicRunProtocol.UpdateBody(update),
                        CancellationToken.None))
                    active.LastDurablePhase = update.Phase;
                return;
            }

            var phaseChanged = active.LastDurablePhase != update.Phase;
            if (phaseChanged)
            {
                var durable = TopicExecutionState(update);
                if (await SendTargetedTopicEnvelopeAsync(
                        active.SourceDeviceId,
                        MeshKinds.TopicRunUpdate,
                        TopicRunProtocol.UpdateBody(durable),
                        CancellationToken.None))
                    active.LastDurablePhase = update.Phase;
            }

            if (HasTransientTopicProgress(update) || !phaseChanged)
                _ = await SendEphemeralTopicUpdateAsync(active, update);
        }
        finally
        {
            active.SendGate.Release();
        }
    }

    private async Task<bool> SendEphemeralTopicUpdateAsync(
        ActiveTopicRun active,
        TopicRunUpdatePayload update)
    {
        var identity = authenticatedReplicationConnectionIdentity;
        if (!supportsEphemeralDelivery
            || identity is null
            || !Connected
            || !IsCurrentReplicationConnectionIdentity(identity))
            return false;
        var result = await TrySendTargetedTopicEnvelopeCoreAsync(
            identity,
            active.SourceDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(update),
            Guid.NewGuid().ToString("n"),
            null,
            CancellationToken.None,
            ephemeral: true);
        return result?.Accepted == true;
    }

    private static bool HasTransientTopicProgress(TopicRunUpdatePayload update)
        => update.Plan is not null
           || update.Subtasks is { Count: > 0 }
           || update.Steps is { Count: > 0 }
           || update.Delta is { Length: > 0 };

    private static TopicRunUpdatePayload TopicExecutionState(TopicRunUpdatePayload update)
        => update with
        {
            Status = update.Phase switch
            {
                TopicRunPhase.Queued => update.Status ?? "Queued",
                TopicRunPhase.Planning => "Planning",
                TopicRunPhase.Executing => "Running",
                TopicRunPhase.Verifying => "Verifying",
                _ => update.Status
            },
            Plan = null,
            Subtasks = null,
            Steps = null,
            Error = null,
            FailureCode = null,
            DeltaSeq = 0,
            DeltaKind = null,
            Delta = null
        };

    private TopicRunUpdatePayload PersistInboundTopicTerminal(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate,
        string targetDeviceId)
    {
        return topicDurabilityHandler.CompleteRun(
            runId, runState, terminalUpdate, targetDeviceId);
    }

    private static string InboundTopicTerminalState(TopicRunPhase phase) => phase switch
    {
        TopicRunPhase.Completed => InboundTopicRunStates.Completed,
        TopicRunPhase.Cancelled => InboundTopicRunStates.Cancelled,
        TopicRunPhase.Failed => InboundTopicRunStates.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "A terminal phase is required.")
    };

    private async Task SendTerminalOnceAsync(
        ActiveTopicRun active,
        TopicRunUpdatePayload update,
        CancellationToken ct)
    {
        if (Interlocked.Exchange(ref active.TerminalSending, 1) != 0) return;
        await active.SendGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref active.TerminalSent) != 0) return;
            var proposed = CorrelateUpdate(active, update);
            var terminal = PersistInboundTopicTerminal(
                active.RunId, InboundTopicTerminalState(proposed.Phase), proposed, active.SourceDeviceId);
            if (await SendTargetedTopicEnvelopeAsync(
                    active.SourceDeviceId,
                    MeshKinds.TopicRunUpdate,
                    TopicRunProtocol.UpdateBody(terminal),
                    ct,
                    PushHintProtocol.ForTopicRunPhase(terminal.Phase)))
                Volatile.Write(ref active.TerminalSent, 1);
        }
        finally
        {
            Volatile.Write(ref active.TerminalSending, 0);
            active.SendGate.Release();
        }
    }

    private static bool RememberReplay(
        ConcurrentDictionary<string, DateTimeOffset> cache,
        string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var now = DateTimeOffset.UtcNow;
        const int maxReplayEntries = TopicAttachmentAssembler.MaxPendingRuns
                                     * AttachmentChunkProtocol.MaxChunks
                                     * 8;
        if (cache.Count >= maxReplayEntries)
        {
            foreach (var pair in cache)
                if (now - pair.Value > TimeSpan.FromHours(1))
                    cache.TryRemove(pair.Key, out _);
            if (cache.Count >= maxReplayEntries) return false;
        }
        return cache.TryAdd(id, now);
    }

    private void TrackBackground(Task task, string operation)
    {
        var id = Interlocked.Increment(ref nextBackgroundTaskId);
        backgroundTasks[id] = task;
        task.ContinueWith(completed =>
        {
            backgroundTasks.TryRemove(id, out _);
            if (completed.IsFaulted)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "background-operation-failed",
                    $"operation={operation};exception="
                    + (completed.Exception?.GetBaseException().GetType().FullName ?? "unknown"));
                Log?.Invoke($"{operation} failed: "
                            + (completed.Exception?.GetBaseException().Message ?? "unknown error"));
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private ReplicationConnectionIdentity? CaptureReplicationConnectionIdentity(HubConnection connection)
    {
        var p = state.Profile;
        var handle = AppState.Norm(p.Handle);
        var deviceId = string.IsNullOrWhiteSpace(p.PublicKey)
            ? ""
            : DeviceProtocol.DeviceId(p.PublicKey);
        if (string.IsNullOrWhiteSpace(handle)
            || string.IsNullOrWhiteSpace(deviceId)
            || string.IsNullOrWhiteSpace(p.PublicKey)
            || string.IsNullOrWhiteSpace(p.PrivateKey))
            return null;
        return new ReplicationConnectionIdentity(
            connection,
            p.Handle,
            handle,
            deviceId,
            p.PublicKey,
            p.PrivateKey,
            p.RelayUrl.TrimEnd('/'));
    }

    private bool MatchesActiveProfile(ReplicationConnectionIdentity identity)
    {
        var p = state.Profile;
        return identity.Connection.State == HubConnectionState.Connected
            && string.Equals(AppState.Norm(p.Handle), identity.NormalizedHandle, StringComparison.Ordinal)
            && string.Equals(p.PublicKey, identity.PublicKey, StringComparison.Ordinal)
            && string.Equals(p.PrivateKey, identity.PrivateKey, StringComparison.Ordinal)
            && string.Equals(p.RelayUrl.TrimEnd('/'), identity.RelayUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(MyDeviceId, identity.DeviceId, StringComparison.Ordinal);
    }

    private bool IsReplicationConnectionIdentityUsable(ReplicationConnectionIdentity identity, bool requireCurrent)
        => requireCurrent ? IsCurrentReplicationConnectionIdentity(identity) : MatchesActiveProfile(identity);

    private bool IsCurrentReplicationConnectionIdentity(ReplicationConnectionIdentity identity)
        => MatchesActiveProfile(identity)
            && ReferenceEquals(hub, identity.Connection)
            && authenticated
            && ReferenceEquals(authenticatedReplicationConnectionIdentity, identity);

    private Task HandleInboundGroupAsync(MeshEnvelope env, string from)
    {
        if (!MessageCrypto.IsEncrypted(env.Body))
        {
            Log?.Invoke($"dropped {env.Kind} from @{from}: group body was not encrypted");
            return Task.CompletedTask;
        }

        var (decrypted, plaintext) = MessageCrypto.TryDecrypt(
            env.Body, state.Profile.PrivateKey, state.Profile.PublicKey);
        if (!decrypted || plaintext is null)
        {
            Log?.Invoke($"dropped {env.Kind} from @{from}: group body could not be decrypted");
            return Task.CompletedTask;
        }

        if (env.Kind == MeshKinds.GroupControl)
            HandleInboundGroupControl(plaintext, from);
        else
            HandleInboundGroupMessage(plaintext, from);
        return Task.CompletedTask;
    }

    private void HandleInboundFanout(MeshEnvelope env, string from)
    {
        if (!MessageCrypto.IsEncrypted(env.Body))
        {
            Log?.Invoke($"dropped fanout from @{from}: body was not encrypted");
            return;
        }

        var (decrypted, plaintext) = MessageCrypto.TryDecrypt(
            env.Body, state.Profile.PrivateKey, state.Profile.PublicKey);
        if (!decrypted || plaintext is null)
        {
            Log?.Invoke($"dropped fanout from @{from}: body could not be decrypted");
            return;
        }

        try
        {
            var content = JsonSerializer.Deserialize<MeshFanoutContent>(plaintext, Json)
                ?? throw new JsonException("Fan-out content was null.");
            if (content.Kind == MeshKinds.GroupControl)
                HandleInboundGroupControl(content.Payload, from);
            else if (content.Kind == MeshKinds.GroupMessage)
                HandleInboundGroupMessage(content.Payload, from);
            else
                Log?.Invoke($"dropped fanout from @{from}: unsupported inner kind '{content.Kind}'");
        }
        catch (JsonException ex)
        {
            Log?.Invoke($"dropped fanout from @{from}: invalid content ({ex.Message})");
        }
    }

    private void HandleInboundGroupControl(string plaintext, string from)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<GroupSnapshotPayload>(plaintext, Json)
                ?? throw new JsonException("Group snapshot was null.");
            ValidateGroupSnapshotShape(snapshot);

            var owner = AppState.Norm(snapshot.OwnerHandle);
            var me = AppState.Norm(state.Profile.Handle);
            if (!string.Equals(owner, from, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The group snapshot sender is not its owner.");
            if (!snapshot.MemberHandles.Any(h =>
                    string.Equals(AppState.Norm(h), me, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The current user is not a group member.");

            var existing = state.FindGroupConversation(snapshot.GroupId);
            if (existing is not null && snapshot.Version > existing.GroupVersion)
                throw new InvalidOperationException("Group membership updates are not supported in the MVP.");

            var group = state.ApplyGroupSnapshot(snapshot);
            if (existing is null)
                TrackBackground(PublishLegacyNotificationAsync(
                    $"group:{group.GroupId}:invite", NotificationKind.ServiceInvite, group.Handle,
                    NotificationRoutes.Messages(group.Handle), $"Added to {group.GroupName}",
                    $"Group created by @{from}."), "group invitation notification");
        }
        catch (JsonException ex)
        {
            Log?.Invoke($"dropped group control from @{from}: invalid JSON ({ex.Message})");
        }
        catch (ArgumentException ex)
        {
            Log?.Invoke($"dropped group control from @{from}: invalid snapshot ({ex.Message})");
        }
        catch (InvalidOperationException ex)
        {
            Log?.Invoke($"dropped group control from @{from}: {ex.Message}");
        }
    }

    private void HandleInboundGroupMessage(string plaintext, string from)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<GroupMessagePayload>(plaintext, Json)
                ?? throw new JsonException("Group message was null.");
            ValidateGroupMessageShape(payload);

            var sender = AppState.Norm(payload.SenderHandle);
            if (!string.Equals(sender, from, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The group message sender does not match its envelope.");

            var group = state.FindGroupConversation(payload.GroupId)
                ?? throw new InvalidOperationException("The group is not known locally.");
            ValidateLocalGroup(group);

            var me = AppState.Norm(state.Profile.Handle);
            if (!group.GroupMembers.Contains(sender, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sender is not a current group member.");
            if (!group.GroupMembers.Contains(me, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The current user is not a current group member.");
            if (!string.Equals(payload.GroupId, group.GroupId, StringComparison.Ordinal)
                || payload.MembershipVersion != group.GroupVersion)
                throw new InvalidOperationException("The group metadata version does not match local state.");
            if (group.Lines.Any(l => string.Equals(l.Id, payload.MessageId, StringComparison.Ordinal)))
                return;

            state.AddChatLine(group.Handle, new ChatLine
            {
                Id = payload.MessageId,
                Role = "user",
                Text = payload.Text,
                Via = "person",
                SenderHandle = sender,
                At = payload.SentAt
            });
            state.MarkUnread(group.Handle);
            TrackBackground(PublishLegacyNotificationAsync(
                $"message:{payload.MessageId}", NotificationKind.Message, group.Handle,
                NotificationRoutes.Messages(group.Handle), group.GroupName!,
                $"@{sender}: {payload.Text}"), "group message notification");
        }
        catch (JsonException ex)
        {
            Log?.Invoke($"dropped group message from @{from}: invalid JSON ({ex.Message})");
        }
        catch (ArgumentException ex)
        {
            Log?.Invoke($"dropped group message from @{from}: invalid payload ({ex.Message})");
        }
        catch (InvalidOperationException ex)
        {
            Log?.Invoke($"dropped group message from @{from}: {ex.Message}");
        }
    }

    private static void ValidateGroupSnapshotShape(GroupSnapshotPayload snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.GroupId)
            || string.IsNullOrWhiteSpace(snapshot.Name)
            || string.IsNullOrWhiteSpace(snapshot.OwnerHandle)
            || snapshot.MemberHandles is null
            || snapshot.Version < 1)
            throw new ArgumentException("Required group snapshot fields are missing.");
        if (snapshot.MemberHandles.Count is < 2 or > FanoutProtocol.MaxRecipients)
            throw new ArgumentException(
                $"A group must contain between 2 and {FanoutProtocol.MaxRecipients} members.");
        if (snapshot.MemberHandles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Group member handles cannot be empty.");
    }

    private static void ValidateGroupMessageShape(GroupMessagePayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.GroupId)
            || string.IsNullOrWhiteSpace(payload.MessageId)
            || string.IsNullOrWhiteSpace(payload.SenderHandle)
            || string.IsNullOrWhiteSpace(payload.Text)
            || payload.MembershipVersion < 1
            || payload.SentAt == default)
            throw new ArgumentException("Required group message fields are missing.");
    }

    private static void ValidateLocalGroup(Conversation group)
    {
        if (!group.IsGroup
            || string.IsNullOrWhiteSpace(group.GroupId)
            || string.IsNullOrWhiteSpace(group.GroupName)
            || string.IsNullOrWhiteSpace(group.GroupOwnerHandle)
            || group.GroupMembers.Count is < 2 or > FanoutProtocol.MaxRecipients
            || group.GroupVersion < 1)
            throw new InvalidOperationException("The local group metadata is incomplete.");
    }

    /// <summary>
    /// Resolves (and caches) a handle's device public keys from the relay directory. External
    /// contacts retain TOFU pins; this account's own sibling devices use the authenticated
    /// authoritative roster so a legitimate link can converge without weakening contact trust.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveDeviceKeysAsync(string handle, bool refresh = false)
    {
        var h = AppState.Norm(handle);
        if (!refresh && keyCache.TryGetValue(h, out var cached)) return cached;
        try
        {
            var http = httpFactory.CreateClient("relay");
            var info = await http.GetFromJsonAsync<HandleInfo>(
                $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}");
            var keys = info?.DevicePublicKeys?.ToList() ?? new List<string>();
            if (keys.Count > 0)
            {
                var isOwnHandle = string.Equals(
                    h,
                    AppState.Norm(state.Profile.Handle),
                    StringComparison.Ordinal);
                var pinned = isOwnHandle
                    ? Array.Empty<string>()
                    : state.PinAndGetKeys(h, keys);
                var trusted = DeviceKeyRefreshPolicy.SelectTrustedDirectoryKeys(
                    isOwnHandle,
                    keys,
                    pinned);
                if (!isOwnHandle
                    && !trusted.ToHashSet(StringComparer.Ordinal).SetEquals(keys))
                    state.FlagContactKeyChanged(h);
                keyCache[h] = trusted;
                keyCacheUpdated[h] = DateTimeOffset.UtcNow;
                return trusted;
            }
            return keys;
        }
        catch { return Array.Empty<string>(); }
    }

    private async Task<DeviceKeyDirectorySnapshot> RefreshAuthoritativeDeviceKeysAsync(
        string handle,
        CancellationToken ct)
    {
        var h = AppState.Norm(handle);
        var info = await ((IReplicationMetadataSource)this)
            .FetchHandleAsync(h, ct)
            .ConfigureAwait(false);
        if (info is null) return DeviceKeyDirectorySnapshot.Unavailable;

        var keys = (info.DevicePublicKeys ?? Array.Empty<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (string.Equals(h, AppState.Norm(state.Profile.Handle), StringComparison.Ordinal))
        {
            keyCache[h] = keys;
            keyCacheUpdated[h] = DateTimeOffset.UtcNow;
        }
        return DeviceKeyDirectorySnapshot.FromKeys(keys);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveDeviceKeysBatchAsync(
        IEnumerable<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var handles = recipients
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(AppState.Norm)
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (handles.Count is < 1 or > FanoutProtocol.MaxRecipients)
            throw new ArgumentException(
                $"Fan-out requires between 1 and {FanoutProtocol.MaxRecipients} distinct recipients.",
                nameof(recipients));

        var resolved = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var ownHandle = AppState.Norm(state.Profile.Handle);
        var toResolve = new List<string>();
        foreach (var handle in handles)
        {
            var isOwnHandle = string.Equals(handle, ownHandle, StringComparison.Ordinal);
            var pinned = state.FindContact(handle)?.SigningKeys;
            if (keyCache.TryGetValue(handle, out var cached)
                && keyCacheUpdated.TryGetValue(handle, out var updated)
                && now - updated < GroupKeyCacheLifetime
                && (isOwnHandle
                    || pinned is { Count: > 0 }
                    && pinned.ToHashSet(StringComparer.Ordinal).SetEquals(cached)))
            {
                resolved[handle] = isOwnHandle ? cached : pinned!.ToList();
            }
            else
            {
                toResolve.Add(handle);
            }
        }

        if (toResolve.Count > 0)
        {
            var http = httpFactory.CreateClient("relay");
            using var response = await http.PostAsJsonAsync(
                $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/resolve",
                new HandleKeysBatchRequest(toResolve));
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Device-key resolution failed: relay {(int)response.StatusCode}: {detail}");
            }

            var batch = await response.Content.ReadFromJsonAsync<HandleKeysBatchResponse>(Json)
                ?? throw new InvalidOperationException("Device-key resolution returned an empty response.");
            var returned = batch.Handles
                .GroupBy(entry => AppState.Norm(entry.Handle), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .SelectMany(entry => entry.DevicePublicKeys ?? Array.Empty<string>())
                        .Where(key => !string.IsNullOrWhiteSpace(key))
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var missing = toResolve
                .Where(handle => !returned.TryGetValue(handle, out var keys) || keys.Count == 0)
                .Select(handle => $"@{handle}")
                .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Cannot send encrypted group traffic: no usable device keys for {string.Join(", ", missing)}.");

            foreach (var handle in toResolve)
            {
                var observed = returned[handle];
                var isOwnHandle = string.Equals(handle, ownHandle, StringComparison.Ordinal);
                var pinned = isOwnHandle
                    ? Array.Empty<string>()
                    : state.PinAndGetKeys(handle, observed);
                var trusted = DeviceKeyRefreshPolicy.SelectTrustedDirectoryKeys(
                    isOwnHandle,
                    observed,
                    pinned);
                if (!isOwnHandle
                    && !trusted.ToHashSet(StringComparer.Ordinal).SetEquals(observed))
                {
                    state.FlagContactKeyChanged(handle);
                    throw new InvalidOperationException(
                        $"Cannot send group traffic to @{handle}: identity keys changed; re-verify first.");
                }
                keyCache[handle] = trusted;
                keyCacheUpdated[handle] = now;
                resolved[handle] = trusted.ToList();
            }
        }

        return handles.ToDictionary(handle => handle, handle => resolved[handle], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-verifies a contact whose keys changed: fetches the handle's current device keys from the
    /// relay directory (bypassing the cache) and re-pins them, clearing the key-changed flag. This
    /// is an explicit, user-initiated trust decision.
    /// </summary>
    public async Task<bool> ReverifyContactAsync(string handle)
    {
        var h = AppState.Norm(handle);
        try
        {
            var http = httpFactory.CreateClient("relay");
            var info = await http.GetFromJsonAsync<HandleInfo>(
                $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}");
            var keys = info?.DevicePublicKeys?.ToList() ?? new List<string>();
            if (keys.Count == 0) return false;
            keyCache[h] = keys;
            keyCacheUpdated[h] = DateTimeOffset.UtcNow;
            state.ReverifyContact(h, keys);
            StateChanged?.Invoke();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Owner approves a held draft (optionally edited): record it and send.</summary>
    public async Task ApproveDraftAsync(string approvalId, string? editedReply = null)
    {
        var approval = state.Profile.Approvals.FirstOrDefault(a => a.Id == approvalId);
        if (approval is null) return;
        var text = string.IsNullOrWhiteSpace(editedReply) ? approval.DraftReply : editedReply!;
        // An approved draft is the agent's reply to the contact who asked, so it reads "to them".
        var line = new ChatLine { Role = "assistant", Text = text, AddressedToAgent = false };
        state.AddChatLine(approval.From, line);
        state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));
        var atomic = !string.IsNullOrWhiteSpace(approval.AgentRequestId)
                     && !string.IsNullOrWhiteSpace(approval.AgentRequestId);
        await SendAsync(
            approval.From,
            atomic ? MeshKinds.AtomicAgentResponse : MeshKinds.AgentResponse,
            text,
            line.Id,
            toDevice: approval.FromDevice,
            agentRequestId: approval.AgentRequestId);
    }

    public void RejectDraft(string approvalId)
        => state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));

    public async Task<TopicDispatchResult> DispatchAsync(
        string targetDeviceId,
        TopicRunRequestPayload request,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Any(item => item.Data.LongLength > MessageLimits.MaxAttachmentBytes))
            return TopicDispatchResult.Reject(
                "attachment_too_large",
                request.RunId,
                $"Each attachment must be at most {MessageLimits.MaxAttachmentBytes} bytes.");
        if (attachments.Sum(item => item.Data.LongLength) > TopicAttachmentAssembler.MaxRunBytes)
            return TopicDispatchResult.Reject(
                "attachments_too_large",
                request.RunId,
                $"Attachments must total at most {TopicAttachmentAssembler.MaxRunBytes} bytes.");
        if (!string.Equals(targetDeviceId, request.TargetDeviceId, StringComparison.Ordinal)
            || string.Equals(targetDeviceId, MyDeviceId, StringComparison.Ordinal)
            || !string.Equals(
                AppState.Norm(request.TriggerHandle),
                AppState.Norm(state.Profile.Handle),
                StringComparison.Ordinal)
            || !TopicRunProtocol.TryParseRequest(TopicRunProtocol.RequestBody(request), out _))
            return TopicDispatchResult.Reject("invalid_request", request.RunId);

        var thread = state.Profile.OwnThreads.FirstOrDefault(item =>
            string.Equals(item.Id, request.ThreadId, StringComparison.Ordinal));
        if (thread is null
            || !string.Equals(thread.ExecutionDeviceId, targetDeviceId, StringComparison.Ordinal))
            return TopicDispatchResult.Reject("invalid_thread_target", request.RunId);
        var target = await ResolveAccountDeviceAsync(targetDeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (target is null || !target.CanHostRemoteTurn)
            return TopicDispatchResult.Reject(
                "device_not_eligible",
                request.RunId,
                "The selected device is not agent-ready.");

        var manifest = request.Attachments ?? Array.Empty<TopicRunAttachment>();
        var ids = request.AttachmentIds ?? manifest.Select(item => item.Id).ToArray();
        if (manifest.Count != attachments.Count
            || ids.Count != attachments.Count
            || manifest.Sum(item => item.Length) > TopicAttachmentAssembler.MaxRunBytes
            || manifest.Where((item, index) =>
                    !string.Equals(item.Name, attachments[index].Name, StringComparison.Ordinal)
                    || !string.Equals(item.MimeType, attachments[index].MimeType, StringComparison.Ordinal)
                    || item.Length != attachments[index].Data.LongLength)
                .Any())
            return TopicDispatchResult.Reject("attachment_manifest_mismatch", request.RunId);

        return await Task.Run(
            () => QueueTopicRequestAsync(
                targetDeviceId, request, attachments, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<TopicDispatchResult> DispatchPersistedAsync(
        MeshDb.TopicOutboxItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var persisted = state.GetTopicOutbox(item.RunId);
        if (persisted is null
            || !string.Equals(
                persisted.ThreadId, item.ThreadId, StringComparison.Ordinal)
            || !string.Equals(
                persisted.TriggerLineId, item.TriggerLineId, StringComparison.Ordinal)
            || !string.Equals(
                persisted.TargetDeviceId, item.TargetDeviceId, StringComparison.Ordinal))
            return Task.FromResult(TopicDispatchResult.Reject(
                "local_persistence_failed",
                item.RunId,
                "The durable topic request is missing or has a different identity.",
                durable: persisted is not null));
        return Task.Run(
            () => DispatchPersistedTopicRequestAsync(persisted, cancellationToken),
            cancellationToken);
    }

    public async Task<bool> CancelAsync(
        string targetDeviceId,
        TopicRunCancelPayload cancel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        if (!TopicRunProtocol.TryParseCancel(TopicRunProtocol.CancelBody(cancel), out _))
            return false;
        var thread = state.Profile.OwnThreads.FirstOrDefault(item =>
            string.Equals(item.Id, cancel.ThreadId, StringComparison.Ordinal));
        if (thread is null
            || !string.Equals(thread.ExecutionDeviceId, targetDeviceId, StringComparison.Ordinal)
            || !string.Equals(thread.ExecutionRunId, cancel.RunId, StringComparison.Ordinal)
               && !state.IsKnownQueuedTopicRun(cancel.ThreadId, cancel.RunId))
            return false;
        return await Task.Run(
            () => QueueTopicCancellationAsync(targetDeviceId, cancel, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken)
        => (await ListMyDevicesCoreAsync(cancellationToken))
            .Where(device => device.Online && device.CanHostRemoteTurn)
            .ToArray();

    private Task<bool> SendTargetedTopicEnvelopeAsync(
        string targetDeviceId,
        string kind,
        string plaintext,
        CancellationToken ct,
        string? pushHint = null)
        => QueueDeviceEnvelopeAsync(targetDeviceId, kind, plaintext, ct, pushHint);

    public async Task<AgentRoutingInfo?> GetAgentRoutingAsync(CancellationToken ct = default)
    {
        if (!supportsAgentHost) return null;
        var p = state.Profile;
        var handle = AppState.Norm(p.Handle);
        if (handle.Length == 0) return null;
        try
        {
            var signature = IdentityService.Sign(
                p.PrivateKey,
                AgentRoutingProtocol.QueryMessage(handle));
            var http = httpFactory.CreateClient("relay");
            var response = await http.PostAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(handle)}/agent-routing/query",
                new AgentRoutingQueryRequest(p.PublicKey, signature),
                ct);
            if (!response.IsSuccessStatusCode) return null;
            var info = await response.Content.ReadFromJsonAsync<AgentRoutingInfo>(cancellationToken: ct);
            if (info is not null) CacheAgentRouting(info);
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"agent routing lookup failed: {ex.Message}");
            return null;
        }
    }

    public async Task<(bool Success, AgentRoutingInfo? Routing, string? Error)> SetAgentRoutingAsync(
        string primaryDeviceId,
        string? failoverDeviceId,
        CancellationToken ct = default)
    {
        if (!supportsAgentHost)
            return (false, null, "The connected relay does not support atomic agent routing.");
        var p = state.Profile;
        var handle = AppState.Norm(p.Handle);
        var version = p.AgentRoutingVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            var current = await GetAgentRoutingAsync(ct);
            version = current?.Version ?? "";
        }
        var message = AgentRoutingProtocol.UpdateMessage(
            handle,
            primaryDeviceId,
            failoverDeviceId,
            version);
        var signature = IdentityService.Sign(p.PrivateKey, message);
        try
        {
            var http = httpFactory.CreateClient("relay");
            var response = await http.PutAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(handle)}/agent-routing",
                new AgentRoutingUpdateRequest(
                    p.PublicKey,
                    primaryDeviceId,
                    failoverDeviceId,
                    version,
                    signature),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    await GetAgentRoutingAsync(ct);
                return (false, null, $"Relay rejected the device selection ({(int)response.StatusCode}).");
            }
            var info = await response.Content.ReadFromJsonAsync<AgentRoutingInfo>(cancellationToken: ct);
            if (info is null) return (false, null, "Relay returned an invalid routing response.");
            CacheAgentRouting(info);
            return (true, info, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, ex.Message);
        }
    }

    private void CacheAgentRouting(AgentRoutingInfo info)
        => state.Mutate(profile =>
        {
            profile.AgentPrimaryDeviceId = info.PrimaryDeviceId;
            profile.AgentFailoverDeviceId = info.FailoverDeviceId;
            profile.AgentRoutingVersion = info.Version;
            profile.AgentPrimaryWasSelectedAutomatically = info.PrimaryWasSelectedAutomatically;
        });

    public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListMyDevicesAsync(CancellationToken ct = default)
        => ListMyDevicesCoreAsync(ct);

    public async Task<RevokeDeviceResponse?> RevokeDeviceAsync(
        string targetDeviceId,
        CancellationToken ct = default)
    {
        var profile = state.Profile;
        var handle = AppState.Norm(profile.Handle);
        if (!supportsDeviceRevocation
            || string.IsNullOrWhiteSpace(handle)
            || string.IsNullOrWhiteSpace(targetDeviceId)
            || string.Equals(targetDeviceId, MyDeviceId, StringComparison.Ordinal))
            return null;

        var signature = IdentityService.Sign(
            profile.PrivateKey,
            DeviceRevocationProtocol.Message(handle, targetDeviceId));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(handle)}/devices/{Uri.EscapeDataString(targetDeviceId)}")
        {
            Content = JsonContent.Create(new RevokeDeviceRequest(
                profile.PublicKey,
                targetDeviceId,
                signature), options: Json)
        };
        var http = httpFactory.CreateClient("relay");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RevokeDeviceResponse>(Json, ct)
                     ?? throw new InvalidDataException("The relay returned an empty device revocation response.");
        keyCache.TryRemove(handle, out _);
        keyCacheUpdated.TryRemove(handle, out _);
        return result;
    }
    private async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListMyDevicesCoreAsync(CancellationToken ct)
    {
        var h = AppState.Norm(state.Profile.Handle);
        if (string.IsNullOrWhiteSpace(h)) return Array.Empty<Mesh.Shared.DeviceInfo>();
        var http = httpFactory.CreateClient("relay");
        var fetched = await http.GetFromJsonAsync<Mesh.Shared.DeviceInfo[]>(
                   $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/devices",
                   Json,
                   ct)
               ?? Array.Empty<Mesh.Shared.DeviceInfo>();
        lock (accountDevicePresenceGate)
        {
            var observed = latestAccountOnlineDevices;
            var devices = observed is null
                ? fetched
                : fetched.Select(device =>
                        device.Online == observed.Contains(device.DeviceId)
                            ? device
                            : device with { Online = observed.Contains(device.DeviceId) })
                    .ToArray();
            latestDeviceDirectory = devices
                .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return devices;
        }
    }

    public bool IsAccountDeviceOnline(string? deviceId)
    {
        if (string.Equals(deviceId, MyDeviceId, StringComparison.Ordinal)) return true;
        if (deviceId is null) return false;
        var observed = latestAccountOnlineDevices;
        return observed?.Contains(deviceId)
               ?? latestDeviceDirectory.TryGetValue(deviceId, out var device) && device.Online;
    }

    internal bool HasAccountDevicePresenceSnapshot => latestAccountOnlineDevices is not null;

    internal void ApplyAccountDevicePresenceSnapshot(IReadOnlyCollection<string> onlineDevices)
    {
        ArgumentNullException.ThrowIfNull(onlineDevices);
        var observed = onlineDevices
            .Where(static device => !string.IsNullOrWhiteSpace(device))
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;
        lock (accountDevicePresenceGate)
        {
            changed = latestAccountOnlineDevices?.SetEquals(observed) != true;
            latestAccountOnlineDevices = observed;
            var directory = latestDeviceDirectory;
            if (directory.Count > 0)
            {
                latestDeviceDirectory = directory.ToDictionary(
                    static item => item.Key,
                    item =>
                    {
                        var online = observed.Contains(item.Key);
                        return item.Value.Online == online
                            ? item.Value
                            : item.Value with { Online = online };
                    },
                    StringComparer.Ordinal);
            }
        }
        if (changed) AccountDevicePresenceChanged?.Invoke();
    }

    private string CurrentDevicePlatform =>
        currentPlatformProvider?.Invoke() ?? PlatformCaps.DevicePlatform;

    private async Task<Mesh.Shared.DeviceInfo?> ResolveAccountDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
        => (await ListMyDevicesCoreAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(device =>
                string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));

    private async Task EnsureCurrentDeviceCanHostRemoteTopicAsync(
        CancellationToken cancellationToken)
    {
        var locallyEligible = Connected
                              && supportsAgentHost
                              && agent.IsModelReady
                              && DevicePlatforms.IsDesktop(CurrentDevicePlatform);
        if (!locallyEligible)
        {
            TraceTransport("topic-remote-host-rejected", "local_capability");
            throw new InboundPermanentRejectException("topic_remote_host_not_eligible");
        }

        Mesh.Shared.DeviceInfo? registered;
        try
        {
            registered = await ResolveAccountDeviceAsync(MyDeviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InboundRetryException(
                "topic_remote_host_authorization_unavailable:" + ex.GetType().Name);
        }
        catch (JsonException ex)
        {
            throw new InboundRetryException(
                "topic_remote_host_authorization_unavailable:" + ex.GetType().Name);
        }

        if (registered is not { Online: true, CanHostRemoteTurn: true })
        {
            TraceTransport("topic-remote-host-rejected", "directory_capability");
            throw new InboundPermanentRejectException("topic_remote_host_not_eligible");
        }
    }

    /// <summary>
    /// Sends a user-submitted report of inappropriate AI content to the reserved report handle as an
    /// end-to-end encrypted message (Microsoft Store Policy 11.16). The caller has shown the user the
    /// exact transcript and obtained explicit consent before calling this.
    /// </summary>
    public async Task<bool> SendReportAsync(string target, string category, string? note, string? serviceId, IReadOnlyList<ReportLine> transcript)
    {
        var payload = new ReportPayload(
            Target: target,
            Category: category,
            Note: string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
            Model: state.CurrentModelKey(),
            ServiceId: serviceId,
            AppVersion: AppVersionString(),
            At: DateTimeOffset.UtcNow,
            Transcript: transcript);
        return await SendAsync(ReservedHandles.Report, MeshKinds.Report, ReportProtocol.Body(payload));
    }

    private static string AppVersionString()
    {
        try { return Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString; }
        catch { return "unknown"; }
    }

    // Renders an inbound report into readable text for the operator's Messages view.
    private static string FormatReport(ReportPayload p)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[AI content report]\n\n");
        sb.Append("Target: ").Append(p.Target).Append('\n');
        sb.Append("Category: ").Append(p.Category).Append('\n');
        if (!string.IsNullOrWhiteSpace(p.Note)) sb.Append("Note: ").Append(p.Note).Append('\n');
        if (!string.IsNullOrWhiteSpace(p.Model)) sb.Append("Model: ").Append(p.Model).Append('\n');
        sb.Append("App: ").Append(p.AppVersion).Append("  ").Append(p.At.ToString("u")).Append("\n\n");
        sb.Append("Transcript:\n");
        foreach (var l in p.Transcript)
            sb.Append("- ").Append(l.Author).Append(": ").Append(l.Text).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Sends a ServiceRequest for the given service thread, carrying a windowed transcript so the
    /// provider's sandboxed agent has multi-turn context for follow-ups. The caller has already
    /// appended the user's prompt line to <paramref name="conv"/>. No contact relationship is required;
    /// the request routes to the real provider handle behind the synthetic thread key.
    /// </summary>
    public async Task<bool> SendServiceRequestAsync(Conversation conv)
    {
        if (conv.ServiceId is null || string.IsNullOrWhiteSpace(conv.ProviderHandle)) return false;
        // Show a processing indicator on this thread until the reply arrives (the response is
        // asynchronous for a remote service, or produced in-process for a service you own).
        state.SetAwaiting(conv.Handle);
        // From the provider agent's point of view, my outgoing lines (Role "assistant") are the user,
        // and the service's prior answers (Role "user") are the assistant.
        var window = conv.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .TakeLast(20)
            .Select(l => new ServiceTurn(l.Role == "assistant" ? "user" : "assistant", l.Text))
            .ToList();

        // A desktop provider invoking their own service answers locally because the relay does not
        // echo a message back to the sending device. Mobile clients deliberately fall through to the
        // relay so an online desktop sibling hosts the service instead.
        if (PlatformCaps.CanHostServices
            && AppState.Norm(conv.ProviderHandle!) == AppState.Norm(state.Profile.Handle))
        {
            await AnswerOwnServiceLocallyAsync(conv, window);
            return true;
        }

        var body = ServiceProtocol.RequestBody(conv.ServiceId, window);
        var ok = await SendAsync(conv.ProviderHandle!, MeshKinds.ServiceRequest, body);
        // If the request could not be sent, do not leave a stuck indicator.
        if (!ok) state.ClearAwaiting(conv.Handle);
        return ok;
    }

    /// <summary>
    /// Answers a service the current user owns, locally (no relay round-trip), so a provider can use
    /// and test their own service. Runs the same hard-sandboxed service agent as the remote path
    /// (public-listed capabilities only), so what the owner sees matches what other handles get. No
    /// budget or rate-limit gating is applied to the owner using their own service.
    /// </summary>
    private async Task AnswerOwnServiceLocallyAsync(Conversation conv, IReadOnlyList<ServiceTurn> window)
    {
        var svc = state.Profile.PublishedServices.FirstOrDefault(s => s.Id == conv.ServiceId);
        if (svc is null) { state.ClearAwaiting(conv.Handle); return; }
        if (!agent.IsModelReady)
        {
            state.AddChatLine(conv.Handle, new ChatLine
            {
                Role = "user",
                Text = "No model is configured, so this service cannot answer yet. Set one up in Settings.",
                Via = "agent",
                AddressedToAgent = true
            });
            state.ClearAwaiting(conv.Handle);
            return;
        }

        var me = AppState.Norm(state.Profile.Handle);
        var svcHistory = window
            .Select(t => new ChatLine { Role = t.Role == "user" ? "user" : "assistant", Text = t.Text, Via = "agent" })
            .ToList();

        var reply = await agent.RespondAsServiceAsync(conv.ServiceId!, me, svcHistory, CancellationToken.None);
        state.ClearAwaiting(conv.Handle);
        if (ModelReply.IsFailure(reply.Text)) return;

        state.AddChatLine(conv.Handle, new ChatLine { Role = "user", Text = reply.Text, Via = "agent", AddressedToAgent = true });
        state.MarkRead(conv.Handle);
    }

    public async Task<Conversation> CreateGroupAsync(string name, IEnumerable<string> memberHandles)
    {
        if (hub is null || hub.State != HubConnectionState.Connected || !authenticated)
            throw new InvalidOperationException("Cannot create a group while disconnected or unauthenticated.");
        if (!supportsFanout)
            throw new InvalidOperationException("The connected relay does not support stateless fan-out.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Group name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(memberHandles);

        var me = AppState.Norm(state.Profile.Handle);
        if (string.IsNullOrWhiteSpace(me))
            throw new InvalidOperationException("An authenticated handle is required to create a group.");

        var members = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in memberHandles)
        {
            if (string.IsNullOrWhiteSpace(requested)) continue;
            var normalized = AppState.Norm(requested);
            if (normalized.Length > 0 && seen.Add(normalized)) members.Add(normalized);
        }
        if (seen.Add(me)) members.Add(me);
        if (members.Count < 2)
            throw new ArgumentException("A group requires at least two distinct members.", nameof(memberHandles));
        if (members.Count > FanoutProtocol.MaxRecipients)
            throw new ArgumentException(
                $"A group cannot contain more than {FanoutProtocol.MaxRecipients} members.",
                nameof(memberHandles));

        var snapshot = new GroupSnapshotPayload(
            Guid.NewGuid().ToString("n"), name.Trim(), me, members, 1);
        var group = state.ApplyGroupSnapshot(snapshot);
        var body = JsonSerializer.Serialize(snapshot, Json);
        var request = await BuildEncryptedGroupFanoutAsync(
            members, MeshKinds.GroupControl, body);

        try
        {
            var result = await hub.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendFanout, request);
            if (!result.Accepted)
                throw new InvalidOperationException($"relay rejected fan-out ({DescribeResult(result)})");
            return group;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"group creation send failed for '{group.GroupName}': {ex.Message}");
            throw new InvalidOperationException(
                $"The group was saved locally, but its encrypted invitations were not sent: {ex.Message}", ex);
        }
    }

    public async Task<bool> SendGroupMessageAsync(Conversation group, string text, string? lineId = null)
    {
        var messageId = string.IsNullOrWhiteSpace(lineId) ? Guid.NewGuid().ToString("n") : lineId;
        try
        {
            ArgumentNullException.ThrowIfNull(group);
            ValidateLocalGroup(group);
            if (hub is null || hub.State != HubConnectionState.Connected || !authenticated)
                throw new InvalidOperationException("Not connected or authenticated.");
            if (!supportsFanout)
                throw new InvalidOperationException("The connected relay does not support stateless fan-out.");
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Group message text is required.", nameof(text));

            var me = AppState.Norm(state.Profile.Handle);
            if (!group.GroupMembers.Contains(me, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The current user is not a group member.");

            var payload = new GroupMessagePayload(
                group.GroupId!, messageId, me, text, group.GroupVersion, DateTimeOffset.UtcNow);
            var body = JsonSerializer.Serialize(payload, Json);
            var recipients = group.GroupMembers
                .Select(AppState.Norm)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var request = await BuildEncryptedGroupFanoutAsync(
                recipients, MeshKinds.GroupMessage, body);

            var result = await hub.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendFanout, request);
            if (!result.Accepted)
                throw new InvalidOperationException($"relay rejected fan-out ({DescribeResult(result)})");
            state.SetLineStatus(messageId, "sent");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"group message send failed: {ex.Message}");
            state.SetLineStatus(messageId, "failed");
            return false;
        }
    }

    private async Task<MeshFanoutRequest> BuildEncryptedGroupFanoutAsync(
        IReadOnlyList<string> recipients, string kind, string plaintext)
    {
        var keysByRecipient = await ResolveDeviceKeysBatchAsync(recipients);
        var normalizedRecipients = keysByRecipient.Keys.ToList();
        var allKeys = keysByRecipient.Values
            .SelectMany(keys => keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var content = JsonSerializer.Serialize(new MeshFanoutContent(kind, plaintext), Json);
        var ciphertext = MessageCrypto.Encrypt(content, allKeys)
            ?? throw new InvalidOperationException(
                "Cannot send encrypted group traffic: no usable recipient device keys.");
        if (System.Text.Encoding.UTF8.GetByteCount(ciphertext) > MessageLimits.MaxEnvelopeBodyBytes)
            throw new InvalidOperationException(
                $"Group message is too large; the limit is {MessageLimits.MaxEnvelopeBodyBytes} bytes. Send large content as a blob attachment.");
        var p = state.Profile;
        var signature = IdentityService.Sign(p.PrivateKey, ciphertext);
        return new MeshFanoutRequest(
            Guid.NewGuid().ToString("n"), normalizedRecipients, ciphertext, signature, DateTimeOffset.UtcNow);
    }


    public async Task<bool> SendAsync(
        string toHandle,
        string kind,
        string body,
        string? lineId = null,
        string? toDevice = null,
        string? agentRequestId = null,
        string? remoteAgentToken = null)
    {
        if (string.Equals(kind, MeshKinds.DirectMessage, StringComparison.Ordinal))
        {
            if (lineId is not null) state.SetLineStatus(lineId, "sent");
            return true;
        }
        if (hub is null || hub.State != HubConnectionState.Connected || !authenticated)
        {
            Log?.Invoke("send failed: not connected");
            if (lineId is not null) state.SetLineStatus(lineId, "failed");
            return false;
        }
        var p = state.Profile;
        var to = AppState.Norm(toHandle);
        if (string.Equals(kind, MeshKinds.AtomicAgentRequest, StringComparison.Ordinal))
            agentRequestId ??= lineId;

        // End-to-end encrypt to the recipient's device keys when we can resolve them. The relay
        // only ever sees ciphertext. If keys are unavailable (recipient not in the directory yet)
        // we fall back to sending plaintext so messaging still works.
        var wire = body;
        var keys = await ResolveDeviceKeysAsync(to);
        if (keys.Count > 0)
        {
            var enc = MessageCrypto.Encrypt(body, keys);
            if (enc is not null) wire = enc;
        }
        if ((string.Equals(kind, MeshKinds.AtomicAgentRequest, StringComparison.Ordinal) || string.Equals(kind, MeshKinds.AtomicAgentResponse, StringComparison.Ordinal))
            && !MessageCrypto.IsEncrypted(wire))
        {
            Log?.Invoke("atomic agent send failed: recipient encryption keys are unavailable");
            if (lineId is not null) state.SetLineStatus(lineId, "failed");
            return false;
        }

        // Client-side backstop for the relay's hard envelope cap: never inline large content. Large
        // payloads must be sent as blob attachment pointers, so the body itself stays well under 2 MB.
        var wireBytes = System.Text.Encoding.UTF8.GetByteCount(wire);
        if (wireBytes > MessageLimits.MaxEnvelopeBodyBytes)
        {
            Log?.Invoke($"send rejected: message too large ({wireBytes} > {MessageLimits.MaxEnvelopeBodyBytes} bytes); send large content as a blob attachment");
            if (lineId is not null) state.SetLineStatus(lineId, "failed");
            return false;
        }
        var sig = IdentityService.Sign(p.PrivateKey, wire);
        var env = MeshEnvelope.Create(
            p.Handle,
            to,
            kind,
            wire,
            sig,
            toDevice: toDevice,
            agentRequestId: agentRequestId,
            id: lineId);
        try
        {
            var resultCode = "accepted";
            if (supportsSendResults)
            {
                var result = await hub.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, env);
                resultCode = result.Code;
                if (!result.Accepted)
                {
                    Log?.Invoke($"send rejected: {DescribeResult(result)}");
                    if (lineId is not null) state.SetLineStatus(lineId, "failed");
                    return false;
                }
            }
            else
            {
                // Older relays route SendEnvelope but return no result payload.
                await hub.InvokeAsync(MeshHubProtocol.SendEnvelope, env);
            }
            if (lineId is not null)
            {
                var status = string.Equals(kind, MeshKinds.AtomicAgentRequest, StringComparison.Ordinal)
                             && supportsSendResults
                             && string.Equals(resultCode, "queued", StringComparison.Ordinal)
                    ? "agent_queued"
                    : "sent";
                state.SetLineStatus(lineId, status);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"send failed: {ex.Message}");
            if (lineId is not null) state.SetLineStatus(lineId, "failed");
            return false;
        }
    }

    private bool ShouldNotify(Domain.Contact? contact)
        => !state.Profile.DoNotDisturb && contact?.Muted != true;

    /// <summary>Sends a lightweight delivery receipt (message id only, signed, no content) to a sender.</summary>
    private async Task SendReceiptAsync(string toHandle, string messageId)
    {
        try
        {
            if (hub is null || hub.State != HubConnectionState.Connected || !authenticated) return;
            var p = state.Profile;
            var body = ReceiptProtocol.Body(messageId);
            var sig = IdentityService.Sign(p.PrivateKey, body);
            var env = MeshEnvelope.Create(
                p.Handle,
                AppState.Norm(toHandle),
                MeshKinds.Receipt,
                body,
                sig,
                id: StableEnvelopeId("receipt", messageId));
            if (supportsSendResults)
                _ = await hub.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, env);
            else
                await hub.InvokeAsync(MeshHubProtocol.SendEnvelope, env);
        }
        catch { /* receipts are best-effort */ }
    }

    private static string DescribeResult(MeshSendResult result)
        => $"code={result.Code}, retryAfterMs={result.RetryAfterMs}";

    private static string Preview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no content)";
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length > 120 ? clean[..120] + "…" : clean;
    }

    private async Task PublishLegacyNotificationAsync(
        string stableId,
        NotificationKind kind,
        string entityId,
        string route,
        string title,
        string body,
        CancellationToken ct = default)
    {
        await state.FlushPersistenceAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var conversationId = route.StartsWith("mesh://messages/", StringComparison.OrdinalIgnoreCase)
            ? entityId
            : null;
        await NotificationCoordinatorBridge.PublishAsync(new CommittedActivity(
            stableId,
            $"legacy:{stableId}",
            kind,
            entityId,
            conversationId,
            route,
            title,
            body,
            now,
            now,
            IsHistorical: false,
            NotifyRequested: true,
            OriginAccount: null), ct).ConfigureAwait(false);
    }
    // ---- Contentless push wakes --------------------------------------------------------------------
    // Register this device's APNs/FCM token so authenticated Protocol 9 peers can request an opaque sync
    // wake while the device is offline. The relay receives no sender, route, title, body, or encrypted event
    // in the push request. Platforms without mobile push return no token and keep this path disabled.
    private sealed record RegisteredPushIdentity(
        string RelayUrl,
        string Handle,
        string DeviceId,
        string Platform,
        string Token,
        bool AlertsEnabled);

    private sealed record PushUnregistrationIdentity(
        string RelayUrl,
        string Handle,
        string DeviceId,
        string PublicKey,
        string PrivateKey);

    private RegisteredPushIdentity? registeredPushIdentity;
    private int pushRegistrationInProgress;

    private void TryRegisterPushToken()
    {
        if (!push.IsSupported) return;
        if (Interlocked.CompareExchange(ref pushRegistrationInProgress, 1, 0) != 0) return;
        TrackBackground(RegisterPushTokenGuardedAsync(), "push token registration");
    }

    private async Task RegisterPushTokenGuardedAsync()
    {
        try
        {
            await RegisterPushTokenAsync();
        }
        finally
        {
            Interlocked.Exchange(ref pushRegistrationInProgress, 0);
        }
    }

    public async Task RegisterPushTokenAsync(CancellationToken ct = default)
    {
        PushRegistrationInfo? registration;
        try { registration = await push.RegisterAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log?.Invoke($"push token request failed: {ex.Message}"); return; }
        if (registration is null) return;

        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle)
            || string.IsNullOrWhiteSpace(p.PublicKey)
            || string.IsNullOrWhiteSpace(p.RelayUrl)) return;
        var platform = PlatformCaps.DevicePlatform;
        var alertsEnabled = registration.AlertsEnabled && !p.DoNotDisturb;
        var deviceId = MyDeviceId;
        var identity = new RegisteredPushIdentity(
            p.RelayUrl.TrimEnd('/'),
            AppState.Norm(p.Handle),
            deviceId,
            platform,
            registration.Token,
            alertsEnabled);
        if (Equals(Volatile.Read(ref registeredPushIdentity), identity)) return;

        try
        {
            var signature = IdentityService.Sign(
                p.PrivateKey,
                PushTokenProtocol.Message(p.Handle, deviceId, platform, registration.Token));
            var http = httpFactory.CreateClient("relay");
            var response = await http.PostAsJsonAsync(
                $"{identity.RelayUrl}/handles/{Uri.EscapeDataString(identity.Handle)}/push",
                new SetDevicePushTokenRequest(
                    p.PublicKey,
                    platform,
                    registration.Token,
                    signature,
                    alertsEnabled),
                ct);
            if (response.IsSuccessStatusCode)
            {
                Volatile.Write(ref registeredPushIdentity, identity);
                Log?.Invoke("push token registered with relay");
            }
            else
            {
                Log?.Invoke($"push token registration rejected: {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log?.Invoke($"push token registration failed: {ex.Message}"); }
    }

    // ---- Blob attachments -------------------------------------------------------------------------
    // Attachments never travel inline in an envelope (the relay caps bodies at ~2 MB). Each attachment is
    // encrypted locally, uploaded as ciphertext to blob storage via a short-lived relay-issued SAS URL, and
    // the envelope then carries only an AttachmentPointer inside its end-to-end-encrypted body.

    /// <summary>Encrypts and uploads one attachment, returning a pointer to embed in an E2EE envelope, or null on failure.</summary>
    public async Task<AttachmentPointer?> UploadAttachmentAsync(ChatAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.Data.LongLength > MessageLimits.MaxAttachmentBytes)
            throw new InvalidOperationException(
                $"Attachment '{attachment.Name}' is {attachment.Data.LongLength} bytes; the limit is {MessageLimits.MaxAttachmentBytes} bytes.");

        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.PublicKey)) return null;

        var (cipher, keyB64) = AttachmentCrypto.Seal(attachment.Data);
        var http = httpFactory.CreateClient("relay");
        var h = AppState.Norm(p.Handle);
        var deviceId = MyDeviceId;
        var sig = IdentityService.Sign(p.PrivateKey, AttachmentProtocol.UploadMessage(p.Handle, deviceId, cipher.LongLength));

        var resp = await http.PostAsJsonAsync(
            $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/attachments",
            new AttachmentUploadRequest(p.PublicKey, cipher.LongLength, sig), ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log?.Invoke($"attachment upload request rejected: {(int)resp.StatusCode}");
            return null;
        }
        var info = await resp.Content.ReadFromJsonAsync<AttachmentUploadResponse>(ct);
        if (info is null) return null;

        using var put = new HttpRequestMessage(HttpMethod.Put, info.UploadUrl)
        {
            Content = new ByteArrayContent(cipher),
        };
        put.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        var up = await http.SendAsync(put, ct);
        if (!up.IsSuccessStatusCode)
        {
            Log?.Invoke($"attachment blob upload failed: {(int)up.StatusCode}");
            return null;
        }

        return new AttachmentPointer(
            info.BlobId, attachment.Name, attachment.MimeType, attachment.Data.LongLength,
            keyB64, AttachmentCrypto.Sha256B64(attachment.Data));
    }

    /// <summary>Downloads and decrypts one attachment referenced by a pointer, or null on failure/integrity mismatch.</summary>
    public async Task<ChatAttachment?> DownloadAttachmentAsync(AttachmentPointer pointer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Size > MessageLimits.MaxAttachmentBytes) return null;

        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.PublicKey)) return null;

        var http = httpFactory.CreateClient("relay");
        var h = AppState.Norm(p.Handle);
        var deviceId = MyDeviceId;
        var sig = IdentityService.Sign(p.PrivateKey, AttachmentProtocol.DownloadMessage(p.Handle, deviceId, pointer.BlobId));

        var resp = await http.PostAsJsonAsync(
            $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/attachments/{Uri.EscapeDataString(pointer.BlobId)}",
            new AttachmentDownloadRequest(p.PublicKey, sig), ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log?.Invoke($"attachment download request rejected: {(int)resp.StatusCode}");
            return null;
        }
        var info = await resp.Content.ReadFromJsonAsync<AttachmentDownloadResponse>(ct);
        if (info is null) return null;

        var cipher = await http.GetByteArrayAsync(info.DownloadUrl, ct);
        byte[] plain;
        try { plain = AttachmentCrypto.Open(cipher, pointer.Key); }
        catch (Exception ex) { Log?.Invoke($"attachment decrypt failed: {ex.Message}"); return null; }
        if (!string.Equals(AttachmentCrypto.Sha256B64(plain), pointer.Sha256, StringComparison.Ordinal))
        {
            Log?.Invoke("attachment integrity check failed");
            return null;
        }
        return new ChatAttachment(pointer.Name, pointer.MimeType, plain);
    }

    /// <summary>
    /// Clears this device's push token on the relay so a signed-out device is no longer woken. Best-effort,
    /// signed with the device key. Call before <see cref="DisconnectAsync"/> on an intentional sign-out (not
    /// on a transient reconnect, which also goes through DisconnectAsync).
    /// </summary>
    public Task UnregisterPushAsync()
    {
        Volatile.Write(ref registeredPushIdentity, null);
        var identity = CapturePushUnregistrationIdentity();
        return identity is null ? Task.CompletedTask : UnregisterPushAsync(identity);
    }

    private PushUnregistrationIdentity? CapturePushUnregistrationIdentity()
    {
        if (!push.IsSupported) return null;
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle)
            || string.IsNullOrWhiteSpace(p.PublicKey)
            || string.IsNullOrWhiteSpace(p.PrivateKey)
            || string.IsNullOrWhiteSpace(p.RelayUrl))
            return null;
        return new PushUnregistrationIdentity(
            p.RelayUrl.TrimEnd('/'),
            AppState.Norm(p.Handle),
            DeviceProtocol.DeviceId(p.PublicKey),
            p.PublicKey,
            p.PrivateKey);
    }

    private async Task UnregisterPushAsync(PushUnregistrationIdentity identity)
    {
        try
        {
            var sig = IdentityService.Sign(
                identity.PrivateKey,
                PushTokenProtocol.ClearMessage(identity.Handle, identity.DeviceId));
            var http = httpFactory.CreateClient("relay");
            using var req = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{identity.RelayUrl}/handles/{Uri.EscapeDataString(identity.Handle)}/push")
            {
                Content = JsonContent.Create(new DeleteDevicePushTokenRequest(identity.PublicKey, sig))
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await http.SendAsync(req, cts.Token);
            Log?.Invoke($"push token cleared: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log?.Invoke($"push token clear failed: {ex.Message}"); }
    }

    public void BeginShutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0) return;
        wantConnected = false;
        onlineDeliveryRetry.Stop();
        StopReplication("shutdown");
        foreach (var run in activeTopicRuns.Values)
        {
            try { run.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task DisconnectAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task DisconnectCoreAsync(
        bool clearConnectionIntent = true,
        TimeSpan? timeout = null,
        string replicationStopReason = "disconnect")
    {
        // Clear intent first on explicit disconnect so the Closed handler cannot trigger recovery.
        if (clearConnectionIntent) wantConnected = false;
        authenticated = false;
        connectionAuthentication?.TrySetCanceled();
        connectionAuthentication = null;
        // Tear online replication down before the transport drops so peer sessions and the roster cache
        // never outlive the identity/connection they were established under. It re-arms on next auth.
        await StopReplicationAsync(replicationStopReason).ConfigureAwait(false);
        authenticatedReplicationConnectionIdentity = null;
        keyCache.Clear();
        keyCacheUpdated.Clear();
        var current = hub;
        hub = null;
        if (current is not null)
        {
            if (timeout is null)
            {
                try { await current.StopAsync().ConfigureAwait(false); } catch { }
                try { await current.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            else
            {
                using var cleanup = new CancellationTokenSource(timeout.Value);
                try { await current.StopAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    TraceTransport("background-disconnect-timeout", "connection stop timed out");
                }
                catch (Exception ex) { TraceTransport("background-disconnect-failed", ex.Message); }

                var dispose = current.DisposeAsync().AsTask();
                try { await dispose.WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    TraceTransport("background-dispose-timeout", "connection disposal timed out");
                    TrackBackground(dispose, "late background connection disposal");
                }
                catch (Exception ex) { TraceTransport("background-dispose-failed", ex.Message); }
            }
        }
        StateChanged?.Invoke();
    }
}
