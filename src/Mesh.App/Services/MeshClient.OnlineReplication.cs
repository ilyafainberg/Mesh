using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Protocol-9 online-replication runtime for <see cref="MeshClient"/>. The relay is a pure, opaque
/// authenticated forwarder: every durable payload it ever sees is an encrypted
/// <see cref="OnlineRelayFrame"/> and it never stores message bodies. This partial makes the client
/// the engine's <see cref="IReplicationTransport"/>, its relay-metadata source, and owns the
/// production wiring that arms the engine automatically after Protocol-9 authentication:
///
///  * <see cref="ArmReplicationAsync"/> is invoked from the authenticated hook (never an external
///    configuration call). It reads this handle's own relay directory entry for the authoritative
///    auth generation and custody head, builds the real <see cref="ReplicationIdentity"/> off the UI
///    thread, constructs the relay-backed roster, starts the engine through
///    <see cref="AppState.TryStartOnlineReplication"/> (which also attaches the concrete inbound
///    projection), and starts the bounded presence poller.
///  * <see cref="StopReplicationAsync"/> tears the engine, roster and poller down on disconnect, sign-out
///    or account switch, so peer sessions never outlive the identity they were established under.
///
/// There is no durable relay queue here: sending is a single opaque <c>Relay</c> invocation, and an
/// offline result leaves the outbox pending. The poller sends bounded authenticated wake requests for
/// authorised offline devices so the relay can emit contentless native wakes, then retries when presence brings
/// the peer online. Presence is discovered by polling <c>ResolvePresence</c>; no relay presence callback
/// or durable payload queue is required.
/// </summary>
public sealed partial class MeshClient : IReplicationTransport, IReplicationMetadataSource
{
    private volatile OnlineReplicationEngine? replicationEngine;
    private volatile RelayReplicationRoster? replicationRoster;
    private volatile ReplicationPresencePoller? replicationPoller;
    private readonly SemaphoreSlim replicationArmGate = new(1, 1);
    private readonly object replicationLifecycleGate = new();
    private Task replicationTeardown = Task.CompletedTask;
    private long replicationLifecycleVersion;

    // The relay authority this connection bound at connect time (from this handle's own directory
    // entry). The Challenge handler rebuilds the exact canonical connect string from these so the
    // signature verifies against what the hub reconstructs. A genesis handle carries 0 / "".
    private long connectAuthGeneration;
    private string connectCustodyHead = "";

    /// <summary>The live replication engine, or null when online replication is not armed/started.</summary>
    public OnlineReplicationEngine? OnlineReplicationEngine => replicationEngine;
    /// <summary>
    /// <see cref="IReplicationTransport"/> implementation: submit one opaque frame to the relay for
    /// forwarding. Returns a not-online result (never throws) when the hub is down so the engine can
    /// leave the outbox pending and retry, rather than losing the event.
    /// </summary>
    async Task<OnlineRelaySendResult> IReplicationTransport.SendAsync(OnlineRelayFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var connection = hub;
        if (connection is null || connection.State != HubConnectionState.Connected || !authenticated)
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);

        try
        {
            return await connection
                .InvokeAsync<OnlineRelaySendResult>(OnlineRelayMethods.Relay, frame, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceTransport("relay-send-failed", ex.Message);
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
        }
    }
    async Task<OnlineWakeResult> IReplicationTransport.WakeAsync(
        OnlineWakeRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = hub;
        if (connection is null || connection.State != HubConnectionState.Connected || !authenticated)
            return new OnlineWakeResult(false, OnlineWakeCodes.Invalid);
        try
        {
            return await connection
                .InvokeAsync<OnlineWakeResult>(OnlineRelayMethods.Wake, request, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceTransport("relay-wake-failed", ex.Message);
            return new OnlineWakeResult(false, OnlineWakeCodes.Invalid);
        }
    }


    // -----------------------------------------------------------------------
    // IReplicationMetadataSource: the roster and poller read relay authority (auth generation,
    // custody head, authorised device keys) over REST and online presence over the hub.
    // -----------------------------------------------------------------------

    async Task<HandleInfo?> IReplicationMetadataSource.FetchHandleAsync(string handle, CancellationToken ct)
    {
        var h = AppState.Norm(handle);
        if (h.Length == 0) return null;
        var relayUrl = state.Profile.RelayUrl;
        if (string.IsNullOrWhiteSpace(relayUrl)) return null;
        try
        {
            var http = httpFactory.CreateClient("relay");
            return await http.GetFromJsonAsync<HandleInfo>(
                $"{relayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}", Json, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TraceTransport("replication-handle-fetch-failed", ex.Message);
            return null;
        }
    }

    async Task<IReadOnlyList<RelayHandlePresence>> IReplicationMetadataSource.ResolvePresenceAsync(
        IReadOnlyList<string> handles, CancellationToken ct)
    {
        var connection = hub;
        if (connection is null || connection.State != HubConnectionState.Connected || !authenticated)
            return Array.Empty<RelayHandlePresence>();
        var targets = handles
            .Select(AppState.Norm)
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 0) return Array.Empty<RelayHandlePresence>();
        try
        {
            var snapshot = await connection
                .InvokeAsync<RelayPresenceSnapshot>(OnlineRelayMethods.ResolvePresence, targets, ct)
                .ConfigureAwait(false);
            return snapshot?.Handles ?? (IReadOnlyList<RelayHandlePresence>)Array.Empty<RelayHandlePresence>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TraceTransport("replication-presence-failed", ex.Message);
            return Array.Empty<RelayHandlePresence>();
        }
    }

    // -----------------------------------------------------------------------
    // Automatic arming / teardown (spec item 1). Driven by the authenticated hook and the
    // disconnect path; never by an external configuration call.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Arms online replication for the authenticated active profile: reads this handle's own relay
    /// directory entry for the authoritative auth generation and custody head, builds the real
    /// identity off the UI thread, starts the engine (which attaches the concrete projection) and the
    /// presence poller. Fail-closed and idempotent: any missing custody/identity/relay metadata
    /// surfaces an <see cref="OnlineReplicationError"/> and leaves replication unstarted rather than
    /// running an under-authenticated session.
    /// </summary>
    internal async Task ArmReplicationAsync(CancellationToken ct = default)
    {
        await replicationArmGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task teardown;
            long lifecycleVersion;
            lock (replicationLifecycleGate)
            {
                if (replicationEngine is not null) return;
                teardown = replicationTeardown;
                lifecycleVersion = replicationLifecycleVersion;
            }

            await teardown.WaitAsync(ct).ConfigureAwait(false);

            var connection = hub;
            if (connection is null || connection.State != HubConnectionState.Connected || !authenticated) return;
            lock (replicationLifecycleGate)
            {
                if (lifecycleVersion != replicationLifecycleVersion || replicationEngine is not null) return;
            }

            var ownHandle = AppState.Norm(state.Profile.Handle);
            if (ownHandle.Length == 0) return;

            IReplicationMetadataSource source = this;
            var own = await source.FetchHandleAsync(ownHandle, ct).ConfigureAwait(false);
            if (own is null)
                throw new OnlineReplicationError(
                    "The relay did not return this handle's directory entry; cannot establish custody authority.");

            var identity = state.BuildReplicationIdentity(own.AuthGeneration, own.CustodyHead);
            var roster = new RelayReplicationRoster(
                source,
                ownHandle,
                identity.AuthGeneration,
                identity.CustodyHead,
                localCustodyHead: state.LocalCustodyHead,
                surface: reason => Log?.Invoke($"replication roster: {reason}"),
                onOwnAuthorityChanged: () => TrackBackground(
                    ReArmReplicationAsync("authority-changed"), "replication re-arm"),
                timeProvider: timeProvider);

            lock (replicationLifecycleGate)
            {
                if (lifecycleVersion != replicationLifecycleVersion
                    || replicationEngine is not null
                    || !ReferenceEquals(hub, connection)
                    || connection.State != HubConnectionState.Connected
                    || !authenticated
                    || !string.Equals(AppState.Norm(state.Profile.Handle), ownHandle, StringComparison.Ordinal))
                    return;

                var engine = state.TryStartOnlineReplication(this, roster, identity);
                if (engine is null)
                    throw new OnlineReplicationError("No account database is open; replication cannot start.");

                replicationRoster = roster;
                replicationEngine = engine;
                engine.LocalWorkPending += OnReplicationLocalWorkPending;
                engine.Activity += OnReplicationEngineActivity;

                var poller = new ReplicationPresencePoller(
                    engine,
                    roster,
                    source,
                    candidateHandles: state.ReplicationPeerCandidates,
                    hasDueOutbox: state.HasDueOutbox,
                    ownHandle: ownHandle,
                    ownDevice: identity.DeviceId,
                    surface: reason => TraceTransport("replication-poll", reason),
                    bootstrapPeer: state.EmitOwnerBootstrapSnapshotAsync,
                    pollCompleted: OnReplicationPresencePollCompleted,
                    rosterOnline: OnReplicationRosterOnline,
                    accountRosterObserved: ApplyAccountDevicePresenceSnapshot);
                replicationPoller = poller;
                if (ShouldMaintainContinuousTransport) poller.Start();
            }

            RefreshReplicationStatus();
            TraceTransport("replication-armed", $"handle={ownHandle}; device={identity.DeviceId}");
        }
        catch (OnlineReplicationError ex)
        {
            TraceTransport("replication-arm-rejected", ex.Message);
            await StopReplicationAsync("arm-rejected").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TraceTransport("replication-arm-failed", ex.Message);
            await StopReplicationAsync("arm-failed").ConfigureAwait(false);
        }
        finally
        {
            replicationArmGate.Release();
        }
    }

    /// <summary>Tears replication down and re-arms it when this handle's authority changes.</summary>
    private async Task ReArmReplicationAsync(string reason)
    {
        TraceTransport("replication-rearm", $"reason={reason}");
        await StopReplicationAsync(reason).ConfigureAwait(false);
        await ArmReplicationAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the presence poller, detaches the engine, and returns the task that drains and disposes it.
    /// New engines wait for that task, so sessions from different engine generations cannot overlap.
    /// </summary>
    internal Task StopReplicationAsync(string reason)
    {
        Task teardown;
        bool hadEngine;
        bool hadPoller;
        lock (replicationLifecycleGate)
        {
            replicationLifecycleVersion++;

            var poller = replicationPoller;
            replicationPoller = null;
            hadPoller = poller is not null;
            var pollerDispose = poller?.DisposeAsync().AsTask() ?? Task.CompletedTask;

            var attached = replicationEngine;
            replicationEngine = null;
            if (attached is not null)
            {
                attached.LocalWorkPending -= OnReplicationLocalWorkPending;
                attached.Activity -= OnReplicationEngineActivity;
            }

            replicationRoster = null;
            var engine = state.DetachReplicationEngine() ?? attached;
            hadEngine = engine is not null;
            var engineDispose = engine?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            if (hadPoller || hadEngine)
                replicationTeardown = Task.WhenAll(replicationTeardown, pollerDispose, engineDispose);
            teardown = replicationTeardown;
        }

        TraceTransport(
            "replication-stopped",
            $"reason={reason}; engine={hadEngine}; poller={hadPoller}");
        return teardown;
    }

    internal void StopReplication(string reason)
    {
        var teardown = StopReplicationAsync(reason);
        if (!teardown.IsCompletedSuccessfully)
            TrackBackground(teardown, $"replication stop ({reason})");
    }

    private void OnReplicationLocalWorkPending()
    {
        replicationPoller?.Poke();
        SetReplicationStatus(ReplicationPhase.Synchronizing);
    }

    private void OnReplicationPresencePollCompleted(bool hasOnlinePendingPeer, bool hasPendingWork)
    {
        if (!hasOnlinePendingPeer && hasPendingWork)
            SetReplicationStatus(ReplicationPhase.WaitingForPeer);
        else if (!hasPendingWork)
            RefreshReplicationStatus(replicationPoller?.LastOnlinePeerDevice);
    }

    private void OnReplicationRosterOnline(IReadOnlyCollection<string> onlineDevices)
    {
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null
            || !Connected
            || !IsCurrentReplicationConnectionIdentity(identity)
            || !HasLocalDurableWorkFor(onlineDevices))
            return;
        WakeOnlineDelivery(identity, onlineDevices, "roster-online-transition");
    }

    internal bool IsReplicationRosterDeviceAvailable(string accountHandle, string deviceId)
        => replicationRoster?.ResolveDevice(accountHandle, deviceId) is { Revoked: false };

    /// <summary>
    /// Mobile backgrounding pauses continuous polling. Desktop, tray, and headless instances retain
    /// polling even when their window is deactivated or hidden.
    /// </summary>
    private void OnReplicationForegroundChanged(bool isForeground)
    {
        var poller = replicationPoller;
        if (poller is null) return;
        if (isForeground || ShouldMaintainContinuousTransport) poller.Resume();
        else poller.Pause();
    }

    /// <summary>
    /// Registers the single Protocol-9 inbound hub callback on a freshly built connection. Presence is
    /// polled, not pushed, so only <c>Deliver</c> is registered. The relay stamps sender identity on
    /// <see cref="OnlineRelayDelivery"/>; this validates the stamped route (sender present, addressed
    /// to this device/handle) before handing the delivery to the engine, which then performs the full
    /// roster + session authorisation.
    /// </summary>
    private void RegisterOnlineReplicationHandlers(HubConnection connection)
    {
        connection.On<OnlineRelayDelivery>(OnlineRelayMethods.Deliver, delivery =>
        {
            var engine = replicationEngine;
            if (engine is null) return;
            if (!ReplicationDeliveryGuard.ValidateRoute(
                    delivery, AppState.Norm(state.Profile.Handle), MyDeviceId, out var reject))
            {
                TraceTransport("replication-deliver-reject", reject);
                return;
            }
            replicationPoller?.Poke();
            TrackBackground(engine.HandleDeliveryAsync(delivery, CancellationToken.None), "replication deliver");
        });
    }
}
