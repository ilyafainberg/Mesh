using Microsoft.Extensions.Logging;

namespace Mesh.App.Services;

public sealed class NotificationCoordinator(
    INotificationState state,
    INotifier notifier,
    NotificationViewState views,
    ILogger<NotificationCoordinator> logger) : INotificationCoordinator
{
    // Keep account resets, native delivery, removal, and badge updates in invocation order.
    private readonly NotificationOperationGate operationGate = new();

    public Task OnCommittedActivityAsync(
        CommittedActivity activity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return operationGate.RunAsync(
            token => OnCommittedActivityCoreAsync(activity, token), ct);
    }

    private async Task OnCommittedActivityCoreAsync(
        CommittedActivity activity,
        CancellationToken ct)
    {
        if (!state.TryRecordNotificationActivity(activity))
        {
            var pending = state.GetPendingNotificationActivity(activity.StableId);
            if (pending is null)
            {
                if (IsOwnerCopy(activity))
                {
                    state.MarkNotificationRead(activity.StableId);
                    await RefreshBadgeCoreAsync(ct).ConfigureAwait(false);
                }
                logger.LogDebug(
                    "notification duplicate suppressed: id={StableId} kind={Kind}",
                    activity.StableId,
                    activity.Kind);
                return;
            }
            activity = pending;
            logger.LogDebug("notification pending delivery resumed: id={StableId}", activity.StableId);
        }

        await ProcessRecordedActivityAsync(activity, ct).ConfigureAwait(false);
    }

    private async Task ProcessRecordedActivityAsync(
        CommittedActivity activity,
        CancellationToken ct)
    {
        if (IsOwnerCopy(activity))
        {
            state.MarkNotificationRead(activity.StableId);
            await RefreshBadgeCoreAsync(ct).ConfigureAwait(false);
            logger.LogDebug("notification owner copy suppressed: kind={Kind}", activity.Kind);
            return;
        }

        var visibleEntity = views.IsEntityVisible(activity.EntityId)
            ? activity.EntityId
            : activity.ConversationId is not null && views.IsEntityVisible(activity.ConversationId)
                ? activity.ConversationId
                : null;
        if (visibleEntity is not null)
        {
            await MarkEntityReadCoreAsync(visibleEntity, ct).ConfigureAwait(false);
            logger.LogDebug(
                "notification banner suppressed for visible entity: kind={Kind}",
                activity.Kind);
            return;
        }

        await RefreshBadgeCoreAsync(ct).ConfigureAwait(false);
        var muted = state.IsNotificationEntityMuted(activity.ConversationId ?? activity.EntityId);
        if (!NotificationDecisionPolicy.ShouldShowBanner(
                activity,
                state.DoNotDisturb,
                muted,
                entityVisible: false))
        {
            state.MarkNotificationSuppressed(activity.StableId);
            logger.LogDebug(
                "notification policy suppressed banner: kind={Kind} historical={Historical} dnd={Dnd} muted={Muted}",
                activity.Kind,
                activity.IsHistorical,
                state.DoNotDisturb,
                muted);
            return;
        }

        var local = NotificationContentPolicy.Build(
            activity,
            state.NotificationSound);
        var shown = await notifier.ShowAsync(local, ct).ConfigureAwait(false);
        if (shown)
        {
            state.MarkNotificationBannerShown(activity.StableId);
            logger.LogInformation("notification shown: kind={Kind}", activity.Kind);
        }
        else
        {
            state.MarkNotificationSuppressed(activity.StableId);
            logger.LogDebug("notification was not accepted by the platform: kind={Kind}", activity.Kind);
        }
    }

    public Task RecoverPendingAsync(CancellationToken ct = default)
        => operationGate.RunAsync(RecoverPendingCoreAsync, ct);

    private async Task RecoverPendingCoreAsync(CancellationToken ct)
    {
        const int batchSize = 128;
        while (true)
        {
            var pending = state.ListPendingNotificationActivities(batchSize);
            if (pending.Count == 0) break;
            foreach (var activity in pending)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessRecordedActivityAsync(activity, ct).ConfigureAwait(false);
            }
            if (pending.Count < batchSize) break;
        }
        await RefreshBadgeCoreAsync(ct).ConfigureAwait(false);
    }

    public Task MarkEntityReadAsync(string entityId, CancellationToken ct = default)
        => operationGate.RunAsync(token => MarkEntityReadCoreAsync(entityId, token), ct);

    private async Task MarkEntityReadCoreAsync(string entityId, CancellationToken ct)
    {
        foreach (var id in state.MarkNotificationEntityRead(entityId))
            await notifier.RemoveAsync(id, ct).ConfigureAwait(false);
        await RefreshBadgeCoreAsync(ct).ConfigureAwait(false);
    }

    public Task MarkKindReadAsync(NotificationKind kind, CancellationToken ct = default)
        => operationGate.RunAsync(token => MarkKindReadCoreAsync(kind, token), ct);

    private async Task MarkKindReadCoreAsync(NotificationKind kind, CancellationToken ct)
    {
        foreach (var id in state.MarkNotificationKindRead(kind))
            await notifier.RemoveAsync(id, ct).ConfigureAwait(false);
        await RefreshBadgeCoreAsync(ct).ConfigureAwait(false);
    }

    public Task ClearEntityAsync(string entityId, CancellationToken ct = default)
        => MarkEntityReadAsync(entityId, ct);

    public Task RefreshBadgeAsync(CancellationToken ct = default)
        => operationGate.RunAsync(RefreshBadgeCoreAsync, ct);

    private Task RefreshBadgeCoreAsync(CancellationToken ct)
        => notifier.SetBadgeAsync(state.GetUnreadNotificationCount(), ct);

    public Task ResetForAccountAsync(CancellationToken ct = default)
        => operationGate.RunAsync(ResetForAccountCoreAsync, ct);

    private async Task ResetForAccountCoreAsync(CancellationToken ct)
    {
        await notifier.ClearAllAsync(ct).ConfigureAwait(false);
        await RecoverPendingCoreAsync(ct).ConfigureAwait(false);
    }

    internal Task<string?> GetHighestPriorityRouteAsync(CancellationToken ct = default)
        => operationGate.RunAsync(
            _ => Task.FromResult(state.GetHighestPriorityNotificationRoute()), ct);

    private static string Normalize(string? handle)
        => (handle ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    private bool IsOwnerCopy(CommittedActivity activity)
    {
        var originAccount = Normalize(activity.OriginAccount);
        return activity.SuppressOnOriginAccount
               && originAccount.Length > 0
               && string.Equals(Normalize(state.LocalHandle), originAccount, StringComparison.Ordinal);
    }

}
