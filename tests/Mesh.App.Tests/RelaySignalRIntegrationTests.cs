using System.Security.Cryptography;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Relay.LiveFaults;
using Mesh.Relay.Observability;
using Mesh.Relay.Push;
using Mesh.Relay.RateLimiting;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// End-to-end SignalR contract tests that host the REAL Protocol 9 relay hub in-process over
/// Kestrel and connect a REAL <see cref="HubConnection"/> client through the full connect
/// challenge handshake. Unlike the socket-free switchboard harness (which records the delivery
/// object directly), these tests exercise the actual SignalR serializer, so they prove the relay
/// sends a typed <see cref="OnlineRelayDelivery"/> that binds to the client's
/// <c>On&lt;OnlineRelayDelivery&gt;</c> handler. This is the regression guard for the blocker
/// where the relay forwarded a JSON string that the typed client handler could not bind.
/// </summary>
[TestClass]
public sealed class RelaySignalRIntegrationTests
{
    private WebApplication _app = null!;
    private InMemoryRelayStore _store = null!;
    private InMemoryBackplane _backplane = null!;
    private string _baseUrl = null!;
    private LiveFaultStore _faults = null!;
    private LiveFaultAuthorityObserver _authority = null!;
    private ManualTimeProvider _clock = null!;
    private readonly List<HubConnection> _connections = new();
    private const string AdminKey = "hosted-test-admin-key";

    [TestMethod]
    public void RecoveryScenarios_DoNotInvokeManualDeliveryTriggers()
    {
        var repository = FindRepositoryRoot();
        AssertNoManualRecoveryTrigger(
            Path.Combine(repository, "tests", "Mesh.App.Tests", "RelaySignalRIntegrationTests.cs"),
            "LongOutage_RealHostedRelay_RefreshesAuthorityAndPersistsExactOnce");
        AssertNoManualRecoveryTrigger(
            Path.Combine(repository, "tests", "Mesh.App.ComponentTests", "RelayLiveFaultRuntimeIntegrationTests.cs"),
            "ActualProgram_RealClients_CoalesceRetriesAndWakeDrainExactlyOnce");
    }

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _store = new InMemoryRelayStore();
        _clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-22T18:00:00Z"));
        _backplane = new InMemoryBackplane(_clock);
        var backplane = _backplane;
        _faults = new LiveFaultStore(new LiveFaultOptions { Enabled = true }, _clock);
        _authority = new LiveFaultAuthorityObserver();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton<IRelayStore>(_store);
        builder.Services.AddSingleton<IBackplane>(backplane);
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<MeshRouter>();
        builder.Services.AddSingleton<RelayFrameDedup>();
        builder.Services.AddSingleton(_faults);
        builder.Services.AddSingleton<LiveFaultHandshakeObserver>();
        builder.Services.AddSingleton<LiveFaultTransportObserver>();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(new RelayMetrics());
        builder.Services.AddSingleton<PushDispatcher>();
        builder.Services.AddSingleton<IMessageRateLimiter, AllowAllRateLimiter>();
        builder.Services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = OnlineReplicationLimits.MaxTransportBytes + 64 * 1024;
        });

        _app = builder.Build();
        _app.MapHub<MeshHub>("/hub");
        _app.MapGet("/handles/{handle}", async (string handle) =>
        {
            var normalized = handle.Trim().TrimStart('@').ToLowerInvariant();
            var record = await _store.GetHandleAsync(normalized);
            if (record is not null) _authority.Record(record);
            return record is null
                ? Results.NotFound()
                : Results.Ok(new HandleInfo(
                    record.Handle,
                    record.DisplayName,
                    record.DevicePublicKeys,
                    await _backplane.GetInstanceForAsync(normalized) is not null,
                    record.RegisteredAt,
                    record.AuthGeneration,
                    record.CustodyHead,
                    record.CustodyAuthority));
        });
        _app.MapLiveFaultAdminEndpoints(
            _faults,
            AdminKey,
            _store,
            authorityObserver: _authority);

        // Route directed backplane forwards to this instance's local delivery (single node).
        var router = _app.Services.GetRequiredService<MeshRouter>();
        await backplane.StartAsync(router.DeliverFromBackplaneAsync);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        _baseUrl = addresses.First();
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
        _connections.Clear();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [TestMethod]
    public async Task Deliver_RoundTrips_As_Typed_Object_Over_Real_SignalR()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");

        var (connA, _) = await ConnectAsync(alice);
        var (connB, bobDeliveries) = await ConnectAsync(bob);

        var frameId = Guid.NewGuid().ToString("n");
        const string ciphertext = "opaque-e2e-ciphertext-payload";
        var frame = new OnlineRelayFrame(bob.Handle, bob.DeviceId, frameId, OnlinePushClasses.Normal, ciphertext);

        var result = await connA.InvokeAsync<OnlineRelaySendResult>(OnlineRelayMethods.Relay, frame);

        Assert.IsTrue(result.Accepted, $"relay send should be accepted, got code {result.Code}");
        Assert.AreEqual(OnlineRelaySendCodes.Delivered, result.Code);

        var delivery = await bobDeliveries.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNotNull(delivery, "bob should receive a typed OnlineRelayDelivery");
        Assert.AreEqual("alice", delivery.FromHandle);
        Assert.AreEqual(alice.DeviceId, delivery.FromDevice);
        Assert.AreEqual("bob", delivery.ToHandle);
        Assert.AreEqual(bob.DeviceId, delivery.ToDevice);
        Assert.AreEqual(frameId, delivery.FrameId);
        Assert.AreEqual(ciphertext, delivery.Ciphertext);
        Assert.AreEqual(OnlinePushClasses.Normal, delivery.PushClass);

        GC.KeepAlive(connB);
    }

    [TestMethod]
    public async Task ResolvePresence_Reports_Online_Sibling_Over_Real_SignalR()
    {
        var alice = await RegisterAsync("alice");
        var bobKeys = KeyPair.New();
        var bobDevice = DeviceProtocol.DeviceId(bobKeys.PublicB64);
        await _store.UpsertHandleAsync("alice", bobKeys.PublicB64, "display", allowNewDevice: true);
        await _store.SetDeviceMetadataAsync("alice", bobDevice, "sibling", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
        var bob = new Registration("alice", bobDevice, bobKeys);

        var (connA, _) = await ConnectAsync(alice);
        await ConnectAsync(bob);

        var snapshot = await connA.InvokeAsync<OnlinePresenceSnapshot>(
            OnlineRelayMethods.ResolvePresence, new[] { "alice" });

        var presence = snapshot.Handles.Single(h => h.Handle == "alice");
        Assert.IsTrue(presence.Online, "alice handle should be online");
        CollectionAssert.Contains(presence.Devices.ToArray(), alice.DeviceId);
        CollectionAssert.Contains(presence.Devices.ToArray(), bobDevice);
    }

    [TestMethod]
    public async Task SendEnvelope_Forwards_OnlineOnly_Control_To_TargetDevice()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");
        var (connA, _) = await ConnectAsync(alice);
        var (connB, _) = await ConnectAsync(bob);
        var controls = new TypedSignal<string>();
        connB.On<string>(MeshHubProtocol.Receive, controls.Set);

        const string body = "opaque-control-body";
        var envelope = MeshEnvelope.Create(
            alice.Handle,
            bob.Handle,
            MeshKinds.Receipt,
            body,
            Sign(alice.Keys.PrivateB64, body),
            toDevice: bob.DeviceId);

        var result = await connA.InvokeAsync<MeshSendResult>(
            MeshHubProtocol.SendEnvelope, envelope);
        var receivedJson = await controls.WaitAsync(TimeSpan.FromSeconds(15));
        var received = System.Text.Json.JsonSerializer.Deserialize<MeshEnvelope>(
            receivedJson, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));

        Assert.IsTrue(result.Accepted);
        Assert.IsNotNull(received);
        Assert.AreEqual(alice.Handle, received.From);
        Assert.AreEqual(alice.DeviceId, received.FromDevice);
        Assert.AreEqual(bob.Handle, received.To);
        Assert.AreEqual(bob.DeviceId, received.ToDevice);
        Assert.AreEqual(MeshKinds.Receipt, received.Kind);
        Assert.AreEqual(body, received.Body);
    }

    [TestMethod]
    public async Task LiveFault_SuccessDropReturnsAcceptedThenStableRetryDelivers()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");
        var (connA, _) = await ConnectAsync(alice);
        var (connB, _) = await ConnectAsync(bob);
        var controls = new TypedSignal<string>();
        connB.On<string>(MeshHubProtocol.Receive, controls.Set);
        const string stableId = "m08-stable-control";
        const string body = "opaque-control-body-never-audited";
        _faults.Activate(new LiveFaultActivationRequest(
            "m08-success-drop",
            LiveFaultMode.SuccessDropBeforeDestination,
            LiveFaultDirection.Outbound,
            alice.Handle,
            bob.DeviceId,
            60,
            SourceDevice: alice.DeviceId,
            TargetAccount: bob.Handle,
            Kind: MeshKinds.Receipt,
            StableIdHash: LiveFaultIds.Hash(stableId)));
        var envelope = MeshEnvelope.Create(
            alice.Handle, bob.Handle, MeshKinds.Receipt, body,
            Sign(alice.Keys.PrivateB64, body), toDevice: bob.DeviceId, id: stableId);

        var first = await connA.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, envelope);
        await Task.Delay(100);
        Assert.IsTrue(first.Accepted);
        Assert.IsFalse(controls.IsCompleted, "success-drop must not reach the destination");

        var retry = await connA.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, envelope);
        var received = await controls.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsTrue(retry.Accepted);
        Assert.IsNotNull(received);
        Assert.IsFalse(JsonSerializer.Serialize(_faults.Audit()).Contains(body, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LiveFault_RejectBeforeForwardingReturnsExplicitFailure()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");
        var (connA, _) = await ConnectAsync(alice);
        var (_, bobDeliveries) = await ConnectAsync(bob);
        var frameId = "reject-stable-frame";
        _faults.Activate(new LiveFaultActivationRequest(
            "reject",
            LiveFaultMode.RejectBeforeForwarding,
            LiveFaultDirection.Outbound,
            alice.Handle,
            bob.DeviceId,
            60,
            SourceDevice: alice.DeviceId,
            TargetAccount: bob.Handle,
            Kind: LiveFaultStore.OnlineFrameKind,
            StableIdHash: LiveFaultIds.Hash(frameId)));

        var result = await connA.InvokeAsync<OnlineRelaySendResult>(
            OnlineRelayMethods.Relay,
            new OnlineRelayFrame(bob.Handle, bob.DeviceId, frameId, OnlinePushClasses.Normal, "opaque"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(LiveFaultStore.RejectedCode, result.Code);
        await Task.Delay(100);
        Assert.IsFalse(bobDeliveries.IsCompleted);
    }

    [TestMethod]
    public async Task LiveFault_DropBeforeForwardingLooksOffline()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");
        var (connA, _) = await ConnectAsync(alice);
        var (_, bobDeliveries) = await ConnectAsync(bob);
        var frameId = "drop-stable-frame";
        _faults.Activate(new LiveFaultActivationRequest(
            "drop",
            LiveFaultMode.DropBeforeForwarding,
            LiveFaultDirection.Outbound,
            alice.Handle,
            bob.DeviceId,
            60,
            SourceDevice: alice.DeviceId,
            TargetAccount: bob.Handle,
            Kind: LiveFaultStore.OnlineFrameKind,
            StableIdHash: LiveFaultIds.Hash(frameId)));

        var result = await connA.InvokeAsync<OnlineRelaySendResult>(
            OnlineRelayMethods.Relay,
            new OnlineRelayFrame(bob.Handle, bob.DeviceId, frameId, OnlinePushClasses.Normal, "opaque"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.NotOnline, result.Code);
        await Task.Delay(100);
        Assert.IsFalse(bobDeliveries.IsCompleted);
    }

    [TestMethod]
    public async Task HostedRoster_ExpiresThroughProductionCachePath()
    {
        var alice = await RegisterAsync("roster-alice");
        var bob = await RegisterAsync("roster-bob");
        var (aliceConnection, _) = await ConnectAsync(alice);
        var (bobConnection, _) = await ConnectAsync(bob);
        var source = new HostedMetadataSource(_baseUrl);
        var surfaces = new List<string>();
        var roster = new RelayReplicationRoster(
            source,
            alice.Handle,
            0,
            "",
            _ => "",
            surfaces.Add,
            () => { },
            _clock);

        await roster.RefreshAsync([bob.Handle], CancellationToken.None);
        Assert.AreEqual(1, source.LookupCount);
        Assert.AreEqual(bob.DeviceId, roster.ResolveDevice(bob.Handle, bob.DeviceId)!.DeviceId);
        var live = await aliceConnection.InvokeAsync<OnlinePresenceSnapshot>(
            OnlineRelayMethods.ResolvePresence, new[] { bob.Handle });
        Assert.IsTrue(live.Handles.Single().Online);

        _clock.Advance(TimeSpan.FromSeconds(31));

        Assert.IsNull(roster.ResolveDevice(bob.Handle, bob.DeviceId));
        Assert.AreEqual(-1, roster.AuthGeneration(bob.Handle));
        Assert.AreEqual(0, roster.AuthorizedDevices(bob.Handle).Count);
        source.Outage = true;
        await roster.RefreshAsync([bob.Handle], CancellationToken.None);
        Assert.AreEqual(2, source.LookupCount);
        Assert.AreEqual(0, roster.AuthorizedDevices(bob.Handle).Count);
        Assert.IsTrue(surfaces.Any(message => message.Contains("unavailable", StringComparison.Ordinal)));
        Console.WriteLine(
            $"REAL_ROSTER_EXPIRY handle={bob.Handle} device={bob.DeviceId} " +
            $"lookups={source.LookupCount} cachedBefore=1 cachedAfter=0 authAfter=-1");
        GC.KeepAlive(bobConnection);
    }

    [TestMethod]
    public async Task HostedEncryptedSiblings_ExpiredRosterBlocksResultUntilFreshThenReceiptsExactlyOnce()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "hosted-roster-emission", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var key = Enumerable.Range(65, 32).Select(value => (byte)value).ToArray();
        OnlineReplicationEngine? desktopEngine = null;
        OnlineReplicationEngine? androidEngine = null;
        MeshDb? desktopDb = null;
        MeshDb? androidDb = null;
        try
        {
            var desktop = await RegisterAsync("roster-emission");
            var androidKeys = KeyPair.New();
            var android = new Registration(
                desktop.Handle,
                DeviceProtocol.DeviceId(androidKeys.PublicB64),
                androidKeys);
            await _store.UpsertHandleAsync(
                desktop.Handle, android.Keys.PublicB64, "display", allowNewDevice: true);
            await _store.SetDeviceMetadataAsync(
                desktop.Handle,
                android.DeviceId,
                "Android",
                DevicePlatforms.Android,
                false,
                false,
                MeshProtocol.Version);

            var (desktopConnection, _) = await ConnectAsync(desktop);
            var (androidConnection, _) = await ConnectAsync(android);
            var desktopSource = new HostedMetadataSource(_baseUrl);
            var androidSource = new HostedMetadataSource(_baseUrl);
            var desktopRoster = new RelayReplicationRoster(
                desktopSource,
                desktop.Handle,
                0,
                "",
                _ => "",
                _ => { },
                () => { },
                _clock);
            var androidRoster = new RelayReplicationRoster(
                androidSource,
                android.Handle,
                0,
                "",
                _ => "",
                _ => { },
                () => { },
                _clock);
            await Task.WhenAll(
                desktopRoster.RefreshAsync([desktop.Handle], CancellationToken.None),
                androidRoster.RefreshAsync([android.Handle], CancellationToken.None));

            desktopDb = MeshDb.Open(
                Path.Combine(directory, "desktop.meshdb"), key, _clock);
            androidDb = MeshDb.Open(
                Path.Combine(directory, "android.meshdb"), key, _clock);
            var desktopApplier = new RecordingApplier();
            var androidApplier = new RecordingApplier();
            desktopEngine = new OnlineReplicationEngine(
                desktopDb,
                new ReplicationIdentity(
                    desktop.Handle,
                    desktop.DeviceId,
                    desktop.Keys.PublicB64,
                    desktop.Keys.PrivateB64,
                    "desktop-epoch",
                    0,
                    OnlineReplicationProtocol.ZeroHash),
                new HostedReplicationTransport(desktopConnection),
                desktopRoster,
                desktopApplier,
                sendTimeout: TimeSpan.FromSeconds(5));
            androidEngine = new OnlineReplicationEngine(
                androidDb,
                new ReplicationIdentity(
                    android.Handle,
                    android.DeviceId,
                    android.Keys.PublicB64,
                    android.Keys.PrivateB64,
                    "android-epoch",
                    0,
                    OnlineReplicationProtocol.ZeroHash),
                new HostedReplicationTransport(androidConnection),
                androidRoster,
                androidApplier,
                deviceIsDesktop: false,
                sendTimeout: TimeSpan.FromSeconds(5));
            desktopEngine.EnsureLocalOrigin();
            androidEngine.EnsureLocalOrigin();
            using var desktopDelivery = desktopConnection.On<OnlineRelayDelivery>(
                OnlineRelayMethods.Deliver,
                delivery => desktopEngine.HandleDeliveryAsync(delivery));
            using var androidDelivery = androidConnection.On<OnlineRelayDelivery>(
                OnlineRelayMethods.Deliver,
                delivery => androidEngine.HandleDeliveryAsync(delivery));

            await Task.WhenAll(
                desktopEngine.OnPresenceOnlineAsync(android.Handle, android.DeviceId),
                androidEngine.OnPresenceOnlineAsync(desktop.Handle, desktop.DeviceId));
            await WaitUntilAsync(
                () => desktopEngine.IsSessionEstablished(android.DeviceId)
                      && androidEngine.IsSessionEstablished(desktop.DeviceId),
                TimeSpan.FromSeconds(10));

            _clock.Advance(TimeSpan.FromSeconds(31));
            desktopSource.FetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            desktopSource.FetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var emission = desktopEngine.EmitLocalAsync(
                new ReplicationPayloadCodec.DomainEnvelope(
                    ReplicationOpKinds.Message,
                    ReplicationPayloadCodec.DomainAction.AppendLine,
                    "assistant-result",
                    "hosted-roster-conversation",
                    "v1",
                    """{"role":"assistant","text":"encrypted Android result"}"""),
                [desktop.Handle],
                domainWork: static (_, _, _) => { });

            await desktopSource.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(emission.IsCompleted);
            Assert.AreEqual(
                0,
                desktopDb.QueryEvents(desktop.DeviceId, "desktop-epoch", 1, 64).Count);
            Assert.AreEqual(0, desktopDb.GetPendingReplicationIntents().Count);

            desktopSource.FetchRelease.TrySetResult(true);
            var eventId = await emission.WaitAsync(TimeSpan.FromSeconds(10));
            var localEvent = desktopDb.GetEvent(eventId)!;
            CollectionAssert.AreEquivalent(
                new[] { desktop.DeviceId, android.DeviceId },
                ReplicationPayloadCodec.RecipientDeviceIds(localEvent.Ciphertext).ToArray());

            await WaitUntilAsync(
                () => androidApplier.Count == 1
                      && desktopDb.CountUnpersistedOutbox([desktop.Handle]) == 0,
                TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, androidDb.QueryEvents(
                desktop.DeviceId, "desktop-epoch", 1, 64).Count);
            Assert.AreEqual(1, androidApplier.Count);
            Assert.AreEqual("assistant-result", androidApplier.Applied.Single().Env.EntityId);
            Assert.AreEqual(0, desktopDb.CountUnpersistedOutbox([desktop.Handle]));
            Console.WriteLine(
                $"REAL_ROSTER_EMISSION event={eventId} recipientSlots=2 " +
                "androidApplied=1 unpersistedOutbox=0");
        }
        finally
        {
            if (desktopEngine is not null) await desktopEngine.DisposeAsync();
            if (androidEngine is not null) await androidEngine.DisposeAsync();
            desktopDb?.Dispose();
            androidDb?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task HostedAuthorityRecovery_RejectsStaleAndReplayThenAcceptsFreshNonce()
    {
        var registration = await RegisterAsync("authority-client");
        string? oldNonce = null;
        string? oldCanonical = null;
        string? oldSignature = null;
        var (original, _) = await ConnectAsync(registration, nonce =>
        {
            oldNonce = nonce;
            var current = _store.GetHandleAsync(registration.Handle).GetAwaiter().GetResult()!;
            oldCanonical = RelayConnectChallenge.Canonical(
                nonce,
                registration.Handle,
                registration.DeviceId,
                MeshProtocol.Version,
                current.AuthGeneration,
                current.CustodyHead);
            oldSignature = Sign(registration.Keys.PrivateB64, oldCanonical);
        });
        await original.DisposeAsync();
        _connections.Remove(original);
        Assert.IsNotNull(oldNonce);
        Assert.IsNotNull(oldSignature);

        var stale = (await _store.GetHandleAsync(registration.Handle))!;
        Assert.IsTrue(await _store.AdvanceCustodyAsync(
            registration.Handle,
            stale.AuthGeneration,
            stale.AuthGeneration + 1,
            "fresh-authority-head"));

        var staleAttempt = await TryConnectWithAuthorityAsync(
            registration,
            stale.AuthGeneration,
            stale.CustodyHead,
            (_, canonical) => Sign(registration.Keys.PrivateB64, canonical));
        Assert.IsFalse(staleAttempt.Authenticated);
        Assert.IsNull(staleAttempt.Nonce, "stale authority must be rejected before challenge");

        using var http = new HttpClient();
        var fresh = await http.GetFromJsonAsync<HandleInfo>(
            $"{_baseUrl}/handles/{registration.Handle}");
        Assert.IsNotNull(fresh);
        Assert.AreEqual(stale.AuthGeneration + 1, fresh.AuthGeneration);
        Assert.AreEqual("fresh-authority-head", fresh.CustodyHead);

        var replayAttempt = await TryConnectWithAuthorityAsync(
            registration,
            fresh.AuthGeneration,
            fresh.CustodyHead,
            (_, _) => oldSignature!);
        Assert.IsFalse(replayAttempt.Authenticated);
        Assert.IsNotNull(replayAttempt.Nonce);
        Assert.AreNotEqual(oldNonce, replayAttempt.Nonce);

        string? newCanonical = null;
        string? newSignature = null;
        var accepted = await TryConnectWithAuthorityAsync(
            registration,
            fresh.AuthGeneration,
            fresh.CustodyHead,
            (_, canonical) =>
            {
                newCanonical = canonical;
                newSignature = Sign(registration.Keys.PrivateB64, canonical);
                return newSignature;
            });
        Assert.IsTrue(accepted.Authenticated);
        Assert.IsNotNull(accepted.Nonce);
        Assert.AreNotEqual(oldNonce, accepted.Nonce);
        Assert.AreNotEqual(oldCanonical, newCanonical);
        Assert.AreNotEqual(oldSignature, newSignature);
        Console.WriteLine(
            $"REAL_AUTH_RECOVERY staleGeneration={stale.AuthGeneration} " +
            $"freshGeneration={fresh.AuthGeneration} oldNonce={oldNonce} replayNonce={replayAttempt.Nonce} " +
            $"newNonce={accepted.Nonce} staleRejected=true replayRejected=true newSignatureAccepted=true");
    }

    [TestMethod]
    public async Task HostedAuthorityRotationSeam_RequiresFreshLookupAndNewDeviceKey()
    {
        var authorityA = await RegisterAsync("authority-rotation");
        string? nonceA = null;
        var (original, _) = await ConnectAsync(authorityA, nonce => nonceA = nonce);
        await original.DisposeAsync();
        _connections.Remove(original);

        var source = new HostedMetadataSource(_baseUrl);
        var roster = new RelayReplicationRoster(
            source,
            "lookup-client",
            0,
            "",
            _ => "",
            _ => { },
            () => { },
            _clock);
        await roster.RefreshAsync([authorityA.Handle], CancellationToken.None);
        var versionA = roster.AuthGeneration(authorityA.Handle);
        Assert.AreEqual(authorityA.DeviceId, roster.ResolveDevice(
            authorityA.Handle, authorityA.DeviceId)!.DeviceId);
        var fingerprintA = LiveFaultAuthorityObserver.Fingerprint(authorityA.Keys.PublicB64);

        var keysB = KeyPair.New();
        var deviceB = DeviceProtocol.DeviceId(keysB.PublicB64);
        using var http = new HttpClient();
        using var rotationRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_baseUrl}/admin/live-faults/rotate-authority");
        rotationRequest.Headers.Add("X-Mesh-Admin-Key", AdminKey);
        rotationRequest.Content = JsonContent.Create(new LiveFaultAuthorityRotationRequest(
            authorityA.Handle,
            authorityA.DeviceId,
            keysB.PublicB64,
            "authority-head-b"));
        using var rotationResponse = await http.SendAsync(rotationRequest);
        rotationResponse.EnsureSuccessStatusCode();
        var rotation = await rotationResponse.Content
            .ReadFromJsonAsync<LiveFaultAuthorityRotationResult>();
        Assert.IsNotNull(rotation);
        Assert.AreEqual(deviceB, rotation.NewDeviceId);
        Assert.AreEqual(versionA + 1, rotation.AuthGeneration);
        Assert.AreNotEqual(fingerprintA, rotation.PublicKeyFingerprint);

        var staleA = await TryConnectWithAuthorityAsync(
            authorityA,
            rotation.AuthGeneration,
            rotation.CustodyHead,
            (_, canonical) => Sign(authorityA.Keys.PrivateB64, canonical));
        Assert.IsFalse(staleA.Authenticated, "the revoked A key must not authenticate");

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsNull(roster.ResolveDevice(authorityA.Handle, authorityA.DeviceId),
            "expired authority A must not remain usable");
        await roster.RefreshAsync([authorityA.Handle], CancellationToken.None);
        Assert.AreEqual(2, source.LookupCount);
        Assert.AreEqual(rotation.AuthGeneration, roster.AuthGeneration(authorityA.Handle));
        Assert.AreEqual(deviceB, roster.ResolveDevice(authorityA.Handle, deviceB)!.DeviceId);
        var lookups = _authority.Snapshot()
            .Where(item => item.Handle == authorityA.Handle)
            .ToArray();
        Assert.AreEqual(2, lookups.Length, "authority A and B must each cross the hosted lookup boundary once");
        Assert.AreEqual(versionA, lookups[0].AuthGeneration);
        Assert.AreEqual(rotation.AuthGeneration, lookups[1].AuthGeneration);
        CollectionAssert.AreEqual(
            new[] { fingerprintA },
            lookups[0].PublicKeyFingerprints.ToArray());
        CollectionAssert.AreEqual(
            new[] { rotation.PublicKeyFingerprint },
            lookups[1].PublicKeyFingerprints.ToArray());

        var authorityB = new Registration(authorityA.Handle, deviceB, keysB);
        string? nonceB = null;
        var acceptedB = await TryConnectWithAuthorityAsync(
            authorityB,
            rotation.AuthGeneration,
            rotation.CustodyHead,
            (nonce, canonical) =>
            {
                nonceB = nonce;
                return Sign(keysB.PrivateB64, canonical);
            });
        Assert.IsTrue(acceptedB.Authenticated);
        Assert.IsNotNull(nonceB);
        Assert.AreNotEqual(nonceA, nonceB);
        Console.WriteLine(
            $"AUTHORITY_ROTATION lookupCount={lookups.Length} versionA={versionA} " +
            $"versionB={rotation.AuthGeneration} fingerprintA={fingerprintA} " +
            $"fingerprintB={rotation.PublicKeyFingerprint} nonceA={nonceA} nonceB={nonceB} " +
            "staleARejected=true authenticatedB=true");
    }

    [TestMethod]
    public async Task RuntimeCli_RunFailureFinallyDeactivatesHostedRule()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "cli-finally", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var gate = Path.Combine(directory, "fail-gate.ps1");
        await File.WriteAllTextAsync(
            gate,
            "param([string]$RuleId,[string]$RelayBaseUri)\nthrow \"intentional inner failure: $RuleId\"\n");
        const string ruleId = "runtime-cli-finally";
        var cli = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "_deploy", "test-relay", "Invoke-MeshLiveFault.ps1"));
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-File", cli,
                         "-Action", "Run",
                         "-BaseUri", _baseUrl,
                         "-AdminKey", AdminKey,
                         "-RuleId", ruleId,
                         "-SourceAccount", "cli-source",
                         "-TargetDevice", "abcdef012345",
                         "-GateScript", gate
                     })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Could not launch PowerShell CLI.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreNotEqual(0, process.ExitCode);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Mesh-Admin-Key", AdminKey);
            var status = await http.GetFromJsonAsync<LiveFaultRuleStatus>(
                $"{_baseUrl}/admin/live-faults/{ruleId}");
            var audit = await http.GetFromJsonAsync<List<LiveFaultAuditEntry>>(
                $"{_baseUrl}/admin/live-faults/audit");
            Assert.IsNotNull(status);
            Assert.IsFalse(status.Active);
            Assert.IsNotNull(status.DeactivatedAt);
            CollectionAssert.AreEqual(
                new[] { "activated", "deactivated" },
                audit!.Where(entry => entry.RuleId == ruleId).Select(entry => entry.Event).ToArray());
            Console.WriteLine(
                $"REAL_CLI_FINALLY processExit={process.ExitCode} rule={ruleId} active={status.Active} " +
                $"audit={string.Join(',', audit.Where(entry => entry.RuleId == ruleId).Select(entry => entry.Event))} " +
                $"stderr={stderr.Replace(Environment.NewLine, " ").Trim()} stdout={stdout.Replace(Environment.NewLine, " ").Trim()}");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task M08_RealHostedRelay_RetriesStableTerminalAndPersistsExactlyOnce()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "hosted-m08", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            var alice = await RegisterAsync("alice");
            var bob = await RegisterAsync("bob");
            var (aliceConnection, _) = await ConnectAsync(alice);
            var (bobConnection, _) = await ConnectAsync(bob);
            var aliceControls = Channel.CreateUnbounded<string>();
            var bobControls = Channel.CreateUnbounded<string>();
            aliceConnection.On<string>(
                MeshHubProtocol.Receive,
                payload => aliceControls.Writer.TryWrite(payload));
            bobConnection.On<string>(
                MeshHubProtocol.Receive,
                payload => bobControls.Writer.TryWrite(payload));

            using var requester = MeshDb.Open(
                Path.Combine(directory, "requester.meshdb"), key, _clock);
            using var executor = MeshDb.Open(
                Path.Combine(directory, "executor.meshdb"), key, _clock);
            requester.SaveProfile(new MeshProfile());
            executor.SaveProfile(new MeshProfile());
            var journal = new InMemoryTopicSendIdentityStore();
            var sends = new TopicSendCoordinator(identityStore: journal);
            var stableSubmission = sends.CreateSnapshot(
                "thread-hosted-m08",
                bob.DeviceId,
                composerRevision: 1,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes("execute exactly once"))),
                _clock.GetUtcNow());
            var request = new TopicRunRequestPayload(
                stableSubmission.RunId,
                "thread-hosted-m08",
                stableSubmission.LineId,
                "owner",
                "execute exactly once",
                _clock.GetUtcNow(),
                bob.DeviceId,
                TopicTurnMode.Single);
            requester.EnsureOwnThread(request.ThreadId, "Hosted M08", request.TriggerAt);
            TopicRunBeginResult? begin = null;
            var submission = sends.Submit(
                stableSubmission,
                (snapshot, handoff) =>
                {
                    Assert.IsTrue(journal.TryGetUnresolved(
                        snapshot.ScopeIdentity,
                        out var persisted));
                    Assert.AreEqual(snapshot.OperationId, persisted!.OperationId);
                    begin = requester.BeginTopicRun(new TopicRunBeginCommand(
                        new TopicTurnDraft(
                            request.RunId,
                            request.ThreadId,
                            request.TriggerLineId,
                            request.TriggerHandle,
                            request.TriggerText,
                            request.TriggerAt,
                            request.TurnMode,
                            request.TargetDeviceId,
                            TriggerOperationId: persisted.OperationId),
                        new ExecutionDevice(
                            bob.DeviceId,
                            "Executor",
                            DevicePlatforms.Windows),
                        TopicRunBeginMode.Remote,
                        TopicAcceptancePolicy.Create(request, request.TriggerAt),
                        request,
                        []));
                    if (begin.Committed) handoff.MarkDurableBoundaryEntered();
                    return Task.FromResult(new TopicSendHandoff(
                        begin.Committed,
                        begin.Committed ? "accepted" : "local_persistence_failed",
                        begin.ProjectionError));
                });
            Assert.AreEqual(TopicSendSubmissionKind.Started, submission.Kind);
            using (var beginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (begin is null)
                    await Task.Delay(10, beginTimeout.Token);
            Assert.IsTrue(begin.Committed);
            Assert.IsTrue(begin.Created);
            Assert.IsNotNull(begin.Outbox);
            var requestOutbox = new TopicRequestOutboxHandler(requester, _clock);
            var queued = begin.Outbox;
            var requesterHandler = new TopicDurabilityHandler(requester, _clock);
            var executorHandler = new TopicDurabilityHandler(executor, _clock);
            var aliceTransport = new HostedTopicTransport(aliceConnection, alice, bob);
            var bobTransport = new HostedTopicTransport(bobConnection, bob, alice);

            var sentRequest = await new TopicRequestOutboxDelivery(
                    requestOutbox, aliceTransport, _clock)
                .TrySendAsync(queued, CancellationToken.None);
            Assert.IsTrue(sentRequest.TransportResult!.Accepted);
            var requestEnvelope = await ReadEnvelopeAsync(bobControls.Reader);
            Assert.AreEqual(MeshKinds.TopicRunRequest, requestEnvelope.Kind);
            Assert.IsTrue(TopicRunProtocol.TryParseRequest(requestEnvelope.Body, out var parsedRequest));
            var inbound = executorHandler.AcceptRequest(parsedRequest, alice.DeviceId);
            var executionState = new Mesh.App.Services.AppState();
            executionState.Profile.Handle = bob.Handle;
            executionState.Profile.Model.ApiKey = "deterministic-hosted-boundary";
            executionState.Profile.OwnThreads.Add(new OwnThread
            {
                Id = request.ThreadId,
                Title = "Hosted M08",
                Lines =
                [
                    new ChatLine
                    {
                        Id = request.TriggerLineId,
                        Role = "user",
                        Text = request.TriggerText
                    }
                ]
            });
            var runnerInvocations = 0;
            var runner = new TopicTurnRunner(
                new AgentService
                {
                    Continue = (_, _, _) =>
                    {
                        Interlocked.Increment(ref runnerInvocations);
                        executionState.Profile.OwnThreads.Single().Lines.Add(new ChatLine
                        {
                            Id = "response-hosted-m08",
                            Role = "assistant",
                            Text = "deterministic hosted response"
                        });
                        return Task.FromResult("deterministic hosted response");
                    }
                },
                executionState);
            var runnerProgress = new SynchronousProgress<TopicRunUpdatePayload>();
            var runnerCompletion = await runner.ExecuteAsync(
                new TopicTurnDraft(
                    request.RunId,
                    request.ThreadId,
                    request.TriggerLineId,
                    request.TriggerHandle,
                    request.TriggerText,
                    request.TriggerAt,
                    request.TurnMode,
                    request.TargetDeviceId),
                runnerProgress,
                CancellationToken.None);
            Assert.AreEqual(TopicRunPhase.Completed, runnerCompletion.Phase);
            Assert.AreEqual(1, runnerInvocations);
            Assert.AreEqual(
                1,
                executionState.Profile.OwnThreads.Single().Lines.Count(line =>
                    line.Role == "assistant"
                    && line.Text == "deterministic hosted response"));

            var acceptance = TopicAcceptancePolicy.Create(request, inbound.AcceptedAt);
            var acceptanceId = TopicControlProtocol.EnvelopeId("topic.accepted", request.RunId);
            var executorDelivery = new TopicControlOutboxDelivery(
                executor, bobTransport, _clock);
            Assert.IsTrue((await executorDelivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(acceptanceId)!,
                CancellationToken.None))!.Accepted);
            var acceptanceEnvelope = await ReadEnvelopeAsync(aliceControls.Reader);
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(acceptanceEnvelope.Body, out var receivedAcceptance));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                requesterHandler.HandleUpdate(
                    receivedAcceptance, bob.DeviceId, acceptanceEnvelope.Id));
            await SendAndApplyReceiptAsync(
                aliceTransport, bobControls.Reader, executorHandler,
                acceptance, alice.DeviceId);
            Assert.IsNull(executor.GetDeviceEnvelopeOutbox(acceptanceId));

            var terminal = acceptance with
            {
                Phase = TopicRunPhase.Completed,
                Status = "Completed",
                Timestamp = _clock.GetUtcNow().AddSeconds(1)
            };
            _ = executorHandler.CompleteRun(
                request.RunId, InboundTopicRunStates.Completed, terminal, alice.DeviceId);
            var terminalId = TopicControlProtocol.EnvelopeId("topic.terminal", request.RunId);
            _faults.Activate(new LiveFaultActivationRequest(
                "m08-hosted-terminal-success-drop",
                LiveFaultMode.SuccessDropBeforeDestination,
                LiveFaultDirection.Outbound,
                bob.Handle,
                alice.DeviceId,
                60,
                SourceDevice: bob.DeviceId,
                TargetAccount: alice.Handle,
                Kind: MeshKinds.TopicRunUpdate,
                StableIdHash: LiveFaultIds.Hash(terminalId)));

            Assert.IsTrue((await executorDelivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(terminalId)!,
                CancellationToken.None))!.Accepted);
            await Task.Delay(100);
            Assert.IsFalse(aliceControls.Reader.TryRead(out _),
                "the first accepted terminal must be success-dropped");

            _clock.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval + TimeSpan.FromSeconds(1));
            Assert.IsTrue((await executorDelivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(terminalId)!,
                CancellationToken.None))!.Accepted);
            var terminalEnvelope = await ReadEnvelopeAsync(aliceControls.Reader);
            Assert.AreEqual(terminalId, terminalEnvelope.Id);
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(terminalEnvelope.Body, out var receivedTerminal));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                requesterHandler.HandleUpdate(receivedTerminal, bob.DeviceId, terminalEnvelope.Id));

            _clock.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval + TimeSpan.FromSeconds(1));
            Assert.IsTrue((await executorDelivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(terminalId)!,
                CancellationToken.None))!.Accepted);
            var duplicateEnvelope = await ReadEnvelopeAsync(aliceControls.Reader);
            Assert.AreEqual(terminalId, duplicateEnvelope.Id);
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(duplicateEnvelope.Body, out var duplicateTerminal));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Duplicate,
                requesterHandler.HandleUpdate(duplicateTerminal, bob.DeviceId, duplicateEnvelope.Id));

            await SendAndApplyReceiptAsync(
                aliceTransport, bobControls.Reader, executorHandler,
                terminal, alice.DeviceId);

            Assert.AreEqual(1, _faults.Get("m08-hosted-terminal-success-drop")!.UseCount);
            Assert.AreEqual(3, bobTransport.AttemptsFor(terminalId));
            Assert.AreEqual(1, runnerInvocations);
            Assert.AreEqual(1, executor.ListInboundTopicRuns().Count);
            Assert.AreEqual(
                InboundTopicRunStates.Completed,
                executor.GetInboundTopicRun(request.RunId)!.State);
            Assert.AreEqual(2, requester.ListReceivedTopicControls().Count);
            Assert.IsNull(requester.GetTopicOutbox(request.RunId));
            Assert.AreEqual(0, executor.ListDeviceEnvelopeOutbox().Count);
            Console.WriteLine(
                $"REAL_DB_ASSERT M08 runId={request.RunId} terminalId={terminalId} " +
                $"receiverRuns=1 runnerInvocations={runnerInvocations} assistantResponses=1 " +
                $"terminalTransportAttempts={bobTransport.AttemptsFor(terminalId)} " +
                "terminalWinners=1 receiverState=completed requesterControls=2 requesterOutbox=0 " +
                "receiverOutbox=0 faultUses=1 stableTerminalDuplicates=1");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task LongOutage_RealHostedRelay_RefreshesAuthorityAndPersistsExactOnce()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "hosted-outage", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var key = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
        try
        {
            var alice = await RegisterAsync("outage-alice");
            var bob = await RegisterAsync("outage-bob");
            var (aliceConnection, _) = await ConnectAsync(alice);
            var (oldBobConnection, _) = await ConnectAsync(bob);
            var authoritySource = new HostedMetadataSource(_baseUrl);
            var authorityRoster = new RelayReplicationRoster(
                authoritySource,
                "outage-authority-reader",
                0,
                "",
                _ => "",
                _ => { },
                () => { },
                _clock);
            await authorityRoster.RefreshAsync([alice.Handle], CancellationToken.None);
            Assert.AreEqual(alice.DeviceId, authorityRoster.ResolveDevice(
                alice.Handle, alice.DeviceId)!.DeviceId);
            await oldBobConnection.DisposeAsync();
            _connections.Remove(oldBobConnection);

            using var requester = MeshDb.Open(
                Path.Combine(directory, "requester.meshdb"), key, _clock);
            using var executor = MeshDb.Open(
                Path.Combine(directory, "executor.meshdb"), key, _clock);
            requester.SaveProfile(new MeshProfile());
            executor.SaveProfile(new MeshProfile());
            var request = new TopicRunRequestPayload(
                "run-hosted-long-outage",
                "thread-hosted-long-outage",
                "line-hosted-long-outage",
                "owner",
                "queued across route isolation",
                _clock.GetUtcNow(),
                bob.DeviceId,
                TopicTurnMode.Single);
            requester.EnsureOwnThread(request.ThreadId, "Hosted outage", request.TriggerAt);
            requester.SetOwnThreadExecution(
                request.ThreadId,
                bob.DeviceId,
                request.TriggerAt,
                request.RunId,
                "Executor",
                DevicePlatforms.Windows);
            var requestHandler = new TopicRequestOutboxHandler(requester, _clock);
            var queued = requestHandler.Queue(bob.DeviceId, request, []);
            var requesterDurability = new TopicDurabilityHandler(requester, _clock);
            var executorDurability = new TopicDurabilityHandler(executor, _clock);

            _faults.Activate(new LiveFaultActivationRequest(
                "hosted-long-outage-route",
                LiveFaultMode.DropBeforeForwarding,
                LiveFaultDirection.Outbound,
                alice.Handle,
                bob.DeviceId,
                180,
                MaxUses: 20,
                SourceDevice: alice.DeviceId,
                TargetAccount: bob.Handle,
                Kind: MeshKinds.TopicRunRequest));

            var before = (await _store.GetHandleAsync(bob.Handle))!;
            Assert.IsTrue(await _store.AdvanceCustodyAsync(
                bob.Handle, before.AuthGeneration, before.AuthGeneration + 1, "head-after-outage"));
            _clock.Advance(TimeSpan.FromSeconds(31));
            var challenges = new List<string>();
            var (bobConnection, _) = await ConnectAsync(bob, challenges.Add);
            Assert.AreEqual(1, challenges.Count);
            Assert.IsFalse(string.IsNullOrWhiteSpace(challenges[0]));
            var refreshed = (await _store.GetHandleAsync(bob.Handle))!;
            Assert.AreEqual(before.AuthGeneration + 1, refreshed.AuthGeneration);
            Assert.AreEqual("head-after-outage", refreshed.CustodyHead);

            var bobControls = Channel.CreateUnbounded<string>();
            var aliceControls = Channel.CreateUnbounded<string>();
            bobConnection.On<string>(
                MeshHubProtocol.Receive,
                payload => bobControls.Writer.TryWrite(payload));
            aliceConnection.On<string>(
                MeshHubProtocol.Receive,
                payload => aliceControls.Writer.TryWrite(payload));
            var aliceTransport = new HostedTopicTransport(aliceConnection, alice, bob);
            var initialAliceTransport = aliceTransport;
            var bobTransport = new HostedTopicTransport(bobConnection, bob, alice);
            var requestDelivery = new TopicRequestOutboxDelivery(
                requestHandler, aliceTransport, _clock);

            var isolated = await requestDelivery.TrySendAsync(queued, CancellationToken.None);
            Assert.IsFalse(isolated.TransportResult!.Accepted);
            Assert.AreEqual(OnlineRelaySendCodes.NotOnline, isolated.TransportResult.Code);
            Assert.AreEqual(
                TopicOutboxStates.Pending,
                requester.GetTopicOutbox(request.RunId)!.State);
            Assert.IsFalse(bobControls.Reader.TryRead(out _));

            await aliceConnection.DisposeAsync();
            _connections.Remove(aliceConnection);
            var keysB = KeyPair.New();
            var deviceB = DeviceProtocol.DeviceId(keysB.PublicB64);
            using var http = new HttpClient();
            using var rotationRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl}/admin/live-faults/rotate-authority");
            rotationRequest.Headers.Add("X-Mesh-Admin-Key", AdminKey);
            rotationRequest.Content = JsonContent.Create(new LiveFaultAuthorityRotationRequest(
                alice.Handle,
                alice.DeviceId,
                keysB.PublicB64,
                "outage-client-authority-b"));
            using var rotationResponse = await http.SendAsync(rotationRequest);
            rotationResponse.EnsureSuccessStatusCode();
            var rotation = await rotationResponse.Content
                .ReadFromJsonAsync<LiveFaultAuthorityRotationResult>();
            Assert.IsNotNull(rotation);
            var staleA = await TryConnectWithAuthorityAsync(
                alice,
                rotation.AuthGeneration,
                rotation.CustodyHead,
                (_, canonical) => Sign(alice.Keys.PrivateB64, canonical));
            Assert.IsFalse(staleA.Authenticated);

            Assert.IsNull(authorityRoster.ResolveDevice(alice.Handle, alice.DeviceId));
            await authorityRoster.RefreshAsync([alice.Handle], CancellationToken.None);
            Assert.AreEqual(2, authoritySource.LookupCount);
            Assert.AreEqual(deviceB, authorityRoster.ResolveDevice(alice.Handle, deviceB)!.DeviceId);
            var aliceB = new Registration(alice.Handle, deviceB, keysB);
            string? nonceB = null;
            (aliceConnection, _) = await ConnectAsync(aliceB, nonce => nonceB = nonce);
            Assert.IsNotNull(nonceB);
            aliceConnection.On<string>(
                MeshHubProtocol.Receive,
                payload => aliceControls.Writer.TryWrite(payload));
            aliceTransport = new HostedTopicTransport(aliceConnection, aliceB, bob);
            bobTransport = new HostedTopicTransport(bobConnection, bob, aliceB);
            requestDelivery = new TopicRequestOutboxDelivery(
                requestHandler, aliceTransport, _clock);

            Assert.IsTrue(_faults.Deactivate("hosted-long-outage-route"));
            _clock.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval + TimeSpan.FromSeconds(1));
            Assert.IsTrue((await requestDelivery.TrySendAsync(
                requester.GetTopicOutbox(request.RunId)!,
                CancellationToken.None)).TransportResult!.Accepted);
            var requestEnvelope = await ReadEnvelopeAsync(bobControls.Reader);
            var attemptsAtRecovery =
                initialAliceTransport.AttemptsFor(request.RunId)
                + aliceTransport.AttemptsFor(request.RunId);
            Assert.AreEqual(2, attemptsAtRecovery);
            Assert.AreEqual(request.RunId, requestEnvelope.Id);
            Assert.IsTrue(TopicRunProtocol.TryParseRequest(requestEnvelope.Body, out var parsedRequest));
            var inbound = executorDurability.AcceptRequest(parsedRequest, aliceB.DeviceId);

            _clock.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval + TimeSpan.FromSeconds(1));
            Assert.IsTrue((await requestDelivery.TrySendAsync(
                requester.GetTopicOutbox(request.RunId)!,
                CancellationToken.None)).TransportResult!.Accepted);
            var replayEnvelope = await ReadEnvelopeAsync(bobControls.Reader);
            Assert.AreEqual(request.RunId, replayEnvelope.Id);
            Assert.IsTrue(TopicRunProtocol.TryParseRequest(replayEnvelope.Body, out var replayRequest));
            var replayInbound = executorDurability.AcceptRequest(replayRequest, aliceB.DeviceId);
            Assert.AreEqual(inbound.RunId, replayInbound.RunId);
            Assert.AreEqual(inbound.AcceptedAt, replayInbound.AcceptedAt);
            Assert.AreEqual(inbound.SourceDeviceId, replayInbound.SourceDeviceId);
            Assert.AreEqual(1, executor.ListInboundTopicRuns().Count);

            var executorDelivery = new TopicControlOutboxDelivery(
                executor, bobTransport, _clock);
            var acceptance = TopicAcceptancePolicy.Create(request, inbound.AcceptedAt);
            var acceptanceId = TopicControlProtocol.EnvelopeId("topic.accepted", request.RunId);
            Assert.IsTrue((await executorDelivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(acceptanceId)!,
                CancellationToken.None))!.Accepted);
            var acceptanceEnvelope = await ReadEnvelopeAsync(aliceControls.Reader);
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(acceptanceEnvelope.Body, out var receivedAcceptance));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                requesterDurability.HandleUpdate(
                    receivedAcceptance, bob.DeviceId, acceptanceEnvelope.Id));
            await SendAndApplyReceiptAsync(
                aliceTransport, bobControls.Reader, executorDurability,
                acceptance, aliceB.DeviceId);

            var terminal = acceptance with
            {
                Phase = TopicRunPhase.Completed,
                Status = "Completed",
                Timestamp = _clock.GetUtcNow()
            };
            _ = executorDurability.CompleteRun(
                request.RunId, InboundTopicRunStates.Completed, terminal, aliceB.DeviceId);
            var terminalId = TopicControlProtocol.EnvelopeId("topic.terminal", request.RunId);
            Assert.IsTrue((await executorDelivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(terminalId)!,
                CancellationToken.None))!.Accepted);
            var terminalEnvelope = await ReadEnvelopeAsync(aliceControls.Reader);
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(terminalEnvelope.Body, out var receivedTerminal));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                requesterDurability.HandleUpdate(
                    receivedTerminal, bob.DeviceId, terminalEnvelope.Id));
            await SendAndApplyReceiptAsync(
                aliceTransport, bobControls.Reader, executorDurability,
                terminal, aliceB.DeviceId);

            var presence = await aliceConnection.InvokeAsync<OnlinePresenceSnapshot>(
                OnlineRelayMethods.ResolvePresence, new[] { bob.Handle });
            Assert.IsTrue(presence.Handles.Single().Online);
            CollectionAssert.Contains(
                presence.Handles.Single().Devices.ToArray(), bob.DeviceId);
            Assert.AreEqual(
                InboundTopicRunStates.Completed,
                executor.GetInboundTopicRun(request.RunId)!.State);
            Assert.IsNull(requester.GetTopicOutbox(request.RunId));
            Assert.AreEqual(0, executor.ListDeviceEnvelopeOutbox().Count);
            Assert.AreEqual(2, requester.ListReceivedTopicControls().Count);
            Console.WriteLine(
                $"REAL_DB_ASSERT LONG_OUTAGE authGeneration={refreshed.AuthGeneration} custodyHead={refreshed.CustodyHead} " +
                $"nonceChallenges={challenges.Count} authorityLookups={authoritySource.LookupCount} " +
                $"rotationVersion={rotation.AuthGeneration} rotationFingerprint={rotation.PublicKeyFingerprint} nonceB={nonceB} " +
                $"attemptsAtRecovery={attemptsAtRecovery} " +
                "requestTransportAttempts=" +
                $"{initialAliceTransport.AttemptsFor(request.RunId) + aliceTransport.AttemptsFor(request.RunId)} receiverRuns=1 " +
                "receiverState=completed requesterControls=2 requesterOutbox=0 receiverOutbox=0 rosterOnline=true");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // -------------------------------------------------------------------------

    private sealed record Registration(string Handle, string DeviceId, KeyPair Keys);
    private sealed record ConnectionAttempt(bool Authenticated, string? Nonce);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "Mesh.App")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void AssertNoManualRecoveryTrigger(string path, string methodName)
    {
        var source = File.ReadAllText(path);
        var methodStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0, $"{methodName} was not found in {path}.");
        var bodyStart = source.IndexOf('{', methodStart);
        Assert.IsTrue(bodyStart >= 0, $"{methodName} has no body.");
        var depth = 0;
        var bodyEnd = -1;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
            {
                bodyEnd = index;
                break;
            }
        }
        Assert.IsTrue(bodyEnd > bodyStart, $"{methodName} body was not terminated.");
        var method = source[bodyStart..(bodyEnd + 1)];
        var forbidden = Regex.Matches(
                method,
                @"\.\s*(Schedule|ResumeTransport|Drain\w*|Wake\w*)\s*\(",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToArray();
        Assert.AreEqual(
            0,
            forbidden.Length,
            $"{methodName} manually invokes recovery: {string.Join(", ", forbidden)}");
    }

    private async Task<ConnectionAttempt> TryConnectWithAuthorityAsync(
        Registration registration,
        long authGeneration,
        string custodyHead,
        Func<string, string, string> signature)
    {
        var url =
            $"{_baseUrl}/hub?handle={Uri.EscapeDataString(registration.Handle)}" +
            $"&deviceId={Uri.EscapeDataString(registration.DeviceId)}" +
            $"&protocolVersion={MeshProtocol.Version}" +
            $"&authGeneration={authGeneration}" +
            $"&custodyHead={Uri.EscapeDataString(custodyHead)}";
        var connection = new HubConnectionBuilder().WithUrl(url).Build();
        var presence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? observedNonce = null;
        connection.On<string>(MeshHubProtocol.Challenge, nonce =>
        {
            observedNonce = nonce;
            var canonical = RelayConnectChallenge.Canonical(
                nonce,
                registration.Handle,
                registration.DeviceId,
                MeshProtocol.Version,
                authGeneration,
                custodyHead);
            return connection.SendAsync(
                MeshHubProtocol.Authenticate,
                registration.Keys.PublicB64,
                signature(nonce, canonical));
        });
        connection.On<PresenceConfirmed>(
            MeshHubProtocol.PresenceConfirmed,
            _ => presence.TrySetResult(true));
        connection.Closed += _ =>
        {
            closed.TrySetResult(true);
            return Task.CompletedTask;
        };
        try
        {
            await connection.StartAsync();
            await Task.WhenAny(
                presence.Task,
                closed.Task,
                Task.Delay(TimeSpan.FromSeconds(3)));
            return new ConnectionAttempt(presence.Task.IsCompletedSuccessfully, observedNonce);
        }
        catch
        {
            return new ConnectionAttempt(false, observedNonce);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private async Task SendAndApplyReceiptAsync(
        HostedTopicTransport transport,
        ChannelReader<string> destination,
        TopicDurabilityHandler destinationHandler,
        TopicRunUpdatePayload control,
        string sourceDevice)
    {
        var receipt = TopicControlProtocol.CreateReceipt(control, _clock.GetUtcNow());
        var receiptId = TopicControlProtocol.EnvelopeId(
            TopicControlProtocol.ControlPurpose(receipt), receipt.RunId);
        var result = await transport.SendAsync(
            transport.Target.DeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(receipt),
            receiptId,
            null,
            CancellationToken.None);
        Assert.IsTrue(result!.Accepted);
        var envelope = await ReadEnvelopeAsync(destination);
        Assert.AreEqual(receiptId, envelope.Id);
        Assert.IsTrue(TopicRunProtocol.TryParseUpdate(envelope.Body, out var parsed));
        Assert.AreEqual(
            TopicControlReceiptPersistenceResult.Applied,
            destinationHandler.HandleReceipt(parsed, sourceDevice));
    }

    private static async Task<MeshEnvelope> ReadEnvelopeAsync(ChannelReader<string> reader)
    {
        var json = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        return JsonSerializer.Deserialize<MeshEnvelope>(
                   json,
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("Relay returned an invalid control envelope.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The hosted replication condition was not reached.");
            await Task.Delay(20);
        }
    }

    private async Task<Registration> RegisterAsync(string handle)
    {
        var keys = KeyPair.New();
        var deviceId = DeviceProtocol.DeviceId(keys.PublicB64);
        await _store.UpsertHandleAsync(handle, keys.PublicB64, "display", allowNewDevice: true);
        await _store.SetDeviceMetadataAsync(handle, deviceId, "device", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
        return new Registration(handle, deviceId, keys);
    }

    private async Task<(HubConnection Conn, TypedSignal<OnlineRelayDelivery> Deliveries)> ConnectAsync(
        Registration reg,
        Action<string>? challengeObserved = null)
    {
        var record = (await _store.GetHandleAsync(reg.Handle))!;
        var url =
            $"{_baseUrl}/hub?handle={Uri.EscapeDataString(reg.Handle)}" +
            $"&deviceId={Uri.EscapeDataString(reg.DeviceId)}" +
            $"&protocolVersion={MeshProtocol.Version}" +
            $"&authGeneration={record.AuthGeneration}" +
            $"&custodyHead={Uri.EscapeDataString(record.CustodyHead)}";

        var conn = new HubConnectionBuilder().WithUrl(url).Build();
        _connections.Add(conn);

        var presence = new TaskCompletionSource<PresenceConfirmed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new TypedSignal<OnlineRelayDelivery>();

        conn.On<string>(MeshHubProtocol.Challenge, nonce =>
        {
            challengeObserved?.Invoke(nonce);
            var canonical = RelayConnectChallenge.Canonical(
                nonce, reg.Handle, reg.DeviceId, MeshProtocol.Version, record.AuthGeneration, record.CustodyHead);
            // Fire-and-forget: replying from inside the receive callback with SendAsync avoids
            // re-entrant Invoke deadlocks. Authenticate returns void; the server replies with
            // PresenceConfirmed which completes the handshake below.
            return conn.SendAsync(MeshHubProtocol.Authenticate, reg.Keys.PublicB64, Sign(reg.Keys.PrivateB64, canonical));
        });
        conn.On<PresenceConfirmed>(MeshHubProtocol.PresenceConfirmed, p => presence.TrySetResult(p));
        conn.On<OnlineRelayDelivery>(OnlineRelayMethods.Deliver, d => deliveries.Set(d));

        await conn.StartAsync();
        await presence.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return (conn, deliveries);
    }

    private static string Sign(string privateKeyB64, string message)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    private sealed class TypedSignal<T>
    {
        private readonly TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(T value) => tcs.TrySetResult(value);
        public bool IsCompleted => tcs.Task.IsCompleted;
        public async Task<T> WaitAsync(TimeSpan timeout) => await tcs.Task.WaitAsync(timeout);
    }

    private sealed class AllowAllRateLimiter : IMessageRateLimiter
    {
        private static readonly HandleRatePolicy Policy = new(1_000_000, 1_000_000, 1_000_000, 1_000_000, 4096);

        public Task<(RateLimitDecision Decision, HandleRatePolicy Policy)> TryAcquireAsync(
            string handle, MessageRateBucket bucket, CancellationToken ct = default)
            => Task.FromResult((new RateLimitDecision(true, 0, 1_000_000), Policy));
    }

    private sealed class HostedTopicTransport(
        HubConnection connection,
        Registration source,
        Registration target) : ITopicEnvelopeTransport
    {
        private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);
        public Registration Target => target;
        public int AttemptsFor(string envelopeId) => attempts.GetValueOrDefault(envelopeId);

        public async Task<MeshSendResult?> SendAsync(
            string targetDeviceId,
            string kind,
            string plaintext,
            string envelopeId,
            string? pushHint,
            CancellationToken cancellationToken)
        {
            attempts.AddOrUpdate(envelopeId, 1, static (_, count) => count + 1);
            var envelope = MeshEnvelope.Create(
                source.Handle,
                target.Handle,
                kind,
                plaintext,
                Sign(source.Keys.PrivateB64, plaintext),
                fromDevice: source.DeviceId,
                toDevice: targetDeviceId,
                pushHint: pushHint,
                id: envelopeId);
            return await connection.InvokeAsync<MeshSendResult>(
                MeshHubProtocol.SendEnvelope, envelope, cancellationToken);
        }
    }

    private sealed class HostedReplicationTransport(HubConnection connection) : IReplicationTransport
    {
        public Task<OnlineRelaySendResult> SendAsync(
            OnlineRelayFrame frame,
            CancellationToken ct)
            => connection.InvokeAsync<OnlineRelaySendResult>(
                OnlineRelayMethods.Relay, frame, ct);

        public Task<OnlineWakeResult> WakeAsync(
            OnlineWakeRequest request,
            CancellationToken ct)
            => connection.InvokeAsync<OnlineWakeResult>(
                OnlineRelayMethods.Wake, request, ct);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class HostedMetadataSource(string baseUrl) : IReplicationMetadataSource
    {
        private readonly HttpClient http = new();
        public int LookupCount { get; private set; }
        public bool Outage { get; set; }
        public TaskCompletionSource<bool>? FetchEntered { get; set; }
        public TaskCompletionSource<bool>? FetchRelease { get; set; }

        public async Task<HandleInfo?> FetchHandleAsync(string handle, CancellationToken ct)
        {
            LookupCount++;
            if (Outage) return null;
            FetchEntered?.TrySetResult(true);
            if (FetchRelease is not null)
                await FetchRelease.Task.WaitAsync(ct);
            return await http.GetFromJsonAsync<HandleInfo>(
                $"{baseUrl}/handles/{Uri.EscapeDataString(handle)}",
                ct);
        }

        public Task<IReadOnlyList<RelayHandlePresence>> ResolvePresenceAsync(
            IReadOnlyList<string> handles,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelayHandlePresence>>([]);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private readonly TaskCompletionSource timerCreated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset now = utcNow;
        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (gate) timers.Add(timer);
            timerCreated.TrySetResult();
            return timer;
        }

        public Task WaitForTimerAsync(TimeSpan timeout)
            => timerCreated.Task.WaitAsync(timeout);

        public void Advance(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (gate)
            {
                now += duration;
                foreach (var timer in timers.ToArray())
                    timer.CollectDue(now, callbacks);
                timers.RemoveAll(timer => timer.Disposed);
            }
            foreach (var callback in callbacks)
                callback.Callback(callback.State);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private DateTimeOffset? dueAt;
            private TimeSpan period;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
                Change(dueTime, period);
            }

            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    this.period = period;
                    dueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : owner.now + dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner.gate)
                {
                    Disposed = true;
                    dueAt = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void CollectDue(
                DateTimeOffset current,
                List<(TimerCallback Callback, object? State)> callbacks)
            {
                if (Disposed || dueAt is null || dueAt > current) return;
                callbacks.Add((callback, state));
                dueAt = period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan
                    ? current + period
                    : null;
            }
        }
    }
}
