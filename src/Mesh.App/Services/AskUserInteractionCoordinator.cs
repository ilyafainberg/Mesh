using System.Collections.Concurrent;
using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// A run-scoped request to pose an ask-user prompt during a suspended owner turn. Carries the
/// thread/run/trigger identity so a persisted <see cref="SuspendedAgentContext"/> can recover the
/// exact place to continue after a resolution, and the visual bubble can be bound to its thread.
/// </summary>
public sealed record AskUserToolRequest(
    string ThreadId,
    string RunId,
    string? TriggerLineId,
    string Question,
    IReadOnlyList<AskUserOption> Options,
    int? RecommendedIndex,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// In-process rendez-vous between a suspended owner run (waiting inside the ask_user tool) and the
/// resolution event raised from the UI. Exactly one waiter per prompt is served, and a single
/// resolution is relayed to it. When no waiter exists (e.g. the original run died across a restart)
/// the resolver falls back to the durable exactly-once resume path instead.
/// Pure and MAUI-free so it can be exercised behaviourally in tests.
/// </summary>
public sealed class AskUserInteractionCoordinator
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AskUserPrompt>> waiters =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers (or reuses) the single waiter for a prompt and returns a task that completes when the
    /// prompt is resolved. The first registration wins; a concurrent duplicate registration observes
    /// the same underlying completion source rather than creating a second waiter.
    /// </summary>
    public Task<AskUserPrompt> WaitAsync(string promptId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId))
            throw new ArgumentException("promptId must be non-blank.", nameof(promptId));

        var mine = new TaskCompletionSource<AskUserPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = waiters.GetOrAdd(promptId, mine);
        if (ReferenceEquals(tcs, mine) && ct.CanBeCanceled)
        {
            var registration = ct.Register(() =>
            {
                if (waiters.TryRemove(promptId, out var pending))
                    pending.TrySetCanceled(ct);
            });
            // Detach the cancellation registration once the wait settles.
            tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return tcs.Task;
    }

    /// <summary>
    /// Delivers a resolution to the in-process waiter, if one is registered. Returns true while a live
    /// waiter owns the prompt, including duplicate signals after the first result was delivered. This
    /// keeps competing resolvers out of the durable restart path until the live run consumes its
    /// exactly-once context fence.
    /// </summary>
    public bool TrySignalResolved(AskUserPrompt resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (waiters.TryGetValue(resolved.PromptId, out var tcs))
        {
            tcs.TrySetResult(resolved);
            return true;
        }
        return false;
    }

    /// <summary>Releases live ownership after the suspended run consumes its durable context fence.</summary>
    public void Complete(string promptId)
        => waiters.TryRemove(promptId, out _);

    /// <summary>Whether an in-process run is currently waiting on this prompt.</summary>
    public bool HasWaiter(string promptId) => waiters.ContainsKey(promptId);

    /// <summary>Drops a waiter without resolving it (used when a run is cancelled or torn down).</summary>
    public void Abandon(string promptId, Exception? reason = null)
    {
        if (waiters.TryRemove(promptId, out var tcs))
        {
            if (reason is not null) tcs.TrySetException(reason);
            else tcs.TrySetCanceled();
        }
    }
}

/// <summary>The presentation state of an ask-user bubble.</summary>
public enum AskUserBubbleStatus { Pending, Answered, Expired, Cancelled }

/// <summary>One rendered option row inside an ask-user bubble.</summary>
public sealed record AskUserOptionView(
    string Id,
    string Title,
    string? Description,
    bool IsRecommended,
    bool IsSelected);

/// <summary>
/// Pure projection of a durable <see cref="AskUserPrompt"/> into the fields the desktop and mobile
/// bubbles render. Kept free of MAUI so the mapping is unit-testable in isolation.
/// </summary>
public sealed record AskUserBubbleView(
    string PromptId,
    string ThreadId,
    string Question,
    IReadOnlyList<AskUserOptionView> Options,
    AskUserBubbleStatus Status,
    string? SelectedOptionId,
    string? SelectedOptionTitle,
    bool IsInteractive)
{
    /// <summary>Maps a prompt to its bubble view. Expired-but-still-pending rows render as expired.</summary>
    public static AskUserBubbleView From(AskUserPrompt prompt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var status = prompt.State switch
        {
            AskUserState.Resolved => AskUserBubbleStatus.Answered,
            AskUserState.Cancelled => AskUserBubbleStatus.Cancelled,
            AskUserState.Expired => AskUserBubbleStatus.Expired,
            _ when prompt.ExpiresAt is { } deadline && deadline <= now => AskUserBubbleStatus.Expired,
            _ => AskUserBubbleStatus.Pending,
        };

        var options = new List<AskUserOptionView>(prompt.Options.Count);
        for (var i = 0; i < prompt.Options.Count; i++)
        {
            var option = prompt.Options[i];
            options.Add(new AskUserOptionView(
                option.Id,
                option.Title,
                option.Description,
                IsRecommended: prompt.RecommendedIndex == i,
                IsSelected: status == AskUserBubbleStatus.Answered
                    && string.Equals(prompt.Selection, option.Id, StringComparison.Ordinal)));
        }

        var selectedTitle = status == AskUserBubbleStatus.Answered
            ? prompt.Options.FirstOrDefault(o =>
                string.Equals(o.Id, prompt.Selection, StringComparison.Ordinal))?.Title
            : null;

        return new AskUserBubbleView(
            prompt.PromptId,
            prompt.ThreadId,
            prompt.Question,
            options,
            status,
            status == AskUserBubbleStatus.Answered ? prompt.Selection : null,
            selectedTitle,
            IsInteractive: status == AskUserBubbleStatus.Pending);
    }
}

/// <summary>
/// The provider-neutral JSON schema and argument parsing for the internal owner-only ask_user tool.
/// Separated from the tool so both the tool and its tests can share one definition of the contract.
/// </summary>
public static class AskUserToolSchema
{
    public const string ToolName = "ask_user";

    public const string Description =
        "Pause and ask the owner a single multiple-choice question when you genuinely need a human "
        + "decision to proceed. Provide a clear question and 2-5 options. Optionally mark one option "
        + "as recommended. The run resumes with the owner's chosen option.";

    /// <summary>The parameters schema advertised to the model.</summary>
    public static object ParametersSchema { get; } = new
    {
        type = "object",
        properties = new
        {
            question = new
            {
                type = "string",
                description = "The decision to put to the owner, phrased as a direct question."
            },
            options = new
            {
                type = "array",
                minItems = 2,
                maxItems = 5,
                description = "Between 2 and 5 distinct choices.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Stable machine id for the option." },
                        title = new { type = "string", description = "Short label shown on the button." },
                        description = new { type = "string", description = "Optional one-line detail." }
                    },
                    required = new[] { "id", "title" }
                }
            },
            recommended_index = new
            {
                type = "integer",
                description = "Optional 0-based index of the recommended option."
            },
            expires_in_seconds = new
            {
                type = "integer",
                description = "Optional lifetime; after it elapses the prompt expires unanswered."
            }
        },
        required = new[] { "question", "options" }
    };

    /// <summary>
    /// Parses tool arguments into a run-scoped request. Throws <see cref="ArgumentException"/> on any
    /// contract violation (blank question, wrong option count, blank/duplicate ids, bad recommended
    /// index) by delegating the option invariants to <see cref="AskUserPrompt.Validate"/>.
    /// </summary>
    public static AskUserToolRequest ParseRequest(
        JsonElement args,
        string threadId,
        string runId,
        string? triggerLineId,
        DateTimeOffset now)
    {
        var question = ToolArgs.GetString(args, "question").Trim();
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("ask_user requires a non-blank question.");

        var options = ParseOptions(args);
        int? recommended = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("recommended_index", out var recEl)
            && recEl.ValueKind == JsonValueKind.Number
            && recEl.TryGetInt32(out var rec))
            recommended = rec;

        // Enforce the same 2-5 / unique-id / recommended-bounds invariants the store will re-check.
        AskUserPrompt.Validate(options, recommended);

        DateTimeOffset? expiresAt = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("expires_in_seconds", out var expEl)
            && expEl.ValueKind == JsonValueKind.Number
            && expEl.TryGetInt32(out var seconds)
            && seconds > 0)
            expiresAt = now.AddSeconds(seconds);

        return new AskUserToolRequest(
            threadId, runId, triggerLineId, question, options, recommended, expiresAt);
    }

    private static IReadOnlyList<AskUserOption> ParseOptions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("options", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("ask_user requires an options array.");

        var options = new List<AskUserOption>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each ask_user option must be an object.");
            var id = ToolArgs.GetString(item, "id").Trim();
            var title = ToolArgs.GetString(item, "title").Trim();
            var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            options.Add(new AskUserOption(id, title, string.IsNullOrWhiteSpace(description) ? null : description));
        }
        return options;
    }
}
