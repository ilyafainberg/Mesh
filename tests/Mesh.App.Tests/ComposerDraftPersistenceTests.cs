using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ComposerDraftPersistenceTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "mesh-composer-draft-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public async Task RapidEdits_CoalesceAndPersistNewestRevision()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(30));

        for (var index = 0; index < 200; index++)
            drafts.Schedule(db, ComposerDraftKind.Topic, "topic", $"draft-{index}");

        await drafts.FlushAsync();

        Assert.AreEqual("draft-199", db.GetTopicDraft("topic"));
        Assert.IsTrue(
            drafts.PersistedWriteCount <= 2,
            $"Expected a coalesced write, observed {drafts.PersistedWriteCount}.");
    }

    [TestMethod]
    public async Task HundredEditsBeforeDeterministicDebounce_PersistExactlyNewestOnce()
    {
        using var db = MeshDb.Open(databasePath, key);
        var time = new ManualTimerTimeProvider();
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMinutes(1),
            time);

        for (var index = 0; index < 100; index++)
            drafts.Schedule(db, ComposerDraftKind.Topic, "topic", $"draft-{index}");

        await time.FirstTimer;
        Assert.AreEqual(0L, drafts.PersistedWriteCount);
        time.Advance(TimeSpan.FromMinutes(1));
        await drafts.FlushAsync();

        Assert.AreEqual("draft-99", db.GetTopicDraft("topic"));
        Assert.AreEqual(1L, drafts.PersistedWriteCount);
    }

    [TestMethod]
    public async Task TimeSpentStaging_DoesNotConsumeDraftDebounce()
    {
        using var db = MeshDb.Open(databasePath, key);
        var time = new ManualTimerTimeProvider();
        var writeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMinutes(1),
            time,
            beforeWrite: (_, _, _) =>
            {
                writeStarted.TrySetResult();
                return Task.CompletedTask;
            },
            afterScheduled: (_, _, _) => time.Advance(TimeSpan.FromMinutes(1)));

        drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "staged");

        var first = await Task.WhenAny(time.FirstTimer, writeStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(
            time.FirstTimer,
            first,
            "The debounce elapsed before Schedule returned from synchronous staging.");
        Assert.AreEqual(0L, drafts.PersistedWriteCount);

        await drafts.FlushAsync();
        Assert.AreEqual("staged", db.GetTopicDraft("topic"));
        Assert.AreEqual(1L, drafts.PersistedWriteCount);
    }

    [TestMethod]
    public async Task TimerWonButNewerEditBeforeWrite_SkipsSupersededGeneration()
    {
        using var db = MeshDb.Open(databasePath, key);
        var time = new ManualTimerTimeProvider();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMinutes(1),
            time,
            async (_, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            });

        drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "old");
        await time.FirstTimer;
        time.Advance(TimeSpan.FromMinutes(1));
        await entered.Task;

        drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "newest");
        release.TrySetResult();
        await drafts.FlushAsync();

        Assert.AreEqual("newest", db.GetTopicDraft("topic"));
        Assert.AreEqual(1L, drafts.PersistedWriteCount);
    }

    [TestMethod]
    public async Task NewerEditDuringActiveWrite_SerializesOldThenNewest()
    {
        var observer = new DraftWriteBarrierObserver();
        using var db = MeshDb.OpenForTesting(databasePath, key, observer);
        var olderRevision = ComposerDraftRevision.New();
        var newerRevision = ComposerDraftRevision.New();
        var newerScheduled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero,
            afterScheduled: (_, _, revision) =>
            {
                if (revision == newerRevision)
                    newerScheduled.TrySetResult();
            });

        drafts.Schedule(
            db,
            ComposerDraftKind.Topic,
            "topic",
            "old",
            olderRevision);
        await observer.Entered.Task;

        var scheduleNewer = Task.Run(() => drafts.Schedule(
            db,
            ComposerDraftKind.Topic,
            "topic",
            "newest",
            newerRevision));
        await newerScheduled.Task;
        observer.Release.TrySetResult();
        await scheduleNewer;
        await drafts.FlushAsync();

        var stored = db.GetTopicDraftState("topic");
        Assert.IsNotNull(stored);
        Assert.AreEqual("newest", stored.Text);
        Assert.AreEqual(newerRevision, stored.Revision);
        Assert.AreEqual(2L, drafts.PersistedWriteCount);
        Assert.AreEqual(1, observer.MaximumConcurrentWrites);
    }

    [TestMethod]
    public async Task ConcurrentFlushAndDispose_DrainAcknowledgedRevision()
    {
        using var db = MeshDb.Open(databasePath, key);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero,
            beforeWrite: async (_, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            });
        var acknowledged = ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
            ComposerDraftKind.Topic,
            "topic",
            "durable",
            () => drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "durable"));
        await entered.Task;

        var flush = drafts.FlushAsync();
        var firstDispose = drafts.DisposeAsync().AsTask();
        var secondDispose = drafts.DisposeAsync().AsTask();
        release.TrySetResult();

        Assert.AreEqual(ComposerDraftMutationResult.Persisted, await acknowledged);
        await Task.WhenAll(flush, firstDispose, secondDispose);
        Assert.AreEqual("durable", db.GetTopicDraft("topic"));
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "late"));
    }

    [TestMethod]
    public async Task ConcurrentAutosaveInboundAndMaintenance_PreserveLatestDraftAndAcceptance()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(2));
        var now = DateTimeOffset.UtcNow;
        var request = new TopicRunRequestPayload(
            "draft-run",
            "draft-topic",
            "draft-line",
            "owner",
            "Persist concurrently",
            now,
            "desktop-device",
            TopicTurnMode.Single);
        var inbound = new MeshDb.InboundTopicRunItem(
            request.RunId,
            "phone-device",
            request,
            InboundTopicRunStates.Accepted,
            now,
            now);

        var maintenance = Enumerable.Range(0, 24)
            .Select(index => db.ExecuteDurableWriteAsync(
                () => index % 2 == 0
                    ? db.TryAddInboundTopicRun(inbound)
                    : db.PruneInboundTopicRuns(now.AddDays(-7)) >= 0))
            .ToArray();
        for (var index = 0; index < 100; index++)
            drafts.Schedule(db, ComposerDraftKind.Topic, request.ThreadId, $"latest-{index}");

        await Task.WhenAll(maintenance);
        await drafts.FlushAsync();

        Assert.AreEqual("latest-99", db.GetTopicDraft(request.ThreadId));
        Assert.IsNotNull(db.GetInboundTopicRun(request.RunId));
        Assert.AreEqual(
            1,
            db.ListInboundTopicRuns(InboundTopicRunStates.Accepted)
                .Count(item => item.RunId == request.RunId));
    }

    [TestMethod]
    public async Task CancelledNavigationWait_DoesNotCancelQueuedDraft()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(40));
        drafts.Schedule(db, ComposerDraftKind.Conversation, "alice", "survive navigation");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => drafts.FlushAsync(cancellation.Token));
        await drafts.FlushAsync();

        Assert.AreEqual("survive navigation", db.GetConversationDraft("alice"));
    }

    [TestMethod]
    public async Task BusyExhaustion_IsNonFatalVisibleAndRetryableWithoutBlockingCaller()
    {
        using var db = MeshDb.Open(databasePath, key);
        ComposerDraftPersistenceFailure? visibleFailure = null;
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            failure => visibleFailure = failure,
            TimeSpan.Zero);

        using (var blocker = OpenRawConnection())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var acknowledgement =
                ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
                    ComposerDraftKind.Topic,
                    "busy-topic",
                    "retained",
                    () => drafts.Schedule(
                        db, ComposerDraftKind.Topic, "busy-topic", "retained"));
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5));

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => drafts.FlushAsync());
            await Assert.ThrowsExactlyAsync<SqliteException>(() => acknowledgement);
            Assert.IsInstanceOfType<SqliteException>(failure.InnerException);
            Assert.IsNotNull(visibleFailure);
            Assert.IsTrue(visibleFailure.Exception is SqliteException);
            Assert.IsTrue(drafts.TryGetLatest(
                db,
                ComposerDraftKind.Topic,
                "busy-topic",
                out var retained));
            Assert.AreEqual("retained", retained);
            Assert.AreEqual("", db.GetTopicDraft("busy-topic"));
            transaction.Commit();
        }

        Assert.IsTrue(drafts.Retry(db, ComposerDraftKind.Topic, "busy-topic"));
        await drafts.FlushAsync();
        Assert.IsNull(visibleFailure);
        Assert.AreEqual("retained", db.GetTopicDraft("busy-topic"));
    }

    [TestMethod]
    public async Task LatestDraft_RecoversAfterCoordinatorAndDatabaseRestart()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            await using var drafts = new ComposerDraftPersistenceCoordinator(_ => { });
            drafts.Schedule(db, ComposerDraftKind.Topic, "restart-topic", "first");
            drafts.Schedule(db, ComposerDraftKind.Topic, "restart-topic", "recovered");
            await drafts.FlushAsync();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        Assert.AreEqual("recovered", reopened.GetTopicDraft("restart-topic"));
    }

    [TestMethod]
    public async Task AwaitedMutation_CompletesOnlyForItsDurableRevision()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(100));

        var acknowledged = ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
            ComposerDraftKind.Topic,
            "topic",
            "",
            () => drafts.Schedule(db, ComposerDraftKind.Topic, "topic", ""));
        Assert.IsFalse(acknowledged.IsCompleted);

        await drafts.FlushAsync();

        Assert.AreEqual(ComposerDraftMutationResult.Persisted, await acknowledged);
        Assert.AreEqual("", db.GetTopicDraft("topic"));
    }

    [TestMethod]
    public async Task AwaitedMutation_IdenticalDurableRevisionAcknowledgesAlreadyPersisted()
    {
        using var db = MeshDb.Open(databasePath, key);
        var snapshot = new MeshDb.TopicComposerSnapshot(
            "durable",
            Array.Empty<MeshDb.ComposerDraftAttachment>(),
            false,
            null,
            "device");
        var revision = ComposerDraftRevision.New();
        await using var initial = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        initial.ScheduleTopicSnapshot(db, "topic", snapshot, revision);
        await initial.FlushAsync();

        await using var retry = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        var acknowledged = ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
            ComposerDraftKind.Topic,
            "topic",
            snapshot.Text,
            () => retry.ScheduleTopicSnapshot(db, "topic", snapshot, revision));

        Assert.AreEqual(
            ComposerDraftMutationResult.AlreadyPersisted,
            await acknowledged);
        Assert.AreEqual(0L, retry.PersistedWriteCount);
        var stored = db.GetTopicDraftState("topic");
        Assert.IsNotNull(stored?.TopicSnapshot);
        Assert.AreEqual(revision, stored.Revision);
        Assert.IsTrue(MeshDb.TopicComposerSnapshotsEqual(
            snapshot,
            stored.TopicSnapshot));
    }

    [TestMethod]
    public async Task AwaitedMutation_EqualRevisionWithDifferentSnapshotIsSuperseded()
    {
        using var db = MeshDb.Open(databasePath, key);
        var revision = ComposerDraftRevision.New();
        var durable = MeshDb.TopicComposerSnapshot.TextOnly("durable");
        var conflicting = durable with { TargetDeviceId = "different-device" };
        await using var initial = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        initial.ScheduleTopicSnapshot(db, "topic", durable, revision);
        await initial.FlushAsync();

        await using var retry = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        var acknowledged = ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
            ComposerDraftKind.Topic,
            "topic",
            conflicting.Text,
            () => retry.ScheduleTopicSnapshot(db, "topic", conflicting, revision));

        Assert.AreEqual(
            ComposerDraftMutationResult.Superseded,
            await acknowledged);
        var stored = db.GetTopicDraftState("topic");
        Assert.IsNotNull(stored?.TopicSnapshot);
        Assert.AreEqual(revision, stored.Revision);
        Assert.IsTrue(MeshDb.TopicComposerSnapshotsEqual(
            durable,
            stored.TopicSnapshot));
    }

    [TestMethod]
    public async Task AwaitedMutation_NewerRevisionReportsSupersededAndPreservesNewerDraft()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(100));

        var clear = ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
            ComposerDraftKind.Topic,
            "topic",
            "",
            () => drafts.Schedule(db, ComposerDraftKind.Topic, "topic", ""));
        drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "newer");

        Assert.AreEqual(ComposerDraftMutationResult.Superseded, await clear);
        await drafts.FlushAsync();
        Assert.AreEqual("newer", db.GetTopicDraft("topic"));
    }

    [TestMethod]
    public async Task AwaitedMutation_CancellationReleasesAwaiterWithoutCancellingPersistence()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        var mutation = ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
            ComposerDraftKind.Topic,
            "topic",
            "survives",
            () => drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "survives"),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => mutation);
        await drafts.FlushAsync();
        Assert.AreEqual("survives", db.GetTopicDraft("topic"));
    }

    [TestMethod]
    public async Task ForgottenPendingDraft_DoesNotResurrectAfterTopicDeletion()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.EnsureOwnThread("deleted-topic", "Delete me", DateTimeOffset.UtcNow);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(100));
        drafts.Schedule(db, ComposerDraftKind.Topic, "deleted-topic", "stale");

        drafts.Forget(db, ComposerDraftKind.Topic, "deleted-topic");
        db.ExecuteDurableWrite(() => db.DeleteOwnThread("deleted-topic"));
        await drafts.FlushAsync();

        Assert.AreEqual("", db.GetTopicDraft("deleted-topic"));
    }

    [TestMethod]
    public async Task FreshDatabase_IdenticalReentryGetsNewRevisionAndOldClearIsSuperseded()
    {
        long submittedRevision;
        using (var first = MeshDb.Open(databasePath, key))
        {
            first.SetTopicDraft("topic", "identical");
            submittedRevision = first.GetTopicDraftState("topic")!.Revision;
        }
        SqliteConnection.ClearAllPools();

        long newerRevision;
        using (var second = MeshDb.Open(databasePath, key))
        {
            second.SetTopicDraft("topic", "identical");
            newerRevision = second.GetTopicDraftState("topic")!.Revision;
            Assert.AreNotEqual(submittedRevision, newerRevision);
        }
        SqliteConnection.ClearAllPools();

        using var recovered = MeshDb.Open(databasePath, key);
        Assert.AreEqual(
            MeshDb.ComposerDraftClearResult.Superseded,
            await recovered.CompareAndClearTopicDraftAsync("topic", submittedRevision));
        var preserved = recovered.GetTopicDraftState("topic");
        Assert.IsNotNull(preserved);
        Assert.AreEqual("identical", preserved.Text);
        Assert.AreEqual(newerRevision, preserved.Revision);
        Assert.AreEqual(
            MeshDb.ComposerDraftClearResult.Cleared,
            await recovered.CompareAndClearTopicDraftAsync("topic", newerRevision));
        Assert.AreEqual(
            MeshDb.ComposerDraftClearResult.Missing,
            await recovered.CompareAndClearTopicDraftAsync("topic", newerRevision));
    }

    [TestMethod]
    public async Task FreshDatabase_NoNewerRevisionClearsAfterRetryAndIsIdempotent()
    {
        long submittedRevision;
        using (var first = MeshDb.Open(databasePath, key))
        {
            first.SetTopicDraft("topic", "pending");
            submittedRevision = first.GetTopicDraftState("topic")!.Revision;
        }
        SqliteConnection.ClearAllPools();

        using var recovered = MeshDb.Open(databasePath, key);
        Assert.AreEqual(
            MeshDb.ComposerDraftClearResult.Cleared,
            await recovered.CompareAndClearTopicDraftAsync("topic", submittedRevision));
        Assert.AreEqual(
            MeshDb.ComposerDraftClearResult.Missing,
            await recovered.CompareAndClearTopicDraftAsync("topic", submittedRevision));
    }

    [TestMethod]
    public async Task OutOfOrderDurableWrites_CannotRegressNewerRevision()
    {
        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        var older = ComposerDraftRevision.New();
        var newer = ComposerDraftRevision.New();

        drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "newer", newer);
        await drafts.FlushAsync();
        drafts.Schedule(db, ComposerDraftKind.Topic, "topic", "older", older);
        await drafts.FlushAsync();

        var stored = db.GetTopicDraftState("topic");
        Assert.IsNotNull(stored);
        Assert.AreEqual("newer", stored.Text);
        Assert.AreEqual(newer, stored.Revision);
    }

    [TestMethod]
    public async Task CompleteSnapshot_IdenticalTextWithPayloadChangesSurvivesRestart()
    {
        var attachmentPath = Path.Combine(directory, "attachment.txt");
        await File.WriteAllTextAsync(attachmentPath, "attachment");
        var attachment = MeshDb.ComposerDraftAttachment.Create(
            "attachment.txt",
            attachmentPath,
            new FileInfo(attachmentPath).Length);
        var first = new MeshDb.TopicComposerSnapshot(
            "identical",
            [attachment],
            false,
            "widget-1",
            "device-1",
            new MeshDb.ComposerDraftWidget(
                "widget-1",
                "Widget one",
                "prompt one",
                "<html>one</html>"));
        var second = first with
        {
            Attachments = Array.Empty<MeshDb.ComposerDraftAttachment>(),
            WidgetId = "widget-2",
            TargetDeviceId = "device-2",
            Widget = new MeshDb.ComposerDraftWidget(
                "widget-2",
                "Widget two",
                "prompt two",
                "<html>two</html>")
        };
        Assert.AreNotEqual(first.Fingerprint, second.Fingerprint);

        long firstRevision;
        long secondRevision;
        using (var db = MeshDb.Open(databasePath, key))
        {
            await using var drafts = new ComposerDraftPersistenceCoordinator(_ => { });
            firstRevision = ComposerDraftRevision.New();
            drafts.ScheduleTopicSnapshot(db, "topic", first, firstRevision);
            await drafts.FlushAsync();
            secondRevision = ComposerDraftRevision.New();
            drafts.ScheduleTopicSnapshot(db, "topic", second, secondRevision);
            await drafts.FlushAsync();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var restored = reopened.GetTopicDraftState("topic");
        Assert.IsNotNull(restored);
        Assert.AreEqual(secondRevision, restored.Revision);
        Assert.AreNotEqual(firstRevision, restored.Revision);
        Assert.IsNotNull(restored.TopicSnapshot);
        Assert.IsTrue(MeshDb.TopicComposerSnapshotsEqual(
            second,
            restored.TopicSnapshot));
    }

    [TestMethod]
    public async Task CompleteSnapshot_EachPayloadEditHasDistinctIdentity()
    {
        var attachmentPath = Path.Combine(directory, "identity.txt");
        await File.WriteAllTextAsync(attachmentPath, "");
        var attachment = MeshDb.ComposerDraftAttachment.Create(
            "identity.txt",
            attachmentPath,
            0);
        var baseline = new MeshDb.TopicComposerSnapshot(
            "same",
            Array.Empty<MeshDb.ComposerDraftAttachment>(),
            false,
            null,
            "device-1");
        var attachmentAdded = baseline with { Attachments = [attachment] };
        var attachmentRemoved = attachmentAdded with
        {
            Attachments = Array.Empty<MeshDb.ComposerDraftAttachment>()
        };
        var widgetChanged = baseline with
        {
            WidgetId = "widget",
            Widget = new MeshDb.ComposerDraftWidget(
                "widget",
                "Widget",
                "prompt",
                "<html>widget</html>")
        };
        var targetChanged = baseline with { TargetDeviceId = "device-2" };

        Assert.AreNotEqual(baseline.Fingerprint, attachmentAdded.Fingerprint);
        Assert.AreEqual(baseline.Fingerprint, attachmentRemoved.Fingerprint);
        Assert.AreNotEqual(baseline.Fingerprint, widgetChanged.Fingerprint);
        Assert.AreNotEqual(baseline.Fingerprint, targetChanged.Fingerprint);
    }

    [TestMethod]
    public async Task FailedR2StageRollsBackAndRetryCommitsWithoutClearingR1()
    {
        using var db = MeshDb.Open(databasePath, key);
        var first = new MeshDb.TopicComposerSnapshot(
            "identical",
            Array.Empty<MeshDb.ComposerDraftAttachment>(),
            false,
            null,
            "device-1");
        var second = first with
        {
            TargetDeviceId = "device-2",
            WidgetId = "widget-2",
            Widget = new MeshDb.ComposerDraftWidget(
                "widget-2",
                "Widget two",
                "prompt two",
                "<html>two</html>")
        };
        var firstRevision = ComposerDraftRevision.New();
        var secondRevision = ComposerDraftRevision.New();
        var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        drafts.ScheduleTopicSnapshot(db, "topic", first, firstRevision);
        await drafts.FlushAsync();

        using (var blocker = new SqliteConnection($"Data Source={databasePath}"))
        {
            blocker.Open();
            using (var keyCommand = blocker.CreateCommand())
            {
                keyCommand.CommandText =
                    $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
                keyCommand.ExecuteNonQuery();
            }
            using var transaction = blocker.BeginTransaction(deferred: false);
            drafts.ScheduleTopicSnapshot(db, "topic", second, secondRevision);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => drafts.FlushAsync());
            Assert.AreEqual(firstRevision, db.GetTopicDraftState("topic")!.Revision);
            transaction.Commit();
        }
        Assert.IsTrue(drafts.Retry(db, ComposerDraftKind.Topic, "topic"));
        await drafts.FlushAsync();
        await drafts.DisposeAsync();

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            var replayed = reopened.GetTopicDraftState("topic");
            Assert.IsNotNull(replayed?.TopicSnapshot);
            Assert.AreEqual(secondRevision, replayed.Revision);
            Assert.AreEqual(second.Fingerprint, replayed.TopicSnapshot.Fingerprint);
        }
        SqliteConnection.ClearAllPools();

        using var reopenedAgain = MeshDb.Open(databasePath, key);
        var stable = reopenedAgain.GetTopicDraftState("topic");
        Assert.IsNotNull(stable?.TopicSnapshot);
        Assert.AreEqual(secondRevision, stable.Revision);
        Assert.AreEqual(second.Fingerprint, stable.TopicSnapshot.Fingerprint);
    }

    [TestMethod]
    public async Task CleanupBarrier_SerializesConcurrentR2Stage()
    {
        var observer = new CleanupBarrierObserver();
        using var db = MeshDb.OpenForTesting(databasePath, key, observer);
        db.SetTopicDraft("topic", "r1");
        var r1 = db.GetTopicDraftState("topic")!.Revision;

        var cleanup = db.CompareAndClearTopicDraftAsync("topic", r1);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var r2Write = Task.Run(() => db.SetTopicDraft("topic", "r2"));
        await Task.Delay(50);
        Assert.IsFalse(r2Write.IsCompleted);

        observer.Release.TrySetResult();
        Assert.AreEqual(MeshDb.ComposerDraftClearResult.Cleared, await cleanup);
        await r2Write;
        Assert.AreEqual("r2", db.GetTopicDraft("topic"));
        Assert.AreNotEqual(r1, db.GetTopicDraftState("topic")!.Revision);
    }

    [TestMethod]
    public async Task AtomicR2FailureAndRepeatedCrashLeaveR1RetryableAcrossRestart()
    {
        long r1;
        using (var seed = MeshDb.Open(databasePath, key))
        {
            seed.SetTopicDraft("topic", "r1");
            r1 = seed.GetTopicDraftState("topic")!.Revision;
        }
        SqliteConnection.ClearAllPools();
        var r2Revision = ComposerDraftRevision.New();
        var r2 = new MeshDb.ComposerDraft(
            "r2",
            r2Revision,
            TopicSnapshot: MeshDb.TopicComposerSnapshot.TextOnly("r2"));

        for (var crash = 0; crash < 2; crash++)
        {
            using var failing = MeshDb.OpenForTesting(
                databasePath,
                key,
                new ThrowBeforeNewerSnapshotObserver());
            await Assert.ThrowsExactlyAsync<TopicSendJournalCrashException>(
                () => failing.ResolveTopicDraftCleanupAsync("topic", r1, r2));
            Assert.AreEqual(r1, failing.GetTopicDraftState("topic")!.Revision);
            Assert.AreEqual("r1", failing.GetTopicDraft("topic"));
            SqliteConnection.ClearAllPools();
        }

        using var recovered = MeshDb.Open(databasePath, key);
        Assert.AreEqual(
            MeshDb.ComposerDraftClearResult.Superseded,
            await recovered.ResolveTopicDraftCleanupAsync("topic", r1, r2));
        Assert.AreEqual(r2Revision, recovered.GetTopicDraftState("topic")!.Revision);
        Assert.AreEqual("r2", recovered.GetTopicDraft("topic"));
    }

    [TestMethod]
    public async Task CompleteSnapshot_OutOfOrderDebounceCannotRegressPayload()
    {
        var attachmentPath = Path.Combine(directory, "ordered.txt");
        await File.WriteAllTextAsync(attachmentPath, "ordered");
        var attachment = MeshDb.ComposerDraftAttachment.Create(
            "ordered.txt",
            attachmentPath,
            new FileInfo(attachmentPath).Length);
        var older = new MeshDb.TopicComposerSnapshot(
            "same",
            [attachment],
            false,
            null,
            "device-1");
        var newer = new MeshDb.TopicComposerSnapshot(
            "same",
            Array.Empty<MeshDb.ComposerDraftAttachment>(),
            true,
            null,
            "device-2");
        var olderRevision = ComposerDraftRevision.New();
        var newerRevision = ComposerDraftRevision.New();

        using var db = MeshDb.Open(databasePath, key);
        await using var drafts = new ComposerDraftPersistenceCoordinator(
            _ => { },
            TimeSpan.Zero);
        drafts.ScheduleTopicSnapshot(db, "topic", newer, newerRevision);
        await drafts.FlushAsync();
        drafts.ScheduleTopicSnapshot(db, "topic", older, olderRevision);
        await drafts.FlushAsync();

        var stored = db.GetTopicDraftState("topic");
        Assert.IsNotNull(stored);
        Assert.AreEqual(newerRevision, stored.Revision);
        Assert.IsNotNull(stored.TopicSnapshot);
        Assert.IsTrue(MeshDb.TopicComposerSnapshotsEqual(
            newer,
            stored.TopicSnapshot));
    }

    [TestMethod]
    public void LegacyDraft_MigratesRevisionDurablyAcrossRestart()
    {
        using (var raw = OpenRawConnection())
        {
            using var create = raw.CreateCommand();
            create.CommandText = """
                CREATE TABLE composer_drafts(
                    kind TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    text TEXT NOT NULL,
                    PRIMARY KEY(kind, entity_id));
                INSERT INTO composer_drafts(kind, entity_id, text)
                VALUES('topic', 'legacy', 'legacy text');
                """;
            create.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        long migratedRevision;
        using (var migrated = MeshDb.Open(databasePath, key))
        {
            var draft = migrated.GetTopicDraftState("legacy");
            Assert.IsNotNull(draft);
            Assert.IsFalse(draft.IsMalformed);
            Assert.IsTrue(draft.Revision > 0);
            migratedRevision = draft.Revision;
        }
        SqliteConnection.ClearAllPools();

        using var restarted = MeshDb.Open(databasePath, key);
        Assert.AreEqual(
            migratedRevision,
            restarted.GetTopicDraftState("legacy")!.Revision);
    }

    [TestMethod]
    public void MalformedRevision_IsConservativelyFencedUntilExplicitEdit()
    {
        using (var db = MeshDb.Open(databasePath, key))
            db.SetTopicDraft("topic", "retained");
        SqliteConnection.ClearAllPools();
        using (var raw = OpenRawConnection())
        {
            using var corrupt = raw.CreateCommand();
            corrupt.CommandText = """
                UPDATE composer_drafts SET revision = 'invalid'
                WHERE kind = 'topic' AND entity_id = 'topic';
                """;
            corrupt.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var recovered = MeshDb.Open(databasePath, key);
        var malformed = recovered.GetTopicDraftState("topic");
        Assert.IsNotNull(malformed);
        Assert.IsTrue(malformed.IsMalformed);
        Assert.AreEqual(0, malformed.Revision);
        recovered.SetTopicDraft("topic", "edited");
        var repaired = recovered.GetTopicDraftState("topic");
        Assert.IsNotNull(repaired);
        Assert.IsFalse(repaired.IsMalformed);
        Assert.IsTrue(repaired.Revision > 0);
    }

    private sealed class CleanupBarrierObserver : MeshDb.IComposerDraftTransactionObserver
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Checkpoint(
            MeshDb.ComposerDraftTransactionCheckpoint checkpoint,
            string threadId,
            long expectedRevision)
        {
            if (checkpoint != MeshDb.ComposerDraftTransactionCheckpoint.CleanupObserved)
                return;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class DraftWriteBarrierObserver :
            MeshDb.IComposerDraftTransactionObserver
        {
            private int active;
            private int maximumConcurrentWrites;
            private int enteredOnce;

            public TaskCompletionSource Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int MaximumConcurrentWrites => Volatile.Read(ref maximumConcurrentWrites);

            public void Checkpoint(
                MeshDb.ComposerDraftTransactionCheckpoint checkpoint,
                string threadId,
                long expectedRevision)
            {
                if (checkpoint != MeshDb.ComposerDraftTransactionCheckpoint.BeforeDraftWrite)
                    return;
                var concurrent = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumConcurrentWrites, concurrent);
                try
                {
                    if (Interlocked.Exchange(ref enteredOnce, 1) == 0)
                    {
                        Entered.TrySetResult();
                        Release.Task.GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        }

        private sealed class ManualTimerTimeProvider : TimeProvider
        {
            private readonly object gate = new();
            private readonly List<ManualTimer> timers = [];
            private long timestamp;

            public TaskCompletionSource FirstTimerSource { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task FirstTimer => FirstTimerSource.Task;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Volatile.Read(ref timestamp);

            public override ITimer CreateTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                lock (gate)
                    timers.Add(timer);
                FirstTimerSource.TrySetResult();
                return timer;
            }

            public void Advance(TimeSpan elapsed)
            {
                Interlocked.Add(ref timestamp, elapsed.Ticks);
                while (true)
                {
                    ManualTimer? due;
                    lock (gate)
                        due = timers.FirstOrDefault(timer => timer.IsDue(timestamp));
                    if (due is null)
                        return;
                    due.Fire(timestamp);
                }
            }

            private sealed class ManualTimer(
                ManualTimerTimeProvider owner,
                TimerCallback callback,
                object? state) : ITimer
            {
                private long dueAt = long.MaxValue;
                private long periodTicks = Timeout.InfiniteTimeSpan.Ticks;
                private int disposed;

                public bool Change(TimeSpan dueTime, TimeSpan period)
                {
                    if (Volatile.Read(ref disposed) != 0)
                        return false;
                    lock (owner.gate)
                    {
                        dueAt = dueTime == Timeout.InfiniteTimeSpan
                            ? long.MaxValue
                            : checked(owner.timestamp + Math.Max(0, dueTime.Ticks));
                        periodTicks = period.Ticks;
                    }
                    return true;
                }

                public bool IsDue(long now)
                    => Volatile.Read(ref disposed) == 0 && dueAt <= now;

                public void Fire(long now)
                {
                    lock (owner.gate)
                    {
                        if (!IsDue(now))
                            return;
                        dueAt = periodTicks > 0
                            ? checked(dueAt + periodTicks)
                            : long.MaxValue;
                    }
                    callback(state);
                }

                public void Dispose() => Interlocked.Exchange(ref disposed, 1);

                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }

        private static class InterlockedExtensions
        {
            public static void Max(ref int location, int value)
            {
                var observed = Volatile.Read(ref location);
                while (observed < value)
                {
                    var original = Interlocked.CompareExchange(ref location, value, observed);
                    if (original == observed)
                        return;
                    observed = original;
                }
            }
        }

    private sealed class ThrowBeforeNewerSnapshotObserver :
        MeshDb.IComposerDraftTransactionObserver
    {
        public void Checkpoint(
            MeshDb.ComposerDraftTransactionCheckpoint checkpoint,
            string threadId,
            long expectedRevision)
        {
            if (checkpoint == MeshDb.ComposerDraftTransactionCheckpoint.BeforeNewerSnapshotWrite)
                throw new TopicSendJournalCrashException("simulated atomic R2 crash");
        }
    }

    private SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        var hex = Convert.ToHexString(key);
        using (var keyCommand = connection.CreateCommand())
        {
            keyCommand.CommandText = $"PRAGMA key = \"x'{hex}'\";";
            keyCommand.ExecuteNonQuery();
        }
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 0; PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }
}
