using System.Collections.Concurrent;

namespace Mesh.App.Services;

public enum UiOperationOutcomeKind
{
    Succeeded,
    TimedOut,
    Cancelled,
    Failed
}

public sealed record UiOperationOutcome<T>(
    UiOperationOutcomeKind Kind,
    T? Result = default,
    Exception? Exception = null);

public enum UiLatestOperationScheduleKind
{
    Started,
    Joined,
    QueuedLatest
}

/// <summary>
/// Starts keyed UI actions off the renderer, keeps repeated taps idempotent, and observes every task.
/// Callback cancellation only suppresses stale UI updates; it never abandons durable work already started.
/// </summary>
public sealed class UiOperationCoordinator
{
    public event Action<string, string>? OperationCompleted;

    private interface ILatestSlot
    {
        Type ResultType { get; }
    }

    private sealed record LatestObserver<T>(
        string ObserverId,
        CallbackReference<T> Callback);

    private sealed class CallbackReference<T>
    {
        private readonly object gate = new();
        private Func<UiOperationOutcome<T>, Task>? callback;
        private CancellationTokenRegistration cancellationRegistration;

        public CallbackReference(
            Func<UiOperationOutcome<T>, Task>? callback,
            CancellationToken cancellationToken)
        {
            this.callback = callback;
            if (!cancellationToken.CanBeCanceled) return;
            var registration = cancellationToken.Register(
                static state => ((CallbackReference<T>)state!).Clear(),
                this);
            lock (gate)
            {
                if (this.callback is null)
                    registration.Dispose();
                else
                    cancellationRegistration = registration;
            }
        }

        public Func<UiOperationOutcome<T>, Task>? Take()
        {
            CancellationTokenRegistration registration;
            Func<UiOperationOutcome<T>, Task>? found;
            lock (gate)
            {
                found = callback;
                callback = null;
                registration = cancellationRegistration;
                cancellationRegistration = default;
            }
            registration.Dispose();
            return found;
        }

        public void Clear() => Take();
    }

    private sealed class LatestRequest<T>(
        string requestId,
        string operation,
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout)
    {
        public string RequestId { get; } = requestId;
        public string Operation { get; } = operation;
        public Func<CancellationToken, Task<T>> Action { get; } = action;
        public TimeSpan Timeout { get; } = timeout;
        public Dictionary<string, LatestObserver<T>> Observers { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed class LatestSlot<T>(LatestRequest<T> current) : ILatestSlot
    {
        public Type ResultType => typeof(T);
        public LatestRequest<T> Current { get; set; } = current;
        public LatestRequest<T>? Pending { get; set; }
    }

    private static readonly TimeSpan DefaultTimeout = Timeout.InfiniteTimeSpan;
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);
    private readonly object latestGate = new();
    private readonly Dictionary<string, ILatestSlot> latest = new(StringComparer.Ordinal);

    public bool IsRunning(string key)
    {
        if (inFlight.ContainsKey(key)) return true;
        lock (latestGate) return latest.ContainsKey(key);
    }

    public UiLatestOperationScheduleKind RunLatest<T>(
        string key,
        string requestId,
        string observerId,
        string operation,
        Func<CancellationToken, Task<T>> action,
        Func<UiOperationOutcome<T>, Task> onOutcome,
        CancellationToken callbackCancellation = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(onOutcome);

        var request = new LatestRequest<T>(
            requestId,
            operation,
            action,
            timeout ?? DefaultTimeout);
        request.Observers[observerId] = new(
            observerId,
            new CallbackReference<T>(onOutcome, callbackCancellation));

        LatestRequest<T>? start = null;
        List<LatestObserver<T>> superseded = [];
        UiLatestOperationScheduleKind result;
        lock (latestGate)
        {
            if (!latest.TryGetValue(key, out var untyped))
            {
                latest[key] = new LatestSlot<T>(request);
                start = request;
                result = UiLatestOperationScheduleKind.Started;
            }
            else
            {
                if (untyped is not LatestSlot<T> slot)
                    throw new InvalidOperationException(
                        $"UI operation '{key}' is already running with result type {untyped.ResultType}.");

                if (string.Equals(
                        slot.Current.RequestId,
                        requestId,
                        StringComparison.Ordinal))
                {
                    ReplaceObserver(
                        slot.Current.Observers,
                        observerId,
                        request.Observers[observerId]);
                    result = UiLatestOperationScheduleKind.Joined;
                }
                else if (slot.Pending is { } pending
                         && string.Equals(
                             pending.RequestId,
                             requestId,
                             StringComparison.Ordinal))
                {
                    ReplaceObserver(
                        pending.Observers,
                        observerId,
                        request.Observers[observerId]);
                    result = UiLatestOperationScheduleKind.Joined;
                }
                else
                {
                    if (slot.Pending is { } replaced)
                        superseded = replaced.Observers.Values.ToList();
                    slot.Pending = request;
                    result = UiLatestOperationScheduleKind.QueuedLatest;
                }
            }
        }

        foreach (var observer in superseded)
            _ = NotifyAsync(
                observer.Callback.Take(),
                new UiOperationOutcome<T>(UiOperationOutcomeKind.Cancelled),
                CancellationToken.None);
        if (start is not null)
            _ = ObserveLatestAsync(key, start);
        return result;
    }

    public bool TryRun<T>(
        string key,
        string operation,
        Func<CancellationToken, Task<T>> action,
        Func<UiOperationOutcome<T>, Task>? onOutcome = null,
        CancellationToken callbackCancellation = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        if (!inFlight.TryAdd(key, 0)) return false;

        var callback = new CallbackReference<T>(onOutcome, callbackCancellation);
        _ = ObserveAsync(
            key,
            operation,
            action,
            callback,
            timeout ?? DefaultTimeout);
        return true;
    }

    private async Task ObserveAsync<T>(
        string key,
        string operation,
        Func<CancellationToken, Task<T>> action,
        CallbackReference<T> callback,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource();
        UiOperationOutcome<T> outcome;
        try
        {
            if (timeout > TimeSpan.Zero)
                timeoutSource.CancelAfter(timeout);

            var result = await Task.Run(async () =>
            {
                using var trace = ManagedOperationDiagnostics.Begin(operation);
                return await action(timeoutSource.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            outcome = new UiOperationOutcome<T>(
                UiOperationOutcomeKind.Succeeded,
                result);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException
                && timeoutSource.IsCancellationRequested)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "ui-operation-timeout",
                    $"operation={operation};timeout_ms={(long)timeout.TotalMilliseconds}");
                outcome = new UiOperationOutcome<T>(UiOperationOutcomeKind.TimedOut);
            }
            else if (exception is OperationCanceledException)
            {
                outcome = new UiOperationOutcome<T>(UiOperationOutcomeKind.Cancelled);
            }
            else
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "ui-operation-failed",
                    $"operation={operation};exception={exception.GetType().FullName}");
                outcome = new UiOperationOutcome<T>(
                    UiOperationOutcomeKind.Failed,
                    Exception: exception);
            }
        }

        inFlight.TryRemove(key, out _);
        await NotifyAsync(callback.Take(), outcome, CancellationToken.None).ConfigureAwait(false);
        SignalCompleted(key, operation);
    }

    private async Task ObserveLatestAsync<T>(
        string key,
        LatestRequest<T> request)
    {
        var outcome = await ExecuteAsync(
                request.Operation,
                request.Action,
                request.Timeout)
            .ConfigureAwait(false);

        List<LatestObserver<T>> observers;
        LatestRequest<T>? next = null;
        lock (latestGate)
        {
            if (!latest.TryGetValue(key, out var untyped)
                || untyped is not LatestSlot<T> slot
                || !ReferenceEquals(slot.Current, request))
                return;

            observers = request.Observers.Values.ToList();
            if (slot.Pending is { } pending)
            {
                slot.Current = pending;
                slot.Pending = null;
                next = pending;
            }
            else
            {
                latest.Remove(key);
            }
        }

        foreach (var observer in observers)
            await NotifyAsync(
                    observer.Callback.Take(),
                    outcome,
                    CancellationToken.None)
                .ConfigureAwait(false);
        SignalCompleted(key, request.Operation);
        if (next is not null)
            _ = ObserveLatestAsync(key, next);
    }

    private void SignalCompleted(string key, string operation)
    {
        var handlers = OperationCompleted;
        if (handlers is null) return;
        foreach (Action<string, string> handler in handlers.GetInvocationList())
            try { handler(key, operation); }
            catch { }
    }

    private static async Task<UiOperationOutcome<T>> ExecuteAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource();
        try
        {
            if (timeout > TimeSpan.Zero)
                timeoutSource.CancelAfter(timeout);
            var result = await Task.Run(async () =>
            {
                using var trace = ManagedOperationDiagnostics.Begin(operation);
                return await action(timeoutSource.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return new(UiOperationOutcomeKind.Succeeded, result);
        }

        catch (Exception exception)
        {
            if (exception is OperationCanceledException
                && timeoutSource.IsCancellationRequested)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "ui-operation-timeout",
                    $"operation={operation};timeout_ms={(long)timeout.TotalMilliseconds}");
                return new(UiOperationOutcomeKind.TimedOut);
            }
            if (exception is OperationCanceledException)
                return new(UiOperationOutcomeKind.Cancelled);

            RuntimeDiagnostics.Current?.RecordEvent(
                "ui-operation-failed",
                $"operation={operation};exception={exception.GetType().FullName}");
            return new(
                UiOperationOutcomeKind.Failed,
                Exception: exception);
        }
    }

    private static void ReplaceObserver<T>(
        Dictionary<string, LatestObserver<T>> observers,
        string observerId,
        LatestObserver<T> replacement)
    {
        if (observers.TryGetValue(observerId, out var existing))
            existing.Callback.Clear();
        observers[observerId] = replacement;
    }

    private static async Task NotifyAsync<T>(
        Func<UiOperationOutcome<T>, Task>? callback,
        UiOperationOutcome<T> outcome,
        CancellationToken callbackCancellation)
    {
        if (callback is null || callbackCancellation.IsCancellationRequested) return;
        try
        {
            await callback(outcome).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callbackCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (callbackCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "ui-operation-callback-failed",
                $"exception={exception.GetType().FullName}");
        }
    }
}
