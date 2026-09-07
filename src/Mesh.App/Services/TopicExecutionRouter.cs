using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>Validates and routes one owner topic turn to its bound execution device.</summary>
public sealed class TopicExecutionRouter(
    AppState state,
    ITopicTurnRunner localRunner,
    IDeviceTopicTransport deviceTransport,
    Func<string>? currentPlatformProvider = null) : ITopicExecutionRouter
{
    internal static Action<string>? BeforeTransportCheckpointHook { get; set; }

    private sealed class RunEntry
    {
        public required TopicTurnDraft Draft { get; set; }
        public required string AttachmentFingerprint { get; init; }
        public required TaskCompletionSource<TopicDispatchResult> Dispatch { get; init; }
        public CancellationTokenSource? LocalCancellation { get; set; }
        public string? RemoteDeviceId { get; set; }
        public bool DurableBeginCommitted { get; set; }
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
        var validation = Validate(draft);
        if (validation is not null)
            return TopicDispatchResult.Reject(validation, draft.RunId);

        RunEntry entry;
        var owner = false;
        lock (submitGate)
        {
            var triggerKey = TriggerKey(draft);
            if (triggerRuns.TryGetValue(triggerKey, out var triggerRunId)
                && !string.Equals(triggerRunId, draft.RunId, StringComparison.Ordinal))
            {
                if (runs.TryGetValue(triggerRunId, out var triggerEntry)
                    && SameRequest(triggerEntry, draft, ignoreRunId: true))
                    entry = triggerEntry;
                else
                    return TopicDispatchResult.Reject(
                        "trigger_line_conflict", draft.RunId,
                        "The trigger line is already associated with another request.");
            }
            else if (runs.TryGetValue(draft.RunId, out entry!))
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
                    Draft = Snapshot(draft),
                    AttachmentFingerprint = AttachmentFingerprint(draft.Attachments),
                    Dispatch = new TaskCompletionSource<TopicDispatchResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously)
                };
                runs[draft.RunId] = entry;
                triggerRuns[triggerKey] = draft.RunId;
                owner = true;
            }
        }

        if (!owner)
            return await entry.Dispatch.Task.WaitAsync(cancellationToken);

        try
        {
            var result = await DispatchNewAsync(
                entry, progress, cancellationToken, handoffContext);
            entry.Dispatch.TrySetResult(result);
            if (!result.Accepted || entry.RemoteDeviceId is not null)
                RememberCompletion(entry);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                runs.TryRemove(draft.RunId, out _);
                var triggerKey = TriggerKey(draft);
                if (triggerRuns.TryGetValue(triggerKey, out var runId)
                    && string.Equals(runId, draft.RunId, StringComparison.Ordinal))
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

    public Task<bool> CancelQueuedAsync(
        string threadId,
        string runId,
        string lineId,
        CancellationToken cancellationToken)
        => RunOffDispatcherAsync(
            "topic.cancel-queued",
            () => CancelQueuedCoreAsync(threadId, runId, lineId, cancellationToken));

    private async Task<bool> CancelQueuedCoreAsync(
        string threadId,
        string runId,
        string lineId,
        CancellationToken cancellationToken)
    {
        if (!state.IsQueuedTopicRunLine(threadId, runId, lineId))
            return false;

        if (!await StopCoreAsync(threadId, runId, cancellationToken).ConfigureAwait(false))
            return false;
        state.SetQueuedTopicRunStage(threadId, runId, TopicQueueStage.Cancelling);
        return true;
    }

    public Task<bool> StopAsync(
        string threadId,
        string runId,
        CancellationToken cancellationToken)
        => RunOffDispatcherAsync(
            "topic.stop",
            () => StopCoreAsync(threadId, runId, cancellationToken));

    private async Task<bool> StopCoreAsync(
        string threadId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (!TopicRunProtocol.IsValidIdentifier(threadId)
            || !TopicRunProtocol.IsValidIdentifier(runId))
            return false;

        if (!runs.TryGetValue(runId, out var entry)
            || !string.Equals(entry.Draft.ThreadId, threadId, StringComparison.Ordinal))
        {
            var thread = state.Profile.OwnThreads.FirstOrDefault(item =>
                string.Equals(item.Id, threadId, StringComparison.Ordinal));
            if (thread?.ExecutionDeviceId is null
                || !state.IsKnownQueuedTopicRun(threadId, runId))
                return false;
            var queuedCancelled = await deviceTransport.CancelAsync(
                thread.ExecutionDeviceId,
                new TopicRunCancelPayload(runId, threadId),
                cancellationToken);
            if (queuedCancelled)
                state.SetQueuedTopicRunStage(threadId, runId, TopicQueueStage.Cancelling);
            return queuedCancelled;
        }

        var localCancellation = entry.LocalCancellation;
        if (localCancellation is not null)
        {
            var isCurrent = string.Equals(
                state.Profile.OwnThreads.FirstOrDefault(thread =>
                    string.Equals(thread.Id, threadId, StringComparison.Ordinal))
                    ?.ExecutionRunId,
                runId,
                StringComparison.Ordinal);
            if (isCurrent && state.IsThreadBusy(threadId))
                state.CancelThreadTurn(threadId);
            try
            {
                localCancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        if (entry.RemoteDeviceId is null) return false;
        var cancelled = await deviceTransport.CancelAsync(
            entry.RemoteDeviceId,
            new TopicRunCancelPayload(runId, threadId),
            cancellationToken);
        return cancelled;
    }
    public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken)
        => RunOffDispatcherAsync(
            "topic.list-devices",
            () => ListEligibleDevicesCoreAsync(cancellationToken));

    private async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesCoreAsync(
        CancellationToken cancellationToken)
    {
        await deviceListGate.WaitAsync(cancellationToken);
        try
        {
            var devices = await deviceTransport.ListEligibleDevicesAsync(cancellationToken);
            var eligible = devices
                .Where(device => device.Online && device.CanHostRemoteTurn)
                .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(device => device.Name ?? device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var current = CurrentDevice();
            if (current is null) return eligible;
            eligible.RemoveAll(device =>
                string.Equals(device.DeviceId, current.DeviceId, StringComparison.Ordinal));
            eligible.Insert(0, current);
            return eligible;
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
        TopicSendHandoffContext? handoffContext)
    {
        var draft = entry.Draft;
        var thread = state.Profile.OwnThreads.First(thread =>
            string.Equals(thread.Id, draft.ThreadId, StringComparison.Ordinal));
        var trigger = thread.Lines.FirstOrDefault(line =>
            string.Equals(line.Id, draft.TriggerLineId, StringComparison.Ordinal));
        if (trigger is not null && !MatchingTrigger(trigger, draft))
        {
            return TopicDispatchResult.Reject(
                "trigger_line_conflict", draft.RunId,
                "The trigger line ID already refers to different content.");
        }
        var current = CurrentDevice();
        var currentDeviceId = DeviceProtocol.DeviceId(state.Profile.PublicKey);
        var targetId = string.IsNullOrWhiteSpace(draft.TargetDeviceId)
            ? thread.ExecutionDeviceId ?? current?.DeviceId
            : draft.TargetDeviceId;
        if (targetId is null)
            return TopicDispatchResult.Reject(
                "device_not_eligible", draft.RunId,
                "This device is not ready to execute agent turns.");

        Mesh.Shared.DeviceInfo? target = null;
        var targetsCurrentDevice = string.Equals(
            targetId, currentDeviceId, StringComparison.Ordinal);
        if (targetsCurrentDevice)
        {
            target = current;
        }
        else
        {
            try
            {
                var devices = await ListEligibleDevicesCoreAsync(cancellationToken);
                target = devices.FirstOrDefault(device =>
                    string.Equals(device.DeviceId, targetId, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                target = null;
            }
        }
        if (target is null)
            return TopicDispatchResult.Reject(
                "device_not_eligible", draft.RunId,
                targetsCurrentDevice
                    ? "This device is not ready to execute agent turns."
                    : "The selected device is not online and agent-ready.");
        if (thread.ExecutionDeviceId is not null
            && !string.Equals(
                     thread.ExecutionDeviceId, target.DeviceId, StringComparison.Ordinal))
        {
            return TopicDispatchResult.Reject(
                "topic_bound_elsewhere", draft.RunId,
                "Move the topic before sending it to a different device.");
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
        var executionTarget = new ExecutionDevice(
            target!.DeviceId, target.Name, target.Platform);

        if (targetsCurrentDevice)
        {
            var beginCommand = new TopicRunBeginCommand(
                    draft,
                    executionTarget,
                    TopicRunBeginMode.Local,
                    queuedUpdate);
            var begin = handoffContext?.BeginTopicRun(
                            beginCommand,
                            () => state.BeginTopicRun(beginCommand))
                        ?? state.BeginTopicRun(beginCommand);
            if (!begin.Committed)
            {
                return TopicDispatchResult.Reject(
                    "local_persistence_failed",
                    draft.RunId,
                    $"The run could not be durably started ({begin.Code}).");
            }
            entry.DurableBeginCommitted = true;
            AdoptAuthoritativeBegin(entry, begin);
            var authoritativeDraft = entry.Draft;
            var authoritativeQueued = queuedUpdate with
            {
                RunId = authoritativeDraft.RunId,
                ThreadId = authoritativeDraft.ThreadId,
                TriggerLineId = authoritativeDraft.TriggerLineId
            };
            progress?.Report(authoritativeQueued);
            if (begin.ProjectionDeferred)
                return TopicDispatchResult.Ok(
                    authoritativeDraft.RunId, "projection_deferred", durable: true);
            if (!begin.Created)
                return TopicDispatchResult.Ok(
                    authoritativeDraft.RunId, begin.Code, durable: true);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            entry.LocalCancellation = linked;
            var projectedProgress = new InlineProgress<TopicRunUpdatePayload>(update =>
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
            _ = ObserveLocalRunAsync(RunLocalAsync(
                entry,
                authoritativeDraft,
                projectedProgress,
                linked));
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
            manifests.Select(manifest => manifest.Id).ToList());
        var command = new TopicRunBeginCommand(
            draft,
            executionTarget,
            TopicRunBeginMode.Remote,
            queuedUpdate,
            request,
            attachments);
        var remoteBegin = handoffContext?.BeginTopicRun(
                              command,
                              () => state.BeginTopicRun(command))
                          ?? state.BeginTopicRun(command);
        if (!remoteBegin.Committed)
        {
            return TopicDispatchResult.Reject(
                "local_persistence_failed",
                draft.RunId,
                $"The run could not be durably started ({remoteBegin.Code}).");
        }
        entry.DurableBeginCommitted = true;
        AdoptAuthoritativeBegin(entry, remoteBegin);
        var authoritativeRemoteDraft = entry.Draft;
        progress?.Report(queuedUpdate with
        {
            RunId = authoritativeRemoteDraft.RunId,
            ThreadId = authoritativeRemoteDraft.ThreadId,
            TriggerLineId = authoritativeRemoteDraft.TriggerLineId
        });
        if (remoteBegin.ProjectionDeferred)
            return TopicDispatchResult.Ok(
                authoritativeRemoteDraft.RunId, "projection_deferred", durable: true);
        if (remoteBegin.Outbox is null)
            return TopicDispatchResult.Ok(
                authoritativeRemoteDraft.RunId, remoteBegin.Code, durable: true);

        try
        {
            BeforeTransportCheckpointHook?.Invoke(authoritativeRemoteDraft.RunId);
            var result = await deviceTransport.DispatchPersistedAsync(
                remoteBegin.Outbox, cancellationToken);
            if (!result.Accepted)
            {
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
            var persistedTrigger = thread.Lines.FirstOrDefault(line =>
                string.Equals(line.Id, draft.TriggerLineId, StringComparison.Ordinal));
            persistedTrigger?.Attachments.Clear();
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
            runs[entry.Draft.RunId] = entry;
            triggerRuns[TriggerKey(entry.Draft)] = entry.Draft.RunId;
            if (!string.Equals(proposedRunId, entry.Draft.RunId, StringComparison.Ordinal))
                runs.TryRemove(proposedRunId, out _);
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
            state.CompleteLocalTopicRun(draft.RunId, DateTimeOffset.UtcNow);
            entry.LocalCancellation = null;
            cancellation.Dispose();
            RememberCompletion(entry);
        }
    }

    private static async Task ObserveLocalRunAsync(Task run)
    {
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-local-run-failed",
                $"exception={exception.GetType().FullName}");
        }
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

    private static string TriggerKey(TopicTurnDraft draft)
        => string.IsNullOrWhiteSpace(draft.TriggerOperationId)
            ? draft.ThreadId + "\0" + draft.TriggerLineId
            : "operation\0" + draft.TriggerOperationId;

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

    private Mesh.Shared.DeviceInfo? CurrentDevice()
    {
        if (!state.Profile.Model.IsConfigured) return null;
        var deviceId = DeviceProtocol.DeviceId(state.Profile.PublicKey);
        return new Mesh.Shared.DeviceInfo(
            deviceId,
            string.IsNullOrWhiteSpace(state.Profile.DeviceName)
                ? null
                : state.Profile.DeviceName.Trim(),
            true,
            CurrentPlatform());
    }

    private string CurrentPlatform() =>
        currentPlatformProvider?.Invoke()
        ?? (OperatingSystem.IsWindows() ? DevicePlatforms.Windows :
            OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() ? DevicePlatforms.MacOS :
            OperatingSystem.IsAndroid() ? DevicePlatforms.Android :
            OperatingSystem.IsIOS() ? DevicePlatforms.IOS :
            DevicePlatforms.Unknown);

    private void RememberCompletion(RunEntry entry)
    {
        lock (submitGate)
        {
            entry.Draft = entry.Draft with { Attachments = null };
            completedRuns.Enqueue(entry.Draft.RunId);
            while (completedRuns.Count > MaxRememberedRuns)
            {
                var expired = completedRuns.Dequeue();
                if (runs.TryRemove(expired, out var removed))
                    triggerRuns.Remove(
                        TriggerKey(removed.Draft));
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
