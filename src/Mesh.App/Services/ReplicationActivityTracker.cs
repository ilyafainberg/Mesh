namespace Mesh.App.Services;

/// <summary>
/// Keeps full device-snapshot activity visible until no new snapshot work has been observed for a
/// short quiet period. Live one-operation sync does not use this tracker.
/// </summary>
internal sealed class ReplicationActivityTracker
{
    private readonly object gate = new();
    private readonly TimeSpan quietPeriod;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private CancellationTokenSource? quietPeriodCancellation;
    private bool active;

    public ReplicationActivityTracker(TimeSpan quietPeriod)
        : this(quietPeriod, Task.Delay)
    {
    }

    internal ReplicationActivityTracker(
        TimeSpan quietPeriod,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        if (quietPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        this.delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        this.quietPeriod = quietPeriod;
    }

    public bool IsActive
    {
        get
        {
            lock (gate) return active;
        }
    }

    public event Action? Changed;

    public void ObserveActivity()
    {
        var next = new CancellationTokenSource();
        CancellationTokenSource? previous;
        bool becameActive;
        lock (gate)
        {
            previous = quietPeriodCancellation;
            quietPeriodCancellation = next;
            becameActive = !active;
            active = true;
        }

        try { previous?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // Its quiet-period task already completed between releasing the lock and cancellation.
        }
        _ = ClearAfterQuietPeriodAsync(next);
        if (becameActive) Changed?.Invoke();
    }

    private async Task ClearAfterQuietPeriodAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await delayAsync(quietPeriod, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancellation.Dispose();
            return;
        }

        bool becameInactive = false;
        lock (gate)
        {
            if (ReferenceEquals(quietPeriodCancellation, cancellation))
            {
                quietPeriodCancellation = null;
                active = false;
                becameInactive = true;
            }
        }
        cancellation.Dispose();
        if (becameInactive) Changed?.Invoke();
    }
}
