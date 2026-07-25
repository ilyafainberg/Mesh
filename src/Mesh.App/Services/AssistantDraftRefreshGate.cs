using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Limits streamed-draft refreshes while keeping first and channel-changing deltas immediate.</summary>
internal sealed class AssistantDraftRefreshGate
{
    internal const int DefaultIntervalMilliseconds = 50;

    private sealed class RefreshState
    {
        public required AgentDeltaKind Kind { get; set; }
        public required long PublishedAt { get; set; }
    }

    private readonly int minimumIntervalMilliseconds;
    private readonly object gate = new();
    private readonly Dictionary<string, RefreshState> states = new(StringComparer.Ordinal);

    public AssistantDraftRefreshGate(int minimumIntervalMilliseconds = DefaultIntervalMilliseconds)
    {
        if (minimumIntervalMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumIntervalMilliseconds));
        this.minimumIntervalMilliseconds = minimumIntervalMilliseconds;
    }

    public bool ShouldPublish(string key, AgentDeltaKind kind, long nowMilliseconds)
    {
        lock (gate)
        {
            if (!states.TryGetValue(key, out var state))
            {
                states[key] = new RefreshState { Kind = kind, PublishedAt = nowMilliseconds };
                return true;
            }

            var elapsed = nowMilliseconds - state.PublishedAt;
            if (state.Kind == kind && elapsed >= 0 && elapsed < minimumIntervalMilliseconds)
                return false;

            state.Kind = kind;
            state.PublishedAt = nowMilliseconds;
            return true;
        }
    }

    public void Reset(string key)
    {
        lock (gate)
            states.Remove(key);
    }
}
