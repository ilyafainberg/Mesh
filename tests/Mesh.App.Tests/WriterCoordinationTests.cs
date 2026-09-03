using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WriterCoordinationTests
{
    [TestMethod]
    public async Task ConcurrentWriterCategories_ShareOnePerDatabaseCoordinator()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "mesh-writer-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.meshdb");
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            using var db = MeshDb.Open(path, key);
            var now = DateTimeOffset.UtcNow;
            var request = new TopicRunRequestPayload(
                "run-coordinated", "topic-coordinated", "line-coordinated", "owner",
                "coordinate", now, "device", TopicTurnMode.Single);
            var inbound = new MeshDb.InboundTopicRunItem(
                request.RunId, "source-device", request,
                InboundTopicRunStates.Accepted, now, now);
            var outbox = new MeshDb.TopicOutboxItem(
                request.RunId, request.ThreadId, request.TriggerLineId, request.TargetDeviceId,
                request, Array.Empty<ChatAttachment>(), TopicOutboxStates.Pending, now, now);
            var notification = new CommittedActivity(
                "notification-coordinated", "event-coordinated", NotificationKind.TopicCompleted,
                request.ThreadId, null, NotificationRoutes.Topic(request.ThreadId),
                "Completed", "Done", now, now, false, true, "owner");

            var writes = new Func<int, bool>[]
            {
                index =>
                {
                    db.UpsertOwnThread(
                        request.ThreadId, "Coordinated", now, index, now.AddTicks(index));
                    return true;
                },
                index =>
                {
                    db.SetTopicDraft(request.ThreadId, $"draft-{index}");
                    return true;
                },
                _ =>
                {
                    db.UpsertTopicOutbox(outbox);
                    return true;
                },
                _ => db.TryAddInboundTopicRun(inbound),
                _ => db.RecordNotificationActivity(notification),
                _ =>
                {
                    db.EnsureLocalOrigin("origin-device", "epoch", 1);
                    return true;
                },
                _ => db.PruneInboundTopicRuns(now.AddDays(-7)) >= 0
            };

            var tasks = Enumerable.Range(0, 70)
                .Select(index => db.ExecuteDurableWriteAsync(
                    () => writes[index % writes.Length](index)))
                .ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.AreEqual(1, db.MaxConcurrentCoordinatedWriters);
            Assert.IsTrue(db.CoordinatedWriteCount >= tasks.Length + 1);
            Assert.IsTrue(db.GetTopicDraft(request.ThreadId).StartsWith("draft-", StringComparison.Ordinal));
            Assert.IsNotNull(db.GetTopicOutbox(request.RunId));
            Assert.HasCount(
                1,
                db.ListInboundTopicRuns(InboundTopicRunStates.Accepted)
                    .Where(item => item.RunId == request.RunId).ToList());
            Assert.AreEqual(1, db.GetUnreadNotificationCount());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DurableThenJournalLockOrder_CompletesWithoutCycle()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "mesh-lock-order-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.meshdb");
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            using var db = MeshDb.Open(path, key);
            var journal = db.ExecuteJournalWriteAsync(() =>
            {
                db.EnsureLocalOrigin("origin", "epoch", 1);
                Thread.Sleep(40);
            });
            var ordinary = db.ExecuteDurableWriteAsync(
                () => db.SetTopicDraft("topic", "ordinary"));

            await Task.WhenAll(journal, ordinary).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("ordinary", db.GetTopicDraft("topic"));
            Assert.AreEqual(1, db.MaxConcurrentCoordinatedWriters);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
