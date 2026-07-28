using System.Collections.Concurrent;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>Serializes owner turns per topic while allowing unrelated topics to run concurrently.</summary>
public sealed class TopicTurnRunner(AgentService agent, AppState state) : ITopicTurnRunner
{
    private sealed class TopicQueue
    {
        public readonly object Sync = new();
        public readonly Queue<WorkItem> Items = new();
        public bool Draining;
    }

    private sealed class WorkItem
    {
        public required TopicTurnDraft Draft { get; init; }
        public required IProgress<TopicRunUpdatePayload> Progress { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public Func<CancellationToken, Task>? OnStarted { get; init; }
        public TaskCompletionSource<TopicRunCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; set; }
        public bool WasQueued { get; set; }
    }

    private sealed record WidgetTurn(
        string Action,
        string? WidgetId,
        string? Prompt,
        string? ChangeRequest,
        string? WidgetName,
        string? WidgetPrompt,
        string? WidgetHtml,
        string? BasePrompt,
        string? BaseHtml);

    private readonly ConcurrentDictionary<string, TopicQueue> queues =
        new(StringComparer.Ordinal);

    public async Task<TopicRunCompletion> ExecuteAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? onStarted = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(progress);

        var queue = queues.GetOrAdd(draft.ThreadId, static _ => new TopicQueue());
        var item = new WorkItem
        {
            Draft = draft,
            Progress = progress,
            CancellationToken = cancellationToken,
            OnStarted = onStarted
        };
        var startDrain = false;
        var queued = 0;
        lock (queue.Sync)
        {
            queued = queue.Items.Count + (queue.Draining ? 1 : 0);
            item.WasQueued = queued > 0;
            queue.Items.Enqueue(item);
            if (!queue.Draining)
            {
                queue.Draining = true;
                startDrain = true;
            }
        }
        if (item.WasQueued)
            state.TrackQueuedTopicRun(draft.ThreadId, draft.RunId, draft.TriggerLineId);
        Report(progress, draft, TopicRunPhase.Queued, "Queued", queued: queued);
        using var registration = cancellationToken.Register(() =>
        {
            var clearQueued = false;
            lock (queue.Sync)
            {
                if (item.Started || item.Completion.Task.IsCompleted) return;
                ClearTriggerAttachments(draft);
                item.Completion.TrySetResult(
                    Complete(progress, draft, TopicRunPhase.Cancelled, "Cancelled"));
                clearQueued = item.WasQueued;
            }
            if (clearQueued)
                state.CompleteQueuedTopicRun(draft.ThreadId, draft.RunId);
        });
        if (startDrain) _ = DrainAsync(queue);
        return await item.Completion.Task;
    }

    private async Task DrainAsync(TopicQueue queue)
    {
        while (true)
        {
            WorkItem item;
            lock (queue.Sync)
            {
                do
                {
                    if (queue.Items.Count == 0)
                    {
                        queue.Draining = false;
                        return;
                    }
                    item = queue.Items.Dequeue();
                }
                while (item.Completion.Task.IsCompleted);
                item.Started = true;
            }
            TopicRunCompletion completion;
            try
            {
                if (item.OnStarted is not null)
                    await item.OnStarted(item.CancellationToken);
                if (item.WasQueued)
                    state.StartQueuedTopicRun(item.Draft.ThreadId, item.Draft.RunId);
                Report(item.Progress, item.Draft, TopicRunPhase.Executing, "Running");
                completion = await ExecuteCoreAsync(
                    item.Draft, item.Progress, item.CancellationToken);
            }
            catch (Exception ex)
            {
                ClearTriggerAttachments(item.Draft);
                completion = new TopicRunCompletion(
                    item.Draft.RunId,
                    item.Draft.ThreadId,
                    TopicRunPhase.Failed,
                    DateTimeOffset.UtcNow,
                    ex.Message,
                    "execution_failed");
            }
            if (item.WasQueued)
                state.CompleteQueuedTopicRun(item.Draft.ThreadId, item.Draft.RunId);
            item.Completion.TrySetResult(completion);
        }
    }

    private async Task<TopicRunCompletion> ExecuteCoreAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationToken cancellationToken)
    {
        var stateToken = state.BeginThreadTurn(draft.ThreadId, IsWidgetBuild(draft.WidgetContext));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, stateToken);
        ChatLine? trigger = null;

        try
        {
            state.SetAgentRun(new AgentRunState(
                draft.RunId,
                draft.ThreadId,
                AgentRunPhase.Executing,
                "",
                Array.Empty<AgentSubtaskState>(),
                draft.TriggerAt));
            trigger = FindTriggerLine(draft);
            trigger.Attachments = draft.Attachments?.ToList() ?? [];

            var widgetTurn = ParseWidgetTurn(draft.WidgetContext);
            if (widgetTurn is null)
                await ContinueOwnerAsync(draft, progress, linked.Token);
            else
                await ExecuteWidgetAsync(draft, widgetTurn, progress, linked.Token);

            state.UpdateAgentRun(draft.ThreadId, AgentRunPhase.Completed);
            state.MarkThreadCompleted(draft.ThreadId);
            return Complete(progress, draft, TopicRunPhase.Completed, "Completed");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            state.UpdateAgentRun(draft.ThreadId, AgentRunPhase.Cancelled);
            return Complete(progress, draft, TopicRunPhase.Cancelled, "Cancelled");
        }
        catch (InvalidWidgetContextException ex)
        {
            state.UpdateAgentRun(draft.ThreadId, AgentRunPhase.Failed);
            return Complete(
                progress, draft, TopicRunPhase.Failed, "Failed",
                ex.Message, "invalid_widget_context");
        }
        catch (Exception ex)
        {
            state.UpdateAgentRun(draft.ThreadId, AgentRunPhase.Failed);
            return Complete(
                progress, draft, TopicRunPhase.Failed, "Failed",
                ex.Message, "execution_failed");
        }
        finally
        {
            if (trigger is not null) trigger.Attachments.Clear();
            state.ClearThreadBuilding(draft.ThreadId);
            state.EndThreadTurn(draft.ThreadId);
            state.ClearRemoteRunProjection(draft.ThreadId, draft.RunId);
        }
    }

    private void ClearTriggerAttachments(TopicTurnDraft draft)
    {
        var thread = state.Profile.OwnThreads.FirstOrDefault(item =>
            string.Equals(item.Id, draft.ThreadId, StringComparison.Ordinal));
        var line = thread?.Lines.FirstOrDefault(item =>
            string.Equals(item.Id, draft.TriggerLineId, StringComparison.Ordinal));
        line?.Attachments.Clear();
    }

    private ChatLine FindTriggerLine(TopicTurnDraft draft)
    {
        var thread = state.Profile.OwnThreads.FirstOrDefault(t =>
            string.Equals(t.Id, draft.ThreadId, StringComparison.Ordinal));
        var line = thread?.Lines.FirstOrDefault(l =>
            string.Equals(l.Id, draft.TriggerLineId, StringComparison.Ordinal));
        if (line is null
            || !string.Equals(line.Role, "user", StringComparison.Ordinal)
            || !string.Equals(line.Text, draft.Prompt, StringComparison.Ordinal))
            throw new InvalidOperationException("The topic trigger line is missing or does not match.");
        return line;
    }

    private async Task ContinueOwnerAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationToken cancellationToken)
    {
        var steps = new List<TopicRunStep>();
        var runProgress = new InlineProgress<AgentRunState>(run =>
        {
            Report(
                progress,
                draft,
                MapPhase(run.Phase),
                StatusFor(run.Phase),
                run.Plan,
                run.Subtasks.Select(MapSubtask).ToList(),
                steps.ToList());
        });
        var stepProgress = new InlineProgress<AgentStep>(step =>
        {
            var mapped = MapStep(step);
            if (step.State == AgentStepState.Started)
                steps.Add(mapped);
            else
            {
                var index = steps.FindLastIndex(item =>
                    item.Tool == mapped.Tool
                    && item.State == TopicRunItemState.Running);
                if (index >= 0) steps[index] = mapped;
                else steps.Add(mapped);
            }
            Report(
                progress, draft, TopicRunPhase.Executing,
                step.Label, steps: steps.ToList());
        });

        // Stream the reply to viewing devices as it is generated. The executing device renders its own
        // draft locally; here we coalesce the model's token-level fragments and forward them on the same
        // topic.run.update channel so a viewer sees the answer build up instead of receiving one block. The
        // committed line remains authoritative, and per-run DeltaSeq gives the viewer ordered application.
        var coalescer = new AgentDeltaCoalescer();
        var deltaSeq = 0;
        var deltaProgress = new InlineProgress<AgentDelta>(fragment =>
        {
            var chunk = coalescer.Accept(fragment, Environment.TickCount64);
            if (chunk is not null)
                ReportDelta(progress, draft, ++deltaSeq, chunk);
        });

        await agent.ContinueAsOwnerAsync(
            draft.ThreadId,
            draft.TriggerLineId,
            draft.RunId,
            draft.TriggerAt,
            runProgress,
            stepProgress,
            deltaProgress,
            cancellationToken);

        var tail = coalescer.Flush();
        if (tail is not null)
            ReportDelta(progress, draft, ++deltaSeq, tail);
    }

    private async Task ExecuteWidgetAsync(
        TopicTurnDraft draft,
        WidgetTurn turn,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, draft, TopicRunPhase.Executing, WidgetStatus(turn.Action));
        var action = turn.Action;
        if (action is "use" or "refine"
            && draft.WidgetId is not null
            && !string.Equals(draft.WidgetId, turn.WidgetId, StringComparison.Ordinal))
            throw new InvalidWidgetContextException(
                "The widget context does not match the requested widget.");
        if (action == "use")
        {
            AddWidgetLine(
                draft.ThreadId, draft.TriggerLineId,
                FirstNonBlank(turn.WidgetHtml),
                FirstNonBlank(turn.WidgetPrompt));
            return;
        }

        if (action == "build")
        {
            var prompt = FirstNonBlank(turn.Prompt);
            var reply = await agent.BuildWidgetAsync(prompt, cancellationToken);
            var html = ExtractWidgetHtml(reply);
            if (html is null) throw new InvalidOperationException(reply);
            AddWidgetLine(draft.ThreadId, draft.TriggerLineId, html, prompt);
            return;
        }

        var saved = FindWidget(FirstNonBlank(turn.WidgetId));
        var originalPrompt = FirstNonBlank(turn.BasePrompt);
        var originalHtml = FirstNonBlank(turn.BaseHtml);
        if (!string.Equals(saved.Prompt, originalPrompt, StringComparison.Ordinal)
            || !string.Equals(saved.Html, originalHtml, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The saved widget changed before refinement began. Retry from the latest version.");
        var change = FirstNonBlank(turn.ChangeRequest);
        var refined = await agent.RefineWidgetAsync(originalPrompt, change, cancellationToken);
        var refinedHtml = ExtractWidgetHtml(refined);
        if (refinedHtml is null) throw new InvalidOperationException(refined);

        var updated = false;
        state.Mutate(profile =>
        {
            var current = profile.Widgets.FirstOrDefault(widget =>
                string.Equals(widget.Id, saved.Id, StringComparison.Ordinal));
            if (current is null
                || !string.Equals(current.Prompt, originalPrompt, StringComparison.Ordinal)
                || !string.Equals(current.Html, originalHtml, StringComparison.Ordinal))
                return;
            current.PreviousPrompt = current.Prompt;
            current.PreviousHtml = current.Html;
            current.Prompt = $"{originalPrompt}\n\nChange request: {change}";
            current.Html = refinedHtml;
            current.ModifiedAt = DateTimeOffset.UtcNow;
            updated = true;
        });
        if (!updated)
            throw new InvalidOperationException(
                "The saved widget changed while it was being refined. Retry from the latest version.");
        AddWidgetLine(
            draft.ThreadId, draft.TriggerLineId, refinedHtml, $"{originalPrompt}\n\nChange request: {change}");
    }

    private Widget FindWidget(string? widgetId)
    {
        if (!TopicRunProtocol.IsValidIdentifier(widgetId))
            throw new InvalidWidgetContextException("A valid saved widget ID is required.");
        return state.Profile.Widgets.FirstOrDefault(widget =>
                   string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
               ?? throw new InvalidWidgetContextException("The saved widget was not found.");
    }

    private void AddWidgetLine(string threadId, string triggerLineId, string html, string prompt)
        => state.AddOwnChatLine(threadId, new ChatLine
        {
            Role = "assistant",
            Text = $"```html-app\n{html}\n```",
            WidgetPrompt = prompt,
            ReplyToLineId = triggerLineId
        });

    private static WidgetTurn? ParseWidgetTurn(string? json)
    {
        if (json is null) return null;
        if (json.Length is 0 or > TopicRunProtocol.MaxWidgetContextChars)
            throw new InvalidWidgetContextException("Widget context has an invalid size.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidWidgetContextException("Widget context must be a JSON object.");
            var root = document.RootElement;
            var action = ReadString(root, "action", 16)
                         ?? ReadString(root, "mode", 16)
                         ?? throw new InvalidWidgetContextException(
                             "Widget context requires an action.");
            action = action.Trim().ToLowerInvariant();
            if (action is not ("build" or "use" or "refine"))
                throw new InvalidWidgetContextException("Unknown widget action.");
            var turn = new WidgetTurn(
                action,
                ReadString(root, "widgetId", TopicRunProtocol.MaxIdChars),
                ReadString(root, "prompt", TopicRunProtocol.MaxTextChars),
                ReadString(root, "changeRequest", TopicRunProtocol.MaxTextChars),
                ReadString(root, "widgetName", 256),
                ReadString(root, "widgetPrompt", TopicRunProtocol.MaxTextChars),
                ReadString(root, "widgetHtml", TopicRunProtocol.MaxWidgetContextChars),
                ReadString(root, "basePrompt", TopicRunProtocol.MaxTextChars),
                ReadString(root, "baseHtml", TopicRunProtocol.MaxWidgetContextChars));
            ValidateWidgetTurn(turn);
            return turn;
        }
        catch (JsonException ex)
        {
            throw new InvalidWidgetContextException("Widget context is not valid JSON.", ex);
        }
    }

    private static void ValidateWidgetTurn(WidgetTurn turn)
    {
        var required = turn.Action switch
        {
            "build" => new[] { turn.Prompt },
            "use" => new[]
            {
                turn.WidgetId, turn.WidgetName, turn.WidgetPrompt, turn.WidgetHtml
            },
            "refine" => new[]
            {
                turn.WidgetId, turn.WidgetName, turn.BasePrompt, turn.BaseHtml,
                turn.ChangeRequest
            },
            _ => throw new InvalidWidgetContextException("Unknown widget action.")
        };
        if (required.Any(string.IsNullOrWhiteSpace))
            throw new InvalidWidgetContextException(
                $"Widget action '{turn.Action}' is missing required fields.");
    }

    private static string? ReadString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidWidgetContextException($"Widget context '{name}' must be a string.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new InvalidWidgetContextException($"Widget context '{name}' is invalid.");
        return text.Trim();
    }

    private static string? ExtractWidgetHtml(string reply)
    {
        var segment = Markdown.Parse(reply).FirstOrDefault(item => item.IsApp);
        return segment is null || string.IsNullOrWhiteSpace(segment.Content)
            ? null
            : segment.Content.Trim();
    }

    private static bool IsWidgetBuild(string? context)
    {
        if (context is null) return false;
        try { return ParseWidgetTurn(context)?.Action == "build"; }
        catch (InvalidWidgetContextException) { return false; }
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? throw new InvalidWidgetContextException("Widget instructions are required.");

    private static string WidgetStatus(string action) => action switch
    {
        "build" => "Building widget",
        "refine" => "Refining widget",
        _ => "Opening widget"
    };

    private static TopicRunPhase MapPhase(AgentRunPhase phase) => phase switch
    {
        AgentRunPhase.Planning => TopicRunPhase.Planning,
        AgentRunPhase.Verifying => TopicRunPhase.Verifying,
        AgentRunPhase.Completed => TopicRunPhase.Completed,
        AgentRunPhase.Failed => TopicRunPhase.Failed,
        AgentRunPhase.Cancelled => TopicRunPhase.Cancelled,
        _ => TopicRunPhase.Executing
    };

    private static string StatusFor(AgentRunPhase phase) => phase switch
    {
        AgentRunPhase.Planning => "Planning",
        AgentRunPhase.Verifying => "Verifying",
        AgentRunPhase.Hyperscaling => "Running parallel work",
        AgentRunPhase.Integrating => "Integrating",
        _ => "Executing"
    };

    private static TopicRunSubtask MapSubtask(AgentSubtaskState item)
        => new(
            Bound(item.Id, TopicRunProtocol.MaxIdChars)!,
            Bound(item.Title, 4096)!,
            MapState(item.State),
            Bound(item.Result, 32 * 1024));

    private static TopicRunStep MapStep(AgentStep step)
        => new(
            Bound(step.Tool, TopicRunProtocol.MaxIdChars)!,
            Bound(step.Label, 4096)!,
            MapState(step.State),
            Bound(step.Arguments, 32 * 1024),
            Bound(step.Result, 32 * 1024),
            Bound(step.ToolName, TopicRunProtocol.MaxIdChars));

    private static TopicRunItemState MapState(AgentStepState state) => state switch
    {
        AgentStepState.Started => TopicRunItemState.Running,
        AgentStepState.Done => TopicRunItemState.Completed,
        _ => TopicRunItemState.Failed
    };

    private static void Report(
        IProgress<TopicRunUpdatePayload> progress,
        TopicTurnDraft draft,
        TopicRunPhase phase,
        string? status = null,
        string? plan = null,
        IReadOnlyList<TopicRunSubtask>? subtasks = null,
        IReadOnlyList<TopicRunStep>? steps = null,
        int queued = 0,
        string? error = null,
        string? failureCode = null)
        => progress.Report(new TopicRunUpdatePayload(
            draft.RunId,
            draft.ThreadId,
            phase,
            Bound(status, 4096),
            Bound(plan, 64 * 1024),
            subtasks?.Take(TopicRunProtocol.MaxItems).ToList(),
            steps?.TakeLast(TopicRunProtocol.MaxItems).ToList(),
            queued,
            error,
            failureCode,
            DateTimeOffset.UtcNow,
            TriggerLineId: draft.TriggerLineId));

    private static string? Bound(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];

    private static void ReportDelta(
        IProgress<TopicRunUpdatePayload> progress,
        TopicTurnDraft draft,
        int seq,
        AgentDelta delta)
        => progress.Report(new TopicRunUpdatePayload(
            draft.RunId,
            draft.ThreadId,
            TopicRunPhase.Executing,
            Timestamp: DateTimeOffset.UtcNow,
            DeltaSeq: seq,
            DeltaKind: delta.Kind == AgentDeltaKind.Reasoning
                ? TopicRunDeltaKind.Reasoning
                : TopicRunDeltaKind.Answer,
            Delta: Bound(delta.Text, TopicRunProtocol.MaxDeltaChars),
            TriggerLineId: draft.TriggerLineId));

    private static TopicRunCompletion Complete(
        IProgress<TopicRunUpdatePayload> progress,
        TopicTurnDraft draft,
        TopicRunPhase phase,
        string status,
        string? error = null,
        string? failureCode = null)
    {
        var completedAt = DateTimeOffset.UtcNow;
        progress.Report(new TopicRunUpdatePayload(
            draft.RunId,
            draft.ThreadId,
            phase,
            Bound(status, 4096),
            Error: Bound(error, 32 * 1024),
            FailureCode: Bound(failureCode, TopicRunProtocol.MaxIdChars),
            Timestamp: completedAt,
            TriggerLineId: draft.TriggerLineId));
        return new TopicRunCompletion(
            draft.RunId, draft.ThreadId, phase, completedAt, error, failureCode);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class InvalidWidgetContextException : Exception
    {
        public InvalidWidgetContextException(string message) : base(message) { }
        public InvalidWidgetContextException(string message, Exception inner) : base(message, inner) { }
    }
}
