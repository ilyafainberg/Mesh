using Mesh.App.Domain;

namespace Mesh.App.Services;

public sealed partial class AppState
{
    private readonly Dictionary<string, string> scopedAsyncOperations =
        new(StringComparer.Ordinal);
    internal static Action<string>? TopicCancellationBoundaryHook { get; set; }

    public ScopedAsyncOperation CaptureScopedAsyncOperation(
        string scopeKey,
        string? topicId = null,
        string? messageId = null,
        string? requestId = null,
        string? runId = null,
        string? conversationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (profileSyncGate)
        {
            var identity = activeDatabaseIdentity
                ?? throw new InvalidOperationException("No active profile database.");
            if (topicId is not null
                && !Profile.OwnThreads.Any(thread =>
                    string.Equals(thread.Id, topicId, StringComparison.Ordinal)))
                throw new KeyNotFoundException($"Topic '{topicId}' does not exist.");

            requestId = ResolveScopedRequestId(
                identity.Database, topicId, messageId, requestId, runId);
            var operationId = Guid.NewGuid().ToString("n");
            scopedAsyncOperations[scopeKey] = operationId;
            return new(
                identity.AccountId,
                identity.Identity,
                identity.Generation,
                scopeKey,
                operationId,
                topicId,
                messageId,
                requestId,
                runId,
                conversationId);
        }
    }

    public bool TryCaptureScopedAsyncOperation(
        string scopeKey,
        out ScopedAsyncOperation? scope,
        string? topicId = null,
        string? messageId = null,
        string? requestId = null,
        string? runId = null,
        string? conversationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (profileSyncGate)
        {
            var identity = activeDatabaseIdentity;
            if (identity is null
                || (topicId is not null
                    && !Profile.OwnThreads.Any(thread =>
                        string.Equals(thread.Id, topicId, StringComparison.Ordinal))))
            {
                scope = null;
                return false;
            }

            requestId = ResolveScopedRequestId(
                identity.Database, topicId, messageId, requestId, runId);
            var operationId = Guid.NewGuid().ToString("n");
            scopedAsyncOperations[scopeKey] = operationId;
            scope = new(
                identity.AccountId,
                identity.Identity,
                identity.Generation,
                scopeKey,
                operationId,
                topicId,
                messageId,
                requestId,
                runId,
                conversationId);
            return true;
        }
    }

    public bool IsCurrentScopedAsyncOperation(ScopedAsyncOperation scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (profileSyncGate)
            return IsCurrentScopedAsyncOperationUnderLock(scope);
    }

    public bool TryApplyScopedAsyncOperation(
        ScopedAsyncOperation scope,
        Action mutation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(mutation);
        lock (profileSyncGate)
        {
            if (!IsCurrentScopedAsyncOperationUnderLock(scope)) return false;
            mutation();
            return true;
        }
    }

    public bool TryCompleteScopedAsyncOperation(
        ScopedAsyncOperation scope,
        Action? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (profileSyncGate)
        {
            if (!IsCurrentScopedAsyncOperationUnderLock(scope)) return false;
            cleanup?.Invoke();
            if (scopedAsyncOperations.TryGetValue(scope.ScopeKey, out var operationId)
                && string.Equals(operationId, scope.OperationId, StringComparison.Ordinal))
                scopedAsyncOperations.Remove(scope.ScopeKey);
            return true;
        }
    }

    internal bool TryApplyScopedTopicCancellation(
        ScopedAsyncOperation scope,
        bool queued,
        Func<bool> mutation)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(mutation);
            lock (profileSyncGate)
            {
                TopicCancellationBoundaryHook?.Invoke("cancellation-commit");
                if (!IsCurrentScopedAsyncOperationUnderLock(scope)
                    || string.IsNullOrWhiteSpace(scope.TopicId)
                    || string.IsNullOrWhiteSpace(scope.RunId))
                    return false;

                var thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, scope.TopicId, StringComparison.Ordinal));
                if (thread is null) return false;

                var runMatches =
                    string.Equals(thread.ExecutionRunId, scope.RunId, StringComparison.Ordinal)
                    || IsKnownQueuedTopicRun(scope.TopicId, scope.RunId)
                    || activeThreadRuns.TryGetValue(scope.TopicId, out var activeRun)
                       && string.Equals(activeRun, scope.RunId, StringComparison.Ordinal)
                    || remoteRuns.TryGetValue(scope.TopicId, out var remote)
                       && string.Equals(remote.RunId, scope.RunId, StringComparison.Ordinal)
                    || activeDatabaseIdentity!.Database.GetTopicOutbox(scope.RunId) is { } outbox
                       && string.Equals(outbox.ThreadId, scope.TopicId, StringComparison.Ordinal);
                if (!runMatches) return false;

                var database = activeDatabaseIdentity!.Database;
                var assistantRequest = database.GetAssistantAiRequest(scope.RunId);
                var storedOutbox = database.GetTopicOutbox(scope.RunId);
                var expectedRequestId =
                    assistantRequest?.OperationId
                    ?? storedOutbox?.Request.RequestId
                    ?? scope.MessageId;
                if (string.IsNullOrWhiteSpace(scope.RequestId)
                    || string.IsNullOrWhiteSpace(expectedRequestId)
                    || !string.Equals(
                        scope.RequestId, expectedRequestId, StringComparison.Ordinal))
                    return false;

                if (queued)
                {
                    if (string.IsNullOrWhiteSpace(scope.MessageId)) return false;
                    if (!IsQueuedTopicRunLine(
                            scope.TopicId, scope.RunId, scope.MessageId))
                        return false;
                }

                return mutation();
            }
        }

    public bool TryAdvanceScopedAsyncOperation(
        ScopedAsyncOperation scope,
        out ScopedAsyncOperation? advanced,
        string? scopeKey = null,
        string? topicId = null,
        string? messageId = null,
        string? requestId = null,
        string? runId = null,
        string? conversationId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (profileSyncGate)
        {
            if (!IsCurrentScopedAsyncOperationUnderLock(scope))
            {
                advanced = null;
                return false;
            }

            var identity = activeDatabaseIdentity!;
            if (topicId is not null
                && !Profile.OwnThreads.Any(thread =>
                    string.Equals(thread.Id, topicId, StringComparison.Ordinal)))
            {
                advanced = null;
                return false;
            }

            var nextKey = scopeKey ?? scope.ScopeKey;
            ArgumentException.ThrowIfNullOrWhiteSpace(nextKey);
            var operationId = Guid.NewGuid().ToString("n");
            if (!string.Equals(nextKey, scope.ScopeKey, StringComparison.Ordinal))
                scopedAsyncOperations.Remove(scope.ScopeKey);
            scopedAsyncOperations[nextKey] = operationId;
            advanced = new(
                identity.AccountId,
                identity.Identity,
                identity.Generation,
                nextKey,
                operationId,
                topicId,
                messageId,
                requestId,
                runId,
                conversationId);
            return true;
        }
    }

    public void InvalidateScopedAsyncOperation(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (profileSyncGate)
            scopedAsyncOperations.Remove(scopeKey);
    }

    private bool IsCurrentScopedAsyncOperationUnderLock(ScopedAsyncOperation scope)
    {
        var identity = activeDatabaseIdentity;
        if (identity is null
            || !ReferenceEquals(activeDb, identity.Database)
            || !string.Equals(identity.AccountId, scope.AccountId, StringComparison.Ordinal)
            || !string.Equals(identity.Identity, scope.DatabaseIdentity, StringComparison.Ordinal)
            || identity.Generation != scope.Epoch
            || !scopedAsyncOperations.TryGetValue(scope.ScopeKey, out var operationId)
            || !string.Equals(operationId, scope.OperationId, StringComparison.Ordinal))
            return false;

        OwnThread? thread = null;
        if (scope.TopicId is not null)
        {
            thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, scope.TopicId, StringComparison.Ordinal));
            if (thread is null) return false;
        }
        Conversation? conversation = null;
        if (scope.ConversationId is not null)
        {
            conversation = Profile.Conversations.FirstOrDefault(candidate =>
                string.Equals(candidate.Handle, scope.ConversationId, StringComparison.OrdinalIgnoreCase));
            if (conversation is null) return false;
        }
        if (scope.MessageId is not null && thread is null && conversation is null) return false;
        if (conversation is not null
            && scope.MessageId is not null
            && !conversation.Lines.Any(line =>
                string.Equals(line.Id, scope.MessageId, StringComparison.Ordinal)))
            return false;

        if (scope.RequestId is null && scope.RunId is null) return true;
        if (scope.RunId is null) return false;
        var request = identity.Database.GetAssistantAiRequest(scope.RunId);
        if (request is null) return true;
        return (scope.RequestId is null
                || string.Equals(request.OperationId, scope.RequestId, StringComparison.Ordinal))
               && (scope.TopicId is null
                   || string.Equals(request.ThreadId, scope.TopicId, StringComparison.Ordinal))
               && (scope.MessageId is null
                   || string.Equals(request.TriggerLineId, scope.MessageId, StringComparison.Ordinal))
               && string.Equals(request.AccountId, scope.AccountId, StringComparison.Ordinal);
    }

    private static string? ResolveScopedRequestId(
        MeshDb database,
        string? topicId,
        string? messageId,
        string? requestId,
        string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return requestId;
        var assistantRequest = database.GetAssistantAiRequest(runId);
        var outbox = database.GetTopicOutbox(runId);
        var expected =
            assistantRequest?.OperationId
            ?? outbox?.Request.RequestId
            ?? messageId;
        if (requestId is not null
            && expected is not null
            && !string.Equals(requestId, expected, StringComparison.Ordinal))
            return requestId;
        if (topicId is not null)
        {
            if (assistantRequest is not null
                && !string.Equals(
                    assistantRequest.ThreadId, topicId, StringComparison.Ordinal))
                return requestId;
            if (outbox is not null
                && !string.Equals(outbox.ThreadId, topicId, StringComparison.Ordinal))
                return requestId;
        }
        return requestId ?? expected;
    }
}
