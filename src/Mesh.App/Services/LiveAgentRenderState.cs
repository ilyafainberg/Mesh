using System.Text;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Owns mutable live agent data and exposes render-safe snapshots.</summary>
internal sealed class LiveAgentRenderState
{
    private static readonly IReadOnlyList<AgentStep> NoSteps = Array.Empty<AgentStep>();
    private readonly object gate = new();
    private readonly Dictionary<string, List<AgentStep>> steps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DraftBuffer> drafts = new(StringComparer.Ordinal);

    public LiveAgentStateSnapshot Capture(string key)
    {
        lock (gate)
        {
            var stepSnapshot = steps.TryGetValue(key, out var current)
                ? current.ToArray()
                : NoSteps;
            AssistantDraftSnapshot? draftSnapshot = drafts.TryGetValue(key, out var draft)
                ? new AssistantDraftSnapshot(draft.Reasoning.ToString(), draft.Answer.ToString())
                : null;
            return new LiveAgentStateSnapshot(stepSnapshot, draftSnapshot);
        }
    }

    public IReadOnlyList<AgentStep> StepsFor(string key)
    {
        lock (gate)
            return steps.TryGetValue(key, out var current) ? current.ToArray() : NoSteps;
    }

    public bool BeginSteps(string key)
    {
        lock (gate)
        {
            if (steps.TryGetValue(key, out var current) && current.Count == 0)
                return false;

            steps[key] = new List<AgentStep>();
            return true;
        }
    }

    public void ReportStep(string key, AgentStep step)
    {
        lock (gate)
        {
            if (!steps.TryGetValue(key, out var current))
                steps[key] = current = new List<AgentStep>();

            if (step.State == AgentStepState.Started)
            {
                current.Add(step);
                return;
            }

            var index = current.FindLastIndex(item =>
                item.Tool == step.Tool && item.State == AgentStepState.Started);
            if (index >= 0) current[index] = step;
            else current.Add(step);
        }
    }

    public bool EndSteps(string key)
    {
        lock (gate)
            return steps.Remove(key);
    }

    public AssistantDraftSnapshot? DraftFor(string key)
    {
        lock (gate)
        {
            if (!drafts.TryGetValue(key, out var draft))
                return null;

            return new AssistantDraftSnapshot(draft.Reasoning.ToString(), draft.Answer.ToString());
        }
    }

    public void BeginDraft(string key)
    {
        lock (gate)
            drafts[key] = new DraftBuffer();
    }

    public bool AppendDraft(string key, AgentDelta delta)
    {
        if (string.IsNullOrEmpty(delta.Text)) return false;

        lock (gate)
        {
            if (!drafts.TryGetValue(key, out var draft))
                drafts[key] = draft = new DraftBuffer();

            (delta.Kind == AgentDeltaKind.Reasoning ? draft.Reasoning : draft.Answer)
                .Append(delta.Text);
            return true;
        }
    }

    public bool EndDraft(string key)
    {
        lock (gate)
            return drafts.Remove(key);
    }

    private sealed class DraftBuffer
    {
        public StringBuilder Reasoning { get; } = new();
        public StringBuilder Answer { get; } = new();
    }
}

internal readonly record struct LiveAgentStateSnapshot(
    IReadOnlyList<AgentStep> Steps,
    AssistantDraftSnapshot? Draft);
internal readonly record struct AssistantDraftSnapshot(string Reasoning, string Answer);
