using System.Text;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Owns mutable live agent data and exposes render-safe snapshots.</summary>
internal sealed class LiveAgentRenderState
{
    private static readonly IReadOnlyList<AgentStep> NoSteps = Array.Empty<AgentStep>();
    private readonly object gate = new();
    private readonly Dictionary<string, RunState> runs = new(StringComparer.Ordinal);
    private readonly HashSet<string> terminalRuns = new(StringComparer.Ordinal);

    public LiveAgentStateSnapshot Capture(string key)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run) || run.Terminal)
                return new LiveAgentStateSnapshot(NoSteps, null);
            var stepSnapshot = run.Steps is { } current
                ? current.ToArray()
                : NoSteps;
            AssistantDraftSnapshot? draftSnapshot =
                run.Reasoning is not null && run.Answer is not null
                    ? new AssistantDraftSnapshot(
                        run.Reasoning.ToString(),
                        run.Answer.ToString())
                    : null;
            return new LiveAgentStateSnapshot(stepSnapshot, draftSnapshot);
        }
    }

    public IReadOnlyList<AgentStep> StepsFor(string key)
    {
        lock (gate)
            return runs.TryGetValue(key, out var run)
                   && !run.Terminal
                   && run.Steps is { } current
                ? current.ToArray()
                : NoSteps;
    }

    public bool BeginSteps(string key, string runId)
    {
        Validate(key, runId);
        lock (gate)
        {
            var run = BeginRun(key, runId);
            if (run.Terminal) return false;
            var changed = run.Steps is not { Count: 0 };
            run.Steps = [];
            run.StepsOpen = true;
            return changed;
        }
    }

    public bool ReportStep(string key, string runId, AgentStep step)
    {
        Validate(key, runId);
        ArgumentNullException.ThrowIfNull(step);
        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run)
                || !string.Equals(run.RunId, runId, StringComparison.Ordinal)
                || run.Terminal
                || !run.StepsOpen
                || run.Steps is not { } current)
                return false;

            if (step.State == AgentStepState.Started)
            {
                current.Add(step);
                return true;
            }

            var index = current.FindLastIndex(item =>
                item.Tool == step.Tool && item.State == AgentStepState.Started);
            if (index >= 0) current[index] = step;
            else current.Add(step);
            return true;
        }
    }

    public bool EndSteps(string key, string runId)
    {
        Validate(key, runId);
        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run)
                || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
                return false;
            run.StepsOpen = false;
            var changed = run.Steps is not null;
            run.Steps = null;
            return changed;
        }
    }

    public AssistantDraftSnapshot? DraftFor(string key)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run)
                || run.Terminal
                || run.Reasoning is null
                || run.Answer is null)
                return null;
            return new AssistantDraftSnapshot(
                run.Reasoning.ToString(),
                run.Answer.ToString());
        }
    }

    public bool BeginDraft(string key, string runId)
    {
        Validate(key, runId);
        lock (gate)
        {
            var run = BeginRun(key, runId);
            if (run.Terminal) return false;
            run.Reasoning = new StringBuilder();
            run.Answer = new StringBuilder();
            run.DraftOpen = true;
            return true;
        }
    }

    public bool AppendDraft(
        string key,
        string runId,
        AgentDelta delta,
        bool beginIfNeeded = false)
    {
        Validate(key, runId);
        ArgumentNullException.ThrowIfNull(delta);
        if (string.IsNullOrEmpty(delta.Text)) return false;

        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run)
                || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
            {
                if (!beginIfNeeded) return false;
                run = BeginRun(key, runId);
            }
            if (run.Terminal) return false;
            if (!run.DraftOpen)
            {
                if (!beginIfNeeded) return false;
                run.Reasoning = new StringBuilder();
                run.Answer = new StringBuilder();
                run.DraftOpen = true;
            }

            (delta.Kind == AgentDeltaKind.Reasoning ? run.Reasoning! : run.Answer!)
                .Append(delta.Text);
            return true;
        }
    }

    public bool EndDraft(string key, string runId)
    {
        Validate(key, runId);
        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run)
                || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
                return false;
            run.DraftOpen = false;
            var changed = run.Reasoning is not null || run.Answer is not null;
            run.Reasoning = null;
            run.Answer = null;
            return changed;
        }
    }

    public bool CompleteRun(string key, string runId)
    {
        Validate(key, runId);
        lock (gate)
        {
            if (!runs.TryGetValue(key, out var run)
                || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
                return false;
            var changed = !run.Terminal
                          || run.Steps is not null
                          || run.Reasoning is not null
                          || run.Answer is not null;
            run.Terminal = true;
            terminalRuns.Add(RunKey(key, runId));
            run.StepsOpen = false;
            run.DraftOpen = false;
            run.Steps = null;
            run.Reasoning = null;
            run.Answer = null;
            return changed;
        }
    }

    public void ResetForAccount()
    {
        lock (gate)
        {
            runs.Clear();
            terminalRuns.Clear();
        }
    }

    private RunState BeginRun(string key, string runId)
    {
        if (runs.TryGetValue(key, out var current)
            && string.Equals(current.RunId, runId, StringComparison.Ordinal))
            return current;
        if (terminalRuns.Contains(RunKey(key, runId)))
            return new RunState(runId) { Terminal = true };
        var next = new RunState(runId);
        runs[key] = next;
        return next;
    }

    private static string RunKey(string key, string runId) => key + "\0" + runId;

    private static void Validate(string key, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    }

    private sealed class RunState(string runId)
    {
        public string RunId { get; } = runId;
        public bool Terminal { get; set; }
        public bool StepsOpen { get; set; }
        public bool DraftOpen { get; set; }
        public List<AgentStep>? Steps { get; set; }
        public StringBuilder? Reasoning { get; set; }
        public StringBuilder? Answer { get; set; }
    }
}

internal readonly record struct LiveAgentStateSnapshot(
    IReadOnlyList<AgentStep> Steps,
    AssistantDraftSnapshot? Draft);
internal readonly record struct AssistantDraftSnapshot(string Reasoning, string Answer);

internal static class AgentRunLifecycle
{
    public static bool IsTerminal(AgentRunPhase phase)
        => phase is AgentRunPhase.Completed or AgentRunPhase.Failed or AgentRunPhase.Cancelled;

    public static bool CanTransition(AgentRunPhase current, AgentRunPhase next)
    {
        if (IsTerminal(current)) return false;
        if (IsTerminal(next)) return true;
        return TransientOrder(next) >= TransientOrder(current);
    }

    private static int TransientOrder(AgentRunPhase phase) => phase switch
    {
        AgentRunPhase.Planning => 0,
        AgentRunPhase.Executing => 1,
        AgentRunPhase.Hyperscaling => 2,
        AgentRunPhase.Integrating => 3,
        AgentRunPhase.Verifying => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };
}
