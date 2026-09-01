namespace Mesh.App.Services;

internal readonly record struct AgentRuntimeScopeToken(string Identity, long Generation);

internal sealed class AgentRuntimeScopeTracker
{
    private readonly object gate = new();
    private readonly AsyncLocal<AgentRuntimeScopeToken?> ambient = new();
    private AgentRuntimeScopeToken? active;
    private static long processGeneration;

    public void Activate(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        lock (gate)
            active = new AgentRuntimeScopeToken(
                identity,
                Interlocked.Increment(ref processGeneration));
    }

    public void Deactivate()
    {
        lock (gate)
        {
            Interlocked.Increment(ref processGeneration);
            active = null;
        }
    }

    public AgentRuntimeScopeToken CaptureCurrent()
    {
        lock (gate)
            return active
                   ?? throw new InvalidOperationException(
                       "An active account database is required for an agent run.");
    }

    public bool TryCaptureCurrent(out AgentRuntimeScopeToken scope)
    {
        lock (gate)
        {
            if (active is { } current)
            {
                scope = current;
                return true;
            }
            scope = default;
            return false;
        }
    }

    public IDisposable Enter(AgentRuntimeScopeToken scope)
    {
        var previous = ambient.Value;
        ambient.Value = scope;
        return new Lease(ambient, previous);
    }

    public bool IsCurrent(AgentRuntimeScopeToken scope)
    {
        lock (gate)
            return active == scope;
    }

    public bool IsCurrentContext
    {
        get
        {
            var expected = ambient.Value;
            if (expected is null) return true;
            lock (gate)
                return expected == active;
        }
    }

    private sealed class Lease(
        AsyncLocal<AgentRuntimeScopeToken?> ambient,
        AgentRuntimeScopeToken? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ambient.Value = previous;
        }
    }
}
