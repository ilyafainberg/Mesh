using Microsoft.Data.Sqlite;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Local SQLCipher schema and atomic primitives for protocol-9 online-only replication.
///
/// The device owns its immutable event log, replication cursors, outbox references,
/// persistence receipts, read watermarks and the custody hash chain. Nothing here talks
/// to the relay: it is purely the on-device store of record. Every method validates the
/// shared protocol limits and surfaces fork conditions as explicit exceptions rather than
/// swallowing them.
/// </summary>
public sealed partial class MeshDb
{
    /// <summary>Outcome of appending an event or custody entry.</summary>
    public enum ReplicationAppendResult
    {
        Inserted = 0,
        Duplicate = 1,
    }

    /// <summary>A due unit of outbox work: an event reference awaiting offer or persistence.</summary>
    public sealed record ReplicationOutboxWork(
        string EventId,
        string TargetAccount,
        string State,
        int Attempts,
        string? LastError);

    /// <summary>Raised when a log position is reused with conflicting content (a fork).</summary>
    public sealed class ReplicationForkException : Exception
    {
        public ReplicationForkException(string message) : base(message) { }
    }

    public const string OutboxStatePending = "pending";
    public const string OutboxStateOffered = "offered";
    public const string OutboxStatePersisted = "persisted";

    // Called from CreateSchema() after all existing schema initialisation.
    internal void CreateOnlineReplicationSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS replication_local_origins(
                origin_device_id TEXT PRIMARY KEY,
                log_epoch TEXT NOT NULL,
                next_seq INTEGER NOT NULL,
                auth_generation INTEGER NOT NULL,
                created_at TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS replication_events(
                origin_device_id TEXT NOT NULL,
                log_epoch TEXT NOT NULL,
                seq INTEGER NOT NULL,
                event_id TEXT NOT NULL UNIQUE,
                conversation_id TEXT,
                origin_account TEXT NOT NULL,
                auth_generation INTEGER NOT NULL,
                kind TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                causal_version TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                ciphertext TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                signature TEXT NOT NULL,
                PRIMARY KEY(origin_device_id, log_epoch, seq));
            CREATE INDEX IF NOT EXISTS ix_replication_events_conv
                ON replication_events(conversation_id, created_at);

            CREATE TABLE IF NOT EXISTS replication_outbox(
                event_id TEXT NOT NULL,
                target_account TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',
                offered_at TEXT,
                last_attempt_at TEXT,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                PRIMARY KEY(event_id, target_account),
                FOREIGN KEY(event_id) REFERENCES replication_events(event_id));
            CREATE INDEX IF NOT EXISTS ix_replication_outbox_due
                ON replication_outbox(target_account, state, event_id);

            CREATE TABLE IF NOT EXISTS replication_cursors(
                origin_device_id TEXT PRIMARY KEY,
                log_epoch TEXT NOT NULL,
                contiguous INTEGER NOT NULL,
                ahead_bits BLOB NOT NULL,
                updated_at TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS replication_receipts(
                receiver_device_id TEXT NOT NULL,
                origin_device_id TEXT NOT NULL,
                log_epoch TEXT NOT NULL,
                through_seq INTEGER NOT NULL,
                cursor_hash TEXT NOT NULL,
                batch_hash TEXT NOT NULL,
                signature TEXT NOT NULL,
                received_at TEXT NOT NULL,
                PRIMARY KEY(receiver_device_id, origin_device_id, log_epoch));

            CREATE TABLE IF NOT EXISTS replication_read_watermarks(
                conversation_id TEXT NOT NULL,
                account_handle TEXT NOT NULL,
                through_event_id TEXT NOT NULL,
                updated_at INTEGER NOT NULL,
                source_device_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                PRIMARY KEY(conversation_id, account_handle));

            CREATE TABLE IF NOT EXISTS custody_entries(
                handle TEXT NOT NULL,
                generation INTEGER NOT NULL,
                entry_hash TEXT NOT NULL,
                prev_hash TEXT NOT NULL,
                action INTEGER NOT NULL,
                subject_device_key TEXT NOT NULL,
                recovery_public_key TEXT,
                effective_at INTEGER NOT NULL,
                signer_key TEXT NOT NULL,
                signature TEXT NOT NULL,
                PRIMARY KEY(handle, generation),
                UNIQUE(entry_hash));

            CREATE TABLE IF NOT EXISTS replication_peer_state(
                peer_handle TEXT NOT NULL,
                peer_device TEXT NOT NULL,
                last_session TEXT,
                last_sync_at TEXT,
                last_error TEXT,
                PRIMARY KEY(peer_handle, peer_device));
            """);
    }

    // -----------------------------------------------------------------------
    // Local origin sequence allocation.
    // -----------------------------------------------------------------------

    /// <summary>Registers this device's local origin log if absent (idempotent).</summary>
    public void EnsureLocalOrigin(string originDeviceId, string logEpoch, long authGeneration)
    {
        if (string.IsNullOrWhiteSpace(originDeviceId)) throw new ArgumentException("Origin device id is required.", nameof(originDeviceId));
        if (string.IsNullOrWhiteSpace(logEpoch)) throw new ArgumentException("Log epoch is required.", nameof(logEpoch));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO replication_local_origins(origin_device_id, log_epoch, next_seq, auth_generation, created_at)
            VALUES($origin, $epoch, 1, $gen, $created)
            ON CONFLICT(origin_device_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        cmd.Parameters.AddWithValue("$epoch", logEpoch);
        cmd.Parameters.AddWithValue("$gen", authGeneration);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Atomically allocates the next strictly monotonic sequence for the local origin log,
    /// returning its epoch and the allocated sequence. Safe under concurrent callers.
    /// </summary>
    public (string LogEpoch, ulong Seq) AllocateNextSequence(string originDeviceId)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        string epoch;
        long next;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT log_epoch, next_seq FROM replication_local_origins WHERE origin_device_id = $origin;";
            read.Parameters.AddWithValue("$origin", originDeviceId);
            using var r = read.ExecuteReader();
            if (!r.Read())
                throw new InvalidOperationException("The local origin log has not been registered.");
            epoch = r.GetString(0);
            next = r.GetInt64(1);
        }
        if (next <= 0 || next == long.MaxValue)
            throw new InvalidOperationException("The local origin sequence is exhausted.");
        using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = "UPDATE replication_local_origins SET next_seq = $nn WHERE origin_device_id = $origin;";
            bump.Parameters.AddWithValue("$nn", next + 1);
            bump.Parameters.AddWithValue("$origin", originDeviceId);
            bump.ExecuteNonQuery();
        }
        tx.Commit();
        return (epoch, (ulong)next);
    }

    // -----------------------------------------------------------------------
    // Immutable event append.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends an immutable event. An exact re-insert of the same log position is idempotent;
    /// reusing the position with a different content hash or signature raises a fork.
    /// </summary>
    public ReplicationAppendResult AppendEvent(ReplicationEvent e)
    {
        if (!OnlineReplicationProtocol.ValidateEventShape(e, out var error))
            throw new ArgumentException(error, nameof(e));
        using var tx = conn.BeginTransaction(deferred: false);
        var result = AppendEventCore(e, tx);
        tx.Commit();
        return result;
    }

    private ReplicationAppendResult AppendEventCore(ReplicationEvent e, SqliteTransaction tx)
    {
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO replication_events(
                    origin_device_id, log_epoch, seq, event_id, conversation_id, origin_account,
                    auth_generation, kind, entity_id, causal_version, created_at, ciphertext,
                    content_hash, signature)
                VALUES($origin, $epoch, $seq, $eid, $conv, $account, $gen, $kind, $entity,
                    $causal, $created, $cipher, $chash, $sig);
                """;
            insert.Parameters.AddWithValue("$origin", e.OriginDeviceId);
            insert.Parameters.AddWithValue("$epoch", e.LogEpoch);
            insert.Parameters.AddWithValue("$seq", (long)e.Seq);
            insert.Parameters.AddWithValue("$eid", e.EventId);
            insert.Parameters.AddWithValue("$conv", (object?)e.ConversationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$account", e.OriginAccount);
            insert.Parameters.AddWithValue("$gen", e.AuthGeneration);
            insert.Parameters.AddWithValue("$kind", e.Kind);
            insert.Parameters.AddWithValue("$entity", e.EntityId);
            insert.Parameters.AddWithValue("$causal", e.CausalVersion);
            insert.Parameters.AddWithValue("$created", e.CreatedAtUnixMs);
            insert.Parameters.AddWithValue("$cipher", e.Ciphertext);
            insert.Parameters.AddWithValue("$chash", e.ContentHash);
            insert.Parameters.AddWithValue("$sig", e.Signature);
            try
            {
                insert.ExecuteNonQuery();
                return ReplicationAppendResult.Inserted;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Primary-key or unique conflict: distinguish idempotent replay from a fork.
            }
        }
        return ClassifyEventConflict(e, tx);
    }

    private ReplicationAppendResult ClassifyEventConflict(ReplicationEvent e, SqliteTransaction tx)
    {
        using var existing = conn.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText = """
            SELECT event_id, content_hash, signature FROM replication_events
            WHERE origin_device_id = $origin AND log_epoch = $epoch AND seq = $seq;
            """;
        existing.Parameters.AddWithValue("$origin", e.OriginDeviceId);
        existing.Parameters.AddWithValue("$epoch", e.LogEpoch);
        existing.Parameters.AddWithValue("$seq", (long)e.Seq);
        using var r = existing.ExecuteReader();
        if (!r.Read())
            throw new ReplicationForkException(
                $"Event id {e.EventId} conflicts with a different existing event.");
        var sameId = string.Equals(r.GetString(0), e.EventId, StringComparison.Ordinal);
        var sameHash = string.Equals(r.GetString(1), e.ContentHash, StringComparison.Ordinal);
        var sameSig = string.Equals(r.GetString(2), e.Signature, StringComparison.Ordinal);
        if (sameId && sameHash && sameSig) return ReplicationAppendResult.Duplicate;
        throw new ReplicationForkException(
            $"Log position ({e.OriginDeviceId},{e.LogEpoch},{e.Seq}) was reused with conflicting content.");
    }

    /// <summary>
    /// Appends a local event and enqueues one outbox reference per target account in a single
    /// transaction. The outbox stores references only; it never duplicates the payload.
    /// </summary>
    public ReplicationAppendResult AppendLocalEventWithOutbox(
        ReplicationEvent e,
        IReadOnlyCollection<string> targetAccounts)
    {
        ArgumentNullException.ThrowIfNull(targetAccounts);
        if (!OnlineReplicationProtocol.ValidateEventShape(e, out var error))
            throw new ArgumentException(error, nameof(e));
        using var tx = conn.BeginTransaction(deferred: false);
        var result = AppendEventCore(e, tx);
        foreach (var account in targetAccounts)
        {
            if (string.IsNullOrWhiteSpace(account)) continue;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO replication_outbox(event_id, target_account, state, attempts)
                VALUES($eid, $account, 'pending', 0)
                ON CONFLICT(event_id, target_account) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$eid", e.EventId);
            cmd.Parameters.AddWithValue("$account", account);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return result;
    }

    /// <summary>
    /// Atomically appends an inbound event and updates the origin cursor in one transaction.
    /// The optional <paramref name="domainApply"/> seam runs inside the same transaction so a
    /// future domain projection commits or rolls back together with the event and cursor.
    /// </summary>
    public ReplicationAppendResult ApplyInboundEvent(
        ReplicationEvent e,
        ReplicationCursorEntry updatedCursor,
        Action<SqliteConnection, SqliteTransaction>? domainApply = null)
    {
        if (!OnlineReplicationProtocol.ValidateEventShape(e, out var error))
            throw new ArgumentException(error, nameof(e));
        ArgumentNullException.ThrowIfNull(updatedCursor);
        if (updatedCursor.AheadBits is null
            || updatedCursor.AheadBits.Length != OnlineReplicationLimits.AheadBitsBytes)
            throw new ArgumentException("The cursor ahead-bitset is malformed.", nameof(updatedCursor));
        using var tx = conn.BeginTransaction(deferred: false);
        var result = AppendEventCore(e, tx);
        UpsertCursorCore(e.OriginDeviceId, updatedCursor, tx);
        domainApply?.Invoke(conn, tx);
        tx.Commit();
        return result;
    }

    // -----------------------------------------------------------------------
    // Event queries.
    // -----------------------------------------------------------------------

    /// <summary>Returns events for one origin log within an inclusive sequence range, ordered by sequence.</summary>
    public IReadOnlyList<ReplicationEvent> QueryEvents(
        string originDeviceId,
        string logEpoch,
        ulong fromSeq,
        ulong toSeq,
        int limit = OnlineReplicationLimits.MaxBatchOps)
    {
        if (fromSeq == 0 || toSeq < fromSeq) throw new ArgumentException("The sequence range is malformed.");
        if (limit is <= 0 or > OnlineReplicationLimits.MaxBatchOps)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT origin_device_id, log_epoch, seq, event_id, conversation_id, origin_account,
                   auth_generation, kind, entity_id, causal_version, created_at, ciphertext,
                   content_hash, signature
            FROM replication_events
            WHERE origin_device_id = $origin AND log_epoch = $epoch AND seq >= $from AND seq <= $to
            ORDER BY seq ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        cmd.Parameters.AddWithValue("$epoch", logEpoch);
        cmd.Parameters.AddWithValue("$from", (long)fromSeq);
        cmd.Parameters.AddWithValue("$to", (long)toSeq);
        cmd.Parameters.AddWithValue("$limit", limit);
        var events = new List<ReplicationEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) events.Add(ReadEvent(r));
        return events;
    }

    /// <summary>Returns a single event by its deterministic id, or null.</summary>
    public ReplicationEvent? GetEvent(string eventId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT origin_device_id, log_epoch, seq, event_id, conversation_id, origin_account,
                   auth_generation, kind, entity_id, causal_version, created_at, ciphertext,
                   content_hash, signature
            FROM replication_events WHERE event_id = $eid;
            """;
        cmd.Parameters.AddWithValue("$eid", eventId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEvent(r) : null;
    }

    private static ReplicationEvent ReadEvent(SqliteDataReader r)
        => new(
            EventId: r.GetString(3),
            ConversationId: r.IsDBNull(4) ? null : r.GetString(4),
            OriginAccount: r.GetString(5),
            OriginDeviceId: r.GetString(0),
            LogEpoch: r.GetString(1),
            Seq: (ulong)r.GetInt64(2),
            AuthGeneration: r.GetInt64(6),
            Kind: r.GetString(7),
            EntityId: r.GetString(8),
            CausalVersion: r.GetString(9),
            CreatedAtUnixMs: r.GetInt64(10),
            Ciphertext: r.GetString(11),
            ContentHash: r.GetString(12),
            Signature: r.GetString(13));

    // -----------------------------------------------------------------------
    // Cursors.
    // -----------------------------------------------------------------------

    /// <summary>Reads the replication cursor for an origin log, or null when none exists.</summary>
    public ReplicationCursorEntry? GetCursor(string originDeviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT log_epoch, contiguous, ahead_bits FROM replication_cursors WHERE origin_device_id = $origin;";
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var bits = (byte[])r.GetValue(2);
        return new ReplicationCursorEntry(r.GetString(0), (ulong)r.GetInt64(1), bits);
    }

    /// <summary>Upserts the replication cursor for an origin log.</summary>
    public void UpsertCursor(string originDeviceId, ReplicationCursorEntry cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.AheadBits is null || cursor.AheadBits.Length != OnlineReplicationLimits.AheadBitsBytes)
            throw new ArgumentException("The cursor ahead-bitset is malformed.", nameof(cursor));
        using var tx = conn.BeginTransaction(deferred: false);
        UpsertCursorCore(originDeviceId, cursor, tx);
        tx.Commit();
    }

    private void UpsertCursorCore(string originDeviceId, ReplicationCursorEntry cursor, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_cursors(origin_device_id, log_epoch, contiguous, ahead_bits, updated_at)
            VALUES($origin, $epoch, $contig, $bits, $updated)
            ON CONFLICT(origin_device_id) DO UPDATE SET
                log_epoch = excluded.log_epoch,
                contiguous = excluded.contiguous,
                ahead_bits = excluded.ahead_bits,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        cmd.Parameters.AddWithValue("$epoch", cursor.LogEpoch);
        cmd.Parameters.AddWithValue("$contig", (long)cursor.Contiguous);
        cmd.Parameters.AddWithValue("$bits", cursor.AheadBits);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // Outbox transitions.
    // -----------------------------------------------------------------------

    /// <summary>Marks an outbox reference as offered.</summary>
    public void MarkOutboxOffered(string eventId, string targetAccount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE replication_outbox
            SET state = 'offered', offered_at = $at, last_attempt_at = $at, attempts = attempts + 1
            WHERE event_id = $eid AND target_account = $account AND state = 'pending';
            """;
        cmd.Parameters.AddWithValue("$eid", eventId);
        cmd.Parameters.AddWithValue("$account", targetAccount);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records an outbox attempt error without changing state.</summary>
    public void RecordOutboxError(string eventId, string targetAccount, string error)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE replication_outbox
            SET last_attempt_at = $at, attempts = attempts + 1, last_error = $err
            WHERE event_id = $eid AND target_account = $account;
            """;
        cmd.Parameters.AddWithValue("$eid", eventId);
        cmd.Parameters.AddWithValue("$account", targetAccount);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$err", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Verifies a signed persistence receipt and, only if valid, stores it and transitions all
    /// covered outbox references (events at or below the receipted sequence) to persisted, in
    /// one transaction. Returns the number of outbox rows advanced.
    /// </summary>
    public int MarkOutboxPersistedFromReceipt(
        PersistenceReceipt receipt,
        string receiverPublicKeyB64,
        string targetAccount)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!OnlineReplicationProtocol.VerifyReceipt(receipt, receiverPublicKeyB64))
            throw new ArgumentException("The persistence receipt failed verification.", nameof(receipt));
        using var tx = conn.BeginTransaction(deferred: false);
        StoreReceiptCore(receipt, tx);
        int advanced;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE replication_outbox
                SET state = 'persisted', last_attempt_at = $at
                WHERE target_account = $account AND state != 'persisted' AND event_id IN (
                    SELECT event_id FROM replication_events
                    WHERE origin_device_id = $origin AND log_epoch = $epoch AND seq <= $through);
                """;
            cmd.Parameters.AddWithValue("$account", targetAccount);
            cmd.Parameters.AddWithValue("$origin", receipt.OriginDeviceId);
            cmd.Parameters.AddWithValue("$epoch", receipt.LogEpoch);
            cmd.Parameters.AddWithValue("$through", (long)receipt.ThroughSeq);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            advanced = cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return advanced;
    }

    /// <summary>Returns bounded due outbox work for a target account filtered by state.</summary>
    public IReadOnlyList<ReplicationOutboxWork> QueryDueOutbox(
        string targetAccount,
        string state,
        int limit = OnlineReplicationLimits.MaxBatchOps)
    {
        if (limit is <= 0 or > OnlineReplicationLimits.MaxBatchOps)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, target_account, state, attempts, last_error
            FROM replication_outbox
            WHERE target_account = $account AND state = $state
            ORDER BY event_id ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$account", targetAccount);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$limit", limit);
        var work = new List<ReplicationOutboxWork>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            work.Add(new ReplicationOutboxWork(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return work;
    }

    // -----------------------------------------------------------------------
    // Persistence receipts.
    // -----------------------------------------------------------------------

    /// <summary>Stores a receipt idempotently and monotonically (only advancing the through sequence).</summary>
    public void StoreReceipt(PersistenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var tx = conn.BeginTransaction(deferred: false);
        StoreReceiptCore(receipt, tx);
        tx.Commit();
    }

    private void StoreReceiptCore(PersistenceReceipt receipt, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_receipts(
                receiver_device_id, origin_device_id, log_epoch, through_seq,
                cursor_hash, batch_hash, signature, received_at)
            VALUES($receiver, $origin, $epoch, $through, $chash, $bhash, $sig, $at)
            ON CONFLICT(receiver_device_id, origin_device_id, log_epoch) DO UPDATE SET
                through_seq = excluded.through_seq,
                cursor_hash = excluded.cursor_hash,
                batch_hash = excluded.batch_hash,
                signature = excluded.signature,
                received_at = excluded.received_at
            WHERE excluded.through_seq > replication_receipts.through_seq;
            """;
        cmd.Parameters.AddWithValue("$receiver", receipt.ReceiverDeviceId);
        cmd.Parameters.AddWithValue("$origin", receipt.OriginDeviceId);
        cmd.Parameters.AddWithValue("$epoch", receipt.LogEpoch);
        cmd.Parameters.AddWithValue("$through", (long)receipt.ThroughSeq);
        cmd.Parameters.AddWithValue("$chash", receipt.CursorHash);
        cmd.Parameters.AddWithValue("$bhash", receipt.BatchHash);
        cmd.Parameters.AddWithValue("$sig", receipt.Signature);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads the stored receipt for a receiver/origin/epoch triple, or null.</summary>
    public PersistenceReceipt? GetReceipt(string receiverDeviceId, string originDeviceId, string logEpoch)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT receiver_device_id, origin_device_id, log_epoch, through_seq, cursor_hash, batch_hash, signature
            FROM replication_receipts
            WHERE receiver_device_id = $receiver AND origin_device_id = $origin AND log_epoch = $epoch;
            """;
        cmd.Parameters.AddWithValue("$receiver", receiverDeviceId);
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        cmd.Parameters.AddWithValue("$epoch", logEpoch);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new PersistenceReceipt(
            r.GetString(0), r.GetString(1), r.GetString(2), (ulong)r.GetInt64(3),
            r.GetString(4), r.GetString(5), r.GetString(6));
    }

    // -----------------------------------------------------------------------
    // Read watermarks (deterministic last-writer-wins).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Upserts a conversation read watermark. Deterministic last-writer-wins by version, then
    /// by through-event id as a tie-break. Returns true when the stored value advanced.
    /// </summary>
    public bool UpsertReadWatermark(ReadWatermarkPayload watermark)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO replication_read_watermarks(
                conversation_id, account_handle, through_event_id, updated_at, source_device_id, version)
            VALUES($conv, $account, $through, $updated, $device, $version)
            ON CONFLICT(conversation_id, account_handle) DO UPDATE SET
                through_event_id = excluded.through_event_id,
                updated_at = excluded.updated_at,
                source_device_id = excluded.source_device_id,
                version = excluded.version
            WHERE excluded.version > replication_read_watermarks.version
               OR (excluded.version = replication_read_watermarks.version
                   AND excluded.through_event_id > replication_read_watermarks.through_event_id);
            """;
        cmd.Parameters.AddWithValue("$conv", watermark.ConversationId);
        cmd.Parameters.AddWithValue("$account", watermark.AccountHandle);
        cmd.Parameters.AddWithValue("$through", watermark.ThroughEventId);
        cmd.Parameters.AddWithValue("$updated", watermark.UpdatedAtUnixMs);
        cmd.Parameters.AddWithValue("$device", watermark.SourceDeviceId);
        cmd.Parameters.AddWithValue("$version", watermark.Version);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Reads the current read watermark for a conversation/account pair, or null.</summary>
    public ReadWatermarkPayload? GetReadWatermark(string conversationId, string accountHandle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT conversation_id, account_handle, through_event_id, source_device_id, version, updated_at
            FROM replication_read_watermarks
            WHERE conversation_id = $conv AND account_handle = $account;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$account", accountHandle);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ReadWatermarkPayload(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5));
    }

    // -----------------------------------------------------------------------
    // Custody chain.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends a custody entry with hash-chain and fork validation. An exact re-append of an
    /// existing generation is idempotent; a differing entry at an existing generation, or a
    /// broken chain link, raises a fork or argument error.
    /// </summary>
    public ReplicationAppendResult AppendCustodyEntry(CustodyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!OnlineReplicationProtocol.VerifyCustodyEntry(entry, entry.SignerKey))
            throw new ArgumentException("The custody entry signature is invalid.", nameof(entry));
        using var tx = conn.BeginTransaction(deferred: false);

        var existing = GetCustodyEntryCore(entry.Handle, entry.Generation, tx);
        if (existing is not null)
        {
            tx.Commit();
            if (string.Equals(existing.EntryHash, entry.EntryHash, StringComparison.Ordinal))
                return ReplicationAppendResult.Duplicate;
            throw new ReplicationForkException(
                $"Custody generation {entry.Generation} for {entry.Handle} was reused with a conflicting entry.");
        }

        var head = GetCustodyHeadCore(entry.Handle, tx);
        var validation = OnlineReplicationProtocol.ValidateCustodyAppend(head, entry);
        if (validation == CustodyValidationResult.Fork)
            throw new ReplicationForkException(
                $"Custody entry at generation {entry.Generation} for {entry.Handle} forks the chain.");
        if (validation != CustodyValidationResult.Valid)
            throw new ArgumentException($"Custody entry is invalid: {validation}.", nameof(entry));

        var chain = GetCustodyChainCore(entry.Handle, tx);
        var authorized = new HashSet<string>(StringComparer.Ordinal);
        string? recoveryKey = null;
        foreach (var prior in chain)
        {
            switch (prior.Action)
            {
                case CustodyAction.Genesis:
                case CustodyAction.AddDevice:
                    authorized.Add(prior.SubjectDeviceKey);
                    if (prior.Action == CustodyAction.Genesis)
                        recoveryKey = prior.RecoveryPublicKey;
                    break;
                case CustodyAction.RemoveDevice:
                    authorized.Remove(prior.SubjectDeviceKey);
                    break;
                case CustodyAction.RekeyRecovery:
                    recoveryKey = prior.RecoveryPublicKey;
                    break;
            }
        }
        var authorizedSigner = entry.Action switch
        {
            CustodyAction.Genesis =>
                head is null
                && string.Equals(
                    entry.SignerKey,
                    entry.SubjectDeviceKey,
                    StringComparison.Ordinal),
            CustodyAction.AddDevice =>
                authorized.Contains(entry.SignerKey)
                && !authorized.Contains(entry.SubjectDeviceKey),
            CustodyAction.RemoveDevice =>
                authorized.Contains(entry.SignerKey)
                && OnlineReplicationProtocol.CanRemoveDevice(
                    authorized,
                    entry.SubjectDeviceKey),
            CustodyAction.RekeyRecovery =>
                !string.IsNullOrWhiteSpace(recoveryKey)
                && string.Equals(
                    entry.SignerKey,
                    recoveryKey,
                    StringComparison.Ordinal),
            _ => false
        };
        if (!authorizedSigner)
            throw new ArgumentException(
                "The custody entry signer is not authorized for this action.",
                nameof(entry));

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO custody_entries(
                    handle, generation, entry_hash, prev_hash, action, subject_device_key,
                    recovery_public_key, effective_at, signer_key, signature)
                VALUES($handle, $gen, $ehash, $phash, $action, $subject, $recovery, $eff, $signer, $sig);
                """;
            cmd.Parameters.AddWithValue("$handle", entry.Handle);
            cmd.Parameters.AddWithValue("$gen", entry.Generation);
            cmd.Parameters.AddWithValue("$ehash", entry.EntryHash);
            cmd.Parameters.AddWithValue("$phash", entry.PrevHash);
            cmd.Parameters.AddWithValue("$action", (int)entry.Action);
            cmd.Parameters.AddWithValue("$subject", entry.SubjectDeviceKey);
            cmd.Parameters.AddWithValue("$recovery", (object?)entry.RecoveryPublicKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$eff", entry.EffectiveAtUnixMs);
            cmd.Parameters.AddWithValue("$signer", entry.SignerKey);
            cmd.Parameters.AddWithValue("$sig", entry.Signature);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return ReplicationAppendResult.Inserted;
    }

    private IReadOnlyList<CustodyEntry> GetCustodyChainCore(
        string handle,
        SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CustodySelect + " WHERE handle = $handle ORDER BY generation ASC;";
        cmd.Parameters.AddWithValue("$handle", handle);
        var chain = new List<CustodyEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) chain.Add(ReadCustody(reader));
        return chain;
    }

    /// <summary>Returns the highest-generation custody entry for a handle, or null.</summary>
    public CustodyEntry? GetCustodyHead(string handle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CustodySelect + " WHERE handle = $handle ORDER BY generation DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$handle", handle);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCustody(r) : null;
    }

    /// <summary>Returns the full custody chain for a handle ordered by generation.</summary>
    public IReadOnlyList<CustodyEntry> GetCustodyChain(string handle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CustodySelect + " WHERE handle = $handle ORDER BY generation ASC;";
        cmd.Parameters.AddWithValue("$handle", handle);
        var chain = new List<CustodyEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) chain.Add(ReadCustody(r));
        return chain;
    }

    private CustodyEntry? GetCustodyHeadCore(string handle, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CustodySelect + " WHERE handle = $handle ORDER BY generation DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$handle", handle);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCustody(r) : null;
    }

    private CustodyEntry? GetCustodyEntryCore(string handle, long generation, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CustodySelect + " WHERE handle = $handle AND generation = $gen;";
        cmd.Parameters.AddWithValue("$handle", handle);
        cmd.Parameters.AddWithValue("$gen", generation);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCustody(r) : null;
    }

    private const string CustodySelect = """
        SELECT handle, generation, entry_hash, prev_hash, action, subject_device_key,
               recovery_public_key, effective_at, signer_key, signature
        FROM custody_entries
        """;

    private static CustodyEntry ReadCustody(SqliteDataReader r)
        => new(
            Handle: r.GetString(0),
            Generation: r.GetInt64(1),
            EntryHash: r.GetString(2),
            PrevHash: r.GetString(3),
            Action: (CustodyAction)r.GetInt32(4),
            SubjectDeviceKey: r.GetString(5),
            RecoveryPublicKey: r.IsDBNull(6) ? null : r.GetString(6),
            EffectiveAtUnixMs: r.GetInt64(7),
            SignerKey: r.GetString(8),
            Signature: r.GetString(9));

    // -----------------------------------------------------------------------
    // Peer state metadata (no payloads).
    // -----------------------------------------------------------------------

    /// <summary>Upserts peer sync metadata (never payloads).</summary>
    public void UpsertPeerState(string peerHandle, string peerDevice, string? lastSession, string? lastError)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO replication_peer_state(peer_handle, peer_device, last_session, last_sync_at, last_error)
            VALUES($handle, $device, $session, $at, $error)
            ON CONFLICT(peer_handle, peer_device) DO UPDATE SET
                last_session = excluded.last_session,
                last_sync_at = excluded.last_sync_at,
                last_error = excluded.last_error;
            """;
        cmd.Parameters.AddWithValue("$handle", peerHandle);
        cmd.Parameters.AddWithValue("$device", peerDevice);
        cmd.Parameters.AddWithValue("$session", (object?)lastSession ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
