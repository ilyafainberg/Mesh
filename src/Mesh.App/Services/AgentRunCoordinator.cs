using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Owns user-visible execution planning and orchestration state. It deliberately publishes only an
/// action outline, never hidden model reasoning. The coordinator is a singleton, so plan/progress
/// survives page navigation together with the running turn.
/// </summary>
public sealed class AgentRunCoordinator(AppState state)
{
    public void Start(string threadId, string request, bool hasImages, bool widget)
    {
        // Do not manufacture a generic plan for every message. Normal turns use the existing
        // Thinking/tool-progress UI. A visible orchestration plan is useful only when the task
        // genuinely benefits from independent parallel workstreams.
        if (!LooksComplex(request))
        {
            state.ClearAgentRun(threadId);
            return;
        }

        var plan = BuildHyperscalePlan(hasImages, widget);
        var tasks = new[]
        {
            new AgentSubtaskState("inspect", hasImages ? "Inspect inputs and attached images" : "Inspect the request and current state", AgentStepState.Started),
            new AgentSubtaskState("execute", "Execute independent workstreams without conflicting changes", AgentStepState.Started),
            new AgentSubtaskState("verify", "Integrate and verify the result", AgentStepState.Started)
        };
        state.SetAgentRun(new AgentRunState(Guid.NewGuid().ToString("n"), threadId,
            AgentRunPhase.Hyperscaling, plan, tasks, DateTimeOffset.UtcNow));
    }

    public void Executing(string threadId)
    {
        // Hyperscaling is itself an executing phase. Preserve it until the parallel specialists
        // finish, otherwise AgentService cannot observe the decision and spawn them.
        if (state.AgentRunFor(threadId)?.Phase == AgentRunPhase.Planning)
            state.UpdateAgentRun(threadId, AgentRunPhase.Executing);
    }

    public void Verifying(string threadId)
        => state.UpdateAgentRun(threadId, AgentRunPhase.Verifying);

    public void Complete(string threadId, bool failed = false, bool cancelled = false)
    {
        var current = state.AgentRunFor(threadId)?.Phase;
        // A generic success cleanup must never overwrite a failure or cancellation recorded by catch.
        if (!failed && !cancelled && current is AgentRunPhase.Failed or AgentRunPhase.Cancelled) return;
        state.UpdateAgentRun(threadId,
            cancelled ? AgentRunPhase.Cancelled : failed ? AgentRunPhase.Failed : AgentRunPhase.Completed);
    }

    /// <summary>
    /// Runs genuinely independent delegates concurrently and returns results in declaration order.
    /// Callers must assign non-overlapping resources. Integration remains the caller's responsibility.
    /// </summary>
    public static async Task<IReadOnlyList<T>> HyperscaleAsync<T>(
        IEnumerable<Func<CancellationToken, Task<T>>> independentTasks, CancellationToken ct)
        => await Task.WhenAll(independentTasks.Select(work => work(ct)));

    private static bool LooksComplex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        var conjunctions = lower.Split(new[] { " and ", ", then ", ";" }, StringSplitOptions.None).Length - 1;
        return text.Length > 700 || conjunctions >= 3 ||
               lower.Contains("multi-step") || lower.Contains("hyperscale") ||
               lower.Contains("in parallel") || lower.Contains("across the project");
    }

    private static string BuildHyperscalePlan(bool hasImages, bool widget)
    {
        var lines = new List<string> { "**Plan - Hyperscale**" };
        var n = 1;
        if (hasImages) lines.Add($"{n++}. Inspect the request and attached images.");
        else lines.Add($"{n++}. Inspect the request and identify independent workstreams.");
        if (widget) lines.Add($"{n++}. Build the widget while independent checks run in parallel.");
        else lines.Add($"{n++}. Execute non-conflicting workstreams in parallel.");
        lines.Add($"{n}. Integrate and verify the result.");
        return string.Join("\n", lines);
    }
}
