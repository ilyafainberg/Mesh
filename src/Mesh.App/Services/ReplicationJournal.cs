using Microsoft.Data.Sqlite;
using Mesh.Shared;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Thrown when a local domain operation is asked to replicate but this device has no usable
/// replication identity / local authority (missing signing keys or an uninitialised custody
/// chain). Greenfield onboarding initialises genesis custody; an account that predates it must
/// be re-onboarded. The local domain operation fails rather than falsely reporting success.
/// </summary>
public sealed class ReplicationIdentityMissingException : Exception
{
    public ReplicationIdentityMissingException(string message) : base(message) { }
}

public sealed record ReplicationLocalEmission(
    string? EventId,
    string IntentId,
    ReplicationEmissionRosterState RosterState)
{
    public bool IsPending => EventId is null;
}

public sealed class ReplicationRosterUnavailableException : Exception
{
    public ReplicationRosterUnavailableException(
        ReplicationEmissionRosterState state,
        string message) : base(message)
        => State = state;

    public ReplicationEmissionRosterState State { get; }
}

/// <summary>
/// The offline-capable local replication emitter (spec item 1). It writes a signed
/// <see cref="ReplicationEvent"/>, its target-account outbox references and the domain change
/// into the active account database in a single transaction <b>whenever the local account is
/// open, regardless of any relay connection or engine session</b>. It never silently no-ops:
/// every call either appends a durable event (and returns its id) or throws. The online engine
/// only drains and sessions this journal; it is not required for a local change to be recorded.
///
/// This type has no MAUI / relay dependency, so it is exercised directly in tests and shared
/// by the online engine's local-emit path.
/// </summary>
public sealed class ReplicationJournal
{
    private const string PendingBatchKind = "$mesh.batch";

    private readonly MeshDb db;
    private readonly ReplicationIdentity identity;
    private readonly IReplicationRoster roster;
    private readonly bool deviceIsDesktop;
    private readonly object observedGenerationGate = new();
    private readonly Dictionary<string, long> observedGenerations = new(StringComparer.Ordinal);

    public ReplicationJournal(MeshDb db, ReplicationIdentity identity, IReplicationRoster roster, bool deviceIsDesktop)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.roster = roster ?? throw new ArgumentNullException(nameof(roster));
        this.deviceIsDesktop = deviceIsDesktop;
        ValidateIdentity(identity);
        observedGenerations[ReplicationHandle.Norm(identity.Handle)] = identity.AuthGeneration;
    }

    /// <summary>This device's replication identity.</summary>
    public ReplicationIdentity Identity => identity;

    internal IReadOnlyList<string> PendingIntentAccounts()
        => db.GetPendingReplicationIntents()
            .SelectMany(intent =>
                JsonSerializer.Deserialize<string[]>(intent.TargetAccountsJson)
                ?? Array.Empty<string>())
            .Append(identity.Handle)
            .Select(ReplicationHandle.Norm)
            .Where(account => account.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Registers this device's local origin log if absent (idempotent).</summary>
    public void EnsureLocalOrigin()
        => db.ExecuteDurableWrite(
            () => db.EnsureLocalOrigin(
                identity.DeviceId, identity.LogEpoch, identity.AuthGeneration));

    /// <summary>
    /// Records a local domain change: encrypts the envelope to every currently-authorised
    /// recipient device, allocates the next sequence, signs the event and commits the event,
    /// its outbox references and the domain projection in one transaction. Returns the new
    /// event id. Never no-ops.
    /// </summary>
    /// <param name="domainWork">
    /// Optional in-transaction domain write. When null, the envelope is projected onto the
    /// local convergence store so local and inbound state converge identically.
    /// </param>
    internal string EmitLocal(
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork = null)
        => EmitLocalAsync(envelope, targetAccounts, domainWork, CancellationToken.None)
            .GetAwaiter().GetResult() is var result
            ? result.EventId ?? result.IntentId
            : throw new InvalidOperationException();

    internal async Task<ReplicationLocalEmission> EmitLocalAsync(
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(targetAccounts);
        ValidateIdentity(identity);

        // Asset upserts and skill-package transfers persist device-local bytes and are
        // desktop-only; a mobile / LocalOnly device never emits them (spec item 3).
        if (!deviceIsDesktop && ReplicationPayloadCodec.RequiresDesktop(envelope.Kind, envelope.Action))
            throw new InvalidOperationException(
                $"Asset/package replication ({envelope.Kind}/{envelope.Action}) is desktop-only and must not be emitted on this device.");

        EnsureLocalOrigin();

        var normalizedAccounts = EmissionAccounts(targetAccounts);
        var intentId = ComputeIntentId(envelope, targetAccounts);
        var completed = db.GetCompletedReplicationIntentEvent(intentId);
        if (completed is not null)
            return new ReplicationLocalEmission(
                completed,
                intentId,
                ReplicationEmissionRosterState.FreshComplete);

        if (db.HasPendingReplicationIntents())
        {
            var ordered = new ReplicationEmissionRosterSnapshot(
                ReplicationEmissionRosterState.Unavailable,
                Array.Empty<ReplicationDevice>(),
                new Dictionary<string, long>(StringComparer.Ordinal),
                "ordered behind an earlier durable pending intent");
            StorePendingIntent(intentId, envelope, targetAccounts, domainWork, ordered);
            return new ReplicationLocalEmission(null, intentId, ordered.State);
        }

        var snapshot = await ResolveSnapshotAsync(normalizedAccounts, ct).ConfigureAwait(false);
        RecordRosterDiagnostic("resolved", intentId, snapshot, normalizedAccounts.Count);
        if (!snapshot.CanEmit)
        {
            StorePendingIntent(intentId, envelope, targetAccounts, domainWork, snapshot);
            return new ReplicationLocalEmission(null, intentId, snapshot.State);
        }

        var eventId = EmitLocalCore(
            intentId, envelope, targetAccounts, snapshot, domainWork, deletePending: false);
        return new ReplicationLocalEmission(eventId, intentId, snapshot.State);
    }

    private string EmitLocalCore(
        string intentId,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts,
        ReplicationEmissionRosterSnapshot snapshot,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork,
        bool deletePending)
    {
        var recipientKeys = BuildRecipientKeys(snapshot);
        var plaintext = ReplicationPayloadCodec.EncodeEnvelope(envelope);
        var ciphertext = ReplicationPayloadCodec.Encrypt(plaintext, recipientKeys);

        var normalizedTargets = NormalizeTargets(targetAccounts, snapshot);
        var evt = db.ExecuteJournalWrite(() => db.AllocateAndAppendLocalEvent(
                identity.DeviceId,
                (epoch, seq) => OnlineReplicationProtocol.CreateEvent(
                    identity.DeviceId,
                    epoch,
                    seq,
                    identity.Handle,
                    identity.AuthGeneration,
                    envelope.Kind,
                    envelope.EntityId,
                    envelope.ConversationId,
                    envelope.CausalVersion,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ciphertext,
                    identity.PrivateKeyB64),
                normalizedTargets,
                (conn, tx, created) =>
                {
                    if (domainWork is not null)
                        domainWork(conn, tx, created);
                    else
                        ReplicationPayloadCodec.Project(conn, tx, created, envelope, deviceIsDesktop);
                    if (deletePending)
                        db.DeletePendingReplicationIntentInTransaction(intentId, tx);
                },
                account => NotificationForTarget(envelope.NotificationIntent, account),
                intentId));
        RecordEmissionDiagnostic(intentId, evt.EventId, snapshot.State, recipientKeys.Count);
        return evt.EventId;
    }

    /// <summary>
    /// Records several local domain changes that belong to ONE logical operation (for example a
    /// chunked skill-package or attachment transfer plus the asset body it installs). All events,
    /// their outbox references, the sequence allocation and every domain write commit in a single
    /// transaction, so a partially transferred package is never observable. Returns the new event
    /// ids in order.
    /// </summary>
    internal IReadOnlyList<string> EmitLocalBatch(
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, int>? domainWork = null,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent, int>? eventWork = null)
        => EmitLocalBatchAsync(envelopes, targetAccounts, domainWork, eventWork, CancellationToken.None)
            .GetAwaiter().GetResult();

    internal async Task<IReadOnlyList<string>> EmitLocalBatchAsync(
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, int>? domainWork = null,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent, int>? eventWork = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(targetAccounts);
        if (envelopes.Count == 0)
            throw new ArgumentException("At least one envelope is required.", nameof(envelopes));
        ValidateIdentity(identity);

        foreach (var envelope in envelopes)
        {
            if (!deviceIsDesktop && ReplicationPayloadCodec.RequiresDesktop(envelope.Kind, envelope.Action))
                throw new InvalidOperationException(
                    $"Asset/package replication ({envelope.Kind}/{envelope.Action}) is desktop-only and must not be emitted on this device.");
        }

        EnsureLocalOrigin();

        var accounts = EmissionAccounts(targetAccounts);
        var intentId = ComputeBatchIntentId(envelopes, targetAccounts);
        var completed = db.GetCompletedReplicationIntentEvent(intentId);
        if (completed is not null) return new[] { completed };

        if (db.HasPendingReplicationIntents())
        {
            var ordered = new ReplicationEmissionRosterSnapshot(
                ReplicationEmissionRosterState.Unavailable,
                Array.Empty<ReplicationDevice>(),
                new Dictionary<string, long>(StringComparer.Ordinal),
                "ordered behind an earlier durable pending intent");
            StorePendingBatchIntent(
                intentId, envelopes, targetAccounts, domainWork, eventWork, ordered);
            return PendingBatchIds(intentId, envelopes.Count);
        }

        var snapshot = await ResolveSnapshotAsync(accounts, ct).ConfigureAwait(false);
        if (!snapshot.CanEmit)
        {
            StorePendingBatchIntent(
                intentId, envelopes, targetAccounts, domainWork, eventWork, snapshot);
            return PendingBatchIds(intentId, envelopes.Count);
        }

        return EmitLocalBatchCore(
            intentId,
            envelopes,
            targetAccounts,
            snapshot,
            domainWork,
            eventWork,
            deletePending: false);
    }

    private IReadOnlyList<string> EmitLocalBatchCore(
        string intentId,
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts,
        ReplicationEmissionRosterSnapshot snapshot,
        Action<SqliteConnection, SqliteTransaction, int>? domainWork,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent, int>? eventWork,
        bool deletePending)
    {
        var recipientKeys = BuildRecipientKeys(snapshot);
        var normalizedTargets = NormalizeTargets(targetAccounts, snapshot);
        var factories = new List<Func<string, ulong, ReplicationEvent>>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            var ciphertext = ReplicationPayloadCodec.Encrypt(
                ReplicationPayloadCodec.EncodeEnvelope(envelope), recipientKeys);
            var captured = envelope;
            factories.Add((epoch, seq) => OnlineReplicationProtocol.CreateEvent(
                identity.DeviceId, epoch, seq, identity.Handle, identity.AuthGeneration,
                captured.Kind, captured.EntityId, captured.ConversationId, captured.CausalVersion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ciphertext, identity.PrivateKeyB64));
        }

        var created = db.ExecuteJournalWrite(() => db.AllocateAndAppendLocalEvents(
                identity.DeviceId, factories, normalizedTargets,
                (conn, tx, evt, index) =>
                {
                    if (domainWork is not null)
                        domainWork(conn, tx, index);
                    else if (eventWork is null)
                        ReplicationPayloadCodec.Project(conn, tx, evt, envelopes[index], deviceIsDesktop);
                    eventWork?.Invoke(conn, tx, evt, index);
                },
                (index, account) => NotificationForTarget(envelopes[index].NotificationIntent, account),
                deletePending ? intentId : null));
        return created.Select(e => e.EventId).ToList();
    }

    public async Task<int> RetryPendingIntentsAsync(CancellationToken ct = default)
    {
        var emitted = 0;
        foreach (var pending in db.GetPendingReplicationIntents())
        {
            ct.ThrowIfCancellationRequested();
            var associatedData = IntentAssociatedData(pending);
            var plaintext = db.UnprotectReplicationIntent(
                pending.EncryptedEnvelope, associatedData);
            if (plaintext is null
                && !pending.EncryptedEnvelope.StartsWith("local-v1:", StringComparison.Ordinal))
            {
                var legacy = ReplicationPayloadCodec.TryDecrypt(
                    pending.EncryptedEnvelope, identity.PrivateKeyB64, identity.PublicKeyB64);
                plaintext = legacy.Outcome == MessageDecryptOutcome.Success
                    ? legacy.Plaintext
                    : null;
                if (plaintext is not null)
                {
                    db.RewrapPendingReplicationIntent(
                        pending.IntentId,
                        db.ProtectReplicationIntent(plaintext, associatedData));
                }
            }
            if (plaintext is null
                || !string.Equals(Hash(plaintext), pending.ContentHash, StringComparison.Ordinal))
            {
                db.RecordPendingReplicationIntentFailure(
                    pending.IntentId, "local-intent-decrypt-failed");
                break;
            }

            var envelope = ReplicationPayloadCodec.DecodeEnvelope(plaintext);
            if (string.Equals(pending.Kind, PendingBatchKind, StringComparison.Ordinal))
            {
                var envelopes = JsonSerializer.Deserialize<
                    List<ReplicationPayloadCodec.DomainEnvelope>>(plaintext);
                if (envelopes is null || envelopes.Count == 0)
                {
                    db.RecordPendingReplicationIntentFailure(
                        pending.IntentId, "local-intent-batch-invalid");
                    break;
                }

                var batchTargets = JsonSerializer.Deserialize<string[]>(pending.TargetAccountsJson)
                    ?? Array.Empty<string>();
                var batchAccounts = EmissionAccounts(batchTargets);
                var batchSnapshot = await ResolveSnapshotAsync(batchAccounts, ct).ConfigureAwait(false);
                RecordRosterDiagnostic("retry", pending.IntentId, batchSnapshot, batchAccounts.Count);
                if (!batchSnapshot.CanEmit)
                {
                    db.RecordPendingReplicationIntentFailure(
                        pending.IntentId, batchSnapshot.State.ToString(), batchSnapshot.State);
                    break;
                }

                _ = EmitLocalBatchCore(
                    pending.IntentId,
                    envelopes,
                    batchTargets,
                    batchSnapshot,
                    domainWork: static (_, _, _) => { },
                    eventWork: null,
                    deletePending: true);
                emitted += envelopes.Count;
                continue;
            }

            if (envelope is null)
            {
                db.RecordPendingReplicationIntentFailure(
                    pending.IntentId, "local-intent-envelope-invalid");
                break;
            }

            var targets = JsonSerializer.Deserialize<string[]>(pending.TargetAccountsJson)
                ?? Array.Empty<string>();
            var accounts = EmissionAccounts(targets);
            var snapshot = await ResolveSnapshotAsync(accounts, ct).ConfigureAwait(false);
            RecordRosterDiagnostic("retry", pending.IntentId, snapshot, accounts.Count);
            if (!snapshot.CanEmit)
            {
                db.RecordPendingReplicationIntentFailure(
                    pending.IntentId, snapshot.State.ToString(), snapshot.State);
                break;
            }

            _ = EmitLocalCore(
                pending.IntentId,
                envelope,
                targets,
                snapshot,
                domainWork: static (_, _, _) => { },
                deletePending: true);
            emitted++;
        }
        return emitted;
    }

    private void StorePendingBatchIntent(
        string intentId,
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, int>? domainWork,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent, int>? eventWork,
        ReplicationEmissionRosterSnapshot snapshot)
    {
        var canonicalTargets = targetAccounts
            .Select(ReplicationHandle.Norm)
            .Where(account => account.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var targetsJson = JsonSerializer.Serialize(canonicalTargets);
        var plaintext = JsonSerializer.Serialize(envelopes);
        var contentHash = Hash(plaintext);
        var first = envelopes[0];
        var encrypted = db.ProtectReplicationIntent(
            plaintext,
            IntentAssociatedData(
                intentId,
                PendingBatchKind,
                first.EntityId,
                first.CausalVersion,
                targetsJson,
                contentHash));

        db.ExecuteJournalWrite(() =>
        {
            db.StorePendingReplicationIntent(
                new MeshDb.PendingReplicationIntent(
                    intentId,
                    PendingBatchKind,
                    first.EntityId,
                    first.CausalVersion,
                    targetsJson,
                    contentHash,
                    encrypted,
                    snapshot.State.ToString()),
                (conn, tx) =>
                {
                    for (var index = 0; index < envelopes.Count; index++)
                    {
                        var envelope = envelopes[index];
                        var projectionContext = new ReplicationEvent(
                            $"{intentId}:{index}",
                            envelope.ConversationId,
                            identity.Handle,
                            identity.DeviceId,
                            identity.LogEpoch,
                            0,
                            identity.AuthGeneration,
                            envelope.Kind,
                            envelope.EntityId,
                            envelope.CausalVersion,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            "",
                            Hash(ReplicationPayloadCodec.EncodeEnvelope(envelope)),
                            "");
                        if (domainWork is not null)
                            domainWork(conn, tx, index);
                        else if (eventWork is null)
                            ReplicationPayloadCodec.Project(
                                conn, tx, projectionContext, envelope, deviceIsDesktop);
                    }
                });
            return true;
        });

        RuntimeDiagnostics.Current?.RecordEvent(
            "replication-intent",
            $"state=pending-batch;intent={intentId};roster={snapshot.State};" +
            $"targets={canonicalTargets.Length};events={envelopes.Count}");
    }

    private static IReadOnlyList<string> PendingBatchIds(string intentId, int count)
        => Enumerable.Range(0, count).Select(index => $"{intentId}:{index}").ToArray();

    private void StorePendingIntent(
        string intentId,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork,
        ReplicationEmissionRosterSnapshot snapshot)
    {
        var plaintext = ReplicationPayloadCodec.EncodeEnvelope(envelope);
        var canonicalTargets = targetAccounts
            .Select(ReplicationHandle.Norm)
            .Where(account => account.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var targetsJson = JsonSerializer.Serialize(canonicalTargets);
        // Intent-at-rest encryption is derived from the SQLCipher database key and is independent
        // of remote roster/device generations. Event ciphertext is created only during replay.
        var encrypted = db.ProtectReplicationIntent(
            plaintext,
            IntentAssociatedData(
                intentId,
                envelope.Kind,
                envelope.EntityId,
                envelope.CausalVersion,
                targetsJson,
                Hash(plaintext)));
        var projectionContext = new ReplicationEvent(
            intentId,
            envelope.ConversationId,
            identity.Handle,
            identity.DeviceId,
            identity.LogEpoch,
            0,
            identity.AuthGeneration,
            envelope.Kind,
            envelope.EntityId,
            envelope.CausalVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "",
            Hash(plaintext),
            "");

        db.ExecuteJournalWrite(() =>
        {
            db.StorePendingReplicationIntent(
                new MeshDb.PendingReplicationIntent(
                    intentId,
                    envelope.Kind,
                    envelope.EntityId,
                    envelope.CausalVersion,
                    targetsJson,
                    Hash(plaintext),
                    encrypted,
                    snapshot.State.ToString()),
                (conn, tx) =>
                {
                    if (domainWork is not null)
                        domainWork(conn, tx, projectionContext);
                    else
                        ReplicationPayloadCodec.Project(
                            conn, tx, projectionContext, envelope, deviceIsDesktop);
                });
            return true;
        });

        RuntimeDiagnostics.Current?.RecordEvent(
            "replication-intent",
            $"state=pending;intent={intentId};roster={snapshot.State};" +
            $"targets={canonicalTargets.Length};reason={snapshot.Reason ?? "unavailable"}");
    }

    private MeshDb.ReplicationOutboxNotification NotificationForTarget(
        NotificationIntent? intent,
        string targetAccount)
    {
        var notificationId = string.IsNullOrWhiteSpace(intent?.StableId) ? null : intent.StableId;
        var ownerCopy = intent?.SuppressOnOriginAccount == true
                        && string.Equals(
                            ReplicationHandle.Norm(targetAccount),
                            ReplicationHandle.Norm(identity.Handle),
                            StringComparison.Ordinal);
        var worthy = intent is { Notify: true, IsHistorical: false } && notificationId is not null && !ownerCopy;
        return new MeshDb.ReplicationOutboxNotification(worthy, notificationId);
    }

    private void ValidateIdentity(ReplicationIdentity id)
    {
        if (string.IsNullOrWhiteSpace(id.Handle)
            || string.IsNullOrWhiteSpace(id.DeviceId)
            || string.IsNullOrWhiteSpace(id.PublicKeyB64)
            || string.IsNullOrWhiteSpace(id.PrivateKeyB64)
            || string.IsNullOrWhiteSpace(id.LogEpoch)
            || string.IsNullOrWhiteSpace(id.CustodyHead))
        {
            throw new ReplicationIdentityMissingException(
                "This device has no usable replication identity / local authority; re-onboard the account.");
        }
    }

    /// <summary>
    /// The set of recipient device public keys for an outbound change: always this device's own
    /// key, plus every non-revoked authorised device of every target account and of the local
    /// account (own devices always receive their account's own changes).
    /// </summary>
    private IReadOnlyCollection<string> BuildRecipientKeys(ReplicationEmissionRosterSnapshot snapshot)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { identity.PublicKeyB64 };
        foreach (var device in snapshot.Devices)
            if (!device.Revoked && !string.IsNullOrWhiteSpace(device.PublicKeyB64))
                keys.Add(device.PublicKeyB64);
        return keys;
    }

    /// <summary>
    /// Normalises and de-duplicates the outbox target accounts. An own-account target is only
    /// tracked when a sibling authorised device exists to take custody; a sole device has no
    /// one to receipt, so the change is locally complete with no false remote custody
    /// (spec item 5).
    /// </summary>
    private IReadOnlyList<string> NormalizeTargets(
        IReadOnlyCollection<string> targetAccounts,
        ReplicationEmissionRosterSnapshot snapshot)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawAccount in targetAccounts)
        {
            var account = ReplicationHandle.Norm(rawAccount);
            if (account.Length == 0 || !seen.Add(account)) continue;
            if (string.Equals(
                    ReplicationHandle.Norm(account),
                    ReplicationHandle.Norm(identity.Handle),
                    StringComparison.Ordinal))
            {
                var siblings = snapshot.Devices
                    .Where(d => string.Equals(
                        ReplicationHandle.Norm(d.Handle),
                        ReplicationHandle.Norm(account),
                        StringComparison.Ordinal))
                    .Count(d => !string.Equals(
                        ReplicationHandle.Device(d.DeviceId),
                        ReplicationHandle.Device(identity.DeviceId),
                        StringComparison.Ordinal));
                if (siblings == 0) continue;
            }
            result.Add(account);
        }
        return result;
    }

    private IReadOnlyCollection<string> EmissionAccounts(IReadOnlyCollection<string> targetAccounts)
        => targetAccounts
            .Append(identity.Handle)
            .Select(ReplicationHandle.Norm)
            .Where(account => account.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<ReplicationEmissionRosterSnapshot> ResolveSnapshotAsync(
        IReadOnlyCollection<string> accounts,
        CancellationToken ct)
    {
        Dictionary<string, long> minimum;
        lock (observedGenerationGate)
            minimum = observedGenerations.ToDictionary(
                item => item.Key, item => item.Value, StringComparer.Ordinal);
        var snapshot = await roster.GetEmissionSnapshotAsync(
            accounts, identity, minimum, ct).ConfigureAwait(false);
        if (snapshot.CanEmit)
        {
            lock (observedGenerationGate)
            {
                foreach (var item in snapshot.AuthGenerations)
                {
                    var handle = ReplicationHandle.Norm(item.Key);
                    if (!observedGenerations.TryGetValue(handle, out var current)
                        || item.Value > current)
                        observedGenerations[handle] = item.Value;
                }
            }
        }
        return snapshot;
    }

    private static string ComputeIntentId(
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts)
    {
        var canonical = string.Join(
            "\n",
            envelope.Kind,
            ((int)envelope.Action).ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.EntityId,
            envelope.ConversationId ?? "",
            envelope.CausalVersion,
            Hash(ReplicationPayloadCodec.EncodeEnvelope(envelope)),
            string.Join(
                ",",
                targetAccounts
                    .Select(ReplicationHandle.Norm)
                    .Where(account => account.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)));
        return Hash(canonical);
    }

    private static string ComputeBatchIntentId(
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts)
    {
        var canonical = string.Join(
            "\n",
            PendingBatchKind,
            envelopes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(
                ",",
                envelopes.Select(envelope => Hash(
                    ReplicationPayloadCodec.EncodeEnvelope(envelope)))),
            string.Join(
                ",",
                targetAccounts
                    .Select(ReplicationHandle.Norm)
                    .Where(account => account.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)));
        return Hash(canonical);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string IntentAssociatedData(MeshDb.PendingReplicationIntent intent)
        => IntentAssociatedData(
            intent.IntentId,
            intent.Kind,
            intent.EntityId,
            intent.CausalVersion,
            intent.TargetAccountsJson,
            intent.ContentHash);

    private static string IntentAssociatedData(
        string intentId,
        string kind,
        string entityId,
        string causalVersion,
        string targetAccountsJson,
        string contentHash)
        => string.Join(
            "\n",
            "mesh.replication.pending-intent.v1",
            intentId,
            kind,
            entityId,
            causalVersion,
            targetAccountsJson,
            contentHash);

    private static void RecordRosterDiagnostic(
        string phase,
        string intentId,
        ReplicationEmissionRosterSnapshot snapshot,
        int accountCount)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "replication-roster",
            $"phase={phase};intent={intentId};state={snapshot.State};" +
            $"accounts={accountCount};recipient_slots={snapshot.Devices.Count}");

    private static void RecordEmissionDiagnostic(
        string intentId,
        string eventId,
        ReplicationEmissionRosterState state,
        int recipientSlots)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "replication-emission",
            $"intent={intentId};event={eventId};state={state};recipient_slots={recipientSlots}");
}
