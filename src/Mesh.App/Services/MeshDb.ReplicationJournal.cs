using Microsoft.Data.Sqlite;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Protocol-9 local replication-journal primitives: the transaction-composable overload
/// that commits a domain change together with the immutable event and its outbox refs,
/// genesis-custody bootstrap for account onboarding, and read helpers over the projected
/// domain convergence tables. These sit alongside the online-replication store but never
/// require a relay connection: the local emitter uses them whenever the account DB is open.
/// </summary>
public sealed partial class MeshDb
{
    /// <summary>
    /// Allocates the next local sequence, creates and appends its signed event, enqueues target
    /// references, applies the domain change and advances <c>next_seq</c> in one transaction.
    /// Any failure rolls the sequence allocation back, so an origin log can never acquire a hole.
    /// </summary>
    public ReplicationEvent AllocateAndAppendLocalEvent(
        string originDeviceId,
        Func<string, ulong, ReplicationEvent> eventFactory,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainApply,
        Func<string, ReplicationOutboxNotification>? notificationForTarget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);
        ArgumentNullException.ThrowIfNull(eventFactory);
        ArgumentNullException.ThrowIfNull(targetAccounts);

        using var tx = conn.BeginTransaction(deferred: false);
        string epoch;
        long next;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT log_epoch, next_seq
                FROM replication_local_origins
                WHERE origin_device_id = $origin;
                """;
            read.Parameters.AddWithValue("$origin", originDeviceId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The local origin log has not been registered.");
            epoch = reader.GetString(0);
            next = reader.GetInt64(1);
        }
        if (next <= 0 || next == long.MaxValue)
            throw new InvalidOperationException("The local origin sequence is exhausted.");

        var evt = eventFactory(epoch, (ulong)next);
        if (!string.Equals(evt.OriginDeviceId, originDeviceId, StringComparison.Ordinal)
            || !string.Equals(evt.LogEpoch, epoch, StringComparison.Ordinal)
            || evt.Seq != (ulong)next)
        {
            throw new InvalidOperationException(
                "The event factory returned an event outside the allocated origin position.");
        }
        if (!OnlineReplicationProtocol.ValidateEventShape(evt, out var error))
            throw new ArgumentException(error, nameof(eventFactory));

        AppendEventCore(evt, tx);
        foreach (var account in targetAccounts)
        {
            if (string.IsNullOrWhiteSpace(account)) continue;
            InsertOutboxCore(tx, evt.EventId, account, notificationForTarget?.Invoke(account));
        }

        domainApply?.Invoke(conn, tx, evt);

        using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = """
                UPDATE replication_local_origins
                SET next_seq = $next
                WHERE origin_device_id = $origin AND next_seq = $expected;
                """;
            bump.Parameters.AddWithValue("$next", next + 1);
            bump.Parameters.AddWithValue("$origin", originDeviceId);
            bump.Parameters.AddWithValue("$expected", next);
            if (bump.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The local origin sequence changed unexpectedly.");
        }

        tx.Commit();
        return evt;
    }

    /// <summary>
    /// Allocates a contiguous run of local sequences and commits every event, its outbox
    /// references and the whole domain change in ONE transaction. Used when a single logical
    /// operation needs several signed events (a chunked skill-package or attachment transfer):
    /// either the complete set is durable or nothing is, so a package can never become visible
    /// without every event that carries its bytes.
    /// </summary>
    /// <param name="eventFactories">One factory per event, applied in order.</param>
    /// <param name="domainApply">
    /// Domain write invoked once per created event, in order, inside the same transaction.
    /// </param>
    public IReadOnlyList<ReplicationEvent> AllocateAndAppendLocalEvents(
        string originDeviceId,
        IReadOnlyList<Func<string, ulong, ReplicationEvent>> eventFactories,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent, int>? domainApply,
        Func<int, string, ReplicationOutboxNotification>? notificationForTarget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);
        ArgumentNullException.ThrowIfNull(eventFactories);
        ArgumentNullException.ThrowIfNull(targetAccounts);
        if (eventFactories.Count == 0)
            throw new ArgumentException("At least one event factory is required.", nameof(eventFactories));

        using var tx = conn.BeginTransaction(deferred: false);
        string epoch;
        long next;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT log_epoch, next_seq
                FROM replication_local_origins
                WHERE origin_device_id = $origin;
                """;
            read.Parameters.AddWithValue("$origin", originDeviceId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The local origin log has not been registered.");
            epoch = reader.GetString(0);
            next = reader.GetInt64(1);
        }
        var first = next;
        if (next <= 0 || next >= long.MaxValue - eventFactories.Count)
            throw new InvalidOperationException("The local origin sequence is exhausted.");

        var created = new List<ReplicationEvent>(eventFactories.Count);
        for (var i = 0; i < eventFactories.Count; i++, next++)
        {
            var evt = eventFactories[i](epoch, (ulong)next);
            if (!string.Equals(evt.OriginDeviceId, originDeviceId, StringComparison.Ordinal)
                || !string.Equals(evt.LogEpoch, epoch, StringComparison.Ordinal)
                || evt.Seq != (ulong)next)
            {
                throw new InvalidOperationException(
                    "The event factory returned an event outside the allocated origin position.");
            }
            if (!OnlineReplicationProtocol.ValidateEventShape(evt, out var error))
                throw new ArgumentException(error, nameof(eventFactories));

            AppendEventCore(evt, tx);
            foreach (var account in targetAccounts)
            {
                if (string.IsNullOrWhiteSpace(account)) continue;
                InsertOutboxCore(tx, evt.EventId, account, notificationForTarget?.Invoke(i, account));
            }
            domainApply?.Invoke(conn, tx, evt, i);
            created.Add(evt);
        }

        using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = """
                UPDATE replication_local_origins
                SET next_seq = $next
                WHERE origin_device_id = $origin AND next_seq = $expected;
                """;
            bump.Parameters.AddWithValue("$next", next);
            bump.Parameters.AddWithValue("$origin", originDeviceId);
            bump.Parameters.AddWithValue("$expected", first);
            if (bump.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The local origin sequence changed unexpectedly.");
        }

        tx.Commit();
        return created;
    }

    /// <summary>
    /// Appends a local-origin event, enqueues one outbox reference per target account and runs
    /// <paramref name="domainApply"/> in all in a single transaction. The domain write and the
    /// event that carries it therefore commit or roll back atomically (spec item 2). The outbox
    /// stores references only; it never duplicates the payload.
    /// </summary>
    public ReplicationAppendResult AppendLocalEventWithOutbox(
        ReplicationEvent e,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction>? domainApply)
    {
        ArgumentNullException.ThrowIfNull(targetAccounts);
        if (!OnlineReplicationProtocol.ValidateEventShape(e, out var error))
            throw new ArgumentException(error, nameof(e));
        using var tx = conn.BeginTransaction(deferred: false);
        var result = AppendEventCore(e, tx);
        foreach (var account in targetAccounts)
        {
            if (string.IsNullOrWhiteSpace(account)) continue;
            InsertOutboxCore(tx, e.EventId, account, ReplicationOutboxNotification.None);
        }
        domainApply?.Invoke(conn, tx);
        tx.Commit();
        return result;
    }

    // -----------------------------------------------------------------------
    // Genesis custody bootstrap. Greenfield account onboarding initialises the local
    // authority (custody generation 0) so the offline journal has a real custody head
    // to sign under from the first local change (spec item 1).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises the account's genesis custody entry if absent and returns the custody head
    /// hash. Idempotent: a second call with the same device key returns the existing head.
    /// </summary>
    public string InitializeGenesisCustody(
        string handle,
        string devicePublicKeyB64,
        string signerPrivateKeyB64,
        string? recoveryPublicKeyB64 = null,
        long? effectiveAtUnixMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePublicKeyB64);
        ArgumentException.ThrowIfNullOrWhiteSpace(signerPrivateKeyB64);

        var head = GetCustodyHead(handle);
        if (head is not null) return head.EntryHash;

        var genesis = OnlineReplicationProtocol.CreateCustodyEntry(
            handle,
            generation: 0,
            prevHash: OnlineReplicationProtocol.ZeroHash,
            action: CustodyAction.Genesis,
            subjectDeviceKey: devicePublicKeyB64,
            recoveryPublicKey: recoveryPublicKeyB64,
            effectiveAtUnixMs: effectiveAtUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            signerKey: devicePublicKeyB64,
            signerPrivateKeyB64: signerPrivateKeyB64);
        AppendCustodyEntry(genesis);
        return genesis.EntryHash;
    }

    /// <summary>The account's current custody head hash, or the zero sentinel when none exists yet.</summary>
    public string GetCustodyHeadHash(string handle)
        => GetCustodyHead(handle)?.EntryHash ?? OnlineReplicationProtocol.ZeroHash;

    /// <summary>True when the account has an initialised local authority (a custody chain).</summary>
    public bool HasLocalAuthority(string handle) => GetCustodyHead(handle) is not null;

    // -----------------------------------------------------------------------
    // Projected-domain read helpers (thin wrappers over ReplicationDomainStore).
    // -----------------------------------------------------------------------

    /// <summary>Reads a converged projected domain entity, or null when absent.</summary>
    public ReplicationDomainStore.DomainEntity? GetReplicatedEntity(string kind, string entityId)
        => ReplicationDomainStore.GetEntity(conn, kind, entityId);

    /// <summary>Counts the converged projected lines of an entity.</summary>
    public int CountReplicatedLines(string kind, string entityId)
        => ReplicationDomainStore.CountLines(conn, kind, entityId);

    /// <summary>Counts stored package chunks for a package id.</summary>
    public int GetPackageChunkCount(string packageId)
        => ReplicationDomainStore.PackageChunkCount(conn, packageId);

    /// <summary>
    /// Test-only access to the underlying connection so unit tests can drive
    /// <see cref="ReplicationDomainStore"/> / <see cref="ReplicationPayloadCodec.Project"/> inside a
    /// caller-controlled transaction. Not used by production code.
    /// </summary>
    internal SqliteConnection RawConnectionForTest => conn;
}

