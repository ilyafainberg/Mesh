using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshDb
{
    private const int MaxDeferredTopicUpdatesPerRun = 512;

    public sealed record DeferredTopicRunUpdate(
        string EnvelopeId,
        TopicRunUpdatePayload Update,
        DateTimeOffset ReceivedAt);

    private void CreateDeferredTopicUpdateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS deferred_topic_updates(
                envelope_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                delta_seq INTEGER NOT NULL,
                update_json TEXT NOT NULL,
                received_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_deferred_topic_updates_run
                ON deferred_topic_updates(run_id, delta_seq, received_at);
            """);
    }

    public void SaveDeferredTopicRunUpdate(
        string envelopeId,
        TopicRunUpdatePayload update,
        DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
        ArgumentNullException.ThrowIfNull(update);
        using var transaction = conn.BeginTransaction(deferred: false);
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO deferred_topic_updates(
                    envelope_id, run_id, thread_id, delta_seq, update_json, received_at)
                VALUES($envelope, $run, $thread, $seq, $update, $received);
                """;
            insert.Parameters.AddWithValue("$envelope", envelopeId);
            insert.Parameters.AddWithValue("$run", update.RunId);
            insert.Parameters.AddWithValue("$thread", update.ThreadId);
            insert.Parameters.AddWithValue("$seq", checked((long)update.DeltaSeq));
            insert.Parameters.AddWithValue("$update", TopicRunProtocol.UpdateBody(update));
            insert.Parameters.AddWithValue("$received", receivedAt.ToString("O"));
            insert.ExecuteNonQuery();
        }
        using (var prune = conn.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM deferred_topic_updates
                WHERE envelope_id IN (
                    SELECT envelope_id
                    FROM deferred_topic_updates
                    WHERE run_id = $run
                    ORDER BY delta_seq DESC, received_at DESC
                    LIMIT -1 OFFSET $retain);
                """;
            prune.Parameters.AddWithValue("$run", update.RunId);
            prune.Parameters.AddWithValue("$retain", MaxDeferredTopicUpdatesPerRun);
            prune.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<DeferredTopicRunUpdate> ListDeferredTopicRunUpdates()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT envelope_id, update_json, received_at
            FROM deferred_topic_updates
            ORDER BY received_at, delta_seq, envelope_id;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<DeferredTopicRunUpdate>();
        while (reader.Read())
        {
            var raw = reader.GetString(1);
            if (!TopicRunProtocol.TryParseUpdate(raw, out var update))
                throw new InvalidDataException("A deferred topic update is corrupt.");
            result.Add(new DeferredTopicRunUpdate(
                reader.GetString(0),
                update,
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return result;
    }

    public void DeleteDeferredTopicRunUpdate(string envelopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM deferred_topic_updates WHERE envelope_id = $envelope;";
        cmd.Parameters.AddWithValue("$envelope", envelopeId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteDeferredTopicRunUpdates(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM deferred_topic_updates WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.ExecuteNonQuery();
    }

    public void UpsertTopicOutbox(TopicOutboxItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO topic_outbox(
                run_id, thread_id, trigger_line_id, target_device_id, request_json,
                attachments_json, state, created_at, updated_at, last_error)
            VALUES($run, $thread, $line, $device, $request, $attachments, $state, $created, $updated, $error)
            ON CONFLICT(run_id) DO UPDATE SET
                request_json = excluded.request_json,
                attachments_json = excluded.attachments_json,
                state = excluded.state,
                updated_at = excluded.updated_at,
                last_error = excluded.last_error;
            """;
        cmd.Parameters.AddWithValue("$run", item.RunId);
        cmd.Parameters.AddWithValue("$thread", item.ThreadId);
        cmd.Parameters.AddWithValue("$line", item.TriggerLineId);
        cmd.Parameters.AddWithValue("$device", item.TargetDeviceId);
        cmd.Parameters.AddWithValue("$request", JsonSerializer.Serialize(item.Request, JsonOpts));
        cmd.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(item.Attachments, JsonOpts));
        cmd.Parameters.AddWithValue("$state", item.State);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)item.LastError ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public TopicOutboxItem? GetTopicOutbox(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM topic_outbox WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTopicOutbox(reader) : null;
    }

    public IReadOnlyList<TopicOutboxItem> ListTopicOutbox()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM topic_outbox ORDER BY created_at, run_id;";
        using var reader = cmd.ExecuteReader();
        var result = new List<TopicOutboxItem>();
        while (reader.Read()) result.Add(ReadTopicOutbox(reader));
        return result;
    }

    public void SetTopicOutboxState(string runId, string state, string? error = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE topic_outbox
            SET state = $state, updated_at = $updated, last_error = $error
            WHERE run_id = $run;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTopicOutbox(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM topic_outbox WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.ExecuteNonQuery();
    }

    public InboundTopicRunItem? GetInboundTopicRun(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM inbound_topic_runs WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadInboundTopicRun(reader) : null;
    }

    public InboundTopicCancellationItem? GetInboundTopicCancellation(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM inbound_topic_cancellations WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new InboundTopicCancellationItem(
                reader.GetString(reader.GetOrdinal("run_id")),
                reader.GetString(reader.GetOrdinal("source_device_id")),
                reader.GetString(reader.GetOrdinal("thread_id")),
                reader.GetString(reader.GetOrdinal("terminal_update_json")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))))
            : null;
    }

    public bool TryAddInboundTopicCancellation(InboundTopicCancellationItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO inbound_topic_cancellations(
                run_id, source_device_id, thread_id, terminal_update_json, created_at)
            VALUES($run, $source, $thread, $terminal, $created);
            """;
        cmd.Parameters.AddWithValue("$run", item.RunId);
        cmd.Parameters.AddWithValue("$source", item.SourceDeviceId);
        cmd.Parameters.AddWithValue("$thread", item.ThreadId);
        cmd.Parameters.AddWithValue("$terminal", item.TerminalUpdateJson);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool TryAddInboundTopicRun(InboundTopicRunItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO inbound_topic_runs(
                run_id, source_device_id, request_json, state, accepted_at, updated_at,
                terminal_update_json, queue_sequence)
            SELECT $run, $source, $request, $state, $accepted, $updated, $terminal,
                   COALESCE(MAX(queue_sequence), 0) + 1
            FROM inbound_topic_runs;
            """;
        cmd.Parameters.AddWithValue("$run", item.RunId);
        cmd.Parameters.AddWithValue("$source", item.SourceDeviceId);
        cmd.Parameters.AddWithValue("$request", JsonSerializer.Serialize(item.Request, JsonOpts));
        cmd.Parameters.AddWithValue("$state", item.State);
        cmd.Parameters.AddWithValue("$accepted", item.AcceptedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$terminal", (object?)item.TerminalUpdateJson ?? DBNull.Value);
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<InboundTopicRunItem> ListInboundTopicRuns(params string[] states)
    {
        using var cmd = conn.CreateCommand();
        if (states.Length == 0)
        {
            cmd.CommandText = "SELECT * FROM inbound_topic_runs ORDER BY queue_sequence, run_id;";
        }
        else
        {
            var names = new List<string>(states.Length);
            for (var i = 0; i < states.Length; i++)
            {
                var name = "$state" + i;
                names.Add(name);
                cmd.Parameters.AddWithValue(name, states[i]);
            }
            cmd.CommandText = $"SELECT * FROM inbound_topic_runs WHERE state IN ({string.Join(",", names)}) ORDER BY queue_sequence, run_id;";
        }
        using var reader = cmd.ExecuteReader();
        var result = new List<InboundTopicRunItem>();
        while (reader.Read()) result.Add(ReadInboundTopicRun(reader));
        return result;
    }

    public bool SetInboundTopicRunState(string runId, string state)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE inbound_topic_runs SET state = $state, updated_at = $updated WHERE run_id = $run AND terminal_update_json IS NULL;";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool SetInboundTopicRunTerminal(
        string runId,
        string state,
        TopicRunUpdatePayload terminalUpdate)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE inbound_topic_runs
            SET state = $state,
                updated_at = $updated,
                terminal_update_json = $terminal
            WHERE run_id = $run
              AND terminal_update_json IS NULL;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$terminal", TopicRunProtocol.UpdateBody(terminalUpdate));
        if (cmd.ExecuteNonQuery() == 1) return true;

        using var check = conn.CreateCommand();
        check.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM inbound_topic_runs
                WHERE run_id = $run
                  AND terminal_update_json IS NOT NULL);
            """;
        check.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt64(check.ExecuteScalar()) == 1;
    }

    public bool SetInboundTopicRunTerminalAndQueue(
        string runId,
        string state,
        TopicRunUpdatePayload terminalUpdate,
        DeviceEnvelopeOutboxItem outbox)
    {
        using var transaction = conn.BeginTransaction();
        using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE inbound_topic_runs
            SET state = $state,
                updated_at = $updated,
                terminal_update_json = $terminal
            WHERE run_id = $run
              AND terminal_update_json IS NULL;
            """;
        update.Parameters.AddWithValue("$run", runId);
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$terminal", TopicRunProtocol.UpdateBody(terminalUpdate));
        var wonTerminal = update.ExecuteNonQuery() == 1;
        if (wonTerminal)
        {
            using var queue = conn.CreateCommand();
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT OR IGNORE INTO device_envelope_outbox(
                    envelope_id, target_device_id, kind, plaintext, push_hint, created_at)
                VALUES($id, $device, $kind, $plaintext, $push, $created);
                """;
            queue.Parameters.AddWithValue("$id", outbox.EnvelopeId);
            queue.Parameters.AddWithValue("$device", outbox.TargetDeviceId);
            queue.Parameters.AddWithValue("$kind", outbox.Kind);
            queue.Parameters.AddWithValue("$plaintext", outbox.Plaintext);
            queue.Parameters.AddWithValue("$push", (object?)outbox.PushHint ?? DBNull.Value);
            queue.Parameters.AddWithValue("$created", outbox.CreatedAt.ToString("O"));
            queue.ExecuteNonQuery();
        }
        transaction.Commit();
        if (wonTerminal) return true;

        using var check = conn.CreateCommand();
        check.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM inbound_topic_runs
                WHERE run_id = $run AND terminal_update_json IS NOT NULL);
            """;
        check.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt64(check.ExecuteScalar()) == 1;
    }
    public int PruneInboundTopicRuns(DateTimeOffset updatedBefore)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM inbound_topic_runs
            WHERE updated_at < $before
              AND state IN ($completed, $failed, $cancelled, $interrupted);
            """;
        cmd.Parameters.AddWithValue("$before", updatedBefore.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", InboundTopicRunStates.Completed);
        cmd.Parameters.AddWithValue("$failed", InboundTopicRunStates.Failed);
        cmd.Parameters.AddWithValue("$cancelled", InboundTopicRunStates.Cancelled);
        cmd.Parameters.AddWithValue("$interrupted", InboundTopicRunStates.Interrupted);
        return cmd.ExecuteNonQuery();
    }

    public int PruneInboundTopicCancellations(DateTimeOffset createdBefore)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM inbound_topic_cancellations WHERE created_at < $before;";
        cmd.Parameters.AddWithValue("$before", createdBefore.ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    public void UpsertInboundRejection(InboundRejectionItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inbound_rejections(
                rejection_id, envelope_id, relay_receipt_id, kind, from_handle,
                from_device_id, reason, rejected_at)
            VALUES($id, $envelope, $delivery, $kind, $from, $device, $reason, $rejected)
            ON CONFLICT(rejection_id) DO UPDATE SET
                reason = excluded.reason,
                rejected_at = excluded.rejected_at;
            """;
        cmd.Parameters.AddWithValue("$id", item.RejectionId);
        cmd.Parameters.AddWithValue("$envelope", item.EnvelopeId);
        cmd.Parameters.AddWithValue("$delivery", (object?)item.RelayReceiptId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", item.Kind);
        cmd.Parameters.AddWithValue("$from", item.FromHandle);
        cmd.Parameters.AddWithValue("$device", (object?)item.FromDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", item.Reason);
        cmd.Parameters.AddWithValue("$rejected", item.RejectedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<InboundRejectionItem> ListInboundRejections()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM inbound_rejections ORDER BY rejected_at, rejection_id;";
        using var reader = cmd.ExecuteReader();
        var result = new List<InboundRejectionItem>();
        while (reader.Read())
        {
            result.Add(new InboundRejectionItem(
                reader.GetString(reader.GetOrdinal("rejection_id")),
                reader.GetString(reader.GetOrdinal("envelope_id")),
                reader.IsDBNull(reader.GetOrdinal("relay_receipt_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("relay_receipt_id")),
                reader.GetString(reader.GetOrdinal("kind")),
                reader.GetString(reader.GetOrdinal("from_handle")),
                reader.IsDBNull(reader.GetOrdinal("from_device_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("from_device_id")),
                reader.GetString(reader.GetOrdinal("reason")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("rejected_at")))));
        }
        return result;
    }

    public int PruneInboundRejections(DateTimeOffset rejectedBefore)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM inbound_rejections WHERE rejected_at < $before;";
        cmd.Parameters.AddWithValue("$before", rejectedBefore.ToString("O"));
        return cmd.ExecuteNonQuery();
    }
    public void UpsertDeviceEnvelopeOutbox(DeviceEnvelopeOutboxItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO device_envelope_outbox(
                envelope_id, target_device_id, kind, plaintext, push_hint, created_at)
            VALUES($id, $device, $kind, $plaintext, $push, $created);
            """;
        cmd.Parameters.AddWithValue("$id", item.EnvelopeId);
        cmd.Parameters.AddWithValue("$device", item.TargetDeviceId);
        cmd.Parameters.AddWithValue("$kind", item.Kind);
        cmd.Parameters.AddWithValue("$plaintext", item.Plaintext);
        cmd.Parameters.AddWithValue("$push", (object?)item.PushHint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<DeviceEnvelopeOutboxItem> ListDeviceEnvelopeOutbox()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM device_envelope_outbox ORDER BY created_at, envelope_id;";
        using var reader = cmd.ExecuteReader();
        var result = new List<DeviceEnvelopeOutboxItem>();
        while (reader.Read())
        {
            result.Add(new DeviceEnvelopeOutboxItem(
                reader.GetString(reader.GetOrdinal("envelope_id")),
                reader.GetString(reader.GetOrdinal("target_device_id")),
                reader.GetString(reader.GetOrdinal("kind")),
                reader.GetString(reader.GetOrdinal("plaintext")),
                reader.IsDBNull(reader.GetOrdinal("push_hint"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("push_hint")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")))));
        }
        return result;
    }

    public void DeleteDeviceEnvelopeOutbox(string envelopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM device_envelope_outbox WHERE envelope_id = $id;";
        cmd.Parameters.AddWithValue("$id", envelopeId);
        cmd.ExecuteNonQuery();
    }

    private static InboundTopicRunItem ReadInboundTopicRun(SqliteDataReader reader)
        => new(
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetString(reader.GetOrdinal("source_device_id")),
            JsonSerializer.Deserialize<TopicRunRequestPayload>(
                reader.GetString(reader.GetOrdinal("request_json")), JsonOpts)!,
            reader.GetString(reader.GetOrdinal("state")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("accepted_at"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            reader.IsDBNull(reader.GetOrdinal("terminal_update_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("terminal_update_json")),
            reader.GetInt64(reader.GetOrdinal("queue_sequence")));
    private static TopicOutboxItem ReadTopicOutbox(SqliteDataReader reader)
        => new(
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetString(reader.GetOrdinal("thread_id")),
            reader.GetString(reader.GetOrdinal("trigger_line_id")),
            reader.GetString(reader.GetOrdinal("target_device_id")),
            JsonSerializer.Deserialize<TopicRunRequestPayload>(
                reader.GetString(reader.GetOrdinal("request_json")), JsonOpts)!,
            JsonSerializer.Deserialize<List<ChatAttachment>>(
                reader.GetString(reader.GetOrdinal("attachments_json")), JsonOpts) ?? [],
            reader.GetString(reader.GetOrdinal("state")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            reader.IsDBNull(reader.GetOrdinal("last_error"))
                ? null
                : reader.GetString(reader.GetOrdinal("last_error")));
}
