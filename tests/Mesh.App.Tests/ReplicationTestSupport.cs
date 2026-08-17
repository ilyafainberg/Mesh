using System.Security.Cryptography;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;

namespace Mesh.App.Tests;

/// <summary>
/// Shared harness for protocol-9 online-replication engine tests. It wires two or more
/// real <see cref="OnlineReplicationEngine"/> instances (each over its own real
/// <see cref="MeshDb"/>) to an in-memory relay <see cref="ReplicationFabric"/> that
/// forwards opaque frames exactly like the real relay: a pure, best-effort forwarder with
/// no reliable queue. Sends to an offline device return <c>not_online</c> and are dropped,
/// leaving the sender's outbox pending, mirroring the greenfield online-only contract.
/// </summary>
internal sealed record KeyPair(string PrivateB64, string PublicB64)
{
    public static KeyPair New()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new KeyPair(
            Convert.ToBase64String(ec.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }
}

/// <summary>A shared, authoritative device roster / custody generation view for all engines.</summary>
internal sealed class FabricRoster : IRefreshableReplicationRoster
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<ReplicationDevice>> byAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> generation = new(StringComparer.Ordinal);
    public int RefreshCalls { get; private set; }

    public void Add(string account, ReplicationDevice device)
    {
        lock (gate)
        {
            if (!byAccount.TryGetValue(account, out var list)) { list = new(); byAccount[account] = list; }
            list.RemoveAll(d => string.Equals(d.DeviceId, device.DeviceId, StringComparison.Ordinal));
            list.Add(device);
            generation.TryAdd(account, device.AuthGeneration);
        }
    }

    public void SetGeneration(string account, long value)
    {
        lock (gate) generation[account] = value;
    }

    public void Revoke(string account, string deviceId)
    {
        lock (gate)
        {
            if (!byAccount.TryGetValue(account, out var list)) return;
            var i = list.FindIndex(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
            if (i >= 0) list[i] = list[i] with { Revoked = true };
        }
    }

    public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string accountHandle)
    {
        lock (gate)
            return byAccount.TryGetValue(accountHandle, out var list)
                ? list.Where(d => !d.Revoked).ToList()
                : Array.Empty<ReplicationDevice>();
    }

    public ReplicationDevice? ResolveDevice(string accountHandle, string deviceId)
    {
        lock (gate)
            return byAccount.TryGetValue(accountHandle, out var list)
                ? list.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal))
                : null;
    }

    public long AuthGeneration(string accountHandle)
    {
        lock (gate) return generation.TryGetValue(accountHandle, out var g) ? g : 0;
    }

    public Task RefreshAsync(IReadOnlyList<string> handles, CancellationToken ct)
    {
        RefreshCalls++;
        return Task.CompletedTask;
    }

    public Task RefreshAuthoritativeAsync(IReadOnlyList<string> handles, CancellationToken ct)
    {
        RefreshCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>Records every inbound domain projection and can run an in-transaction side effect.</summary>
internal sealed class RecordingApplier : IReplicationDomainApplier
{
    public readonly List<(ReplicationEvent Evt, ReplicationPayloadCodec.DomainEnvelope Env, bool Desktop)> Applied = new();
    public Action<SqliteConnection, SqliteTransaction, ReplicationEvent, ReplicationPayloadCodec.DomainEnvelope>? OnApply;
    public bool ApplyResult = true;

    public bool Apply(
        SqliteConnection conn,
        SqliteTransaction tx,
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop)
    {
        OnApply?.Invoke(conn, tx, evt, envelope);
        lock (Applied) Applied.Add((evt, envelope, deviceIsDesktop));
        return ApplyResult;
    }

    public int Count { get { lock (Applied) return Applied.Count; } }

    /// <summary>Post-commit hook: fired only after the inbound transaction has committed.</summary>
    public Action<ReplicationEvent, ReplicationPayloadCodec.DomainEnvelope>? OnAfterCommit;
    public int AfterCommitBatchCalls;

    public Task AfterCommitAsync(
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop)
    {
        OnAfterCommit?.Invoke(evt, envelope);
        return Task.CompletedTask;
    }

    public async Task AfterCommitBatchAsync(
        IReadOnlyList<ReplicationCommittedDomainEvent> committed,
        bool deviceIsDesktop)
    {
        Interlocked.Increment(ref AfterCommitBatchCalls);
        foreach (var item in committed)
            await AfterCommitAsync(item.Event, item.Envelope, deviceIsDesktop).ConfigureAwait(false);
    }
}

/// <summary>In-memory relay fabric: forwards frames between engines with no reliable storage.</summary>
internal sealed class ReplicationFabric
{
    private readonly object gate = new();
    private readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
    private readonly Queue<Pending> queue = new();

    public int Delivered { get; private set; }
    public int DroppedOffline { get; private set; }
    public int DroppedAccepted { get; private set; }
    public int Unknown { get; private set; }
    public Func<string, OnlineRelayFrame, bool>? DropFrame { get; set; }
    public Func<string, OnlineRelayFrame, bool>? DropAcceptedFrame { get; set; }

    private sealed class Node(string handle, string device, OnlineReplicationEngine engine)
    {
        public string Handle { get; } = handle;
        public string Device { get; } = device;
        public OnlineReplicationEngine Engine { get; } = engine;
        public bool Online { get; set; } = true;
    }

    private readonly record struct Pending(OnlineReplicationEngine Target, OnlineRelayDelivery Delivery);

    public void Register(string handle, string device, OnlineReplicationEngine engine)
    {
        lock (gate) nodes[device] = new Node(handle, device, engine);
    }

    public void SetOnline(string device, bool online)
    {
        lock (gate) { if (nodes.TryGetValue(device, out var n)) n.Online = online; }
    }

    public IReplicationTransport TransportFor(string handle, string device) => new FabricTransport(this, handle, device);

    private OnlineRelaySendResult Enqueue(string fromHandle, string fromDevice, OnlineRelayFrame frame)
    {
        lock (gate)
        {
            if (DropFrame?.Invoke(fromDevice, frame) == true)
            {
                DroppedOffline++;
                return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
            }
            if (frame.ToDevice is null || !nodes.TryGetValue(frame.ToDevice, out var target))
            {
                Unknown++;
                return new OnlineRelaySendResult(false, OnlineRelaySendCodes.TargetDeviceUnknown);
            }
            if (!target.Online)
            {
                DroppedOffline++;
                return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
            }
            if (DropAcceptedFrame?.Invoke(fromDevice, frame) == true)
            {
                DroppedAccepted++;
                Delivered++;
                return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
            }
            var delivery = new OnlineRelayDelivery(
                fromHandle, fromDevice, frame.ToHandle, frame.ToDevice, frame.FrameId, frame.PushClass, frame.Ciphertext);
            queue.Enqueue(new Pending(target.Engine, delivery));
            Delivered++;
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }
    }

    /// <summary>Processes every queued delivery until the network quiesces (no more frames).</summary>
    public async Task DrainAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            Pending item;
            lock (gate)
            {
                if (queue.Count == 0) return;
                item = queue.Dequeue();
            }
            await item.Target.HandleDeliveryAsync(item.Delivery).ConfigureAwait(false);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Replication did not quiesce within the deadline.");
        }
    }

    public int PendingCount { get { lock (gate) return queue.Count; } }

    private sealed class FabricTransport(ReplicationFabric fabric, string handle, string device) : IReplicationTransport
    {
        public Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct)
            => Task.FromResult(fabric.Enqueue(handle, device, frame));

        public Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct)
            => Task.FromResult(new OnlineWakeResult(true, OnlineWakeCodes.Accepted));
    }
}

/// <summary>One replication participant: a real MeshDb + engine + applier bound to the fabric.</summary>
internal sealed class ReplicationNode : IAsyncDisposable
{
    public required string Handle { get; init; }
    public required string Device { get; init; }
    public required KeyPair Keys { get; init; }
    public required MeshDb Db { get; init; }
    public required OnlineReplicationEngine Engine { get; init; }
    public required RecordingApplier Applier { get; init; }
    public required bool Desktop { get; init; }
    public required string DbPath { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync().ConfigureAwait(false);
        Db.Dispose();
    }
}

/// <summary>Base class providing temp-db lifecycle and node creation for engine test suites.</summary>
public abstract class ReplicationTestBase
{
    private string root = null!;
    private byte[] key = null!;
    private readonly List<ReplicationNode> nodes = new();
    private readonly List<MeshDb> extraDbs = new();

    private protected FabricRoster Roster { get; private set; } = null!;
    private protected ReplicationFabric Fabric { get; private set; } = null!;

    [Microsoft.VisualStudio.TestTools.UnitTesting.TestInitialize]
    public void BaseInitialize()
    {
        root = Path.Combine(AppContext.BaseDirectory, "online-replication-engine", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        key = Enumerable.Range(7, 32).Select(v => (byte)v).ToArray();
        Roster = new FabricRoster();
        Fabric = new ReplicationFabric();
    }

    [Microsoft.VisualStudio.TestTools.UnitTesting.TestCleanup]
    public async Task BaseCleanup()
    {
        foreach (var node in nodes)
            await node.DisposeAsync();
        nodes.Clear();
        foreach (var db in extraDbs)
            db.Dispose();
        extraDbs.Clear();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private protected ReplicationNode NewNode(
        string handle,
        string device,
        bool desktop = true,
        long authGeneration = 0,
        string logEpoch = "epoch-1",
        int maxSendAttempts = 3,
        TimeSpan? sendTimeout = null,
        ReplicationFlow? flow = null,
        TimeSpan? requestRetryInterval = null,
        bool deferSiblingOffersUntilBootstrap = false)
    {
        var keys = KeyPair.New();
        var dbPath = Path.Combine(root, device + ".meshdb");
        var db = MeshDb.Open(dbPath, key);
        var applier = new RecordingApplier();
        var identity = new ReplicationIdentity(
            handle, device, keys.PublicB64, keys.PrivateB64, logEpoch, authGeneration, OnlineReplicationProtocol.ZeroHash);
        var engine = new OnlineReplicationEngine(
            db, identity, Fabric.TransportFor(handle, device), Roster, applier,
            deviceIsDesktop: desktop,
            sendTimeout: sendTimeout ?? TimeSpan.FromSeconds(5),
            maxSendAttempts: maxSendAttempts,
            flow: flow,
            requestRetryInterval: requestRetryInterval,
            deferSiblingOffersUntilBootstrap: deferSiblingOffersUntilBootstrap);
        engine.EnsureLocalOrigin();
        Roster.Add(handle, new ReplicationDevice(handle, device, keys.PublicB64, authGeneration, Revoked: false));
        Fabric.Register(handle, device, engine);
        var node = new ReplicationNode
        {
            Handle = handle, Device = device, Keys = keys, Db = db,
            Engine = engine, Applier = applier, Desktop = desktop, DbPath = dbPath,
        };
        nodes.Add(node);
        return node;
    }

    /// <summary>Reopens a node's database + engine in place, simulating a crash / restart.</summary>
    private protected ReplicationNode Reopen(ReplicationNode node)
    {
        nodes.Remove(node);
        node.Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        node.Db.Dispose();
        SqliteConnection.ClearAllPools();
        var db = MeshDb.Open(node.DbPath, key);
        var applier = new RecordingApplier();
        var identity = new ReplicationIdentity(
            node.Handle, node.Device, node.Keys.PublicB64, node.Keys.PrivateB64,
            "epoch-1", 0, OnlineReplicationProtocol.ZeroHash);
        var engine = new OnlineReplicationEngine(
            db, identity, Fabric.TransportFor(node.Handle, node.Device), Roster, applier, deviceIsDesktop: node.Desktop);
        engine.EnsureLocalOrigin();
        Fabric.Register(node.Handle, node.Device, engine);
        var reopened = new ReplicationNode
        {
            Handle = node.Handle, Device = node.Device, Keys = node.Keys, Db = db,
            Engine = engine, Applier = applier, Desktop = node.Desktop, DbPath = node.DbPath,
        };
        nodes.Add(reopened);
        return reopened;
    }

    private protected static ReplicationPayloadCodec.DomainEnvelope Msg(
        string entityId,
        string conversationId = "conv-1",
        string body = "{\"text\":\"hi\"}",
        string kind = ReplicationOpKinds.Message,
        ReplicationPayloadCodec.DomainAction action = ReplicationPayloadCodec.DomainAction.Upsert,
        string causal = "v1")
        => new(kind, action, entityId, conversationId, causal, body);

    /// <summary>Brings two peers online to each other and drives the full symmetric exchange.</summary>
    private protected async Task ConnectAsync(ReplicationNode a, ReplicationNode b)
    {
        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await b.Engine.OnPresenceOnlineAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();
    }

    /// <summary>
    /// A bare offline replication participant: a real <see cref="MeshDb"/> and a
    /// <see cref="ReplicationJournal"/> with no engine and no transport at all. Used to prove a
    /// local change is journaled with no relay/engine present (spec item 1: never no-ops).
    /// </summary>
    private protected sealed record OfflineJournal(
        MeshDb Db, ReplicationJournal Journal, KeyPair Keys, ReplicationIdentity Identity, string Handle, string Device);

    /// <summary>
    /// Opens a fresh account database, onboards it (genesis custody) unless suppressed, and
    /// returns a journal bound to it. No <see cref="OnlineReplicationEngine"/> is created.
    /// </summary>
    private protected OfflineJournal NewJournal(
        string handle,
        string device,
        bool desktop = true,
        long authGeneration = 0,
        string logEpoch = "epoch-1",
        bool initCustody = true,
        string? custodyHeadOverride = null)
    {
        var keys = KeyPair.New();
        var dbPath = Path.Combine(root, device + ".journal.meshdb");
        var db = MeshDb.Open(dbPath, key);
        extraDbs.Add(db);
        Roster.Add(handle, new ReplicationDevice(handle, device, keys.PublicB64, authGeneration, Revoked: false));
        if (Roster.AuthGeneration(handle) < authGeneration) Roster.SetGeneration(handle, authGeneration);
        var custodyHead = custodyHeadOverride ?? (initCustody
            ? db.InitializeGenesisCustody(handle, keys.PublicB64, keys.PrivateB64)
            : OnlineReplicationProtocol.ZeroHash);
        var identity = new ReplicationIdentity(
            handle, device, keys.PublicB64, keys.PrivateB64, logEpoch, authGeneration, custodyHead);
        var journal = new ReplicationJournal(db, identity, Roster, desktop);
        journal.EnsureLocalOrigin();
        return new OfflineJournal(db, journal, keys, identity, handle, device);
    }

    /// <summary>
    /// Registers a fresh sibling device for an account in the roster and returns its keys, so
    /// an own-account target has an authorised sibling to take custody (spec item 5).
    /// </summary>
    private protected KeyPair AddSibling(string account, string device, long authGeneration = 0)
    {
        var keys = KeyPair.New();
        Roster.Add(account, new ReplicationDevice(account, device, keys.PublicB64, authGeneration, Revoked: false));
        return keys;
    }

    /// <summary>Wires a node's inbound applier to materialise domain state via the codec projection.</summary>
    private protected static void UseProjectingApplier(ReplicationNode node)
        => node.Applier.OnApply = (conn, tx, evt, env)
            => ReplicationPayloadCodec.Project(conn, tx, evt, env, node.Desktop);

    /// <summary>Builds a neutral domain envelope for an arbitrary kind/action.</summary>
    private protected static ReplicationPayloadCodec.DomainEnvelope Env(
        string kind,
        ReplicationPayloadCodec.DomainAction action,
        string entityId,
        string? conversationId = null,
        string causal = "v1",
        string body = "{}")
        => new(kind, action, entityId, conversationId, causal, body);

    // -----------------------------------------------------------------------
    // Low-level crafting helpers: build a synthetic origin (with a controlled
    // signing key registered in the roster) and hand-deliver arbitrary frames so
    // receiver logic (ordering, gaps, duplicates, forks, receipts, revocation)
    // can be probed precisely and adversarially, independent of a live emitter.
    // -----------------------------------------------------------------------

    /// <summary>Registers a synthetic origin device in the roster and returns its signing key pair.</summary>
    private protected KeyPair AddOrigin(string account, string device, long authGeneration = 0)
    {
        var keys = KeyPair.New();
        Roster.Add(account, new ReplicationDevice(account, device, keys.PublicB64, authGeneration, Revoked: false));
        if (Roster.AuthGeneration(account) < authGeneration) Roster.SetGeneration(account, authGeneration);
        return keys;
    }

    /// <summary>Builds a signed replication event for a synthetic origin, encrypted to the given recipients.</summary>
    private protected static ReplicationEvent MakeEvent(
        KeyPair originKeys,
        string account,
        string device,
        ulong seq,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> recipientPubs,
        string epoch = "epoch-1",
        long authGeneration = 0)
    {
        var cipher = ReplicationPayloadCodec.Encrypt(ReplicationPayloadCodec.EncodeEnvelope(envelope), recipientPubs);
        return OnlineReplicationProtocol.CreateEvent(
            device, epoch, seq, account, authGeneration,
            envelope.Kind, envelope.EntityId, envelope.ConversationId, envelope.CausalVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cipher, originKeys.PrivateB64);
    }

    /// <summary>Wraps events into a single-origin batch.</summary>
    private protected static ReplicationBatch Batch(
        string originDevice, IReadOnlyList<ReplicationEvent> events, string epoch = "epoch-1")
        => new(originDevice, epoch, events);

    /// <summary>
    /// Encrypts and hand-delivers one control frame from <paramref name="from"/> to
    /// <paramref name="to"/> as if the relay forwarded it, bypassing the fabric queue so the
    /// caller controls exact ordering. The frame body is encrypted to the target device key.
    /// </summary>
    private protected static Task RawControlAsync<T>(
        ReplicationNode from, ReplicationNode to, E2EFrameKind kind, T control, string sessionId = "s")
    {
        var cipher = ReplicationPayloadCodec.Encrypt(
            ReplicationPayloadCodec.SerializeControl(control), new[] { to.Keys.PublicB64 });
        var frame = new E2EFrame(kind, sessionId, cipher);
        var delivery = new OnlineRelayDelivery(
            from.Handle, from.Device, to.Handle, to.Device, Guid.NewGuid().ToString("n"),
            OnlinePushClasses.Normal, ReplicationPayloadCodec.EncodeFrame(frame));
        return to.Engine.HandleDeliveryAsync(delivery);
    }

    /// <summary>Delivers a batch of events for a (possibly synthetic) origin over an established peer session.</summary>
    private protected static Task DeliverBatchAsync(
        ReplicationNode from, ReplicationNode to, ReplicationBatch batch, string sessionId = "s")
        => RawControlAsync(from, to, E2EFrameKind.Batch, batch, sessionId);

    /// <summary>Establishes a session between two peers with no serveable origins (a clean handshake, no data).</summary>
    private protected async Task EstablishAsync(ReplicationNode a, ReplicationNode b)
    {
        await a.Engine.StartSessionAsync(b.Handle, b.Device);
        await b.Engine.StartSessionAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();
    }
}
