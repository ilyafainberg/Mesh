using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshDb
{
    private const int MaxDeferredTopicUpdatesPerRun = 512;

    internal enum TopicRunBeginCheckpoint
    {
        TriggerPersisted,
        ThreadBound,
        PromptPersisted,
        OutboxPersisted,
        CorrelationPersisted,
        LocalRunPersisted,
        BeforeCommit
    }

    public sealed record DeferredTopicRunUpdate(
        string EnvelopeId,
        TopicRunUpdatePayload Update,
        DateTimeOffset ReceivedAt);

    internal sealed record TopicRunTriggerObservation(
        TopicRunTriggerItem? Trigger,
        TopicOutboxItem? Outbox,
        long Epoch);

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

    public DeviceEnvelopeOutboxItem? GetDeviceEnvelopeOutbox(string envelopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM device_envelope_outbox WHERE envelope_id = $id;";
        cmd.Parameters.AddWithValue("$id", envelopeId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDeviceEnvelopeOutbox(reader) : null;
    }

    public TopicReceiptOutboxPersistenceResult GetOrCreateTopicReceiptOutbox(
        DeviceEnvelopeOutboxItem candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.Kind, MeshKinds.TopicRunUpdate, StringComparison.Ordinal)
            || !TopicRunProtocol.TryParseUpdate(candidate.Plaintext, out var receipt)
            || !TopicControlProtocol.IsReceipt(receipt))
            throw new ArgumentException("A topic control receipt outbox item is required.", nameof(candidate));

        using var transaction = conn.BeginTransaction(deferred: false);
        DeviceEnvelopeOutboxItem? existing;
        using (var query = conn.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT * FROM device_envelope_outbox WHERE envelope_id = $id;";
            query.Parameters.AddWithValue("$id", candidate.EnvelopeId);
            using var reader = query.ExecuteReader();
            existing = reader.Read() ? ReadDeviceEnvelopeOutbox(reader) : null;
        }

        if (existing is not null)
        {
            return new TopicReceiptOutboxPersistenceResult(
                TopicReceiptOutboxSemanticallyMatches(existing, candidate)
                    ? TopicReceiptOutboxPersistenceKind.Reused
                    : TopicReceiptOutboxPersistenceKind.IdentityConflict,
                existing);
        }

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = transaction;
            BindDeviceEnvelopeInsert(insert, candidate);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The topic receipt outbox insert lost its write fence.");
        }
        transaction.Commit();
        return new TopicReceiptOutboxPersistenceResult(
            TopicReceiptOutboxPersistenceKind.Created,
            candidate);
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

    internal TopicRunBeginResult BeginTopicRun(
        TopicRunBeginCommand command,
        Action<TopicRunBeginCheckpoint>? checkpoint = null,
        long? expectedObservationEpoch = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Draft);
        ArgumentNullException.ThrowIfNull(command.Target);
        var proposedDraft = command.Draft;
        var isRemote = command.Mode == TopicRunBeginMode.Remote;
        if (isRemote != (command.Request is not null))
            throw new ArgumentException("Remote begin requires exactly one request.", nameof(command));
        if (isRemote
            && (!string.Equals(
                    command.Request!.RunId, proposedDraft.RunId, StringComparison.Ordinal)
                || !string.Equals(
                    command.Request.ThreadId, proposedDraft.ThreadId, StringComparison.Ordinal)
                || !string.Equals(
                    command.Request.TriggerLineId, proposedDraft.TriggerLineId, StringComparison.Ordinal)
                || !string.Equals(
                    command.Request.TargetDeviceId, command.Target.DeviceId, StringComparison.Ordinal)))
            return new TopicRunBeginResult(false, false, "run_identity_conflict");

        var triggerId = TopicRunTriggerIdentity.For(
            proposedDraft.ThreadId,
            proposedDraft.TriggerLineId,
            proposedDraft.TriggerOperationId);
        var payloadHash = TopicRunTriggerIdentity.PayloadHash(command);
        var now = command.InitialProjection.Timestamp;
        var attachments = command.Attachments?.Select(CloneAttachment).ToArray()
                          ?? Array.Empty<ChatAttachment>();

        using var transaction = conn.BeginTransaction(deferred: false);
        if (expectedObservationEpoch is not null
            && ReadTopicTriggerEpoch(transaction) != expectedObservationEpoch.Value)
            return new TopicRunBeginResult(false, false, "reconcile_required");
        var existingTrigger = GetTopicRunTrigger(transaction, triggerId);
        if (existingTrigger is null
            && !string.IsNullOrWhiteSpace(proposedDraft.TriggerOperationId))
        {
            var legacyTriggerId = TopicRunTriggerIdentity.For(
                proposedDraft.ThreadId, proposedDraft.TriggerLineId);
            existingTrigger = GetTopicRunTrigger(transaction, legacyTriggerId);
            if (existingTrigger is not null) triggerId = legacyTriggerId;
        }
        if (existingTrigger is not null)
        {
            if (!TopicRunTriggerMatches(existingTrigger, command, payloadHash))
                return new TopicRunBeginResult(
                    false,
                    false,
                    "trigger_identity_conflict",
                    AuthoritativeRunId: existingTrigger.RunId,
                    TriggerId: triggerId);
            command = RebindTopicRun(command, existingTrigger.RunId);
        }
        else if (GetTopicRunTriggerByRunId(transaction, proposedDraft.RunId) is not null)
        {
            return new TopicRunBeginResult(
                false, false, "run_id_conflict", TriggerId: triggerId);
        }

        var draft = command.Draft;
        var requestJson = isRemote
            ? JsonSerializer.Serialize(command.Request, JsonOpts)
            : null;
        var attachmentsJson = isRemote
            ? JsonSerializer.Serialize(attachments, JsonOpts)
            : null;
        string? boundDevice;
        using (var thread = conn.CreateCommand())
        {
            thread.Transaction = transaction;
            thread.CommandText = """
                SELECT agent_execution_host_device_id
                FROM own_threads
                WHERE id = $thread;
                """;
            thread.Parameters.AddWithValue("$thread", draft.ThreadId);
            using var reader = thread.ExecuteReader();
            if (!reader.Read())
                return new TopicRunBeginResult(
                    false, false, "thread_not_found",
                    AuthoritativeRunId: draft.RunId,
                    TriggerId: triggerId,
                    AuthoritativeDraft: draft);
            boundDevice = reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        if (boundDevice is not null
            && !string.Equals(boundDevice, command.Target.DeviceId, StringComparison.Ordinal))
            return new TopicRunBeginResult(
                false, false, "topic_bound_elsewhere",
                AuthoritativeRunId: draft.RunId,
                TriggerId: triggerId,
                AuthoritativeDraft: draft);
        using (var prompt = conn.CreateCommand())
        {
            prompt.Transaction = transaction;
            prompt.CommandText = """
                SELECT role, text, sender_handle, at
                FROM own_chat
                WHERE thread_id = $thread AND line_id = $line
                LIMIT 1;
                """;
            prompt.Parameters.AddWithValue("$thread", draft.ThreadId);
            prompt.Parameters.AddWithValue("$line", draft.TriggerLineId);
            using var reader = prompt.ExecuteReader();
            if (reader.Read()
                && (!string.Equals(reader.GetString(0), "user", StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(1), draft.Prompt, StringComparison.Ordinal)
                    || !string.Equals(
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        draft.TriggerHandle,
                        StringComparison.Ordinal)
                    || DateTimeOffset.Parse(reader.GetString(3)) != draft.TriggerAt))
                return new TopicRunBeginResult(
                    false, false, "trigger_line_conflict",
                    AuthoritativeRunId: draft.RunId,
                    TriggerId: triggerId,
                    AuthoritativeDraft: draft);
        }
        if (existingTrigger?.TerminalAt is not null)
        {
            transaction.Commit();
            return new TopicRunBeginResult(
                true,
                false,
                "already_completed",
                AuthoritativeRunId: draft.RunId,
                TriggerId: triggerId,
                AuthoritativeDraft: draft);
        }

        if (!isRemote)
        {
            var existing = GetLocalTopicRun(transaction, draft.RunId);
            if (existing is not null)
            {
                if (!LocalTopicRunMatches(existing, command))
                    return new TopicRunBeginResult(
                        false, false, "run_id_conflict",
                        AuthoritativeRunId: draft.RunId,
                        TriggerId: triggerId,
                        AuthoritativeDraft: draft);
                transaction.Commit();
                return new TopicRunBeginResult(
                    true,
                    false,
                    existing.TerminalAt is null ? "already_started" : "already_completed",
                    AuthoritativeRunId: draft.RunId,
                    TriggerId: triggerId,
                    AuthoritativeDraft: draft);
            }
            if (TopicRunArtifactsExist(transaction, draft.RunId))
                return new TopicRunBeginResult(
                    false, false, "run_id_conflict",
                    AuthoritativeRunId: draft.RunId,
                    TriggerId: triggerId,
                    AuthoritativeDraft: draft);
        }
        else
        {
            var existingCorrelation = GetTopicRunCorrelation(transaction, draft.RunId);
            var existingOutbox = GetTopicOutbox(transaction, draft.RunId);
            if (existingCorrelation is not null || existingOutbox is not null)
            {
                if (existingCorrelation is null
                    || !TopicRunCorrelationMatches(existingCorrelation, command)
                    || existingOutbox is not null
                       && !TopicOutboxMatches(
                           existingOutbox, command, attachmentsJson!))
                    return new TopicRunBeginResult(false, false, "run_id_conflict");
                transaction.Commit();
                return new TopicRunBeginResult(
                    true,
                    false,
                    existingOutbox is null ? "already_completed" : "already_started",
                    existingOutbox,
                    draft.RunId,
                    triggerId,
                    draft);
            }
            if (GetLocalTopicRun(transaction, draft.RunId) is not null)
                return new TopicRunBeginResult(
                    false, false, "run_id_conflict",
                    AuthoritativeRunId: draft.RunId,
                    TriggerId: triggerId,
                    AuthoritativeDraft: draft);
        }

        if (existingTrigger is null)
        {
            using var trigger = conn.CreateCommand();
            trigger.Transaction = transaction;
            trigger.CommandText = """
                INSERT INTO topic_run_triggers(
                    trigger_id, run_id, mode, thread_id, trigger_line_id, target_device_id,
                    payload_hash, created_at, terminal_at)
                VALUES($trigger, $run, $mode, $thread, $line, $target, $payload, $created, NULL);
                """;
            trigger.Parameters.AddWithValue("$trigger", triggerId);
            trigger.Parameters.AddWithValue("$run", draft.RunId);
            trigger.Parameters.AddWithValue(
                "$mode", command.Mode.ToString().ToLowerInvariant());
            trigger.Parameters.AddWithValue("$thread", draft.ThreadId);
            trigger.Parameters.AddWithValue("$line", draft.TriggerLineId);
            trigger.Parameters.AddWithValue("$target", command.Target.DeviceId);
            trigger.Parameters.AddWithValue("$payload", payloadHash);
            trigger.Parameters.AddWithValue("$created", now.ToString("O"));
            trigger.ExecuteNonQuery();
            AdvanceTopicTriggerEpoch(transaction);
            checkpoint?.Invoke(TopicRunBeginCheckpoint.TriggerPersisted);
        }

        using (var bind = conn.CreateCommand())
        {
            bind.Transaction = transaction;
            bind.CommandText = """
                UPDATE own_threads
                SET agent_execution_host_device_id = $device,
                    agent_execution_host_device_name = $deviceName,
                    agent_execution_host_device_platform = $platform,
                    execution_at = $executionAt,
                    execution_run_id = COALESCE(execution_run_id, $run),
                    last_activity_at = CASE
                        WHEN last_activity_at IS NULL
                             OR julianday($activity) > julianday(last_activity_at)
                        THEN $activity ELSE last_activity_at
                    END
                WHERE id = $thread
                  AND (agent_execution_host_device_id IS NULL OR agent_execution_host_device_id = $device);
                """;
            bind.Parameters.AddWithValue("$device", command.Target.DeviceId);
            bind.Parameters.AddWithValue(
                "$deviceName", (object?)command.Target.DeviceName ?? DBNull.Value);
            bind.Parameters.AddWithValue("$platform", command.Target.Platform);
            bind.Parameters.AddWithValue("$executionAt", draft.TriggerAt.ToString("O"));
            bind.Parameters.AddWithValue("$run", draft.RunId);
            bind.Parameters.AddWithValue("$activity", draft.TriggerAt.ToString("O"));
            bind.Parameters.AddWithValue("$thread", draft.ThreadId);
            if (bind.ExecuteNonQuery() != 1)
                return new TopicRunBeginResult(false, false, "topic_run_conflict");
        }
        checkpoint?.Invoke(TopicRunBeginCheckpoint.ThreadBound);

        using (var prompt = conn.CreateCommand())
        {
            prompt.Transaction = transaction;
            prompt.CommandText = """
                INSERT OR IGNORE INTO own_chat(
                    line_id, thread_id, role, text, reply_to_line_id, via, status, at,
                    internal, reasoning, sender_handle, model_id)
                VALUES($line, $thread, 'user', $text, NULL, '', '', $at, 0, NULL, $sender, NULL);
                """;
            prompt.Parameters.AddWithValue("$line", draft.TriggerLineId);
            prompt.Parameters.AddWithValue("$thread", draft.ThreadId);
            prompt.Parameters.AddWithValue("$text", draft.Prompt);
            prompt.Parameters.AddWithValue("$at", draft.TriggerAt.ToString("O"));
            prompt.Parameters.AddWithValue("$sender", draft.TriggerHandle);
            prompt.ExecuteNonQuery();
        }
        checkpoint?.Invoke(TopicRunBeginCheckpoint.PromptPersisted);

        TopicOutboxItem? persistedOutbox = null;
        if (isRemote)
        {
            using (var outbox = conn.CreateCommand())
            {
                outbox.Transaction = transaction;
                outbox.CommandText = """
                    INSERT INTO topic_outbox(
                        run_id, thread_id, trigger_line_id, target_device_id, request_json,
                        attachments_json, state, created_at, updated_at, last_error,
                        remote_stage, remote_stage_ordinal)
                    VALUES($run, $thread, $line, $device, $request, $attachments, $state,
                           $created, $updated, NULL, $stage, 0);
                    """;
                outbox.Parameters.AddWithValue("$run", draft.RunId);
                outbox.Parameters.AddWithValue("$thread", draft.ThreadId);
                outbox.Parameters.AddWithValue("$line", draft.TriggerLineId);
                outbox.Parameters.AddWithValue("$device", command.Target.DeviceId);
                outbox.Parameters.AddWithValue("$request", requestJson!);
                outbox.Parameters.AddWithValue("$attachments", attachmentsJson!);
                outbox.Parameters.AddWithValue("$state", TopicOutboxStates.Pending);
                outbox.Parameters.AddWithValue("$created", now.ToString("O"));
                outbox.Parameters.AddWithValue("$updated", now.ToString("O"));
                outbox.Parameters.AddWithValue("$stage", "sender_queued");
                outbox.ExecuteNonQuery();
            }
            checkpoint?.Invoke(TopicRunBeginCheckpoint.OutboxPersisted);

            using (var correlation = conn.CreateCommand())
            {
                correlation.Transaction = transaction;
                correlation.CommandText = """
                    INSERT INTO topic_run_correlations(
                        run_id, thread_id, target_device_id, trigger_line_id, created_at,
                        terminal_at, terminal_event_at)
                    VALUES($run, $thread, $device, $line, $created, NULL, NULL);
                    """;
                correlation.Parameters.AddWithValue("$run", draft.RunId);
                correlation.Parameters.AddWithValue("$thread", draft.ThreadId);
                correlation.Parameters.AddWithValue("$device", command.Target.DeviceId);
                correlation.Parameters.AddWithValue("$line", draft.TriggerLineId);
                correlation.Parameters.AddWithValue("$created", now.ToString("O"));
                correlation.ExecuteNonQuery();
            }
            checkpoint?.Invoke(TopicRunBeginCheckpoint.CorrelationPersisted);
            persistedOutbox = new TopicOutboxItem(
                draft.RunId,
                draft.ThreadId,
                draft.TriggerLineId,
                command.Target.DeviceId,
                command.Request!,
                attachments,
                TopicOutboxStates.Pending,
                now,
                now,
                RemoteStage: "sender_queued");
        }
        else
        {
            using var local = conn.CreateCommand();
            local.Transaction = transaction;
            local.CommandText = """
                INSERT INTO topic_local_runs(
                    run_id, thread_id, trigger_line_id, target_device_id, created_at, terminal_at)
                VALUES($run, $thread, $line, $device, $created, NULL);
                """;
            local.Parameters.AddWithValue("$run", draft.RunId);
            local.Parameters.AddWithValue("$thread", draft.ThreadId);
            local.Parameters.AddWithValue("$line", draft.TriggerLineId);
            local.Parameters.AddWithValue("$device", command.Target.DeviceId);
            local.Parameters.AddWithValue("$created", now.ToString("O"));
            local.ExecuteNonQuery();
            checkpoint?.Invoke(TopicRunBeginCheckpoint.LocalRunPersisted);
        }

        checkpoint?.Invoke(TopicRunBeginCheckpoint.BeforeCommit);
        transaction.Commit();
        return new TopicRunBeginResult(
            true,
            true,
            "created",
            persistedOutbox,
            draft.RunId,
            triggerId,
            draft);
    }

    private static TopicRunBeginCommand RebindTopicRun(
        TopicRunBeginCommand command,
        string authoritativeRunId)
    {
        var draft = command.Draft with { RunId = authoritativeRunId };
        var request = command.Request is null
            ? null
            : command.Request with { RunId = authoritativeRunId };
        return command with
        {
            Draft = draft,
            Request = request,
            InitialProjection = command.InitialProjection with { RunId = authoritativeRunId }
        };
    }

    private static bool TopicRunTriggerMatches(
        TopicRunTriggerItem trigger,
        TopicRunBeginCommand command,
        string payloadHash)
        => trigger.Mode == command.Mode
           && string.Equals(trigger.ThreadId, command.Draft.ThreadId, StringComparison.Ordinal)
           && string.Equals(
               trigger.TriggerLineId, command.Draft.TriggerLineId, StringComparison.Ordinal)
           && string.Equals(
               trigger.TargetDeviceId, command.Target.DeviceId, StringComparison.Ordinal)
           && (string.Equals(trigger.PayloadHash, payloadHash, StringComparison.Ordinal)
               || string.Equals(
                   trigger.PayloadHash, "legacy-unverifiable", StringComparison.Ordinal));

    private TopicRunTriggerItem? GetTopicRunTrigger(
        SqliteTransaction? transaction,
        string triggerId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT trigger_id, run_id, mode, thread_id, trigger_line_id, target_device_id,
                   payload_hash, created_at, terminal_at
            FROM topic_run_triggers
            WHERE trigger_id = $trigger;
            """;
        command.Parameters.AddWithValue("$trigger", triggerId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTopicRunTrigger(reader) : null;
    }

    private TopicRunTriggerItem? GetTopicRunTriggerByRunId(
        SqliteTransaction? transaction,
        string runId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT trigger_id, run_id, mode, thread_id, trigger_line_id, target_device_id,
                   payload_hash, created_at, terminal_at
            FROM topic_run_triggers
            WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTopicRunTrigger(reader) : null;
    }

    public TopicRunTriggerItem? GetTopicRunTrigger(string triggerId)
        => GetTopicRunTrigger(null, triggerId);

    public TopicRunTriggerItem? GetTopicRunTriggerByOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return GetTopicRunTrigger(
            null,
            TopicRunTriggerIdentity.For("", "", operationId));
    }

    public TopicRunTriggerItem? GetTopicRunTriggerByRunId(string runId)
        => GetTopicRunTriggerByRunId(null, runId);

    internal TopicRunTriggerObservation ObserveTopicRunTrigger(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        using var transaction = conn.BeginTransaction(deferred: true);
        var epoch = ReadTopicTriggerEpoch(transaction);
        var trigger = GetTopicRunTrigger(
            transaction,
            TopicRunTriggerIdentity.For("", "", operationId));
        var outbox = trigger is null
            ? null
            : GetTopicOutbox(transaction, trigger.RunId);
        transaction.Commit();
        return new TopicRunTriggerObservation(trigger, outbox, epoch);
    }

    internal long GetTopicTriggerEpoch()
        => ReadTopicTriggerEpoch(null);

    private long ReadTopicTriggerEpoch(SqliteTransaction? transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT v FROM meta WHERE k = 'topic_trigger_epoch';";
        return long.TryParse(
            command.ExecuteScalar()?.ToString(),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var epoch)
            ? epoch
            : 1;
    }

    private void AdvanceTopicTriggerEpoch(SqliteTransaction? transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO meta(k, v) VALUES('topic_trigger_epoch', '2')
            ON CONFLICT(k) DO UPDATE SET v = CAST(CAST(v AS INTEGER) + 1 AS TEXT);
            """;
        command.ExecuteNonQuery();
    }

    private void MigrateTopicRunTriggerLedger()
    {
        foreach (var outbox in ListTopicOutbox())
        {
            var draft = new TopicTurnDraft(
                outbox.RunId,
                outbox.ThreadId,
                outbox.TriggerLineId,
                outbox.Request.TriggerHandle,
                outbox.Request.TriggerText,
                outbox.Request.TriggerAt,
                outbox.Request.TurnMode,
                outbox.TargetDeviceId,
                outbox.Request.WidgetId,
                outbox.Request.WidgetContext,
                outbox.Attachments);
            var command = new TopicRunBeginCommand(
                draft,
                new AgentExecutionHost(outbox.TargetDeviceId, null, DevicePlatforms.Unknown),
                TopicRunBeginMode.Remote,
                new TopicRunUpdatePayload(
                    outbox.RunId,
                    outbox.ThreadId,
                    TopicRunPhase.Queued,
                    Timestamp: outbox.CreatedAt,
                    TriggerLineId: outbox.TriggerLineId),
                outbox.Request,
                outbox.Attachments);
            InsertMigratedTopicRunTrigger(
                TopicRunTriggerIdentity.For(outbox.ThreadId, outbox.TriggerLineId),
                outbox.RunId,
                TopicRunBeginMode.Remote,
                outbox.ThreadId,
                outbox.TriggerLineId,
                outbox.TargetDeviceId,
                TopicRunTriggerIdentity.PayloadHash(command),
                outbox.CreatedAt,
                null);
        }

        var legacy = new List<TopicRunTriggerItem>();
        using (var query = conn.CreateCommand())
        {
            query.CommandText = """
                SELECT run_id, 'remote', thread_id, trigger_line_id, target_device_id,
                       created_at, terminal_at
                FROM topic_run_correlations
                WHERE trigger_line_id IS NOT NULL
                UNION ALL
                SELECT run_id, 'local', thread_id, trigger_line_id, target_device_id,
                       created_at, terminal_at
                FROM topic_local_runs;
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var mode = string.Equals(reader.GetString(1), "remote", StringComparison.Ordinal)
                    ? TopicRunBeginMode.Remote
                    : TopicRunBeginMode.Local;
                var threadId = reader.GetString(2);
                var lineId = reader.GetString(3);
                legacy.Add(new TopicRunTriggerItem(
                    TopicRunTriggerIdentity.For(threadId, lineId),
                    reader.GetString(0),
                    mode,
                    threadId,
                    lineId,
                    reader.GetString(4),
                    "legacy-unverifiable",
                    DateTimeOffset.Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))));
            }
        }
        foreach (var item in legacy)
            InsertMigratedTopicRunTrigger(
                item.TriggerId,
                item.RunId,
                item.Mode,
                item.ThreadId,
                item.TriggerLineId,
                item.TargetDeviceId,
                item.PayloadHash,
                item.CreatedAt,
                item.TerminalAt);
    }

    private void InsertMigratedTopicRunTrigger(
        string triggerId,
        string runId,
        TopicRunBeginMode mode,
        string threadId,
        string triggerLineId,
        string targetDeviceId,
        string payloadHash,
        DateTimeOffset createdAt,
        DateTimeOffset? terminalAt)
    {
        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO topic_run_triggers(
                trigger_id, run_id, mode, thread_id, trigger_line_id, target_device_id,
                payload_hash, created_at, terminal_at)
            VALUES($trigger, $run, $mode, $thread, $line, $target, $payload, $created, $terminal);
            """;
        insert.Parameters.AddWithValue("$trigger", triggerId);
        insert.Parameters.AddWithValue("$run", runId);
        insert.Parameters.AddWithValue("$mode", mode.ToString().ToLowerInvariant());
        insert.Parameters.AddWithValue("$thread", threadId);
        insert.Parameters.AddWithValue("$line", triggerLineId);
        insert.Parameters.AddWithValue("$target", targetDeviceId);
        insert.Parameters.AddWithValue("$payload", payloadHash);
        insert.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        insert.Parameters.AddWithValue(
            "$terminal", terminalAt is null ? DBNull.Value : terminalAt.Value.ToString("O"));
        if (insert.ExecuteNonQuery() == 1)
            AdvanceTopicTriggerEpoch(null);
    }

    private TopicRunTriggerItem? ReadTopicRunTrigger(
        string column,
        string value,
        SqliteTransaction? transaction)
    {
        using var query = conn.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"""
            SELECT trigger_id, run_id, mode, thread_id, trigger_line_id,
                   target_device_id, payload_hash, created_at, terminal_at
            FROM topic_run_triggers
            WHERE {column} = $value
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$value", value);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        return new TopicRunTriggerItem(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<TopicRunBeginMode>(reader.GetString(2), ignoreCase: true),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static TopicRunTriggerItem ReadTopicRunTrigger(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<TopicRunBeginMode>(reader.GetString(2), ignoreCase: true),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));

    public LocalTopicRunItem? GetLocalTopicRun(string runId)
        => GetLocalTopicRun(null, runId);

    public void CompleteLocalTopicRun(string runId, DateTimeOffset terminalAt)
    {
        using var transaction = conn.BeginTransaction();
        using (var command = conn.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE topic_local_runs
                SET terminal_at = COALESCE(terminal_at, $terminal)
                WHERE run_id = $run;
                """;
            command.Parameters.AddWithValue("$terminal", terminalAt.ToString("O"));
            command.Parameters.AddWithValue("$run", runId);
            command.ExecuteNonQuery();
        }
        MarkTopicRunTriggerTerminal(transaction, runId, terminalAt);
        transaction.Commit();
    }

    private LocalTopicRunItem? GetLocalTopicRun(SqliteTransaction? transaction, string runId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, thread_id, trigger_line_id, target_device_id, created_at, terminal_at
            FROM topic_local_runs
            WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new LocalTopicRunItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)))
            : null;
    }

    private TopicRunCorrelationItem? GetTopicRunCorrelation(
        SqliteTransaction transaction,
        string runId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, thread_id, target_device_id, trigger_line_id, created_at,
                   terminal_at, terminal_event_at
            FROM topic_run_correlations
            WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new TopicRunCorrelationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)))
            : null;
    }

    private TopicOutboxItem? GetTopicOutbox(SqliteTransaction transaction, string runId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM topic_outbox WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTopicOutbox(reader) : null;
    }

    private bool TopicRunArtifactsExist(SqliteTransaction transaction, string runId)
        => GetTopicRunCorrelation(transaction, runId) is not null
           || GetTopicOutbox(transaction, runId) is not null;

    private static bool LocalTopicRunMatches(
        LocalTopicRunItem existing,
        TopicRunBeginCommand command)
        => string.Equals(existing.ThreadId, command.Draft.ThreadId, StringComparison.Ordinal)
           && string.Equals(
               existing.TriggerLineId, command.Draft.TriggerLineId, StringComparison.Ordinal)
           && string.Equals(
               existing.TargetDeviceId, command.Target.DeviceId, StringComparison.Ordinal);

    private static bool TopicRunCorrelationMatches(
        TopicRunCorrelationItem existing,
        TopicRunBeginCommand command)
        => string.Equals(existing.ThreadId, command.Draft.ThreadId, StringComparison.Ordinal)
           && string.Equals(
               existing.TriggerLineId, command.Draft.TriggerLineId, StringComparison.Ordinal)
           && string.Equals(
               existing.TargetDeviceId, command.Target.DeviceId, StringComparison.Ordinal);

    private static bool TopicOutboxMatches(
        TopicOutboxItem existing,
        TopicRunBeginCommand command,
        string attachmentsJson)
    {
        var proposedRequest = command.Request;
        return proposedRequest is not null
           && string.Equals(existing.ThreadId, command.Draft.ThreadId, StringComparison.Ordinal)
           && string.Equals(
               existing.TriggerLineId, command.Draft.TriggerLineId, StringComparison.Ordinal)
           && string.Equals(
               existing.TargetDeviceId, command.Target.DeviceId, StringComparison.Ordinal)
           && string.Equals(
               JsonSerializer.Serialize(existing.Request, JsonOpts),
               JsonSerializer.Serialize(
                   proposedRequest with
                   {
                       OriginScopeId = existing.Request.OriginScopeId
                   },
                   JsonOpts),
               StringComparison.Ordinal)
           && string.Equals(
               JsonSerializer.Serialize(existing.Attachments, JsonOpts),
               attachmentsJson,
               StringComparison.Ordinal);
    }

    private static ChatAttachment CloneAttachment(ChatAttachment attachment)
        => new(attachment.Name, attachment.MimeType, attachment.Data.ToArray());

    public void UpsertTopicOutbox(TopicOutboxItem item)
    {
        using var transaction = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO topic_outbox(
                run_id, thread_id, trigger_line_id, target_device_id, request_json,
                attachments_json, state, created_at, updated_at, last_error,
                remote_stage, remote_stage_ordinal, transport_attempt_ordinal)
            VALUES($run, $thread, $line, $device, $request, $attachments, $state, $created,
                   $updated, $error, $remoteStage, $remoteOrdinal, $transportOrdinal)
            ON CONFLICT(run_id) DO UPDATE SET
                request_json = excluded.request_json,
                attachments_json = excluded.attachments_json,
                state = CASE
                    WHEN excluded.remote_stage_ordinal >= topic_outbox.remote_stage_ordinal
                    THEN excluded.state ELSE topic_outbox.state
                END,
                updated_at = excluded.updated_at,
                last_error = excluded.last_error,
                remote_stage = CASE
                    WHEN excluded.remote_stage_ordinal >= topic_outbox.remote_stage_ordinal
                    THEN excluded.remote_stage ELSE topic_outbox.remote_stage
                END,
                remote_stage_ordinal = MAX(
                    topic_outbox.remote_stage_ordinal, excluded.remote_stage_ordinal),
                transport_attempt_ordinal = MAX(
                    topic_outbox.transport_attempt_ordinal,
                    excluded.transport_attempt_ordinal);
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
        cmd.Parameters.AddWithValue(
            "$remoteStage", (object?)item.RemoteStage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remoteOrdinal", item.RemoteStageOrdinal);
        cmd.Parameters.AddWithValue("$transportOrdinal", item.TransportAttemptOrdinal);
        cmd.ExecuteNonQuery();

        using var correlation = conn.CreateCommand();
        correlation.Transaction = transaction;
        correlation.CommandText = """
            INSERT INTO topic_run_correlations(
                run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at)
            VALUES($run, $thread, $device, $line, $created, NULL)
            ON CONFLICT(run_id) DO UPDATE SET
                thread_id = excluded.thread_id,
                target_device_id = excluded.target_device_id,
                trigger_line_id = excluded.trigger_line_id
            WHERE topic_run_correlations.terminal_at IS NULL;
            """;
        correlation.Parameters.AddWithValue("$run", item.RunId);
        correlation.Parameters.AddWithValue("$thread", item.ThreadId);
        correlation.Parameters.AddWithValue("$device", item.TargetDeviceId);
        correlation.Parameters.AddWithValue("$line", item.TriggerLineId);
        correlation.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        correlation.ExecuteNonQuery();
        EnsureTopicOutboxTrigger(transaction, item);
        transaction.Commit();
    }

    private void EnsureTopicOutboxTrigger(
        SqliteTransaction transaction,
        TopicOutboxItem item)
    {
        var draft = new TopicTurnDraft(
            item.RunId,
            item.ThreadId,
            item.TriggerLineId,
            item.Request.TriggerHandle,
            item.Request.TriggerText,
            item.Request.TriggerAt,
            item.Request.TurnMode,
            item.TargetDeviceId,
            item.Request.WidgetId,
            item.Request.WidgetContext,
            item.Attachments);
        var command = new TopicRunBeginCommand(
            draft,
            new AgentExecutionHost(item.TargetDeviceId, null, DevicePlatforms.Unknown),
            TopicRunBeginMode.Remote,
            new TopicRunUpdatePayload(
                item.RunId,
                item.ThreadId,
                TopicRunPhase.Queued,
                Timestamp: item.CreatedAt,
                TriggerLineId: item.TriggerLineId),
            item.Request,
            item.Attachments);
        var triggerId = TopicRunTriggerIdentity.For(item.ThreadId, item.TriggerLineId);
        var payloadHash = TopicRunTriggerIdentity.PayloadHash(command);
        var existing = GetTopicRunTrigger(transaction, triggerId);
        if (existing is not null)
        {
            if (!TopicRunTriggerMatches(existing, command, payloadHash)
                || !string.Equals(existing.RunId, item.RunId, StringComparison.Ordinal))
                throw new InvalidOperationException("trigger_identity_conflict");
            return;
        }
        if (GetTopicRunTriggerByRunId(transaction, item.RunId) is not null)
            throw new InvalidOperationException("run_id_conflict");
        using var insert = conn.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO topic_run_triggers(
                trigger_id, run_id, mode, thread_id, trigger_line_id, target_device_id,
                payload_hash, created_at, terminal_at)
            VALUES($trigger, $run, 'remote', $thread, $line, $target, $payload, $created, NULL);
            """;
        insert.Parameters.AddWithValue("$trigger", triggerId);
        insert.Parameters.AddWithValue("$run", item.RunId);
        insert.Parameters.AddWithValue("$thread", item.ThreadId);
        insert.Parameters.AddWithValue("$line", item.TriggerLineId);
        insert.Parameters.AddWithValue("$target", item.TargetDeviceId);
        insert.Parameters.AddWithValue("$payload", payloadHash);
        insert.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        if (insert.ExecuteNonQuery() == 1)
            AdvanceTopicTriggerEpoch(transaction);
    }

    bool ITopicRequestOutboxStore.SaveTopicOutbox(TopicOutboxItem item)
    {
        ExecuteDurableWrite(() => UpsertTopicOutbox(item));
        return GetTopicOutbox(item.RunId) is not null;
    }

    public TopicOutboxItem? GetTopicOutbox(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM topic_outbox WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTopicOutbox(reader) : null;
    }

    public TopicTransportAttempt? BeginTopicTransportAttempt(string runId)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        using (var update = conn.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE topic_outbox
                SET transport_attempt_ordinal = transport_attempt_ordinal + 1
                WHERE run_id = $run;
                """;
            update.Parameters.AddWithValue("$run", runId);
            if (update.ExecuteNonQuery() != 1) return null;
        }
        using var query = conn.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT trigger.trigger_id, outbox.run_id, outbox.transport_attempt_ordinal
            FROM topic_outbox AS outbox
            JOIN topic_run_triggers AS trigger ON trigger.run_id = outbox.run_id
            WHERE outbox.run_id = $run;
            """;
        query.Parameters.AddWithValue("$run", runId);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        var result = new TopicTransportAttempt(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
        transaction.Commit();
        return result;
    }

    internal RemoteTopicUpdatePersistenceResult ApplyRemoteTopicUpdate(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        ReceivedTopicControlItem? control = null,
        Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        if (control is not null
            && (!string.Equals(
                    control.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
                || !string.Equals(control.RunId, update.RunId, StringComparison.Ordinal)
                || !string.Equals(control.ThreadId, update.ThreadId, StringComparison.Ordinal)
                || !string.Equals(
                    control.ControlKind,
                    TopicControlProtocol.ControlPurpose(update),
                    StringComparison.Ordinal)
                || !string.Equals(
                    control.UpdateJson,
                    TopicRunProtocol.UpdateBody(update),
                    StringComparison.Ordinal)))
            return RemoteTopicUpdatePersistenceResult.IdentityConflict;
        using var transaction = conn.BeginTransaction(deferred: false);

        if (control is not null)
        {
            using var existingCommand = conn.CreateCommand();
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT envelope_id, source_device_id, run_id, thread_id, control_kind,
                       update_json, received_at
                FROM received_topic_controls
                WHERE envelope_id = $envelope;
                """;
            existingCommand.Parameters.AddWithValue("$envelope", control.EnvelopeId);
            using var reader = existingCommand.ExecuteReader();
            if (reader.Read())
            {
                var existing = ReadReceivedTopicControl(reader);
                return ReceivedTopicControlMatches(existing, control)
                    ? RemoteTopicUpdatePersistenceResult.Duplicate
                    : RemoteTopicUpdatePersistenceResult.IdentityConflict;
            }
        }

        string targetDeviceId;
        int currentOrdinal;
        bool hasOutbox;
        bool isTerminalCorrelation;
        string? triggerLineId;
        using (var correlation = conn.CreateCommand())
        {
            correlation.Transaction = transaction;
            correlation.CommandText = """
                SELECT correlation.target_device_id,
                       outbox.remote_stage_ordinal,
                       correlation.terminal_at,
                       correlation.trigger_line_id
                FROM topic_run_correlations AS correlation
                LEFT JOIN topic_outbox AS outbox
                  ON outbox.run_id = correlation.run_id
                 AND outbox.thread_id = correlation.thread_id
                WHERE correlation.run_id = $run
                  AND correlation.thread_id = $thread;
                """;
            correlation.Parameters.AddWithValue("$run", update.RunId);
            correlation.Parameters.AddWithValue("$thread", update.ThreadId);
            using var reader = correlation.ExecuteReader();
            if (!reader.Read())
                return RemoteTopicUpdatePersistenceResult.NotCorrelated;
            targetDeviceId = reader.GetString(0);
            hasOutbox = !reader.IsDBNull(1);
            currentOrdinal = hasOutbox ? reader.GetInt32(1) : TopicRemoteStage.Terminal;
            isTerminalCorrelation = !reader.IsDBNull(2);
            triggerLineId = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        if (!string.Equals(targetDeviceId, sourceDeviceId, StringComparison.Ordinal))
            return RemoteTopicUpdatePersistenceResult.NotCorrelated;
        if (!string.Equals(triggerLineId, update.TriggerLineId, StringComparison.Ordinal))
            return RemoteTopicUpdatePersistenceResult.NotCorrelated;

        if (isTerminalCorrelation || !hasOutbox)
        {
            if (control is null || !TopicControlProtocol.RequiresPersistenceReceipt(update))
                return RemoteTopicUpdatePersistenceResult.NotCorrelated;
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            BindReceivedTopicControlInsert(insert, control);
            if (insert.ExecuteNonQuery() != 1)
                return RemoteTopicUpdatePersistenceResult.IdentityConflict;
            beforeCommit?.Invoke();
            transaction.Commit();
            return RemoteTopicUpdatePersistenceResult.Ignored;
        }

        var nextOrdinal = TopicRemoteStage.Ordinal(update);
        var applied = nextOrdinal >= currentOrdinal;
        if (applied)
        {
            var terminal = TopicControlProtocol.IsTerminal(update);
            using var threadUpdate = conn.CreateCommand();
            threadUpdate.Transaction = transaction;
            threadUpdate.CommandText = terminal
                ? """
                  UPDATE own_threads
                  SET execution_run_id = NULL,
                      last_activity_at = CASE
                          WHEN last_activity_at IS NULL
                               OR julianday($activity) > julianday(last_activity_at)
                          THEN $activity ELSE last_activity_at
                      END
                  WHERE id = $thread
                       AND (execution_run_id = $run OR execution_run_id IS NULL)
                    AND agent_execution_host_device_id = $source;
                  """
                : """
                  UPDATE own_threads
                  SET execution_at = COALESCE(execution_at, $activity),
                      last_activity_at = CASE
                          WHEN last_activity_at IS NULL
                               OR julianday($activity) > julianday(last_activity_at)
                          THEN $activity ELSE last_activity_at
                      END
                  WHERE id = $thread
                    AND execution_run_id = $run
                    AND agent_execution_host_device_id = $source;
                  """;
            threadUpdate.Parameters.AddWithValue("$thread", update.ThreadId);
            threadUpdate.Parameters.AddWithValue("$run", update.RunId);
            threadUpdate.Parameters.AddWithValue("$source", sourceDeviceId);
            threadUpdate.Parameters.AddWithValue(
                "$activity", update.Timestamp.UtcDateTime.ToString("O"));
            if (threadUpdate.ExecuteNonQuery() != 1)
                return RemoteTopicUpdatePersistenceResult.NotCorrelated;

            using var outboxUpdate = conn.CreateCommand();
            outboxUpdate.Transaction = transaction;
            if (terminal)
            {
                outboxUpdate.CommandText =
                    "DELETE FROM topic_outbox WHERE run_id = $run AND thread_id = $thread;";
            }
            else
            {
                outboxUpdate.CommandText = """
                    UPDATE topic_outbox
                    SET state = $state,
                        updated_at = $updated,
                        last_error = NULL,
                        remote_stage = $stage,
                        remote_stage_ordinal = $ordinal
                    WHERE run_id = $run
                      AND thread_id = $thread
                      AND remote_stage_ordinal <= $ordinal;
                    """;
                outboxUpdate.Parameters.AddWithValue(
                    "$state", TopicControlProtocol.IsAcceptance(update)
                        ? TopicOutboxStates.DeviceAccepted
                        : update.Phase == TopicRunPhase.Queued
                            ? TopicOutboxStates.DeviceQueued
                            : TopicOutboxStates.Running);
                outboxUpdate.Parameters.AddWithValue(
                    "$updated", timeProvider.GetUtcNow().ToString("O"));
                outboxUpdate.Parameters.AddWithValue("$stage", TopicRemoteStage.Name(update));
                outboxUpdate.Parameters.AddWithValue("$ordinal", nextOrdinal);
            }
            outboxUpdate.Parameters.AddWithValue("$run", update.RunId);
            outboxUpdate.Parameters.AddWithValue("$thread", update.ThreadId);
            if (outboxUpdate.ExecuteNonQuery() != 1)
                return RemoteTopicUpdatePersistenceResult.NotCorrelated;
            if (terminal)
            {
                MarkTopicRunCorrelationTerminal(
                    transaction,
                    update.RunId,
                    timeProvider.GetUtcNow(),
                    update.Timestamp);
                if (update.Result is { } terminalResult)
                {
                    Protocol9DomainTables.AppendOwnChat(
                        conn,
                        transaction,
                        update.ThreadId,
                        TerminalResultLine(update, terminalResult));
                }
            }
        }

        if (control is not null)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            BindReceivedTopicControlInsert(insert, control);
            if (insert.ExecuteNonQuery() != 1)
                return RemoteTopicUpdatePersistenceResult.IdentityConflict;
        }

        beforeCommit?.Invoke();
        transaction.Commit();
        return applied
            ? RemoteTopicUpdatePersistenceResult.Applied
            : RemoteTopicUpdatePersistenceResult.Ignored;
    }

    private static ChatLine TerminalResultLine(
        TopicRunUpdatePayload update,
        TopicRunResultPayload result)
        => new()
        {
            Id = result.LineId,
            Role = "assistant",
            Text = result.Text,
            ReplyToLineId = update.TriggerLineId,
            At = result.At,
            ModelId = result.ModelId,
            Reasoning = result.Reasoning
        };

    public IReadOnlyList<TopicOutboxItem> ListTopicOutbox()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM topic_outbox ORDER BY created_at, run_id;";
        using var reader = cmd.ExecuteReader();
        var result = new List<TopicOutboxItem>();
        while (reader.Read()) result.Add(ReadTopicOutbox(reader));
        return result;
    }

    public TopicSendOutcomePersistenceResult ApplyTopicRequestSendOutcome(
        string runId,
        string state,
        string? error = null)
    {
        if (state is not TopicOutboxStates.Pending
            and not TopicOutboxStates.RelayQueued
            and not TopicOutboxStates.Failed)
            throw new ArgumentOutOfRangeException(nameof(state));
        using var transaction = conn.BeginTransaction(deferred: false);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE topic_outbox
            SET state = $state, updated_at = $updated, last_error = $error
            WHERE run_id = $run
              AND remote_stage_ordinal < $accepted
              AND state = $pending;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$pending", TopicOutboxStates.Pending);
        cmd.Parameters.AddWithValue("$accepted", TopicRemoteStage.Accepted);
        cmd.Parameters.AddWithValue("$updated", timeProvider.GetUtcNow().ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() == 1)
        {
            transaction.Commit();
            return TopicSendOutcomePersistenceResult.Applied;
        }

        using var exists = conn.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM topic_outbox WHERE run_id = $run);";
        exists.Parameters.AddWithValue("$run", runId);
        var found = Convert.ToInt64(exists.ExecuteScalar()) == 1;
        transaction.Commit();
        return found
            ? TopicSendOutcomePersistenceResult.Ignored
            : TopicSendOutcomePersistenceResult.NotFound;
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
        cmd.Parameters.AddWithValue("$updated", timeProvider.GetUtcNow().ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTopicOutbox(string runId)
    {
        using var transaction = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM topic_outbox WHERE run_id = $run;";
            cmd.Parameters.AddWithValue("$run", runId);
            var outboxRowsRemoved = cmd.ExecuteNonQuery();
            if (outboxRowsRemoved > 0)
                AdvanceTopicTriggerEpoch(transaction);
        }
        using (var correlation = conn.CreateCommand())
        {
            correlation.Transaction = transaction;
            correlation.CommandText = """
                DELETE FROM topic_run_correlations
                WHERE run_id = $run AND terminal_at IS NULL;
                """;
            correlation.Parameters.AddWithValue("$run", runId);
            correlation.ExecuteNonQuery();
        }
        MarkTopicRunTriggerTerminal(transaction, runId, timeProvider.GetUtcNow());
        transaction.Commit();
    }

    public void CompleteTopicOutbox(string runId, DateTimeOffset terminalAt)
    {
        using var transaction = conn.BeginTransaction();
        using var delete = conn.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM topic_outbox WHERE run_id = $run;";
        delete.Parameters.AddWithValue("$run", runId);
        delete.ExecuteNonQuery();
        MarkTopicRunCorrelationTerminal(
            transaction, runId, timeProvider.GetUtcNow(), terminalAt);
        transaction.Commit();
    }

    public TopicRunCorrelationItem? GetTopicRunCorrelation(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at,
                   terminal_event_at
            FROM topic_run_correlations
            WHERE run_id = $run;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new TopicRunCorrelationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)))
            : null;
    }

    public bool TryBindLegacyTopicRunCorrelation(
        string runId,
        string threadId,
        string sourceDeviceId,
        string triggerLineId)
    {
        if (!TopicRunProtocol.IsValidIdentifier(runId)
            || !TopicRunProtocol.IsValidIdentifier(threadId)
            || !TopicRunProtocol.IsValidIdentifier(sourceDeviceId)
            || !TopicRunProtocol.IsValidIdentifier(triggerLineId))
            return false;

        using var transaction = conn.BeginTransaction(deferred: false);
        using var bind = conn.CreateCommand();
        bind.Transaction = transaction;
        bind.CommandText = """
            UPDATE topic_run_correlations AS correlation
            SET trigger_line_id = $trigger,
                trigger_identity_state = 'strict'
            WHERE correlation.run_id = $run
              AND correlation.thread_id = $thread
              AND correlation.target_device_id = $source
              AND correlation.trigger_line_id IS NULL
              AND correlation.trigger_identity_state = 'legacy-active-null'
              AND correlation.terminal_at IS NULL
              AND (
                  EXISTS(
                      SELECT 1 FROM topic_outbox AS outbox
                      WHERE outbox.run_id = correlation.run_id
                        AND outbox.thread_id = correlation.thread_id
                        AND outbox.target_device_id = correlation.target_device_id
                        AND outbox.state NOT IN ('expired', 'dead_letter', 'failed'))
                  OR EXISTS(
                      SELECT 1 FROM topic_local_runs AS local
                      WHERE local.run_id = correlation.run_id
                        AND local.thread_id = correlation.thread_id
                        AND local.target_device_id = correlation.target_device_id
                        AND local.terminal_at IS NULL)
                  OR EXISTS(
                      SELECT 1 FROM own_threads AS thread
                      WHERE thread.id = correlation.thread_id
                        AND thread.execution_run_id = correlation.run_id
                        AND thread.agent_execution_host_device_id = correlation.target_device_id));
            """;
        bind.Parameters.AddWithValue("$run", runId);
        bind.Parameters.AddWithValue("$thread", threadId);
        bind.Parameters.AddWithValue("$source", sourceDeviceId);
        bind.Parameters.AddWithValue("$trigger", triggerLineId);
        var changed = bind.ExecuteNonQuery() == 1;
        if (changed)
        {
            using var diagnostics = conn.CreateCommand();
            diagnostics.Transaction = transaction;
            diagnostics.CommandText = """
                INSERT INTO meta(k, v) VALUES('topic_run_trigger_legacy_bind_count', '1')
                ON CONFLICT(k) DO UPDATE
                SET v = CAST(CAST(meta.v AS INTEGER) + 1 AS TEXT);
                INSERT INTO meta(k, v) VALUES('topic_run_trigger_last_bound_hash', $hash)
                ON CONFLICT(k) DO UPDATE SET v = excluded.v;
                """;
            diagnostics.Parameters.AddWithValue(
                "$hash", Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(runId))).ToLowerInvariant());
            diagnostics.ExecuteNonQuery();
        }
        transaction.Commit();
        return changed;
    }

    public TopicRunCorrelationItem? FindTopicRunCorrelation(
        string threadId,
        string triggerLineId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at,
                   terminal_event_at
            FROM topic_run_correlations
            WHERE thread_id = $thread AND trigger_line_id = $trigger
            ORDER BY CASE WHEN terminal_at IS NULL THEN 0 ELSE 1 END, created_at DESC, run_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$thread", threadId);
        cmd.Parameters.AddWithValue("$trigger", triggerLineId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new TopicRunCorrelationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)))
            : null;
    }

    public bool HasTopicRunCorrelationForThread(string threadId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM topic_run_correlations
                WHERE thread_id = $thread);
            """;
        cmd.Parameters.AddWithValue("$thread", threadId);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<TopicRunCorrelationItem> ListActiveTopicRunCorrelations()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at,
                   terminal_event_at
            FROM topic_run_correlations
            WHERE terminal_at IS NULL
            ORDER BY created_at, run_id;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<TopicRunCorrelationItem>();
        while (reader.Read())
        {
            result.Add(new TopicRunCorrelationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))));
        }
        return result;
    }

    public IReadOnlyList<TopicRunCorrelationItem> ListRetainedTopicRunCorrelations()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at,
                   terminal_event_at
            FROM topic_run_correlations
            ORDER BY created_at, run_id;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<TopicRunCorrelationItem>();
        while (reader.Read())
        {
            result.Add(new TopicRunCorrelationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))));
        }
        return result;
    }

    public int PruneTopicRunCorrelations(DateTimeOffset localNow)
    {
        var terminalBefore = localNow - TopicTransportPolicy.TerminalCorrelationRetention;
        using var transaction = conn.BeginTransaction();
        int removed;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM topic_run_correlations
                WHERE terminal_at IS NOT NULL
                  AND julianday(terminal_at) < julianday($before);
                """;
            cmd.Parameters.AddWithValue("$before", terminalBefore.ToString("O"));
            removed = cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM topic_run_triggers
                WHERE terminal_at IS NOT NULL
                  AND julianday(terminal_at) < julianday($before);
                """;
            cmd.Parameters.AddWithValue(
                "$before",
                (localNow - TopicTransportPolicy.TriggerLedgerRetention).ToString("O"));
            var triggerRowsRemoved = cmd.ExecuteNonQuery();
            if (triggerRowsRemoved > 0)
                AdvanceTopicTriggerEpoch(transaction);
        }
        transaction.Commit();
        return removed;
    }

    public TopicControlReceiptPersistenceResult ApplyTopicControlReceipt(
        TopicRunUpdatePayload receipt,
        string sourceDeviceId,
        string acknowledgedEnvelopeId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgedEnvelopeId);
        if (!TopicControlProtocol.IsReceipt(receipt))
            return TopicControlReceiptPersistenceResult.IdentityConflict;

        using var transaction = conn.BeginTransaction(deferred: false);
        string persistedSource;
        TopicRunRequestPayload request;
        using (var correlation = conn.CreateCommand())
        {
            correlation.Transaction = transaction;
            correlation.CommandText = """
                SELECT source_device_id, request_json
                FROM inbound_topic_runs
                WHERE run_id = $run;
                """;
            correlation.Parameters.AddWithValue("$run", receipt.RunId);
            using var reader = correlation.ExecuteReader();
            if (!reader.Read())
                return TopicControlReceiptPersistenceResult.NotCorrelated;
            persistedSource = reader.GetString(0);
            request = JsonSerializer.Deserialize<TopicRunRequestPayload>(
                reader.GetString(1), JsonOpts)!;
        }

        var validShape =
            string.Equals(persistedSource, sourceDeviceId, StringComparison.Ordinal)
            && string.Equals(request.ThreadId, receipt.ThreadId, StringComparison.Ordinal)
            && (string.Equals(
                    receipt.Status,
                    TopicControlProtocol.AcceptanceReceiptStatus,
                    StringComparison.Ordinal)
                && receipt.Phase == TopicRunPhase.Queued
                || string.Equals(
                    receipt.Status,
                    TopicControlProtocol.TerminalReceiptStatus,
                    StringComparison.Ordinal)
                && TopicControlProtocol.IsTerminal(receipt));
        if (!validShape)
            return TopicControlReceiptPersistenceResult.IdentityConflict;

        using var delete = conn.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM device_envelope_outbox
            WHERE envelope_id = $envelope
              AND target_device_id = $source
              AND kind = $kind;
            """;
        delete.Parameters.AddWithValue("$envelope", acknowledgedEnvelopeId);
        delete.Parameters.AddWithValue("$source", sourceDeviceId);
        delete.Parameters.AddWithValue("$kind", MeshKinds.TopicRunUpdate);
        var deleted = delete.ExecuteNonQuery() == 1;
        transaction.Commit();
        return deleted
            ? TopicControlReceiptPersistenceResult.Applied
            : TopicControlReceiptPersistenceResult.Duplicate;
    }

    private void MarkTopicRunCorrelationTerminal(
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset terminalObservedAt,
        DateTimeOffset? terminalEventAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE topic_run_correlations
            SET terminal_at = COALESCE(terminal_at, $observed),
                terminal_event_at = COALESCE(terminal_event_at, $event)
            WHERE run_id = $run;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$observed", terminalObservedAt.ToString("O"));
        cmd.Parameters.AddWithValue(
            "$event",
            terminalEventAt is null
                ? DBNull.Value
                : terminalEventAt.Value.ToString("O"));
        cmd.ExecuteNonQuery();
        MarkTopicRunTriggerTerminal(transaction, runId, terminalObservedAt);
    }

    private void MarkTopicRunTriggerTerminal(
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset terminalAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE topic_run_triggers
            SET terminal_at = COALESCE(terminal_at, $terminal)
            WHERE run_id = $run
              AND terminal_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$terminal", terminalAt.ToString("O"));
        if (cmd.ExecuteNonQuery() == 1)
            AdvanceTopicTriggerEpoch(transaction);
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
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                reader.IsDBNull(reader.GetOrdinal("request_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("request_id")),
                reader.IsDBNull(reader.GetOrdinal("origin_scope_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("origin_scope_id")))
            : null;
    }

    public bool TryAddInboundTopicCancellation(InboundTopicCancellationItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO inbound_topic_cancellations(
                run_id, source_device_id, thread_id, request_id, origin_scope_id,
                terminal_update_json, created_at)
            VALUES($run, $source, $thread, $request, $origin, $terminal, $created);
            """;
        cmd.Parameters.AddWithValue("$run", item.RunId);
        cmd.Parameters.AddWithValue("$source", item.SourceDeviceId);
        cmd.Parameters.AddWithValue("$thread", item.ThreadId);
        cmd.Parameters.AddWithValue("$request", (object?)item.RequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origin", (object?)item.OriginScopeId ?? DBNull.Value);
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

    public bool TryAddInboundTopicRunAndQueueAcceptance(
        InboundTopicRunItem item,
        DeviceEnvelopeOutboxItem acceptance)
    {
        using var transaction = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO inbound_topic_runs(
                run_id, source_device_id, request_json, state, accepted_at, updated_at,
                terminal_update_json, queue_sequence)
            SELECT $run, $source, $request, $state, $accepted, $updated, $terminal,
                   COALESCE(MAX(queue_sequence), 0) + 1
            FROM inbound_topic_runs;
            """;
        insert.Parameters.AddWithValue("$run", item.RunId);
        insert.Parameters.AddWithValue("$source", item.SourceDeviceId);
        insert.Parameters.AddWithValue("$request", JsonSerializer.Serialize(item.Request, JsonOpts));
        insert.Parameters.AddWithValue("$state", item.State);
        insert.Parameters.AddWithValue("$accepted", item.AcceptedAt.ToString("O"));
        insert.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        insert.Parameters.AddWithValue(
            "$terminal", (object?)item.TerminalUpdateJson ?? DBNull.Value);
        var inserted = insert.ExecuteNonQuery() == 1;
        if (inserted)
        {
            using var queue = conn.CreateCommand();
            queue.Transaction = transaction;
            BindDeviceEnvelopeInsert(queue, acceptance);
            queue.ExecuteNonQuery();
        }
        transaction.Commit();
        return inserted;
    }

    bool ITopicDurabilityStore.TryAcceptInboundTopicRunAndQueueAcceptance(
        InboundTopicRunItem item,
        DeviceEnvelopeOutboxItem acceptance)
        => ExecuteDurableWrite(
            () => TryAddInboundTopicRunAndQueueAcceptance(item, acceptance));

    RemoteTopicUpdatePersistenceResult ITopicDurabilityStore.TryApplyReceivedTopicControl(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        ReceivedTopicControlItem control)
        => ExecuteDurableWrite(
            () => ApplyRemoteTopicUpdate(update, sourceDeviceId, control));

    RemoteTopicUpdatePersistenceResult ITopicDurabilityStore.ApplyRemoteTopicUpdate(
        TopicRunUpdatePayload update,
        string sourceDeviceId)
        => ExecuteDurableWrite(
            () => ApplyRemoteTopicUpdate(update, sourceDeviceId));

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
        cmd.Parameters.AddWithValue("$updated", timeProvider.GetUtcNow().ToString("O"));
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
        cmd.Parameters.AddWithValue("$updated", timeProvider.GetUtcNow().ToString("O"));
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
        update.Parameters.AddWithValue("$updated", timeProvider.GetUtcNow().ToString("O"));
        update.Parameters.AddWithValue("$terminal", TopicRunProtocol.UpdateBody(terminalUpdate));
        var wonTerminal = update.ExecuteNonQuery() == 1;
        if (wonTerminal)
        {
            using var queue = conn.CreateCommand();
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT OR IGNORE INTO device_envelope_outbox(
                    envelope_id, target_device_id, kind, plaintext, push_hint, created_at,
                    state, last_attempt_at, last_error)
                VALUES($id, $device, $kind, $plaintext, $push, $created,
                       $state, $attempted, $error);
                """;
            queue.Parameters.AddWithValue("$id", outbox.EnvelopeId);
            queue.Parameters.AddWithValue("$device", outbox.TargetDeviceId);
            queue.Parameters.AddWithValue("$kind", outbox.Kind);
            queue.Parameters.AddWithValue("$plaintext", outbox.Plaintext);
            queue.Parameters.AddWithValue("$push", (object?)outbox.PushHint ?? DBNull.Value);
            queue.Parameters.AddWithValue("$created", outbox.CreatedAt.ToString("O"));
            queue.Parameters.AddWithValue("$state", outbox.State);
            queue.Parameters.AddWithValue(
                "$attempted",
                outbox.LastAttemptAt is null
                    ? DBNull.Value
                    : outbox.LastAttemptAt.Value.ToString("O"));
            queue.Parameters.AddWithValue("$error", (object?)outbox.LastError ?? DBNull.Value);
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
        BindDeviceEnvelopeInsert(cmd, item);
        cmd.ExecuteNonQuery();
    }

    public void ReplaceDeviceEnvelopeOutboxForTargetAndKind(
        DeviceEnvelopeOutboxItem item,
        Func<DeviceEnvelopeOutboxItem, bool>? shouldReplaceExisting = null)
    {
        using var transaction = conn.BeginTransaction();
        if (shouldReplaceExisting is not null)
        {
            var preserveExisting = false;
            using (var select = conn.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT * FROM device_envelope_outbox
                    WHERE target_device_id = $device AND kind = $kind;
                    """;
                select.Parameters.AddWithValue("$device", item.TargetDeviceId);
                select.Parameters.AddWithValue("$kind", item.Kind);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (shouldReplaceExisting(ReadDeviceEnvelopeOutbox(reader))) continue;
                    preserveExisting = true;
                    break;
                }
            }
            if (preserveExisting)
            {
                transaction.Commit();
                return;
            }
        }
        using (var remove = conn.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM device_envelope_outbox
                WHERE target_device_id = $device AND kind = $kind;
                """;
            remove.Parameters.AddWithValue("$device", item.TargetDeviceId);
            remove.Parameters.AddWithValue("$kind", item.Kind);
            remove.ExecuteNonQuery();
        }
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO device_envelope_outbox(
                    envelope_id, target_device_id, kind, plaintext, push_hint, created_at)
                VALUES($id, $device, $kind, $plaintext, $push, $created);
                """;
            insert.Parameters.AddWithValue("$id", item.EnvelopeId);
            insert.Parameters.AddWithValue("$device", item.TargetDeviceId);
            insert.Parameters.AddWithValue("$kind", item.Kind);
            insert.Parameters.AddWithValue("$plaintext", item.Plaintext);
            insert.Parameters.AddWithValue("$push", (object?)item.PushHint ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    private static bool TopicReceiptOutboxSemanticallyMatches(
        DeviceEnvelopeOutboxItem existing,
        DeviceEnvelopeOutboxItem candidate)
    {
        if (!string.Equals(existing.EnvelopeId, candidate.EnvelopeId, StringComparison.Ordinal)
            || !string.Equals(existing.TargetDeviceId, candidate.TargetDeviceId, StringComparison.Ordinal)
            || !string.Equals(existing.Kind, candidate.Kind, StringComparison.Ordinal)
            || !TopicRunProtocol.TryParseUpdate(existing.Plaintext, out var left)
            || !TopicRunProtocol.TryParseUpdate(candidate.Plaintext, out var right)
            || !TopicControlProtocol.IsReceipt(left)
            || !TopicControlProtocol.IsReceipt(right))
            return false;

        return string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
               && string.Equals(left.ThreadId, right.ThreadId, StringComparison.Ordinal)
               && left.Phase == right.Phase
               && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
               && string.Equals(left.TriggerLineId, right.TriggerLineId, StringComparison.Ordinal)
               && string.Equals(
                   TopicControlProtocol.AcknowledgedPurpose(left),
                   TopicControlProtocol.AcknowledgedPurpose(right),
                   StringComparison.Ordinal);
    }

    public IReadOnlyList<DeviceEnvelopeOutboxItem> ListDeviceEnvelopeOutbox()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM device_envelope_outbox ORDER BY created_at, envelope_id;";
        using var reader = cmd.ExecuteReader();
        var result = new List<DeviceEnvelopeOutboxItem>();
        while (reader.Read())
        {
            result.Add(ReadDeviceEnvelopeOutbox(reader));
        }
        return result;
    }

    public void SetDeviceEnvelopeOutboxAttempt(
        string envelopeId,
        string state,
        DateTimeOffset attemptedAt,
        string? error = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE device_envelope_outbox
            SET state = CASE
                    WHEN state = $deadLetter THEN state
                    ELSE $state
                END,
                last_attempt_at = CASE
                    WHEN state = $deadLetter THEN last_attempt_at
                    ELSE $attempted
                END,
                last_error = CASE
                    WHEN state = $deadLetter THEN last_error
                    ELSE $error
                END
            WHERE envelope_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", envelopeId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$deadLetter", TopicOutboxStates.DeadLetter);
        cmd.Parameters.AddWithValue("$attempted", attemptedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool TryRecoverDeadLetteredDeviceEnvelope(
        string envelopeId,
        DateTimeOffset recoveredAt,
        int maximumRecoveryCount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE device_envelope_outbox
            SET state = $pending,
                last_attempt_at = NULL,
                last_error = $reason,
                recovery_count = recovery_count + 1,
                recovery_started_at = $recovered
            WHERE envelope_id = $id
              AND state = $deadLetter
              AND recovery_count < $maximum;
            """;
        cmd.Parameters.AddWithValue("$id", envelopeId);
        cmd.Parameters.AddWithValue("$pending", TopicOutboxStates.Pending);
        cmd.Parameters.AddWithValue("$deadLetter", TopicOutboxStates.DeadLetter);
        cmd.Parameters.AddWithValue("$reason", "dead_letter_recovery_requested");
        cmd.Parameters.AddWithValue("$recovered", recoveredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$maximum", maximumRecoveryCount);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void DeleteDeviceEnvelopeOutbox(string envelopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM device_envelope_outbox WHERE envelope_id = $id;";
        cmd.Parameters.AddWithValue("$id", envelopeId);
        cmd.ExecuteNonQuery();
    }

    bool ITopicControlOutboxStore.SetDeviceEnvelopeOutboxAttempt(
        string envelopeId,
        string outboxState,
        DateTimeOffset attemptedAt,
        string? error)
    {
        ExecuteDurableWrite(() =>
            SetDeviceEnvelopeOutboxAttempt(
                envelopeId, outboxState, attemptedAt, error));
        return GetDeviceEnvelopeOutbox(envelopeId) is not null;
    }

    bool ITopicControlOutboxStore.DeleteDeviceEnvelopeOutbox(string envelopeId)
    {
        ExecuteDurableWrite(() => DeleteDeviceEnvelopeOutbox(envelopeId));
        return GetDeviceEnvelopeOutbox(envelopeId) is null;
    }

    bool ITopicControlOutboxStore.TryRecoverDeadLetteredDeviceEnvelope(
        string envelopeId,
        DateTimeOffset recoveredAt,
        int maximumRecoveryCount)
    {
        var recovered = ExecuteDurableWrite(() =>
            TryRecoverDeadLetteredDeviceEnvelope(
                envelopeId, recoveredAt, maximumRecoveryCount));
        return recovered;
    }

    public bool TryAddReceivedTopicControl(ReceivedTopicControlItem item)
    {
        using var cmd = conn.CreateCommand();
        BindReceivedTopicControlInsert(cmd, item);
        return cmd.ExecuteNonQuery() == 1;
    }

    public ReceivedTopicControlItem? GetReceivedTopicControl(string envelopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT envelope_id, source_device_id, run_id, thread_id, control_kind,
                   update_json, received_at
            FROM received_topic_controls
            WHERE envelope_id = $envelope;
            """;
        cmd.Parameters.AddWithValue("$envelope", envelopeId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadReceivedTopicControl(reader) : null;
    }

    public IReadOnlyList<ReceivedTopicControlItem> ListReceivedTopicControls()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT envelope_id, source_device_id, run_id, thread_id, control_kind,
                   update_json, received_at
            FROM received_topic_controls
            ORDER BY received_at, envelope_id;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<ReceivedTopicControlItem>();
        while (reader.Read()) result.Add(ReadReceivedTopicControl(reader));
        return result;
    }

    public int PruneReceivedTopicControls(DateTimeOffset receivedBefore)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM received_topic_controls WHERE received_at < $before;";
        cmd.Parameters.AddWithValue("$before", receivedBefore.ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    private static void BindDeviceEnvelopeInsert(
        SqliteCommand cmd,
        DeviceEnvelopeOutboxItem item)
    {
        cmd.CommandText = """
            INSERT OR IGNORE INTO device_envelope_outbox(
                envelope_id, target_device_id, kind, plaintext, push_hint, created_at,
                state, last_attempt_at, last_error, recovery_count, recovery_started_at)
            VALUES($id, $device, $kind, $plaintext, $push, $created,
                   $state, $attempted, $error, $recoveryCount, $recoveryStarted);
            """;
        cmd.Parameters.AddWithValue("$id", item.EnvelopeId);
        cmd.Parameters.AddWithValue("$device", item.TargetDeviceId);
        cmd.Parameters.AddWithValue("$kind", item.Kind);
        cmd.Parameters.AddWithValue("$plaintext", item.Plaintext);
        cmd.Parameters.AddWithValue("$push", (object?)item.PushHint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$state", item.State);
        cmd.Parameters.AddWithValue(
            "$attempted",
            item.LastAttemptAt is null
                ? DBNull.Value
                : item.LastAttemptAt.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)item.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$recoveryCount", item.RecoveryCount);
        cmd.Parameters.AddWithValue(
            "$recoveryStarted",
            item.RecoveryStartedAt is null
                ? DBNull.Value
                : item.RecoveryStartedAt.Value.ToString("O"));
    }

    private static ReceivedTopicControlItem ReadReceivedTopicControl(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)));

    private static void BindReceivedTopicControlInsert(
        SqliteCommand command,
        ReceivedTopicControlItem item)
    {
        command.CommandText = """
            INSERT OR IGNORE INTO received_topic_controls(
                envelope_id, source_device_id, run_id, thread_id, control_kind,
                update_json, received_at)
            VALUES($envelope, $source, $run, $thread, $kind, $update, $received);
            """;
        command.Parameters.AddWithValue("$envelope", item.EnvelopeId);
        command.Parameters.AddWithValue("$source", item.SourceDeviceId);
        command.Parameters.AddWithValue("$run", item.RunId);
        command.Parameters.AddWithValue("$thread", item.ThreadId);
        command.Parameters.AddWithValue("$kind", item.ControlKind);
        command.Parameters.AddWithValue("$update", item.UpdateJson);
        command.Parameters.AddWithValue("$received", item.ReceivedAt.ToString("O"));
    }

    private static bool ReceivedTopicControlMatches(
        ReceivedTopicControlItem left,
        ReceivedTopicControlItem right)
        => string.Equals(left.EnvelopeId, right.EnvelopeId, StringComparison.Ordinal)
           && string.Equals(left.SourceDeviceId, right.SourceDeviceId, StringComparison.Ordinal)
           && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
           && string.Equals(left.ThreadId, right.ThreadId, StringComparison.Ordinal)
           && string.Equals(left.ControlKind, right.ControlKind, StringComparison.Ordinal)
           && string.Equals(left.UpdateJson, right.UpdateJson, StringComparison.Ordinal);

    private static DeviceEnvelopeOutboxItem ReadDeviceEnvelopeOutbox(
        SqliteDataReader reader)
        => new(
            reader.GetString(reader.GetOrdinal("envelope_id")),
            reader.GetString(reader.GetOrdinal("target_device_id")),
            reader.GetString(reader.GetOrdinal("kind")),
            reader.GetString(reader.GetOrdinal("plaintext")),
            reader.IsDBNull(reader.GetOrdinal("push_hint"))
                ? null
                : reader.GetString(reader.GetOrdinal("push_hint")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            reader.GetString(reader.GetOrdinal("state")),
            reader.IsDBNull(reader.GetOrdinal("last_attempt_at"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_attempt_at"))),
            reader.IsDBNull(reader.GetOrdinal("last_error"))
                ? null
                : reader.GetString(reader.GetOrdinal("last_error")),
            reader.GetInt32(reader.GetOrdinal("recovery_count")),
            reader.IsDBNull(reader.GetOrdinal("recovery_started_at"))
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("recovery_started_at"))));

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
                : reader.GetString(reader.GetOrdinal("last_error")),
            reader.IsDBNull(reader.GetOrdinal("remote_stage"))
                ? null
                : reader.GetString(reader.GetOrdinal("remote_stage")),
            reader.GetInt32(reader.GetOrdinal("remote_stage_ordinal")),
            reader.GetInt32(reader.GetOrdinal("transport_attempt_ordinal")));
}
