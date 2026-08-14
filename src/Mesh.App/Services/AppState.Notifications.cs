namespace Mesh.App.Services;

public sealed partial class AppState : INotificationState
{
    internal bool TryRecordNotificationActivity(CommittedActivity activity)
    {
        lock (profileSyncGate) return activeDb?.RecordNotificationActivity(activity) == true;
    }

    internal void MarkNotificationBannerShown(string stableId)
    {
        lock (profileSyncGate) activeDb?.MarkNotificationBannerShown(stableId);
    }

    internal void MarkNotificationSuppressed(string stableId)
    {
        lock (profileSyncGate) activeDb?.MarkNotificationSuppressed(stableId);
    }

    internal void MarkNotificationRead(string stableId)
    {
        lock (profileSyncGate) activeDb?.MarkNotificationRead(stableId);
    }

    internal IReadOnlyList<string> MarkNotificationEntityRead(string entityId)
    {
        lock (profileSyncGate)
            return activeDb?.MarkNotificationEntityRead(entityId) ?? Array.Empty<string>();
    }

    internal IReadOnlyList<string> MarkNotificationKindRead(NotificationKind kind)
    {
        lock (profileSyncGate)
            return activeDb?.MarkNotificationKindRead(kind) ?? Array.Empty<string>();
    }

    internal CommittedActivity? GetPendingNotificationActivity(string stableId)
    {
        lock (profileSyncGate) return activeDb?.GetPendingNotificationActivity(stableId);
    }

    internal IReadOnlyList<CommittedActivity> ListPendingNotificationActivities(int limit)
    {
        lock (profileSyncGate)
            return activeDb?.ListPendingNotificationActivities(limit) ?? Array.Empty<CommittedActivity>();
    }

    internal int GetUnreadNotificationCount()
    {
        lock (profileSyncGate) return activeDb?.GetUnreadNotificationCount() ?? 0;
    }

    internal string? GetHighestPriorityNotificationRoute()
    {
        lock (profileSyncGate) return activeDb?.GetHighestPriorityPendingNotification()?.Route;
    }

    internal bool IsNotificationEntityMuted(string entityId)
        => FindContact(entityId)?.Muted == true;

    internal string TopicTitle(string threadId)
        => Profile.OwnThreads.FirstOrDefault(thread =>
               string.Equals(thread.Id, threadId, StringComparison.Ordinal))?.Title
           ?? "Mesh topic";

    string INotificationState.LocalHandle => Profile.Handle;
    bool INotificationState.DoNotDisturb => Profile.DoNotDisturb;
    Domain.NotificationPreviewMode INotificationState.NotificationPreview => Profile.NotificationPreview;
    bool INotificationState.NotificationSound => Profile.NotificationSound;
    bool INotificationState.TryRecordNotificationActivity(CommittedActivity activity)
        => TryRecordNotificationActivity(activity);
    void INotificationState.MarkNotificationBannerShown(string stableId)
        => MarkNotificationBannerShown(stableId);
    void INotificationState.MarkNotificationSuppressed(string stableId)
        => MarkNotificationSuppressed(stableId);
    void INotificationState.MarkNotificationRead(string stableId) => MarkNotificationRead(stableId);
    IReadOnlyList<string> INotificationState.MarkNotificationEntityRead(string entityId)
        => MarkNotificationEntityRead(entityId);
    IReadOnlyList<string> INotificationState.MarkNotificationKindRead(NotificationKind kind)
        => MarkNotificationKindRead(kind);
    CommittedActivity? INotificationState.GetPendingNotificationActivity(string stableId)
        => GetPendingNotificationActivity(stableId);
    IReadOnlyList<CommittedActivity> INotificationState.ListPendingNotificationActivities(int limit)
        => ListPendingNotificationActivities(limit);
    int INotificationState.GetUnreadNotificationCount() => GetUnreadNotificationCount();
    string? INotificationState.GetHighestPriorityNotificationRoute() => GetHighestPriorityNotificationRoute();
    bool INotificationState.IsNotificationEntityMuted(string entityId) => IsNotificationEntityMuted(entityId);
}
