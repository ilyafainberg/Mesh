namespace Mesh.App.Services;

/// <summary>
/// Serialised, coalescing profile-persistence writer that keeps SQLite/profile work off the
/// caller (UI) thread.
///
/// Guarantees:
///   * <see cref="Schedule"/> only stores the latest snapshot and wakes a single background
///     worker, so a burst of rapid revisions collapses to at most a few writes and the final
///     write always carries the newest revision.
///   * The save delegate is never invoked concurrently: the worker awaits each save to
///     completion before taking the next snapshot.
///   * <see cref="FlushAsync"/> completes only once every revision scheduled before the call
///     has been processed. It waits on explicit completion signalling rather than polling.
///   * A failed save is surfaced through <see cref="FlushAsync"/> (wrapped) and recorded in
///     <see cref="LastError"/>; a later successful revision clears it.
///   * <see cref="DisposeAsync"/> flushes all scheduled work before releasing resources.
///     A stuck write is cancelled only after the disposal timeout and is surfaced as a failure.
///   * <see cref="Schedule"/> after disposal throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class ProfilePersistenceCoordinator<T> : IAsyncDisposable
{
    private readonly Func<T, CancellationToken, Task> _save;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _disposeTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly object _gate = new();
    private readonly List<(long Target, TaskCompletionSource Tcs)> _waiters = new();
    private readonly Task _worker;

    private T? _pendingSnapshot;
    private long _pendingRevision;
    private bool _hasPending;
    private long _scheduledRevision;
    private long _writtenRevision;
    private volatile Exception? _lastError;
    private int _disposed;

    public ProfilePersistenceCoordinator(
        Func<T, CancellationToken, Task> save,
        TimeSpan debounce,
        TimeSpan? disposeTimeout = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _debounce = debounce < TimeSpan.Zero ? TimeSpan.Zero : debounce;
        _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(10);
        if (_disposeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(disposeTimeout), "Dispose timeout must be positive.");
        _worker = Task.Factory.StartNew(RunWorkerAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>The last save failure, or null once a subsequent revision has been saved.</summary>
    public Exception? LastError => _lastError;

    /// <summary>Records the latest snapshot to persist and wakes the background worker.</summary>
    public void Schedule(T snapshot)
    {
        lock (_gate)
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(ProfilePersistenceCoordinator<T>));

            _scheduledRevision++;
            _pendingSnapshot = snapshot;
            _pendingRevision = _scheduledRevision;
            _hasPending = true;
        }

        _signal.Release();
    }

    /// <summary>
    /// Completes once all revisions scheduled before this call have been processed.
    /// Throws if the most recent processing left an unrecovered failure.
    /// </summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            long target = _scheduledRevision;
            if (_writtenRevision >= target)
                return ThrowIfFailed();

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, tcs));
        }

        return AwaitFlushAsync(tcs, ct);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
        }

        Exception? disposeFailure = null;
        using var timeout = new CancellationTokenSource(_disposeTimeout);
        try
        {
            await FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            disposeFailure = new TimeoutException(
                $"Profile persistence did not finish within {_disposeTimeout}.", ex);
            _lastError = disposeFailure;
        }
        catch (Exception ex)
        {
            disposeFailure = ex;
        }
        finally
        {
            _cts.Cancel();
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected after the flush has completed or timed out.
            }
            catch (Exception ex)
            {
                disposeFailure ??= ex;
            }

            CompleteAllWaiters(disposeFailure);
            _cts.Dispose();
            _signal.Dispose();
        }

        if (disposeFailure is not null)
            throw new InvalidOperationException(
                "Profile persistence failed during disposal.", disposeFailure);
    }

    private async Task RunWorkerAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                DrainSignals();

                if (_debounce > TimeSpan.Zero)
                    await Task.Delay(_debounce, ct).ConfigureAwait(false);

                // Absorb any revisions that arrived during the debounce window.
                DrainSignals();

                var pending = TakePending();
                if (pending is null)
                    continue;

                await ProcessAsync(pending.Value, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal requested. Remaining queued work is flushed by DisposeAsync.
        }
    }

    private async Task ProcessAsync((T Snapshot, long Revision) item, CancellationToken ct)
    {
        try
        {
            await _save(item.Snapshot, ct).ConfigureAwait(false);
            _lastError = null;
            AdvanceWritten(item.Revision);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // In-flight save cancelled by disposal. Do not advance; propagate to end the loop.
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex;
            AdvanceWritten(item.Revision);
        }
    }

    private void DrainSignals()
    {
        while (_signal.Wait(0))
        {
            // Collapse multiple wake signals into the single coalesced pending snapshot.
        }
    }

    private (T Snapshot, long Revision)? TakePending()
    {
        lock (_gate)
        {
            if (!_hasPending)
                return null;

            _hasPending = false;
            return (_pendingSnapshot!, _pendingRevision);
        }
    }

    private void AdvanceWritten(long revision)
    {
        List<TaskCompletionSource> ready = new();
        lock (_gate)
        {
            if (revision > _writtenRevision)
                _writtenRevision = revision;

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Target <= _writtenRevision)
                {
                    ready.Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var tcs in ready)
            tcs.TrySetResult();
    }

    private void CompleteAllWaiters(Exception? disposalFailure)
    {
        List<(long Target, TaskCompletionSource Tcs)> remaining;
        long written;
        lock (_gate)
        {
            remaining = _waiters.ToList();
            _waiters.Clear();
            written = _writtenRevision;
        }

        foreach (var waiter in remaining)
        {
            if (waiter.Target <= written)
            {
                waiter.Tcs.TrySetResult();
                continue;
            }

            waiter.Tcs.TrySetException(disposalFailure
                ?? new ObjectDisposedException(
                    nameof(ProfilePersistenceCoordinator<T>),
                    "Profile persistence stopped before the requested revision was written."));
        }
    }

    private Task ThrowIfFailed()
    {
        var err = _lastError;
        return err is null
            ? Task.CompletedTask
            : Task.FromException(
                new InvalidOperationException("Profile persistence failed.", err));
    }

    private async Task AwaitFlushAsync(TaskCompletionSource waiter, CancellationToken ct)
    {
        try
        {
            await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                _waiters.RemoveAll(item => ReferenceEquals(item.Tcs, waiter));
            throw;
        }

        var err = _lastError;
        if (err is not null)
            throw new InvalidOperationException("Profile persistence failed.", err);
    }
}
