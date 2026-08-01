using System.Security.Cryptography;
using System.Text;
using Mesh.App.Services;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
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
        public OnlineRelaySendResult Result = new(true, OnlineRelaySendCodes.Delivered);

        public Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
        {
            lock (sent) sent.Add(frame);
            return Task.FromResult(Result);
        }

        public IReadOnlyList<OnlineRelayFrame> Sent { get { lock (sent) return sent.ToList(); } }

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

    private sealed class FakeMetadataSource : IReplicationMetadataSource
    {
        private readonly object gate = new();
        private readonly Dictionary<string, HandleInfo> handles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (bool Online, List<string> Devices)> presence = new(StringComparer.Ordinal);

        public int FetchCount;
        public int ResolveCount;

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

        public Task<HandleInfo?> FetchHandleAsync(string handle, CancellationToken ct)
        {
            Interlocked.Increment(ref FetchCount);
            lock (gate) return Task.FromResult(handles.TryGetValue(handle, out var i) ? i : null);
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
        public bool Apply(SqliteConnection conn, SqliteTransaction tx, ReplicationEvent evt,
            ReplicationPayloadCodec.DomainEnvelope envelope, bool deviceIsDesktop)
        {
            Interlocked.Increment(ref Count);
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
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private OnlineReplicationEngine NewEngine(
        string handle, string device, IReplicationRoster roster, IReplicationTransport transport,
        KeyPair keys, IReplicationDomainApplier? applier = null, bool desktop = true)
    {
        var db = MeshDb.Open(Path.Combine(_root, device + ".meshdb"), DbKey);
        _dbs.Add(db);
        var identity = new ReplicationIdentity(
            handle, device, keys.PublicB64, keys.PrivateB64, "epoch-1", 0, OnlineReplicationProtocol.ZeroHash);
        var engine = new OnlineReplicationEngine(
            db, identity, transport, roster, applier ?? new CapturingApplier(),
            deviceIsDesktop: desktop, sendTimeout: TimeSpan.FromSeconds(2), maxSendAttempts: 1);
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
        Func<string, string?>? localCustody = null, long ownAuthGen = 0, string ownCustody = "")
        => new(source, ownHandle, ownAuthGen, ownCustody,
            localCustody ?? (_ => ""), s => { lock (surfaced) surfaced.Add(s); },
            onAuthorityChanged ?? (() => { }));

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
    public async Task Poller_OfflineHandle_StartsNoSession()
    {
        var source = new FakeMetadataSource();
        var mine = KeyPair.New();
        var sib = KeyPair.New();
        var myDevice = DeviceProtocol.DeviceId(mine.PublicB64);
        source.SetHandle("alice", Dir("alice", 0, "", mine.PublicB64, sib.PublicB64));
        source.SetPresence("alice", false, DeviceProtocol.DeviceId(sib.PublicB64));

        var roster = NewRoster(source, "alice", new List<string>());
        var transport = new RecordingTransport();
        var engine = NewEngine("alice", myDevice, roster, transport, mine);
        var poller = NewPoller(engine, roster, source, "alice", myDevice, () => new[] { "alice" });

        var pending = await poller.PollOnceAsync(default);

        Assert.AreEqual(0, transport.Sent.Count, "offline peers must leave the outbox pending, not start a session");
        Assert.IsFalse(pending, "no online peer and no due outbox means no pending work");
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

    private static OnlineRelayDelivery BuildSessionInit(
        KeyPair senderKeys, string fromHandle, string fromDevice, string toDevice, string toPublicKey)
    {
        var sessionId = Guid.NewGuid().ToString("n");
        var init = OnlineReplicationProtocol.CreateSessionInit(
            sessionId, fromDevice, toDevice, MeshCrypto.NewNonce(),
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
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<MeshRouter>();
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
