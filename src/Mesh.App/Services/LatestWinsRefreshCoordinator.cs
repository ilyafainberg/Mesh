namespace Mesh.App.Services;

public sealed class LatestWinsRefreshCoordinator : IDisposable
{
    private sealed record RefreshRequest(
        Func<CancellationToken, Task> Work,
        Action<Exception>? OnFailure);

    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private RefreshRequest? latest;
    private TaskCompletionSource idle = CompletedSignal();
    private bool running;
    private bool disposed;

    public LatestWinsRefreshCoordinator()
    {
        lifetimeToken = lifetime.Token;
    }

    public bool Request(
        Func<CancellationToken, Task> work,
        Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (gate)
        {
            if (disposed) return false;
            latest = new RefreshRequest(work, onFailure);
            if (running) return false;

            running = true;
            idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(DrainAsync);
            return true;
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (gate)
            completion = idle.Task;
        return completion.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        var disposeLifetime = false;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            latest = null;
            disposeLifetime = !running;
        }
        lifetime.Cancel();
        if (disposeLifetime) lifetime.Dispose();
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            RefreshRequest request;
            lock (gate)
            {
                if (latest is null)
                {
                    running = false;
                    idle.TrySetResult();
                    if (disposed) lifetime.Dispose();
                    return;
                }
                request = latest!;
                latest = null;
            }

            try
            {
                await request.Work(lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                try
                {
                    request.OnFailure?.Invoke(exception);
                }
                catch (Exception callbackException)
                {
                    RuntimeDiagnostics.Current?.RecordEvent(
                        "latest-refresh-callback-failed",
                        $"exception={callbackException.GetType().FullName}");
                }
            }

            lock (gate)
            {
                if (latest is not null && !disposed) continue;
                running = false;
                idle.TrySetResult();
                if (disposed) lifetime.Dispose();
                return;
            }
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
