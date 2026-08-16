using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Protocol-9 online replication hooks on <see cref="AppState"/>.
///
/// Every local domain change is carried to the background persistence worker as an immutable
/// replication descriptor. The worker performs the actual domain SQL write, the signed local
/// event and its per-target outbox references in ONE SQLite transaction, so a local change can
/// never claim replication success if journaling failed, and can never leave a sequence hole.
///
/// Inbound events are materialised onto the same actual domain tables inside the transaction that
/// appends the event and advances the cursor, and each committed wire batch is then applied to the
/// in-memory profile on a serialized state gate before one coalesced UI notification.
/// </summary>
public sealed partial class AppState
{
    private OnlineReplicationEngine? replicationEngine;
    private readonly SemaphoreSlim ownerBootstrapGate = new(1, 1);
    private readonly AsyncLocal<ReplicationNotificationBatch?> replicationNotificationBatch = new();

    private sealed class ReplicationNotificationBatch
    {
        public bool Pending { get; set; }
    }

    /// <summary>
    /// The database the attached engine was started against. A post-commit callback whose database
    /// is no longer the active one belongs to a switched-away or closed account and is ignored.
    /// </summary>
    private MeshDb? replicationEngineDb;

    /// <summary>
    /// Raised immediately before the active account's database is closed (account switch or delete),
    /// so a listener (the online-replication runtime) can tear down peer sessions and the roster cache
    /// while the old database is still open. Fires on the caller's thread before disposal.
    /// </summary>
    public event Action? ActiveAccountChanging;

    /// <summary>Signals listeners that the active account is about to change. Called off the hot path.</summary>
    private void RaiseActiveAccountChanging()
    {
        lock (localJournalGate) { localJournal = null; localJournalDb = null; }
        // A misbehaving listener must not abort the account switch, but its failure is surfaced
        // rather than swallowed.
        try { ActiveAccountChanging?.Invoke(); }
        catch (Exception ex) { RecordError(ex); }
    }

    /// <summary>The attached replication engine, if online replication has been started.</summary>
    public OnlineReplicationEngine? ReplicationEngine => replicationEngine;

    /// <summary>
    /// Constructs and attaches the Protocol-9 engine over the supplied relay transport and
    /// authoritative roster, binding it to the currently open local database and this device's
    /// desktop capability. Idempotent, and fail-closed: returns null (no engine attached) when no
    /// account database is open, so replication never starts without durable local storage.
    /// The mobile/desktop capability drives the codec's desktop-only asset/package policy.
    /// </summary>
    public OnlineReplicationEngine? TryStartOnlineReplication(
        IReplicationTransport transport,
        IReplicationRoster roster,
        ReplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(identity);

        if (replicationEngine is not null) return replicationEngine;

        MeshDb? db;
        lock (profileSyncGate) db = activeDb;
        if (db is null) return null;

        var engine = new OnlineReplicationEngine(
            db, identity, transport, roster, CreateReplicationApplier(),
            deviceIsDesktop: !PlatformCaps.IsMobile,
            deferSiblingOffersUntilBootstrap: true);
        engine.StateChanged += change =>
            RuntimeDiagnostics.Current?.RecordEvent(
                "replication",
                $"reason={change.Reason}; error={change.Error}");
        replicationEngine = engine;
        replicationEngineDb = db;
        return engine;
    }

    /// <summary>
    /// Tears down the attached engine (if any) and returns it so the caller can dispose it off the
    /// hot path. Idempotent. Used on disconnect / sign-out / active-account switch so peer sessions
    /// never outlive the identity or database they were established under.
    /// </summary>
    public OnlineReplicationEngine? DetachReplicationEngine()
    {
        var engine = replicationEngine;
        replicationEngine = null;
        replicationEngineDb = null;
        return engine;
    }

    /// <summary>
    /// Builds this device's authoritative <see cref="ReplicationIdentity"/> off the UI thread from the
    /// active profile keys, the local origin-log epoch, and the relay-authoritative custody authority
    /// (<paramref name="relayAuthGeneration"/> / <paramref name="relayCustodyHead"/>) the caller read
    /// from this handle's own directory entry. Fail-closed: throws <see cref="OnlineReplicationError"/>
    /// when no account database is open, when the profile has no device keypair, or when the relay
    /// custody head disagrees with the local custody chain head for this handle. Custody is never
    /// hardcoded or zeroed in a live session.
    /// </summary>
    public ReplicationIdentity BuildReplicationIdentity(long relayAuthGeneration, string relayCustodyHead)
    {
        relayCustodyHead ??= "";

        string handle, deviceId, publicKey, privateKey, logEpoch;
        lock (profileSyncGate)
        {
            var db = activeDb
                ?? throw new OnlineReplicationError("No account database is open; replication cannot start.");
            var profile = Profile;
            handle = Norm(profile.Handle);
            publicKey = profile.PublicKey;
            privateKey = profile.PrivateKey;
            if (string.IsNullOrWhiteSpace(handle)
                || string.IsNullOrWhiteSpace(publicKey)
                || string.IsNullOrWhiteSpace(privateKey))
                throw new OnlineReplicationError("The active profile has no device identity for replication.");
            deviceId = DeviceProtocol.DeviceId(publicKey);

            // Custody cross-check: the relay-reported custody head must agree with this device's own
            // local custody chain head for its handle. A fresh handle legitimately has an empty head
            // on both sides; a disagreement means stale or forged authority, so fail closed.
            var localHead = db.GetCustodyHead(handle)?.EntryHash ?? "";
            if (!CustodyHeadsAgree(localHead, relayCustodyHead))
                throw new OnlineReplicationError(
                    "The relay custody head does not match the local custody chain; refusing to start replication.");

            // The local origin log epoch is allocated once and is immutable thereafter (first epoch
            // wins). Registering is idempotent; the stored epoch is authoritative for emission.
            var candidate = Guid.NewGuid().ToString("n");
            db.EnsureLocalOrigin(deviceId, candidate, relayAuthGeneration);
            logEpoch = db.GetServeableOrigins()
                .FirstOrDefault(o => string.Equals(o.OriginDeviceId, deviceId, StringComparison.Ordinal))
                ?.LogEpoch ?? candidate;
        }

        return new ReplicationIdentity(
            handle, deviceId, publicKey, privateKey, logEpoch, relayAuthGeneration, relayCustodyHead);
    }

    private static bool CustodyHeadsAgree(string local, string relay)
    {
        // Treat the two "empty custody" sentinels (an empty string and the all-zero hash) as equal so
        // a genesis handle binds regardless of which representation each side uses.
        static bool IsEmpty(string v) => string.IsNullOrEmpty(v)
            || string.Equals(v, OnlineReplicationProtocol.ZeroHash, StringComparison.Ordinal);
        if (IsEmpty(local) && IsEmpty(relay)) return true;
        return string.Equals(local, relay, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distinct peer account handles replication should track: this account's own handle (its
    /// authorised sibling devices) plus every direct (non-group) conversation and contact handle.
    /// Read off the UI thread; used by the presence poller to derive which handles to resolve.
    /// </summary>
    public IReadOnlyList<string> ReplicationPeerCandidates()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        lock (profileSyncGate)
        {
            var profile = Profile;
            var own = Norm(profile.Handle);
            if (own.Length > 0) set.Add(own);
            foreach (var conv in profile.Conversations)
            {
                if (conv.IsGroup) continue;
                var h = Norm(conv.Handle);
                if (h.Length > 0) set.Add(h);
            }
            foreach (var contact in profile.Contacts)
            {
                var h = Norm(contact.Handle);
                if (h.Length > 0) set.Add(h);
            }
        }
        return set.ToList();
    }

    /// <summary>Builds the domain applier the engine drives.</summary>
    public IReplicationDomainApplier CreateReplicationApplier()
        => new AppStateReplicationApplier(this);

    /// <summary>
    /// Emits a local-origin replication event for a durable domain change. This NEVER silently
    /// no-ops (spec item 1): whenever the local account database is open it writes the actual
    /// domain rows, the signed event and its target-account outbox references in one SQLCipher
    /// transaction, regardless of any relay connection or attached engine. When a session engine
    /// is attached it reuses that engine's journal (freshest relay roster) and best-effort offers
    /// to live peers; otherwise it drains through the offline local-authority journal. If this
    /// device has no usable identity / custody authority it throws
    /// <see cref="ReplicationIdentityMissingException"/> so the local domain operation fails
    /// instead of falsely reporting replicated success.
    /// </summary>
    public async Task<string?> ReplicateLocalAsync(
        string kind,
        ReplicationPayloadCodec.DomainAction action,
        string entityId,
        string? conversationId,
        string causalVersion,
        string bodyJson,
        IReadOnlyCollection<string> targetAccounts,
        string pushClass = OnlinePushClasses.Normal,
        CancellationToken ct = default,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork = null,
        NotificationIntent? notificationIntent = null)
    {
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            kind, action, entityId, conversationId, causalVersion, bodyJson, notificationIntent);

        var engine = replicationEngine;
        if (engine is not null)
            return await engine.EmitLocalAsync(
                envelope,
                targetAccounts,
                pushClass,
                ct,
                domainWork).ConfigureAwait(false);

        // Offline / no session: write the immutable event + outbox directly to the open account
        // database. The engine will drain the pending outbox once a session is later established.
        var journal = EnsureLocalJournal();
        return journal.EmitLocal(envelope, targetAccounts, domainWork);
    }

    private ReplicationJournal? localJournal;
    private MeshDb? localJournalDb;

    /// <summary>
    /// Emits several local-origin events as ONE journal transaction (chunked transfers). Every
    /// event, its outbox references, the contiguous sequence allocation and the supplied domain
    /// work commit together, so a partial transfer is never observable.
    /// </summary>
    public Task<IReadOnlyList<string>> ReplicateLocalBatchAsync(
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, int>? domainWork = null,
        CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(
            () => EnsureLocalJournal().EmitLocalBatch(envelopes, targetAccounts, domainWork), ct);

    /// <summary>
    /// Replicates one local attachment to the owner's other devices as deterministic bounded
    /// Protocol 9 chunks. The actual local attachment staging row and every chunk event (with its
    /// outbox references and sequence allocation) commit in ONE transaction, so an attachment is
    /// never staged without its durable pending transfer. No relay blob storage is involved.
    /// </summary>
    public async Task<int> ReplicateAttachmentAsync(
        string attachmentId,
        string runId,
        string name,
        string mimeType,
        byte[] bytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        ArgumentNullException.ThrowIfNull(bytes);

        MeshDb? db;
        lock (profileSyncGate) db = activeDb;
        if (db is null) throw new InvalidOperationException("No account database is open.");

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var chunks = SkillPackageTransfer.Chunk(bytes, (int)ReplicationDomainStore.MaxChunkBytes);
        var entityId = ReplicationDomainMaterializer.AttachmentEntityId(attachmentId);
        var createdAt = DateTimeOffset.UtcNow;

        var envelopes = new List<ReplicationPayloadCodec.DomainEnvelope>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var body = JsonSerializer.Serialize(
                new ReplicationDomainStore.PackageChunkBody(
                    index, chunks.Count, bytes.LongLength, hash,
                    Convert.ToBase64String(chunks[index]), name, mimeType, runId),
                ReplicationJson);
            envelopes.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                entityId, null, NewReplicationVersion(), body));
        }

        var targets = TargetsForOwnerState();
        if (targets.Count == 0)
        {
            await Task.Run(() => db.SaveReplicatedAttachment(
                attachmentId, runId, name, mimeType, hash, bytes, createdAt), ct).ConfigureAwait(false);
            return 0;
        }

        await ReplicateLocalBatchAsync(envelopes, targets, (conn, tx, index) =>
        {
            if (index == 0)
                Protocol9DomainTables.UpsertReplicatedAttachment(
                    conn, tx, attachmentId, runId, name, mimeType, hash, bytes, createdAt);
        }, ct).ConfigureAwait(false);

        return chunks.Count;
    }
    private readonly object localJournalGate = new();

    /// <summary>
    /// Initialises this account's genesis custody / local authority so the offline journal has a
    /// real custody head to sign under from the first local change (spec item 1). Idempotent, and
    /// safe to call on every account open. Returns the custody head hash, or null when no account
    /// database or device keypair is available yet.
    /// </summary>
    public string? EnsureLocalReplicationAuthority()
    {
        lock (profileSyncGate)
        {
            var db = activeDb;
            if (db is null) return null;
            var profile = Profile;
            var handle = Norm(profile.Handle);
            var publicKey = profile.PublicKey;
            var privateKey = profile.PrivateKey;
            if (string.IsNullOrWhiteSpace(handle)
                || string.IsNullOrWhiteSpace(publicKey)
                || string.IsNullOrWhiteSpace(privateKey))
                return null;
            return db.InitializeGenesisCustody(handle, publicKey, privateKey);
        }
    }

    /// <summary>
    /// Builds (or returns the cached) offline local-authority journal bound to the open account
    /// database. The cached local AuthGeneration / custody head are read from the durable custody
    /// chain (updated by relay auth refresh); a missing chain fails closed with a re-onboard error.
    /// </summary>
    private ReplicationJournal EnsureLocalJournal()
    {
        MeshDb db;
        string handle, publicKey, privateKey;
        lock (profileSyncGate)
        {
            db = activeDb
                ?? throw new ReplicationIdentityMissingException(
                    "No account database is open; a local change cannot be replicated.");
            var profile = Profile;
            handle = Norm(profile.Handle);
            publicKey = profile.PublicKey;
            privateKey = profile.PrivateKey;
        }
        if (string.IsNullOrWhiteSpace(handle)
            || string.IsNullOrWhiteSpace(publicKey)
            || string.IsNullOrWhiteSpace(privateKey))
            throw new ReplicationIdentityMissingException(
                "The active profile has no device identity for replication; re-onboard the account.");

        var deviceId = DeviceProtocol.DeviceId(publicKey);

        // Greenfield onboarding: a valid device keypair with no custody chain yet initialises its
        // genesis local authority (idempotent). Only a missing keypair is an unrecoverable identity
        // failure that must fail the local domain operation rather than falsely report success.
        var head = db.GetCustodyHead(handle);
        if (head is null)
        {
            db.InitializeGenesisCustody(handle, publicKey, privateKey);
            head = db.GetCustodyHead(handle)
                ?? throw new ReplicationIdentityMissingException(
                    "The account has no local custody authority; re-onboard the account.");
        }

        // The local origin log epoch is allocated once and immutable thereafter (first epoch wins).
        var candidate = Guid.NewGuid().ToString("n");
        db.EnsureLocalOrigin(deviceId, candidate, head.Generation);
        var logEpoch = db.GetServeableOrigins()
            .FirstOrDefault(o => string.Equals(o.OriginDeviceId, deviceId, StringComparison.Ordinal))
            ?.LogEpoch ?? candidate;

        lock (localJournalGate)
        {
            if (localJournal is not null
                && ReferenceEquals(localJournalDb, db)
                && string.Equals(localJournal.Identity.DeviceId, deviceId, StringComparison.Ordinal)
                && string.Equals(localJournal.Identity.CustodyHead, head.EntryHash, StringComparison.Ordinal)
                && localJournal.Identity.AuthGeneration == head.Generation)
                return localJournal;

            var identity = new ReplicationIdentity(
                handle, deviceId, publicKey, privateKey, logEpoch, head.Generation, head.EntryHash);
            localJournal = new ReplicationJournal(
                db, identity, new LocalCustodyRoster(db), deviceIsDesktop: !PlatformCaps.IsMobile);
            localJournalDb = db;
            return localJournal;
        }
    }

    /// <summary>The UI-facing delivery state of a replicated event toward a target account.</summary>
    public ReplicationDeliveryState GetReplicationDeliveryState(string eventId, string targetAccount)
        => replicationEngine?.GetDeliveryState(eventId, targetAccount) ?? ReplicationDeliveryState.Unknown;

    /// <summary>The local custody-chain head hash for a handle, or null when none is stored. Read off UI.</summary>
    public string? LocalCustodyHead(string handle)
    {
        var h = Norm(handle);
        lock (profileSyncGate)
            return activeDb?.GetCustodyHead(h)?.EntryHash;
    }

    /// <summary>Returns the signed local custody authority for registration or re-assertion.</summary>
    public CustodyEntry? LocalCustodyAuthority(string handle)
    {
        var h = Norm(handle);
        lock (profileSyncGate)
            return activeDb?.GetCustodyHead(h);
    }

    /// <summary>Imports relay-published signed custody metadata after device linking or recovery.</summary>
    public void ImportCustodyAuthority(string handle, CustodyEntry authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var h = Norm(handle);
        if (!string.Equals(Norm(authority.Handle), h, StringComparison.Ordinal))
            throw new OnlineReplicationError("The relay returned custody authority for a different handle.");
        if (!OnlineReplicationProtocol.VerifyCustodyEntry(authority, authority.SignerKey))
            throw new OnlineReplicationError("The relay returned invalid custody authority.");
        lock (profileSyncGate)
        {
            var db = activeDb
                ?? throw new OnlineReplicationError("No account database is open for custody import.");
            db.AppendCustodyEntry(authority);
        }
    }

    /// <summary>Emits or resumes one durable owner-state bootstrap for an established sibling session.</summary>
    public async Task EmitOwnerBootstrapSnapshotAsync(
        ReplicationBootstrapTarget target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        await ownerBootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            OnlineReplicationEngine? engine;
            MeshDb? db;
            lock (profileSyncGate)
            {
                engine = replicationEngine;
                db = activeDb;
                if (engine is null
                    || db is null
                    || !string.Equals(Norm(target.PeerHandle), Norm(Profile.Handle), StringComparison.Ordinal))
                    return;
            }
            if (!engine.IsSessionEstablished(target.PeerDeviceId)) return;

            const int chunkSize = 50;
            var marker = db.GetPeerBootstrap(target);
            var needsFreshSnapshot = marker is null
                || !string.Equals(marker.LocalOriginDeviceId, target.LocalOriginDeviceId, StringComparison.Ordinal)
                || !string.Equals(marker.LocalLogEpoch, target.LocalLogEpoch, StringComparison.Ordinal)
                || marker.BootstrapFromSeq == 0;

            if (needsFreshSnapshot)
            {
                using var projectionBoundary = await engine.EnterProjectionBoundaryAsync(ct).ConfigureAwait(false);
                lock (profileSyncGate)
                {
                    if (!ReferenceEquals(db, activeDb)
                        || !ReferenceEquals(engine, replicationEngine)
                        || !engine.IsSessionEstablished(target.PeerDeviceId)
                        || !string.Equals(Norm(target.PeerHandle), Norm(Profile.Handle), StringComparison.Ordinal))
                        return;

                    using var journalLock = db.EnterLocalOriginJournalLock();
                    var snapshot = CaptureOwnerBootstrapSnapshot(target.PeerHandle);
                    var snapshotJson = JsonSerializer.Serialize(snapshot, ReplicationJson);
                    var coverageJson = JsonSerializer.Serialize(
                        db.GetSnapshotCoverage(target.LocalOriginDeviceId),
                        ReplicationJson);
                    var stateHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
                    marker = db.CreateOrResumePeerBootstrap(
                        target,
                        Guid.NewGuid().ToString("n"),
                        stateHash,
                        snapshotJson,
                        snapshot.Count,
                        coverageJson);

                    if (snapshot.Count > 0 && marker.State != MeshDb.BootstrapStatePersisted)
                    {
                        var firstChunk = snapshot.Take(chunkSize).ToList();
                        EmitBootstrapChunk(
                            engine,
                            db,
                            target,
                            marker,
                            firstChunk,
                            firstChunk.Count,
                            snapshot.Count);
                        marker = db.GetPeerBootstrap(target)
                            ?? throw new InvalidOperationException("The bootstrap marker disappeared after its first chunk committed.");
                    }
                    else if (snapshot.Count == 0)
                    {
                        marker = db.CompleteEmptyPeerBootstrap(
                            target,
                            marker.BootstrapId,
                            db.GetLocalOriginNextSeq(target.LocalOriginDeviceId, target.LocalLogEpoch));
                    }
                }
            }

            if (marker is null) return;
            engine.ReportBootstrapActivity("bootstrap.started", target, marker.BootstrapId);
            if (marker.State == MeshDb.BootstrapStatePersisted)
            {
                engine.ReportBootstrapActivity("bootstrap.persisted", target, marker.BootstrapId, marker.TotalItems);
                return;
            }

            var actualStateHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(marker.SnapshotJson))).ToLowerInvariant();
            if (!string.Equals(actualStateHash, marker.StateHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The saved bootstrap snapshot failed its integrity check.");
            var items = JsonSerializer.Deserialize<List<ReplicationPayloadCodec.DomainEnvelope>>(
                            marker.SnapshotJson,
                            ReplicationJson)
                        ?? throw new InvalidOperationException("The saved bootstrap snapshot is invalid.");
            if (items.Count != marker.TotalItems)
                throw new InvalidOperationException("The saved bootstrap snapshot count is inconsistent.");
            if (marker.EmittedItems < 0 || marker.EmittedItems > items.Count)
                throw new InvalidOperationException("The saved bootstrap progress is inconsistent.");
            if (marker.EmittedItems > 0 && marker.BootstrapFromSeq == 0)
                throw new InvalidOperationException("The saved bootstrap is missing its first emitted sequence.");

            if (items.Count == 0)
            {
                engine.ReportBootstrapActivity("bootstrap.emitted", target, marker.BootstrapId);
                await engine.OfferPeerAsync(target.PeerHandle, target.PeerDeviceId, ct).ConfigureAwait(false);
                return;
            }

            if (needsFreshSnapshot && marker.EmittedItems > 0)
            {
                engine.ReportBootstrapActivity(
                    "bootstrap.progress",
                    target,
                    marker.BootstrapId,
                    marker.EmittedItems);
                await engine.OfferPeerAsync(target.PeerHandle, target.PeerDeviceId, ct).ConfigureAwait(false);
            }

            for (var offset = marker.EmittedItems; offset < items.Count; offset += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                lock (profileSyncGate)
                {
                    if (!ReferenceEquals(db, activeDb) || !ReferenceEquals(engine, replicationEngine))
                        throw new OperationCanceledException("The active replication account changed.", ct);
                }

                var chunk = items.Skip(offset).Take(chunkSize).ToList();
                var emittedThrough = offset + chunk.Count;
                await Task.Run(() => EmitBootstrapChunk(
                    engine,
                    db,
                    target,
                    marker,
                    chunk,
                    emittedThrough,
                    items.Count), ct).ConfigureAwait(false);

                engine.ReportBootstrapActivity(
                    "bootstrap.progress",
                    target,
                    marker.BootstrapId,
                    emittedThrough);
                await engine.OfferPeerAsync(target.PeerHandle, target.PeerDeviceId, ct).ConfigureAwait(false);
                await Task.Yield();
            }

            engine.ReportBootstrapActivity(
                "bootstrap.emitted",
                target,
                marker.BootstrapId,
                items.Count);
            await engine.OfferPeerAsync(target.PeerHandle, target.PeerDeviceId, ct).ConfigureAwait(false);
        }
        finally
        {
            ownerBootstrapGate.Release();
        }
    }

    private static void EmitBootstrapChunk(
        OnlineReplicationEngine engine,
        MeshDb db,
        ReplicationBootstrapTarget target,
        MeshDb.ReplicationPeerBootstrap marker,
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> chunk,
        int emittedThrough,
        int totalItems)
    {
        var bootstrapFrom = marker.BootstrapFromSeq;
        engine.Journal.EmitLocalBatch(
            chunk,
            new[] { target.PeerHandle },
            domainWork: static (_, _, _) => { },
            eventWork: (_, tx, evt, index) =>
            {
                if (index == 0 && bootstrapFrom == 0)
                    bootstrapFrom = evt.Seq;
                if (index == chunk.Count - 1)
                    db.UpdatePeerBootstrapProgress(
                        target,
                        marker.BootstrapId,
                        emittedThrough,
                        totalItems,
                        bootstrapFrom,
                        evt.Seq,
                        tx);
            });
    }
    private List<ReplicationPayloadCodec.DomainEnvelope> CaptureOwnerBootstrapSnapshot(string accountHandle)
    {
        OwnerBootstrapSource source;
        var capture = Stopwatch.StartNew();
        lock (profileSyncGate)
        {
            if (!string.Equals(Norm(accountHandle), Norm(Profile.Handle), StringComparison.Ordinal))
                return new List<ReplicationPayloadCodec.DomainEnvelope>();
            source = new OwnerBootstrapSource(
                LocalDeviceId() ?? "local",
                DateTimeOffset.UtcNow,
                Profile.OwnThreads.Select(CloneBootstrapThread).ToList(),
                Profile.Conversations.Select(conversation => CloneBootstrapConversation(conversation)).ToList(),
                Profile.Contacts.Select(CloneBootstrapContact).ToList(),
                Profile.Circles.Select(circle => new Circle
                {
                    Name = circle.Name,
                    RequireApproval = circle.RequireApproval
                }).ToList(),
                Profile.Memories.Select(CloneBootstrapMemory).ToList());
        }
        capture.Stop();
        ReplicationDiagnostics.Record(
            "bootstrap.snapshot_captured",
            ("duration_ms", capture.ElapsedMilliseconds),
            ("topic_count", source.Threads.Count),
            ("conversation_count", source.Conversations.Count));

        string Version() => ProjectionVersion.Create(
            source.CapturedAt,
            source.SourceDeviceId,
            Guid.NewGuid().ToString("n"));

        var items = new List<ReplicationPayloadCodec.DomainEnvelope>();
        for (var sortOrder = 0; sortOrder < source.Threads.Count; sortOrder++)
        {
            var thread = source.Threads[sortOrder];
            var body = JsonSerializer.Serialize(new
            {
                thread.Id,
                thread.Title,
                thread.CreatedAt,
                SortOrder = sortOrder,
                thread.ExecutionDeviceId,
                thread.ExecutionDeviceName,
                thread.ExecutionDevicePlatform,
                thread.LastActivityAt,
                thread.IsPinned,
                thread.ExecutionAt,
                thread.ExecutionRunId
            }, ReplicationJson);
            items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                thread.Id,
                thread.Id,
                Version(),
                body, NotificationIntent.SuppressedHistorical));
            foreach (var line in thread.Lines)
                items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                    ReplicationOpKinds.Topic,
                    ReplicationPayloadCodec.DomainAction.AppendLine,
                    thread.Id,
                    thread.Id,
                    Version(),
                    JsonSerializer.Serialize(line, ReplicationJson), NotificationIntent.SuppressedHistorical));
        }

        foreach (var conversation in source.Conversations)
        {
            var handle = Norm(conversation.Handle);
            items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Conversation,
                ReplicationPayloadCodec.DomainAction.Upsert,
                handle,
                handle,
                Version(),
                JsonSerializer.Serialize(
                    CloneBootstrapConversation(conversation, includeLines: false),
                    ReplicationJson), NotificationIntent.SuppressedHistorical));
            foreach (var line in conversation.Lines)
                items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                    ReplicationOpKinds.Message,
                    ReplicationPayloadCodec.DomainAction.AppendLine,
                    handle,
                    handle,
                    Version(),
                    JsonSerializer.Serialize(line, ReplicationJson), NotificationIntent.SuppressedHistorical));
        }

        var projectionProfile = new MeshProfile
        {
            Contacts = source.Contacts,
            Circles = source.Circles
        };
        var projection = ProfileProjection.Snapshot(projectionProfile);
        foreach (var (entityId, circle) in projection.Circles.OrderBy(item => item.Key, StringComparer.Ordinal))
            items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Circle,
                ReplicationPayloadCodec.DomainAction.Upsert,
                entityId,
                null,
                Version(),
                JsonSerializer.Serialize(circle, ReplicationJson), NotificationIntent.SuppressedHistorical));
        foreach (var (entityId, contact) in projection.Contacts.OrderBy(item => item.Key, StringComparer.Ordinal))
            items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Contact,
                ReplicationPayloadCodec.DomainAction.Upsert,
                entityId,
                null,
                Version(),
                JsonSerializer.Serialize(contact, ReplicationJson), NotificationIntent.SuppressedHistorical));
        foreach (var memory in source.Memories.OrderBy(item => item.Id, StringComparer.Ordinal))
            items.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Memory,
                ReplicationPayloadCodec.DomainAction.Upsert,
                memory.Id,
                null,
                Version(),
                JsonSerializer.Serialize(MemoryPolicy.ToSync(memory), ReplicationJson), NotificationIntent.SuppressedHistorical));
        return items;
    }

    private sealed record OwnerBootstrapSource(
        string SourceDeviceId,
        DateTimeOffset CapturedAt,
        List<OwnThread> Threads,
        List<Conversation> Conversations,
        List<Domain.Contact> Contacts,
        List<Circle> Circles,
        List<MemoryItem> Memories);

    private static OwnThread CloneBootstrapThread(OwnThread thread)
        => new()
        {
            Id = thread.Id,
            Title = thread.Title,
            CreatedAt = thread.CreatedAt,
            LastActivityAt = thread.LastActivityAt,
            IsPinned = thread.IsPinned,
            ExecutionDeviceId = thread.ExecutionDeviceId,
            ExecutionDeviceName = thread.ExecutionDeviceName,
            ExecutionDevicePlatform = thread.ExecutionDevicePlatform,
            ExecutionAt = thread.ExecutionAt,
            ExecutionRunId = thread.ExecutionRunId,
            Lines = thread.Lines.Where(static line => !line.Internal).Select(CloneBootstrapLine).ToList()
        };

    private static Conversation CloneBootstrapConversation(
        Conversation conversation,
        bool includeLines = true)
        => new()
        {
            Handle = conversation.Handle,
            CreatedAt = conversation.CreatedAt,
            LastActivityAt = conversation.LastActivityAt,
            IsPinned = conversation.IsPinned,
            GroupId = conversation.GroupId,
            GroupName = conversation.GroupName,
            GroupOwnerHandle = conversation.GroupOwnerHandle,
            GroupMembers = conversation.GroupMembers.ToList(),
            GroupVersion = conversation.GroupVersion,
            ServiceId = conversation.ServiceId,
            ServiceName = conversation.ServiceName,
            ProviderHandle = conversation.ProviderHandle,
            Lines = includeLines
                ? conversation.Lines.Where(static line => !line.Internal).Select(CloneBootstrapLine).ToList()
                : new List<ChatLine>()
        };

    private static ChatLine CloneBootstrapLine(ChatLine line)
        => new()
        {
            Id = line.Id,
            Role = line.Role,
            Text = line.Text,
            ReplyToLineId = line.ReplyToLineId,
            WidgetPrompt = line.WidgetPrompt,
            SenderHandle = line.SenderHandle,
            Via = line.Via,
            AddressedToAgent = line.AddressedToAgent,
            Status = line.Status,
            Reasoning = line.Reasoning,
            ModelId = line.ModelId,
            Internal = line.Internal,
            At = line.At
        };

    private static Domain.Contact CloneBootstrapContact(Domain.Contact contact)
        => new()
        {
            Handle = contact.Handle,
            DisplayName = contact.DisplayName,
            Circles = contact.Circles.ToList(),
            Allowed = contact.Allowed,
            SigningKeys = contact.SigningKeys.ToList(),
            KeyChanged = contact.KeyChanged,
            TokensSpent = contact.TokensSpent,
            Muted = contact.Muted,
            Blocked = contact.Blocked
        };

    private static MemoryItem CloneBootstrapMemory(MemoryItem memory)
        => new()
        {
            Id = memory.Id,
            Title = memory.Title,
            Content = memory.Content,
            Category = memory.Category,
            Origin = memory.Origin,
            Importance = memory.Importance,
            Confidence = memory.Confidence,
            Stability = memory.Stability,
            ReinforcementCount = memory.ReinforcementCount,
            SourceThreadId = memory.SourceThreadId,
            SourceLineId = memory.SourceLineId,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            LastReinforcedAt = memory.LastReinforcedAt
        };
    /// <summary>
    /// True when any of <paramref name="targetAccounts"/> has a pending outbox reference awaiting
    /// delivery. Read off the UI thread; the presence poller uses it to choose the fast (pending) vs
    /// slow (idle) polling cadence.
    /// </summary>
    public bool HasDueOutbox(IReadOnlyCollection<string> targetAccounts)
    {
        ArgumentNullException.ThrowIfNull(targetAccounts);
        lock (profileSyncGate)
        {
            var db = activeDb;
            if (db is null) return false;
            return db.CountUnpersistedOutbox(targetAccounts) > 0;
        }
    }

    public int CountPendingReplicationEvents(IReadOnlyCollection<string>? targetAccounts = null)
    {
        lock (profileSyncGate)
        {
            var db = activeDb;
            if (db is null) return 0;
            var targets = targetAccounts ?? TargetsForOwnerState();
            return db.CountUnpersistedOutbox(targets);
        }
    }

    public MeshDb.ReplicationSyncCheckpoint? GetLastSuccessfulReplication()
    {
        lock (profileSyncGate)
        {
            var db = activeDb;
            var handle = Norm(Profile.Handle);
            return db is null || handle.Length == 0
                ? null
                : db.GetLastSuccessfulReplication(handle);
        }
    }

    public bool HasReplicationSibling()
    {
        lock (profileSyncGate)
        {
            var db = activeDb;
            var handle = Norm(Profile.Handle);
            var localDevice = LocalDeviceId();
            if (db is null || handle.Length == 0 || string.IsNullOrWhiteSpace(localDevice)) return false;
            return new LocalCustodyRoster(db).AuthorizedDevices(handle)
                .Any(device => !string.Equals(device.DeviceId, localDevice, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Applies one COMMITTED replicated envelope to the live in-memory profile on the serialized
    /// state gate. The batch wrapper coalesces any resulting change notifications. Ignored when the callback belongs
    /// to a database that is no longer active (account switch, sign-out or disconnect), so stale
    /// replication traffic can never mutate the new account's state.
    ///
    /// <c>applyingReplicationProjection</c> is set only for the duration of this update so the
    /// mutation can never echo back out as a new local replication event, and is always reset.
    /// </summary>
    private async Task ApplyReplicatedStateAfterCommitAsync(
        MeshDb? sourceDb,
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(envelope);

        lock (profileSyncGate)
        {
            if (sourceDb is null || !ReferenceEquals(sourceDb, activeDb)) return;
            envelope = ReplicationInboundProjection.ForLocalAccount(evt, envelope, Profile.Handle);
        }

        if (envelope.Action is ReplicationPayloadCodec.DomainAction.AssetUpsert
            or ReplicationPayloadCodec.DomainAction.AssetDelete)
        {
            if (!TryParseAssetEntityId(envelope.EntityId, out var kind, out var id))
                throw new ReplicationProjectionException(
                    $"Committed asset entity id '{envelope.EntityId}' was invalid.");
            await RefreshAssetFromStoreAsync(kind, id).ConfigureAwait(false);
            return;
        }

        if (envelope.Action == ReplicationPayloadCodec.DomainAction.PackageTransfer)
        {
            await RefreshAssetFromStoreAsync(AssetKind.Skill, envelope.EntityId).ConfigureAwait(false);
            return;
        }

        if (envelope.Action is ReplicationPayloadCodec.DomainAction.AskUserPrompt
            or ReplicationPayloadCodec.DomainAction.AskUserResolve)
        {
            var prompt = sourceDb!.GetAskUserPrompt(envelope.EntityId);
            if (prompt is null) return;
            if (prompt.State == AskUserState.Pending)
            {
                UpsertAskUserView(prompt);
                NotifyChanged();
                await PublishNotificationAfterCommitAsync(evt, envelope).ConfigureAwait(false);
            }
            else
            {
                await ApplyAskUserResolvedAsync(prompt, CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        bool changed;
        var markConversationUnread = false;
        lock (profileSyncGate)
        {
            if (!ReferenceEquals(sourceDb, activeDb)) return;
            var previous = applyingReplicationProjection;
            applyingReplicationProjection = true;
            try
            {
                changed = ReplicationProfileMaterializer.Apply(Profile, envelope);
                if (changed
                    && envelope.Kind == ReplicationOpKinds.Topic
                    && envelope.Action == ReplicationPayloadCodec.DomainAction.AppendLine)
                {
                    var line = JsonSerializer.Deserialize<ChatLine>(envelope.BodyJson, ReplicationJson);
                    var thread = Profile.OwnThreads.FirstOrDefault(item =>
                        string.Equals(item.Id, envelope.EntityId, StringComparison.Ordinal));
                    if (line is { Role: "assistant" } && thread is not null)
                        changed |= ReconcileTopicRunWithAnswer(
                            thread,
                            line.ReplyToLineId,
                            line.At == default ? DateTimeOffset.UtcNow : line.At);
                }
                if (changed
                    && envelope.Kind == ReplicationOpKinds.Message
                    && envelope.Action == ReplicationPayloadCodec.DomainAction.AppendLine
                    && !string.Equals(Norm(evt.OriginAccount), Norm(Profile.Handle), StringComparison.Ordinal))
                {
                    var line = JsonSerializer.Deserialize<ChatLine>(envelope.BodyJson, ReplicationJson);
                    markConversationUnread = ReplicationUnreadPolicy.ShouldMarkConversationUnread(line?.Role);
                    if (markConversationUnread)
                    {
                        var conversation = Norm(envelope.ConversationId ?? envelope.EntityId);
                        if (unread.Add(conversation) && !Profile.UnreadFrom.Contains(conversation))
                            Profile.UnreadFrom.Add(conversation);
                    }
                }
            }
            finally
            {
                applyingReplicationProjection = previous;
            }
        }
        if (markConversationUnread) ScheduleProfileSave();
        await PublishNotificationAfterCommitAsync(evt, envelope).ConfigureAwait(false);
        if (changed) NotifyChanged();
    }

    private async Task ApplyReplicatedStateBatchAfterCommitAsync(
        MeshDb? sourceDb,
        IReadOnlyList<ReplicationCommittedDomainEvent> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Count == 0) return;

        var previous = replicationNotificationBatch.Value;
        var batch = new ReplicationNotificationBatch();
        replicationNotificationBatch.Value = batch;
        try
        {
            foreach (var item in committed)
                await ApplyReplicatedStateAfterCommitAsync(
                    sourceDb,
                    item.Event,
                    item.Envelope).ConfigureAwait(false);
        }
        finally
        {
            replicationNotificationBatch.Value = previous;
            if (batch.Pending)
            {
                if (previous is null) Changed?.Invoke();
                else previous.Pending = true;
            }
        }
    }

    private static Task PublishNotificationAfterCommitAsync(
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        var intent = envelope.NotificationIntent;
        if (intent is null || string.IsNullOrWhiteSpace(intent.StableId)) return Task.CompletedTask;
        var activity = NotificationIntents.ToCommittedActivity(
            intent,
            evt.EventId,
            DateTimeOffset.FromUnixTimeMilliseconds(evt.CreatedAtUnixMs),
            DateTimeOffset.UtcNow,
            evt.OriginAccount);
        return NotificationCoordinatorBridge.PublishAsync(activity);
    }

    private static bool TryParseAssetEntityId(
        string entityId,
        out AssetKind kind,
        out string id)
    {
        kind = default;
        id = "";
        if (string.IsNullOrWhiteSpace(entityId)) return false;
        var slash = entityId.IndexOf('/');
        if (slash <= 0 || slash == entityId.Length - 1) return false;
        if (!Enum.TryParse(entityId[..slash], ignoreCase: true, out kind)) return false;
        id = entityId[(slash + 1)..];
        return id.Length > 0;
    }

    /// <summary>
    /// Materialises an inbound replicated envelope onto the ACTUAL domain tables inside the same
    /// transaction that appends the event and advances the cursor. Throwing rolls the whole apply
    /// back so the cursor never advances past an event this device could not project.
    /// </summary>
    private bool ProjectInbound(
        SqliteConnection conn,
        SqliteTransaction tx,
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop)
    {
        lock (profileSyncGate)
            envelope = ReplicationInboundProjection.ForLocalAccount(evt, envelope, Profile.Handle);
        return ReplicationDomainMaterializer.Apply(conn, tx, evt, envelope, deviceIsDesktop);
    }

    private sealed class AppStateReplicationApplier(AppState owner) : IReplicationDomainApplier
    {
        public bool Apply(
            SqliteConnection conn,
            SqliteTransaction tx,
            ReplicationEvent evt,
            ReplicationPayloadCodec.DomainEnvelope envelope,
            bool deviceIsDesktop)
            => owner.ProjectInbound(conn, tx, evt, envelope, deviceIsDesktop);

        public Task AfterCommitAsync(
            ReplicationEvent evt,
            ReplicationPayloadCodec.DomainEnvelope envelope,
            bool deviceIsDesktop)
            => owner.ApplyReplicatedStateAfterCommitAsync(
                owner.replicationEngineDb,
                evt,
                envelope);

        public Task AfterCommitBatchAsync(
            IReadOnlyList<ReplicationCommittedDomainEvent> committed,
            bool deviceIsDesktop)
            => owner.ApplyReplicatedStateBatchAfterCommitAsync(
                owner.replicationEngineDb,
                committed);
    }
}

/// <summary>
/// Offline device roster derived from the durable local custody chain. It resolves an account's
/// authorised devices from the custody entries persisted in the account database (genesis + add
/// device, minus removed), so the offline journal can encrypt a local change to this account's
/// own sibling devices and detect whether a sibling exists to take custody, all without any relay
/// connection. Peer (contact) accounts have no local custody chain, so they resolve to an empty
/// device set; their events still target the recipient account and are offered once the online
/// engine (with the relay roster) later drains the pending outbox.
/// </summary>
internal sealed class LocalCustodyRoster(MeshDb db) : IReplicationRoster
{
    public IReadOnlyList<ReplicationDevice> AuthorizedDevices(string accountHandle)
    {
        var handle = accountHandle ?? "";
        var chain = db.GetCustodyChain(handle);
        if (chain.Count == 0) return Array.Empty<ReplicationDevice>();

        long generation = chain[^1].Generation;
        var keys = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in chain)
        {
            switch (entry.Action)
            {
                case CustodyAction.Genesis:
                case CustodyAction.AddDevice:
                    keys[entry.SubjectDeviceKey] = false;
                    break;
                case CustodyAction.RemoveDevice:
                    keys[entry.SubjectDeviceKey] = true;
                    break;
            }
        }

        var devices = new List<ReplicationDevice>();
        foreach (var (key, revoked) in keys)
        {
            if (revoked || string.IsNullOrWhiteSpace(key)) continue;
            devices.Add(new ReplicationDevice(handle, DeviceProtocol.DeviceId(key), key, generation, Revoked: false));
        }
        return devices;
    }

    public ReplicationDevice? ResolveDevice(string accountHandle, string deviceId)
        => AuthorizedDevices(accountHandle)
            .FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));

    public long AuthGeneration(string accountHandle)
    {
        var head = db.GetCustodyHead(accountHandle ?? "");
        return head?.Generation ?? -1;
    }
}
