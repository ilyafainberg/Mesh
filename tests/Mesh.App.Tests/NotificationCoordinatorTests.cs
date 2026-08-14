using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class NotificationCoordinatorTests
{
    [TestMethod]
    public async Task CommittedActivity_ShowsOnceAndKeepsUnreadAttention()
    {
        var state = new RecordingState();
        var notifier = new RecordingNotifier();
        var coordinator = Create(state, notifier, foreground: false);
        var activity = Activity("message:1", NotificationKind.Message, "alice", "alice");

        await coordinator.OnCommittedActivityAsync(activity);
        await coordinator.OnCommittedActivityAsync(activity);

        Assert.AreEqual(1, notifier.Shown.Count);
        Assert.AreEqual(activity.StableId, notifier.Shown[0].StableId);
        Assert.AreEqual(1, state.GetUnreadNotificationCount());
        CollectionAssert.Contains(notifier.Badges, 1);
    }

    [TestMethod]
    public async Task VisibleTopic_ReadsAssociatedPromptWithoutBanner()
    {
        var state = new RecordingState();
        var notifier = new RecordingNotifier();
        var lifecycle = new TestLifecycle { IsForeground = true };
        var views = new NotificationViewState(lifecycle);
        views.SetVisibleEntities("topic-page", new[] { "topic-1" });
        var coordinator = Create(state, notifier, views);
        var activity = Activity(
            "ask:1",
            NotificationKind.DecisionRequired,
            "prompt-1",
            "topic-1");

        await coordinator.OnCommittedActivityAsync(activity);

        Assert.AreEqual(0, notifier.Shown.Count);
        CollectionAssert.Contains(notifier.Removed, activity.StableId);
        Assert.AreEqual(0, state.GetUnreadNotificationCount());
    }

    [TestMethod]
    public async Task OwnerCopyAndRemoteAlertSuppressLocalBanner()
    {
        var state = new RecordingState { LocalHandle = "alice" };
        var notifier = new RecordingNotifier();
        var sessions = new NotificationWakeSession();
        var coordinator = Create(state, notifier, wakeSessions: sessions, foreground: false);
        var ownerCopy = Activity("message:owner", NotificationKind.Message, "bob", "bob") with
        {
            OriginAccount = "@ALICE",
            SuppressOnOriginAccount = true
        };

        await coordinator.OnCommittedActivityAsync(ownerCopy);
        using (sessions.Begin("wake-1", visibleRemoteAlert: true))
            await coordinator.OnCommittedActivityAsync(
                Activity("message:remote", NotificationKind.Message, "carol", "carol"));

        Assert.AreEqual(0, notifier.Shown.Count);
        Assert.IsTrue(state.Read.Contains(ownerCopy.StableId));
        Assert.IsTrue(state.Suppressed.Contains("message:remote"));
        Assert.AreEqual(1, state.GetUnreadNotificationCount());
    }

    [TestMethod]
    public async Task DoNotDisturbSuppressesBannerButPreservesAttention()
    {
        var state = new RecordingState { DoNotDisturb = true };
        var notifier = new RecordingNotifier();
        var coordinator = Create(state, notifier, foreground: false);
        var activity = Activity("topic:run:terminal", NotificationKind.TopicCompleted, "topic", "topic");

        await coordinator.OnCommittedActivityAsync(activity);

        Assert.AreEqual(0, notifier.Shown.Count);
        Assert.IsTrue(state.Suppressed.Contains(activity.StableId));
        Assert.AreEqual(1, state.GetUnreadNotificationCount());
    }

    [TestMethod]
    public async Task Recovery_PublishesPendingDeliveryOnce()
    {
        var state = new RecordingState();
        var notifier = new RecordingNotifier();
        var activity = Activity("message:pending", NotificationKind.Message, "alice", "alice");
        Assert.IsTrue(state.TryRecordNotificationActivity(activity));
        var coordinator = Create(state, notifier, foreground: false);

        await coordinator.RecoverPendingAsync();
        await coordinator.RecoverPendingAsync();

        Assert.AreEqual(1, notifier.Shown.Count);
        Assert.AreEqual(activity.StableId, notifier.Shown[0].StableId);
        Assert.AreEqual(1, state.GetUnreadNotificationCount());
    }

    private static NotificationCoordinator Create(
        RecordingState state,
        RecordingNotifier notifier,
        NotificationViewState? views = null,
        NotificationWakeSession? wakeSessions = null,
        bool foreground = true)
        => new(
            state,
            notifier,
            views ?? new NotificationViewState(new TestLifecycle { IsForeground = foreground }),
            wakeSessions ?? new NotificationWakeSession(),
            NullLogger<NotificationCoordinator>.Instance);

    private static CommittedActivity Activity(
        string id,
        NotificationKind kind,
        string entityId,
        string conversationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new CommittedActivity(
            id,
            "event:" + id,
            kind,
            entityId,
            conversationId,
            kind == NotificationKind.DecisionRequired
                ? NotificationRoutes.Ask(conversationId, entityId)
                : NotificationRoutes.Messages(conversationId),
            "Title",
            "Body",
            now,
            now,
            IsHistorical: false,
            NotifyRequested: true,
            OriginAccount: "bob");
    }

    private sealed class TestLifecycle : IAppLifecycleState
    {
        public bool IsForeground { get; init; }
        public event Action<bool>? ForegroundChanged { add { } remove { } }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<LocalNotification> Shown { get; } = new();
        public List<string> Removed { get; } = new();
        public List<int> Badges { get; } = new();

        public Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default)
        {
            Shown.Add(notification);
            return Task.FromResult(true);
        }

        public Task RemoveAsync(string stableId, CancellationToken ct = default)
        {
            Removed.Add(stableId);
            return Task.CompletedTask;
        }

        public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetBadgeAsync(int count, CancellationToken ct = default)
        {
            Badges.Add(count);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingState : INotificationState
    {
        private readonly Dictionary<string, CommittedActivity> activities = new(StringComparer.Ordinal);
        private readonly HashSet<string> attention = new(StringComparer.Ordinal);
        private readonly HashSet<string> pending = new(StringComparer.Ordinal);
        public HashSet<string> Suppressed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Read { get; } = new(StringComparer.Ordinal);
        public string LocalHandle { get; set; } = "owner";
        public bool DoNotDisturb { get; set; }
        public NotificationPreviewMode NotificationPreview { get; set; } = NotificationPreviewMode.Always;
        public bool NotificationSound { get; set; } = true;
        public bool Muted { get; set; }

        public bool TryRecordNotificationActivity(CommittedActivity activity)
        {
            if (!activities.TryAdd(activity.StableId, activity)) return false;
            if (!activity.IsHistorical && activity.NotifyRequested)
            {
                attention.Add(activity.StableId);
                pending.Add(activity.StableId);
            }
            return true;
        }

        public void MarkNotificationBannerShown(string stableId) => pending.Remove(stableId);
        public void MarkNotificationSuppressed(string stableId)
        {
            pending.Remove(stableId);
            Suppressed.Add(stableId);
        }
        public void MarkNotificationRead(string stableId)
        {
            pending.Remove(stableId);
            attention.Remove(stableId);
            Read.Add(stableId);
        }

        public IReadOnlyList<string> MarkNotificationEntityRead(string entityId)
        {
            var ids = activities.Values
                .Where(activity => attention.Contains(activity.StableId)
                                   && (string.Equals(activity.EntityId, entityId, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(activity.ConversationId, entityId, StringComparison.OrdinalIgnoreCase)))
                .Select(activity => activity.StableId)
                .ToArray();
            foreach (var id in ids) MarkNotificationRead(id);
            return ids;
        }

        public IReadOnlyList<string> MarkNotificationKindRead(NotificationKind kind)
        {
            var ids = activities.Values
                .Where(activity => attention.Contains(activity.StableId) && activity.Kind == kind)
                .Select(activity => activity.StableId)
                .ToArray();
            foreach (var id in ids) MarkNotificationRead(id);
            return ids;
        }

        public CommittedActivity? GetPendingNotificationActivity(string stableId)
            => pending.Contains(stableId) && activities.TryGetValue(stableId, out var activity)
                ? activity
                : null;

        public IReadOnlyList<CommittedActivity> ListPendingNotificationActivities(int limit)
            => activities.Values
                .Where(activity => pending.Contains(activity.StableId))
                .Take(limit)
                .ToArray();

        public int GetUnreadNotificationCount() => attention.Count;
        public string? GetHighestPriorityNotificationRoute()
            => activities.Values.LastOrDefault(activity => attention.Contains(activity.StableId))?.Route;
        public bool IsNotificationEntityMuted(string entityId) => Muted;
    }
}
