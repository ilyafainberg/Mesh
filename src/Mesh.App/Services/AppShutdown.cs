namespace Mesh.App.Services;

/// <summary>Process-wide signal that prevents new work once application shutdown begins.</summary>
public sealed class AppShutdownState
{
    private readonly CancellationTokenSource stopping = new();
    private int started;

    public bool IsStopping => Volatile.Read(ref started) != 0;
    public CancellationToken Token => stopping.Token;

    internal void BeginStopping()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        try
        {
            stopping.Cancel();
        }
        catch (AggregateException ex)
        {
            RuntimeDiagnostics.Current?.RecordException("shutdown-cancellation", ex);
        }
    }
}

/// <summary>
/// Cancels registered producers and drains observed background work before the native WebView is destroyed.
/// </summary>
public sealed class AppShutdownCoordinator(AppShutdownState state)
{
    private sealed record StopRegistration(string Name, Func<CancellationToken, Task> Stop);
    private sealed record TrackedTask(string Operation, Task Task);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    private readonly object gate = new();
    private readonly List<StopRegistration> registrations = [];
    private readonly List<StopRegistration> drains = [];
    private readonly Dictionary<long, TrackedTask> trackedTasks = [];
    private long nextTaskId;
    private Task? shutdownTask;

    public bool IsStopping => state.IsStopping;
    public CancellationToken Token => state.Token;

    public void Register(string name, Func<CancellationToken, Task> stop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stop);
        lock (gate)
        {
            if (shutdownTask is not null)
                throw new InvalidOperationException("Shutdown has already started.");
            registrations.Add(new StopRegistration(name, stop));
        }
    }

    /// <summary>Registers a final barrier that runs after producers and tracked work have stopped.</summary>
    public void RegisterDrain(string name, Func<CancellationToken, Task> drain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(drain);
        lock (gate)
        {
            if (shutdownTask is not null)
                throw new InvalidOperationException("Shutdown has already started.");
            drains.Add(new StopRegistration(name, drain));
        }
    }

    /// <summary>Observes a background task immediately and includes it in the shutdown drain.</summary>
    public void Track(Task task, string operation)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var id = Interlocked.Increment(ref nextTaskId);
        lock (gate)
            trackedTasks[id] = new TrackedTask(operation, task);
        _ = ObserveTrackedAsync(id, task, operation);
    }

    public Task ShutdownAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (gate)
            return shutdownTask ??= ShutdownCoreAsync(effectiveTimeout);
    }

    private async Task ShutdownCoreAsync(TimeSpan timeout)
    {
        state.BeginStopping();
        RuntimeDiagnostics.Current?.MarkLifecycle("shutdown-start");

        StopRegistration[] stops;
        StopRegistration[] finalDrains;
        lock (gate)
            stops = registrations.ToArray();
        lock (gate)
            finalDrains = drains.ToArray();

        using var timeoutSource = new CancellationTokenSource(timeout);
        var stopTasks = stops.Select(stop => StopOneAsync(stop, timeoutSource.Token)).ToArray();
        try
        {
            await Task.WhenAll(stopTasks).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            await DrainTrackedAsync(timeoutSource.Token).ConfigureAwait(false);
            var drainTasks = finalDrains.Select(drain => StopOneAsync(drain, timeoutSource.Token));
            await Task.WhenAll(drainTasks).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            await DrainTrackedAsync(timeoutSource.Token).ConfigureAwait(false);
            RuntimeDiagnostics.Current?.MarkLifecycle("shutdown-complete");
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            string pending;
            lock (gate)
                pending = string.Join(", ", trackedTasks.Values.Select(item => item.Operation).Distinct());
            RuntimeDiagnostics.Current?.RecordEvent(
                "shutdown-timeout",
                string.IsNullOrEmpty(pending) ? "registered services did not stop" : $"pending={pending}");
        }
    }

    private static async Task StopOneAsync(StopRegistration registration, CancellationToken cancellationToken)
    {
        try
        {
            await registration.Stop(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException($"shutdown-{registration.Name}", ex);
        }
    }

    private async Task DrainTrackedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TrackedTask[] pending;
            lock (gate)
                pending = trackedTasks.Values.Where(item => !item.Task.IsCompleted).ToArray();
            if (pending.Length == 0) return;

            var completions = pending.Select(item => item.Task.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
            await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ObserveTrackedAsync(long id, Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.IsStopping)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException($"background-{operation}", ex);
        }
        finally
        {
            lock (gate)
                trackedTasks.Remove(id);
        }
    }
}
