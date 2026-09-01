using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>Validates and routes one owner topic turn to its bound execution device.</summary>
public sealed class TopicExecutionRouter : ITopicExecutionRouter, IAsyncDisposable
{
    private readonly AppState state;
    private readonly ITopicTurnRunner localRunner;
    private readonly IDeviceTopicTransport deviceTransport;
    private readonly CancellationTokenSource lifetime;
    private readonly ConcurrentDictionary<long, Task> localTasks = new();
    private readonly object stopGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> cancellationOperations =
        new(StringComparer.Ordinal);
    private long nextLocalTaskId;
    private Task? stopTask;
    internal static Action<string>? BeforeTransportCheckpointHook { get; set; }

    public TopicExecutionRouter(
        AppState state,
        ITopicTurnRunner localRunner,
        IDeviceTopicTransport deviceTransport)
        : this(state, localRunner, deviceTransport, new AppShutdownState(), null)
    {
    }

    public TopicExecutionRouter(
        AppState state,
        ITopicTurnRunner localRunner,
        IDeviceTopicTransport deviceTransport,
        AppShutdownState shutdownState,
        AppShutdownCoordinator? shutdown)
    {
        this.state = state;
        this.localRunner = localRunner;
        this.deviceTransport = deviceTransport;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownState.Token);
        shutdown?.Register(
            "topic-execution-router",
            cancellationToken => StopForProcessShutdownAsync().WaitAsync(cancellationToken));
    }

    private sealed class RunEntry
    {
        public required AgentRuntimeScopeToken RuntimeScope { get; init; }
        public required TopicTurnDraft Draft { get; set; }
        public required string AttachmentFingerprint { get; init; }
        public required TaskCompletionSource<TopicDispatchResult> Dispatch { get; init; }
        public CancellationTokenSource? LocalCancellation { get; set; }
        public string? RemoteDeviceId { get; set; }
        public bool DurableBeginCommitted { get; set; }
        public int CancellationCommitted;
        public required string OriginScopeId { get; init; }
    }

    private readonly ConcurrentDictionary<string, RunEntry> runs =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> triggerRuns = new(StringComparer.Ordinal);
    private readonly Queue<string> completedRuns = new();
    private readonly object submitGate = new();
    private readonly SemaphoreSlim deviceListGate = new(1, 1);
    private const int MaxRememberedRuns = 1024;

    public Task<TopicDispatchResult> SubmitAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload>? progress,
        CancellationToken cancellationToken,
        TopicSendHandoffContext? handoffContext = null)
        => RunOffDispatcherAsync(
            "topic.submit",
            () => SubmitCoreAsync(draft, progress, cancellationToken, handoffContext));

    private async Task<TopicDispatchResult> SubmitCoreAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload>? progress,
        CancellationToken cancellationToken,
        TopicSendHandoffContext? handoffContext)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!state.IsCurrentAgentRuntimeContext
            || !state.TryCaptureAgentRuntimeScope(out var runtimeScope))
            return TopicDispatchResult.Reject(
                "stale_account_scope",
                draft.RunId,
                "The active account changed before dispatch started.");
        using var runtimeContext = state.EnterAgentRuntimeScope(runtimeScope);
        var validation = Validate(draft);
        if (validation is not null)
            return TopicDispatchResult.Reject(validation, draft.RunId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.Token);
        var effectiveCancellation = linkedCancellation.Token;

        RunEntry entry;
        var owner = false;
        var runKey = RunKey(runtimeScope, draft.RunId);
        lock (submitGate)
        {
            var triggerKey = TriggerKey(runtimeScope, draft);
            if (triggerRuns.TryGetValue(triggerKey, out var triggerRunKey)
                && !string.Equals(triggerRunKey, runKey, StringComparison.Ordinal))
            {
                if (runs.TryGetValue(triggerRunKey, out var triggerEntry)
                    && SameRequest(triggerEntry, draft, ignoreRunId: true))
                    entry = triggerEntry;
                else
                    return TopicDispatchResult.Reject(
                        "trigger_line_conflict", draft.RunId,
                        "The trigger line is already associated with another request.");
            }
            else if (runs.TryGetValue(runKey, out entry!))
            {
                if (!SameRequest(entry, draft))
                    return TopicDispatchResult.Reject(
                        "run_id_conflict", draft.RunId,
                        "The run ID is already associated with a different request.");
            }
            else
            {
                entry = new RunEntry
                {
                    RuntimeScope = runtimeScope,
                    Draft = Snapshot(draft),
                    AttachmentFingerprint = AttachmentFingerprint(draft.Attachments),
                    Dispatch = new TaskCompletionSource<TopicDispatchResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
                    OriginScopeId = OriginScopeId(runtimeScope)
                };
                runs[runKey] = entry;
                triggerRuns[triggerKey] = runKey;
                owner = true;
            }
        }

        if (!owner)
            return await entry.Dispatch.Task.WaitAsync(effectiveCancellation);

        try
        {
            var result = await DispatchNewAsync(
                entry, progress, effectiveCancellation, handoffContext, runtimeScope);
            if (!state.IsCurrentAgentRuntimeScope(runtimeScope))
                return TopicDispatchResult.Reject(
                    "stale_account_scope",
                    draft.RunId,
                    "The active account changed while dispatch was running.");
            entry.Dispatch.TrySetResult(result);
            if (!result.Accepted && IsRetryablePreDispatch(result))
                ForgetRetryable(entry);
            else if (!result.Accepted || entry.RemoteDeviceId is not null)
                RememberCompletion(entry);
            return result;
        }

        catch (OperationCanceledException) when (effectiveCancellation.IsCancellationRequested)
        {
            var result = TopicDispatchResult.Reject("cancelled", draft.RunId);
            entry.Dispatch.TrySetResult(result);
            RememberCompletion(entry);
            return result;
        }
        catch (TopicSendAuthorizationException ex)
        {
            entry.Dispatch.TrySetException(ex);
            lock (submitGate)
            {
                runs.TryRemove(runKey, out _);
                var triggerKey = TriggerKey(runtimeScope, draft);
                if (triggerRuns.TryGetValue(triggerKey, out var storedRunKey)
                    && string.Equals(storedRunKey, runKey, StringComparison.Ordinal))
                    triggerRuns.Remove(triggerKey);
            }
            throw;
        }
        catch (Exception ex)
        {
            var result = TopicDispatchResult.Reject(
                "dispatch_failed",
                draft.RunId,
                ex.Message,
                entry.DurableBeginCommitted);
            entry.Dispatch.TrySetResult(result);
            RememberCompletion(entry);
            return result;
        }
    }

    private static bool IsRetryablePreDispatch(TopicDispatchResult result)
        => !result.Durable
           && result.Code is "device_not_eligible" or "dispatch_failed";

    private void ForgetRetryable(RunEntry entry)
    {
        lock (submitGate)
        {
            var runKey = RunKey(entry.RuntimeScope, entry.Draft.RunId);
            runs.TryRemove(runKey, out _);
            var triggerKey = TriggerKey(entry.RuntimeScope, entry.Draft);
            if (triggerRuns.TryGetValue(triggerKey, out var storedRunKey)
                && string.Equals(storedRunKey, runKey, StringComparison.Ordinal))
                triggerRuns.Remove(triggerKey);
        }
    }

    public Task<bool> CancelQueuedAsync(
        ScopedAsyncOperation operation,
        CancellationToken cancellationToken)
        => GetOrStartCancellation(
            "queued",
            operation,
            () => RunOffDispatcherAsync(
                "topic.cancel-queued",
                () => CancelQueuedCoreAsync(operation, cancellationToken)));

    private async Task<bool> CancelQueuedCoreAsync(
        ScopedAsyncOperation operation,
        CancellationToken cancellationToken)
    {
        if (!TryCaptureCancellationTarget(
                operation, queued: true, out var runtimeScope, out _, out _, out _))
            return false;
        using var runtimeContext = state.EnterAgentRuntimeScope(runtimeScope);

        if (!await StopCoreAsync(operation, cancellationToken, queued: true).ConfigureAwait(false))
            return false;
        return state.TryApplyScopedTopicCancellation(
            operation,
            queued: true,
            () =>
            {
                state.SetQueuedTopicRunStage(
                    operation.TopicId!, operation.RunId!, TopicQueueStage.Cancelling);
                return true;
            });
    }

    public Task<bool> StopAsync(
        ScopedAsyncOperation operation,
        CancellationToken cancellationToken)
        => GetOrStartCancellation(
            "stop",
            operation,
            () => RunOffDispatcherAsync(
                "topic.stop",
                () => StopCoreAsync(operation, cancellationToken, queued: false)));

    private async Task<bool> StopCoreAsync(
        ScopedAsyncOperation operation,
        CancellationToken cancellationToken,
        bool queued)
    {
        if (!TryCaptureCancellationTarget(
                operation,
                queued,
                out var runtimeScope,
                out var threadId,
                out var runId,
                out var entry))
            return false;
        using var runtimeContext = state.EnterAgentRuntimeScope(runtimeScope);

        if (entry is null)
        {
            string? targetDeviceId = null;
            TopicRunCancelPayload? cancel = null;
            if (!state.TryApplyScopedTopicCancellation(
                    operation,
                    queued,
                    () =>
                    {
                        targetDeviceId = state.Profile.OwnThreads.First(item =>
                            string.Equals(item.Id, threadId, StringComparison.Ordinal))
                            .AgentExecutionHostDeviceId;
                        var outbox = state.GetTopicOutbox(runId);
                        cancel = new TopicRunCancelPayload(
                            runId,
                            threadId,
                            outbox?.Request.RequestId,
                            outbox?.Request.OriginScopeId);
                        return targetDeviceId is not null;
                    }))
                return false;
            var queuedCancelled = await deviceTransport.CancelAsync(
                operation,
                targetDeviceId!,
                cancel!,
                cancellationToken);
            if (queuedCancelled)
                state.TryApplyScopedTopicCancellation(
                    operation,
                    queued,
                    () =>
                    {
                        state.SetQueuedTopicRunStage(
                            threadId, runId, TopicQueueStage.Cancelling);
                        return true;
                    });
            return queuedCancelled;
        }

        var localCancellation = entry.LocalCancellation;
        if (localCancellation is not null)
        {
            return state.TryApplyScopedTopicCancellation(
                operation,
                queued,
                () =>
                {
                    if (Interlocked.Exchange(ref entry.CancellationCommitted, 1) != 0)
                        return true;
                    if (state.IsThreadBusy(threadId))
                        state.CancelThreadTurn(operation);
                    try
                    {
                        localCancellation.Cancel();
                        return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                });
        }

        if (entry.RemoteDeviceId is null) return false;
        return await deviceTransport.CancelAsync(
            operation,
            entry.RemoteDeviceId,
            new TopicRunCancelPayload(
                runId,
                threadId,
                entry.Draft.TriggerOperationId ?? entry.Draft.TriggerLineId,
                entry.OriginScopeId),
            cancellationToken);
    }

    private bool TryCaptureCancellationTarget(
        ScopedAsyncOperation operation,
        bool queued,
        out AgentRuntimeScopeToken runtimeScope,
        out string threadId,
        out string runId,
        out RunEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(operation);
        runtimeScope = default;
        threadId = operation.TopicId ?? "";
        runId = operation.RunId ?? "";
        entry = null;
        if (!TopicRunProtocol.IsValidIdentifier(threadId)
            || !TopicRunProtocol.IsValidIdentifier(runId))
            return false;

        BeforeTransportCheckpointHook?.Invoke("cancellation-callee-lookup");
        var capturedRuntimeScope = default(AgentRuntimeScopeToken);
        var capturedThreadId = threadId;
        var capturedRunId = runId;
        RunEntry? capturedEntry = null;
        var captured = state.TryApplyScopedTopicCancellation(
            operation,
            queued,
            () =>
            {
                if (!state.TryCaptureAgentRuntimeScope(out capturedRuntimeScope))
                    return false;
                runs.TryGetValue(
                    RunKey(capturedRuntimeScope, capturedRunId), out capturedEntry);
                if (capturedEntry is not null
                    && (!string.Equals(
                            capturedEntry.Draft.ThreadId,
                            capturedThreadId,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            operation.RequestId,
                            capturedEntry.Draft.TriggerOperationId
                            ?? capturedEntry.Draft.TriggerLineId,
                            StringComparison.Ordinal)))
                    return false;
                return true;
            });
        runtimeScope = capturedRuntimeScope;
        entry = capturedEntry;
        return captured;
    }

    private Task<bool> GetOrStartCancellation(
        string kind,
        ScopedAsyncOperation operation,
        Func<Task<bool>> start)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var key = $"{kind}\0{operation.OperationId}";
        return cancellationOperations.GetOrAdd(
            key,
            _ => new Lazy<Task<bool>>(
                start,
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
    public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken)
        => RunOffDispatcherAsync(
            "topic.list-devices",
            () => ListEligibleDevicesCoreAsync(cancellationToken));

    public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesAsync(
        CancellationToken cancellationToken)
        => RunOffDispatcherAsync(
            "topic.list-roster",
            () => ListDevicesCoreAsync(cancellationToken));

    private async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesCoreAsync(
        CancellationToken cancellationToken)
        => Mesh.Shared.DeviceExecutionEligibility.EligibleHosts(
            await ListDevicesCoreAsync(cancellationToken));

    private async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesCoreAsync(
        CancellationToken cancellationToken)
    {
        await deviceListGate.WaitAsync(cancellationToken);
        try
        {
            return (await deviceTransport.ListDevicesAsync(cancellationToken))
                .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(device => device.Name ?? device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            deviceListGate.Release();
        }
    }

    private async Task<TopicDispatchResult> DispatchNewAsync(
        RunEntry entry,
        IProgress<TopicRunUpdatePayload>? progress,
        CancellationToken cancellationToken,
        TopicSendHandoffContext? handoffContext,
        AgentRuntimeScopeToken runtimeScope)
    {
        var draft = entry.Draft;
        OwnThread? thread = null;
        if (!state.TryApplyAgentRuntimeScope(
                runtimeScope,
                () => thread = state.Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, draft.ThreadId, StringComparison.Ordinal)))
            || thread is null)
            return TopicDispatchResult.Reject(
                "stale_account_scope",
                draft.RunId,
                "The active account changed before dispatch.");
        var trigger = thread.Lines.FirstOrDefault(line =>
            string.Equals(line.Id, draft.TriggerLineId, StringComparison.Ordinal));
        if (trigger is not null && !MatchingTrigger(trigger, draft))
        {
            return TopicDispatchResult.Reject(
                "trigger_line_conflict", draft.RunId,
                "The trigger line ID already refers to different content.");
        }
        IReadOnlyList<Mesh.Shared.DeviceInfo> eligible;
        try
        {
            eligible = await ListEligibleDevicesCoreAsync(cancellationToken);
            if (!state.IsCurrentAgentRuntimeScope(runtimeScope))
                return TopicDispatchResult.Reject(
                    "stale_account_scope",
                    draft.RunId,
                    "The active account changed while execution hosts were loaded.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!state.IsCurrentAgentRuntimeScope(runtimeScope))
                return TopicDispatchResult.Reject(
                    "stale_account_scope",
                    draft.RunId,
                    "The active account changed while execution hosts were loaded.");
            eligible = [];
        }
        var currentDeviceId = DeviceProtocol.DeviceId(state.Profile.PublicKey);
        var preferred = eligible.FirstOrDefault(device =>
            string.Equals(device.DeviceId, currentDeviceId, StringComparison.Ordinal))
            ?? eligible.FirstOrDefault();
        var targetId = string.IsNullOrWhiteSpace(draft.TargetDeviceId)
            ? thread.AgentExecutionHostDeviceId ?? preferred?.DeviceId
            : draft.TargetDeviceId;
        if (targetId is null)
            return TopicDispatchResult.Reject(
                "device_not_eligible", draft.RunId,
                "This device is not ready to execute agent turns.");

        Mesh.Shared.DeviceInfo? target = null;
        if (targetId is not null)
        {
            target = eligible.FirstOrDefault(device =>
                string.Equals(device.DeviceId, targetId, StringComparison.Ordinal));
            if (target is null)
                return TopicDispatchResult.Reject(
                    "device_not_eligible", draft.RunId,
                    "The selected device is not agent-ready.");
            if (thread.AgentExecutionHostDeviceId is not null
                && !string.Equals(
                         thread.AgentExecutionHostDeviceId, target.DeviceId, StringComparison.Ordinal))
            {
                return TopicDispatchResult.Reject(
                    "topic_bound_elsewhere", draft.RunId,
                    "Move the topic before sending it to a different device.");
            }
        }
        var queuedUpdate = new TopicRunUpdatePayload(
            draft.RunId,
            draft.ThreadId,
            TopicRunPhase.Queued,
            "Queued",
            Queued: state.QueuedCountForThread(thread.Id)
                    + (thread.ExecutionRunId is null ? 0 : 1),
            Timestamp: DateTimeOffset.UtcNow,
            TriggerLineId: draft.TriggerLineId);
        var executionTarget = new AgentExecutionHost(
            target!.DeviceId, target.Name, target.Platform);

        if (string.Equals(target.DeviceId, currentDeviceId, StringComparison.Ordinal))
        {
            var beginCommand = new TopicRunBeginCommand(
                    draft,
                    executionTarget,
                    TopicRunBeginMode.Local,
                    queuedUpdate);
            TopicRunBeginResult? begin = null;
            if (!state.TryApplyAgentRuntimeScope(
                    runtimeScope,
                    () => begin = handoffContext?.BeginTopicRun(
                                      beginCommand,
                                      () => state.BeginTopicRun(beginCommand))
                                  ?? state.BeginTopicRun(beginCommand)))
                return TopicDispatchResult.Reject(
                    "stale_account_scope",
                    draft.RunId,
                    "The active account changed before the run was persisted.");
            var committedBegin = begin!;
            if (!committedBegin.Committed)
            {
                return TopicDispatchResult.Reject(
                    "local_persistence_failed",
                    draft.RunId,
                    $"The run could not be durably started ({committedBegin.Code}).");
            }
            entry.DurableBeginCommitted = true;
            AdoptAuthoritativeBegin(entry, committedBegin);
            var authoritativeDraft = entry.Draft;
            var authoritativeQueued = queuedUpdate with
            {
                RunId = authoritativeDraft.RunId,
                ThreadId = authoritativeDraft.ThreadId,
                TriggerLineId = authoritativeDraft.TriggerLineId
            };
            progress?.Report(authoritativeQueued);
            if (committedBegin.ProjectionDeferred)
                return TopicDispatchResult.Ok(
                    authoritativeDraft.RunId, "projection_deferred", durable: true);
            if (!committedBegin.Created)
                return TopicDispatchResult.Ok(
                    authoritativeDraft.RunId, committedBegin.Code, durable: true);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lifetime.Token);
            entry.LocalCancellation = linked;
            var projectedProgress = new InlineProgress<TopicRunUpdatePayload>(update =>
            {
                state.TryApplyAgentRuntimeScope(entry.RuntimeScope, () =>
                {
                    if (update.Phase == TopicRunPhase.Queued)
                        state.TrackQueuedTopicRun(
                            update.ThreadId,
                            update.RunId,
                            update.TriggerLineId ?? authoritativeDraft.TriggerLineId);
                    else if (update.Phase is TopicRunPhase.Completed
                             or TopicRunPhase.Failed
                             or TopicRunPhase.Cancelled)
                        state.CompleteQueuedTopicRun(update.ThreadId, update.RunId);
                    else
                        state.StartQueuedTopicRun(update.ThreadId, update.RunId);
                    progress?.Report(update);
                });
            });
            TrackLocal(
                RunLocalAsync(entry, authoritativeDraft, projectedProgress, linked),
                authoritativeDraft.RunId);
            return TopicDispatchResult.Ok(authoritativeDraft.RunId, durable: true);
        }

        var remoteTarget = target!;
        entry.RemoteDeviceId = remoteTarget.DeviceId;

        var attachments = draft.Attachments?.ToList() ?? [];
        var manifests = attachments.Select((attachment, index) =>
            new TopicRunAttachment(
                AttachmentId(draft.RunId, index),
                attachment.Name,
                attachment.MimeType,
                attachment.Data.LongLength)).ToList();
        var request = new TopicRunRequestPayload(
            draft.RunId,
            draft.ThreadId,
            draft.TriggerLineId,
            draft.TriggerHandle,
            draft.Prompt,
            draft.TriggerAt,
            remoteTarget.DeviceId,
            draft.TurnMode,
            draft.WidgetId,
            draft.WidgetContext,
            manifests,
            manifests.Select(manifest => manifest.Id).ToList(),
            draft.TriggerOperationId ?? draft.TriggerLineId,
            entry.OriginScopeId);
        var command = new TopicRunBeginCommand(
            draft,
            executionTarget,
            TopicRunBeginMode.Remote,
            queuedUpdate,
            request,
            attachments);
        TopicRunBeginResult? remoteBegin = null;
        if (!state.TryApplyAgentRuntimeScope(
                runtimeScope,
                () => remoteBegin = handoffContext?.BeginTopicRun(
                                         command,
                                         () => state.BeginTopicRun(command))
                                     ?? state.BeginTopicRun(command)))
            return TopicDispatchResult.Reject(
                "stale_account_scope",
                draft.RunId,
                "The active account changed before the run was persisted.");
        var committedRemoteBegin = remoteBegin!;
        if (!committedRemoteBegin.Committed)
        {
            return TopicDispatchResult.Reject(
                "local_persistence_failed",
                draft.RunId,
                $"The run could not be durably started ({committedRemoteBegin.Code}).");
        }
        entry.DurableBeginCommitted = true;
        AdoptAuthoritativeBegin(entry, committedRemoteBegin);
        var authoritativeRemoteDraft = entry.Draft;
        progress?.Report(queuedUpdate with
        {
            RunId = authoritativeRemoteDraft.RunId,
            ThreadId = authoritativeRemoteDraft.ThreadId,
            TriggerLineId = authoritativeRemoteDraft.TriggerLineId
        });
        if (committedRemoteBegin.ProjectionDeferred)
            return TopicDispatchResult.Ok(
                authoritativeRemoteDraft.RunId, "projection_deferred", durable: true);
        if (committedRemoteBegin.Outbox is null)
            return TopicDispatchResult.Ok(
                authoritativeRemoteDraft.RunId, committedRemoteBegin.Code, durable: true);

        try
        {
            BeforeTransportCheckpointHook?.Invoke(authoritativeRemoteDraft.RunId);
            var result = await deviceTransport.DispatchPersistedAsync(
                committedRemoteBegin.Outbox, cancellationToken);
            var applied = state.TryApplyAgentRuntimeScope(runtimeScope, () =>
            {
                if (!result.Accepted)
                {
                    if (result.Code is "device_not_eligible" or "sender_offline")
                    {
                        state.TryApplyRemoteRunUpdate(new TopicRunUpdatePayload(
                            authoritativeRemoteDraft.RunId,
                            authoritativeRemoteDraft.ThreadId,
                            TopicRunPhase.Failed,
                            "Unavailable",
                            Error: result.Error,
                            FailureCode: result.Code,
                            Timestamp: DateTimeOffset.UtcNow,
                            TriggerLineId: authoritativeRemoteDraft.TriggerLineId));
                    }
                    state.SetQueuedTopicRunStage(
                        thread.Id, authoritativeRemoteDraft.RunId, TopicQueueStage.Failed);
                }
                else
                {
                    state.SetQueuedTopicRunStage(
                        thread.Id,
                        authoritativeRemoteDraft.RunId,
                        TopicExecutionStatus.IsRelayAccepted(result.Code)
                            ? TopicQueueStage.Relay
                            : TopicQueueStage.Sending);
                }
            });
            if (!applied)
                return TopicDispatchResult.Reject(
                    "stale_account_scope",
                    authoritativeRemoteDraft.RunId,
                    "The active account changed while the durable request was dispatched.",
                    durable: true);
            return result with
            {
                RunId = authoritativeRemoteDraft.RunId,
                Durable = true
            };
        }

        catch
        {
            throw;
        }
        finally
        {
            state.TryApplyAgentRuntimeScope(runtimeScope, () =>
            {
                var currentThread = state.Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, draft.ThreadId, StringComparison.Ordinal));
                var persistedTrigger = currentThread?.Lines.FirstOrDefault(line =>
                    string.Equals(line.Id, draft.TriggerLineId, StringComparison.Ordinal));
                persistedTrigger?.Attachments.Clear();
            });
        }
    }

    private void AdoptAuthoritativeBegin(
        RunEntry entry,
        TopicRunBeginResult begin)
    {
        if (begin.AuthoritativeDraft is null) return;
        lock (submitGate)
        {
            var proposedRunId = entry.Draft.RunId;
            entry.Draft = Snapshot(begin.AuthoritativeDraft);
            var authoritativeRunKey = RunKey(entry.RuntimeScope, entry.Draft.RunId);
            runs[authoritativeRunKey] = entry;
            triggerRuns[TriggerKey(entry.RuntimeScope, entry.Draft)] = authoritativeRunKey;
            if (!string.Equals(proposedRunId, entry.Draft.RunId, StringComparison.Ordinal))
                runs.TryRemove(RunKey(entry.RuntimeScope, proposedRunId), out _);
        }
    }

    private async Task RunLocalAsync(
        RunEntry entry,
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationTokenSource cancellation)
    {
        try
        {
            await localRunner.ExecuteAsync(draft, progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            state.TryCompleteLocalTopicRun(
                entry.RuntimeScope,
                draft.RunId,
                DateTimeOffset.UtcNow);
            entry.LocalCancellation = null;
            cancellation.Dispose();
            RememberCompletion(entry);
        }
    }

    private void TrackLocal(Task task, string runId)
    {
        var id = Interlocked.Increment(ref nextLocalTaskId);
        localTasks[id] = task;
        _ = ObserveLocalAsync(id, task, runId);
    }

    private async Task ObserveLocalAsync(long id, Task task, string runId)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException($"topic-local-run-{runId}", ex);
        }
        finally
        {
            localTasks.TryRemove(id, out _);
        }
    }

    private Task StopForProcessShutdownAsync()
    {
        lock (stopGate)
            return stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (AggregateException ex)
        {
            RuntimeDiagnostics.Current?.RecordException("topic-router-cancel", ex);
        }

        foreach (var entry in runs.Values)
        {
            try { entry.LocalCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        while (true)
        {
            var pending = localTasks.Values.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            var completions = pending.Select(task => task.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
            await Task.WhenAll(completions).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopForProcessShutdownAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
    private string? Validate(TopicTurnDraft draft)
    {
        if (!TopicRunProtocol.IsValidIdentifier(draft.RunId)) return "invalid_run";
        if (!TopicRunProtocol.IsValidIdentifier(draft.ThreadId)
            || !state.Profile.OwnThreads.Any(thread =>
                string.Equals(thread.Id, draft.ThreadId, StringComparison.Ordinal)))
            return "invalid_thread";
        if (!TopicRunProtocol.IsValidIdentifier(draft.TriggerLineId)) return "invalid_trigger_line";
        if (draft.TriggerOperationId is not null
            && !TopicRunProtocol.IsValidIdentifier(draft.TriggerOperationId))
            return "invalid_trigger_operation";
        if (!TopicRunProtocol.IsValidIdentifier(draft.TriggerHandle)
            || !string.Equals(
                AppState.Norm(draft.TriggerHandle),
                AppState.Norm(state.Profile.Handle),
                StringComparison.Ordinal))
            return "invalid_trigger_handle";
        if (string.IsNullOrWhiteSpace(draft.Prompt)
            || draft.Prompt.Length > TopicRunProtocol.MaxTextChars)
            return "invalid_prompt";
        if (draft.TriggerAt == default) return "invalid_timestamp";
        if (!Enum.IsDefined(draft.TurnMode)) return "invalid_turn_mode";
        if (draft.TargetDeviceId is not null
            && !TopicRunProtocol.IsValidIdentifier(draft.TargetDeviceId))
            return "invalid_target_device";
        if (draft.WidgetId is not null
            && !TopicRunProtocol.IsValidIdentifier(draft.WidgetId))
            return "invalid_widget";
        if (!ValidWidgetContext(draft.WidgetContext)) return "invalid_widget_context";
        if (!ValidAttachments(draft.Attachments)) return "invalid_attachments";
        return null;
    }

    private static bool ValidWidgetContext(string? context)
    {
        if (context is null) return true;
        if (context.Length is 0 or > TopicRunProtocol.MaxWidgetContextChars) return false;
        try
        {
            using var document = JsonDocument.Parse(context, new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryRequiredString(root, "action", 16, out var action))
                return false;
            return action switch
            {
                "build" => TryRequiredString(
                    root, "prompt", TopicRunProtocol.MaxTextChars, out _),
                "use" => TryRequiredString(root, "widgetId", TopicRunProtocol.MaxIdChars, out _)
                         && TryRequiredString(root, "widgetName", 256, out _)
                         && TryRequiredString(
                             root, "widgetPrompt", TopicRunProtocol.MaxTextChars, out _)
                         && TryRequiredString(
                             root, "widgetHtml", TopicRunProtocol.MaxWidgetContextChars, out _),
                "refine" => TryRequiredString(root, "widgetId", TopicRunProtocol.MaxIdChars, out _)
                            && TryRequiredString(root, "widgetName", 256, out _)
                            && TryRequiredString(
                                root, "basePrompt", TopicRunProtocol.MaxTextChars, out _)
                            && TryRequiredString(
                                root, "baseHtml", TopicRunProtocol.MaxWidgetContextChars, out _)
                            && TryRequiredString(
                                root, "changeRequest", TopicRunProtocol.MaxTextChars, out _),
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static bool TryRequiredString(
        JsonElement root,
        string name,
        int maxLength,
        out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()?.Trim() ?? "";
        return value.Length is > 0 && value.Length <= maxLength;
    }

    private static bool ValidAttachments(IReadOnlyList<ChatAttachment>? attachments)
    {
        if (attachments is null) return true;
        if (attachments.Count > TopicRunProtocol.MaxItems) return false;
        return attachments.All(attachment =>
            !string.IsNullOrWhiteSpace(attachment.Name)
            && attachment.Name.Length <= TopicRunProtocol.MaxIdChars
            && !string.IsNullOrWhiteSpace(attachment.MimeType)
            && attachment.MimeType.Length <= TopicRunProtocol.MaxIdChars
            && attachment.Data is not null
            && attachment.Data.LongLength is > 0 and <= AttachmentChunkProtocol.MaxAttachmentBytes);
    }

    private static bool MatchingTrigger(ChatLine line, TopicTurnDraft draft)
        => string.Equals(line.Role, "user", StringComparison.Ordinal)
           && string.Equals(line.Text, draft.Prompt, StringComparison.Ordinal)
           && line.At == draft.TriggerAt
           && (line.SenderHandle is null
               || string.Equals(
                   AppState.Norm(line.SenderHandle),
                   AppState.Norm(draft.TriggerHandle),
                   StringComparison.Ordinal));

    private static bool SameRequest(
        RunEntry entry,
        TopicTurnDraft right,
        bool ignoreRunId = false)
        => SameRequestMetadata(entry.Draft, right, ignoreRunId)
           && string.Equals(
               entry.AttachmentFingerprint,
               AttachmentFingerprint(right.Attachments),
               StringComparison.Ordinal);

    private static bool SameRequestMetadata(
        TopicTurnDraft left,
        TopicTurnDraft right,
        bool ignoreRunId = false)
        => (ignoreRunId || string.Equals(left.RunId, right.RunId, StringComparison.Ordinal))
           && string.Equals(left.ThreadId, right.ThreadId, StringComparison.Ordinal)
           && string.Equals(left.TriggerLineId, right.TriggerLineId, StringComparison.Ordinal)
           && string.Equals(left.TriggerHandle, right.TriggerHandle, StringComparison.Ordinal)
           && string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)
           && left.TriggerAt == right.TriggerAt
           && left.TurnMode == right.TurnMode
           && string.Equals(left.TargetDeviceId, right.TargetDeviceId, StringComparison.Ordinal)
           && string.Equals(left.WidgetId, right.WidgetId, StringComparison.Ordinal)
           && string.Equals(left.WidgetContext, right.WidgetContext, StringComparison.Ordinal)
           && string.Equals(
               left.TriggerOperationId, right.TriggerOperationId, StringComparison.Ordinal);

    private static string RunKey(AgentRuntimeScopeToken scope, string runId)
        => $"{scope.Identity}\0{scope.Generation}\0{runId}";

    private static string OriginScopeId(AgentRuntimeScopeToken scope)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{scope.Identity}\0{scope.Generation}"))).ToLowerInvariant();

    private static string TriggerKey(
        AgentRuntimeScopeToken scope,
        TopicTurnDraft draft)
        => $"{scope.Identity}\0{scope.Generation}\0"
           + (string.IsNullOrWhiteSpace(draft.TriggerOperationId)
               ? draft.ThreadId + "\0" + draft.TriggerLineId
               : "operation\0" + draft.TriggerOperationId);

    private static string AttachmentFingerprint(IReadOnlyList<ChatAttachment>? attachments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var attachment in attachments ?? [])
        {
            hash.AppendData(Encoding.UTF8.GetBytes(attachment.Name));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(attachment.MimeType));
            hash.AppendData([0]);
            hash.AppendData(attachment.Data);
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static TopicTurnDraft Snapshot(TopicTurnDraft draft)
        => draft with
        {
            Attachments = draft.Attachments?.Select(attachment =>
                new ChatAttachment(
                    attachment.Name,
                    attachment.MimeType,
                    attachment.Data.ToArray())).ToList()
        };

    private void RememberCompletion(RunEntry entry)
    {
        lock (submitGate)
        {
            entry.Draft = entry.Draft with { Attachments = null };
            completedRuns.Enqueue(RunKey(entry.RuntimeScope, entry.Draft.RunId));
            while (completedRuns.Count > MaxRememberedRuns)
            {
                var expired = completedRuns.Dequeue();
                if (runs.TryRemove(expired, out var removed))
                    triggerRuns.Remove(
                        TriggerKey(removed.RuntimeScope, removed.Draft));
            }
        }
    }

    private static string AttachmentId(string runId, int index)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{runId}\0{index}"))).ToLowerInvariant();

    private static Task<T> RunOffDispatcherAsync<T>(
        string operation,
        Func<Task<T>> action)
        => Task.Run(async () =>
        {
            using var trace = ManagedOperationDiagnostics.Begin(operation);
            return await action().ConfigureAwait(false);
        });

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
