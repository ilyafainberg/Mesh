namespace Mesh.App.Services;

/// <summary>Tracks modal surfaces that must own the full mobile viewport.</summary>
public sealed class MobileOverlayState
{
    private int activeCount;

    public bool IsOpen => Volatile.Read(ref activeCount) > 0;

    public event Action? Changed;

    public IDisposable Enter()
    {
        if (Interlocked.Increment(ref activeCount) == 1)
            Changed?.Invoke();
        return new Lease(this);
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref activeCount) == 0)
            Changed?.Invoke();
    }

    private sealed class Lease(MobileOverlayState owner) : IDisposable
    {
        private MobileOverlayState? current = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref current, null)?.Exit();
        }
    }
}
