using Mesh.App.Domain;
using Microsoft.Data.Sqlite;

namespace Mesh.App.Services;

public enum AssistantAiRequestState
{
    MessageCommitted = 0,
    AwaitingHost = 10,
    DispatchPending = 20,
    Dispatched = 30,
    RetryPending = 40,
    Completed = 50,
    Cancelled = 60
}

public sealed record AssistantAiRequest(
    string RunId,
    string OperationId,
    string ThreadId,
    string TriggerLineId,
    string AccountId,
    long AccountGeneration,
    string? TargetDeviceId,
    string? TargetDeviceName,
    string? TargetDevicePlatform,
    AssistantAiRequestState State,
    int DispatchAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError)
{
    public bool IsTerminal => State is AssistantAiRequestState.Completed
        or AssistantAiRequestState.Cancelled;
}

public enum AssistantAiRequestTransitionOutcome
{
    Applied,
    AlreadyApplied,
    TerminalNoOp,
    StaleIdentity,
    Missing
}

public sealed record AssistantAiRequestTransition(
    AssistantAiRequestTransitionOutcome Outcome,
    AssistantAiRequest? Request)
{
    public bool Applied => Outcome == AssistantAiRequestTransitionOutcome.Applied;
    public bool IsStale => Outcome is AssistantAiRequestTransitionOutcome.StaleIdentity
        or AssistantAiRequestTransitionOutcome.Missing;
}

public sealed partial class MeshDb
{
    private void CreateAssistantAiRequestSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS assistant_ai_requests(
                run_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                trigger_line_id TEXT NOT NULL UNIQUE,
                account_id TEXT NOT NULL,
                account_generation INTEGER NOT NULL,
                target_device_id TEXT,
                target_device_name TEXT,
                target_device_platform TEXT,
                state INTEGER NOT NULL,
                dispatch_attempts INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                FOREIGN KEY(thread_id) REFERENCES own_threads(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_assistant_ai_requests_pending
                ON assistant_ai_requests(thread_id, state, updated_at);
            """);
        AddColumnIfMissing("assistant_ai_requests", "operation_id", "TEXT");
        Exec("""
            UPDATE assistant_ai_requests
            SET operation_id = run_id
            WHERE operation_id IS NULL OR trim(operation_id) = '';
            """);
    }

    public AssistantAiRequest CreateAssistantAiRequest(
        string runId,
        string operationId,
        string threadId,
        string triggerLineId,
        string accountId,
        long accountGeneration,
        AgentExecutionHost? target,
        DateTimeOffset createdAt)
    {
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO assistant_ai_requests(
                    run_id, operation_id, thread_id, trigger_line_id, account_id, account_generation,
                    target_device_id, target_device_name, target_device_platform,
                    state, created_at, updated_at)
                VALUES(
                    $run, $operation, $thread, $trigger, $account, $generation,
                    $target, $targetName, $targetPlatform,
                    $state, $created, $created);
                """;
            insert.Parameters.AddWithValue("$run", runId);
            insert.Parameters.AddWithValue("$operation", operationId);
            insert.Parameters.AddWithValue("$thread", threadId);
            insert.Parameters.AddWithValue("$trigger", triggerLineId);
            insert.Parameters.AddWithValue("$account", accountId);
            insert.Parameters.AddWithValue("$generation", accountGeneration);
            insert.Parameters.AddWithValue("$target", (object?)target?.DeviceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$targetName", (object?)target?.DeviceName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$targetPlatform", (object?)target?.Platform ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$state",
                (int)(target is null
                    ? AssistantAiRequestState.AwaitingHost
                    : AssistantAiRequestState.DispatchPending));
            insert.Parameters.AddWithValue("$created", createdAt.UtcDateTime.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var stored = GetAssistantAiRequest(runId)
            ?? throw new InvalidOperationException("The durable AI request could not be read after insert.");
        if (!string.Equals(stored.OperationId, operationId, StringComparison.Ordinal)
            || !string.Equals(stored.ThreadId, threadId, StringComparison.Ordinal)
            || !string.Equals(stored.TriggerLineId, triggerLineId, StringComparison.Ordinal)
            || !string.Equals(stored.AccountId, accountId, StringComparison.Ordinal))
            throw new InvalidOperationException("The AI request identity conflicts with an existing request.");
        return stored;
    }

    public AssistantAiRequest? GetAssistantAiRequest(string runId)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT run_id, operation_id, thread_id, trigger_line_id, account_id, account_generation,
                   target_device_id, target_device_name, target_device_platform,
                   state, dispatch_attempts, created_at, updated_at, last_error
            FROM assistant_ai_requests
            WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAssistantAiRequest(reader) : null;
    }

    public AssistantAiRequest? GetPendingAssistantAiRequest(string threadId)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT run_id, operation_id, thread_id, trigger_line_id, account_id, account_generation,
                   target_device_id, target_device_name, target_device_platform,
                   state, dispatch_attempts, created_at, updated_at, last_error
            FROM assistant_ai_requests
            WHERE thread_id = $thread
              AND state IN ($committed, $awaitingHost, $dispatchPending, $dispatched, $retryPending)
            ORDER BY julianday(updated_at) DESC, run_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$committed", (int)AssistantAiRequestState.MessageCommitted);
        command.Parameters.AddWithValue("$awaitingHost", (int)AssistantAiRequestState.AwaitingHost);
        command.Parameters.AddWithValue("$dispatchPending", (int)AssistantAiRequestState.DispatchPending);
        command.Parameters.AddWithValue("$dispatched", (int)AssistantAiRequestState.Dispatched);
        command.Parameters.AddWithValue("$retryPending", (int)AssistantAiRequestState.RetryPending);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAssistantAiRequest(reader) : null;
    }

    public AssistantAiRequest ReassignAssistantAiRequest(
        string runId,
        AgentExecutionHost target,
        DateTimeOffset updatedAt)
    {
        var current = GetAssistantAiRequest(runId)
            ?? throw new KeyNotFoundException($"AI request '{runId}' does not exist.");
        var transition = TryReassignAssistantAiRequest(
            current.RunId,
            current.OperationId,
            current.ThreadId,
            current.TriggerLineId,
            current.AccountId,
            current.AccountGeneration,
            target,
            updatedAt);
        if (transition.Outcome is not AssistantAiRequestTransitionOutcome.Applied)
            throw new InvalidOperationException("Only a pending AI request can be reassigned.");
        return transition.Request!;
    }

    public AssistantAiRequestTransition TryReassignAssistantAiRequest(
        string runId,
        string operationId,
        string threadId,
        string triggerLineId,
        string accountId,
        long accountGeneration,
        AgentExecutionHost target,
        DateTimeOffset updatedAt)
    {
        using var update = conn.CreateCommand();
        update.CommandText = """
            UPDATE assistant_ai_requests
            SET target_device_id = $target,
                target_device_name = $targetName,
                target_device_platform = $targetPlatform,
                state = CASE
                    WHEN state = $retryPending THEN state
                    ELSE $state
                END,
                updated_at = $updated,
                last_error = CASE
                    WHEN state = $retryPending THEN last_error
                    ELSE NULL
                END
            WHERE run_id = $run
              AND operation_id = $operation
              AND thread_id = $thread
              AND trigger_line_id = $trigger
              AND account_id = $account
              AND account_generation = $generation
              AND state NOT IN ($completed, $cancelled);
            """;
        update.Parameters.AddWithValue("$run", runId);
        update.Parameters.AddWithValue("$operation", operationId);
        update.Parameters.AddWithValue("$thread", threadId);
        update.Parameters.AddWithValue("$trigger", triggerLineId);
        update.Parameters.AddWithValue("$account", accountId);
        update.Parameters.AddWithValue("$generation", accountGeneration);
        update.Parameters.AddWithValue("$target", target.DeviceId);
        update.Parameters.AddWithValue("$targetName", (object?)target.DeviceName ?? DBNull.Value);
        update.Parameters.AddWithValue("$targetPlatform", target.Platform);
        update.Parameters.AddWithValue("$state", (int)AssistantAiRequestState.DispatchPending);
        update.Parameters.AddWithValue(
            "$retryPending",
            (int)AssistantAiRequestState.RetryPending);
        update.Parameters.AddWithValue("$updated", updatedAt.UtcDateTime.ToString("O"));
        update.Parameters.AddWithValue("$completed", (int)AssistantAiRequestState.Completed);
        update.Parameters.AddWithValue("$cancelled", (int)AssistantAiRequestState.Cancelled);
        if (update.ExecuteNonQuery() == 1)
            return new(AssistantAiRequestTransitionOutcome.Applied, GetAssistantAiRequest(runId));

        var current = GetAssistantAiRequest(runId);
        if (current is null)
            return new(AssistantAiRequestTransitionOutcome.Missing, null);
        if (!MatchesIdentity(
                current,
                operationId,
                threadId,
                triggerLineId,
                accountId,
                accountGeneration))
            return new(AssistantAiRequestTransitionOutcome.StaleIdentity, current);
        return new(
            current.IsTerminal
                ? AssistantAiRequestTransitionOutcome.TerminalNoOp
                : AssistantAiRequestTransitionOutcome.AlreadyApplied,
            current);
    }

    public AssistantAiRequest SetAssistantAiRequestState(
        string runId,
        AssistantAiRequestState state,
        DateTimeOffset updatedAt,
        string? error = null,
        bool incrementAttempt = false)
    {
        var current = GetAssistantAiRequest(runId);
        if (current is null)
            throw new KeyNotFoundException($"AI request '{runId}' does not exist.");
        if (current.IsTerminal) return current;

        var transition = TryApplyAssistantAiRequestTransition(
            current.RunId,
            current.OperationId,
            current.ThreadId,
            current.TriggerLineId,
            current.AccountId,
            current.AccountGeneration,
            state,
            NonterminalStates,
            updatedAt,
            error,
            incrementAttempt);
        return transition.Request
            ?? throw new KeyNotFoundException($"AI request '{runId}' does not exist.");
    }

    public AssistantAiRequestTransition TryApplyAssistantAiRequestTransition(
        string runId,
        string operationId,
        string threadId,
        string triggerLineId,
        string accountId,
        long accountGeneration,
        AssistantAiRequestState state,
        IReadOnlyCollection<AssistantAiRequestState> allowedSourceStates,
        DateTimeOffset updatedAt,
        string? error = null,
        bool incrementAttempt = false)
    {
        ArgumentNullException.ThrowIfNull(allowedSourceStates);
        if (allowedSourceStates.Count == 0)
            throw new ArgumentException("At least one source state is required.", nameof(allowedSourceStates));

        var sourceParameters = allowedSourceStates
            .Select((_, index) => $"$source{index}")
            .ToArray();
        using var update = conn.CreateCommand();
        update.CommandText = $"""
            UPDATE assistant_ai_requests
            SET state = $state,
                dispatch_attempts = dispatch_attempts + $increment,
                updated_at = $updated,
                last_error = $error
            WHERE run_id = $run
              AND operation_id = $operation
              AND thread_id = $thread
              AND trigger_line_id = $trigger
              AND account_id = $account
              AND account_generation = $generation
              AND state IN ({string.Join(", ", sourceParameters)});
            """;
        update.Parameters.AddWithValue("$run", runId);
        update.Parameters.AddWithValue("$operation", operationId);
        update.Parameters.AddWithValue("$thread", threadId);
        update.Parameters.AddWithValue("$trigger", triggerLineId);
        update.Parameters.AddWithValue("$account", accountId);
        update.Parameters.AddWithValue("$generation", accountGeneration);
        update.Parameters.AddWithValue("$state", (int)state);
        update.Parameters.AddWithValue("$increment", incrementAttempt ? 1 : 0);
        update.Parameters.AddWithValue("$updated", updatedAt.UtcDateTime.ToString("O"));
        update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        var index = 0;
        foreach (var source in allowedSourceStates)
            update.Parameters.AddWithValue(sourceParameters[index++], (int)source);

        if (update.ExecuteNonQuery() == 1)
            return new(AssistantAiRequestTransitionOutcome.Applied, GetAssistantAiRequest(runId));

        var current = GetAssistantAiRequest(runId);
        if (current is null)
            return new(AssistantAiRequestTransitionOutcome.Missing, null);
        if (!MatchesIdentity(
                current,
                operationId,
                threadId,
                triggerLineId,
                accountId,
                accountGeneration))
            return new(AssistantAiRequestTransitionOutcome.StaleIdentity, current);
        if (current.IsTerminal)
            return new(AssistantAiRequestTransitionOutcome.TerminalNoOp, current);
        return new(AssistantAiRequestTransitionOutcome.AlreadyApplied, current);
    }

    public bool TryCompleteAssistantAiRequest(string runId, DateTimeOffset updatedAt)
    {
        using var update = conn.CreateCommand();
        update.CommandText = """
            UPDATE assistant_ai_requests
            SET state = $completed,
                updated_at = $updated,
                last_error = NULL
            WHERE run_id = $run
              AND state NOT IN ($completed, $cancelled);
            """;
        update.Parameters.AddWithValue("$run", runId);
        update.Parameters.AddWithValue("$completed", (int)AssistantAiRequestState.Completed);
        update.Parameters.AddWithValue("$cancelled", (int)AssistantAiRequestState.Cancelled);
        update.Parameters.AddWithValue("$updated", updatedAt.UtcDateTime.ToString("O"));
        return update.ExecuteNonQuery() == 1;
    }

    private static readonly AssistantAiRequestState[] NonterminalStates =
    [
        AssistantAiRequestState.MessageCommitted,
        AssistantAiRequestState.AwaitingHost,
        AssistantAiRequestState.DispatchPending,
        AssistantAiRequestState.Dispatched,
        AssistantAiRequestState.RetryPending
    ];

    private static bool MatchesIdentity(
        AssistantAiRequest request,
        string operationId,
        string threadId,
        string triggerLineId,
        string accountId,
        long accountGeneration)
        => string.Equals(request.OperationId, operationId, StringComparison.Ordinal)
           && string.Equals(request.ThreadId, threadId, StringComparison.Ordinal)
           && string.Equals(request.TriggerLineId, triggerLineId, StringComparison.Ordinal)
           && string.Equals(request.AccountId, accountId, StringComparison.Ordinal)
           && request.AccountGeneration == accountGeneration;

    private static AssistantAiRequest ReadAssistantAiRequest(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            (AssistantAiRequestState)reader.GetInt64(9),
            reader.GetInt32(10),
            DateTimeOffset.Parse(reader.GetString(11)),
            DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13));
}
