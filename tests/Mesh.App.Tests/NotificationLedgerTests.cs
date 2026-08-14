using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Services;
using Mesh.Shared;

namespace Mesh.App.Tests;

[TestClass]
public sealed class NotificationLedgerTests
{
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        databasePath = Path.Combine(
            Path.GetTempPath(), $"mesh-notifications-{Guid.NewGuid():n}.db");
        key = RandomNumberGenerator.GetBytes(32);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        CryptographicOperations.ZeroMemory(key);
    }

    [TestMethod]
    public void Ledger_DeduplicatesPersistsReadStateAndPrioritizesDecisions()
    {
        using var db = MeshDb.Open(databasePath, key);
        var now = DateTimeOffset.UtcNow;
        var message = Activity(
            "msg:1", NotificationKind.Message, "conversation",
            NotificationRoutes.Messages("conversation"), now);
        var decision = Activity(
            "ask:1", NotificationKind.DecisionRequired, "prompt",
            NotificationRoutes.Ask("topic", "prompt"), now.AddSeconds(1));

        Assert.IsTrue(db.RecordNotificationActivity(message));
        Assert.IsFalse(db.RecordNotificationActivity(message));
        Assert.IsTrue(db.RecordNotificationActivity(decision));
        Assert.AreEqual(2, db.GetUnreadNotificationCount());
        Assert.AreEqual(decision.Route, db.GetHighestPriorityPendingNotification()?.Route);

        db.MarkNotificationRead(decision.StableId);
        Assert.AreEqual(1, db.GetUnreadNotificationCount());
        CollectionAssert.AreEqual(
            new[] { message.StableId },
            db.MarkNotificationEntityRead(message.EntityId).ToArray());
        Assert.AreEqual(0, db.GetUnreadNotificationCount());
    }

    [TestMethod]
    public void Ledger_HistoricalAndSuppressedActivitiesNeverBecomeUnread()
    {
        using var db = MeshDb.Open(databasePath, key);
        var now = DateTimeOffset.UtcNow;

        Assert.IsTrue(db.RecordNotificationActivity(
            Activity("history", NotificationKind.Message, "conversation",
                NotificationRoutes.Messages("conversation"), now)
            with { IsHistorical = true }));
        Assert.IsTrue(db.RecordNotificationActivity(
            Activity("suppressed", NotificationKind.Message, "conversation",
                NotificationRoutes.Messages("conversation"), now)
            with { NotifyRequested = false }));

        Assert.AreEqual(0, db.GetUnreadNotificationCount());
        Assert.IsNull(db.GetHighestPriorityPendingNotification());
    }

    [TestMethod]
    public void Ledger_RestartPreservesDedupAndTopicReadClearsPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = Activity(
            "ask:restart",
            NotificationKind.DecisionRequired,
            "prompt-1",
            NotificationRoutes.Ask("topic-1", "prompt-1"),
            now,
            conversationId: "topic-1");

        using (var db = MeshDb.Open(databasePath, key))
            Assert.IsTrue(db.RecordNotificationActivity(decision));

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            Assert.IsFalse(reopened.RecordNotificationActivity(decision));
            Assert.AreEqual(1, reopened.GetUnreadNotificationCount());
            CollectionAssert.AreEqual(
                new[] { decision.StableId },
                reopened.MarkNotificationEntityRead("topic-1").ToArray());
            Assert.AreEqual(0, reopened.GetUnreadNotificationCount());
        }
    }

    [TestMethod]
    public void Ledger_RestartExposesOnlyUnscheduledPendingDelivery()
    {
        var pending = Activity(
            "message:pending",
            NotificationKind.Message,
            "conversation",
            NotificationRoutes.Messages("conversation"),
            DateTimeOffset.UtcNow);

        using (var db = MeshDb.Open(databasePath, key))
            Assert.IsTrue(db.RecordNotificationActivity(pending));

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual(pending, reopened.GetPendingNotificationActivity(pending.StableId));
            CollectionAssert.AreEqual(
                new[] { pending },
                reopened.ListPendingNotificationActivities(16).ToArray());
            reopened.MarkNotificationBannerShown(pending.StableId);
            Assert.IsNull(reopened.GetPendingNotificationActivity(pending.StableId));
            Assert.AreEqual(0, reopened.ListPendingNotificationActivities(16).Count);
        }
    }

    [TestMethod]
    public void DeferredTopicUpdates_AreDurableAndTerminalDeltasAreNotDeferred()
    {
        var update = new TopicRunUpdatePayload(
            "run-1",
            "topic-1",
            TopicRunPhase.Executing,
            Timestamp: DateTimeOffset.UtcNow,
            DeltaSeq: 2,
            DeltaKind: TopicRunDeltaKind.Answer,
            Delta: "partial");
        using (var db = MeshDb.Open(databasePath, key))
            db.SaveDeferredTopicRunUpdate("envelope-1", update, DateTimeOffset.UtcNow);

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            var deferred = reopened.ListDeferredTopicRunUpdates();
            Assert.AreEqual(1, deferred.Count);
            Assert.AreEqual(update, deferred[0].Update);
            reopened.DeleteDeferredTopicRunUpdates(update.RunId);
            Assert.AreEqual(0, reopened.ListDeferredTopicRunUpdates().Count);
        }

        Assert.IsFalse(TopicRunBackgroundPolicy.ShouldDefer(
            update with { Phase = TopicRunPhase.Completed }));
    }

    private static CommittedActivity Activity(
        string stableId,
        NotificationKind kind,
        string entityId,
        string route,
        DateTimeOffset at,
        string? conversationId = null)
        => new(
            stableId, $"event:{stableId}", kind, entityId, conversationId ?? entityId,
            route, "Title", "Body", at, at, false, true, "alice");
}
