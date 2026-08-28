namespace Mesh.App.Services;

/// <summary>Static seam used by AppState and native callbacks without introducing DI cycles.</summary>
public static class NotificationCoordinatorBridge
{
    private static INotificationCoordinator? coordinator;

    public static void Register(INotificationCoordinator value)
        => Volatile.Write(ref coordinator, value ?? throw new ArgumentNullException(nameof(value)));

    public static async Task PublishAsync(CommittedActivity activity, CancellationToken ct = default)
    {
        var current = Volatile.Read(ref coordinator);
        if (current is null) return;
        try
        {
            await current.OnCommittedActivityAsync(activity, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("notification-publish", ex);
            System.Diagnostics.Debug.WriteLine(ex);
            throw;
        }
    }

    public static Task MarkEntityReadAsync(string entityId, CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.MarkEntityReadAsync(entityId, ct) ?? Task.CompletedTask;

    public static Task MarkKindReadAsync(NotificationKind kind, CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.MarkKindReadAsync(kind, ct) ?? Task.CompletedTask;

    public static Task RefreshBadgeAsync(CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.RefreshBadgeAsync(ct) ?? Task.CompletedTask;

    public static Task RecoverPendingAsync(CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.RecoverPendingAsync(ct) ?? Task.CompletedTask;

    public static Task ResetForAccountAsync(CancellationToken ct = default)
        => Volatile.Read(ref coordinator)?.ResetForAccountAsync(ct) ?? Task.CompletedTask;

    public static void MarkEntityRead(string entityId)
        => Observe(MarkEntityReadAsync(entityId));

    public static void MarkKindRead(NotificationKind kind)
        => Observe(MarkKindReadAsync(kind));

    public static void RefreshBadge()
        => Observe(RefreshBadgeAsync());

    public static void RecoverPending()
        => Observe(RecoverPendingAsync());

    public static void ResetForAccount()
        => Observe(ResetForAccountAsync());

    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static completed =>
            {
                var failure = completed.Exception?.GetBaseException();
                if (failure is null) return;
                RuntimeDiagnostics.Current?.RecordException("notification-background", failure);
                System.Diagnostics.Debug.WriteLine(failure);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}

public static class NotificationWakeSessionBridge
{
    private static NotificationWakeSession? sessions;

    public static void Register(NotificationWakeSession value)
        => Volatile.Write(ref sessions, value ?? throw new ArgumentNullException(nameof(value)));

    public static IDisposable Begin(string? wakeId, bool visibleRemoteAlert)
        => Volatile.Read(ref sessions)?.Begin(wakeId, visibleRemoteAlert) ?? EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}

public static class NotificationNavigationBridge
{
    private static Func<CancellationToken, Task<string?>>? routeResolver;

    public static void Register(Func<CancellationToken, Task<string?>> resolver)
        => Volatile.Write(ref routeResolver, resolver ?? throw new ArgumentNullException(nameof(resolver)));

    public static async Task OpenHighestPriorityAfterSyncAsync(CancellationToken ct = default)
    {
        await OnlineReplicationWakeBridge
            .SynchronizePendingAsync(TimeSpan.FromSeconds(25), ct)
            .ConfigureAwait(false);
        var resolver = Volatile.Read(ref routeResolver);
        var route = resolver is null ? null : await resolver(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(route)) DeepLinkDispatch.Dispatch(route);
    }
}

public static class PushRegistrationBridge
{
    private static Func<CancellationToken, Task>? register;

    public static void Register(Func<CancellationToken, Task> callback)
        => Volatile.Write(ref register, callback ?? throw new ArgumentNullException(nameof(callback)));

    public static Task RegisterCurrentTokenAsync(CancellationToken ct = default)
        => Volatile.Read(ref register)?.Invoke(ct) ?? Task.CompletedTask;
}
