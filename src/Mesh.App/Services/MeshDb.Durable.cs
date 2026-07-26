using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshDb
{
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

    public bool TryAddInboundTopicRun(InboundTopicRunItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO inbound_topic_runs(
                run_id, source_device_id, request_json, state, accepted_at, updated_at, terminal_update_json)
            VALUES($run, $source, $request, $state, $accepted, $updated, $terminal);
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
            cmd.CommandText = "SELECT * FROM inbound_topic_runs ORDER BY accepted_at, run_id;";
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
            cmd.CommandText = $"SELECT * FROM inbound_topic_runs WHERE state IN ({string.Join(",", names)}) ORDER BY accepted_at, run_id;";
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
                : reader.GetString(reader.GetOrdinal("terminal_update_json")));
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
