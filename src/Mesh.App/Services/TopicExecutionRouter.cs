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
    IDeviceTopicTransport deviceTransport) : ITopicExecutionRouter
{
    private sealed class RunEntry
    {
        public required TopicTurnDraft Draft { get; set; }
        public required string AttachmentFingerprint { get; init; }
        public required TaskCompletionSource<TopicDispatchResult> Dispatch { get; init; }
        public CancellationTokenSource? LocalCancellation { get; set; }
        public string? RemoteDeviceId { get; set; }
    }

    private readonly ConcurrentDictionary<string, RunEntry> runs =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> triggerRuns = new(StringComparer.Ordinal);
    private readonly Queue<string> completedRuns = new();
    private readonly object submitGate = new();
    private readonly SemaphoreSlim deviceListGate = new(1, 1);
    private const int MaxRememberedRuns = 1024;

    public async Task<TopicDispatchResult> SubmitAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var validation = Validate(draft);
        if (validation is not null)
            return TopicDispatchResult.Reject(validation, draft.RunId);

        RunEntry entry;
        var owner = false;
        lock (submitGate)
        {
            var triggerKey = draft.ThreadId + "\0" + draft.TriggerLineId;
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
                entry, progress, cancellationToken);
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
        catch (Exception ex)
        {
            var result = TopicDispatchResult.Reject(
                "dispatch_failed", draft.RunId, ex.Message);
            entry.Dispatch.TrySetResult(result);
            RememberCompletion(entry);
            return result;
        }
    }

    public async Task<bool> StopAsync(
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
            state.SetQueuedTopicRunStage(threadId, runId, TopicQueueStage.Cancelling);
            var queuedCancelled = await deviceTransport.CancelAsync(
                thread.ExecutionDeviceId,
                new TopicRunCancelPayload(runId, threadId),
                cancellationToken);
            if (queuedCancelled)
                state.ClearRemoteRunProjection(threadId, runId);
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
        if (cancelled)
            state.ClearRemoteRunProjection(threadId, runId);
        return cancelled;
    }
    public async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken)
    {
        await deviceListGate.WaitAsync(cancellationToken);
        try
        {
            var devices = await deviceTransport.ListEligibleDevicesAsync(cancellationToken);
            var eligible = devices
                .Where(device => device.CanHostRemoteTurn)
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
        CancellationToken cancellationToken)
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
        var targetId = string.IsNullOrWhiteSpace(draft.TargetDeviceId)
            ? thread.ExecutionDeviceId ?? current?.DeviceId
            : draft.TargetDeviceId;
        if (targetId is null)
            return TopicDispatchResult.Reject(
                "device_not_eligible", draft.RunId,
                "This device is not ready to execute agent turns.");

        Mesh.Shared.DeviceInfo? target = null;
        if (targetId is not null)
        {
            try
            {
                var devices = await ListEligibleDevicesAsync(cancellationToken);
                target = devices.FirstOrDefault(device =>
                    string.Equals(device.DeviceId, targetId, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                target = null;
            }
            if (target is null
                && string.Equals(thread.ExecutionDeviceId, targetId, StringComparison.Ordinal)
                && DevicePlatforms.CanHostRemoteAgent(
                    true, thread.ExecutionDevicePlatform ?? DevicePlatforms.Unknown))
                target = new Mesh.Shared.DeviceInfo(
                    targetId,
                    thread.ExecutionDeviceName,
                    false,
                    thread.ExecutionDevicePlatform ?? DevicePlatforms.Unknown,
                    true);
            if (target is null)
                return TopicDispatchResult.Reject(
                    "device_not_eligible", draft.RunId,
                    "The selected device is not agent-ready.");
            if (thread.ExecutionDeviceId is null)
            {
                try
                {
                    state.BindOwnThreadForSend(
                        thread.Id,
                        new ExecutionDevice(target.DeviceId, target.Name, target.Platform));
                }
                catch (InvalidOperationException)
                {
                    return TopicDispatchResult.Reject("topic_bind_conflict", draft.RunId);
                }
            }
            else if (!string.Equals(
                         thread.ExecutionDeviceId, target.DeviceId, StringComparison.Ordinal))
            {
                return TopicDispatchResult.Reject(
                    "topic_bound_elsewhere", draft.RunId,
                    "Move the topic before sending it to a different device.");
            }
        }
        if (trigger is null)
        {
            trigger = new ChatLine
            {
                Id = draft.TriggerLineId,
                Role = "user",
                Text = draft.Prompt,
                SenderHandle = draft.TriggerHandle,
                At = draft.TriggerAt,
                Attachments = draft.Attachments?.ToList() ?? []
            };
            state.AddOwnChatLine(thread.Id, trigger);
        }
        var queuedBehindActiveRun = thread.ExecutionRunId is not null
                                    || state.IsThreadBusy(thread.Id);
        if (queuedBehindActiveRun)
            state.TrackQueuedTopicRun(thread.Id, draft.RunId, draft.TriggerLineId);

        var queuedUpdate = new TopicRunUpdatePayload(
            draft.RunId,
            draft.ThreadId,
            TopicRunPhase.Queued,
            "Queued",
            Queued: state.QueuedCountForThread(thread.Id),
            Timestamp: DateTimeOffset.UtcNow,
            TriggerLineId: draft.TriggerLineId);
        progress?.Report(queuedUpdate);

        if (current is not null
            && string.Equals(target!.DeviceId, current.DeviceId, StringComparison.Ordinal))
        {
            if (thread.ExecutionRunId is null)
            {
                state.RegisterExpectedRemoteRun(
                    thread.Id,
                    draft.RunId,
                    new ExecutionDevice(target.DeviceId, target.Name, target.Platform),
                    draft.TriggerAt);
                state.ApplyRemoteRunUpdate(queuedUpdate);
            }
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            entry.LocalCancellation = linked;
            var projectedProgress = new InlineProgress<TopicRunUpdatePayload>(update =>
            {
                state.ApplyRemoteRunUpdate(update);
                progress?.Report(update);
            });
            _ = RunLocalAsync(
                entry,
                draft,
                projectedProgress,
                linked);
            return TopicDispatchResult.Ok(draft.RunId);
        }

        var remoteTarget = target!;
        entry.RemoteDeviceId = remoteTarget.DeviceId;
        state.TrackQueuedTopicRun(
            thread.Id, draft.RunId, draft.TriggerLineId, TopicQueueStage.Sending);
        if (thread.ExecutionRunId is null)
        {
            state.RegisterExpectedRemoteRun(
                thread.Id,
                draft.RunId,
                new ExecutionDevice(
                    remoteTarget.DeviceId, remoteTarget.Name, remoteTarget.Platform),
                draft.TriggerAt);
            state.ApplyRemoteRunUpdate(queuedUpdate);
        }

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

        try
        {
            var result = await deviceTransport.DispatchAsync(
                remoteTarget.DeviceId, request, attachments, cancellationToken);
            if (!result.Accepted)
            {
                state.CompleteQueuedTopicRun(thread.Id, draft.RunId);
                state.ClearRemoteRunProjection(thread.Id, draft.RunId);
            }
            else
            {
                state.SetQueuedTopicRunStage(
                    thread.Id,
                    draft.RunId,
                    result.Code is DurableDeliveryCodes.RelayQueued
                        or DurableDeliveryCodes.Delivered
                        ? TopicQueueStage.Relay
                        : TopicQueueStage.Sending);
            }
            return result;
        }
        catch
        {
            state.CompleteQueuedTopicRun(thread.Id, draft.RunId);
            state.ClearRemoteRunProjection(thread.Id, draft.RunId);
            throw;
        }
        finally
        {
            trigger.Attachments.Clear();
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
            entry.LocalCancellation = null;
            cancellation.Dispose();
            RememberCompletion(entry);
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
           && string.Equals(left.WidgetContext, right.WidgetContext, StringComparison.Ordinal);

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
            CurrentPlatform(),
            true);
    }

    private static string CurrentPlatform() =>
        OperatingSystem.IsWindows() ? DevicePlatforms.Windows :
        OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() ? DevicePlatforms.MacOS :
        OperatingSystem.IsAndroid() ? DevicePlatforms.Android :
        OperatingSystem.IsIOS() ? DevicePlatforms.IOS :
        DevicePlatforms.Unknown;

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
                        removed.Draft.ThreadId + "\0" + removed.Draft.TriggerLineId);
            }
        }
    }

    private static string AttachmentId(string runId, int index)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{runId}\0{index}"))).ToLowerInvariant();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
