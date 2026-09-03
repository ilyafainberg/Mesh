using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Phase 1 runtime-arming tests for the Protocol 9 online-replication engine. These exercise the
/// production wiring that makes a session actually start: the connect-challenge canonical the client
/// signs against the real relay, the relay-backed <see cref="RelayReplicationRoster"/>, the bounded
/// <see cref="ReplicationPresencePoller"/>, the pre-engine route guard, the non-null projection codec,
/// and the engine's typed inbound dispatch. Every type under test lives in a linked source file or a
/// referenced assembly, so no MAUI build is needed.
/// </summary>
[TestClass]
public sealed class OnlineReplicationRuntimeTests
{
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private static readonly byte[] DbKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private string _root = null!;
    private readonly List<OnlineReplicationEngine> _engines = new();
    private readonly List<MeshDb> _dbs = new();
    private readonly List<IDisposable> _disposables = new();

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "repl-runtime", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch { /* best effort */ } }
        foreach (var e in _engines) await e.DisposeAsync();
        foreach (var db in _dbs) db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // =====================================================================
    // Test doubles.
    // =====================================================================

    private sealed class RecordingTransport : IReplicationTransport
    {
        private readonly List<OnlineRelayFrame> sent = new();
        private readonly List<OnlineWakeRequest> wakes = new();
        public Func<OnlineRelayFrame, OnlineRelaySendResult>? SendResult { get; set; }
        public OnlineRelaySendResult Result = new(true, OnlineRelaySendCodes.Delivered);
        public OnlineWakeResult WakeResult = new(true, OnlineWakeCodes.Accepted);

        public Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            lock (sent) sent.Add(frame);
            return Task.FromResult(SendResult?.Invoke(frame) ?? Result);
        }

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
        {
            lock (wakes) wakes.Add(request);
            return Task.FromResult(WakeResult);
        }

        public IReadOnlyList<OnlineRelayFrame> Sent { get { lock (sent) return sent.ToList(); } }
        public IReadOnlyList<OnlineWakeRequest> Wakes { get { lock (wakes) return wakes.ToList(); } }

        public IReadOnlyList<(E2EFrameKind Kind, string ToDevice)> DecodedKinds()
        {
            lock (sent)
                return sent
                    .Select(f => (ReplicationPayloadCodec.DecodeFrame(f.Ciphertext), f.ToDevice ?? ""))
                    .Where(x => x.Item1 is not null)
                    .Select(x => (x.Item1!.Kind, x.Item2))
                    .ToList();
        }
    }

    private sealed class BlockingTransport : IReplicationTransport
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            Entered.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
            => Task.FromResult(new OnlineWakeResult(true, OnlineWakeCodes.Accepted));
    }

    private sealed class FirstSendBlockingTransport : IReplicationTransport
    {
        private readonly List<OnlineRelayFrame> sent = new();
        private int sendCount;

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            lock (sent) sent.Add(frame);
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
            => Task.FromResult(new OnlineWakeResult(true, OnlineWakeCodes.Accepted));

        public IReadOnlyList<OnlineRelayFrame> Sent
        {
            get { lock (sent) return sent.ToList(); }
        }
    }

    private sealed class FirstKindBlockingTransport(E2EFrameKind blockedKind) : IReplicationTransport
    {
        private readonly List<OnlineRelayFrame> sent = new();
        private int blocked;

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            lock (sent) sent.Add(frame);
            var decoded = ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext);
            if (decoded?.Kind == blockedKind
                && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
            => Task.FromResult(new OnlineWakeResult(true, OnlineWakeCodes.Accepted));

        public IReadOnlyList<OnlineRelayFrame> Sent
        {
            get { lock (sent) return sent.ToList(); }
        }
    }

    private sealed class ArmedFirstOfferBlockingTransport : IReplicationTransport
    {
        private readonly List<OnlineRelayFrame> sent = new();
        private int armed;
        private int blocked;

        public TaskCompletionSource<string> EnteredDevice { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref armed, 1);

        public async Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            lock (sent) sent.Add(frame);
            var decoded = ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext);
            if (Volatile.Read(ref armed) == 1
                && decoded?.Kind == E2EFrameKind.Offer
                && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                EnteredDevice.TrySetResult(frame.ToDevice ?? "");
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
            => Task.FromResult(new OnlineWakeResult(true, OnlineWakeCodes.Accepted));

        public IReadOnlyList<(E2EFrameKind Kind, string ToDevice)> DecodedKinds()
        {
            lock (sent)
                return sent
                    .Select(frame => (ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext), frame.ToDevice ?? ""))
                    .Where(item => item.Item1 is not null)
                    .Select(item => (item.Item1!.Kind, item.Item2))
                    .ToList();
        }
    }

    private sealed class FirstTwoKindBlockingTransport(E2EFrameKind blockedKind) : IReplicationTransport
    {
        private readonly List<OnlineRelayFrame> sent = new();
        private int blockedCount;

        public TaskCompletionSource<bool> FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            lock (sent) sent.Add(frame);
            var decoded = ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext);
            if (decoded?.Kind == blockedKind)
            {
                switch (Interlocked.Increment(ref blockedCount))
                {
                    case 1:
                        FirstEntered.TrySetResult(true);
                        await ReleaseFirst.Task.WaitAsync(ct).ConfigureAwait(false);
                        break;
                    case 2:
                        SecondEntered.TrySetResult(true);
                        await ReleaseSecond.Task.WaitAsync(ct).ConfigureAwait(false);
                        break;
                }
            }
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
            => Task.FromResult(new OnlineWakeResult(true, OnlineWakeCodes.Accepted));

        public IReadOnlyList<E2EFrameKind> DecodedKinds()
        {
            lock (sent)
                return sent
                    .Select(frame => ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext))
                    .Where(frame => frame is not null)
                    .Select(frame => frame!.Kind)
                    .ToList();
        }
    }
    private sealed class BlockingMetadataSource(HandleInfo handle) : IReplicationMetadataSource
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task<HandleInfo?> FetchHandleAsync(string requestedHandle, CancellationToken ct)
        {
            Entered.TrySetResult(true);
            try
            {
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            return handle;
        }

        public Task<IReadOnlyList<RelayHandlePresence>> ResolvePresenceAsync(
            IReadOnlyList<string> handles,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelayHandlePresence>>(Array.Empty<RelayHandlePresence>());
    }

    private sealed class FakeMetadataSource : IReplicationMetadataSource
    {
        private readonly object gate = new();
        private readonly Dictionary<string, HandleInfo> handles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (bool Online, List<string> Devices)> presence = new(StringComparer.Ordinal);

        public int FetchCount;
        public int ResolveCount;
        public TaskCompletionSource<bool>? FetchEntered;
        public TaskCompletionSource<bool>? FetchRelease;
        public Exception? FetchError;

        public void SetHandle(string handle, HandleInfo info)
        {
            lock (gate) handles[handle] = info;
        }

        public void RemoveHandle(string handle)
        {
            lock (gate) handles.Remove(handle);
        }

        public void SetPresence(string handle, bool online, params string[] devices)
        {
            lock (gate) presence[handle] = (online, devices.ToList());
        }

        public async Task<HandleInfo?> FetchHandleAsync(string handle, CancellationToken ct)
        {
            Interlocked.Increment(ref FetchCount);
            HandleInfo? result;
            lock (gate) result = handles.TryGetValue(handle, out var i) ? i : null;

            FetchEntered?.TrySetResult(true);
            var release = FetchRelease;
            if (release is not null)
                await release.Task.WaitAsync(ct);
            if (FetchError is not null)
                throw FetchError;

            return result;
        }

        public Task<IReadOnlyList<RelayHandlePresence>> ResolvePresenceAsync(IReadOnlyList<string> hs, CancellationToken ct)
        {
            Interlocked.Increment(ref ResolveCount);
            lock (gate)
            {
                var list = new List<RelayHandlePresence>();
                foreach (var h in hs)
                    list.Add(presence.TryGetValue(h, out var p)
                        ? new RelayHandlePresence(h, p.Online, p.Devices.ToList())
                        : new RelayHandlePresence(h, false, Array.Empty<string>()));
                return Task.FromResult<IReadOnlyList<RelayHandlePresence>>(list);
            }
        }
    }

    private sealed class CapturingApplier : IReplicationDomainApplier
    {
        public int Count;
        public readonly List<string> EntityIds = new();

        public bool Apply(SqliteConnection conn, SqliteTransaction tx, ReplicationEvent evt,
            ReplicationPayloadCodec.DomainEnvelope envelope, bool deviceIsDesktop)
        {
            Interlocked.Increment(ref Count);
            lock (EntityIds) EntityIds.Add(envelope.EntityId);
            return true;
        }
    }

    private sealed class StubRoster : IReplicationRoster
    {
        private readonly List<ReplicationDevice> devices = new();
        public long Gen;

        public void Add(ReplicationDevice d) => devices.Add(d);

        public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string handle)
            => devices.Where(d => string.Equals(d.Handle, handle, StringComparison.Ordinal) && !d.Revoked).ToList();

        public ReplicationDevice? ResolveDevice(string handle, string deviceId)
            => devices.FirstOrDefault(d => string.Equals(d.Handle, handle, StringComparison.Ordinal)
                && string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));

        public long AuthGeneration(string handle) => Gen;

        public Task<ReplicationEmissionRosterSnapshot> GetEmissionSnapshotAsync(
            IReadOnlyCollection<string> accountHandles,
            ReplicationIdentity localIdentity,
            CancellationToken ct)
        {
            var snapshot = accountHandles
                .Append(localIdentity.Handle)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(AuthorizedDevices)
                .ToList();
            var ownOnly = accountHandles.All(handle =>
                string.Equals(handle, localIdentity.Handle, StringComparison.Ordinal));
            var hasSibling = snapshot.Any(device =>
                string.Equals(device.Handle, localIdentity.Handle, StringComparison.Ordinal)
                && !string.Equals(device.DeviceId, localIdentity.DeviceId, StringComparison.Ordinal));
            return Task.FromResult(new ReplicationEmissionRosterSnapshot(
                ownOnly && !hasSibling
                    ? ReplicationEmissionRosterState.NoSiblings
                    : ReplicationEmissionRosterState.FreshComplete,
                snapshot,
                accountHandles.Append(localIdentity.Handle)
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(handle => handle, _ => Gen, StringComparer.Ordinal)));
        }
    }

    private sealed class UnavailableEmissionRoster : IReplicationRoster
        {
            public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string accountHandle)
                => Array.Empty<ReplicationDevice>();

            public ReplicationDevice? ResolveDevice(string accountHandle, string deviceId) => null;

            public long AuthGeneration(string accountHandle) => -1;

            public Task<ReplicationEmissionRosterSnapshot> GetEmissionSnapshotAsync(
                IReadOnlyCollection<string> accountHandles,
                ReplicationIdentity localIdentity,
                CancellationToken ct)
                => Task.FromResult(new ReplicationEmissionRosterSnapshot(
                    ReplicationEmissionRosterState.Unavailable,
                    Array.Empty<ReplicationDevice>(),
                    new Dictionary<string, long>(StringComparer.Ordinal),
                    "test authority unavailable"));
        }

    private sealed class BlockingEmissionRoster(
            IReadOnlyList<ReplicationDevice> devices) : IReplicationRoster
        {
            public TaskCompletionSource<bool> Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string accountHandle)
                => devices.Where(device =>
                    ReplicationHandle.Norm(device.Handle)
                    == ReplicationHandle.Norm(accountHandle)).ToArray();

            public ReplicationDevice? ResolveDevice(string accountHandle, string deviceId)
                => AuthorizedDevices(accountHandle).FirstOrDefault(device =>
                    ReplicationHandle.Device(device.DeviceId)
                    == ReplicationHandle.Device(deviceId));

            public long AuthGeneration(string accountHandle)
                => AuthorizedDevices(accountHandle).Select(device => device.AuthGeneration)
                    .DefaultIfEmpty(-1).Max();

            public async Task<ReplicationEmissionRosterSnapshot> GetEmissionSnapshotAsync(
                IReadOnlyCollection<string> accountHandles,
                ReplicationIdentity localIdentity,
                CancellationToken ct)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(ct);
                var handles = accountHandles.Append(localIdentity.Handle)
                    .Select(ReplicationHandle.Norm)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var selected = devices.Where(device =>
                    handles.Contains(ReplicationHandle.Norm(device.Handle), StringComparer.Ordinal))
                    .ToArray();
                return new ReplicationEmissionRosterSnapshot(
                    ReplicationEmissionRosterState.FreshComplete,
                    selected,
                    handles.ToDictionary(
                        handle => handle,
                        handle => selected.Where(device =>
                                ReplicationHandle.Norm(device.Handle) == handle)
                            .Select(device => device.AuthGeneration)
                            .DefaultIfEmpty(0).Max(),
                        StringComparer.Ordinal),
                    ValidAt: DateTimeOffset.UtcNow);
            }
    }

    private sealed class BlockingResolveRoster : IReplicationRoster, IDisposable
    {
        private readonly List<ReplicationDevice> devices = new();
        private int blockNextResolve;

        public TaskCompletionSource<bool> Entered { get; private set; } = NewSignal();
        public TaskCompletionSource<bool> Release { get; private set; } = NewSignal();

        public void Add(ReplicationDevice device) => devices.Add(device);

        public void BlockNextResolve()
        {
            Entered = NewSignal();
            Release = NewSignal();
            Volatile.Write(ref blockNextResolve, 1);
        }

        public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string handle)
            => devices.Where(device => string.Equals(device.Handle, handle, StringComparison.Ordinal) && !device.Revoked).ToList();

        public ReplicationDevice? ResolveDevice(string handle, string deviceId)
        {
            if (Interlocked.Exchange(ref blockNextResolve, 0) == 1)
            {
                Entered.TrySetResult(true);
                Release.Task.GetAwaiter().GetResult();
            }
            return devices.FirstOrDefault(device => string.Equals(device.Handle, handle, StringComparison.Ordinal)
                && string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
        }

        public long AuthGeneration(string handle) => 0;

        public void Dispose() => Release.TrySetResult(true);

        private static TaskCompletionSource<bool> NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private OnlineReplicationEngine NewEngine(
        string handle, string device, IReplicationRoster roster, IReplicationTransport transport,
        KeyPair keys, IReplicationDomainApplier? applier = null, bool desktop = true,
        TimeSpan? sessionInitRetryInterval = null,
        TimeSpan? receiptRetryInterval = null)
    {
        var db = MeshDb.Open(Path.Combine(_root, device + ".meshdb"), DbKey);
        _dbs.Add(db);
        var identity = new ReplicationIdentity(
            handle, device, keys.PublicB64, keys.PrivateB64, "epoch-1", 0, OnlineReplicationProtocol.ZeroHash);
        var engine = new OnlineReplicationEngine(
            db, identity, transport, roster, applier ?? new CapturingApplier(),
            deviceIsDesktop: desktop, sendTimeout: TimeSpan.FromSeconds(2), maxSendAttempts: 1,
            sessionInitRetryInterval: sessionInitRetryInterval,
            receiptRetryInterval: receiptRetryInterval);
        engine.EnsureLocalOrigin();
        _engines.Add(engine);
        return engine;
    }

    private static HandleInfo Dir(string handle, long authGen, string custody, params string[] pubKeys)
        => new(handle, "display", pubKeys, true, DateTimeOffset.UtcNow, authGen, custody);

    private static ReplicationEvent DummyEvent(string kind = ReplicationOpKinds.Message)
        => new("evt", "conv", "alice", "dev", "epoch-1", 1UL, 0, kind, "m1", "v1", 0, "cipher", "hash", "sig");

    private static ReplicationPayloadCodec.DomainEnvelope Env(
        string kind, ReplicationPayloadCodec.DomainAction action, string entityId = "m1", string body = "{}")
        => new(kind, action, entityId, "conv", "v1", body);

    // =====================================================================
    // Connect canonical parity with the REAL relay hub (production fix).
    // =====================================================================

    [TestMethod]
    public async Task Engine_DisposeWaitsForActivePeerOperationBeforeDisposingItsGate()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new BlockingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        var operation = engine.StartSessionAsync("alice", peerDevice);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = engine.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(dispose.IsCompleted, "disposal must drain the active peer operation");

        transport.Release.TrySetResult(true);
        await Task.WhenAll(operation, dispose).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Engine_DisposeWaitsForProjectionCaptureBeforeCompleting()
    {
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = engine.WithProjectionBoundaryAsync(
            async _ =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return true;
            },
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = engine.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(dispose.IsCompleted, "disposal must drain snapshot capture");

        release.TrySetResult(true);
        await Task.WhenAll(capture, dispose).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Engine_DisposeWaitsForActiveDurableEmissionBeforeDisposingRepairGate()
    {
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        var emission = Task.Run(() => engine.EmitLocalDurableAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "dispose-emission"),
            new[] { "alice" },
            domainWork: (_, _, _) =>
            {
                entered.TrySetResult(true);
                release.Wait();
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = engine.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(dispose.IsCompleted, "disposal must drain the durable journal transaction");

        release.Set();
        await Task.WhenAll(emission, dispose).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Poller_DisposeAsyncCancelsAndDrainsBlockedRosterFetch()
    {
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var source = new BlockingMetadataSource(Dir("alice", 0, "", mine.PublicB64));
        var roster = new RelayReplicationRoster(
            source,
            "alice",
            ownAuthGeneration: 0,
            ownCustodyHead: "",
            localCustodyHead: _ => "",
            surface: _ => { },
            onOwnAuthorityChanged: () => { });
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var poller = new ReplicationPresencePoller(
            engine,
            roster,
            source,
            candidateHandles: () => new[] { "alice" },
            hasDueOutbox: _ => false,
            ownHandle: "alice",
            ownDevice: myDevice,
            surface: _ => { });

        poller.Start();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = poller.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(source.CancellationObserved, "disposal must cancel the active roster HTTP await");
    }

    [TestMethod]
    public void Canonical_Matches_Relay_ForGenesisAuthority()
    {
        var expected = RelayConnectChallenge.Canonical("nonce-abc", "alice", "dev-1", MeshProtocol.Version, 0, "");
        var actual = ReplicationConnectChallenge.Canonical("nonce-abc", "alice", "dev-1", MeshProtocol.Version, 0, "");
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Canonical_Matches_Relay_ForPopulatedAuthority()
    {
        var expected = RelayConnectChallenge.Canonical("n2", "bob", "dev-xyz", MeshProtocol.Version, 7, "abcdef0123");
        var actual = ReplicationConnectChallenge.Canonical("n2", "bob", "dev-xyz", MeshProtocol.Version, 7, "abcdef0123");
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Canonical_Matches_Relay_WithDelimiterCharsInFields()
    {
        // Length-prefixing must make delimiter-looking content unambiguous.
        var expected = RelayConnectChallenge.Canonical("a|3:x", "h|1:y", "d", MeshProtocol.Version, 12, "c|9:z");
        var actual = ReplicationConnectChallenge.Canonical("a|3:x", "h|1:y", "d", MeshProtocol.Version, 12, "c|9:z");
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Canonical_IsSensitive_ToAuthGeneration()
    {
        var g0 = ReplicationConnectChallenge.Canonical("n", "alice", "d", MeshProtocol.Version, 0, "");
        var g1 = ReplicationConnectChallenge.Canonical("n", "alice", "d", MeshProtocol.Version, 1, "");
        Assert.AreNotEqual(g0, g1);
    }

    // =====================================================================
    // RelayReplicationRoster.
    // =====================================================================

    private RelayReplicationRoster NewRoster(
        FakeMetadataSource source, string ownHandle, List<string> surfaced, Action? onAuthorityChanged = null,
        Func<string, string?>? localCustody = null, long ownAuthGen = 0, string ownCustody = "",
        TimeProvider? timeProvider = null)
        => new(source, ownHandle, ownAuthGen, ownCustody,
            localCustody ?? (_ => ""), s => { lock (surfaced) surfaced.Add(s); },
            onAuthorityChanged ?? (() => { }), timeProvider);

    [TestMethod]
    public async Task Emission_ExpiredRosterWaitsForSingleFlightRefreshBeforeAllocatingEvent()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64, sibling.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);
        await roster.RefreshAsync(new[] { "alice" }, default);
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var db = _dbs[^1];

        clock.Advance(TimeSpan.FromSeconds(31));
        source.FetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var emission = engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "result"),
            new[] { "alice" },
            domainWork: static (_, _, _) => { });

        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(emission.IsCompleted, "emission must await the in-flight authoritative refresh");
        Assert.AreEqual(0, db.QueryEvents(myDevice, "epoch-1", 1, 64).Count);
        Assert.AreEqual(0, db.GetPendingReplicationIntents().Count);

        source.FetchRelease.TrySetResult(true);
        var eventId = await emission.WaitAsync(TimeSpan.FromSeconds(2));
        var evt = db.GetEvent(eventId)!;
        CollectionAssert.AreEquivalent(
            new[] { myDevice, siblingDevice },
            ReplicationPayloadCodec.RecipientDeviceIds(evt.Ciphertext).ToArray());
        Assert.AreEqual(MeshDb.OutboxStatePending, db.GetOutboxState(eventId, "alice"));
    }

    [TestMethod]
    public async Task Emission_UnavailableRosterPersistsEncryptedIntentAndRetriesExactlyOnce()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);
        await roster.RefreshAsync(new[] { "alice" }, default);
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var db = _dbs[^1];

        clock.Advance(TimeSpan.FromSeconds(31));
        source.RemoveHandle("alice");
        var intentId = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "offline-result"),
            new[] { "alice" },
            domainWork: static (_, _, _) => { });

        Assert.IsNull(db.GetEvent(intentId));
        Assert.AreEqual(0, db.QueryEvents(myDevice, "epoch-1", 1, 64).Count);
        var pending = db.GetPendingReplicationIntents().Single();
        Assert.AreEqual(intentId, pending.IntentId);
        Assert.IsFalse(
            pending.EncryptedEnvelope.Contains("offline-result", StringComparison.Ordinal),
            "pending intent must not duplicate plaintext");

        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var failedFetches = source.FetchCount;
        Assert.AreEqual(
            0,
            await engine.RetryPendingIntentsAsync(),
            "the cached directory failure must fail closed during its bounded cooldown");
        Assert.AreEqual(
            failedFetches,
            source.FetchCount,
            "a near retry must not create a directory fetch storm");
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, await engine.RetryPendingIntentsAsync());
        Assert.AreEqual(0, await engine.RetryPendingIntentsAsync());
        Assert.AreEqual(0, db.GetPendingReplicationIntents().Count);
        var repaired = db.QueryEvents(myDevice, "epoch-1", 1, 64).Single();
        Assert.IsNull(db.GetOutboxState(repaired.EventId, "alice"));
    }

    [TestMethod]
    public async Task Emission_AuthoritativeNoSiblingsAllowsLocalOnlyEvent()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var db = _dbs[^1];

        var eventId = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "local-only"),
            new[] { "alice" },
            domainWork: static (_, _, _) => { });

        Assert.IsNotNull(db.GetEvent(eventId));
        Assert.IsNull(db.GetOutboxState(eventId, "alice"));
        CollectionAssert.AreEqual(
            new[] { myDevice },
            ReplicationPayloadCodec.RecipientDeviceIds(db.GetEvent(eventId)!.Ciphertext).ToArray());
    }

    [TestMethod]
    public async Task Emission_CaseVariantSelfDeviceIsNotASibling()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        var roster = NewRoster(source, "ALICE", new List<string>());
        var identity = new ReplicationIdentity(
            "@Alice",
            myDevice.ToUpperInvariant(),
            mine.PublicB64,
            mine.PrivateB64,
            "epoch-case",
            0,
            OnlineReplicationProtocol.ZeroHash);

        var snapshot = await roster.GetEmissionSnapshotAsync(
            new[] { "@ALICE" }, identity, default);

        Assert.AreEqual(ReplicationEmissionRosterState.NoSiblings, snapshot.State);
        Assert.IsNotNull(snapshot.ValidAt);
    }

    [TestMethod]
    public async Task Roster_TwentyFailedCallersShareFailureCooldownAndInvalidationRefresh()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var identity = new ReplicationIdentity(
            "alice",
            DeviceProtocol.DeviceId(mine.PublicB64),
            mine.PublicB64,
            mine.PrivateB64,
            "epoch-failure",
            0,
            OnlineReplicationProtocol.ZeroHash);
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);

        var failures = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            roster.GetEmissionSnapshotAsync(new[] { "alice" }, identity, default)));

        Assert.AreEqual(1, source.FetchCount);
        Assert.IsTrue(failures.All(result =>
            result.State == ReplicationEmissionRosterState.Unavailable));
        _ = await roster.GetEmissionSnapshotAsync(new[] { "alice" }, identity, default);
        Assert.AreEqual(1, source.FetchCount, "failure must remain cached during cooldown");

        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        roster.Invalidate("ALICE");
        var recovered = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            roster.GetEmissionSnapshotAsync(new[] { "alice" }, identity, default)));

        Assert.AreEqual(2, source.FetchCount);
        Assert.IsTrue(recovered.All(result =>
            result.State == ReplicationEmissionRosterState.NoSiblings));
    }

    [TestMethod]
    public async Task Roster_RefreshFailureIsDistinctAndCanceledWaiterDoesNotCancelSharedFetch()
    {
        var source = new FakeMetadataSource
        {
            FetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            FetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            FetchError = new InvalidOperationException("secret-upstream-detail"),
        };
        var mine = KeyPair.New();
        var identity = new ReplicationIdentity(
            "alice",
            DeviceProtocol.DeviceId(mine.PublicB64),
            mine.PublicB64,
            mine.PrivateB64,
            "epoch-refresh-failure",
            0,
            OnlineReplicationProtocol.ZeroHash);
        var roster = NewRoster(source, "alice", new List<string>());
        using var canceled = new CancellationTokenSource();

        var canceledWaiter = roster.GetEmissionSnapshotAsync(
            new[] { "alice" }, identity, canceled.Token);
        var remaining = Enumerable.Range(0, 19)
            .Select(_ => roster.GetEmissionSnapshotAsync(
                new[] { "alice" }, identity, CancellationToken.None))
            .ToArray();
        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await canceledWaiter);
        source.FetchRelease.TrySetResult(true);

        var results = await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, source.FetchCount);
        Assert.IsTrue(results.All(result =>
            result.State == ReplicationEmissionRosterState.RefreshFailed));
        Assert.IsTrue(results.All(result =>
            string.Equals(result.Reason, "directory refresh failed", StringComparison.Ordinal)));
        Assert.IsTrue(results.All(result =>
            !(result.Reason ?? "").Contains("secret-upstream-detail", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Roster_InvalidateDuringFetchReturnsStaleAndDoesNotPublishResult()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        source.FetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new ReplicationIdentity(
            "alice",
            DeviceProtocol.DeviceId(mine.PublicB64),
            mine.PublicB64,
            mine.PrivateB64,
            "epoch-stale",
            0,
            OnlineReplicationProtocol.ZeroHash);
        var roster = NewRoster(source, "alice", new List<string>());

        var resolving = roster.GetEmissionSnapshotAsync(
            new[] { "alice" }, identity, default);
        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        roster.Invalidate("alice");
        source.FetchRelease.TrySetResult(true);
        var snapshot = await resolving;

        Assert.AreEqual(ReplicationEmissionRosterState.Stale, snapshot.State);
        Assert.AreEqual(0, roster.AuthorizedDevices("alice").Count);
    }

    [TestMethod]
    public async Task Roster_MinimumObservedGenerationForcesFreshFetchAndReturnsStale()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        var identity = new ReplicationIdentity(
            "alice",
            DeviceProtocol.DeviceId(mine.PublicB64),
            mine.PublicB64,
            mine.PrivateB64,
            "epoch-minimum",
            0,
            OnlineReplicationProtocol.ZeroHash);
        var roster = NewRoster(source, "alice", new List<string>());
        _ = await roster.GetEmissionSnapshotAsync(new[] { "alice" }, identity, default);

        var snapshot = await roster.GetEmissionSnapshotAsync(
            new[] { "alice" },
            identity,
            new Dictionary<string, long>(StringComparer.Ordinal) { ["@ALICE"] = 1 },
            default);

        Assert.AreEqual(2, source.FetchCount, "the fresh-but-older cache must be bypassed");
        Assert.AreEqual(ReplicationEmissionRosterState.Stale, snapshot.State);
    }

    [TestMethod]
    public void Crypto_DecryptOutcomeDistinguishesMissingAuthenticationAndMalformed()
    {
        var mine = KeyPair.New();
        var other = KeyPair.New();
        var ciphertext = ReplicationPayloadCodec.Encrypt("secret", new[] { mine.PublicB64 });
        Assert.AreEqual(
            MessageDecryptOutcome.Success,
            ReplicationPayloadCodec.TryDecrypt(
                ciphertext, mine.PrivateB64, mine.PublicB64).Outcome);
        Assert.AreEqual(
            MessageDecryptOutcome.MissingRecipientSlot,
            ReplicationPayloadCodec.TryDecrypt(
                ciphertext, other.PrivateB64, other.PublicB64).Outcome);

        var payload = JsonNode.Parse(ciphertext)!.AsObject();
        var body = Convert.FromBase64String(payload["ct"]!.GetValue<string>());
        body[0] ^= 1;
        payload["ct"] = Convert.ToBase64String(body);
        Assert.AreEqual(
            MessageDecryptOutcome.AuthenticationFailed,
            ReplicationPayloadCodec.TryDecrypt(
                payload.ToJsonString(), mine.PrivateB64, mine.PublicB64).Outcome);
        Assert.AreEqual(
            MessageDecryptOutcome.Malformed,
            ReplicationPayloadCodec.TryDecrypt(
                "{\"alg\":\"ECIES-P256-AESGCM\"}",
                mine.PrivateB64,
                mine.PublicB64).Outcome);
    }

    [TestMethod]
    public async Task Emission_TwentyConcurrentCallsShareOneExpiredRosterRefresh()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64, sibling.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);
        await roster.RefreshAsync(new[] { "alice" }, default);
        var baselineFetches = source.FetchCount;
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var db = _dbs[^1];

        clock.Advance(TimeSpan.FromSeconds(31));
        var emissions = Enumerable.Range(0, 20)
            .Select(index => engine.EmitLocalAsync(
                Env(
                    ReplicationOpKinds.Message,
                    ReplicationPayloadCodec.DomainAction.Upsert,
                    "concurrent-" + index),
                new[] { "alice" },
                domainWork: static (_, _, _) => { }))
            .ToArray();
        var eventIds = await Task.WhenAll(emissions);

        Assert.AreEqual(baselineFetches + 1, source.FetchCount);
        Assert.AreEqual(20, eventIds.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(20, db.QueryEvents(myDevice, "epoch-1", 1, 64).Count);
        Assert.IsTrue(db.QueryEvents(myDevice, "epoch-1", 1, 64).All(evt =>
            ReplicationPayloadCodec.RecipientDeviceIds(evt.Ciphertext).Count == 2));
        Assert.IsTrue(eventIds.All(eventId =>
            db.GetOutboxState(eventId, "alice") == MeshDb.OutboxStatePending));
    }

    [TestMethod]
    public async Task Emission_PendingIntentRepairsOnceOnEngineRestart()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);
        await roster.RefreshAsync(new[] { "alice" }, default);
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var db = _dbs[^1];

        clock.Advance(TimeSpan.FromSeconds(31));
        source.RemoveHandle("alice");
        _ = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "restart-result"),
            new[] { "alice" },
            domainWork: static (_, _, _) => { });
        Assert.AreEqual(1, db.GetPendingReplicationIntents().Count);

        await engine.DisposeAsync();
        _engines.Remove(engine);
        db.Dispose();
        _dbs.Remove(db);

        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var reopened = MeshDb.Open(Path.Combine(_root, myDevice + ".meshdb"), DbKey);
        _dbs.Add(reopened);
        var identity = new ReplicationIdentity(
            "alice", myDevice, mine.PublicB64, mine.PrivateB64,
            "epoch-1", 0, OnlineReplicationProtocol.ZeroHash);
        var restarted = new OnlineReplicationEngine(
            reopened,
            identity,
            new RecordingTransport(),
            roster,
            new CapturingApplier(),
            sendTimeout: TimeSpan.FromSeconds(2),
            maxSendAttempts: 1);
        _engines.Add(restarted);
        restarted.EnsureLocalOrigin();

        await WaitUntilAsync(
            () => reopened.QueryEvents(myDevice, "epoch-1", 1, 64).Count == 1,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, reopened.GetPendingReplicationIntents().Count);
        Assert.AreEqual(1, reopened.QueryEvents(myDevice, "epoch-1", 1, 64).Count);
    }

    [TestMethod]
    public async Task Emission_PendingIntentSurvivesLocalAndRemoteKeyRotation()
    {
        var oldKeys = KeyPair.New();
        var oldDevice = DeviceProtocol.DeviceId(oldKeys.PublicB64);
        var first = NewEngine(
            "alice",
            oldDevice,
            new UnavailableEmissionRoster(),
            new RecordingTransport(),
            oldKeys);
        var db = _dbs[^1];
        _ = await first.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Message,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "rotation-safe-intent"),
            new[] { "bob" },
            domainWork: static (_, _, _) => { });
        var pending = db.GetPendingReplicationIntents().Single();
        Assert.IsTrue(
            pending.EncryptedEnvelope.StartsWith("local-v1:", StringComparison.Ordinal));
        Assert.IsFalse(
            pending.EncryptedEnvelope.Contains("rotation-safe-intent", StringComparison.Ordinal));

        await first.DisposeAsync();
        _engines.Remove(first);
        db.Dispose();
        _dbs.Remove(db);
        SqliteConnection.ClearAllPools();
        DowngradePendingIntentSchemaToLegacy(
            Path.Combine(_root, oldDevice + ".meshdb"),
            DbKey,
            "rotation-safe-intent");

        var newKeys = KeyPair.New();
        var remoteKeys = KeyPair.New();
        var newDevice = DeviceProtocol.DeviceId(newKeys.PublicB64);
        var remoteDevice = DeviceProtocol.DeviceId(remoteKeys.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", newDevice, newKeys.PublicB64, 1, false));
        roster.Add(new ReplicationDevice("bob", remoteDevice, remoteKeys.PublicB64, 1, false));
        var reopened = MeshDb.Open(Path.Combine(_root, oldDevice + ".meshdb"), DbKey);
        _dbs.Add(reopened);
        var restarted = new OnlineReplicationEngine(
            reopened,
            new ReplicationIdentity(
                "alice",
                newDevice,
                newKeys.PublicB64,
                newKeys.PrivateB64,
                "epoch-rotated",
                1,
                OnlineReplicationProtocol.ZeroHash),
            new RecordingTransport(),
            roster,
            new CapturingApplier(),
            sendTimeout: TimeSpan.FromSeconds(2),
            maxSendAttempts: 1);
        _engines.Add(restarted);

        await WaitUntilAsync(
            () => reopened.QueryEvents(newDevice, "epoch-rotated", 1, 64).Count == 1,
            TimeSpan.FromSeconds(2));
        var repaired = reopened.QueryEvents(newDevice, "epoch-rotated", 1, 64).Single();
        var decrypt = ReplicationPayloadCodec.TryDecrypt(
            repaired.Ciphertext,
            remoteKeys.PrivateB64,
            remoteKeys.PublicB64);
        Assert.AreEqual(MessageDecryptOutcome.Success, decrypt.Outcome);
        Assert.AreEqual(
            "rotation-safe-intent",
            ReplicationPayloadCodec.DecodeEnvelope(decrypt.Plaintext!)!.EntityId);
        Assert.AreEqual(0, reopened.GetPendingReplicationIntents().Count);
    }

    private static void DowngradePendingIntentSchemaToLegacy(
        string databasePath,
        byte[] databaseKey,
        string plaintextMarker)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var key = connection.CreateCommand())
        {
            key.CommandText =
                $"PRAGMA key = \"x'{Convert.ToHexString(databaseKey)}'\";";
            key.ExecuteNonQuery();
        }
        using (var downgrade = connection.CreateCommand())
        {
            downgrade.CommandText = """
                DROP INDEX IF EXISTS ix_replication_pending_intents_sequence;
                ALTER TABLE replication_pending_intents DROP COLUMN durable_sequence;
                """;
            downgrade.ExecuteNonQuery();
        }
        using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            SELECT encrypted_envelope,
                   EXISTS(
                       SELECT 1 FROM pragma_table_info('replication_pending_intents')
                       WHERE name = 'durable_sequence')
            FROM replication_pending_intents;
            """;
        using var reader = inspect.ExecuteReader();
        Assert.IsTrue(reader.Read());
        var protectedEnvelope = reader.GetString(0);
        Assert.IsTrue(protectedEnvelope.StartsWith("local-v1:", StringComparison.Ordinal));
        Assert.IsFalse(protectedEnvelope.Contains(plaintextMarker, StringComparison.Ordinal));
        Assert.AreEqual(0L, reader.GetInt64(1));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public async Task Emission_StartupBarrierPreservesThreePendingThenConcurrentNewOrder()
    {
        var mine = KeyPair.New();
        var remote = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var remoteDevice = DeviceProtocol.DeviceId(remote.PublicB64);
        var first = NewEngine(
            "alice",
            myDevice,
            new UnavailableEmissionRoster(),
            new RecordingTransport(),
            mine);
        var db = _dbs[^1];
        for (var index = 1; index <= 3; index++)
        {
            _ = await first.EmitLocalAsync(
                Env(
                    ReplicationOpKinds.Message,
                    ReplicationPayloadCodec.DomainAction.Upsert,
                    "ordered-" + index),
                new[] { "bob" },
                domainWork: static (_, _, _) => { });
        }
        Assert.AreEqual(3, db.GetPendingReplicationIntents().Count);
        await first.DisposeAsync();
        _engines.Remove(first);
        db.Dispose();
        _dbs.Remove(db);

        var blocking = new BlockingEmissionRoster(new[]
        {
            new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false),
            new ReplicationDevice("bob", remoteDevice, remote.PublicB64, 0, false),
        });
        var reopened = MeshDb.Open(Path.Combine(_root, myDevice + ".meshdb"), DbKey);
        _dbs.Add(reopened);
        var restarted = new OnlineReplicationEngine(
            reopened,
            new ReplicationIdentity(
                "alice",
                myDevice,
                mine.PublicB64,
                mine.PrivateB64,
                "epoch-1",
                0,
                OnlineReplicationProtocol.ZeroHash),
            new RecordingTransport(),
            blocking,
            new CapturingApplier(),
            sendTimeout: TimeSpan.FromSeconds(2),
            maxSendAttempts: 1);
        _engines.Add(restarted);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var fourth = restarted.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Message,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "ordered-4"),
            new[] { "bob" },
            domainWork: static (_, _, _) => { });
        Assert.IsFalse(fourth.IsCompleted, "new emission must await startup repair");
        blocking.Release.TrySetResult(true);
        _ = await fourth.WaitAsync(TimeSpan.FromSeconds(2));

        var events = reopened.QueryEvents(myDevice, "epoch-1", 1, 64);
        CollectionAssert.AreEqual(
            new[] { "ordered-1", "ordered-2", "ordered-3", "ordered-4" },
            events.Select(evt =>
            {
                var decrypt = ReplicationPayloadCodec.TryDecrypt(
                    evt.Ciphertext, mine.PrivateB64, mine.PublicB64);
                Assert.AreEqual(MessageDecryptOutcome.Success, decrypt.Outcome);
                return ReplicationPayloadCodec.DecodeEnvelope(decrypt.Plaintext!)!.EntityId;
            }).ToArray());
        Assert.AreEqual(0, reopened.GetPendingReplicationIntents().Count);
        Assert.IsTrue(events.All(evt =>
            reopened.GetOutboxState(evt.EventId, "bob") == MeshDb.OutboxStatePending));
    }

    [TestMethod]
    public async Task Emission_UnavailableBatchQueuesAtomicallyAheadOfLaterSingleAcrossRestart()
    {
        var mine = KeyPair.New();
        var remote = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var remoteDevice = DeviceProtocol.DeviceId(remote.PublicB64);
        var first = NewEngine(
            "alice",
            myDevice,
            new UnavailableEmissionRoster(),
            new RecordingTransport(),
            mine);
        var db = _dbs[^1];
        var batch = new[]
        {
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "batch-1"),
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "batch-2"),
        };

        var pendingBatchIds = await first.EmitLocalBatchAsync(
            batch, new[] { "bob" }, domainWork: static (_, _, _) => { });
        _ = await first.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "after-batch"),
            new[] { "bob" },
            domainWork: static (_, _, _) => { });
        Assert.AreEqual(2, pendingBatchIds.Count);
        Assert.AreEqual(2, db.GetPendingReplicationIntents().Count);
        Assert.AreEqual(0, db.QueryEvents(myDevice, "epoch-1", 1, 10).Count);

        await first.DisposeAsync();
        _engines.Remove(first);
        db.Dispose();
        _dbs.Remove(db);
        SqliteConnection.ClearAllPools();

        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", remoteDevice, remote.PublicB64, 0, false));
        var reopened = MeshDb.Open(Path.Combine(_root, myDevice + ".meshdb"), DbKey);
        _dbs.Add(reopened);
        var restarted = new OnlineReplicationEngine(
            reopened,
            new ReplicationIdentity(
                "alice", myDevice, mine.PublicB64, mine.PrivateB64,
                "epoch-1", 0, OnlineReplicationProtocol.ZeroHash),
            new RecordingTransport(),
            roster,
            new CapturingApplier(),
            sendTimeout: TimeSpan.FromSeconds(2),
            maxSendAttempts: 1);
        _engines.Add(restarted);

        await WaitUntilAsync(
            () => reopened.QueryEvents(myDevice, "epoch-1", 1, 10).Count == 3,
            TimeSpan.FromSeconds(2));
        var entities = reopened.QueryEvents(myDevice, "epoch-1", 1, 10)
            .Select(evt =>
            {
                var decrypted = ReplicationPayloadCodec.TryDecrypt(
                    evt.Ciphertext, mine.PrivateB64, mine.PublicB64);
                Assert.AreEqual(MessageDecryptOutcome.Success, decrypted.Outcome);
                return ReplicationPayloadCodec.DecodeEnvelope(decrypted.Plaintext!)!.EntityId;
            })
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "batch-1", "batch-2", "after-batch" },
            entities);
        Assert.AreEqual(0, reopened.GetPendingReplicationIntents().Count);
    }

    [TestMethod]
    public async Task Emission_GenerationChangeFailsClosedUntilRearmedWithoutRevokedSlot()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var revokedSibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir(
            "alice", 0, OnlineReplicationProtocol.ZeroHash, mine.PublicB64, revokedSibling.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);
        await roster.RefreshAsync(new[] { "alice" }, default);
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var db = _dbs[^1];

        clock.Advance(TimeSpan.FromSeconds(31));
        source.SetHandle("alice", Dir("alice", 1, "head-1", mine.PublicB64));
        var intentId = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "post-revoke"),
            new[] { "alice" },
            domainWork: static (_, _, _) => { });
        Assert.IsNull(db.GetEvent(intentId));
        Assert.AreEqual(
            ReplicationEmissionRosterState.GenerationChanged.ToString(),
            db.GetPendingReplicationIntents().Single().RosterState);

        await engine.DisposeAsync();
        _engines.Remove(engine);
        var freshIdentity = new ReplicationIdentity(
            "alice", myDevice, mine.PublicB64, mine.PrivateB64, "epoch-1", 1, "head-1");
        var rearmed = new OnlineReplicationEngine(
            db,
            freshIdentity,
            new RecordingTransport(),
            roster,
            new CapturingApplier(),
            sendTimeout: TimeSpan.FromSeconds(2),
            maxSendAttempts: 1);
        _engines.Add(rearmed);
        await WaitUntilAsync(
            () => db.QueryEvents(myDevice, "epoch-1", 1, 64).Count == 1,
            TimeSpan.FromSeconds(2));

        var repaired = db.QueryEvents(myDevice, "epoch-1", 1, 64).Single();
        CollectionAssert.AreEqual(
            new[] { myDevice },
            ReplicationPayloadCodec.RecipientDeviceIds(repaired.Ciphertext).ToArray());
        Assert.IsNull(db.GetOutboxState(repaired.EventId, "alice"));
    }

    [TestMethod]
    public async Task Roster_AuthorizedDevices_DerivedFromDirectoryKeys()
    {
        var source = new FakeMetadataSource();
        var k1 = KeyPair.New();
        var k2 = KeyPair.New();
        source.SetHandle("alice", Dir("alice", 0, "", k1.PublicB64, k2.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>());

        await roster.RefreshAsync(new[] { "alice" }, default);

        var devices = roster.AuthorizedDevices("alice");
        Assert.AreEqual(2, devices.Count);
        CollectionAssert.AreEquivalent(
            new[] { DeviceProtocol.DeviceId(k1.PublicB64), DeviceProtocol.DeviceId(k2.PublicB64) },
            devices.Select(d => d.DeviceId).ToArray());
        Assert.IsTrue(devices.All(d => !d.Revoked));
    }

    [TestMethod]
    public async Task Roster_ResolveDevice_KnownAndUnknown()
    {
        var source = new FakeMetadataSource();
        var k1 = KeyPair.New();
        source.SetHandle("alice", Dir("alice", 3, "", k1.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), ownAuthGen: 3);
        await roster.RefreshAsync(new[] { "alice" }, default);

        var dev = roster.ResolveDevice("alice", DeviceProtocol.DeviceId(k1.PublicB64));
        Assert.IsNotNull(dev);
        Assert.AreEqual(k1.PublicB64, dev!.PublicKeyB64);
        Assert.AreEqual(3, dev.AuthGeneration);
        Assert.IsNull(roster.ResolveDevice("alice", "no-such-device"));
    }

    [TestMethod]
    public async Task Roster_AuthGeneration_CachedValueAndMinusOneForUnknown()
    {
        var source = new FakeMetadataSource();
        var k1 = KeyPair.New();
        source.SetHandle("alice", Dir("alice", 5, "", k1.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), ownAuthGen: 5);

        // Unknown before any fetch: -1 so the engine's stale-generation guard never false-rejects.
        Assert.AreEqual(-1, roster.AuthGeneration("bob"));
        await roster.RefreshAsync(new[] { "alice" }, default);
        Assert.AreEqual(5, roster.AuthGeneration("alice"));
    }

    [TestMethod]
    public async Task Roster_Refresh_CachesWithinLifetime()
    {
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 0, "", KeyPair.New().PublicB64));
        var roster = NewRoster(source, "alice", new List<string>());

        await roster.RefreshAsync(new[] { "alice" }, default);
        await roster.RefreshAsync(new[] { "alice" }, default);

        Assert.AreEqual(1, source.FetchCount, "second refresh within the cache lifetime must not re-fetch");
    }

    [TestMethod]
    public async Task Roster_Refresh_UsesInjectedTimeProviderForThirtySecondExpiry()
    {
        var source = new FakeMetadataSource();
        var first = KeyPair.New();
        var second = KeyPair.New();
        source.SetHandle("alice", Dir("alice", 0, "", first.PublicB64));
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-22T18:00:00Z"));
        var roster = NewRoster(source, "alice", new List<string>(), timeProvider: clock);

        await roster.RefreshAsync(new[] { "alice" }, default);
        source.SetHandle("alice", Dir("alice", 0, "", first.PublicB64, second.PublicB64));
        clock.Advance(TimeSpan.FromSeconds(29));
        await roster.RefreshAsync(new[] { "alice" }, default);
        Assert.AreEqual(1, source.FetchCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        await roster.RefreshAsync(new[] { "alice" }, default);

        Assert.AreEqual(2, source.FetchCount);
        Assert.IsNotNull(roster.ResolveDevice("alice", DeviceProtocol.DeviceId(second.PublicB64)));
    }

    [TestMethod]
    public async Task RouteIsolation_RosterExpiryFreshAuthorityNonceAndRestoreAreDeterministic()
    {
        var sourceKey = KeyPair.New();
        var targetKey = KeyPair.New();
        var sourceDevice = DeviceProtocol.DeviceId(sourceKey.PublicB64);
        var targetDevice = DeviceProtocol.DeviceId(targetKey.PublicB64);
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 0, "", sourceKey.PublicB64, targetKey.PublicB64));
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-22T18:00:00Z"));
        var authorityChanged = false;
        var roster = NewRoster(
            source,
            "alice",
            new List<string>(),
            onAuthorityChanged: () => authorityChanged = true,
            ownAuthGen: 0,
            timeProvider: clock);
        await roster.RefreshAsync(new[] { "alice" }, default);

        var faults = new LiveFaultStore(
            new LiveFaultOptions { Enabled = true, MaxTtlSeconds = 300, MaxUses = 100 },
            clock);
        faults.Activate(new LiveFaultActivationRequest(
            "route-isolation",
            LiveFaultMode.DropBeforeForwarding,
            LiveFaultDirection.Outbound,
            "alice",
            targetDevice,
            120,
            MaxUses: 100,
            SourceDevice: sourceDevice,
            TargetAccount: "alice",
            Kind: LiveFaultStore.OnlineFrameKind));
        Assert.AreEqual(
            LiveFaultMode.DropBeforeForwarding,
            faults.TryApply(
                LiveFaultDirection.Outbound, "alice", sourceDevice, "alice", targetDevice,
                LiveFaultStore.OnlineFrameKind, "outage-frame")!.Mode);

        source.SetHandle("alice", Dir("alice", 1, "head-1", sourceKey.PublicB64, targetKey.PublicB64));
        clock.Advance(TimeSpan.FromSeconds(31));
        await roster.RefreshAsync(new[] { "alice" }, default);

        Assert.AreEqual(2, source.FetchCount);
        Assert.AreEqual(1, roster.AuthGeneration("alice"));
        Assert.IsTrue(authorityChanged);
        var nonce = MeshCrypto.NewNonce();
        var canonical = ReplicationConnectChallenge.Canonical(
            nonce, "alice", targetDevice, MeshProtocol.Version, 1, "head-1");
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(Convert.FromBase64String(targetKey.PrivateB64), out _);
        var signature = Convert.ToBase64String(
            signer.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        Assert.IsTrue(MeshCrypto.Verify(targetKey.PublicB64, canonical, signature));

        Assert.IsTrue(faults.Deactivate("route-isolation"));
        Assert.IsNull(faults.TryApply(
            LiveFaultDirection.Outbound, "alice", sourceDevice, "alice", targetDevice,
            LiveFaultStore.OnlineFrameKind, "restored-frame"));
    }

    [TestMethod]
    public async Task Roster_AuthoritativeRefresh_CoalescesConcurrentFetches()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "alice" }, default);

        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.FetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = roster.RefreshAuthoritativeAsync(new[] { "alice" }, default);
        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = roster.RefreshAuthoritativeAsync(new[] { "alice" }, default);
        await Task.Delay(50);

        Assert.AreEqual(2, source.FetchCount,
            "the prewarm and one coalesced authoritative refresh are the only directory reads");
        source.FetchRelease.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, source.FetchCount);
        Assert.IsNotNull(roster.ResolveDevice("alice", siblingDevice));
    }

    [TestMethod]
    public async Task Roster_Invalidate_ForcesRefetch()
    {
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 0, "", KeyPair.New().PublicB64));
        var roster = NewRoster(source, "alice", new List<string>());

        await roster.RefreshAsync(new[] { "alice" }, default);
        roster.Invalidate("alice");
        await roster.RefreshAsync(new[] { "alice" }, default);

        Assert.AreEqual(2, source.FetchCount);
    }

    [TestMethod]
    public async Task Roster_Clear_EmptiesCache()
    {
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 4, "", KeyPair.New().PublicB64));
        var roster = NewRoster(source, "alice", new List<string>(), ownAuthGen: 4);
        await roster.RefreshAsync(new[] { "alice" }, default);

        roster.Clear();

        Assert.AreEqual(0, roster.AuthorizedDevices("alice").Count);
        Assert.AreEqual(-1, roster.AuthGeneration("alice"));
    }

    [TestMethod]
    public async Task Roster_Revocation_DropsRemovedKeyOnRefetch()
    {
        var source = new FakeMetadataSource();
        var keep = KeyPair.New();
        var revoke = KeyPair.New();
        source.SetHandle("alice", Dir("alice", 0, "", keep.PublicB64, revoke.PublicB64));
        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "alice" }, default);
        Assert.AreEqual(2, roster.AuthorizedDevices("alice").Count);

        // Directory drops the revoked device's key; a fresh fetch must remove it.
        source.SetHandle("alice", Dir("alice", 1, "", keep.PublicB64));
        roster.Invalidate("alice");
        await roster.RefreshAsync(new[] { "alice" }, default);

        var devices = roster.AuthorizedDevices("alice");
        Assert.AreEqual(1, devices.Count);
        Assert.AreEqual(DeviceProtocol.DeviceId(keep.PublicB64), devices[0].DeviceId);
        Assert.IsNull(roster.ResolveDevice("alice", DeviceProtocol.DeviceId(revoke.PublicB64)));
    }

    [TestMethod]
    public async Task Roster_OwnAuthGenerationChange_FiresAuthorityChanged()
    {
        var source = new FakeMetadataSource();
        var fired = false;
        source.SetHandle("alice", Dir("alice", 9, "", KeyPair.New().PublicB64));
        // Client believes it is on generation 0; relay says 9 -> authority moved.
        var roster = NewRoster(source, "alice", new List<string>(), onAuthorityChanged: () => fired = true, ownAuthGen: 0);

        await roster.RefreshAsync(new[] { "alice" }, default);

        Assert.IsTrue(fired, "an auth-generation mismatch on the own handle must fire the authority-changed callback");
    }

    [TestMethod]
    public async Task Roster_OwnCustodyMismatch_SurfacesAndFires()
    {
        var source = new FakeMetadataSource();
        var fired = false;
        var surfaced = new List<string>();
        source.SetHandle("alice", Dir("alice", 0, "relay-head", KeyPair.New().PublicB64));
        var roster = NewRoster(
            source, "alice", surfaced, onAuthorityChanged: () => fired = true,
            localCustody: _ => "local-head", ownAuthGen: 0, ownCustody: "relay-head");

        await roster.RefreshAsync(new[] { "alice" }, default);

        Assert.IsTrue(fired);
        lock (surfaced)
            Assert.IsTrue(surfaced.Any(s => s.Contains("custody", StringComparison.OrdinalIgnoreCase)),
                "a local/relay custody-head disagreement must be surfaced");
    }

    [TestMethod]
    public async Task Roster_MissingDirectoryEntry_SurfacesAndDoesNotThrow()
    {
        var source = new FakeMetadataSource(); // no handle registered
        var surfaced = new List<string>();
        var roster = NewRoster(source, "alice", surfaced);

        await roster.RefreshAsync(new[] { "ghost" }, default);

        Assert.AreEqual(0, roster.AuthorizedDevices("ghost").Count);
        lock (surfaced)
            Assert.IsTrue(surfaced.Any(s => s.Contains("unavailable", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Roster_NullSource_Throws()
        => Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RelayReplicationRoster(null!, "alice", 0, "", _ => "", _ => { }, () => { }));

    // =====================================================================
    // ReplicationDeliveryGuard (pre-engine route validation).
    // =====================================================================

    private static OnlineRelayDelivery Delivery(string from, string fromDev, string to, string? toDev)
        => new(from, fromDev, to, toDev, "frame-1", OnlinePushClasses.Normal, "cipher");

    [TestMethod]
    public void Guard_AcceptsValidDeviceDirectedRoute()
    {
        var ok = ReplicationDeliveryGuard.ValidateRoute(
            Delivery("bob", "bob-dev", "alice", "alice-dev"), "alice", "alice-dev", out var reason);
        Assert.IsTrue(ok);
        Assert.AreEqual("", reason);
    }

    [TestMethod]
    public void Guard_AcceptsHandleDirectedRoute_WhenNoToDevice()
    {
        var ok = ReplicationDeliveryGuard.ValidateRoute(
            Delivery("bob", "bob-dev", "alice", null), "alice", "alice-dev", out _);
        Assert.IsTrue(ok);
    }

    [TestMethod]
    public void Guard_RejectsMissingSenderStamp()
    {
        var ok = ReplicationDeliveryGuard.ValidateRoute(
            Delivery("", "bob-dev", "alice", "alice-dev"), "alice", "alice-dev", out var reason);
        Assert.IsFalse(ok);
        StringAssert.Contains(reason, "route stamp");
    }

    [TestMethod]
    public void Guard_RejectsMisroutedToHandle()
    {
        var ok = ReplicationDeliveryGuard.ValidateRoute(
            Delivery("bob", "bob-dev", "carol", "alice-dev"), "alice", "alice-dev", out var reason);
        Assert.IsFalse(ok);
        StringAssert.Contains(reason, "handle");
    }

    [TestMethod]
    public void Guard_RejectsWrongToDevice()
    {
        var ok = ReplicationDeliveryGuard.ValidateRoute(
            Delivery("bob", "bob-dev", "alice", "someone-elses-device"), "alice", "alice-dev", out var reason);
        Assert.IsFalse(ok);
        StringAssert.Contains(reason, "device");
    }

    // =====================================================================
    // Projection codec (non-null, explicit validation, no silent cursor advance).
    // =====================================================================

    private (SqliteConnection Conn, SqliteTransaction Tx) OpenTx()
    {
        SQLitePCL.Batteries_V2.Init();
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var tx = conn.BeginTransaction();
        _disposables.Add(tx);
        _disposables.Add(conn);
        return (conn, tx);
    }

    [TestMethod]
    public void Projection_MissingDomainSchema_FailsClosed()
    {
        var (conn, tx) = OpenTx();
        // A bare database carries no domain tables. Advancing the cursor here would strand the
        // change forever, so the projection fails closed instead of silently accepting it.
        Assert.ThrowsExactly<ReplicationProjectionException>(() => ReplicationPayloadCodec.Project(
            conn, tx, DummyEvent(), Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert), true));
    }

    [TestMethod]
    public void Projection_ThrowsForUnknownKind()
        => Assert.ThrowsExactly<ReplicationProjectionException>(() =>
        {
            var (conn, tx) = OpenTx();
            ReplicationPayloadCodec.Project(
                conn, tx, DummyEvent(), Env("totally-unknown-kind", ReplicationPayloadCodec.DomainAction.Upsert), true);
        });

    [TestMethod]
    public void Projection_ThrowsForUnmappedActionOnKnownKind()
        => Assert.ThrowsExactly<ReplicationProjectionException>(() =>
        {
            var (conn, tx) = OpenTx();
            // Asset upsert is not legal on a message kind.
            ReplicationPayloadCodec.Project(
                conn, tx, DummyEvent(), Env(
                    ReplicationOpKinds.Message,
                    ReplicationPayloadCodec.DomainAction.AssetUpsert),
                true);
        });

    [TestMethod]
    public void Projection_ThrowsForMissingEntityId()
        => Assert.ThrowsExactly<ReplicationProjectionException>(() =>
        {
            var (conn, tx) = OpenTx();
            ReplicationPayloadCodec.Project(
                conn, tx, DummyEvent(),
                Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, entityId: ""), true);
        });

    [TestMethod]
    public void Projection_DesktopOnlyAsset_FailsClosedOnMobile()
    {
        var (conn, tx) = OpenTx();
        // Mobile cannot hold asset bytes, so the change is refused rather than skipped: the cursor
        // must not advance past something this device can never materialise.
        Assert.ThrowsExactly<ReplicationProjectionException>(() => ReplicationPayloadCodec.Project(
            conn, tx, DummyEvent(ReplicationOpKinds.Asset),
            Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert), deviceIsDesktop: false));
    }

    // =====================================================================
    // ReplicationPresencePoller (bounded ResolvePresence -> StartSession).
    // =====================================================================

    private ReplicationPresencePoller NewPoller(
        OnlineReplicationEngine engine, RelayReplicationRoster roster, FakeMetadataSource source,
        string ownHandle, string ownDevice, Func<IReadOnlyList<string>> candidates, bool hasDueOutbox = false)
    {
        var poller = new ReplicationPresencePoller(
            engine, roster, source, candidates, _ => hasDueOutbox, ownHandle, ownDevice, _ => { });
        _disposables.Add(poller);
        return poller;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsTrue(predicate(), $"Condition was not met within {timeout}.");
    }

    [TestMethod]
    public async Task Poller_OnlineAuthorizedSibling_StartsSession()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sib = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var sibDevice = DeviceProtocol.DeviceId(sib.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sib.PublicB64));
        source.SetPresence("alice", true, myDevice, sibDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });

        var pending = await poller.PollOnceAsync(default);

        Assert.IsTrue(pending, "an online authorized peer is pending work");
        var kinds = transport.DecodedKinds();
        Assert.IsTrue(kinds.Any(k => k.Kind == E2EFrameKind.SessionInit && k.ToDevice == sibDevice),
            "the poller must start a session to the online authorized sibling");
        Assert.IsFalse(kinds.Any(k => k.ToDevice == myDevice), "the poller must never start a session to its own device");
    }

    [TestMethod]
    public async Task Poller_RosterWake_ReportsOnlyMeaningfulPerDeviceTransitions()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var target = KeyPair.New();
        var unrelated = KeyPair.New();
        var rotatedTarget = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var targetDevice = DeviceProtocol.DeviceId(target.PublicB64);
        var unrelatedDevice = DeviceProtocol.DeviceId(unrelated.PublicB64);
        var rotatedTargetDevice = DeviceProtocol.DeviceId(rotatedTarget.PublicB64);
        source.SetHandle("bob", Dir("bob", 1, "authority-a", target.PublicB64));
        source.SetHandle("charlie", Dir("charlie", 1, "authority-c", unrelated.PublicB64));
        source.SetPresence("bob", false, targetDevice);
        source.SetPresence("charlie", false, unrelatedDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var wakes = new List<string[]>();
        var poller = new ReplicationPresencePoller(
            engine,
            roster,
            source,
            () => new[] { "bob", "charlie" },
            _ => true,
            "alice",
            myDevice,
            _ => { },
            rosterOnline: devices => wakes.Add(devices.OrderBy(id => id, StringComparer.Ordinal).ToArray()));
        _disposables.Add(poller);

        await poller.PollOnceAsync(default);
        await poller.PollOnceAsync(default);
        Assert.AreEqual(0, wakes.Count, "unchanged offline polls must not wake retry backoff");

        source.SetPresence("charlie", true, unrelatedDevice);
        await poller.PollOnceAsync(default);
        Assert.AreEqual(1, wakes.Count);
        CollectionAssert.AreEqual(new[] { unrelatedDevice }, wakes[0],
            "an unrelated transition must identify only the unrelated device");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => poller.PollOnceAsync(default)));
        Assert.AreEqual(1, wakes.Count, "concurrent unchanged polls must not duplicate a wake");

        source.SetPresence("bob", true, targetDevice);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => poller.PollOnceAsync(default)));
        Assert.AreEqual(2, wakes.Count, "the exact target transition must coalesce to one wake");
        CollectionAssert.AreEqual(new[] { targetDevice }, wakes[1]);

        source.SetHandle("bob", Dir("bob", 2, "authority-b", rotatedTarget.PublicB64));
        source.SetPresence("bob", true, rotatedTargetDevice);
        roster.Invalidate("bob");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => poller.PollOnceAsync(default)));
        Assert.AreEqual(3, wakes.Count, "an authority/device epoch change must coalesce to one wake");
        CollectionAssert.AreEqual(new[] { rotatedTargetDevice }, wakes[2]);

        await poller.PollOnceAsync(default);
        Assert.AreEqual(3, wakes.Count, "an unchanged authority epoch must not reset retry backoff");
        Console.WriteLine(
            $"WAKE_TARGETS count={wakes.Count} targets=" +
            $"{string.Join("|", wakes.Select(batch => string.Join(",", batch)))} " +
            "unchangedOfflineWakes=0 concurrentDuplicateWakes=0");
    }

    [TestMethod]
    public async Task Poller_AccountRoster_ReportsOnlineOfflineReconnectAndCoalescesReplays()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var mobile = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var mobileDevice = DeviceProtocol.DeviceId(mobile.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, mobile.PublicB64));
        source.SetPresence("alice", true, myDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var observed = new List<string[]>();
        var poller = new ReplicationPresencePoller(
            engine,
            roster,
            source,
            () => new[] { "alice" },
            _ => false,
            "alice",
            myDevice,
            _ => { },
            accountRosterObserved: devices => observed.Add(
                devices.OrderBy(device => device, StringComparer.Ordinal).ToArray()));
        _disposables.Add(poller);

        await poller.PollOnceAsync(default);
        await poller.PollOnceAsync(default);
        Assert.AreEqual(1, observed.Count, "a replayed roster must not duplicate notifications");
        CollectionAssert.AreEqual(new[] { myDevice }, observed[0]);

        source.SetPresence("alice", true, myDevice, mobileDevice);
        await poller.PollOnceAsync(default);
        await poller.PollOnceAsync(default);
        Assert.AreEqual(2, observed.Count);
        CollectionAssert.AreEqual(
            new[] { mobileDevice, myDevice }.OrderBy(device => device, StringComparer.Ordinal).ToArray(),
            observed[1]);

        source.SetPresence("alice", true, myDevice);
        await poller.PollOnceAsync(default);
        Assert.AreEqual(3, observed.Count, "disconnect must remove the expired device");
        CollectionAssert.AreEqual(new[] { myDevice }, observed[2]);

        source.SetPresence("alice", true, myDevice, mobileDevice);
        await poller.PollOnceAsync(default);
        Assert.AreEqual(4, observed.Count, "reconnect must be observable after an offline transition");
    }

    [TestMethod]
    public async Task Poller_OnlineNewSibling_BypassesFreshStaleRoster()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));

        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "alice" }, default);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", true, myDevice, siblingDevice);

        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });

        await poller.PollOnceAsync(default);

        Assert.AreEqual(2, source.FetchCount,
            "online presence for an unknown device must bypass the still-fresh roster cache");
        Assert.IsTrue(
            transport.DecodedKinds().Any(k =>
                k.Kind == E2EFrameKind.SessionInit && k.ToDevice == siblingDevice),
            "the newly linked online sibling must be contacted in the same poll");
    }

    [TestMethod]
    public async Task Poller_PokeInterruptsIdleDelay()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", false, siblingDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });
        poller.Start();
        await WaitUntilAsync(() => Volatile.Read(ref source.ResolveCount) >= 1, TimeSpan.FromSeconds(2));

        source.SetPresence("alice", true, siblingDevice);
        poller.Poke();

        await WaitUntilAsync(
            () => transport.DecodedKinds().Any(item =>
                item.Kind == E2EFrameKind.SessionInit && item.ToDevice == siblingDevice),
            TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Poller_NewSibling_BootstrapsOnlyAfterSessionEstablished()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", true, myDevice, siblingDevice);

        var sessionRoster = new FabricRoster();
        sessionRoster.Add("alice", new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        sessionRoster.Add("alice", new ReplicationDevice("alice", siblingDevice, sibling.PublicB64, 0, false));
        var fabric = new ReplicationFabric();
        var engine = NewEngine("alice", myDevice, sessionRoster, fabric.TransportFor("alice", myDevice), mine);
        var siblingEngine = NewEngine(
            "alice",
            siblingDevice,
            sessionRoster,
            fabric.TransportFor("alice", siblingDevice),
            sibling);
        fabric.Register("alice", myDevice, engine);
        fabric.Register("alice", siblingDevice, siblingEngine);

        var presenceRoster = NewRoster(source, "alice", new List<string>());
        ReplicationBootstrapTarget? bootstrapTarget = null;
        var bootstraps = 0;
        var poller = new ReplicationPresencePoller(
            engine,
            presenceRoster,
            source,
            () => new[] { "alice" },
            _ => false,
            "alice",
            myDevice,
            _ => { },
            (target, _) =>
            {
                bootstrapTarget = target;
                bootstraps++;
                return Task.CompletedTask;
            });
        _disposables.Add(poller);

        await poller.PollOnceAsync(default);
        Assert.AreEqual(0, bootstraps, "bootstrap must not start before the signed session handshake completes");

        await fabric.DrainAsync();
        Assert.IsTrue(engine.IsSessionEstablished(siblingDevice));
        await poller.PollOnceAsync(default);

        Assert.AreEqual(1, bootstraps);
        Assert.IsNotNull(bootstrapTarget);
        Assert.AreEqual(siblingDevice, bootstrapTarget.PeerDeviceId);
        Assert.AreEqual("alice", bootstrapTarget.PeerHandle);
    }

    [TestMethod]
    public async Task Poller_TwentyConcurrentManualPollsAndPokeCoalesceOneFollowupFlight()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", true, myDevice, siblingDevice);

        var sessionRoster = new FabricRoster();
        sessionRoster.Add("alice", new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        sessionRoster.Add("alice", new ReplicationDevice("alice", siblingDevice, sibling.PublicB64, 0, false));
        var fabric = new ReplicationFabric();
        var engine = NewEngine("alice", myDevice, sessionRoster, fabric.TransportFor("alice", myDevice), mine);
        var siblingEngine = NewEngine(
            "alice",
            siblingDevice,
            sessionRoster,
            fabric.TransportFor("alice", siblingDevice),
            sibling);
        fabric.Register("alice", myDevice, engine);
        fabric.Register("alice", siblingDevice, siblingEngine);
        await engine.StartSessionAsync("alice", siblingDevice);
        await fabric.DrainAsync();
        Assert.IsTrue(engine.IsSessionEstablished(siblingDevice));

        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bootstrapCount = 0;
        var presenceRoster = NewRoster(source, "alice", new List<string>());
        var poller = new ReplicationPresencePoller(
            engine,
            presenceRoster,
            source,
            () => new[] { "alice" },
            _ => false,
            "alice",
            myDevice,
            _ => { },
            async (_, ct) =>
            {
                Interlocked.Increment(ref bootstrapCount);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(ct);
            });
        _disposables.Add(poller);

        var first = poller.PollOnceAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var joined = Enumerable.Range(0, 19)
            .Select(_ => poller.PollOnceAsync(default))
            .ToArray();
        poller.Poke();
        await Task.Delay(25);
        Assert.AreEqual(1, Volatile.Read(ref bootstrapCount));

        release.TrySetResult(true);
        await Task.WhenAll(joined.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => !poller.PollFlightState.Active,
            TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, bootstrapCount, "a poke newer than the active pass must cause one follow-up");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(64)]
    public async Task Poller_PokesAfterFinalCheckBeforeClear_CoalesceOneFollowup(int pokeCount)
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var hookCalls = 0;
        using var finalCheckEntered = new ManualResetEventSlim();
        using var allowClear = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                Interlocked.Increment(ref polls);
                return Array.Empty<string>();
            });
        poller.BeforePollFlightFinalize = () =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1) return;
            finalCheckEntered.Set();
            allowClear.Wait(TimeSpan.FromSeconds(5));
        };

        var first = poller.PollOnceAsync(default);
        Assert.IsTrue(finalCheckEntered.Wait(TimeSpan.FromSeconds(2)));
        var pokes = Enumerable.Range(0, pokeCount)
            .Select(_ => Task.Run(poller.Poke))
            .ToArray();
        await Task.WhenAll(pokes).WaitAsync(TimeSpan.FromSeconds(2));

        allowClear.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => !poller.PollFlightState.Active,
            TimeSpan.FromSeconds(2));

        var state = poller.PollFlightState;
        Assert.AreEqual(2, polls, "the entire poke burst must produce exactly one follow-up poll");
        Assert.AreEqual(pokeCount, state.Requested);
        Assert.AreEqual(state.Requested, state.Completed, "the follow-up must cover the newest generation");
        Assert.IsFalse(state.Pending);
        Assert.IsFalse(state.Active);
    }

    [TestMethod]
    public async Task Poller_ConcurrentCallersAndPokeBurst_NeverOverlapOrAmplify()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var active = 0;
        var maxActive = 0;
        using var firstPollEntered = new ManualResetEventSlim();
        using var allowFirstPoll = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                var current = Interlocked.Increment(ref active);
                var observedMax = Volatile.Read(ref maxActive);
                while (current > observedMax)
                {
                    var previous = Interlocked.CompareExchange(ref maxActive, current, observedMax);
                    if (previous == observedMax) break;
                    observedMax = previous;
                }
                try
                {
                    if (Interlocked.Increment(ref polls) == 1)
                    {
                        firstPollEntered.Set();
                        allowFirstPoll.Wait(TimeSpan.FromSeconds(5));
                    }
                    return Array.Empty<string>();
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var callers = Enumerable.Range(0, 32)
            .Select(_ => poller.PollOnceAsync(default))
            .ToArray();
        Assert.IsTrue(firstPollEntered.Wait(TimeSpan.FromSeconds(2)));
        var pokes = Enumerable.Range(0, 128)
            .Select(_ => Task.Run(poller.Poke))
            .ToArray();
        await Task.WhenAll(pokes).WaitAsync(TimeSpan.FromSeconds(2));
        allowFirstPoll.Set();
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => !poller.PollFlightState.Active,
            TimeSpan.FromSeconds(2));

        var state = poller.PollFlightState;
        Assert.AreEqual(1, maxActive, "poll cores must never overlap");
        Assert.AreEqual(2, polls, "all callers and the poke burst must coalesce to one flight plus one follow-up");
        Assert.AreEqual(128L, state.Requested);
        Assert.AreEqual(state.Requested, state.Completed);
        Assert.IsFalse(state.Pending);
        Assert.IsFalse(state.Active);
    }

    [TestMethod]
    public async Task Poller_PokeSimultaneousWithFinalClear_IsNeverLost_OneThousandIterations()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var armed = 0;
        ManualResetEventSlim? finalCheckEntered = null;
        ManualResetEventSlim? allowClear = null;
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                Interlocked.Increment(ref polls);
                return Array.Empty<string>();
            });
        poller.BeforePollFlightFinalize = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1) return;
            finalCheckEntered!.Set();
            allowClear!.Wait(TimeSpan.FromSeconds(5));
        };

        for (var iteration = 1; iteration <= 1_000; iteration++)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            finalCheckEntered = entered;
            allowClear = release;
            Volatile.Write(ref armed, 1);

            var flight = poller.PollOnceAsync(default);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)), $"iteration {iteration} missed the final-check barrier");
            await Task.Run(poller.Poke).WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await flight.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () =>
                {
                    var current = poller.PollFlightState;
                    return !current.Active && current.Completed == current.Requested;
                },
                TimeSpan.FromSeconds(2));

            var state = poller.PollFlightState;
            Assert.AreEqual(iteration, state.Requested, $"iteration {iteration} lost its poke");
            Assert.AreEqual(state.Requested, state.Completed, $"iteration {iteration} was not covered");
            Assert.IsFalse(state.Pending);
            Assert.IsFalse(state.Active);
            Assert.AreEqual(iteration * 2, Volatile.Read(ref polls),
                $"iteration {iteration} amplified or omitted its single follow-up");
        }
    }

    [TestMethod]
    public async Task Poller_FailedPollWithPoke_InstallsSuccessorBeforeOldWaiterResumes()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        using var failedPollEntered = new ManualResetEventSlim();
        using var allowFailure = new ManualResetEventSlim();
        using var successorEntered = new ManualResetEventSlim();
        using var allowSuccessor = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                var poll = Interlocked.Increment(ref polls);
                if (poll == 1)
                {
                    failedPollEntered.Set();
                    allowFailure.Wait(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("injected poll failure");
                }
                successorEntered.Set();
                allowSuccessor.Wait(TimeSpan.FromSeconds(5));
                return Array.Empty<string>();
            });

        var failed = poller.PollOnceAsync(default);
        Assert.IsTrue(failedPollEntered.Wait(TimeSpan.FromSeconds(2)));
        poller.Poke();
        allowFailure.Set();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failed);

        var failedState = poller.PollFlightState;
        Assert.AreEqual(1L, failedState.Requested);
        Assert.AreEqual(0L, failedState.Completed, "a failed poll must not observe its generation");
        Assert.IsTrue(failedState.Pending);
        Assert.IsTrue(failedState.Active, "pending work must already own the successor slot");

        Assert.IsTrue(successorEntered.Wait(TimeSpan.FromSeconds(2)));
        var successor = poller.PollOnceAsync(default);
        allowSuccessor.Set();
        await successor.WaitAsync(TimeSpan.FromSeconds(2));
        var recoveredState = poller.PollFlightState;
        Assert.AreEqual(2, polls);
        Assert.AreEqual(recoveredState.Requested, recoveredState.Completed);
        Assert.IsFalse(recoveredState.Pending);
        Assert.IsFalse(recoveredState.Active);
    }

    [TestMethod]
    public async Task Poller_FailedLatestPoke_DelaysAndRunsOneSuccessor()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        using var firstEntered = new ManualResetEventSlim();
        using var allowFailure = new ManualResetEventSlim();
        using var successorEntered = new ManualResetEventSlim();
        using var allowSuccessor = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                if (Interlocked.Increment(ref polls) == 1)
                {
                    firstEntered.Set();
                    allowFailure.Wait(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("latest poke failed");
                }
                successorEntered.Set();
                allowSuccessor.Wait(TimeSpan.FromSeconds(5));
                return Array.Empty<string>();
            });

        poller.Poke();
        var failed = poller.PollOnceAsync(default);
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(2)));
        allowFailure.Set();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failed);

        var failedState = poller.PollFlightState;
        Assert.AreEqual(1L, failedState.Requested);
        Assert.AreEqual(0L, failedState.Completed);
        Assert.IsTrue(failedState.Pending);
        Assert.IsTrue(failedState.Active);
        await Task.Delay(ReplicationPresencePoller.PollFailureRetryBaseDelay / 2);
        Assert.AreEqual(1, polls, "a failed demanded generation must not tight-loop");

        Assert.IsTrue(successorEntered.Wait(TimeSpan.FromSeconds(2)));
        var successor = poller.PollOnceAsync(default);
        allowSuccessor.Set();
        await successor.WaitAsync(TimeSpan.FromSeconds(2));

        var recoveredState = poller.PollFlightState;
        Assert.AreEqual(2, polls);
        Assert.AreEqual(recoveredState.Requested, recoveredState.Completed);
        Assert.IsFalse(recoveredState.Pending);
        Assert.IsFalse(recoveredState.Active);
    }

    [TestMethod]
    public async Task Poller_RepeatedFailures_BackOffAndRunOneAtATime()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var active = 0;
        var maxActive = 0;
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                var current = Interlocked.Increment(ref active);
                var observedMax = Volatile.Read(ref maxActive);
                while (current > observedMax)
                {
                    var previous = Interlocked.CompareExchange(ref maxActive, current, observedMax);
                    if (previous == observedMax) break;
                    observedMax = previous;
                }
                try
                {
                    var poll = Interlocked.Increment(ref polls);
                    if (poll <= 3)
                        throw new InvalidOperationException($"failure {poll}");
                    return Array.Empty<string>();
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        poller.Poke();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => poller.PollOnceAsync(default));
        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 2, TimeSpan.FromSeconds(2));
        await Task.Delay(ReplicationPresencePoller.PollFailureRetryBaseDelay);
        Assert.AreEqual(2, Volatile.Read(ref polls), "the second retry must use a longer cooldown");
        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 3, TimeSpan.FromSeconds(2));
        await Task.Delay(ReplicationPresencePoller.PollFailureRetryBaseDelay * 2);
        Assert.AreEqual(3, Volatile.Read(ref polls), "the third retry must continue bounded backoff");
        await WaitUntilAsync(
            () =>
            {
                var state = poller.PollFlightState;
                return Volatile.Read(ref polls) == 4 && !state.Active && !state.Pending;
            },
            TimeSpan.FromSeconds(3));

        Assert.AreEqual(1, maxActive);
        Assert.AreEqual(1L, poller.PollFlightState.Completed);
    }

    [TestMethod]
    public async Task Poller_CanceledPollWithPoke_InstallsSuccessorBeforeOldWaiterResumes()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        using var canceledFinalizeEntered = new ManualResetEventSlim();
        using var allowCanceledFinalize = new ManualResetEventSlim();
        using var successorEntered = new ManualResetEventSlim();
        using var allowSuccessor = new ManualResetEventSlim();
        var hookCalls = 0;
        var canceledToken = new CancellationToken(canceled: true);
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                if (Interlocked.Increment(ref polls) == 1)
                    throw new OperationCanceledException(canceledToken);
                successorEntered.Set();
                allowSuccessor.Wait(TimeSpan.FromSeconds(5));
                return Array.Empty<string>();
            });
        poller.BeforePollFlightFinalize = () =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1) return;
            canceledFinalizeEntered.Set();
            allowCanceledFinalize.Wait(TimeSpan.FromSeconds(5));
        };

        var canceled = poller.PollOnceAsync(default);
        Assert.IsTrue(canceledFinalizeEntered.Wait(TimeSpan.FromSeconds(2)));
        poller.Poke();
        allowCanceledFinalize.Set();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceled);

        var canceledState = poller.PollFlightState;
        Assert.AreEqual(1L, canceledState.Requested);
        Assert.AreEqual(0L, canceledState.Completed);
        Assert.IsTrue(canceledState.Pending);
        Assert.IsTrue(canceledState.Active, "pending work must atomically replace the canceled flight");

        Assert.IsTrue(successorEntered.Wait(TimeSpan.FromSeconds(2)));
        var successor = poller.PollOnceAsync(default);
        allowSuccessor.Set();
        await successor.WaitAsync(TimeSpan.FromSeconds(2));
        var recoveredState = poller.PollFlightState;
        Assert.AreEqual(2, polls);
        Assert.AreEqual(recoveredState.Requested, recoveredState.Completed);
        Assert.IsFalse(recoveredState.Pending);
        Assert.IsFalse(recoveredState.Active);
    }

    [TestMethod]
    public async Task Poller_ExplicitlyCanceledLatestPoke_RetainsDemandAndRunsSuccessor()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        using var firstEntered = new ManualResetEventSlim();
        using var allowCancellation = new ManualResetEventSlim();
        using var successorEntered = new ManualResetEventSlim();
        var canceledToken = new CancellationToken(canceled: true);
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                if (Interlocked.Increment(ref polls) == 1)
                {
                    firstEntered.Set();
                    allowCancellation.Wait(TimeSpan.FromSeconds(5));
                    throw new OperationCanceledException(canceledToken);
                }
                successorEntered.Set();
                return Array.Empty<string>();
            });

        poller.Poke();
        var canceled = poller.PollOnceAsync(default);
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(2)));
        allowCancellation.Set();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceled);
        Assert.IsTrue(poller.PollFlightState.Pending);
        Assert.IsTrue(poller.PollFlightState.Active);

        Assert.IsTrue(successorEntered.Wait(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(
            () => !poller.PollFlightState.Active,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, polls);
        Assert.AreEqual(1L, poller.PollFlightState.Completed);
        Assert.IsFalse(poller.PollFlightState.Pending);
    }

    [TestMethod]
    public async Task Poller_PokesDuringFailureBackoff_CoalesceIntoInstalledSuccessor()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        using var successorEntered = new ManualResetEventSlim();
        using var allowSuccessor = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                if (Interlocked.Increment(ref polls) == 1)
                    throw new InvalidOperationException("initial failure");
                successorEntered.Set();
                allowSuccessor.Wait(TimeSpan.FromSeconds(5));
                return Array.Empty<string>();
            });

        poller.Poke();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => poller.PollOnceAsync(default));
        var pokes = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(poller.Poke))
            .ToArray();
        await Task.WhenAll(pokes).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(ReplicationPresencePoller.PollFailureRetryBaseDelay / 2);
        Assert.AreEqual(1, polls);

        Assert.IsTrue(successorEntered.Wait(TimeSpan.FromSeconds(2)));
        var successor = poller.PollOnceAsync(default);
        allowSuccessor.Set();
        await successor.WaitAsync(TimeSpan.FromSeconds(2));

        var state = poller.PollFlightState;
        Assert.AreEqual(2, polls);
        Assert.AreEqual(101L, state.Requested);
        Assert.AreEqual(state.Requested, state.Completed);
        Assert.IsFalse(state.Pending);
        Assert.IsFalse(state.Active);
    }

    [TestMethod]
    public async Task Poller_CanceledWaiterWithPoke_DoesNotCancelGenerationCoverage()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetPresence("alice", false);
        source.FetchEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.FetchRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                Interlocked.Increment(ref polls);
                return new[] { "alice" };
            });
        using var waiterCancellation = new CancellationTokenSource();

        var waiter = poller.PollOnceAsync(waiterCancellation.Token);
        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        poller.Poke();
        waiterCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => waiter);

        source.FetchRelease.TrySetResult(true);
        await WaitUntilAsync(
            () =>
            {
                var state = poller.PollFlightState;
                return !state.Active && !state.Pending && state.Completed == state.Requested;
            },
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, polls, "waiter cancellation must not suppress or amplify the poke follow-up");
        Assert.AreEqual(1L, poller.PollFlightState.Completed);
    }

    [TestMethod]
    public async Task Poller_DisposeWithPendingPoke_ClearsPendingAndDrainsWithoutSuccessor()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.FetchEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.FetchRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                Interlocked.Increment(ref polls);
                return new[] { "alice" };
            });

        var flight = poller.PollOnceAsync(default);
        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        poller.Poke();
        await poller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => flight);

        var state = poller.PollFlightState;
        Assert.AreEqual(1, polls);
        Assert.AreEqual(1L, state.Requested);
        Assert.AreEqual(0L, state.Completed);
        Assert.IsFalse(state.Pending);
        Assert.IsFalse(state.Active);
        poller.Poke();
        Assert.AreEqual(state, poller.PollFlightState, "a disposed poller must ignore later pokes");
    }

    [TestMethod]
    public async Task Poller_DisposeDuringNonCancellationFault_CleansResourcesAndSurfacesError()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        using var pollEntered = new ManualResetEventSlim();
        using var allowFault = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                pollEntered.Set();
                allowFault.Wait(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("fault during disposal");
            });

        poller.Poke();
        var sharedFlight = poller.PollOnceAsync(default);
        Assert.IsTrue(pollEntered.Wait(TimeSpan.FromSeconds(2)));
        var disposal = poller.DisposeAsync().AsTask();
        allowFault.Set();

        var disposeError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => disposal);
        Assert.AreEqual("fault during disposal", disposeError.Message);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sharedFlight);

        var state = poller.PollFlightState;
        var resources = poller.PollResourceState;
        Assert.IsFalse(state.Pending);
        Assert.IsFalse(state.Active);
        Assert.IsTrue(resources.LifetimeDisposed);
        Assert.IsFalse(resources.LoopOwned);
        Assert.IsFalse(resources.Active);
    }

    [TestMethod]
    public async Task Poller_DisposeAtFinalizationBarrier_WaitsUntilOldCompletionIsSignaled()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        using var finalizeEntered = new ManualResetEventSlim();
        using var allowFinalize = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () => Array.Empty<string>());
        poller.BeforePollFlightFinalize = () =>
        {
            finalizeEntered.Set();
            allowFinalize.Wait(TimeSpan.FromSeconds(5));
        };

        var old = poller.PollOnceAsync(default);
        Assert.IsTrue(finalizeEntered.Wait(TimeSpan.FromSeconds(2)));
        var disposal = poller.DisposeAsync().AsTask();
        await Task.Delay(25);

        Assert.IsFalse(old.IsCompleted, "the old promise must remain incomplete at the finalization barrier");
        Assert.IsFalse(disposal.IsCompleted, "disposal must observe and drain the active old flight");
        Assert.IsTrue(poller.PollFlightState.Active);

        allowFinalize.Set();
        await Task.WhenAll(old, disposal).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(poller.PollFlightState.Active);
    }

    [TestMethod]
    public async Task Poller_FinalizationBarrier_ThousandPokesInstallOneSuccessorAndDisposeDrainsIt()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var polls = 0;
        var active = 0;
        var maxActive = 0;
        var hookCalls = 0;
        using var oldFinalizeEntered = new ManualResetEventSlim();
        using var allowOldFinalize = new ManualResetEventSlim();
        using var successorEntered = new ManualResetEventSlim();
        using var allowSuccessor = new ManualResetEventSlim();
        var poller = NewPoller(
            engine,
            roster,
            source,
            "alice",
            myDevice,
            () =>
            {
                var current = Interlocked.Increment(ref active);
                var observedMax = Volatile.Read(ref maxActive);
                while (current > observedMax)
                {
                    var previous = Interlocked.CompareExchange(ref maxActive, current, observedMax);
                    if (previous == observedMax) break;
                    observedMax = previous;
                }
                var poll = Interlocked.Increment(ref polls);
                try
                {
                    if (poll == 2)
                    {
                        successorEntered.Set();
                        allowSuccessor.Wait(TimeSpan.FromSeconds(5));
                    }
                    return Array.Empty<string>();
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        poller.BeforePollFlightFinalize = () =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1) return;
            oldFinalizeEntered.Set();
            allowOldFinalize.Wait(TimeSpan.FromSeconds(5));
        };

        var old = poller.PollOnceAsync(default);
        Assert.IsTrue(oldFinalizeEntered.Wait(TimeSpan.FromSeconds(2)));
        var pokes = Enumerable.Range(0, 1_000)
            .Select(_ => Task.Run(poller.Poke))
            .ToArray();
        await Task.WhenAll(pokes).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(old.IsCompleted, "the active slot must not become observable as idle before signaling");

        allowOldFinalize.Set();
        await old.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(successorEntered.Wait(TimeSpan.FromSeconds(2)));
        var successor = poller.PollOnceAsync(default);
        var disposal = poller.DisposeAsync().AsTask();
        await Task.Delay(25);
        Assert.IsFalse(disposal.IsCompleted, "disposal must drain the installed successor");

        allowSuccessor.Set();
        await Task.WhenAll(successor, disposal).WaitAsync(TimeSpan.FromSeconds(2));
        var state = poller.PollFlightState;
        Assert.AreEqual(2, polls, "the poke burst must install exactly one successor");
        Assert.AreEqual(1, maxActive, "successor network work must not overlap the old poll");
        Assert.AreEqual(1_000L, state.Requested);
        Assert.AreEqual(state.Requested, state.Completed, "no poke generation may be lost");
        Assert.IsFalse(state.Pending);
        Assert.IsFalse(state.Active);
    }

    [TestMethod]
    public async Task Poller_FirstWaiterCancellationDoesNotCancelSharedFlight()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetPresence("alice", false);
        source.FetchEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.FetchRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var roster = NewRoster(source, "alice", new List<string>());
        var engine = NewEngine("alice", myDevice, roster, new RecordingTransport(), mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });
        using var firstWaiter = new CancellationTokenSource();

        var first = poller.PollOnceAsync(firstWaiter.Token);
        await source.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = poller.PollOnceAsync(CancellationToken.None);
        firstWaiter.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => first);
        Assert.IsFalse(second.IsCompleted, "the second waiter must remain joined to the shared flight");

        source.FetchRelease.TrySetResult(true);
        Assert.IsFalse(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, source.FetchCount);
    }

    [TestMethod]
    public async Task Poller_OfflineNewSibling_RequestsBootstrapWake()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sib = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sib.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sib.PublicB64));
        source.SetPresence("alice", false, siblingDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport
        {
            Result = new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline)
        };
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });

        var pending = await poller.PollOnceAsync(default);

        Assert.IsTrue(pending, "an unbootstrapped authorised sibling is pending synchronization work");
        Assert.IsTrue(transport.Wakes.Any(item =>
            item.ToHandle == "alice" && item.ToDevice == siblingDevice));
    }

    [TestMethod]
    public async Task Poller_DueOfflineSibling_EmitsOneBoundedWakeRequest()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var bob = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        var bobDevice = DeviceProtocol.DeviceId(bob.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetHandle("bob", Dir("bob", 0, "", bob.PublicB64));
        source.SetPresence("alice", false);
        source.SetPresence("bob", true, bobDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport
        {
            Result = new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline)
        };
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        (bool Online, bool Pending)? completed = null;
        var poller = new ReplicationPresencePoller(
            engine,
            roster,
            source,
            () => new[] { "alice", "bob" },
            handles => handles.Contains("alice", StringComparer.Ordinal),
            "alice",
            myDevice,
            _ => { },
            pollCompleted: (online, pending) => completed = (online, pending));
        _disposables.Add(poller);

        Assert.IsTrue(await poller.PollOnceAsync(default));
        Assert.AreEqual((false, true), completed);
        Assert.IsFalse(
            poller.HasImmediatelyDeliverableWork,
            "pending work for an offline sibling is not immediately deliverable through an unrelated online handle");
        await poller.PollOnceAsync(default);

        var wakes = transport.Wakes.Count(item => item.ToDevice == siblingDevice);
        Assert.AreEqual(1, wakes, "the offline device must receive one bounded relay wake request");
    }

    [TestMethod]
    public async Task Poller_OfflineEstablishedSibling_UsesNativeWakeInsteadOfStaleSession()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", false);

        var engineRoster = new StubRoster();
        engineRoster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        engineRoster.Add(new ReplicationDevice("alice", siblingDevice, sibling.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, engineRoster, transport, mine);
        await engine.HandleDeliveryAsync(BuildSessionInit(
            sibling, "alice", siblingDevice, myDevice, mine.PublicB64));
        Assert.IsTrue(engine.IsSessionEstablished(siblingDevice));

        await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert, "topic-complete") with
            {
                NotificationIntent = NotificationIntents.Topic(
                    "run-1",
                    "topic-complete",
                    "Notification test",
                    NotificationKind.TopicCompleted)
            },
            new[] { "alice" });
        var offersBeforePoll = transport.DecodedKinds()
            .Count(item => item.Kind == E2EFrameKind.Offer);
        var presenceRoster = NewRoster(source, "alice", new List<string>());
        var poller = NewPoller(
            engine,
            presenceRoster,
            source,
            "alice",
            myDevice,
            () => new[] { "alice" },
            hasDueOutbox: true);

        await poller.PollOnceAsync(default);

        Assert.AreEqual(1, transport.Wakes.Count);
        var wake = transport.Wakes[0];
        Assert.AreEqual(siblingDevice, wake.ToDevice);
        Assert.IsTrue(wake.NotificationWorthy);
        Assert.AreEqual(
            offersBeforePoll,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.Offer),
            "an offline peer must not be offered over a cached session whose socket is gone");
    }

    [TestMethod]
    public async Task Poller_AccountReceiptDoesNotSuppressLaggingDeviceWake()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var bob1 = KeyPair.New();
        var bob2 = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var bob1Device = DeviceProtocol.DeviceId(bob1.PublicB64);
        var bob2Device = DeviceProtocol.DeviceId(bob2.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetHandle("bob", Dir("bob", 0, "", bob1.PublicB64, bob2.PublicB64));
        source.SetPresence("bob", false);

        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "bob" }, default);
        var transport = new RecordingTransport
        {
            Result = new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline)
        };
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var db = _dbs[^1];
        var eventId = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "lagged"),
            new[] { "bob" });
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            bob1Device,
            myDevice,
            "epoch-1",
            1,
            OnlineReplicationProtocol.HashText("cursor"),
            OnlineReplicationProtocol.HashText("batch"),
            bob1.PrivateB64);
        Assert.AreEqual(1, db.MarkOutboxPersistedFromReceipt(receipt, bob1.PublicB64, "bob"));
        Assert.AreEqual(MeshDb.OutboxStatePersisted, db.GetOutboxState(eventId, "bob"));

        var poller = new ReplicationPresencePoller(
            engine,
            roster,
            source,
            () => new[] { "bob" },
            handles => db.CountUnpersistedOutbox(handles) > 0,
            "alice",
            myDevice,
            _ => { });
        _disposables.Add(poller);

        Assert.IsTrue(await poller.PollOnceAsync(default));

        var wokenDevices = transport.Wakes
            .Select(item => item.ToDevice)
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { bob2Device }, wokenDevices,
            "only the authorised device without its own receipt should be woken");
        Assert.IsTrue(poller.HasPendingSynchronizationWork);
        Assert.IsFalse(poller.HasImmediatelyDeliverableWork);
    }

    [TestMethod]
    public async Task Poller_EstablishedPeerReoffersPendingWorkAfterReconnect()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", true, siblingDevice);

        var sessionRoster = new FabricRoster();
        sessionRoster.Add("alice", new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        sessionRoster.Add("alice", new ReplicationDevice("alice", siblingDevice, sibling.PublicB64, 0, false));
        var fabric = new ReplicationFabric();
        var engine = NewEngine("alice", myDevice, sessionRoster, fabric.TransportFor("alice", myDevice), mine);
        var siblingEngine = NewEngine(
            "alice",
            siblingDevice,
            sessionRoster,
            fabric.TransportFor("alice", siblingDevice),
            sibling);
        var siblingDb = _dbs[^1];
        fabric.Register("alice", myDevice, engine);
        fabric.Register("alice", siblingDevice, siblingEngine);

        await engine.StartSessionAsync("alice", siblingDevice);
        await fabric.DrainAsync();
        Assert.IsTrue(engine.IsSessionEstablished(siblingDevice));

        fabric.SetOnline(siblingDevice, false);
        var eventId = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert, "topic-retry"),
            new[] { "alice" });
        await fabric.DrainAsync();
        Assert.IsNull(siblingDb.GetEvent(eventId));

        fabric.SetOnline(siblingDevice, true);
        var presenceRoster = NewRoster(source, "alice", new List<string>());
        var poller = NewPoller(
            engine,
            presenceRoster,
            source,
            "alice",
            myDevice,
            () => new[] { "alice" },
            hasDueOutbox: true);

        await poller.PollOnceAsync(default);
        await fabric.DrainAsync();

        Assert.IsNotNull(siblingDb.GetEvent(eventId));
    }

    [TestMethod]
    public async Task Poller_ReplayedOfferRecoversLostPersistenceReceipt()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sibling = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(sibling.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sibling.PublicB64));
        source.SetPresence("alice", true, siblingDevice);

        var sessionRoster = new FabricRoster();
        sessionRoster.Add("alice", new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        sessionRoster.Add("alice", new ReplicationDevice("alice", siblingDevice, sibling.PublicB64, 0, false));
        var fabric = new ReplicationFabric();
        var engine = NewEngine("alice", myDevice, sessionRoster, fabric.TransportFor("alice", myDevice), mine);
        var engineDb = _dbs[^1];
        var siblingEngine = NewEngine(
            "alice",
            siblingDevice,
            sessionRoster,
            fabric.TransportFor("alice", siblingDevice),
            sibling);
        var siblingDb = _dbs[^1];
        fabric.Register("alice", myDevice, engine);
        fabric.Register("alice", siblingDevice, siblingEngine);

        await engine.StartSessionAsync("alice", siblingDevice);
        await fabric.DrainAsync();
        fabric.DropFrame = (fromDevice, frame) =>
            string.Equals(fromDevice, siblingDevice, StringComparison.Ordinal)
            && ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext)?.Kind == E2EFrameKind.Receipt;

        var eventId = await engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert, "topic-receipt"),
            new[] { "alice" });
        await fabric.DrainAsync();

        Assert.IsNotNull(siblingDb.GetEvent(eventId));
        Assert.AreNotEqual(ReplicationDeliveryState.Persisted, engine.GetDeliveryState(eventId, "alice"));

        fabric.DropFrame = null;
        var presenceRoster = NewRoster(source, "alice", new List<string>());
        var poller = new ReplicationPresencePoller(
            engine,
            presenceRoster,
            source,
            () => new[] { "alice" },
            handles => engineDb.CountUnpersistedOutbox(handles) > 0,
            "alice",
            myDevice,
            _ => { });
        _disposables.Add(poller);

        await poller.PollOnceAsync(default);
        await fabric.DrainAsync();

        Assert.AreEqual(ReplicationDeliveryState.Persisted, engine.GetDeliveryState(eventId, "alice"));
    }

    [TestMethod]
    public async Task Poller_OnlyOwnDeviceOnline_StartsNoSession()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetPresence("alice", true, myDevice);

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });

        await poller.PollOnceAsync(default);

        Assert.AreEqual(0, transport.Sent.Count);
    }

    [TestMethod]
    public async Task Poller_UnauthorizedOnlineDevice_StartsNoSession()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        // Directory authorizes only my own device; presence reports an extra, unauthorized device id.
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetPresence("alice", true, myDevice, "unauthorized-device-id");

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });

        await poller.PollOnceAsync(default);

        Assert.AreEqual(0, transport.Sent.Count, "a device not in the roster must not receive a session start");
    }

    [TestMethod]
    public async Task Poller_NoCandidates_ReturnsFalseAndDoesNothing()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => Array.Empty<string>());

        var pending = await poller.PollOnceAsync(default);

        Assert.IsFalse(pending);
        Assert.AreEqual(0, source.ResolveCount, "with no candidates the poller must not even query presence");
    }

    [TestMethod]
    public async Task Poller_DueOutbox_KeepsPollingPending_EvenWhenOffline()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetPresence("alice", false);

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" }, hasDueOutbox: true);

        var pending = await poller.PollOnceAsync(default);

        Assert.IsTrue(pending, "a due outbox must keep the poller in the fast pending cadence");
    }

    [TestMethod]
    public void Poller_NullEngine_Throws()
        => Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReplicationPresencePoller(
                null!, NewRoster(new FakeMetadataSource(), "alice", new List<string>()), new FakeMetadataSource(),
                Array.Empty<string>, _ => false, "alice", "dev", _ => { }));

    // =====================================================================
    // Engine typed inbound dispatch (real delivery invokes the engine; spoof rejected).
    // =====================================================================

    [TestMethod]
    public async Task Engine_RepeatedSessionStart_RetriesStableHandshake()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new FabricRoster();
        roster.Add("alice", new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add("alice", new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine(
            "alice", myDevice, roster, transport, mine,
            sessionInitRetryInterval: TimeSpan.FromMilliseconds(20));

        await engine.StartSessionAsync("alice", peerDevice);
        await engine.StartSessionAsync("alice", peerDevice);
        await engine.OnWakeAsync("alice", peerDevice);

        Assert.AreEqual(
            1,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.SessionInit),
            "polling must coalesce an in-flight handshake before its retry interval");

        await Task.Delay(TimeSpan.FromMilliseconds(40));
        await engine.StartSessionAsync("alice", peerDevice);

        var initFrames = transport.Sent
            .Select(item => ReplicationPayloadCodec.DecodeFrame(item.Ciphertext))
            .Where(item => item?.Kind == E2EFrameKind.SessionInit)
            .Cast<E2EFrame>()
            .ToList();
        Assert.AreEqual(2, initFrames.Count,
            "an unacknowledged handshake must retry after the bounded interval");

        var firstInit = DecodeSessionInit(initFrames[0]);
        var retriedInit = DecodeSessionInit(initFrames[1]);
        Assert.AreEqual(firstInit.SessionId, retriedInit.SessionId,
            "a retry must keep the original session id so a delayed ack remains valid");
        Assert.AreEqual(firstInit.Nonce, retriedInit.Nonce,
            "a retry must keep the original nonce so a delayed ack remains valid");

        var ack = OnlineReplicationProtocol.CreateSessionAck(
            firstInit.SessionId, peerDevice, myDevice, MeshCrypto.NewNonce(), firstInit.Nonce,
            OnlineReplicationProtocol.ZeroHash, 0, peer.PrivateB64);
        var ackCipher = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.SerializeControl(ack), new[] { mine.PublicB64 });
        await engine.HandleDeliveryAsync(new OnlineRelayDelivery(
            "alice", peerDevice, "alice", myDevice, Guid.NewGuid().ToString("n"),
            OnlinePushClasses.High, ReplicationPayloadCodec.EncodeFrame(
                new E2EFrame(E2EFrameKind.SessionAck, firstInit.SessionId, ackCipher))));

        Assert.IsTrue(engine.IsSessionEstablished(peerDevice),
            "an acknowledgement delayed past a retry must establish the original handshake");
        ReplicationSessionInit DecodeSessionInit(E2EFrame frame)
        {
            var (decrypted, plaintext) = ReplicationPayloadCodec.TryDecrypt(
                frame.Payload, peer.PrivateB64, peer.PublicB64);
            Assert.IsTrue(
                decrypted,
                "offer recipients: "
                + string.Join(",", ReplicationPayloadCodec.RecipientDeviceIds(frame.Payload))
                + $"; expected: {peerDevice}");
            return ReplicationPayloadCodec.DeserializeControl<ReplicationSessionInit>(plaintext!)
                   ?? throw new AssertFailedException("Session init payload was invalid.");
        }
    }

    [TestMethod]
    public async Task Engine_FreshSessionInitBypassesBlockedDataLane()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));
        var transport = new FirstKindBlockingTransport(E2EFrameKind.Offer);
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        await engine.EmitLocalAsync(Env(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "blocked-old-session-offer"), new[] { "bob" });
        var oldSessionId = Guid.NewGuid().ToString("n");
        var freshSessionId = Guid.NewGuid().ToString("n");

        var oldHandshake = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, oldSessionId));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var freshHandshake = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, freshSessionId));

        try
        {
            var freshAcknowledged = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                freshAcknowledged = transport.Sent
                    .Select(item => ReplicationPayloadCodec.DecodeFrame(item.Ciphertext))
                    .Any(frame => frame?.Kind == E2EFrameKind.SessionAck
                        && string.Equals(frame.SessionId, freshSessionId, StringComparison.Ordinal));
                if (freshAcknowledged) break;
                await Task.Delay(10);
            }

            Assert.IsTrue(
                freshAcknowledged,
                "a restarted peer's signed handshake must not wait behind stale data-lane work");
            Assert.IsTrue(engine.IsSessionEstablished(peerDevice));
        }
        finally
        {
            transport.Release.TrySetResult(true);
        }

        await Task.WhenAll(oldHandshake, freshHandshake).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Engine_QueuedSessionInits_SkipSupersededHandshakes()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));
        var transport = new FirstSendBlockingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var firstSessionId = Guid.NewGuid().ToString("n");
        var obsoleteSessionId = Guid.NewGuid().ToString("n");
        var currentSessionId = Guid.NewGuid().ToString("n");

        var first = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, firstSessionId));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var obsolete = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, obsoleteSessionId));
        var current = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, currentSessionId));

        transport.Release.TrySetResult(true);
        await Task.WhenAll(first, obsolete, current).WaitAsync(TimeSpan.FromSeconds(2));

        var acknowledgedSessionIds = transport.Sent
            .Select(item => ReplicationPayloadCodec.DecodeFrame(item.Ciphertext))
            .Where(frame => frame?.Kind == E2EFrameKind.SessionAck)
            .Select(frame => frame!.SessionId)
            .ToList();
        CollectionAssert.AreEqual(
            new[] { firstSessionId, currentSessionId },
            acknowledgedSessionIds,
            "queued obsolete handshakes must not delay the newest session acknowledgement");
        Assert.IsTrue(engine.IsSessionEstablished(peerDevice));
    }

    [TestMethod]
    public async Task Engine_QueuedStableSessionRetries_AreEachAcknowledged()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));
        var transport = new FirstSendBlockingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var sessionId = Guid.NewGuid().ToString("n");
        var nonce = MeshCrypto.NewNonce();

        var first = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var retryOne = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce));
        var retryTwo = engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce));

        transport.Release.TrySetResult(true);
        await Task.WhenAll(first, retryOne, retryTwo).WaitAsync(TimeSpan.FromSeconds(2));

        var frames = transport.Sent
            .Select(item => ReplicationPayloadCodec.DecodeFrame(item.Ciphertext))
            .Where(frame => frame is not null)
            .Cast<E2EFrame>()
            .ToList();
        Assert.AreEqual(
            3,
            frames.Count(frame => frame.Kind == E2EFrameKind.SessionAck),
            "stable retries must each receive an acknowledgement even when newer retries are queued");
        Assert.AreEqual(
            0,
            frames.Count(frame => frame.Kind == E2EFrameKind.Offer),
            "stable retries must not repeat the initial offer pass when no origins exist");
        Assert.IsTrue(engine.IsSessionEstablished(peerDevice));
    }

    [TestMethod]
    public async Task Engine_DuplicateSessionInit_RepliesWithoutRepeatingInitialOffers()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        await engine.EmitLocalAsync(Env(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "queued-message"), new[] { "bob" });
        var sessionId = Guid.NewGuid().ToString("n");
        var nonce = MeshCrypto.NewNonce();

        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce));
        var offersAfterFirstInit = transport.DecodedKinds()
            .Count(item => item.Kind == E2EFrameKind.Offer);
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce));

        Assert.IsTrue(offersAfterFirstInit > 0);
        Assert.AreEqual(
            offersAfterFirstInit,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.Offer),
            "a stable session retry must not repeat the full initial offer pass");
        Assert.AreEqual(
            2,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.SessionAck),
            "each retry still needs an acknowledgement in case the previous one was lost");
    }

    [TestMethod]
    public async Task Engine_SessionInitRetry_OffersAfterFirstAckDeliveryFails()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var ackAttempts = 0;
        transport.SendResult = frame =>
        {
            var decoded = ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext);
            if (decoded?.Kind == E2EFrameKind.SessionAck
                && Interlocked.Increment(ref ackAttempts) == 1)
            {
                return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
            }

            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        };
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        await engine.EmitLocalAsync(Env(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "ack-retry-message"), new[] { "bob" });
        var sessionId = Guid.NewGuid().ToString("n");
        var nonce = MeshCrypto.NewNonce();
        var init = BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce);

        await engine.HandleDeliveryAsync(init);
        Assert.AreEqual(
            0,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.Offer),
            "a failed acknowledgement must not start the initial offer pass");

        await engine.HandleDeliveryAsync(init);
        var offersAfterSuccessfulRetry = transport.DecodedKinds()
            .Count(item => item.Kind == E2EFrameKind.Offer);
        Assert.IsTrue(
            offersAfterSuccessfulRetry > 0,
            "the first successfully delivered acknowledgement must trigger initial offers");

        await engine.HandleDeliveryAsync(init);
        Assert.AreEqual(
            offersAfterSuccessfulRetry,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.Offer),
            "later stable retries must acknowledge without repeating initial offers");
        Assert.AreEqual(
            3,
            transport.DecodedKinds().Count(item => item.Kind == E2EFrameKind.SessionAck));
    }

    [TestMethod]
    public async Task Engine_SessionInitRetry_ReschedulesInitialOffersAfterCancellation()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));
        var transport = new FirstKindBlockingTransport(E2EFrameKind.Offer);
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        await engine.EmitLocalAsync(Env(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "cancelled-initial-offer"), new[] { "bob" });
        var sessionId = Guid.NewGuid().ToString("n");
        var nonce = MeshCrypto.NewNonce();
        var init = BuildSessionInit(
            peer, "bob", peerDevice, myDevice, mine.PublicB64, sessionId, nonce);
        using var cancellation = new CancellationTokenSource();

        var firstAttempt = engine.HandleDeliveryAsync(init, cancellation.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        try
        {
            await firstAttempt.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Fail("the blocked initial offer should observe cancellation");
        }
        catch (OperationCanceledException)
        {
        }

        await engine.HandleDeliveryAsync(init);

        Assert.AreEqual(
            2,
            transport.Sent.Count(frame =>
                ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext)?.Kind == E2EFrameKind.Offer),
            "a stable retry must reschedule the initial offer after cancellation");
    }

    [TestMethod]
    public async Task Engine_NotificationWakeIsWorthyOpaqueAndStableAcrossRetries()
    {
        var mine = KeyPair.New();
        var peerKeys = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peerKeys.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peerKeys.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var envelope = Env(
            ReplicationOpKinds.Topic,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "topic-notification") with
        {
            NotificationIntent = NotificationIntents.Message(
                "line-1", "bob", "Alice", "private body")
        };

        await engine.EmitLocalAsync(envelope, new[] { "bob" });
        await engine.OnWakeAsync("bob", peerDevice);
        await engine.OnWakeAsync("bob", peerDevice);

        Assert.AreEqual(2, transport.Wakes.Count);
        Assert.IsTrue(transport.Wakes.All(wake => wake.NotificationWorthy));
        Assert.AreEqual(transport.Wakes[0].WakeId, transport.Wakes[1].WakeId);
        Assert.AreEqual(OnlineReplicationLimits.HashHexLength, transport.Wakes[0].WakeId.Length);
        Assert.IsFalse(transport.Wakes[0].WakeId.Contains("line-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Engine_OwnerSiblingMessageUsesSilentStableWake()
    {
        var mine = KeyPair.New();
        var siblingKeys = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var siblingDevice = DeviceProtocol.DeviceId(siblingKeys.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", siblingDevice, siblingKeys.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var envelope = Env(
            ReplicationOpKinds.Topic,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "topic-owner-copy") with
        {
            NotificationIntent = NotificationIntents.Message(
                "line-owner", "bob", "Alice", "private body", suppressOnOriginAccount: true)
        };

        await engine.EmitLocalAsync(envelope, new[] { "alice" });
        await engine.OnWakeAsync("alice", siblingDevice);

        Assert.AreEqual(1, transport.Wakes.Count);
        Assert.IsFalse(transport.Wakes[0].NotificationWorthy);
    }

    [TestMethod]
    public async Task Engine_DoesNotWakeDeviceMissingFromEncryptedRecipientSlots()
    {
        var mine = KeyPair.New();
        var peerKeys = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peerKeys.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var envelope = Env(
            ReplicationOpKinds.Topic,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "topic-before-link") with
        {
            NotificationIntent = NotificationIntents.Message(
                "line-before-link", "bob", "Alice", "private body")
        };

        await engine.EmitLocalAsync(envelope, new[] { "bob" });
        roster.Add(new ReplicationDevice("bob", peerDevice, peerKeys.PublicB64, 0, false));
        await engine.OnWakeAsync("bob", peerDevice);

        Assert.AreEqual(0, transport.Wakes.Count);
    }

    [TestMethod]
    public async Task Engine_SessionInitFromAuthorizedPeer_RepliesWithAck()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);

        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("bob", peerDevice, peer.PublicB64, 0, false));

        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        var delivery = BuildSessionInit(peer, "bob", peerDevice, myDevice, mine.PublicB64);
        await engine.HandleDeliveryAsync(delivery);

        var kinds = transport.DecodedKinds();
        Assert.IsTrue(kinds.Any(k => k.Kind == E2EFrameKind.SessionAck && k.ToDevice == peerDevice),
            "a verified session init from an authorized peer must be answered with a session ack");
    }

    [TestMethod]
    public async Task Engine_BlockedReceiptDoesNotStarveFreshOffer()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new BlockingResolveRoster();
        _disposables.Add(roster);
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var sessionId = Guid.NewGuid().ToString("n");
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "alice", peerDevice, myDevice, mine.PublicB64, sessionId));

        var receipt = OnlineReplicationProtocol.CreateReceipt(
            peerDevice,
            myDevice,
            "epoch-1",
            1,
            OnlineReplicationProtocol.HashText("cursor"),
            OnlineReplicationProtocol.HashText("batch"),
            peer.PrivateB64);
        var receiptDelivery = BuildControlDelivery(
            peerDevice, myDevice, mine.PublicB64, sessionId, E2EFrameKind.Receipt, receipt);
        var offerDelivery = BuildControlDelivery(
            peerDevice,
            myDevice,
            mine.PublicB64,
            sessionId,
            E2EFrameKind.Offer,
            new ReplicationOffer(peerDevice, "epoch-1", 1, 1, null));

        roster.BlockNextResolve();
        var blockedReceipt = Task.Run(() => engine.HandleDeliveryAsync(receiptDelivery));
        await roster.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var freshOffer = engine.HandleDeliveryAsync(offerDelivery);

        var requestObservedWhileReceiptBlocked = false;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (transport.DecodedKinds().Any(frame => frame.Kind == E2EFrameKind.Request))
            {
                requestObservedWhileReceiptBlocked = true;
                break;
            }
            await Task.Delay(10);
        }

        roster.Release.TrySetResult(true);
        await Task.WhenAll(blockedReceipt, freshOffer).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(requestObservedWhileReceiptBlocked,
            "a queued receipt must not hold the peer data lane ahead of a fresh origin offer");
    }

    [TestMethod]
    public async Task Engine_FreshOfferBypassesQueuedBulkRequestWork()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new FirstTwoKindBlockingTransport(E2EFrameKind.Batch);
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        await engine.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "bulk-backlog"),
            new[] { "alice" });
        var sessionId = Guid.NewGuid().ToString("n");
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "alice", peerDevice, myDevice, mine.PublicB64, sessionId));

        var bulkRequest = new ReplicationRequest(
            myDevice,
            "epoch-1",
            new[] { new ReplicationRange(1, 1) });
        var firstBulk = engine.HandleDeliveryAsync(BuildControlDelivery(
            peerDevice, myDevice, mine.PublicB64, sessionId, E2EFrameKind.Request, bulkRequest));
        await transport.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondBulk = engine.HandleDeliveryAsync(BuildControlDelivery(
            peerDevice, myDevice, mine.PublicB64, sessionId, E2EFrameKind.Request, bulkRequest));
        var freshOffer = engine.HandleDeliveryAsync(BuildControlDelivery(
            peerDevice,
            myDevice,
            mine.PublicB64,
            sessionId,
            E2EFrameKind.Offer,
            new ReplicationOffer(peerDevice, "epoch-1", 1, 1, null)));

        try
        {
            transport.ReleaseFirst.TrySetResult(true);
            await transport.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsTrue(
                transport.DecodedKinds().Contains(E2EFrameKind.Request),
                "a fresh origin offer must run before already queued bulk request work");
        }
        finally
        {
            transport.ReleaseSecond.TrySetResult(true);
        }

        await Task.WhenAll(firstBulk, secondBulk, freshOffer).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Engine_PriorityOffersDoNotStarveQueuedBatch()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new BlockingResolveRoster();
        _disposables.Add(roster);
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var applier = new CapturingApplier();
        var engine = NewEngine("alice", myDevice, roster, transport, mine, applier);
        var sessionId = Guid.NewGuid().ToString("n");
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "alice", peerDevice, myDevice, mine.PublicB64, sessionId));

        var blockerRequest = new ReplicationRequest(
            myDevice,
            "epoch-1",
            new[] { new ReplicationRange(1, 1) });
        roster.BlockNextResolve();
        var blocker = Task.Run(() => engine.HandleDeliveryAsync(BuildControlDelivery(
            peerDevice,
            myDevice,
            mine.PublicB64,
            sessionId,
            E2EFrameKind.Request,
            blockerRequest)));
        await roster.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var queuedRequest = new ReplicationRequest(
            peerDevice,
            "epoch-1",
            new[] { new ReplicationRange(2, 2) });
        var normal = engine.HandleDeliveryAsync(BuildControlDelivery(
            peerDevice,
            myDevice,
            mine.PublicB64,
            sessionId,
            E2EFrameKind.Request,
            queuedRequest));
        var offers = Enumerable.Range(0, 8)
            .Select(_ => engine.HandleDeliveryAsync(BuildControlDelivery(
                peerDevice,
                myDevice,
                mine.PublicB64,
                sessionId,
                E2EFrameKind.Offer,
                new ReplicationOffer(peerDevice, "epoch-1", 1, 1, null))))
            .ToArray();

        var protocolFrames = 0;
        var requestsReceived = 0;
        var framesAtNormal = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Activity += activity =>
        {
            if (activity.Name == "protocol.frame_received")
                Interlocked.Increment(ref protocolFrames);
            else if (activity.Name == "request.received"
                     && Interlocked.Increment(ref requestsReceived) == 2)
                framesAtNormal.TrySetResult(Volatile.Read(ref protocolFrames));
        };

        roster.Release.TrySetResult(true);
        var observedFrames = await framesAtNormal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            2,
            observedFrames,
            "after one priority offer, an already-queued normal operation must run before newer offers");
        await Task.WhenAll(offers.Append(normal).Append(blocker)).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Engine_LocalEventOffersAllEstablishedPeersConcurrently()
    {
        var mine = KeyPair.New();
        var peerOne = KeyPair.New();
        var peerTwo = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerOneDevice = DeviceProtocol.DeviceId(peerOne.PublicB64);
        var peerTwoDevice = DeviceProtocol.DeviceId(peerTwo.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerOneDevice, peerOne.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerTwoDevice, peerTwo.PublicB64, 0, false));
        var transport = new ArmedFirstOfferBlockingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        await engine.HandleDeliveryAsync(BuildSessionInit(
            peerOne, "alice", peerOneDevice, myDevice, mine.PublicB64));
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peerTwo, "alice", peerTwoDevice, myDevice, mine.PublicB64));
        Assert.IsTrue(engine.IsSessionEstablished(peerOneDevice));
        Assert.IsTrue(engine.IsSessionEstablished(peerTwoDevice));

        transport.Arm();
        var emitted = engine.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "multi-peer-fresh-event"),
            new[] { "alice" });
        var blockedDevice = await transport.EnteredDevice.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var otherDevice = string.Equals(blockedDevice, peerOneDevice, StringComparison.Ordinal)
            ? peerTwoDevice
            : peerOneDevice;

        try
        {
            var otherOfferObserved = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                otherOfferObserved = transport.DecodedKinds().Any(item =>
                    item.Kind == E2EFrameKind.Offer
                    && string.Equals(item.ToDevice, otherDevice, StringComparison.Ordinal));
                if (otherOfferObserved) break;
                await Task.Delay(10);
            }

            Assert.IsTrue(
                otherOfferObserved,
                "one lagging sibling must not delay a fresh local event offer to another established peer");
        }
        finally
        {
            transport.Release.TrySetResult(true);
        }

        await emitted.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Engine_PersistedSiblingBootstrap_IsNotAttachedToIncrementalOffers()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var peerRecord = new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(peerRecord);
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var db = _dbs[^1];
        var target = ReplicationBootstrapTarget.Create(peerRecord, engine.LocalIdentity);
        const string bootstrapId = "persisted-bootstrap";
        var bootstrapEnvelope = Env(
            ReplicationOpKinds.Topic,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "bootstrap-topic");
        var snapshotJson = System.Text.Json.JsonSerializer.Serialize(new[] { bootstrapEnvelope });
        db.CreateOrResumePeerBootstrap(
            target,
            bootstrapId,
            OnlineReplicationProtocol.HashText(snapshotJson),
            snapshotJson,
            1);
        engine.Journal.EmitLocalBatch(
            new[] { bootstrapEnvelope },
            new[] { "alice" },
            domainWork: static (_, _, _) => { },
            eventWork: (_, tx, evt, _) =>
                db.UpdatePeerBootstrapProgress(target, bootstrapId, 1, 1, evt.Seq, evt.Seq, tx));
        var marker = db.GetPeerBootstrap(target)!;
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            peerDevice,
            myDevice,
            engine.LocalIdentity.LogEpoch,
            marker.BootstrapThroughSeq,
            OnlineReplicationProtocol.HashText("cursor"),
            OnlineReplicationProtocol.HashText("batch"),
            peer.PrivateB64);
        db.MarkOutboxPersistedFromReceipt(receipt, peer.PublicB64, "alice");
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, db.GetPeerBootstrap(target)!.State);
        await engine.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "post-bootstrap-topic"),
            new[] { "alice" });

        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "alice", peerDevice, myDevice, mine.PublicB64));

        var offers = transport.Sent
            .Select(item => ReplicationPayloadCodec.DecodeFrame(item.Ciphertext))
            .Where(frame => frame?.Kind == E2EFrameKind.Offer)
            .Cast<E2EFrame>()
            .Select(frame =>
            {
                var (decrypted, plaintext) = ReplicationPayloadCodec.TryDecrypt(
                    frame.Payload, peer.PrivateB64, peer.PublicB64);
                Assert.IsTrue(
                    decrypted,
                    "offer recipients: "
                    + string.Join(",", ReplicationPayloadCodec.RecipientDeviceIds(frame.Payload))
                    + $"; expected: {peerDevice}");
                return ReplicationPayloadCodec.DeserializeControl<ReplicationOffer>(plaintext!)
                       ?? throw new AssertFailedException("Offer payload was invalid.");
            })
            .ToList();

        Assert.IsTrue(offers.Count > 0);
        Assert.IsTrue(
            offers.All(offer => offer.Snapshot is null),
            "a peer that already receipted its bootstrap must receive incremental offers without the full snapshot manifest");
    }

    [TestMethod]
    public async Task Engine_RepeatedUpToDateOffersThrottleReceiptsButRetry()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine(
            "alice",
            myDevice,
            roster,
            transport,
            mine,
            receiptRetryInterval: TimeSpan.FromMilliseconds(40));
        var sessionId = Guid.NewGuid().ToString("n");
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer, "alice", peerDevice, myDevice, mine.PublicB64, sessionId));

        var cursor = new ReplicationCursorEntry(
            "epoch-1",
            1,
            new byte[OnlineReplicationLimits.AheadBitsBytes]);
        var db = _dbs[^1];
        db.UpsertCursor(peerDevice, cursor);
        db.StoreReceipt(OnlineReplicationProtocol.CreateReceipt(
            myDevice,
            peerDevice,
            "epoch-1",
            1,
            OnlineReplicationProtocol.ComputeCursorHash(cursor),
            OnlineReplicationProtocol.HashText("batch"),
            mine.PrivateB64));
        var offer = BuildControlDelivery(
            peerDevice,
            myDevice,
            mine.PublicB64,
            sessionId,
            E2EFrameKind.Offer,
            new ReplicationOffer(peerDevice, "epoch-1", 1, 1, null));
        var receiptsBefore = transport.DecodedKinds().Count(frame => frame.Kind == E2EFrameKind.Receipt);

        await engine.HandleDeliveryAsync(offer);
        await engine.HandleDeliveryAsync(offer);
        Assert.AreEqual(
            receiptsBefore + 1,
            transport.DecodedKinds().Count(frame => frame.Kind == E2EFrameKind.Receipt),
            "duplicate up-to-date offers must not create a receipt storm");

        await Task.Delay(80);
        await engine.HandleDeliveryAsync(offer);
        Assert.AreEqual(
            receiptsBefore + 2,
            transport.DecodedKinds().Count(frame => frame.Kind == E2EFrameKind.Receipt),
            "receipt suppression must expire so a lost acknowledgement is retried");
    }
    [TestMethod]
    public async Task Engine_RateLimitedPriorityOffer_StopsBeforeLowerPriorityOrigins()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var foreign = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var foreignDevice = DeviceProtocol.DeviceId(foreign.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", foreignDevice, foreign.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        await engine.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "priority-local"),
            new[] { "alice" });
        var foreignEnvelope = Env(
            ReplicationOpKinds.Topic,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "lower-priority-foreign");
        var foreignCiphertext = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.EncodeEnvelope(foreignEnvelope),
            new[] { mine.PublicB64, peer.PublicB64 });
        _dbs[^1].AppendEvent(OnlineReplicationProtocol.CreateEvent(
            foreignDevice,
            "foreign-epoch",
            1,
            "alice",
            0,
            foreignEnvelope.Kind,
            foreignEnvelope.EntityId,
            foreignEnvelope.ConversationId,
            foreignEnvelope.CausalVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            foreignCiphertext,
            foreign.PrivateB64));

        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer,
            "alice",
            peerDevice,
            myDevice,
            mine.PublicB64));
        var sentBefore = transport.Sent.Count;
        var offerAttempts = 0;
        transport.SendResult = frame =>
        {
            var decoded = ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext);
            if (decoded?.Kind == E2EFrameKind.Offer
                && Interlocked.Increment(ref offerAttempts) == 1)
            {
                return new OnlineRelaySendResult(
                    false,
                    OnlineRelaySendCodes.RateLimited,
                    RetryAfterMs: 500);
            }
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        };

        await engine.OfferPeerAsync("alice", peerDevice);

        var attemptedOffers = transport.Sent
            .Skip(sentBefore)
            .Select(frame => ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext))
            .Where(frame => frame?.Kind == E2EFrameKind.Offer)
            .Cast<E2EFrame>()
            .ToList();
        Assert.AreEqual(
            1,
            attemptedOffers.Count,
            "a rate-limited priority offer must stop the pass before historical origins spend later tokens");
        var (decrypted, plaintext) = ReplicationPayloadCodec.TryDecrypt(
            attemptedOffers[0].Payload,
            peer.PrivateB64,
            peer.PublicB64);
        Assert.IsTrue(decrypted);
        var offer = ReplicationPayloadCodec.DeserializeControl<ReplicationOffer>(plaintext!);
        Assert.IsNotNull(offer);
        Assert.AreEqual(
            myDevice,
            offer.OriginDeviceId,
            "the local origin must remain first in every offer pass");
    }

    [TestMethod]
    public async Task Engine_ReceiptedOrigin_IsNotReofferedOnEveryPoll()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        roster.Add(new ReplicationDevice("alice", peerDevice, peer.PublicB64, 0, false));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        await engine.EmitLocalAsync(
            Env(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "already-receipted"),
            new[] { "alice" });
        await engine.HandleDeliveryAsync(BuildSessionInit(
            peer,
            "alice",
            peerDevice,
            myDevice,
            mine.PublicB64));
        _dbs[^1].StoreReceipt(OnlineReplicationProtocol.CreateReceipt(
            peerDevice,
            myDevice,
            "epoch-1",
            1,
            OnlineReplicationProtocol.HashText("cursor"),
            OnlineReplicationProtocol.HashText("batch"),
            peer.PrivateB64));
        var offersBefore = transport.DecodedKinds().Count(frame => frame.Kind == E2EFrameKind.Offer);

        await engine.OfferPeerAsync("alice", peerDevice);

        Assert.AreEqual(
            offersBefore,
            transport.DecodedKinds().Count(frame => frame.Kind == E2EFrameKind.Offer),
            "a signed receipt already covering the held origin must suppress redundant offer traffic");
    }
    [TestMethod]
    public async Task Engine_SessionInitFromNewlyLinkedPeer_RefreshesStaleRosterAndReplies()
    {
        var mine = KeyPair.New();
        var peer = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peer.PublicB64);
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));

        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "alice" }, default);
        Assert.IsNull(roster.ResolveDevice("alice", peerDevice),
            "the cached roster must reproduce the pre-link view");

        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, peer.PublicB64));
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var delivery = BuildSessionInit(peer, "alice", peerDevice, myDevice, mine.PublicB64);

        await engine.HandleDeliveryAsync(delivery);

        Assert.AreEqual(2, source.FetchCount,
            "an unknown authenticated route must force one authoritative directory refresh");
        Assert.IsNotNull(roster.ResolveDevice("alice", peerDevice),
            "the linked peer must be installed in the roster before the held frame is revalidated");
        Assert.IsTrue(
            transport.DecodedKinds().Any(k =>
                k.Kind == E2EFrameKind.SessionAck && k.ToDevice == peerDevice),
            "the held session init must continue after the refreshed roster authorizes its sender");
    }

    [TestMethod]
    public async Task Engine_ThirdOriginBatch_RefreshesFreshStaleRosterBeforeVerification()
    {
        var mine = KeyPair.New();
        var holder = KeyPair.New();
        var origin = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var holderDevice = DeviceProtocol.DeviceId(holder.PublicB64);
        var originDevice = DeviceProtocol.DeviceId(origin.PublicB64);
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));
        source.SetHandle("bob", Dir("bob", 0, "", holder.PublicB64));
        source.SetHandle("carol", Dir("carol", 0, ""));

        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "alice", "bob", "carol" }, default);
        source.SetHandle("carol", Dir("carol", 0, "", origin.PublicB64));
        var transport = new RecordingTransport();
        var applier = new CapturingApplier();
        var engine = NewEngine("alice", myDevice, roster, transport, mine, applier);
        await engine.HandleDeliveryAsync(
            BuildSessionInit(holder, "bob", holderDevice, myDevice, mine.PublicB64));

        var envelope = Env(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.Upsert,
            "third-origin");
        var eventCiphertext = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.EncodeEnvelope(envelope), new[] { mine.PublicB64 });
        var evt = OnlineReplicationProtocol.CreateEvent(
            originDevice,
            "epoch-1",
            1,
            "carol",
            0,
            envelope.Kind,
            envelope.EntityId,
            envelope.ConversationId,
            envelope.CausalVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            eventCiphertext,
            origin.PrivateB64);
        var batch = new ReplicationBatch(originDevice, "epoch-1", new[] { evt });
        var batchCiphertext = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.SerializeControl(batch), new[] { mine.PublicB64 });
        var frame = new E2EFrame(E2EFrameKind.Batch, "holder-session", batchCiphertext);
        var delivery = new OnlineRelayDelivery(
            "bob",
            holderDevice,
            "alice",
            myDevice,
            Guid.NewGuid().ToString("n"),
            OnlinePushClasses.Normal,
            ReplicationPayloadCodec.EncodeFrame(frame));

        await engine.HandleDeliveryAsync(delivery);

        Assert.AreEqual(4, source.FetchCount,
            "the third-party origin must be authoritatively refreshed after the cached lookup misses");
        CollectionAssert.Contains(applier.EntityIds, "third-origin");
    }

    [TestMethod]
    public async Task Engine_UnregisteredPeer_RemainsRejectedAfterAuthoritativeRefresh()
    {
        var mine = KeyPair.New();
        var attacker = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var attackerDevice = DeviceProtocol.DeviceId(attacker.PublicB64);
        var source = new FakeMetadataSource();
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64));

        var roster = NewRoster(source, "alice", new List<string>());
        await roster.RefreshAsync(new[] { "alice" }, default);
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        await engine.HandleDeliveryAsync(
            BuildSessionInit(attacker, "alice", attackerDevice, myDevice, mine.PublicB64));

        Assert.AreEqual(2, source.FetchCount);
        Assert.AreEqual(0, transport.Sent.Count,
            "an authoritative refresh must never authorize a device absent from the directory");
        StringAssert.Contains(engine.LastError ?? "", "unauthorised");
    }

    [TestMethod]
    public async Task Engine_NewlyLinkedSibling_ConvergesBidirectionallyFromStaleRoster()
    {
        var a = KeyPair.New();
        var b = KeyPair.New();
        var aDevice = DeviceProtocol.DeviceId(a.PublicB64);
        var bDevice = DeviceProtocol.DeviceId(b.PublicB64);
        var aSource = new FakeMetadataSource();
        var bSource = new FakeMetadataSource();
        aSource.SetHandle("alice", Dir("alice", 0, "", a.PublicB64));
        bSource.SetHandle("alice", Dir("alice", 0, "", a.PublicB64, b.PublicB64));
        var aRoster = NewRoster(aSource, "alice", new List<string>());
        var bRoster = NewRoster(bSource, "alice", new List<string>());
        await aRoster.RefreshAsync(new[] { "alice" }, default);
        await bRoster.RefreshAsync(new[] { "alice" }, default);
        aSource.SetHandle("alice", Dir("alice", 0, "", a.PublicB64, b.PublicB64));

        var fabric = new ReplicationFabric();
        var aApplier = new CapturingApplier();
        var bApplier = new CapturingApplier();
        var aEngine = NewEngine(
            "alice", aDevice, aRoster, fabric.TransportFor("alice", aDevice), a, aApplier);
        var bEngine = NewEngine(
            "alice", bDevice, bRoster, fabric.TransportFor("alice", bDevice), b, bApplier);
        fabric.Register("alice", aDevice, aEngine);
        fabric.Register("alice", bDevice, bEngine);

        var fromB = await bEngine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "from-b"),
            new[] { "alice" });
        await bEngine.OnPresenceOnlineAsync("alice", aDevice);
        await fabric.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(aEngine.IsSessionEstablished(bDevice));
        Assert.IsTrue(bEngine.IsSessionEstablished(aDevice));
        CollectionAssert.Contains(aApplier.EntityIds, "from-b",
            "the existing device must materialize the newly linked sibling's event");
        Assert.AreEqual(ReplicationDeliveryState.Persisted, bEngine.GetDeliveryState(fromB, "alice"));

        var fromA = await aEngine.EmitLocalAsync(
            Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "from-a"),
            new[] { "alice" });
        await aEngine.OfferPeerAsync("alice", bDevice);
        await fabric.DrainAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.Contains(bApplier.EntityIds, "from-a",
            "the newly linked sibling must materialize the existing device's event");
        Assert.AreEqual(ReplicationDeliveryState.Persisted, aEngine.GetDeliveryState(fromA, "alice"));
    }

    [TestMethod]
    public async Task Engine_DeliveryFromUnauthorizedDevice_IsDropped()
    {
        var mine = KeyPair.New();
        var attacker = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        var attackerDevice = DeviceProtocol.DeviceId(attacker.PublicB64);

        var roster = new StubRoster();
        roster.Add(new ReplicationDevice("alice", myDevice, mine.PublicB64, 0, false));
        // Attacker's device is NOT in the roster.

        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);

        var delivery = BuildSessionInit(attacker, "bob", attackerDevice, myDevice, mine.PublicB64);
        await engine.HandleDeliveryAsync(delivery);

        Assert.AreEqual(0, transport.Sent.Count, "a delivery from an unauthorized device must be dropped with no reply");
    }

    private static OnlineRelayDelivery BuildControlDelivery<T>(
        string fromDevice,
        string toDevice,
        string toPublicKey,
        string sessionId,
        E2EFrameKind kind,
        T body)
    {
        var cipher = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.SerializeControl(body), new[] { toPublicKey });
        var frame = new E2EFrame(kind, sessionId, cipher);
        return new OnlineRelayDelivery(
            "alice", fromDevice, "alice", toDevice, Guid.NewGuid().ToString("n"),
            OnlinePushClasses.Normal, ReplicationPayloadCodec.EncodeFrame(frame));
    }
    private static OnlineRelayDelivery BuildSessionInit(
        KeyPair senderKeys,
        string fromHandle,
        string fromDevice,
        string toDevice,
        string toPublicKey,
        string? sessionId = null,
        string? nonce = null)
    {
        sessionId ??= Guid.NewGuid().ToString("n");
        var init = OnlineReplicationProtocol.CreateSessionInit(
            sessionId, fromDevice, toDevice, nonce ?? MeshCrypto.NewNonce(),
            OnlineReplicationProtocol.ZeroHash, 0, senderKeys.PrivateB64);
        var cipher = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.SerializeControl(init), new[] { toPublicKey });
        var frame = new E2EFrame(E2EFrameKind.SessionInit, sessionId, cipher);
        return new OnlineRelayDelivery(
            fromHandle, fromDevice, "alice", toDevice, Guid.NewGuid().ToString("n"),
            OnlinePushClasses.High, ReplicationPayloadCodec.EncodeFrame(frame));
    }

    // =====================================================================
    // No dead external configuration seam.
    // =====================================================================

    [TestMethod]
    public void Engine_HasNoPublicParameterlessOrExternalConfigConstructor()
    {
        var ctors = typeof(OnlineReplicationEngine).GetConstructors();
        Assert.IsFalse(ctors.Any(c => c.GetParameters().Length == 0),
            "the engine must be armed through injected identity/roster/transport, not a parameterless external seam");
        Assert.IsTrue(ctors.All(c =>
            c.GetParameters().Any(p => p.ParameterType == typeof(ReplicationIdentity))
            && c.GetParameters().Any(p => p.ParameterType == typeof(IReplicationTransport))
            && c.GetParameters().Any(p => p.ParameterType == typeof(IReplicationRoster))),
            "every engine constructor must require real identity, transport and roster");
    }

    [TestMethod]
    public void Emission_JournalEntryPointsAreInternalAndProductionHasNoDirectBypass()
    {
        var publicMethods = typeof(ReplicationJournal)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(publicMethods, "EmitLocal");
        CollectionAssert.DoesNotContain(publicMethods, "EmitLocalAsync");
        CollectionAssert.DoesNotContain(publicMethods, "EmitLocalBatch");
        CollectionAssert.DoesNotContain(publicMethods, "EmitLocalBatchAsync");

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mesh.slnx")))
            root = root.Parent;
        Assert.IsNotNull(root, "repository root was not found");
        var services = Path.Combine(root.FullName, "src", "Mesh.App", "Services");
        var bypasses = Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("ReplicationJournal.cs", StringComparison.Ordinal)
                           && !path.EndsWith("OnlineReplicationEngine.cs", StringComparison.Ordinal)
                           && !path.EndsWith("AppState.OnlineReplication.cs", StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("EnsureLocalJournal().Emit", StringComparison.Ordinal)
                       || source.Contains(".Journal.EmitLocal", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), bypasses);
    }

    [TestMethod]
    public void RuntimeTypes_ExposeNoPublicConfigureReplicationSeam()
    {
        foreach (var t in new[]
                 {
                     typeof(OnlineReplicationEngine), typeof(RelayReplicationRoster),
                     typeof(ReplicationPresencePoller), typeof(ReplicationPayloadCodec),
                 })
        {
            var bad = t.GetMethods()
                .Where(m => m.IsPublic)
                .Where(m => m.Name.Contains("Configure", StringComparison.OrdinalIgnoreCase)
                    || m.Name.Contains("EnsureReplicationEngine", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.AreEqual(0, bad.Length, $"{t.Name} exposes a dead external configuration seam: {string.Join(",", bad.Select(m => m.Name))}");
        }
    }

    // =====================================================================
    // Real relay hub round trip: production canonical drives a real connect + ResolvePresence.
    // =====================================================================

    [TestMethod]
    public async Task RealHub_ConnectWithProductionCanonical_ThenResolvePresence()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRelayStore>(store);
        builder.Services.AddSingleton<IBackplane>(backplane);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<MeshRouter>();
        builder.Services.AddSingleton(new Mesh.Relay.LiveFaults.LiveFaultStore(
            new Mesh.Relay.LiveFaults.LiveFaultOptions { Enabled = false }));
        builder.Services.AddSingleton<Mesh.Relay.LiveFaults.LiveFaultHandshakeObserver>();
        builder.Services.AddSingleton<Mesh.Relay.LiveFaults.LiveFaultTransportObserver>();
        builder.Services.AddSingleton<RelayFrameDedup>();
        builder.Services.AddSingleton(new RelayMetrics());
        builder.Services.AddSingleton<PushDispatcher>();
        builder.Services.AddSingleton<IMessageRateLimiter, AllowAllRateLimiter>();
        builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = OnlineReplicationLimits.MaxTransportBytes + 64 * 1024);

        var app = builder.Build();
        app.MapHub<MeshHub>("/hub");
        var router = app.Services.GetRequiredService<MeshRouter>();
        await backplane.StartAsync(router.DeliverFromBackplaneAsync);
        await app.StartAsync();
        var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        HubConnection? conn = null;
        try
        {
            // Two authorized sibling devices under one account.
            var aKeys = KeyPair.New();
            var bKeys = KeyPair.New();
            var aDevice = DeviceProtocol.DeviceId(aKeys.PublicB64);
            var bDevice = DeviceProtocol.DeviceId(bKeys.PublicB64);
            await store.UpsertHandleAsync("alice", aKeys.PublicB64, "display", allowNewDevice: true);
            await store.SetDeviceMetadataAsync("alice", aDevice, "a", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
            await store.UpsertHandleAsync("alice", bKeys.PublicB64, "display", allowNewDevice: true);
            await store.SetDeviceMetadataAsync("alice", bDevice, "b", DevicePlatforms.IOS, false, false, MeshProtocol.Version);

            var record = (await store.GetHandleAsync("alice"))!;

            conn = await ConnectWithProductionCanonical(baseUrl, "alice", aDevice, aKeys, record.AuthGeneration, record.CustodyHead);
            var bConn = await ConnectWithProductionCanonical(baseUrl, "alice", bDevice, bKeys, record.AuthGeneration, record.CustodyHead);

            var snapshot = await conn.InvokeAsync<OnlinePresenceSnapshot>(OnlineRelayMethods.ResolvePresence, new[] { "alice" });
            var presence = snapshot.Handles.Single(h => h.Handle == "alice");
            Assert.IsTrue(presence.Online);
            CollectionAssert.Contains(presence.Devices.ToArray(), aDevice);
            CollectionAssert.Contains(presence.Devices.ToArray(), bDevice);

            await bConn.DisposeAsync();
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<HubConnection> ConnectWithProductionCanonical(
        string baseUrl, string handle, string device, KeyPair keys, long authGeneration, string custodyHead)
    {
        var url =
            $"{baseUrl}/hub?handle={Uri.EscapeDataString(handle)}" +
            $"&deviceId={Uri.EscapeDataString(device)}" +
            $"&protocolVersion={MeshProtocol.Version}" +
            $"&authGeneration={authGeneration}" +
            $"&custodyHead={Uri.EscapeDataString(custodyHead)}";

        var conn = new HubConnectionBuilder().WithUrl(url).Build();
        var presence = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        conn.On<string>(MeshHubProtocol.Challenge, nonce =>
        {
            // The production replica of the relay canonical: this is exactly what MeshClient signs.
            var canonical = ReplicationConnectChallenge.Canonical(
                nonce, handle, device, MeshProtocol.Version, authGeneration, custodyHead);
            return conn.SendAsync(MeshHubProtocol.Authenticate, keys.PublicB64, Sign(keys.PrivateB64, canonical));
        });
        conn.On<PresenceConfirmed>(MeshHubProtocol.PresenceConfirmed, _ => presence.TrySetResult(true));

        await conn.StartAsync();
        await presence.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return conn;
    }

    private static string Sign(string privateKeyB64, string message)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    private sealed class AllowAllRateLimiter : IMessageRateLimiter
    {
        private static readonly HandleRatePolicy Policy = new(1_000_000, 1_000_000, 1_000_000, 1_000_000, 4096);

        public Task<(RateLimitDecision Decision, HandleRatePolicy Policy)> TryAcquireAsync(
            string handle, MessageRateBucket bucket, CancellationToken ct = default)
            => Task.FromResult((new RateLimitDecision(true, 0, 1_000_000), Policy));
    }
}
