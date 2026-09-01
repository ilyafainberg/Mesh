using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed record AssistantAiRequestMutationScope(
    string AccountId,
    string DatabaseIdentity,
    long DatabaseGeneration,
    string RunId,
    string OperationId,
    string ThreadId,
    string TriggerLineId,
    long RequestAccountGeneration);

public sealed partial class AppState
{
    public async Task<AssistantAiRequest> CommitAssistantAiRequestAsync(
        string threadId,
        ChatLine trigger,
        string runId,
        string operationId,
        AgentExecutionHost? target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ValidateThreadId(threadId);
        if (!TopicRunProtocol.IsValidIdentifier(runId)
            || !TopicRunProtocol.IsValidIdentifier(operationId)
            || !TopicRunProtocol.IsValidIdentifier(trigger.Id))
            throw new ArgumentException("Stable run and trigger identities are required.");

        ActiveDatabaseIdentity identity;
        lock (profileSyncGate)
        {
            identity = activeDatabaseIdentity
                ?? throw new InvalidOperationException("No active profile database.");
            var thread = Profile.OwnThreads.FirstOrDefault(item =>
                string.Equals(item.Id, threadId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Topic '{threadId}' does not exist.");
            var existing = thread.Lines.FirstOrDefault(line =>
                string.Equals(line.Id, trigger.Id, StringComparison.Ordinal));
            if (existing is not null
                && (!string.Equals(existing.Text, trigger.Text, StringComparison.Ordinal)
                    || !string.Equals(existing.Role, trigger.Role, StringComparison.Ordinal)))
                throw new InvalidOperationException("The durable trigger identity conflicts with another message.");
            if (existing is null)
                AddOwnChatLine(threadId, trigger);
        }

        await FlushPersistenceAsync(cancellationToken).ConfigureAwait(false);

        lock (profileSyncGate)
        {
            if (!ReferenceEquals(activeDb, identity.Database)
                || !string.Equals(activeId, identity.AccountId, StringComparison.Ordinal)
                || activeDatabaseIdentity?.Generation != identity.Generation)
                throw new InvalidOperationException(
                    "The active account changed before the assistant message was committed.");

            return identity.Database.ExecuteDurableWrite(() =>
                identity.Database.CreateAssistantAiRequest(
                    runId,
                    operationId,
                    threadId,
                    trigger.Id,
                    identity.AccountId,
                    identity.Generation,
                    target,
                    trigger.At));
        }
    }

    public AssistantAiRequest? GetPendingAssistantAiRequest(string threadId)
    {
        lock (profileSyncGate)
            return activeDb?.GetPendingAssistantAiRequest(threadId);
    }

    public AssistantAiRequest? GetAssistantAiRequest(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetAssistantAiRequest(runId);
    }

    public AssistantAiRequestMutationScope CaptureAssistantAiRequestMutationScope(
        AssistantAiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (profileSyncGate)
        {
            var identity = activeDatabaseIdentity
                ?? throw new InvalidOperationException("No active profile database.");
            var current = identity.Database.GetAssistantAiRequest(request.RunId)
                ?? throw new KeyNotFoundException($"AI request '{request.RunId}' does not exist.");
            if (!MatchesAssistantAiRequestIdentity(current, request)
                || !string.Equals(current.AccountId, identity.AccountId, StringComparison.Ordinal))
                throw new InvalidOperationException("The AI request does not belong to the active account.");
            return new(
                identity.AccountId,
                identity.Identity,
                identity.Generation,
                current.RunId,
                current.OperationId,
                current.ThreadId,
                current.TriggerLineId,
                current.AccountGeneration);
        }
    }

    public bool TryCaptureAssistantAiRequestMutationScope(
        AssistantAiRequest request,
        out AssistantAiRequestMutationScope? scope)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (profileSyncGate)
        {
            var identity = activeDatabaseIdentity;
            var current = identity?.Database.GetAssistantAiRequest(request.RunId);
            if (identity is null
                || current is null
                || !MatchesAssistantAiRequestIdentity(current, request)
                || !string.Equals(current.AccountId, identity.AccountId, StringComparison.Ordinal))
            {
                scope = null;
                return false;
            }

            scope = new(
                identity.AccountId,
                identity.Identity,
                identity.Generation,
                current.RunId,
                current.OperationId,
                current.ThreadId,
                current.TriggerLineId,
                current.AccountGeneration);
            return true;
        }
    }

    public bool IsCurrentAssistantAiRequestMutationScope(
        AssistantAiRequestMutationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (profileSyncGate)
            return TryGetScopedAssistantAiRequest(scope, out _, out _);
    }

    public AssistantAiRequestTransition ReassignAssistantAiRequest(
        AssistantAiRequestMutationScope scope,
        AgentExecutionHost target)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateAgentExecutionHost(target);
        lock (profileSyncGate)
        {
            if (!TryGetScopedAssistantAiRequest(scope, out var db, out var request))
                return new(AssistantAiRequestTransitionOutcome.StaleIdentity, request);
            if (request!.IsTerminal)
                return new(AssistantAiRequestTransitionOutcome.TerminalNoOp, request);
            return db!.ExecuteDurableWrite(() =>
                db.TryReassignAssistantAiRequest(
                    scope.RunId,
                    scope.OperationId,
                    scope.ThreadId,
                    scope.TriggerLineId,
                    scope.AccountId,
                    scope.RequestAccountGeneration,
                    target,
                    timeProvider.GetUtcNow()));
        }
    }

    public AssistantAiRequestTransition RecordAssistantAiDispatch(
        AssistantAiRequestMutationScope scope,
        TopicDispatchResult result)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(result);
        lock (profileSyncGate)
        {
            if (!TryGetScopedAssistantAiRequest(scope, out var db, out var request))
                return new(AssistantAiRequestTransitionOutcome.StaleIdentity, request);
            var state = result.Accepted
                ? AssistantAiRequestState.Dispatched
                : AssistantAiRequestState.RetryPending;
            return db!.ExecuteDurableWrite(() =>
                db.TryApplyAssistantAiRequestTransition(
                    scope.RunId,
                    scope.OperationId,
                    scope.ThreadId,
                    scope.TriggerLineId,
                    scope.AccountId,
                    scope.RequestAccountGeneration,
                    state,
                    result.Accepted ? DispatchableStates : RejectableStates,
                    timeProvider.GetUtcNow(),
                    result.Accepted ? null : result.Error ?? result.Code,
                    incrementAttempt: true));
        }
    }

    public AssistantAiRequestTransition RecordAssistantAiDispatch(
        ActiveThreadMutationScope threadScope,
        string runId,
        string operationId,
        string triggerLineId,
        TopicDispatchResult result)
    {
        ArgumentNullException.ThrowIfNull(threadScope);
        ArgumentNullException.ThrowIfNull(result);
        lock (profileSyncGate)
        {
            if (!TryGetActiveThreadForScope(threadScope, out var db, out _))
                return new(AssistantAiRequestTransitionOutcome.StaleIdentity, null);
            var request = db!.GetAssistantAiRequest(runId);
            if (request is null)
                return new(AssistantAiRequestTransitionOutcome.Missing, null);
            if (!string.Equals(request.OperationId, operationId, StringComparison.Ordinal)
                || !string.Equals(request.TriggerLineId, triggerLineId, StringComparison.Ordinal)
                || !string.Equals(request.ThreadId, threadScope.ThreadId, StringComparison.Ordinal)
                || !string.Equals(request.AccountId, threadScope.AccountId, StringComparison.Ordinal))
                return new(AssistantAiRequestTransitionOutcome.StaleIdentity, request);
            var state = result.Accepted
                ? AssistantAiRequestState.Dispatched
                : AssistantAiRequestState.RetryPending;
            return db.ExecuteDurableWrite(() =>
                db.TryApplyAssistantAiRequestTransition(
                    request.RunId,
                    request.OperationId,
                    request.ThreadId,
                    request.TriggerLineId,
                    request.AccountId,
                    request.AccountGeneration,
                    state,
                    result.Accepted ? DispatchableStates : RejectableStates,
                    timeProvider.GetUtcNow(),
                    result.Accepted ? null : result.Error ?? result.Code,
                    incrementAttempt: true));
        }
    }

    [Obsolete("CaptureAssistantAiRequestMutationScope before asynchronous work.")]
    public AssistantAiRequest RecordAssistantAiDispatch(
        string runId,
        TopicDispatchResult result)
    {
        lock (profileSyncGate)
        {
            var request = activeDb?.GetAssistantAiRequest(runId)
                ?? throw new KeyNotFoundException($"AI request '{runId}' does not exist.");
            RuntimeDiagnostics.Current?.RecordEvent(
                "assistant-dispatch-unscoped-ignored",
                $"run={TopicSendSnapshot.StableId("run", runId)};state={request.State}");
            return request;
        }
    }

    public void CompleteAssistantAiRequest(string runId)
    {
        lock (profileSyncGate)
        {
            if (!IsCurrentAgentRuntimeContext) return;
            var db = activeDb ?? throw new InvalidOperationException("No active profile database.");
            db.ExecuteDurableWrite(() =>
                db.TryCompleteAssistantAiRequest(runId, timeProvider.GetUtcNow()));
        }
    }

    public AssistantAiRequestTransition CancelAssistantAiRequest(
        AssistantAiRequestMutationScope scope,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (profileSyncGate)
        {
            if (!TryGetScopedAssistantAiRequest(scope, out var db, out var request))
                return new(AssistantAiRequestTransitionOutcome.StaleIdentity, request);
            return db!.ExecuteDurableWrite(() =>
                db.TryApplyAssistantAiRequestTransition(
                    scope.RunId,
                    scope.OperationId,
                    scope.ThreadId,
                    scope.TriggerLineId,
                    scope.AccountId,
                    scope.RequestAccountGeneration,
                    AssistantAiRequestState.Cancelled,
                    TerminalizableStates,
                    timeProvider.GetUtcNow(),
                    reason));
        }
    }

    private bool TryGetScopedAssistantAiRequest(
        AssistantAiRequestMutationScope scope,
        out MeshDb? database,
        out AssistantAiRequest? request)
    {
        var identity = activeDatabaseIdentity;
        if (identity is null
            || !ReferenceEquals(activeDb, identity.Database)
            || !string.Equals(identity.AccountId, scope.AccountId, StringComparison.Ordinal)
            || !string.Equals(identity.Identity, scope.DatabaseIdentity, StringComparison.Ordinal)
            || identity.Generation != scope.DatabaseGeneration)
        {
            database = null;
            request = null;
            return false;
        }

        database = identity.Database;
        request = database.GetAssistantAiRequest(scope.RunId);
        return request is not null
               && string.Equals(request.OperationId, scope.OperationId, StringComparison.Ordinal)
               && string.Equals(request.ThreadId, scope.ThreadId, StringComparison.Ordinal)
               && string.Equals(request.TriggerLineId, scope.TriggerLineId, StringComparison.Ordinal)
               && string.Equals(request.AccountId, scope.AccountId, StringComparison.Ordinal)
               && request.AccountGeneration == scope.RequestAccountGeneration;
    }

    private static bool MatchesAssistantAiRequestIdentity(
        AssistantAiRequest left,
        AssistantAiRequest right)
        => string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
           && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
           && string.Equals(left.ThreadId, right.ThreadId, StringComparison.Ordinal)
           && string.Equals(left.TriggerLineId, right.TriggerLineId, StringComparison.Ordinal)
           && string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal)
           && left.AccountGeneration == right.AccountGeneration;

    private static readonly AssistantAiRequestState[] DispatchableStates =
    [
        AssistantAiRequestState.MessageCommitted,
        AssistantAiRequestState.AwaitingHost,
        AssistantAiRequestState.DispatchPending,
        AssistantAiRequestState.RetryPending
    ];

    private static readonly AssistantAiRequestState[] RejectableStates =
    [
        AssistantAiRequestState.MessageCommitted,
        AssistantAiRequestState.AwaitingHost,
        AssistantAiRequestState.DispatchPending,
        AssistantAiRequestState.Dispatched
    ];

    private static readonly AssistantAiRequestState[] TerminalizableStates =
    [
        AssistantAiRequestState.MessageCommitted,
        AssistantAiRequestState.AwaitingHost,
        AssistantAiRequestState.DispatchPending,
        AssistantAiRequestState.Dispatched,
        AssistantAiRequestState.RetryPending
    ];
}
