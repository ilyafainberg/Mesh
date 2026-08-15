using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mesh.Shared;

namespace Mesh.App.Services;

// ===========================================================================
// Seams. The engine owns replication decisions and transport orchestration but
// depends only on these interfaces, so it can be driven end-to-end in tests with
// two in-memory engines and refreshed from real relay/custody metadata in the app.
// ===========================================================================

/// <summary>This device's authoritative replication identity and signing material.</summary>
public sealed record ReplicationIdentity(
    string Handle,
    string DeviceId,
    string PublicKeyB64,
    string PrivateKeyB64,
    string LogEpoch,
    long AuthGeneration,
    string CustodyHead);

public sealed record ReplicationBootstrapTarget(
    string PeerHandle,
    string PeerDeviceId,
    string PeerKeyHash,
    long AuthGeneration,
    string LocalOriginDeviceId,
    string LocalLogEpoch)
{
    public static ReplicationBootstrapTarget Create(
        ReplicationDevice peer,
        ReplicationIdentity localIdentity)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(localIdentity);
        var keyBytes = Convert.FromBase64String(peer.PublicKeyB64);
        var keyHash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        return new ReplicationBootstrapTarget(
            ReplicationHandle.Norm(peer.Handle),
            peer.DeviceId,
            keyHash,
            peer.AuthGeneration,
            localIdentity.DeviceId,
            localIdentity.LogEpoch);
    }
}

public sealed record ReplicationProgressSnapshot(
    long ActivityVersion,
    long CommittedEvents,
    DateTimeOffset? LastActivity);

public sealed record ReplicationEngineActivity(
    string Name,
    string? PeerHandle = null,
    string? PeerDeviceId = null,
    int EventCount = 0,
    long ByteCount = 0,
    string? ErrorCode = null,
    string? BootstrapId = null);

/// <summary>One authorised device of some account, as known from custody / relay metadata.</summary>
public sealed record ReplicationDevice(
    string Handle,
    string DeviceId,
    string PublicKeyB64,
    long AuthGeneration,
    bool Revoked);

/// <summary>
/// Read-only view of the authoritative device roster the engine consumes from the relay
/// device directory and the local custody chain. The engine trusts nothing else for
/// origin-device verification, recipient selection and revocation.
/// </summary>
public interface IReplicationRoster
{
    /// <summary>Non-revoked authorised devices for an account handle.</summary>
    IReadOnlyList<ReplicationDevice> AuthorizedDevices(string accountHandle);

    /// <summary>Resolves a specific device (revoked or not), or null when unknown.</summary>
    ReplicationDevice? ResolveDevice(string accountHandle, string deviceId);

    /// <summary>The authoritative auth generation for a handle (highest valid custody generation).</summary>
    long AuthGeneration(string accountHandle);
}

/// <summary>
/// Relay-backed rosters can refresh origin-account authority before validating a holder-served
/// batch that contains events created by a third-party account.
/// </summary>
public interface IRefreshableReplicationRoster : IReplicationRoster
{
    Task RefreshAsync(IReadOnlyList<string> handles, CancellationToken ct);
}

/// <summary>Opaque relay forwarder seam. Implemented over the hub by <c>MeshClient</c>.</summary>
public interface IReplicationTransport
{
    Task<OnlineRelaySendResult> SendAsync(OnlineRelayFrame frame, CancellationToken ct);
    Task<OnlineWakeResult> WakeAsync(OnlineWakeRequest request, CancellationToken ct);

}

/// <summary>
/// Domain projection seam. <see cref="Apply"/> is invoked inside the same transaction that appends
/// an inbound event, so the projection commits or rolls back atomically with the log and cursor.
/// <see cref="AfterCommitAsync"/> is invoked once that transaction has committed, so an implementation
/// can refresh its in-memory state and notify the UI without ever doing so for a change that later
/// rolled back. Implemented by <c>AppState</c>; a recording fake is used in isolation tests.
/// </summary>
public interface IReplicationDomainApplier
{
    /// <summary>
    /// Applies the durable domain projection and returns true only when the event won causal
    /// arbitration. A false result still commits the immutable event/cursor, but suppresses the
    /// post-commit in-memory/UI mutation.
    /// </summary>
    bool Apply(
        SqliteConnection conn,
        SqliteTransaction tx,
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop);

    /// <summary>
    /// Called after the apply transaction committed, for the same event and envelope. Never called
    /// for a duplicate event or for a transaction that rolled back.
    /// </summary>
    Task AfterCommitAsync(
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop)
        => Task.CompletedTask;
}

/// <summary>The UI-facing delivery state of one outbound event toward one target account.</summary>
public enum ReplicationDeliveryState
{
    Unknown = 0,
    Stored = 1,
    Pending = 2,
    Offered = 3,
    Persisted = 4,
}

/// <summary>A surfaced replication state transition (for UI / diagnostics).</summary>
public sealed record ReplicationStateChange(string Origin, string Reason, string? Error);

/// <summary>
/// Protocol-9 online-only replication engine. Owns peer sessions, per-peer serialized
/// state, flow-controlled batching, bounded timeouts / retry / backoff, inbound
/// verification and atomic application, signed persistence receipts, fork halting and the
/// online snapshot path. It never enqueues durable relay state: the relay is a pure opaque
/// forwarder, and offline sends simply leave the outbox pending.
/// </summary>
public sealed class OnlineReplicationEngine : IAsyncDisposable
{
    private readonly MeshDb db;
    private readonly ReplicationIdentity identity;
    private readonly IReplicationTransport transport;
    private readonly IReplicationRoster roster;
    private readonly IReplicationDomainApplier applier;
    private readonly bool deviceIsDesktop;
    private readonly ILogger? log;
    private readonly ReplicationJournal journal;

    private readonly ReplicationFlow flow;
    private readonly TimeSpan sendTimeout;
    private readonly int maxSendAttempts;
    private readonly long snapshotByteBudget;
    private readonly TimeSpan sessionInitRetryInterval;
    internal static readonly TimeSpan SessionInitRetryInterval = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> peerLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerSession> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> haltedOrigins = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Task? disposeTask;
    private TaskCompletionSource<bool>? peerOperationsDrained;
    private int activePeerOperations;
    private bool disposed;
    private readonly object activityGate = new();
    private TaskCompletionSource<bool> activitySignal = NewActivitySignal();
    private long activityVersion;
    private long committedEvents;
    private DateTimeOffset? lastProtocolActivity;

    private volatile string? lastError;

    public OnlineReplicationEngine(
        MeshDb db,
        ReplicationIdentity identity,
        IReplicationTransport transport,
        IReplicationRoster roster,
        IReplicationDomainApplier applier,
        bool deviceIsDesktop = true,
        ILogger? logger = null,
        ReplicationFlow? flow = null,
        TimeSpan? sendTimeout = null,
        int maxSendAttempts = 4,
        long snapshotByteBudget = 256L * 1024 * 1024,
        TimeSpan? sessionInitRetryInterval = null)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.roster = roster ?? throw new ArgumentNullException(nameof(roster));
        this.applier = applier ?? throw new ArgumentNullException(nameof(applier));
        this.deviceIsDesktop = deviceIsDesktop;
        this.log = logger;
        this.journal = new ReplicationJournal(db, identity, roster, deviceIsDesktop);
        this.flow = flow ?? new ReplicationFlow(8, OnlineReplicationLimits.MaxBatchOps, OnlineReplicationLimits.MaxBatchBytes);
        this.sendTimeout = sendTimeout ?? TimeSpan.FromSeconds(20);
        this.maxSendAttempts = Math.Max(1, maxSendAttempts);
        this.snapshotByteBudget = snapshotByteBudget;
        this.sessionInitRetryInterval = sessionInitRetryInterval ?? SessionInitRetryInterval;
        if (this.sessionInitRetryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sessionInitRetryInterval));
    }

    /// <summary>The most recent replication error surfaced (fork, verification failure, ...).</summary>
    public string? LastError => lastError;

    /// <summary>
    /// The offline-capable local emitter this engine drives. Exposed so <c>AppState</c> can reuse
    /// the exact same journal (freshest relay roster) for local domain changes while a session is
    /// attached, and fall back to a local-authority journal when it is not.
    /// </summary>
    internal ReplicationJournal Journal => journal;

    /// <summary>Raised when replication state changes in a way the UI should reflect.</summary>
    public event Action<ReplicationStateChange>? StateChanged;
    public event Action? LocalWorkPending;
    public event Action<ReplicationEngineActivity>? Activity;

    internal ReplicationIdentity LocalIdentity => identity;

    public bool IsSessionEstablished(string peerDeviceId)
        => sessions.TryGetValue(peerDeviceId, out var session) && session.Established;

    public ReplicationProgressSnapshot GetProgress()
    {
        lock (activityGate)
            return new ReplicationProgressSnapshot(activityVersion, committedEvents, lastProtocolActivity);
    }

    public async Task<bool> WaitForActivityAsync(
        long observedVersion,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Task signal;
        lock (activityGate)
        {
            if (activityVersion != observedVersion) return true;
            signal = activitySignal.Task;
        }
        try
        {
            await signal.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public Task OfferPeerAsync(string peerHandle, string peerDevice, CancellationToken ct = default)
        => WithPeerLock(peerDevice, async operationCt =>
        {
            if (sessions.TryGetValue(peerDevice, out var session) && session.Established)
                await TryOfferLocalOriginsAsync(session, OnlinePushClasses.Normal, operationCt).ConfigureAwait(false);
        }, ct);

    internal void ReportBootstrapActivity(
        string name,
        ReplicationBootstrapTarget target,
        string bootstrapId,
        int eventCount = 0)
        => ObserveActivity(new ReplicationEngineActivity(
            name,
            target.PeerHandle,
            target.PeerDeviceId,
            eventCount,
            BootstrapId: bootstrapId));

    /// <summary>True when the given origin log has been halted (e.g. by a detected fork).</summary>
    public bool IsHalted(string originDeviceId) => haltedOrigins.ContainsKey(originDeviceId);

    /// <summary>Registers this device's local origin log (idempotent).</summary>
    public void EnsureLocalOrigin()
        => db.EnsureLocalOrigin(identity.DeviceId, identity.LogEpoch, identity.AuthGeneration);

    // =======================================================================
    // Local origin: create a signed event, persist it and enqueue outbox refs.
    // =======================================================================

    /// <summary>
    /// Creates a local-origin replication event: allocates the next sequence, encrypts the
    /// domain envelope to every currently-authorised recipient device, signs the event and
    /// appends it together with one outbox reference per target account in a single
    /// transaction. The event history payload is stored once; outbox refs carry no body.
    /// A direct message is therefore locally persisted first and stays pending toward each
    /// recipient account until a signed persistence receipt arrives.
    /// </summary>
    public async Task<string> EmitLocalAsync(
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts,
        string pushClass = OnlinePushClasses.Normal,
        CancellationToken ct = default,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(targetAccounts);

        // Local-journal write: append the signed event, its outbox refs and the domain
        // projection atomically. This happens whether or not any session is established, so a
        // local change is always durably recorded before any transport is attempted.
        var eventId = journal.EmitLocal(envelope, targetAccounts, domainWork);

        if (targetAccounts.Count > 0)
            LocalWorkPending?.Invoke();

        // Best-effort immediate offer to any peer with a live established session. Offline this
        // simply finds no sessions and leaves the outbox pending for a later drain.
        var established = sessions.Values.Where(session => session.Established).ToArray();
        foreach (var session in established)
            await TryOfferLocalOriginsAsync(session, pushClass, ct).ConfigureAwait(false);

        return eventId;
    }

    // =======================================================================
    // Session lifecycle triggers.
    // =======================================================================

    /// <summary>Opens a replication session by sending a signed session init to the peer.</summary>
    public Task StartSessionAsync(string peerHandle, string peerDevice, CancellationToken ct = default)
        => WithPeerLock(peerDevice, async operationCt =>
        {
            if (!ShouldStartSession(peerDevice, DateTimeOffset.UtcNow)) return;
            await SendSessionInitAsync(peerHandle, peerDevice, operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Presence transition to online: initiate a session (which drives the symmetric
    /// offer / request / batch exchange once acknowledged).
    /// </summary>
    public Task OnPresenceOnlineAsync(string peerHandle, string peerDevice, CancellationToken ct = default)
        => StartSessionAsync(peerHandle, peerDevice, ct);

    /// <summary>
    /// Offers immediately when a peer session exists; otherwise asks the authenticated relay to emit
    /// an ephemeral contentless wake derived from the peer's unreceipted encrypted work. Custody remains
    /// local until the peer reconnects and receipts.
    /// </summary>
    public Task OnWakeAsync(string peerHandle, string peerDevice, CancellationToken ct = default)
        => WithPeerLock(peerDevice, async operationCt =>
        {
            if (sessions.TryGetValue(peerDevice, out var session) && session.Established)
            {
                await TryOfferLocalOriginsAsync(session, OnlinePushClasses.Silent, operationCt).ConfigureAwait(false);
                return;
            }

            var peer = roster.ResolveDevice(peerHandle, peerDevice);
            if (peer is null || peer.Revoked)
            {
                ObserveActivity(new ReplicationEngineActivity(
                    "wake.rejected", peerHandle, peerDevice, ErrorCode: OnlineWakeCodes.TargetDeviceUnknown));
                return;
            }

            var request = BuildWakeRequest(peer);
            if (request is null)
            {
                ObserveActivity(new ReplicationEngineActivity("wake.skipped", peerHandle, peerDevice));
                return;
            }

            var result = await transport.WakeAsync(request, operationCt).ConfigureAwait(false);
            ObserveActivity(new ReplicationEngineActivity(
                result.Accepted ? "wake.requested" : "wake.rejected",
                peerHandle, peerDevice, ErrorCode: result.Accepted ? null : result.Code));
        }, ct);

    private bool ShouldStartSession(string peerDevice, DateTimeOffset now)
        => !sessions.TryGetValue(peerDevice, out var existing)
           || (!existing.Established && now - existing.LastInitAttemptAt >= sessionInitRetryInterval);

    private async Task SendSessionInitAsync(string peerHandle, string peerDevice, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("n");
        var nonce = MeshCrypto.NewNonce();
        var init = OnlineReplicationProtocol.CreateSessionInit(
            sessionId, identity.DeviceId, peerDevice, nonce,
            identity.CustodyHead, identity.AuthGeneration, identity.PrivateKeyB64);
        var session = new PeerSession(peerHandle, peerDevice, sessionId, nonce) { Established = false };
        sessions[peerDevice] = session;
        ObserveActivity(new ReplicationEngineActivity("session.started", peerHandle, peerDevice));
        await SendControlAsync(peerHandle, peerDevice, E2EFrameKind.SessionInit, sessionId,
            ReplicationPayloadCodec.SerializeControl(init), OnlinePushClasses.High, ct).ConfigureAwait(false);
        if (sessions.TryGetValue(peerDevice, out var current)
            && ReferenceEquals(current, session)
            && !current.Established)
            current.LastInitAttemptAt = DateTimeOffset.UtcNow;
    }

    // =======================================================================
    // Inbound dispatch.
    // =======================================================================

    /// <summary>
    /// Handles one relay delivery. The relay-stamped <see cref="OnlineRelayDelivery.FromHandle"/>
    /// / <see cref="OnlineRelayDelivery.FromDevice"/> are the trusted route identity and are
    /// validated against the session peer; the opaque frame body is decoded, decrypted and
    /// dispatched by kind under the per-peer lock.
    /// </summary>
    public Task HandleDeliveryAsync(OnlineRelayDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return WithPeerLock(
            delivery.FromDevice, operationCt => DispatchAsync(delivery, operationCt), ct);
    }

    private async Task DispatchAsync(OnlineRelayDelivery delivery, CancellationToken ct)
    {
        var frame = ReplicationPayloadCodec.DecodeFrame(delivery.Ciphertext);
        if (frame is null) { Surface("route", "Malformed E2E frame."); return; }
        ObserveActivity(new ReplicationEngineActivity(
            "protocol.frame_received",
            delivery.FromHandle,
            delivery.FromDevice,
            ByteCount: Encoding.UTF8.GetByteCount(delivery.Ciphertext)));

        var peerDevice = roster.ResolveDevice(delivery.FromHandle, delivery.FromDevice);
        if (peerDevice is null || peerDevice.Revoked)
        {
            Surface("auth", $"Delivery from unauthorised or revoked device {delivery.FromDevice}.");
            return;
        }

        // Session handshake frames are verified by their own signatures; other frames must
        // ride an established session whose peer matches the stamped route.
        switch (frame.Kind)
        {
            case E2EFrameKind.SessionInit:
                await OnSessionInitAsync(delivery, frame, peerDevice, ct).ConfigureAwait(false);
                return;
            case E2EFrameKind.SessionAck:
                await OnSessionAckAsync(delivery, frame, peerDevice, ct).ConfigureAwait(false);
                return;
        }

        // Data frames must ride an established session with the stamped peer device. We match
        // on the peer device identity (validated above via the roster + relay route stamp)
        // rather than a strict session-id equality: under simultaneous connect both peers may
        // hold divergent session ids, and rejecting on that would silently drop replication.
        if (!sessions.TryGetValue(delivery.FromDevice, out var session) || !session.Established)
        {
            Surface("route", $"Frame {frame.Kind} for peer {delivery.FromDevice} with no established session.");
            return;
        }

        var (ok, plaintext) = ReplicationPayloadCodec.TryDecrypt(frame.Payload, identity.PrivateKeyB64, identity.PublicKeyB64);
        if (!ok || plaintext is null) { Surface("crypto", $"Undecryptable {frame.Kind} body."); return; }

        switch (frame.Kind)
        {
            case E2EFrameKind.Offer: await OnOfferAsync(session, plaintext, ct).ConfigureAwait(false); break;
            case E2EFrameKind.Request: await OnRequestAsync(session, plaintext, ct).ConfigureAwait(false); break;
            case E2EFrameKind.Batch: await OnBatchAsync(session, delivery, plaintext, ct).ConfigureAwait(false); break;
            case E2EFrameKind.Receipt: OnReceipt(delivery, plaintext, peerDevice); break;
            case E2EFrameKind.ResyncRequest: await OnResyncRequestAsync(session, plaintext, ct).ConfigureAwait(false); break;
            case E2EFrameKind.ResyncSnapshot: await OnBatchAsync(session, delivery, plaintext, ct).ConfigureAwait(false); break;
            case E2EFrameKind.ReadWatermark: OnReadWatermark(plaintext); break;
            case E2EFrameKind.Custody: OnCustody(delivery, plaintext); break;
            default: Surface("route", $"Unhandled frame kind {frame.Kind}."); break;
        }
    }

    private async Task OnSessionInitAsync(OnlineRelayDelivery delivery, E2EFrame frame, ReplicationDevice peer, CancellationToken ct)
    {
        var (ok, plaintext) = ReplicationPayloadCodec.TryDecrypt(frame.Payload, identity.PrivateKeyB64, identity.PublicKeyB64);
        if (!ok || plaintext is null) { Surface("crypto", "Undecryptable session init."); return; }
        var init = ReplicationPayloadCodec.DeserializeControl<ReplicationSessionInit>(plaintext);
        if (init is null || !OnlineReplicationProtocol.VerifySessionInit(init, peer.PublicKeyB64))
        {
            Surface("auth", "Session init failed verification.");
            return;
        }
        if (init.AuthGeneration < roster.AuthGeneration(delivery.FromHandle) && roster.AuthGeneration(delivery.FromHandle) >= 0
            && init.AuthGeneration < peer.AuthGeneration)
        {
            Surface("auth", "Session init carried a stale auth generation.");
            return;
        }

        var myNonce = MeshCrypto.NewNonce();
        var ack = OnlineReplicationProtocol.CreateSessionAck(
            init.SessionId, identity.DeviceId, delivery.FromDevice, myNonce, init.Nonce,
            identity.CustodyHead, identity.AuthGeneration, identity.PrivateKeyB64);
        var session = new PeerSession(delivery.FromHandle, delivery.FromDevice, init.SessionId, myNonce) { Established = true };
        sessions[delivery.FromDevice] = session;
        db.UpsertPeerState(delivery.FromHandle, delivery.FromDevice, init.SessionId, null);
        ObserveActivity(new ReplicationEngineActivity(
            "session.established", delivery.FromHandle, delivery.FromDevice));

        await SendControlAsync(delivery.FromHandle, delivery.FromDevice, E2EFrameKind.SessionAck, init.SessionId,
            ReplicationPayloadCodec.SerializeControl(ack), OnlinePushClasses.High, ct).ConfigureAwait(false);
        await TryOfferLocalOriginsAsync(session, OnlinePushClasses.Normal, ct).ConfigureAwait(false);
    }

    private async Task OnSessionAckAsync(OnlineRelayDelivery delivery, E2EFrame frame, ReplicationDevice peer, CancellationToken ct)
    {
        var (ok, plaintext) = ReplicationPayloadCodec.TryDecrypt(frame.Payload, identity.PrivateKeyB64, identity.PublicKeyB64);
        if (!ok || plaintext is null) { Surface("crypto", "Undecryptable session ack."); return; }
        var ack = ReplicationPayloadCodec.DeserializeControl<ReplicationSessionAck>(plaintext);
        if (ack is null || !sessions.TryGetValue(delivery.FromDevice, out var session))
        {
            Surface("route", "Session ack for unknown session.");
            return;
        }
        if (!OnlineReplicationProtocol.VerifySessionAck(ack, peer.PublicKeyB64, session.LocalNonce))
        {
            Surface("auth", "Session ack failed verification.");
            return;
        }
        session.Established = true;
        session.SessionId = ack.SessionId;
        db.UpsertPeerState(delivery.FromHandle, delivery.FromDevice, ack.SessionId, null);
        ObserveActivity(new ReplicationEngineActivity(
            "session.established", delivery.FromHandle, delivery.FromDevice));
        await TryOfferLocalOriginsAsync(session, OnlinePushClasses.Normal, ct).ConfigureAwait(false);
    }

    private async Task TryOfferLocalOriginsAsync(PeerSession session, string pushClass, CancellationToken ct)
    {
        foreach (var offer in db.GetServeableOrigins())
        {
            if (offer.AvailableThrough == 0) continue;
            await SendControlAsync(session.PeerHandle, session.PeerDevice, E2EFrameKind.Offer, session.SessionId,
                ReplicationPayloadCodec.SerializeControl(new ReplicationOffer(
                    offer.OriginDeviceId, offer.LogEpoch, offer.AvailableFrom, offer.AvailableThrough)),
                pushClass, ct).ConfigureAwait(false);
        }
    }

    private async Task OnOfferAsync(PeerSession session, string plaintext, CancellationToken ct)
    {
        var offer = ReplicationPayloadCodec.DeserializeControl<ReplicationOffer>(plaintext);
        if (offer is null || !OnlineReplicationProtocol.ValidateOffer(offer, out _)) { Surface("route", "Malformed offer."); return; }
        if (IsHalted(offer.OriginDeviceId)) return;

        var cursor = db.GetCursor(offer.OriginDeviceId) ?? OnlineReplicationProtocol.EmptyCursor();
        var plan = OnlineReplicationProtocol.PlanReplication(cursor, offer);
        if (plan.RequiresResync)
        {
            await SendControlAsync(session.PeerHandle, session.PeerDevice, E2EFrameKind.ResyncRequest, session.SessionId,
                ReplicationPayloadCodec.SerializeControl(new ReplicationResyncRequest(
                    offer.OriginDeviceId, offer.LogEpoch, cursor.Contiguous + 1)),
                OnlinePushClasses.Normal, ct).ConfigureAwait(false);
            return;
        }
        if (plan.Ranges.Count == 0)
        {
            var storedReceipt = db.GetReceipt(identity.DeviceId, offer.OriginDeviceId, offer.LogEpoch);
            if (storedReceipt is not null && storedReceipt.ThroughSeq >= offer.AvailableThrough)
            {
                var sent = await SendControlAsync(
                    session.PeerHandle,
                    session.PeerDevice,
                    E2EFrameKind.Receipt,
                    session.SessionId,
                    ReplicationPayloadCodec.SerializeControl(storedReceipt),
                    OnlinePushClasses.Normal,
                    ct).ConfigureAwait(false);
                if (sent.Accepted)
                    ObserveActivity(new ReplicationEngineActivity(
                        "receipt.sent", session.PeerHandle, session.PeerDevice));
            }
            return;
        }

        await SendControlAsync(session.PeerHandle, session.PeerDevice, E2EFrameKind.Request, session.SessionId,
            ReplicationPayloadCodec.SerializeControl(new ReplicationRequest(offer.OriginDeviceId, offer.LogEpoch, plan.Ranges)),
            OnlinePushClasses.Normal, ct).ConfigureAwait(false);
    }

    private async Task OnRequestAsync(PeerSession session, string plaintext, CancellationToken ct)
    {
        var request = ReplicationPayloadCodec.DeserializeControl<ReplicationRequest>(plaintext);
        if (request is null || !OnlineReplicationProtocol.ValidateRequest(request, out _)) { Surface("route", "Malformed request."); return; }
        ObserveActivity(new ReplicationEngineActivity(
            "request.received", session.PeerHandle, session.PeerDevice));
        await ServeRangesAsync(session, request.OriginDeviceId, request.LogEpoch, request.Ranges, long.MaxValue, ct).ConfigureAwait(false);
    }

    private async Task OnResyncRequestAsync(PeerSession session, string plaintext, CancellationToken ct)
    {
        var resync = ReplicationPayloadCodec.DeserializeControl<ReplicationResyncRequest>(plaintext);
        if (resync is null) { Surface("route", "Malformed resync request."); return; }
        var through = db.GetLocalOriginThrough(resync.OriginDeviceId);
        if (through < resync.FromSeq) return;
        var ranges = new[] { new ReplicationRange(resync.FromSeq == 0 ? 1 : resync.FromSeq, through) };
        // Online snapshot: stream the log directly as E2E batches, bounded by a byte budget.
        await ServeRangesAsync(session, resync.OriginDeviceId, resync.LogEpoch, ranges, snapshotByteBudget, ct).ConfigureAwait(false);
    }

    private async Task ServeRangesAsync(
        PeerSession session, string origin, string epoch,
        IReadOnlyList<ReplicationRange> ranges, long byteBudget, CancellationToken ct)
    {
        long streamed = 0;
        foreach (var range in ranges)
        {
            var from = range.FromSeq;
            while (from <= range.ToSeq)
            {
                var page = db.QueryEvents(origin, epoch, from, range.ToSeq, OnlineReplicationLimits.MaxBatchOps);
                if (page.Count == 0) break;
                foreach (var batch in OnlineReplicationState.BuildBatches(origin, epoch, page, flow))
                {
                    streamed += batch.Events.Sum(e => (long)System.Text.Encoding.UTF8.GetByteCount(e.Ciphertext) + 256);
                    await SendControlAsync(session.PeerHandle, session.PeerDevice, E2EFrameKind.Batch, session.SessionId,
                        ReplicationPayloadCodec.SerializeControl(batch), OnlinePushClasses.Normal, ct).ConfigureAwait(false);
                    if (streamed >= byteBudget) return;
                }
                from = page[^1].Seq + 1;
            }
        }
    }

    private async Task OnBatchAsync(PeerSession session, OnlineRelayDelivery delivery, string plaintext, CancellationToken ct)
    {
        var batch = ReplicationPayloadCodec.DeserializeControl<ReplicationBatch>(plaintext);
        if (batch is null) { Surface("route", "Malformed batch: null."); return; }
        if (!OnlineReplicationProtocol.ValidateBatch(batch, out var berr)) { Surface("route", $"Malformed batch: {berr}."); return; }
        if (IsHalted(batch.OriginDeviceId)) return;

        if (roster is IRefreshableReplicationRoster refreshable)
        {
            var originAccounts = batch.Events
                .Select(evt => evt.OriginAccount)
                .Where(account => !string.IsNullOrWhiteSpace(account))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await refreshable.RefreshAsync(originAccounts, ct).ConfigureAwait(false);
        }

        var committedCount = 0;
        foreach (var evt in batch.Events)
        {
            var originDevice = roster.ResolveDevice(evt.OriginAccount, evt.OriginDeviceId);
            if (originDevice is null || originDevice.Revoked)
            {
                Surface("auth", $"Event from unknown/revoked origin device {evt.OriginDeviceId}.");
                return;
            }
            if (evt.AuthGeneration > roster.AuthGeneration(evt.OriginAccount) && roster.AuthGeneration(evt.OriginAccount) >= 0)
            {
                Surface("auth", $"Event carried a future auth generation {evt.AuthGeneration}.");
                return;
            }
            if (!OnlineReplicationProtocol.VerifyEvent(evt, originDevice.PublicKeyB64))
            {
                Surface("auth", $"Event {evt.EventId} failed signature verification.");
                return;
            }

            var cursor = db.GetCursor(evt.OriginDeviceId) ?? OnlineReplicationProtocol.EmptyCursor();
            var apply = OnlineReplicationProtocol.ApplyToCursor(cursor, evt.LogEpoch, evt.Seq, out var updated);
            switch (apply)
            {
                case CursorApplyResult.Duplicate:
                    continue; // Exact duplicate: never re-project the domain.
                case CursorApplyResult.RejectedTooFarAhead:
                    continue; // Outside the reorder window; a follow-up request refetches.
                case CursorApplyResult.RejectedEpochMismatch:
                    Halt(evt.OriginDeviceId, "epoch-mismatch", $"Epoch changed for {evt.OriginDeviceId}.");
                    return;
                case CursorApplyResult.RejectedInvalid:
                    Surface("route", $"Event {evt.EventId} rejected by cursor.");
                    return;
            }

            ReplicationPayloadCodec.DomainEnvelope? committed = null;
            try
            {
                var append = db.ApplyInboundEvent(
                    evt,
                    updated,
                    (conn, tx) => committed = ProjectDomain(conn, tx, evt));
                if (append == MeshDb.ReplicationAppendResult.Inserted) committedCount++;
            }
            catch (MeshDb.ReplicationForkException fork)
            {
                Halt(evt.OriginDeviceId, "fork", fork.Message);
                return;
            }
            catch (ReplicationProjectionException projection)
            {
                // A permanent, unrecoverable domain-projection failure (unknown/invalid/
                // unauthorised payload). The transaction has already rolled back, so the event
                // is not stored and the cursor did not advance. Halt this origin so we never
                // silently skip the event or advance past it (spec items 4 & 6: fail closed).
                Halt(evt.OriginDeviceId, "projection", projection.Message);
                return;
            }

            // The transaction committed. Only now may in-memory state be refreshed and the UI
            // notified, so a rolled-back apply can never leave the UI showing phantom state.
            if (committed is not null)
                await applier.AfterCommitAsync(evt, committed, deviceIsDesktop).ConfigureAwait(false);
        }

        if (committedCount > 0)
        {
            db.RecordPeerSync(session.PeerHandle, session.PeerDevice);
            ObserveActivity(new ReplicationEngineActivity(
                "batch.committed",
                session.PeerHandle,
                session.PeerDevice,
                committedCount,
                Encoding.UTF8.GetByteCount(plaintext)));
        }
        await SendReceiptAsync(session, delivery, batch, ct).ConfigureAwait(false);
    }

    private ReplicationPayloadCodec.DomainEnvelope? ProjectDomain(SqliteConnection conn, SqliteTransaction tx, ReplicationEvent evt)
    {
        var (ok, plaintext) = ReplicationPayloadCodec.TryDecrypt(evt.Ciphertext, identity.PrivateKeyB64, identity.PublicKeyB64);
        if (!ok || plaintext is null) return null; // Not addressed to this device: store the log, no projection.
        var envelope = ReplicationPayloadCodec.DecodeEnvelope(plaintext)
            ?? throw new ReplicationProjectionException(
                "The authenticated event payload could not be decoded or mapped.");
        if (!deviceIsDesktop && ReplicationPayloadCodec.RequiresDesktop(envelope.Kind, envelope.Action))
            throw new ReplicationProjectionException(
                "A desktop-only payload was routed to a mobile replica.");
        return applier.Apply(conn, tx, evt, envelope, deviceIsDesktop)
            ? envelope
            : null;
    }

    private async Task SendReceiptAsync(PeerSession session, OnlineRelayDelivery delivery, ReplicationBatch batch, CancellationToken ct)
    {
        var cursor = db.GetCursor(batch.OriginDeviceId) ?? OnlineReplicationProtocol.EmptyCursor();
        if (cursor.Contiguous == 0) return;
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            identity.DeviceId, batch.OriginDeviceId, batch.LogEpoch, cursor.Contiguous,
            OnlineReplicationProtocol.ComputeCursorHash(cursor),
            OnlineReplicationProtocol.ComputeBatchHash(batch),
            identity.PrivateKeyB64);
        db.StoreReceipt(receipt);
        var sent = await SendControlAsync(
            session.PeerHandle,
            session.PeerDevice,
            E2EFrameKind.Receipt,
            session.SessionId,
            ReplicationPayloadCodec.SerializeControl(receipt),
            OnlinePushClasses.Normal,
            ct).ConfigureAwait(false);
        if (sent.Accepted)
            ObserveActivity(new ReplicationEngineActivity(
                "receipt.sent", session.PeerHandle, session.PeerDevice));
    }

    private void OnReceipt(OnlineRelayDelivery delivery, string plaintext, ReplicationDevice peer)
    {
        var receipt = ReplicationPayloadCodec.DeserializeControl<PersistenceReceipt>(plaintext);
        if (receipt is null) { Surface("route", "Malformed receipt."); return; }
        if (!string.Equals(receipt.ReceiverDeviceId, peer.DeviceId, StringComparison.Ordinal))
        {
            Surface("auth", "Receipt receiver does not match the stamped route device.");
            return;
        }
        try
        {
            // Custody is cleared when one authorised recipient device proves durable persistence.
            var bootstrapTarget = ReplicationBootstrapTarget.Create(peer, identity);
            var bootstrapBefore = db.GetPeerBootstrap(bootstrapTarget);
            var advanced = db.MarkOutboxPersistedFromReceipt(receipt, peer.PublicKeyB64, delivery.FromHandle);
            ObserveActivity(new ReplicationEngineActivity(
                "receipt.received",
                delivery.FromHandle,
                delivery.FromDevice,
                advanced));
            var bootstrapAfter = db.GetPeerBootstrap(bootstrapTarget);
            if (bootstrapBefore?.State == MeshDb.BootstrapStateEmitted
                && bootstrapAfter?.State == MeshDb.BootstrapStatePersisted)
            {
                ReportBootstrapActivity(
                    "bootstrap.persisted",
                    bootstrapTarget,
                    bootstrapAfter.BootstrapId,
                    bootstrapAfter.TotalItems);
            }
        }
        catch (ArgumentException)
        {
            Surface("auth", "Forged or invalid persistence receipt rejected.");
        }
    }

    private void OnReadWatermark(string plaintext)
    {
        var watermark = ReplicationPayloadCodec.DeserializeControl<ReadWatermarkPayload>(plaintext);
        if (watermark is null) { Surface("route", "Malformed read watermark."); return; }
        db.UpsertReadWatermark(watermark);
    }

    private void OnCustody(OnlineRelayDelivery delivery, string plaintext)
    {
        var entry = ReplicationPayloadCodec.DeserializeControl<CustodyEntry>(plaintext);
        if (entry is null) { Surface("route", "Malformed custody entry."); return; }
        if (!string.Equals(
                AppState.Norm(entry.Handle),
                AppState.Norm(delivery.FromHandle),
                StringComparison.Ordinal))
        {
            Surface("custody", "Custody entry handle did not match the authenticated sender.");
            return;
        }
        try { db.AppendCustodyEntry(entry); }
        catch (MeshDb.ReplicationForkException fork) { Halt("custody:" + entry.Handle, "custody-fork", fork.Message); }
        catch (ArgumentException ex) { Surface("custody", ex.Message); }
    }

    // =======================================================================
    // Sending with bounded timeout, retry and backoff.
    // =======================================================================

    private async Task<OnlineRelaySendResult> SendControlAsync(
        string peerHandle, string peerDevice, E2EFrameKind kind, string sessionId,
        string bodyPlaintext, string pushClass, CancellationToken ct)
    {
        var peer = roster.ResolveDevice(peerHandle, peerDevice);
        if (peer is null || peer.Revoked)
        {
            Surface("auth", $"Cannot send {kind}: peer {peerDevice} unauthorised.");
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.DeviceRevoked);
        }
        var cipher = ReplicationPayloadCodec.Encrypt(bodyPlaintext, new[] { peer.PublicKeyB64 });
        var frame = new E2EFrame(kind, sessionId, cipher);
        var relayFrame = new OnlineRelayFrame(
            peerHandle, peerDevice, Guid.NewGuid().ToString("n"), pushClass,
            ReplicationPayloadCodec.EncodeFrame(frame));
        var result = await SendWithRetryAsync(relayFrame, peerHandle, peerDevice, ct).ConfigureAwait(false);
        if (result.Accepted)
        {
            var name = kind switch
            {
                E2EFrameKind.Offer => "offer.sent",
                E2EFrameKind.Batch or E2EFrameKind.ResyncSnapshot => "batch.sent",
                _ => "protocol.frame_sent"
            };
            var eventCount = kind is E2EFrameKind.Batch or E2EFrameKind.ResyncSnapshot
                ? ReplicationPayloadCodec.DeserializeControl<ReplicationBatch>(bodyPlaintext)?.Events.Count ?? 0
                : 0;
            ObserveActivity(new ReplicationEngineActivity(
                name,
                peerHandle,
                peerDevice,
                eventCount,
                Encoding.UTF8.GetByteCount(relayFrame.Ciphertext)));
        }
        return result;
    }

    private async Task<OnlineRelaySendResult> SendWithRetryAsync(
        OnlineRelayFrame frame, string peerHandle, string peerDevice, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        OnlineRelaySendResult? last = null;
        for (var attempt = 0; attempt < maxSendAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            timeout.CancelAfter(sendTimeout);
            try
            {
                last = await transport.SendAsync(frame, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new OnlineRelaySendResult(false, OnlineRelaySendCodes.RateLimited);
            }

            if (last.Accepted) return last;
            switch (last.Code)
            {
                case OnlineRelaySendCodes.NotOnline:
                case OnlineRelaySendCodes.TargetDeviceUnknown:
                    // Offline: leave the outbox pending. No durable relay queue exists.
                    return last;
                case OnlineRelaySendCodes.DeviceRevoked:
                    Surface("auth", $"Peer device {peerDevice} is revoked.");
                    return last;
                case OnlineRelaySendCodes.TooLarge:
                    Surface("size", "Transport frame exceeded the relay limit.");
                    return last;
            }

            var wait = last.RetryAfterMs is int ms and > 0 ? TimeSpan.FromMilliseconds(ms) : delay;
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return last; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }
        return last ?? new OnlineRelaySendResult(false, OnlineRelaySendCodes.RateLimited);
    }

    // =======================================================================
    // UI-facing delivery state.
    // =======================================================================

    /// <summary>
    /// True when this authorised device has not receipted every local-origin event addressed to its
    /// account, or when an own-account sibling still needs its durable bootstrap snapshot.
    /// Account custody may already be clear through another device; this per-device view prevents
    /// that receipt from suppressing convergence work for a lagging sibling.
    /// </summary>
    public bool HasPendingWorkForPeer(ReplicationDevice peer)
        => BuildWakeRequest(peer) is not null;

    private OnlineWakeRequest? BuildWakeRequest(ReplicationDevice peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.Revoked || string.Equals(peer.DeviceId, identity.DeviceId, StringComparison.Ordinal))
            return null;

        var targetAccount = ReplicationHandle.Norm(peer.Handle);
        if (targetAccount.Length == 0) return null;
        var receiptThrough = db.GetReceipt(
            peer.DeviceId,
            identity.DeviceId,
            identity.LogEpoch)?.ThroughSeq ?? 0;

        var notification = FindRecipientWakeCandidate(
            peer,
            targetAccount,
            receiptThrough,
            notificationWorthy: true);
        if (notification?.NotificationId is { Length: > 0 } notificationId)
        {
            return new OnlineWakeRequest(
                targetAccount,
                peer.DeviceId,
                StableWakeId(
                    "notification",
                    identity.DeviceId,
                    identity.LogEpoch,
                    notificationId,
                    peer.DeviceId),
                NotificationWorthy: true);
        }

        var pending = FindRecipientWakeCandidate(
            peer,
            targetAccount,
            receiptThrough,
            notificationWorthy: null);
        if (pending is not null)
        {
            return new OnlineWakeRequest(
                targetAccount,
                peer.DeviceId,
                StableWakeId(
                    "sync",
                    identity.DeviceId,
                    identity.LogEpoch,
                    pending.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    peer.DeviceId));
        }

        if (!string.Equals(targetAccount, ReplicationHandle.Norm(identity.Handle), StringComparison.Ordinal))
            return null;
        var bootstrap = db.GetPeerBootstrap(ReplicationBootstrapTarget.Create(peer, identity));
        if (bootstrap?.State == MeshDb.BootstrapStatePersisted) return null;
        var bootstrapWakeId = bootstrap?.BootstrapId
                              ?? StableWakeId(
                                  "bootstrap-pending",
                                  identity.DeviceId,
                                  identity.LogEpoch,
                                  peer.DeviceId);
        return new OnlineWakeRequest(
            targetAccount,
            peer.DeviceId,
            StableWakeId("bootstrap", bootstrapWakeId, peer.DeviceId));
    }

    private MeshDb.ReplicationWakeCandidate? FindRecipientWakeCandidate(
        ReplicationDevice peer,
        string targetAccount,
        ulong receiptThrough,
        bool? notificationWorthy)
    {
        var after = receiptThrough;
        while (true)
        {
            var candidates = db.QueryTargetOutboxAfter(
                targetAccount,
                identity.DeviceId,
                identity.LogEpoch,
                after,
                notificationWorthy);
            if (candidates.Count == 0) return null;
            var encryptedDeviceId = DeviceProtocol.DeviceId(peer.PublicKeyB64);
            foreach (var candidate in candidates)
            {
                after = candidate.Seq;
                if (ReplicationPayloadCodec.RecipientDeviceIds(candidate.Ciphertext)
                    .Contains(encryptedDeviceId, StringComparer.Ordinal))
                    return candidate;
            }
            if (candidates.Count < OnlineReplicationLimits.MaxBatchOps) return null;
        }
    }

    private static string StableWakeId(string purpose, params string[] values)
    {
        var material = string.Join("\0", values.Prepend(purpose));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }


    /// <summary>
    /// Counts account-targeted local events not yet receipted by every currently authorised device.
    /// Persisted account-custody rows remain visible here until each device catches up.
    /// </summary>
    public int CountPendingTargetEvents(IReadOnlyCollection<string> targetAccounts)
    {
        ArgumentNullException.ThrowIfNull(targetAccounts);
        var total = 0;
        foreach (var targetAccount in targetAccounts
                     .Select(ReplicationHandle.Norm)
                     .Where(value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            var peers = roster.AuthorizedDevices(targetAccount)
                .Where(peer => !peer.Revoked
                               && !string.Equals(peer.DeviceId, identity.DeviceId, StringComparison.Ordinal))
                .ToArray();
            var through = peers.Length == 0
                ? 0UL
                : peers.Min(peer => db.GetReceipt(
                    peer.DeviceId,
                    identity.DeviceId,
                    identity.LogEpoch)?.ThroughSeq ?? 0UL);
            total = checked(total + db.CountTargetOutboxAfter(
                targetAccount,
                identity.DeviceId,
                identity.LogEpoch,
                through));
        }
        return total;
    }

    /// <summary>The delivery state of one outbound event toward one target account.</summary>
    public ReplicationDeliveryState GetDeliveryState(string eventId, string targetAccount)
    {
        var state = db.GetOutboxState(eventId, targetAccount);
        return state switch
        {
            MeshDb.OutboxStatePending => ReplicationDeliveryState.Pending,
            MeshDb.OutboxStateOffered => ReplicationDeliveryState.Offered,
            MeshDb.OutboxStatePersisted => ReplicationDeliveryState.Persisted,
            null when db.GetEvent(eventId) is not null => ReplicationDeliveryState.Stored,
            _ => ReplicationDeliveryState.Unknown,
        };
    }

    // =======================================================================
    // Halting, error surfacing and per-peer serialisation.
    // =======================================================================

    private static TaskCompletionSource<bool> NewActivitySignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ObserveActivity(ReplicationEngineActivity activity)
    {
        TaskCompletionSource<bool> signal;
        lock (activityGate)
        {
            activityVersion++;
            lastProtocolActivity = DateTimeOffset.UtcNow;
            if (activity.Name == "batch.committed") committedEvents += activity.EventCount;
            signal = activitySignal;
            activitySignal = NewActivitySignal();
        }
        signal.TrySetResult(true);
        Activity?.Invoke(activity);
        ReplicationDiagnostics.Record(
            activity.Name,
            ("peer_handle", activity.PeerHandle),
            ("peer_device_id", activity.PeerDeviceId),
            ("event_count", activity.EventCount == 0 ? null : activity.EventCount),
            ("byte_count", activity.ByteCount == 0 ? null : activity.ByteCount),
            ("error_code", activity.ErrorCode),
            ("bootstrap_id", activity.BootstrapId));
    }

    private void Halt(string origin, string reason, string error)
    {
        haltedOrigins[origin] = 1;
        lastError = error;
        log?.LogError("Replication halted origin {Origin}: {Reason} {Error}", origin, reason, error);
        StateChanged?.Invoke(new ReplicationStateChange(origin, reason, error));
    }

    private void Surface(string reason, string error)
    {
        lastError = error;
        log?.LogWarning("Replication {Reason}: {Error}", reason, error);
        StateChanged?.Invoke(new ReplicationStateChange("", reason, error));
    }

    private async Task WithPeerLock(
        string peerDevice,
        Func<CancellationToken, Task> body,
        CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (disposalGate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(OnlineReplicationEngine));
            activePeerOperations++;
            gate = peerLocks.GetOrAdd(peerDevice, _ => new SemaphoreSlim(1, 1));
        }

        var entered = false;
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            await gate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            await body(operation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (entered) gate.Release();
            ExitPeerOperation();
        }
    }

    private void ExitPeerOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (disposalGate)
        {
            activePeerOperations--;
            if (disposed && activePeerOperations == 0)
                drained = peerOperationsDrained;
        }
        drained?.TrySetResult(true);
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        Task drain;
        TaskCompletionSource<bool> completion;
        lock (disposalGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);

            disposed = true;
            drain = activePeerOperations == 0
                ? Task.CompletedTask
                : (peerOperationsDrained ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            disposeTask = task;
        }

        _ = CompleteDisposeAsync(drain, completion);
        return new ValueTask(task);
    }

    private async Task CompleteDisposeAsync(Task drain, TaskCompletionSource<bool> completion)
    {
        try
        {
            lifetime.Cancel();
            lock (activityGate) activitySignal.TrySetCanceled();
            await drain.ConfigureAwait(false);
            foreach (var gate in peerLocks.Values) gate.Dispose();
            lifetime.Dispose();
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private sealed class PeerSession(string peerHandle, string peerDevice, string sessionId, string localNonce)
    {
        public string PeerHandle { get; } = peerHandle;
        public string PeerDevice { get; } = peerDevice;
        public string SessionId { get; set; } = sessionId;
        public string LocalNonce { get; } = localNonce;
        public bool Established { get; set; }
        public DateTimeOffset LastInitAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

// ===========================================================================
// Online-replication runtime (Phase 1): the relay-metadata seam, the relay-backed authoritative
// roster, and the bounded presence poller that starts engine sessions. These types depend only on
// the engine, Mesh.Shared contracts and this metadata seam (no MAUI / SignalR), so they compile and
// are unit-tested in the App.Tests assembly with a fake metadata source and a real engine.
// ===========================================================================

/// <summary>Handle normalisation identical to the app's, kept dependency-free for the runtime/tests.</summary>
internal static class ReplicationHandle
{
    public static string Norm(string? handle)
        => (handle ?? "").Trim().TrimStart('@').ToLowerInvariant();
}

/// <summary>
/// Byte-for-byte replica of <c>Mesh.Relay.Hub.RelayConnectChallenge.Canonical</c> (domain
/// <c>mesh.relay.connect.v9</c>, length-prefixed; field order nonce, handle, deviceId,
/// protocolVersion, authGeneration, custodyHead). The app does not reference the relay assembly, so
/// the client rebuilds this to sign the connect challenge; the two must stay in lockstep or connect
/// fails. A test asserts equality against the real relay canonical (which App.Tests does reference).
/// </summary>
public static class ReplicationConnectChallenge
{
    public const string Domain = "mesh.relay.connect.v9";

    public static string Canonical(
        string nonce,
        string handle,
        string deviceId,
        int protocolVersion,
        long authGeneration,
        string custodyHead)
    {
        var sb = new System.Text.StringBuilder(Domain);
        Append(nonce);
        Append(handle);
        Append(deviceId);
        Append(protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(authGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(custodyHead ?? "");
        return sb.ToString();

        void Append(string field)
        {
            field ??= "";
            sb.Append('|')
              .Append(field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(':')
              .Append(field);
        }
    }
}

/// <summary>
/// Pre-engine validation of a relay-stamped delivery: the sender fields must be present and the
/// delivery must be addressed to this authenticated handle (and this device, when device-directed).
/// A spoofed or misrouted stamp is dropped before the engine runs its own roster/session checks.
/// </summary>
public static class ReplicationDeliveryGuard
{
    public static bool ValidateRoute(
        OnlineRelayDelivery? delivery, string myHandle, string myDevice, out string rejectReason)
    {
        if (delivery is null
            || string.IsNullOrWhiteSpace(delivery.FromHandle)
            || string.IsNullOrWhiteSpace(delivery.FromDevice)
            || string.IsNullOrWhiteSpace(delivery.ToHandle))
        {
            rejectReason = "missing route stamp";
            return false;
        }

        var mine = ReplicationHandle.Norm(myHandle);
        if (mine.Length == 0
            || !string.Equals(ReplicationHandle.Norm(delivery.ToHandle), mine, StringComparison.Ordinal))
        {
            rejectReason = "not addressed to this handle";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(delivery.ToDevice))
        {
            if (string.IsNullOrEmpty(myDevice)
                || !string.Equals(delivery.ToDevice, myDevice, StringComparison.Ordinal))
            {
                rejectReason = "not addressed to this device";
                return false;
            }
        }

        rejectReason = "";
        return true;
    }
}

/// <summary>Online presence for one queried handle: whether it has any live device and which ones.</summary>
public sealed record RelayHandlePresence(
    string Handle,
    bool Online,
    IReadOnlyList<string> Devices);

/// <summary>
/// The relay's answer to a ResolvePresence query. Structurally identical to the relay hub's
/// <c>OnlinePresenceSnapshot</c> so the typed SignalR client binds it by property name without the
/// app taking a dependency on the relay assembly.
/// </summary>
public sealed record RelayPresenceSnapshot(
    IReadOnlyList<RelayHandlePresence> Handles);

/// <summary>
/// The relay-metadata seam the roster and poller depend on: the directory entry for a handle
/// (authorised keys + custody authority) and online presence. Implemented over REST + hub by
/// <c>MeshClient</c>; a fake drives it in isolation tests.
/// </summary>
public interface IReplicationMetadataSource
{
    Task<HandleInfo?> FetchHandleAsync(string handle, CancellationToken ct);
    Task<IReadOnlyList<RelayHandlePresence>> ResolvePresenceAsync(IReadOnlyList<string> handles, CancellationToken ct);
}

/// <summary>
/// Raised when online replication cannot start or continue with authoritative custody: no open
/// database, no device identity, unfetchable relay authority, or a relay custody head that disagrees
/// with the local custody chain. Surfacing this fails closed (replication stays stopped) so a session
/// never runs with hardcoded, zeroed or stale custody.
/// </summary>
public sealed class OnlineReplicationError : Exception
{
    public OnlineReplicationError(string message) : base(message) { }
}

/// <summary>
/// The relay-backed authoritative device roster the engine consumes. Reads authorised device keys,
/// auth generation and custody head from the relay directory (via <see cref="IReplicationMetadataSource"/>)
/// and caches them briefly. The engine's interface is synchronous, so callers pre-warm the cache with
/// <see cref="RefreshAsync"/> (the presence poller does this before starting sessions). Unknown handles
/// report auth generation -1 so the engine's stale-generation guard does not falsely reject a peer
/// whose directory entry has not yet been fetched; a fetched handle reports its true generation.
/// </summary>
public sealed class RelayReplicationRoster : IRefreshableReplicationRoster
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly IReplicationMetadataSource source;
    private readonly string ownHandle;
    private readonly long ownAuthGeneration;
    private readonly string ownCustodyHead;
    private readonly Func<string, string?> localCustodyHead;
    private readonly Action<string> surface;
    private readonly Action onOwnAuthorityChanged;

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);

    public RelayReplicationRoster(
        IReplicationMetadataSource source,
        string ownHandle,
        long ownAuthGeneration,
        string ownCustodyHead,
        Func<string, string?> localCustodyHead,
        Action<string> surface,
        Action onOwnAuthorityChanged)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.ownHandle = ReplicationHandle.Norm(ownHandle);
        this.ownAuthGeneration = ownAuthGeneration;
        this.ownCustodyHead = ownCustodyHead ?? "";
        this.localCustodyHead = localCustodyHead ?? (_ => null);
        this.surface = surface ?? (_ => { });
        this.onOwnAuthorityChanged = onOwnAuthorityChanged ?? (() => { });
    }

    private sealed record Entry(
        IReadOnlyList<ReplicationDevice> Devices,
        long AuthGeneration,
        string CustodyHead,
        DateTimeOffset FetchedAt);

    public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string accountHandle)
    {
        var h = ReplicationHandle.Norm(accountHandle);
        lock (gate)
            return cache.TryGetValue(h, out var e)
                ? e.Devices.Where(d => !d.Revoked).ToList()
                : Array.Empty<ReplicationDevice>();
    }

    public ReplicationDevice? ResolveDevice(string accountHandle, string deviceId)
    {
        var h = ReplicationHandle.Norm(accountHandle);
        lock (gate)
            return cache.TryGetValue(h, out var e)
                ? e.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal))
                : null;
    }

    public long AuthGeneration(string accountHandle)
    {
        var h = ReplicationHandle.Norm(accountHandle);
        lock (gate)
            return cache.TryGetValue(h, out var e) ? e.AuthGeneration : -1;
    }

    /// <summary>Drops any cached entry for a handle so the next refresh re-fetches it.</summary>
    public void Invalidate(string accountHandle)
    {
        var h = ReplicationHandle.Norm(accountHandle);
        lock (gate) cache.Remove(h);
    }

    /// <summary>Drops the entire cache (e.g. on account switch).</summary>
    public void Clear()
    {
        lock (gate) cache.Clear();
    }

    /// <summary>
    /// Refreshes the roster cache for the supplied handles from the relay directory, re-fetching any
    /// entry that is missing or older than the cache lifetime. Cross-checks this handle's own custody
    /// head against the local chain and raises the authority-changed callback on an auth-generation or
    /// custody-head shift so the client can re-arm under fresh authority (revocation handling).
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<string> handles, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var toFetch = new List<string>();
        lock (gate)
        {
            foreach (var raw in handles)
            {
                var h = ReplicationHandle.Norm(raw);
                if (h.Length == 0) continue;
                if (cache.TryGetValue(h, out var e) && now - e.FetchedAt < CacheLifetime) continue;
                if (!toFetch.Contains(h)) toFetch.Add(h);
            }
        }

        foreach (var h in toFetch)
        {
            ct.ThrowIfCancellationRequested();
            var info = await source.FetchHandleAsync(h, ct).ConfigureAwait(false);
            if (info is null)
            {
                surface($"directory entry for '{h}' is unavailable");
                continue;
            }

            var devices = (info.DevicePublicKeys ?? Array.Empty<string>())
                .Where(pk => !string.IsNullOrWhiteSpace(pk))
                .Select(pk => new ReplicationDevice(h, DeviceProtocol.DeviceId(pk), pk, info.AuthGeneration, Revoked: false))
                .ToList();

            lock (gate)
                cache[h] = new Entry(devices, info.AuthGeneration, info.CustodyHead ?? "", DateTimeOffset.UtcNow);

            if (string.Equals(h, ownHandle, StringComparison.Ordinal))
                CrossCheckOwnAuthority(info);
        }
    }

    private void CrossCheckOwnAuthority(HandleInfo info)
    {
        var relayHead = info.CustodyHead ?? "";
        var local = localCustodyHead(ownHandle) ?? "";
        var authorityMoved = info.AuthGeneration != ownAuthGeneration
            || !string.Equals(relayHead, ownCustodyHead, StringComparison.Ordinal);
        var localDisagrees = !CustodyHeadsAgree(local, relayHead);

        if (localDisagrees)
            surface("local custody chain disagrees with the relay custody head");
        if (authorityMoved || localDisagrees)
        {
            surface("own custody authority changed; re-arming");
            onOwnAuthorityChanged();
        }
    }

    private static bool CustodyHeadsAgree(string local, string relay)
    {
        static bool IsEmpty(string v) => string.IsNullOrEmpty(v)
            || string.Equals(v, OnlineReplicationProtocol.ZeroHash, StringComparison.Ordinal);
        if (IsEmpty(local) && IsEmpty(relay)) return true;
        return string.Equals(local, relay, StringComparison.Ordinal);
    }
}

/// <summary>
/// Bounded presence poller (spec item 4). Derives target handles from the local outbox candidates and
/// this account's authorised siblings, resolves their online devices via the relay, warms the roster,
/// and starts an engine session to every online authorised device it is not already talking to.
/// Pending work also sends a rate-limited authenticated wake request for authorised offline devices;
/// the relay emits only a contentless native wake and stores no application payload.
///
/// Cadence: it polls immediately on start / resume / poke; while there is pending work (a due outbox
/// or an online authorised peer) it re-polls on a short interval (~2s) with mild exponential backoff
/// and jitter; when idle it falls back to a slow interval (~20s). Pausing (background) stops the
/// continuous loop; resuming (foreground) restarts it with an immediate poll.
/// </summary>
public sealed class ReplicationPresencePoller : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PendingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxPendingInterval = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan OfflineWakeInterval = TimeSpan.FromMinutes(1);

    private readonly OnlineReplicationEngine engine;
    private readonly RelayReplicationRoster roster;
    private readonly IReplicationMetadataSource source;
    private readonly Func<IReadOnlyList<string>> candidateHandles;
    private readonly Func<IReadOnlyCollection<string>, bool> hasDueOutbox;
    private readonly string ownHandle;
    private readonly string ownDevice;
    private readonly Action<string> surface;
    private readonly Func<ReplicationBootstrapTarget, CancellationToken, Task>? bootstrapPeer;
    private readonly Action<bool, bool>? pollCompleted;

    private readonly object gate = new();
    private readonly Random jitter = new();
    private CancellationTokenSource? loopCts;
    private Task? loopTask;
    private TaskCompletionSource<bool>? poke;
    private readonly Dictionary<string, DateTimeOffset> offlineWakes = new(StringComparer.Ordinal);
    private Task? disposeTask;
    private bool runningRequested;
    private int backoffStep;
    private volatile bool onlineAuthorizedPeer;
    private volatile bool immediatelyDeliverableWork;
    private volatile bool pendingSynchronizationWork;
    private string? lastOnlinePeerDevice;
    private bool disposed;

    public ReplicationPresencePoller(
        OnlineReplicationEngine engine,
        RelayReplicationRoster roster,
        IReplicationMetadataSource source,
        Func<IReadOnlyList<string>> candidateHandles,
        Func<IReadOnlyCollection<string>, bool> hasDueOutbox,
        string ownHandle,
        string ownDevice,
        Action<string> surface,
        Func<ReplicationBootstrapTarget, CancellationToken, Task>? bootstrapPeer = null,
        Action<bool, bool>? pollCompleted = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.roster = roster ?? throw new ArgumentNullException(nameof(roster));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.candidateHandles = candidateHandles ?? throw new ArgumentNullException(nameof(candidateHandles));
        this.hasDueOutbox = hasDueOutbox ?? throw new ArgumentNullException(nameof(hasDueOutbox));
        this.ownHandle = ReplicationHandle.Norm(ownHandle);
        this.ownDevice = ownDevice ?? "";
        this.surface = surface ?? (_ => { });
        this.bootstrapPeer = bootstrapPeer;
        this.pollCompleted = pollCompleted;
    }

    public bool HasOnlineAuthorizedPeer => onlineAuthorizedPeer;
    public bool HasImmediatelyDeliverableWork => immediatelyDeliverableWork;
    public bool HasPendingSynchronizationWork => pendingSynchronizationWork;
    public string? LastOnlinePeerDevice => Volatile.Read(ref lastOnlinePeerDevice);

    /// <summary>Starts (or restarts) the polling loop, polling immediately.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (disposed) return;
            runningRequested = true;
            StartLoopUnderLock();
        }
    }

    private void StartLoopUnderLock()
    {
        if (disposed || loopTask is { IsCompleted: false }) return;
        var cts = new CancellationTokenSource();
        loopCts = cts;
        poke = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backoffStep = 0;
        loopTask = Task.Run(() => RunOwnedLoopAsync(cts));
    }

    private async Task RunOwnedLoopAsync(CancellationTokenSource owner)
    {
        try
        {
            await RunAsync(owner.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(loopCts, owner))
                {
                    loopCts = null;
                    loopTask = null;
                }
                owner.Dispose();
                if (runningRequested && !disposed) StartLoopUnderLock();
            }
        }
    }

    /// <summary>Resume continuous polling (foreground); polls immediately.</summary>
    public void Resume() => Start();

    /// <summary>Stop continuous polling (background) without disposing the poller.</summary>
    public void Pause()
    {
        CancellationTokenSource? cts;
        lock (gate)
        {
            runningRequested = false;
            cts = loopCts;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Requests an immediate poll on the next loop iteration.</summary>
    public void Poke()
    {
        lock (gate) poke?.TrySetResult(true);
    }

    /// <summary>Runs one poll pass synchronously (used by tests and the immediate-on-connect path).</summary>
    public Task<bool> PollOnceAsync(CancellationToken ct) => PollCoreAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool pending;
            try
            {
                pending = await PollCoreAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                surface($"poll iteration failed: {ex.Message}");
                pending = true;
            }

            var delay = NextDelay(pending);
            try { await WaitAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> PollCoreAsync(CancellationToken ct)
    {
        var candidates = candidateHandles()
            .Select(ReplicationHandle.Norm)
            .Where(handle => handle.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        onlineAuthorizedPeer = false;
        immediatelyDeliverableWork = false;
        pendingSynchronizationWork = false;
        Volatile.Write(ref lastOnlinePeerDevice, null);
        if (candidates.Length == 0)
        {
            ReportPollCompleted(hasOnlinePeer: false, hasPendingWork: false);
            return false;
        }

        await roster.RefreshAsync(candidates, ct).ConfigureAwait(false);
        var presence = await source.ResolvePresenceAsync(candidates, ct).ConfigureAwait(false);
        var dueHandles = new HashSet<string>(StringComparer.Ordinal);
        var pendingDevices = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var handlePending = false;
            try
            {
                handlePending = hasDueOutbox(new[] { candidate });
            }
            catch (Exception ex)
            {
                surface($"pending work check failed for {candidate}: {ex.Message}");
                handlePending = true;
            }

            foreach (var peer in roster.AuthorizedDevices(candidate))
            {
                if (peer.Revoked || string.Equals(peer.DeviceId, ownDevice, StringComparison.Ordinal))
                    continue;
                try
                {
                    var peerPending = engine.HasPendingWorkForPeer(peer);
                    pendingDevices[peer.DeviceId] = peerPending;
                    handlePending |= peerPending;
                }
                catch (Exception ex)
                {
                    pendingDevices[peer.DeviceId] = true;
                    handlePending = true;
                    surface($"pending device check failed for {peer.DeviceId}: {ex.Message}");
                }
            }
            if (handlePending) dueHandles.Add(candidate);
        }

        var onlineAuthorized = false;
        var onlinePending = false;
        foreach (var handle in presence)
        {
            var handleName = ReplicationHandle.Norm(handle.Handle);
            if (handleName.Length == 0) continue;
            var onlineDevices = handle.Online
                ? new HashSet<string>(handle.Devices ?? Array.Empty<string>(), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var device in onlineDevices)
            {
                if (string.IsNullOrWhiteSpace(device)) continue;
                MarkPeerOnline(device);
                if (string.Equals(device, ownDevice, StringComparison.Ordinal)) continue;
                var resolved = roster.ResolveDevice(handleName, device);
                if (resolved is null || resolved.Revoked) continue;
                onlineAuthorized = true;
                onlineAuthorizedPeer = true;
                var peerPending = pendingDevices.TryGetValue(device, out var pending) && pending;
                onlinePending |= peerPending;
                Volatile.Write(ref lastOnlinePeerDevice, device);
                ReplicationDiagnostics.Record(
                    "presence.peer_online",
                    ("peer_handle", handleName),
                    ("peer_device_id", device));
                try
                {
                    await engine.StartSessionAsync(handleName, device, ct).ConfigureAwait(false);
                    if (peerPending && engine.IsSessionEstablished(device))
                        await engine.OfferPeerAsync(handleName, device, ct).ConfigureAwait(false);
                    if (bootstrapPeer is not null
                        && engine.IsSessionEstablished(device)
                        && string.Equals(handleName, ownHandle, StringComparison.Ordinal))
                    {
                        await bootstrapPeer(
                            ReplicationBootstrapTarget.Create(resolved, engine.LocalIdentity),
                            ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    surface($"start session to {device} failed: {ex.Message}");
                }
            }

            if (!dueHandles.Contains(handleName)) continue;
            foreach (var peer in roster.AuthorizedDevices(handleName))
            {
                if (peer.Revoked
                    || string.Equals(peer.DeviceId, ownDevice, StringComparison.Ordinal)
                    || onlineDevices.Contains(peer.DeviceId)
                    || !pendingDevices.TryGetValue(peer.DeviceId, out var peerPending)
                    || !peerPending
                    || !ShouldWakeOfflinePeer(peer.DeviceId, DateTimeOffset.UtcNow))
                    continue;

                ReplicationDiagnostics.Record(
                    "presence.offline_wake",
                    ("peer_handle", handleName),
                    ("peer_device_id", peer.DeviceId));
                try
                {
                    await engine.OnWakeAsync(handleName, peer.DeviceId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    surface($"wake request to {peer.DeviceId} failed: {ex.Message}");
                }
            }
        }

        immediatelyDeliverableWork = onlinePending;
        pendingSynchronizationWork = dueHandles.Count > 0;
        ReportPollCompleted(onlinePending, pendingSynchronizationWork);
        return onlineAuthorized || pendingSynchronizationWork;
    }

    private bool ShouldWakeOfflinePeer(string peerDeviceId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (offlineWakes.TryGetValue(peerDeviceId, out var previous)
                && now - previous < OfflineWakeInterval)
                return false;
            offlineWakes[peerDeviceId] = now;
            return true;
        }
    }

    private void MarkPeerOnline(string peerDeviceId)
    {
        lock (gate) offlineWakes.Remove(peerDeviceId);
    }

    private void ReportPollCompleted(bool hasOnlinePeer, bool hasPendingWork)
    {
        try { pollCompleted?.Invoke(hasOnlinePeer, hasPendingWork); }
        catch (Exception ex) { surface($"poll completion callback failed: {ex.Message}"); }
    }

    private TimeSpan NextDelay(bool pending)
    {
        if (!pending)
        {
            lock (gate) backoffStep = 0;
            return IdleInterval;
        }

        int step;
        lock (gate) step = Math.Min(++backoffStep, 4);
        var scaled = TimeSpan.FromMilliseconds(PendingInterval.TotalMilliseconds * Math.Pow(1.5, step - 1));
        if (scaled > MaxPendingInterval) scaled = MaxPendingInterval;
        var jitterMs = jitter.Next(0, 400);
        return scaled + TimeSpan.FromMilliseconds(jitterMs);
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        Task pokeTask;
        lock (gate)
        {
            poke ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pokeTask = poke.Task;
        }
        var completed = await Task.WhenAny(pokeTask, Task.Delay(delay, ct)).ConfigureAwait(false);
        if (completed == pokeTask)
        {
            lock (gate) poke = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        Task task;
        Task? loop;
        CancellationTokenSource? cts;
        TaskCompletionSource<bool> completion;
        lock (gate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            disposed = true;
            runningRequested = false;
            cts = loopCts;
            loop = loopTask;
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            disposeTask = task;
        }

        _ = CompleteDisposeAsync(cts, loop, completion);
        return new ValueTask(task);
    }

    private static async Task CompleteDisposeAsync(
        CancellationTokenSource? cts,
        Task? loop,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { }
            if (loop is not null) await loop.ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
}
