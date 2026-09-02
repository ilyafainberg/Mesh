using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class TopicSendLifecycleTests
{
    [TestMethod]
    public async Task ComponentDisposeRecreate_ReattachesAndReceivesOneCompletion()
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var release = NewSignal();
        var completed = NewSignal();
        var disposedNotifications = 0;
        var recreatedNotifications = 0;

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                await release.Task;
                return new TopicSendHandoff(true, "accepted");
            }));

        var first = new ComponentLifecycle(coordinator);
        Assert.IsTrue(first.Attach(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref disposedNotifications);
                return Task.CompletedTask;
            }));
        first.Dispose();

        using var recreated = new ComponentLifecycle(coordinator);
        Assert.IsTrue(recreated.Attach(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref recreatedNotifications);
                completed.TrySetResult();
                return Task.CompletedTask;
            }));

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, Volatile.Read(ref disposedNotifications));
        Assert.AreEqual(1, Volatile.Read(ref recreatedNotifications));
    }

    [TestMethod]
    public async Task RecoveryAlias_DoesNotReplaceLiveOperationStateOrLoseObserver()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var coordinator = new TopicSendCoordinator(identityStore: store);
        var snapshot = coordinator.CreateSnapshot(
            "thread",
            "device",
            1,
            "fingerprint",
            DateTimeOffset.UtcNow,
            "account");
        var entered = NewSignal();
        var release = NewSignal();
        var completed = NewSignal();

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            coordinator.Submit(
                snapshot,
                async _ =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    return new TopicSendHandoff(true, "accepted");
                },
                draftCleanup: PersistedDraftCleanup()).Kind);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var recovered = coordinator.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(snapshot.OperationId, recovered.OperationId);
        await using var observer = coordinator.Observe(
            recovered.OperationId,
            "recreated-component",
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.Accepted)
                    completed.TrySetResult();
                return Task.CompletedTask;
            });
        Assert.IsNotNull(observer);

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(coordinator.TryGetOutcome(snapshot.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Accepted, outcome!.Kind);
    }

    [TestMethod]
    public async Task IdenticalTextEnteredAtNewRevision_SurvivesAcceptedCompletion()
    {
        var coordinator = new TopicSendCoordinator();
        var revisions = new ComposerRevisionGuard();
        var submittedDraft = revisions.Capture("thread", "identical text");
        var snapshot = coordinator.CreateSnapshot(
            "thread",
            "device",
            submittedDraft.Revision,
            "fingerprint",
            DateTimeOffset.UtcNow);
        var release = NewSignal();
        var reconciled = NewSignal();

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                await release.Task;
                return new TopicSendHandoff(true, "accepted");
            },
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.Accepted)
                    Assert.IsFalse(revisions.TryClear(submittedDraft, out _));
                reconciled.TrySetResult();
                return Task.CompletedTask;
            }));

        var newerRevision = revisions.Track("thread", "identical text");
        Assert.AreNotEqual(submittedDraft.Revision, newerRevision);
        release.TrySetResult();
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var current = revisions.GetOrCreate("thread", "");
        Assert.AreEqual(newerRevision, current.Revision);
        Assert.AreEqual("identical text", current.Text);
    }

    [TestMethod]
    public async Task FailedRestore_RetriesWithinRealComponentSchedule()
    {
        var operations = new UiOperationCoordinator();
        using var component = new RestoreLifecycle(operations);
        var attempts = 0;
        var restored = component.RestoreAsync(
            "thread",
            _ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<string>(new InvalidOperationException("transient"))
                : Task.FromResult("draft"));

        Assert.AreEqual(
            "draft",
            await restored.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2, Volatile.Read(ref attempts));
    }

    [TestMethod]
    public async Task AcceptedOperations_EvictFullStateAndBoundIdentityRetention()
    {
        var coordinator = new TopicSendCoordinator(new TopicSendRetentionOptions
        {
            MaximumRunningOperations = 4,
            MaximumCompletedIdentities = 2,
            MaximumUnsubmittedSnapshots = 4,
            CompletedIdentityRetention = TimeSpan.FromMinutes(5)
        });

        for (var revision = 1; revision <= 6; revision++)
        {
            var snapshot = coordinator.CreateSnapshot(
                "thread",
                "device",
                revision,
                $"fingerprint-{revision}",
                DateTimeOffset.UtcNow);
            var completed = NewSignal();
            Assert.IsTrue(coordinator.TrySubmit(
                snapshot,
                _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
                _ =>
                {
                    completed.TrySetResult();
                    return Task.CompletedTask;
                }));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => !coordinator.RequiresRecovery(snapshot.OperationId),
                TimeSpan.FromSeconds(2));
        }

        await WaitUntilAsync(
            () => coordinator.RunningOperationCount == 0,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, coordinator.RunningOperationCount);
        Assert.AreEqual(2, coordinator.CompletedIdentityCount);
    }

    [TestMethod]
    public async Task LiveObserverNeverExpires_ExplicitUnsubscribeReleasesStrongReference()
    {
        var coordinator = new TopicSendCoordinator(new TopicSendRetentionOptions
        {
            MaximumRunningOperations = 2,
            MaximumCompletedIdentities = 2,
            MaximumUnsubmittedSnapshots = 2,
            CompletedIdentityRetention = TimeSpan.FromMinutes(1)
        });
        var snapshot = CreateSnapshot(coordinator);
        var release = NewSignal();
        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                await release.Task;
                return new TopicSendHandoff(true, "accepted");
            }));

        var attached = AttachCollectibleObserver(coordinator, snapshot);
        await Task.Delay(100);
        ForceCollection();
        Assert.IsTrue(attached.Target.IsAlive);
        Assert.IsTrue(coordinator.IsRunning(snapshot.OperationId));

        await attached.Subscription.DisposeAsync();
        Assert.AreEqual(0, attached.Subscription.InFlightCallbackCount);
        ForceCollection();
        Assert.IsFalse(attached.Target.IsAlive);
        release.TrySetResult();
        await WaitUntilAsync(
            () => !coordinator.IsRunning(snapshot.OperationId),
            TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("error")]
    [DataRow("cancellation")]
    public async Task DisposeAsync_WaitsForExecutingCallback_ThenReleasesTarget(
        string completion)
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var handoffRelease = NewSignal();
        var callbackRelease = NewSignal();

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                await handoffRelease.Task;
                return new TopicSendHandoff(true, "accepted");
            }));
        var attached = AttachBarrierObserver(
            coordinator, snapshot, callbackRelease, completion);

        handoffRelease.TrySetResult();
        await attached.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, attached.Subscription.InFlightCallbackCount);

        var disposal = attached.Subscription.DisposeAsync().AsTask();
        Assert.IsFalse(
            disposal.IsCompleted,
            "detachment must remain incomplete while a callback is executing");
        Assert.AreEqual(0, coordinator.ObserverCount(snapshot.OperationId));
        Assert.AreEqual(
            0,
            coordinator.ObserverReferenceCount(snapshot.OperationId),
            "detachment must clear the stored component delegate synchronously");

        callbackRelease.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => !coordinator.IsRunning(snapshot.OperationId),
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, attached.Subscription.InFlightCallbackCount);
        Assert.AreEqual(0, coordinator.ObserverCount(snapshot.OperationId));
        Assert.AreEqual(0, coordinator.ObserverReferenceCount(snapshot.OperationId));
        Assert.AreEqual(1, attached.Counter.InvocationCount);
        ForceCollection();
        Assert.IsFalse(
            attached.Target.IsAlive,
            $"the {completion} callback target remained rooted after quiescent detachment");
        Console.WriteLine(
            $"CALLBACK_QUIESCENT completion={completion} observers=0 inflight=0 delegates=0 invocations=1");
    }

    [TestMethod]
    public async Task RepeatedComponentRecreation_UsesOneDurableIdentityAndOneTerminalNotice()
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var release = NewSignal();
        var completed = NewSignal();
        var executions = 0;
        var notifications = 0;

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                Interlocked.Increment(ref executions);
                await release.Task;
                return new TopicSendHandoff(true, "accepted");
            }));

        for (var index = 0; index < 20; index++)
        {
            using var transient = new ComponentLifecycle(coordinator);
            Assert.IsTrue(transient.Attach(
                snapshot,
                _ =>
                {
                    Interlocked.Increment(ref notifications);
                    return Task.CompletedTask;
                }));
            Assert.IsFalse(coordinator.TrySubmit(
                snapshot,
                _ => Task.FromResult(new TopicSendHandoff(true, "duplicate"))));
        }

        using var current = new ComponentLifecycle(coordinator);
        Assert.IsTrue(current.Attach(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref notifications);
                completed.TrySetResult();
                return Task.CompletedTask;
            }));
        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, Volatile.Read(ref executions));
        Assert.AreEqual(1, Volatile.Read(ref notifications));
        Assert.IsFalse(coordinator.TrySubmit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "duplicate-after-completion"))));
    }

    [TestMethod]
    public async Task CompletedCleanup_UsesMonotonicSequenceInsteadOfSubmissionLedger()
    {
        var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        var store = CreatePersistedIdentityStore(persisted);
        var clock = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 26, 10, 33, 20, TimeSpan.Zero));
        var options = new TopicSendRetentionOptions
        {
            MaximumRunningOperations = 2,
            MaximumCompletedIdentities = 1,
            MaximumUnsubmittedSnapshots = 2
        };
        var compactions = new FinalizationProbe();
        var coordinator = new TopicSendCoordinator(
            options, clock, store, null, null, compactions);
        var older = coordinator.CreateSnapshot(
            "older-thread",
            "older-device",
            7,
            "older-fingerprint",
            clock.GetUtcNow());
        var newer = coordinator.CreateSnapshot(
            "newer-thread",
            "newer-device",
            7,
            "newer-fingerprint",
            clock.GetUtcNow());
        Assert.IsTrue(newer.SubmissionSequence > older.SubmissionSequence);

        var olderHandoffEntered = NewSignal();
        var releaseOlderHandoff = NewSignal();
        var olderCompacted = compactions.Track(older.OperationId);
        var newerCompacted = compactions.Track(newer.OperationId);
        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            coordinator.Submit(
                older,
                async _ =>
                {
                    olderHandoffEntered.TrySetResult();
                    await releaseOlderHandoff.Task;
                    return new TopicSendHandoff(true, "accepted");
                },
                draftCleanup: PersistedDraftCleanup()).Kind);
        await olderHandoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            coordinator.Submit(
                newer,
                _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
                draftCleanup: PersistedDraftCleanup()).Kind);
        await newerCompacted.WaitAsync(TimeSpan.FromSeconds(2));
        releaseOlderHandoff.TrySetResult();
        await olderCompacted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, coordinator.CompletedIdentityCount);
        var duplicateExecutions = 0;
        var duplicate = coordinator.Submit(
            newer,
            _ =>
            {
                Interlocked.Increment(ref duplicateExecutions);
                return Task.FromResult(new TopicSendHandoff(true, "duplicate"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.AlreadyCompleted, duplicate.Kind);
        Assert.AreEqual(0, Volatile.Read(ref duplicateExecutions));

        await coordinator.DisposeAsync();

        var restartedStore = CreatePersistedIdentityStore(persisted);
        var restartedCompactions = new FinalizationProbe();
        var recreated = new TopicSendCoordinator(
            options, clock, restartedStore, null, null, restartedCompactions);
        var next = recreated.CreateSnapshot(
            newer.ThreadId,
            newer.TargetDeviceId,
            newer.ComposerRevision,
            newer.DraftFingerprint,
            clock.GetUtcNow());
        Assert.AreEqual(newer.ComposerRevision, next.ComposerRevision);
        Assert.AreNotEqual(newer.OperationId, next.OperationId);
        Assert.IsTrue(next.SubmissionSequence > newer.SubmissionSequence);

        var cleanupEntered = NewSignal();
        var releaseCleanup = NewSignal();
        var nextCompacted = restartedCompactions.Track(next.OperationId);
        var executions = 0;
        var result = recreated.Submit(
            next,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            draftCleanup: new TopicSendDraftCleanup(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                return TopicSendDraftCleanupResult.DraftClearPersisted;
            }));

        Assert.AreEqual(TopicSendSubmissionKind.Started, result.Kind);
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        restartedStore.Save(new TopicSendIdentityRecord(
            newer.LogicalIdentity,
            newer.ScopeIdentity,
            newer.SubmissionSequence,
            newer.ComposerRevision,
            newer.OperationId,
            newer.RunId,
            newer.LineId,
            newer.DraftFingerprint,
            TopicSendOutcomeKind.Accepted,
            Version: TopicSendIdentityRecord.CurrentVersion,
            Lifecycle: TopicSendJournalLifecycle.Terminal,
            Cleanup: TopicSendJournalCleanup.DraftClearPersisted));
        restartedStore.Remove(newer.ScopeIdentity, newer.OperationId);
        Assert.IsTrue(restartedStore.TryGetUnresolved(next.ScopeIdentity, out var retained));
        Assert.AreEqual(next.OperationId, retained!.OperationId);
        Assert.AreEqual(next.SubmissionSequence, retained.SubmissionSequence);
        Assert.AreEqual(TopicSendJournalCleanup.DraftClearPending, retained.Cleanup);

        releaseCleanup.TrySetResult();
        await nextCompacted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(restartedStore.TryGetUnresolved(next.ScopeIdentity, out _));
        Assert.AreEqual(1, Volatile.Read(ref executions));
        await recreated.DisposeAsync();
    }

    [TestMethod]
    public void JournalOrdering_EqualSequenceOnlyAllowsCanonicalExactReplay()
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var store = new InMemoryTopicSendIdentityStore();
        var baseline = TestJournalRecord(
            snapshot,
            stateSequence: 10,
            TopicSendJournalLifecycle.Terminal,
            TopicSendJournalCleanup.DraftClearPending,
            TopicSendOutcomeKind.Accepted);
        store.Save(baseline);
        Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var canonical));

        for (var iteration = 0; iteration < 1000; iteration++)
            store.Save(canonical!);

        var conflicts = new[]
        {
            canonical! with { OutcomeKind = TopicSendOutcomeKind.Rejected, PayloadHash = null },
            canonical! with { Cleanup = TopicSendJournalCleanup.DraftClearPersisted, PayloadHash = null },
            canonical! with { OperationId = canonical.OperationId + "-other", PayloadHash = null },
            canonical! with { RunId = canonical.RunId + "-other", PayloadHash = null },
            canonical! with { ComposerRevision = canonical.ComposerRevision + 1, PayloadHash = null },
            canonical! with { Compaction = TopicSendJournalCompaction.Compacted, PayloadHash = null },
            canonical! with { PayloadHash = "different-payload" }
        };
        foreach (var conflict in conflicts)
            AssertJournalConflict(() => store.Save(conflict));

        Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var retained));
        Assert.AreEqual(canonical, retained);
    }

    [TestMethod]
    public void JournalOrdering_ConcurrentTerminalOutcomesFenceOneWriter_OneThousandIterations()
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var store = new InMemoryTopicSendIdentityStore();
            store.Save(TestJournalRecord(
                snapshot,
                stateSequence: 20,
                TopicSendJournalLifecycle.AcceptedOrUnknown,
                TopicSendJournalCleanup.None));
            var accepted = TestJournalRecord(
                snapshot,
                stateSequence: 21,
                TopicSendJournalLifecycle.Terminal,
                TopicSendJournalCleanup.DraftClearPending,
                TopicSendOutcomeKind.Accepted);
            var rejected = TestJournalRecord(
                snapshot,
                stateSequence: 21,
                TopicSendJournalLifecycle.Terminal,
                TopicSendJournalCleanup.DraftClearPersisted,
                TopicSendOutcomeKind.Rejected);
            using var start = new ManualResetEventSlim();
            var conflicts = 0;
            var first = Task.Run(() => SaveAtBarrier(store, accepted, start, ref conflicts));
            var second = Task.Run(() => SaveAtBarrier(store, rejected, start, ref conflicts));
            start.Set();
            Task.WaitAll(first, second);

            Assert.AreEqual(1, conflicts, $"iteration {iteration}");
            Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var winner));
            Assert.IsTrue(winner!.OutcomeKind is TopicSendOutcomeKind.Accepted
                or TopicSendOutcomeKind.Rejected);
        }
    }

    [TestMethod]
    public void JournalOrdering_CleanupCompactionAndRestartUseOneTotalOrder()
    {
        var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var store = CreatePersistedIdentityStore(persisted);
        var pending = TestJournalRecord(
            snapshot,
            stateSequence: 30,
            TopicSendJournalLifecycle.Terminal,
            TopicSendJournalCleanup.DraftClearPending,
            TopicSendOutcomeKind.Accepted);
        store.Save(pending);
        var persistedCleanup = pending with
        {
            StateSequence = 31,
            Cleanup = TopicSendJournalCleanup.DraftClearPersisted,
            PayloadHash = null
        };
        store.Save(persistedCleanup);
        store.Compact(persistedCleanup with { StateSequence = 32 });
        Assert.IsFalse(store.TryGetUnresolved(snapshot.ScopeIdentity, out _));

        store.Save(pending);
        Assert.IsFalse(store.TryGetUnresolved(snapshot.ScopeIdentity, out _));
        AssertJournalConflict(() => store.Save(persistedCleanup with
        {
            StateSequence = 33,
            Cleanup = TopicSendJournalCleanup.DraftClearSuperseded,
            PayloadHash = null
        }));

        var restarted = CreatePersistedIdentityStore(persisted);
        Assert.IsFalse(restarted.TryGetUnresolved(snapshot.ScopeIdentity, out _));
        restarted.Save(pending);
        Assert.IsFalse(restarted.TryGetUnresolved(snapshot.ScopeIdentity, out _));

        var newer = snapshot with
        {
            SubmissionSequence = snapshot.SubmissionSequence + 1,
            OperationId = snapshot.OperationId + "-new",
            RunId = snapshot.RunId + "-new",
            LineId = snapshot.LineId + "-new"
        };
        var newerPending = TestJournalRecord(
            newer,
            stateSequence: 34,
            TopicSendJournalLifecycle.PreHandoff,
            TopicSendJournalCleanup.None);
        restarted.Save(newerPending);
        Assert.IsTrue(restarted.TryGetUnresolved(snapshot.ScopeIdentity, out var recovered));
        Assert.AreEqual(newer.OperationId, recovered!.OperationId);
    }

    [TestMethod]
    public async Task JournalOrdering_MalformedLegacyRestartMigratesBeforeTerminalCleanup()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var scope = TopicSendSnapshot.ScopeId("thread", "device");
        values[$"mesh.ui.topic-send.v3.pending.{scope}"] = "{ malformed";
        var store = CreatePersistedIdentityStore(values);
        var coordinator = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: new SequencedReconciliationQuery(
                TopicSendReconciliationKind.NotFound));
        var recovered = coordinator.CreateSnapshot(
            "thread",
            "device",
            1,
            "fingerprint",
            DateTimeOffset.UtcNow);
        Assert.IsTrue(store.TryGetUnresolved(scope, out var migrated));
        var terminal = TestJournalRecord(
            recovered,
            migrated!.StateSequence + 1,
            TopicSendJournalLifecycle.Terminal,
            TopicSendJournalCleanup.DraftClearPending,
            TopicSendOutcomeKind.RetryableFailed) with
        {
            FailureMessage = "No durable handoff was found. The unchanged draft can be retried."
        };
        Assert.AreEqual(
            TopicSendJournalApplyResult.Advance,
            TopicSendJournalOrdering.Compare(migrated, terminal));

        TopicSendOutcome? observed = null;
        await coordinator.RequestReconciliationAsync(
            recovered,
            outcome =>
            {
                observed = outcome;
                return Task.CompletedTask;
            },
            PersistedDraftCleanup());

        Assert.AreEqual(
            TopicSendOutcomeKind.RetryableFailed,
            observed!.Kind,
            string.Join(Environment.NewLine, values.Select(pair => $"{pair.Key}={pair.Value}")));
        Assert.IsFalse(store.TryGetUnresolved(scope, out _));
    }

    [TestMethod]
    public async Task ActualTerminalFailedSerialization_ReopensCleansExactlyOnceAndNeverResubmits()
    {
        var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        var terminalCrash = new OneShotJournalCrash(
            "terminal",
            TopicSendJournalBoundary.AfterWrite);
        var initialStore = CreatePersistedIdentityStore(persisted);
        var initial = new TopicSendCoordinator(
            identityStore: initialStore,
            reconciliationQuery: new SequencedReconciliationQuery(
                TopicSendReconciliationKind.Failed),
            journalFaultInjector: terminalCrash);
        var snapshot = CreateSnapshot(initial);

        var submission = initial.Submit(
            snapshot,
            (_, context) =>
            {
                context.MarkDurableBoundaryEntered();
                throw new InvalidOperationException("handoff result lost");
            },
            draftCleanup: new TopicSendDraftCleanup(_ =>
                Task.FromException<TopicSendDraftCleanupResult>(
                    new InvalidOperationException("cleanup must happen after restart"))));
        Assert.AreEqual(TopicSendSubmissionKind.Started, submission.Kind);
        await WaitUntilAsync(() => terminalCrash.Triggered, TimeSpan.FromSeconds(2));
        await initial.DisposeAsync();

        var json = persisted[$"mesh.ui.topic-send.v5.pending.{snapshot.ScopeIdentity}"];
        var serialized = System.Text.Json.JsonSerializer.Deserialize<TopicSendIdentityRecord>(
            json,
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        Assert.IsNotNull(serialized);
        Assert.AreEqual(TopicSendJournalLifecycle.Terminal, serialized.Lifecycle);
        Assert.AreEqual(TopicSendOutcomeKind.Failed, serialized.OutcomeKind);
        Assert.AreEqual(TopicSendJournalCleanup.DraftClearPending, serialized.Cleanup);

        var restartedStore = CreatePersistedIdentityStore(persisted);
        await using var restarted = new TopicSendCoordinator(identityStore: restartedStore);
        var recovered = restarted.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow,
            snapshot.AccountId);
        Assert.AreEqual(snapshot.OperationId, recovered.OperationId);
        Assert.AreEqual(0, restarted.RunningOperationCount);

        var submits = 0;
        var duplicate = restarted.Submit(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref submits);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.AlreadyCompleted, duplicate.Kind);
        Assert.AreEqual(TopicSendOutcomeKind.Failed, duplicate.Outcome!.Kind);

        var cleanupCalls = 0;
        var cleanup = new TopicSendDraftCleanup(outcome =>
        {
            Assert.AreEqual(TopicSendOutcomeKind.Failed, outcome.Kind);
            Interlocked.Increment(ref cleanupCalls);
            return Task.FromResult(TopicSendDraftCleanupResult.DraftClearPersisted);
        });
        await restarted.RequestReconciliationAsync(recovered, draftCleanup: cleanup);
        await restarted.RequestReconciliationAsync(recovered, draftCleanup: cleanup);

        Assert.AreEqual(1, Volatile.Read(ref cleanupCalls));
        Assert.AreEqual(0, Volatile.Read(ref submits));
        Assert.IsFalse(restartedStore.TryGetUnresolved(snapshot.ScopeIdentity, out _));
        Assert.IsTrue(restarted.TryGetOutcome(recovered.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Failed, outcome!.Kind);
    }

    [TestMethod]
    public async Task TerminalFailed_RestartPreservesFailureCleansUpAndNeverResubmits_OneHundredTimes()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
            var initialStore = CreatePersistedIdentityStore(persisted);
            var initial = new TopicSendCoordinator(identityStore: initialStore);
            var snapshot = CreateSnapshot(initial);
            initialStore.Save(TestJournalRecord(
                    snapshot,
                    initialStore.NextSequence(snapshot.ScopeIdentity),
                    TopicSendJournalLifecycle.Terminal,
                    TopicSendJournalCleanup.DraftClearPending,
                    TopicSendOutcomeKind.Failed) with
                {
                    FailureMessage = $"durable run failed {iteration}"
                });
            await initial.DisposeAsync();

            var restartedStore = CreatePersistedIdentityStore(persisted);
            await using var restarted = new TopicSendCoordinator(identityStore: restartedStore);
            var recovered = restarted.CreateSnapshot(
                snapshot.ThreadId,
                snapshot.TargetDeviceId,
                snapshot.ComposerRevision + 100,
                "replacement draft",
                DateTimeOffset.UtcNow,
                snapshot.AccountId);
            Assert.AreEqual(snapshot.OperationId, recovered.OperationId, $"iteration {iteration}");
            Assert.AreEqual(0, restarted.RunningOperationCount, $"iteration {iteration}");

            var submits = 0;
            var duplicate = restarted.Submit(
                recovered,
                _ =>
                {
                    Interlocked.Increment(ref submits);
                    return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
                });
            Assert.AreEqual(
                TopicSendSubmissionKind.AlreadyCompleted,
                duplicate.Kind,
                $"iteration {iteration}");

            TopicSendOutcome? cleanupOutcome = null;
            await restarted.RequestReconciliationAsync(
                recovered,
                draftCleanup: new TopicSendDraftCleanup(outcome =>
                {
                    cleanupOutcome = outcome;
                    return Task.FromResult(TopicSendDraftCleanupResult.DraftClearPersisted);
                }));

            Assert.AreEqual(0, Volatile.Read(ref submits), $"iteration {iteration}");
            Assert.AreEqual(TopicSendOutcomeKind.Failed, cleanupOutcome!.Kind, $"iteration {iteration}");
            Assert.AreEqual(
                $"durable run failed {iteration}",
                cleanupOutcome.Exception!.Message,
                $"iteration {iteration}");
            Assert.IsFalse(
                restartedStore.TryGetUnresolved(snapshot.ScopeIdentity, out _),
                $"iteration {iteration}");

            var replay = restarted.Submit(
                recovered,
                _ =>
                {
                    Interlocked.Increment(ref submits);
                    return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
                });
            Assert.AreEqual(TopicSendSubmissionKind.AlreadyCompleted, replay.Kind);
            Assert.AreEqual(0, Volatile.Read(ref submits), $"iteration {iteration}");
        }
    }

    [TestMethod]
    public async Task TerminalFailed_CrashAfterCleanupWrite_ReopensAndCompactsMonotonically()
    {
        var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        var initialStore = CreatePersistedIdentityStore(persisted);
        var initial = new TopicSendCoordinator(identityStore: initialStore);
        var snapshot = CreateSnapshot(initial);
        initialStore.Save(TestJournalRecord(
                snapshot,
                initialStore.NextSequence(snapshot.ScopeIdentity),
                TopicSendJournalLifecycle.Terminal,
                TopicSendJournalCleanup.DraftClearPending,
                TopicSendOutcomeKind.Failed) with
            {
                FailureMessage = "terminal failure"
            });
        await initial.DisposeAsync();

        var crashStore = CreatePersistedIdentityStore(persisted);
        var crash = new OneShotJournalCrash(
            "draft-clear-persisted",
            TopicSendJournalBoundary.AfterWrite);
        var recovering = new TopicSendCoordinator(
            identityStore: crashStore,
            journalFaultInjector: crash);
        var recovered = recovering.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow,
            snapshot.AccountId);
        await recovering.RequestReconciliationAsync(
            recovered,
            draftCleanup: PersistedDraftCleanup());
        Assert.IsTrue(crash.Triggered);
        Assert.IsTrue(crashStore.TryGetUnresolved(snapshot.ScopeIdentity, out var cleanupWritten));
        Assert.AreEqual(TopicSendOutcomeKind.Failed, cleanupWritten!.OutcomeKind);
        Assert.AreEqual(TopicSendJournalCleanup.DraftClearPersisted, cleanupWritten.Cleanup);
        var cleanupSequence = cleanupWritten.StateSequence;
        await recovering.DisposeAsync();

        var finalStore = CreatePersistedIdentityStore(persisted);
        await using var final = new TopicSendCoordinator(identityStore: finalStore);
        var reopened = final.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow,
            snapshot.AccountId);
        Assert.AreEqual(snapshot.OperationId, reopened.OperationId);
        Assert.IsFalse(finalStore.TryGetUnresolved(snapshot.ScopeIdentity, out _));
        Assert.IsTrue(
            long.Parse(persisted["mesh.ui.topic-send.v5.counter.global"]) > cleanupSequence);
        Assert.IsTrue(final.TryGetOutcome(reopened.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Failed, outcome!.Kind);
        Assert.AreEqual("terminal failure", outcome.Exception!.Message);
    }

    [DataTestMethod]
    [DataRow(TopicSendOutcomeKind.Accepted)]
    [DataRow(TopicSendOutcomeKind.Rejected)]
    [DataRow(TopicSendOutcomeKind.Failed)]
    [DataRow(TopicSendOutcomeKind.RetryableFailed)]
    public void JournalInvariant_AllTerminalOutcomesAcceptCleanupMatrix(
        TopicSendOutcomeKind outcomeKind)
    {
        using var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        foreach (var cleanup in new[]
                 {
                     TopicSendJournalCleanup.DraftClearPending,
                     TopicSendJournalCleanup.DraftClearPersisted,
                     TopicSendJournalCleanup.DraftClearSuperseded
                 })
        {
            var store = new InMemoryTopicSendIdentityStore();
            var record = TestJournalRecord(
                snapshot,
                10,
                TopicSendJournalLifecycle.Terminal,
                cleanup,
                outcomeKind);
            if (outcomeKind != TopicSendOutcomeKind.Accepted)
                record = record with { FailureMessage = outcomeKind.ToString() };
            store.Save(record);
            Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var saved));
            Assert.AreEqual(outcomeKind, saved!.OutcomeKind);
            Assert.AreEqual(cleanup, saved.Cleanup);
        }
    }

    [TestMethod]
    public void JournalInvariant_RejectsInvalidLifecycleIdentityAndEqualSequenceConflict()
    {
        using var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var store = new InMemoryTopicSendIdentityStore();
        var failed = TestJournalRecord(
                snapshot,
                10,
                TopicSendJournalLifecycle.Terminal,
                TopicSendJournalCleanup.DraftClearPending,
                TopicSendOutcomeKind.Failed) with
            {
                FailureMessage = "first failure"
            };
        store.Save(failed);
        store.Save(failed);

        AssertJournalConflict(() => store.Save(failed with
        {
            FailureMessage = "conflicting failure",
            PayloadHash = null
        }));
        AssertJournalConflict(() => new InMemoryTopicSendIdentityStore().Save(
            failed with
            {
                Lifecycle = TopicSendJournalLifecycle.PreHandoff,
                Cleanup = TopicSendJournalCleanup.None,
                PayloadHash = null
            }));
        AssertJournalConflict(() => new InMemoryTopicSendIdentityStore().Save(
            failed with
            {
                OperationId = "",
                PayloadHash = null
            }));
    }

    [TestMethod]
    public async Task LegacyTerminalFailed_RestartMigratesWithoutChangingOutcome()
    {
        var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        var initialStore = CreatePersistedIdentityStore(persisted);
        var initial = new TopicSendCoordinator(identityStore: initialStore);
        var snapshot = CreateSnapshot(initial);
        var legacy = TestJournalRecord(
                snapshot,
                initialStore.NextSequence(snapshot.ScopeIdentity),
                TopicSendJournalLifecycle.Terminal,
                TopicSendJournalCleanup.DraftClearPending,
                TopicSendOutcomeKind.Failed) with
            {
                Version = TopicSendIdentityRecord.CurrentVersion - 1,
                FailureMessage = "legacy terminal failure",
                PayloadHash = null
            };
        persisted[$"mesh.ui.topic-send.v4.pending.{snapshot.ScopeIdentity}"] =
            System.Text.Json.JsonSerializer.Serialize(
                legacy,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
        await initial.DisposeAsync();

        var restartedStore = CreatePersistedIdentityStore(persisted);
        await using var restarted = new TopicSendCoordinator(identityStore: restartedStore);
        var recovered = restarted.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow,
            snapshot.AccountId);
        Assert.AreEqual(snapshot.OperationId, recovered.OperationId);
        Assert.IsTrue(restartedStore.TryGetUnresolved(snapshot.ScopeIdentity, out var migrated));
        Assert.AreEqual(TopicSendIdentityRecord.CurrentVersion, migrated!.Version);
        Assert.AreEqual(TopicSendJournalLifecycle.Terminal, migrated.Lifecycle);
        Assert.AreEqual(TopicSendOutcomeKind.Failed, migrated.OutcomeKind);
        Assert.AreEqual("legacy terminal failure", migrated.FailureMessage);
        Assert.IsTrue(migrated.StateSequence > legacy.StateSequence);
    }

    [DataTestMethod]
    [DataRow(TopicSendReconciliationKind.Completed, TopicSendOutcomeKind.Accepted)]
    [DataRow(TopicSendReconciliationKind.Failed, TopicSendOutcomeKind.Failed)]
    [DataRow(TopicSendReconciliationKind.Cancelled, TopicSendOutcomeKind.Failed)]
    [DataRow(TopicSendReconciliationKind.Interrupted, TopicSendOutcomeKind.Failed)]
    public async Task Reconciliation_CanonicalTerminalMatrixNeverResubmits(
        TopicSendReconciliationKind terminalKind,
        TopicSendOutcomeKind expectedOutcome)
    {
        var store = new InMemoryTopicSendIdentityStore();
        var snapshot = await PersistAcceptedOrUnknownAsync(store);
        await using var restarted = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: new SequencedReconciliationQuery(terminalKind));
        var recovered = restarted.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow,
            snapshot.AccountId);

        TopicSendOutcome? observed = null;
        await restarted.RequestReconciliationAsync(
            recovered,
            outcome =>
            {
                observed = outcome;
                return Task.CompletedTask;
            },
            PersistedDraftCleanup());
        Assert.AreEqual(expectedOutcome, observed!.Kind);

        var submits = 0;
        var duplicate = restarted.Submit(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref submits);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.AlreadyCompleted, duplicate.Kind);
        Assert.AreEqual(0, Volatile.Read(ref submits));
        Assert.AreNotEqual(TopicSendOutcomeKind.RetryableFailed, duplicate.Outcome!.Kind);
    }

    [TestMethod]
    public async Task FinalizationCompleted_IsPublishedAfterDurableCompactionAndCompletedCache()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var afterCompaction = new AfterCompactionBarrier();
        var finalization = new FinalizationProbe();
        var coordinator = new TopicSendCoordinator(
            null, null, store, null, afterCompaction, finalization);
        var snapshot = CreateSnapshot(coordinator);
        var finalized = finalization.Track(snapshot.OperationId);

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            coordinator.Submit(
                snapshot,
                _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
                draftCleanup: PersistedDraftCleanup()).Kind);
        await afterCompaction.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(finalized.IsCompleted);
        Assert.AreEqual(0, coordinator.CompletedIdentityCount);
        Assert.IsFalse(store.TryGetUnresolved(snapshot.ScopeIdentity, out _));

        afterCompaction.Release.TrySetResult();
        await finalized.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, coordinator.CompletedIdentityCount);
        Assert.IsFalse(coordinator.RequiresRecovery(snapshot.OperationId));
        Assert.IsTrue(coordinator.TryGetOutcome(snapshot.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Accepted, outcome!.Kind);
    }

    [TestMethod]
    public async Task PreHandoffFailure_LedgerNotFoundRetriesSameStableIdentity()
    {
        var coordinator = new TopicSendCoordinator(
            reconciliationQuery: new SequencedReconciliationQuery(
                TopicSendReconciliationKind.NotFound));
        var snapshot = CreateSnapshot(coordinator);
        var firstDone = new TaskCompletionSource<TopicSendOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            coordinator.Submit(
                snapshot,
                (_, _) =>
                {
                    Interlocked.Increment(ref attempts);
                    return Task.FromException<TopicSendHandoff>(
                        new IOException("attachment hydration failed"));
                },
                outcome =>
                {
                    firstDone.TrySetResult(outcome);
                    return Task.CompletedTask;
                }).Kind);
        Assert.AreEqual(
            TopicSendOutcomeKind.RetryableFailed,
            (await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);

        var retryDone = NewSignal();
        var retry = coordinator.Submit(
            snapshot,
            (_, context) =>
            {
                Interlocked.Increment(ref attempts);
                context.AuthorizeDurableHandoff();
                context.MarkDurableBoundaryEntered();
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            _ =>
            {
                retryDone.TrySetResult();
                return Task.CompletedTask;
            });
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await retryDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, Volatile.Read(ref attempts));
        Assert.AreEqual(snapshot.OperationId, retry.Snapshot.OperationId);
    }

    [TestMethod]
    public async Task UnknownBoundary_ReconcilesWithoutSecondDurableSubmit()
    {
        var query = new SequencedReconciliationQuery(
            TopicSendReconciliationKind.Unknown,
            TopicSendReconciliationKind.Accepted);
        var coordinator = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumReconciliationAttempts = 3,
                ReconciliationInitialBackoff = TimeSpan.FromMilliseconds(1),
                ReconciliationMaximumBackoff = TimeSpan.FromMilliseconds(2)
            },
            reconciliationQuery: query);
        var snapshot = CreateSnapshot(coordinator);
        var done = new TaskCompletionSource<TopicSendOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var submits = 0;

        coordinator.Submit(
            snapshot,
            (_, context) =>
            {
                Interlocked.Increment(ref submits);
                context.MarkDurableBoundaryEntered();
                return Task.FromException<TopicSendHandoff>(
                    new IOException("response lost after handoff"));
            },
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.Accepted)
                    done.TrySetResult(outcome);
                return Task.CompletedTask;
            });

        Assert.AreEqual(
            TopicSendOutcomeKind.Accepted,
            (await done.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
        Assert.AreEqual(1, Volatile.Read(ref submits));
        Assert.IsTrue(query.Calls >= 2);
    }

    [TestMethod]
    public async Task RecoveredUnknown_PreservesOriginalRevisionAndCancelsStatusQuery()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var original = new TopicSendCoordinator(identityStore: store);
        var snapshot = original.CreateSnapshot(
            "thread",
            "device",
            7,
            "persisted-fingerprint",
            DateTimeOffset.UtcNow);
        original.Submit(
            snapshot,
            (_, context) =>
            {
                context.MarkDurableBoundaryEntered();
                return Task.FromException<TopicSendHandoff>(
                    new IOException("accepted response was lost"));
            });
        await WaitUntilAsync(
            () => original.RunningOperationCount == 0,
            TimeSpan.FromSeconds(2));

        var query = new CancellableReconciliationQuery();
        var recreated = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: query);
        var recovered = recreated.CreateSnapshot(
            "thread",
            "device",
            1,
            "persisted-fingerprint",
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.AreEqual(snapshot.OperationId, recovered.OperationId);
        Assert.AreEqual(7, recovered.ComposerRevision);

        using var cancellation = new CancellationTokenSource();
        var reconciliation = recreated.RequestReconciliationAsync(
            recovered,
            cancellationToken: cancellation.Token);
        await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(query.CancellationObserved);
        Assert.AreEqual(0, recreated.RunningOperationCount);
        Assert.IsTrue(recreated.TryGetOutcome(recovered.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Reconciling, outcome!.Kind);
    }

    [TestMethod]
    public async Task CapacityBackpressure_IsExplicitRetryableAndUsesStableIdentity()
    {
        var coordinator = new TopicSendCoordinator(new TopicSendRetentionOptions
        {
            MaximumRunningOperations = 1,
            MaximumCompletedIdentities = 4,
            MaximumUnsubmittedSnapshots = 4
        });
        var release = NewSignal();
        var firstDone = NewSignal();
        var first = coordinator.CreateSnapshot(
            "thread-1", "device", 1, "first", DateTimeOffset.UtcNow);
        Assert.IsTrue(coordinator.TrySubmit(
            first,
            async _ =>
            {
                await release.Task;
                return new TopicSendHandoff(true, "accepted");
            },
            _ =>
            {
                firstDone.TrySetResult();
                return Task.CompletedTask;
            }));

        var blocked = coordinator.CreateSnapshot(
            "thread-2", "device", 1, "second", DateTimeOffset.UtcNow);
        var blockedExecutions = 0;
        var backpressure = coordinator.Submit(
            blocked,
            _ =>
            {
                Interlocked.Increment(ref blockedExecutions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.CapacityExceeded, backpressure.Kind);
        Assert.IsTrue(backpressure.Retryable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(backpressure.Error));
        Assert.AreEqual(0, Volatile.Read(ref blockedExecutions));

        release.TrySetResult();
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retryDone = NewSignal();
        var retry = coordinator.Submit(
            blocked,
            _ =>
            {
                Interlocked.Increment(ref blockedExecutions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            _ =>
            {
                retryDone.TrySetResult();
                return Task.CompletedTask;
            });
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await retryDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref blockedExecutions));
        Assert.AreEqual(blocked.OperationId, retry.Snapshot.OperationId);
    }

    [TestMethod]
    public async Task TerminalCommit_ReleasesCapacityBeforeCompletionProjectionReturns()
    {
        var coordinator = new TopicSendCoordinator(new TopicSendRetentionOptions
        {
            MaximumRunningOperations = 1,
            MaximumCompletedIdentities = 4,
            MaximumUnsubmittedSnapshots = 4
        });
        var completionEntered = NewSignal();
        var releaseCompletion = NewSignal();
        var first = coordinator.CreateSnapshot(
            "thread-1", "device", 1, "first", DateTimeOffset.UtcNow);

        Assert.IsTrue(coordinator.TrySubmit(
            first,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            async _ =>
            {
                completionEntered.TrySetResult();
                await releaseCompletion.Task;
            }));
        await completionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondDone = NewSignal();
        var second = coordinator.CreateSnapshot(
            "thread-2", "device", 1, "second", DateTimeOffset.UtcNow);
        var submission = coordinator.Submit(
            second,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            _ =>
            {
                secondDone.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.AreEqual(TopicSendSubmissionKind.Started, submission.Kind);
        releaseCompletion.TrySetResult();
        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(second.OperationId, submission.Snapshot.OperationId);
    }

    [TestMethod]
    public async Task CapacityAndTerminalBarrier_LinearizesAtTerminalCommit()
    {
        var probe = new LifecycleProbe("terminal-before-commit");
        var coordinator = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumRunningOperations = 1,
                MaximumCompletedIdentities = 4,
                MaximumUnsubmittedSnapshots = 4
            },
            null,
            null,
            null,
            null,
            probe);
        var firstDone = NewSignal();
        var first = coordinator.CreateSnapshot(
            "thread-1", "device", 1, "first", DateTimeOffset.UtcNow);
        Assert.IsTrue(coordinator.TrySubmit(
            first,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            _ =>
            {
                firstDone.TrySetResult();
                return Task.CompletedTask;
            }));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = coordinator.CreateSnapshot(
            "thread-2", "device", 1, "second", DateTimeOffset.UtcNow);
        var beforeCommit = coordinator.Submit(
            second,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")));
        Assert.AreEqual(TopicSendSubmissionKind.CapacityExceeded, beforeCommit.Kind);
        Assert.AreEqual(second.OperationId, beforeCommit.Snapshot.OperationId);

        probe.Release.TrySetResult();
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var afterCommitDone = NewSignal();
        var afterCommit = coordinator.Submit(
            second,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            _ =>
            {
                afterCommitDone.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.AreEqual(TopicSendSubmissionKind.Started, afterCommit.Kind);
        Assert.AreEqual(second.OperationId, afterCommit.Snapshot.OperationId);
        await afterCommitDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task DraftClearFailure_RestartKeepsFenceAndRetriesBeforeCompaction()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var coordinator = new TopicSendCoordinator(identityStore: store);
        var snapshot = CreateSnapshot(coordinator);
        var cleanupFailed = NewSignal();
        var cleanupEntered = NewSignal();
        var failCleanup = NewSignal();
        var submits = 0;

        var result = coordinator.Submit(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref submits);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            draftCleanup: new TopicSendDraftCleanup(
                async _ =>
                {
                    cleanupEntered.TrySetResult();
                    await failCleanup.Task;
                    throw new IOException("storage unavailable");
                }));
        Assert.AreEqual(TopicSendSubmissionKind.Started, result.Kind);
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var observer = coordinator.Observe(
            snapshot.OperationId,
            "failure-observer",
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.Reconciling
                    && outcome.Handoff?.Error?.Contains(
                        "draft cleanup failed", StringComparison.OrdinalIgnoreCase) == true)
                    cleanupFailed.TrySetResult();
                return Task.CompletedTask;
            });
        Assert.IsNotNull(observer);
        failCleanup.TrySetResult();
        await cleanupFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var pending));
        Assert.AreEqual(
            TopicSendJournalCleanup.DraftClearPending,
            pending!.Cleanup);

        var restarted = new TopicSendCoordinator(identityStore: store);
        var recovered = restarted.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            999,
            "different",
            DateTimeOffset.UtcNow);
        var duplicate = restarted.Submit(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref submits);
                return Task.FromResult(new TopicSendHandoff(true, "duplicate"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.AlreadyCompleted, duplicate.Kind);
        Assert.AreEqual(1, Volatile.Read(ref submits));

        await restarted.RequestReconciliationAsync(
            recovered,
            draftCleanup: new TopicSendDraftCleanup(
                _ => Task.FromResult(
                    TopicSendDraftCleanupResult.DraftClearPersisted)));
        Assert.IsFalse(store.TryGetUnresolved(snapshot.ScopeIdentity, out _));
        Assert.AreEqual(1, Volatile.Read(ref submits));
    }

    [TestMethod]
    public async Task NewerDraftSupersedesPendingClearAndRecordsSafeFinality()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var crash = new OneShotJournalCrash(
            "draft-clear-superseded",
            TopicSendJournalBoundary.AfterWrite);
        var coordinator = new TopicSendCoordinator(
            identityStore: store,
            journalFaultInjector: crash);
        var snapshot = CreateSnapshot(coordinator);

        coordinator.Submit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            draftCleanup: new TopicSendDraftCleanup(
                _ => Task.FromResult(
                    TopicSendDraftCleanupResult.DraftClearSuperseded)));
        await WaitUntilAsync(() => crash.Triggered, TimeSpan.FromSeconds(2));

        Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var record));
        Assert.AreEqual(
            TopicSendJournalCleanup.DraftClearSuperseded,
            record!.Cleanup);
        var restarted = new TopicSendCoordinator(identityStore: store);
        _ = restarted.CreateSnapshot(
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision,
            snapshot.DraftFingerprint,
            DateTimeOffset.UtcNow);
        Assert.IsFalse(store.TryGetUnresolved(snapshot.ScopeIdentity, out _));
    }

    [DataTestMethod]
    [DataRow(TopicSendReconciliationKind.Conflict)]
    [DataRow(TopicSendReconciliationKind.Corrupt)]
    public async Task RecoveredPreHandoff_ConflictingLedgerFencesWithoutSubmit(
        TopicSendReconciliationKind ledgerResult)
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException(
                "simulated process termination"));
        await WaitUntilAsync(
            () => first.RunningOperationCount == 0,
            TimeSpan.FromSeconds(2));

        var restarted = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: new SequencedReconciliationQuery(ledgerResult));
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(submitted.OperationId, recovered.OperationId);

        await restarted.RequestReconciliationAsync(recovered);
        Assert.IsTrue(restarted.TryGetOutcome(recovered.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Failed, outcome!.Kind);
        var executions = 0;
        var retry = restarted.Submit(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.IdentityConflict, retry.Kind);
        Assert.AreEqual(0, executions);
        Assert.IsTrue(store.TryGetUnresolved(submitted.ScopeIdentity, out var retained));
        Assert.AreEqual(submitted.OperationId, retained!.OperationId);
    }

    [TestMethod]
    public async Task RecoveredPreHandoff_AdoptsAuthoritativeRunFromTriggerLedger()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException(
                "simulated process termination"));
        await WaitUntilAsync(
            () => first.RunningOperationCount == 0,
            TimeSpan.FromSeconds(2));

        const string authoritativeRunId = "authoritative-ledger-run";
        var restarted = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: new DelegateTopicSendReconciliationQuery(
                (snapshot, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        new TopicSendReconciliationResult(
                            TopicSendReconciliationKind.Accepted,
                            AuthoritativeRunId: authoritativeRunId,
                            AuthoritativeLineId: snapshot.LineId,
                            AuthoritativeOutboxId: authoritativeRunId));
                }));
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow);

        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = restarted.RequestReconciliationAsync(
            recovered,
            draftCleanup: new TopicSendDraftCleanup(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                return TopicSendDraftCleanupResult.DraftClearPersisted;
            }));
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(store.TryGetUnresolved(recovered.ScopeIdentity, out var journal));
        Assert.AreEqual(authoritativeRunId, journal!.RunId);
        Assert.AreEqual(
            TopicSendJournalLifecycle.Terminal,
            journal.Lifecycle);
        releaseCleanup.TrySetResult();
        await recovery;
        Assert.IsTrue(restarted.TryGetOutcome(recovered.OperationId, out var outcome));
        Assert.AreEqual(TopicSendOutcomeKind.Accepted, outcome!.Kind);
        Assert.AreEqual(authoritativeRunId, outcome.AuthoritativeRunId);
        Assert.AreEqual(recovered.LineId, outcome.AuthoritativeLineId);
        Assert.AreEqual(authoritativeRunId, outcome.AuthoritativeOutboxId);
    }

    [TestMethod]
    public async Task RecoveredPreHandoff_UnavailableThenFound_NeverSubmitsAgain()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = first.CreateSnapshot(
            "thread",
            "device",
            1,
            "draft",
            DateTimeOffset.UtcNow,
            "account-a");
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var query = new AvailabilityReconciliationQuery(
            new(
                TopicSendReconciliationKind.Unavailable,
                "Database unavailable.",
                DiagnosticReason: "database_unavailable",
                AccountId: "account-a"));
        var restarted = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumReconciliationAttempts = 2,
                ReconciliationInitialBackoff = TimeSpan.FromMilliseconds(1),
                ReconciliationMaximumBackoff = TimeSpan.FromMilliseconds(2)
            },
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);

        await restarted.RequestReconciliationAsync(
            recovered,
            draftCleanup: PersistedDraftCleanup());
        var executions = 0;
        var fenced = restarted.Submit(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.ReconciliationRequired, fenced.Kind);
        Assert.AreEqual(0, executions);

        query.Set(new(
            TopicSendReconciliationKind.Accepted,
            AuthoritativeRunId: recovered.RunId,
            AuthoritativeLineId: recovered.LineId,
            AuthoritativeOutboxId: recovered.RunId,
            AccountId: recovered.AccountId));
        query.SignalAvailability();
        await WaitUntilAsync(
            () => restarted.TryGetOutcome(recovered.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.Accepted,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, executions);
    }

    [TestMethod]
    public async Task RecoveredPreHandoff_UnavailableThenNotFound_RetriesSameIdentityOnce()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = first.CreateSnapshot(
            "thread",
            "device",
            1,
            "draft",
            DateTimeOffset.UtcNow,
            "account-a");
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var query = new AvailabilityReconciliationQuery(
            new(TopicSendReconciliationKind.Unavailable));
        var restarted = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumReconciliationAttempts = 2,
                ReconciliationInitialBackoff = TimeSpan.FromMilliseconds(1),
                ReconciliationMaximumBackoff = TimeSpan.FromMilliseconds(2)
            },
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        await restarted.RequestReconciliationAsync(recovered);

        query.Set(new(TopicSendReconciliationKind.NotFound));
        query.SignalAvailability();
        await WaitUntilAsync(
            () => restarted.TryGetOutcome(recovered.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
            TimeSpan.FromSeconds(2));

        var executions = 0;
        TopicSendSnapshot? executed = null;
        var retry = restarted.Submit(
            recovered,
            candidate =>
            {
                executed = candidate;
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            draftCleanup: PersistedDraftCleanup());
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await WaitUntilAsync(
            () => restarted.TryGetOutcome(recovered.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.Accepted,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, executions);
        Assert.AreEqual(submitted.OperationId, executed!.OperationId);
        Assert.AreEqual(submitted.RunId, executed.RunId);
        Assert.AreEqual(submitted.LineId, executed.LineId);
    }

    [TestMethod]
    public async Task RecoveredPreHandoff_QueryFailuresAndAvailabilityFlapsRemainFenced()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = first.CreateSnapshot(
            "thread",
            "device",
            1,
            "draft",
            DateTimeOffset.UtcNow,
            "account-a");
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var query = new AvailabilityReconciliationQuery(
            new(
                TopicSendReconciliationKind.QueryFailed,
                DiagnosticReason: "SqliteException",
                AccountId: "account-a"));
        var restarted = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumReconciliationAttempts = 2,
                ReconciliationInitialBackoff = TimeSpan.FromMilliseconds(1),
                ReconciliationMaximumBackoff = TimeSpan.FromMilliseconds(2)
            },
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        await restarted.RequestReconciliationAsync(recovered);
        query.Set(new(
            TopicSendReconciliationKind.Unavailable,
            DiagnosticReason: "account_mismatch",
            AccountId: "account-b"));
        for (var index = 0; index < 5; index++)
            query.SignalAvailability();
        await WaitUntilAsync(() => !restarted.IsRunning(recovered.OperationId), TimeSpan.FromSeconds(2));

        var executions = 0;
        var retry = restarted.Submit(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.ReconciliationRequired, retry.Kind);
        Assert.AreEqual(0, executions);
        Assert.IsTrue(query.Calls >= 2);
    }

    [TestMethod]
    public async Task AvailabilityDuringFinalFailedQuery_RequeuesLatestGenerationOnce()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var query = new AvailabilityReconciliationQuery.BlockingAvailabilityReconciliationQuery();
        using var restarted = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumReconciliationAttempts = 1,
                ReconciliationInitialBackoff = TimeSpan.Zero,
                ReconciliationMaximumBackoff = TimeSpan.Zero
            },
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);

        var recovery = restarted.RequestReconciliationAsync(
            recovered,
            draftCleanup: PersistedDraftCleanup());
        await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        query.SetNext(new(
            TopicSendReconciliationKind.Accepted,
            AuthoritativeRunId: recovered.RunId,
            AuthoritativeLineId: recovered.LineId,
            AuthoritativeOutboxId: recovered.RunId));
        for (var index = 0; index < 100; index++)
            query.SignalAvailability();
        query.Release.TrySetResult();

        await recovery.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => restarted.TryGetOutcome(recovered.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.Accepted,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, query.Calls, "event storms must coalesce to one latest-generation retry");
    }

    [TestMethod]
    public async Task AvailabilityAfterNotFoundObservationBeforeAuthorization_RequeriesLatestGeneration()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var query =
            new AvailabilityReconciliationQuery.PostObservationAvailabilityQuery();
        using var restarted = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var callbacks = 0;

        await restarted.RequestReconciliationAsync(
            recovered,
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.RetryableFailed)
                    Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            });
        await WaitUntilAsync(
            () => restarted.TryGetOutcome(recovered.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.Accepted,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, query.Calls);
        Assert.AreEqual(0, query.AuthorizationsIssued);
        Assert.AreEqual(0, callbacks, "the stale NotFound must never be published or parked");
    }

    [TestMethod]
    public async Task DisposeBetweenYieldAndHandoffLease_PreventsHandoffInvocation()
    {
        var probe = new LifecycleProbe("observe-before-handoff-lease");
        var coordinator = new TopicSendCoordinator(
            null, null, null, null, null, probe);
        var snapshot = CreateSnapshot(coordinator);
        var handoffs = 0;

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref handoffs);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            }));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        probe.Release.TrySetResult();
        await Task.Delay(50);

        Assert.AreEqual(0, Volatile.Read(ref handoffs));
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
    }

    [TestMethod]
    public async Task DisposeAfterHandoffCommit_WaitsUntilResultIsJournaled()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var probe = new LifecycleProbe("journal:terminal:before-write");
        var coordinator = new TopicSendCoordinator(
            null, null, store, null, null, probe);
        var snapshot = CreateSnapshot(coordinator);
        var handoffReturned = NewSignal();

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ =>
            {
                handoffReturned.TrySetResult();
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            }));
        await handoffReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(
            disposal.IsCompleted,
            "disposal must drain the handoff lease until its committed result is journaled");
        probe.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(store.TryGetUnresolved(snapshot.ScopeIdentity, out var journal));
        Assert.AreEqual(TopicSendJournalLifecycle.Terminal, journal!.Lifecycle);
        Assert.AreEqual(TopicSendJournalCleanup.DraftClearPending, journal.Cleanup);
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
    }

    [TestMethod]
    public async Task DisposeBeforeReconcileQueryLease_PreventsQueryInvocation()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var submitted = await PersistAcceptedOrUnknownAsync(store);
        var probe = new LifecycleProbe("reconcile-before-query-lease");
        var calls = 0;
        var query = new DelegateTopicSendReconciliationQuery(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(
                    new TopicSendReconciliationResult(
                        TopicSendReconciliationKind.Accepted));
            });
        var coordinator = new TopicSendCoordinator(
            null, null, store, query, null, probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);

        var recovery = coordinator.RequestReconciliationAsync(recovered);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        probe.Release.TrySetResult();
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, Volatile.Read(ref calls));
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
    }

    [TestMethod]
    public async Task DisposeAtReconcileQueryReturned_WaitsForResultFence()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var submitted = await PersistAcceptedOrUnknownAsync(store);
        var probe = new LifecycleProbe("reconcile-query-returned");
        var coordinator = new TopicSendCoordinator(
            null,
            null,
            store,
            new DelegateTopicSendReconciliationQuery(
                (snapshot, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        new TopicSendReconciliationResult(
                            TopicSendReconciliationKind.Accepted,
                            AuthoritativeRunId: snapshot.RunId,
                            AuthoritativeLineId: snapshot.LineId,
                            AuthoritativeOutboxId: snapshot.RunId));
                }),
            null,
            probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var callbacks = 0;

        var recovery = coordinator.RequestReconciliationAsync(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            },
            PersistedDraftCleanup());
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(
            disposal.IsCompleted,
            "disposal must wait while a returned query result remains under its lease");
        probe.Release.TrySetResult();
        await Task.WhenAll(disposal, recovery).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(store.TryGetUnresolved(recovered.ScopeIdentity, out var journal));
        Assert.AreEqual(TopicSendJournalLifecycle.Terminal, journal!.Lifecycle);
        Assert.AreEqual(TopicSendJournalCleanup.DraftClearPending, journal.Cleanup);
        Assert.AreEqual(0, callbacks);
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
    }

    [TestMethod]
    public async Task SnapshotMismatchInvalidation_DisposalWaitsForHandoffLease()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));
        await first.DisposeAsync();

        var probe = new LifecycleProbe("retry-snapshot-mismatch");
        var coordinator = new TopicSendCoordinator(
            null,
            null,
            store,
            new SequencedReconciliationQuery(TopicSendReconciliationKind.NotFound),
            null,
            probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        await coordinator.RequestReconciliationAsync(recovered);
        var durableCalls = 0;
        var tampered = recovered with { DraftFingerprint = "snapshot-mismatch" };

        var retry = coordinator.Submit(
            tampered,
            (_, context) =>
            {
                context.AuthorizeDurableHandoff();
                Interlocked.Increment(ref durableCalls);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(
            disposal.IsCompleted,
            "snapshot token invalidation must remain covered by the active handoff lease");
        probe.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, Volatile.Read(ref durableCalls));
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
    }

    [TestMethod]
    public async Task DisposeWhileQueryBlocked_ReleaseCannotMutateJournalTokenOrCallback()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.IsTrue(store.TryGetUnresolved(submitted.ScopeIdentity, out var before));

        var query =
            new AvailabilityReconciliationQuery.DisposeBlockedNotFoundQuery();
        var restarted = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var callbacks = 0;
        _ = restarted.RequestReconciliationAsync(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            });
        await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = restarted.DisposeAsync().AsTask();
        Assert.IsFalse(
            disposal.IsCompleted,
            "disposal must wait for a query that ignores cancellation to return");
        query.Release.TrySetResult();
        await query.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, query.AuthorizationsIssued);
        Assert.AreEqual(0, callbacks);
        Assert.IsTrue(store.TryGetUnresolved(submitted.ScopeIdentity, out var after));
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task DisposeAfterQueryBeforeMutation_AbortsContinuationWithNoPostDisposeEffects()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.IsTrue(store.TryGetUnresolved(submitted.ScopeIdentity, out var before));

        var probe = new LifecycleProbe("prehandoff-query-returned");
        var query = new SequencedReconciliationQuery(TopicSendReconciliationKind.Accepted);
        var coordinator = new TopicSendCoordinator(
            null, null, store, query, null, probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var callbacks = 0;
        var recovery = Task.Run(() => coordinator.RequestReconciliationAsync(
            recovered,
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            },
            PersistedDraftCleanup()));

        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(
            disposal.IsCompleted,
            "the pre-handoff query-return barrier is still covered by its query lease");
        probe.Release.TrySetResult();
        await Task.WhenAll(disposal, recovery).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, callbacks);
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
        Assert.IsTrue(store.TryGetUnresolved(submitted.ScopeIdentity, out var after));
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task DisposeDuringLeasedJournalTransaction_WaitsThenReturnsQuiescent()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var probe = new LifecycleProbe(
            "journal:prehandoff-ledger-found:before-write");
        var coordinator = new TopicSendCoordinator(
            null,
            null,
            store,
            new SequencedReconciliationQuery(TopicSendReconciliationKind.Accepted),
            null,
            probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var recovery = Task.Run(() => coordinator.RequestReconciliationAsync(
            recovered,
            draftCleanup: PersistedDraftCleanup()));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted, "disposal must drain the acquired mutation lease");
        probe.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
    }

    [TestMethod]
    public async Task DisposeDuringReconcileCleanupAwait_StopsLaterJournalAndCallbackMutation()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, context) =>
            {
                context.MarkDurableBoundaryEntered();
                throw new IOException("response lost");
            });
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var probe = new LifecycleProbe("not-used");
        var coordinator = new TopicSendCoordinator(
            null,
            null,
            store,
            new SequencedReconciliationQuery(TopicSendReconciliationKind.Accepted),
            null,
            probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var cleanupEntered = NewSignal();
        var cleanupRelease = NewSignal();
        var recovery = coordinator.RequestReconciliationAsync(
            recovered,
            draftCleanup: new TopicSendDraftCleanup(async _ =>
            {
                cleanupEntered.TrySetResult();
                await cleanupRelease.Task;
                return TopicSendDraftCleanupResult.DraftClearPersisted;
            }));
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted, "disposal must drain the cleanup callback lease");
        cleanupRelease.TrySetResult();
        await Task.WhenAll(disposal, recovery).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
        Assert.IsTrue(store.TryGetUnresolved(recovered.ScopeIdentity, out var terminal));
        Assert.AreEqual(TopicSendJournalCleanup.DraftClearPending, terminal!.Cleanup);
    }

    [TestMethod]
    public async Task DisposeDuringReconcileQuery_CancelsPendingWorkAndReturnsQuiescent()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, context) =>
            {
                context.MarkDurableBoundaryEntered();
                throw new IOException("response lost");
            });
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var probe = new LifecycleProbe("not-used");
        var query = new CancellationAwareReconciliationQuery();
        var coordinator = new TopicSendCoordinator(
            null, null, store, query, null, probe);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        var recovery = coordinator.RequestReconciliationAsync(
            recovered,
            draftCleanup: PersistedDraftCleanup());
        await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(recovery, query.CancellationObserved.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
        Assert.AreEqual(0, probe.CallbacksAfterDisposal);
    }

    [TestMethod]
    public async Task DisposeAfterRendererEnqueue_InvalidatesQueuedGenerationWithoutWaitingRenderer()
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSnapshot(coordinator);
        var dispatcher = new BarrierDispatcher();
        var callbacks = 0;
        var subscription = coordinator.Observe(
            snapshot.OperationId,
            "renderer",
            dispatcher,
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            });
        Assert.IsNull(subscription, "an operation must exist before observation");

        var handoff = NewSignal();
        coordinator.Submit(
            snapshot,
            async _ =>
            {
                await handoff.Task;
                return new TopicSendHandoff(true, "accepted");
            },
            draftCleanup: PersistedDraftCleanup());
        subscription = coordinator.Observe(
            snapshot.OperationId,
            "renderer",
            dispatcher,
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            });
        Assert.IsNotNull(subscription);
        handoff.TrySetResult();
        await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.RunAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, callbacks);
        Assert.AreEqual(0, subscription.InFlightCallbackCount);
    }

    [TestMethod]
    public async Task AuthorizationAndAvailability_OppositeScheduleCompletesWithoutLockInversion()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var authority = new OppositeScheduleAuthority();
        await using var coordinator = new TopicSendCoordinator(
            identityStore: store,
            reconciliationQuery: authority);
        var recovered = coordinator.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        await coordinator.RequestReconciliationAsync(recovered);

        var executions = 0;
        var completed = NewSignal();
        var retry = coordinator.Submit(
            recovered,
            (_, context) =>
            {
                context.AuthorizeDurableHandoff();
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.Accepted)
                    completed.TrySetResult();
                return Task.CompletedTask;
            },
            draftCleanup: PersistedDraftCleanup());
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await authority.ConsumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        authority.ArmAvailabilityCheck();
        var availability = Task.Run(authority.SignalAvailability);
        await authority.AvailabilityCheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        authority.ReleaseConsume.TrySetResult();

        await Task.WhenAll(availability, completed.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, executions);
        Assert.AreEqual(
            new TopicSendAuthorizationScope("account-a", "database-a", 1),
            authority.ConsumedScope);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JournalCheckpointFailure_ReleasesMutationLease(bool cancellation)
    {
        var probe = new LifecycleProbe(
            "journal:pre-handoff:before-write",
            cancellation
                ? new OperationCanceledException("injected cancellation")
                : new IOException("injected failure"));
        var coordinator = new TopicSendCoordinator(
            null, null, null, null, null, probe);
        var snapshot = CreateSnapshot(coordinator);

        var result = coordinator.Submit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "unexpected")));
        Assert.AreEqual(TopicSendSubmissionKind.PersistenceFailed, result.Kind);
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, probe.JournalWritesAfterDisposal);
    }

    [TestMethod]
    public async Task AvailabilityStormAfterDispose_DoesNotStartAnotherReconciler()
    {
        var store = new InMemoryTopicSendIdentityStore();
        var first = new TopicSendCoordinator(identityStore: store);
        var submitted = CreateSnapshot(first);
        first.Submit(
            submitted,
            (_, _) => throw new TopicSendJournalCrashException("process loss"));
        await WaitUntilAsync(() => first.RunningOperationCount == 0, TimeSpan.FromSeconds(2));

        var query = new AvailabilityReconciliationQuery(
            new(TopicSendReconciliationKind.QueryFailed));
        var restarted = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                MaximumReconciliationAttempts = 1,
                ReconciliationInitialBackoff = TimeSpan.Zero,
                ReconciliationMaximumBackoff = TimeSpan.Zero
            },
            identityStore: store,
            reconciliationQuery: query);
        var recovered = restarted.CreateSnapshot(
            submitted.ThreadId,
            submitted.TargetDeviceId,
            submitted.ComposerRevision,
            submitted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            submitted.AccountId);
        await restarted.RequestReconciliationAsync(recovered);
        var callsAtDispose = query.Calls;

        restarted.Dispose();
        for (var index = 0; index < 100; index++)
            query.SignalAvailability();

        Assert.AreEqual(callsAtDispose, query.Calls);
        Assert.AreEqual(0, restarted.RunningOperationCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CollectibleObserverAttachment AttachCollectibleObserver(
        TopicSendCoordinator coordinator,
        TopicSendSnapshot snapshot)
    {
        var target = new ObserverTarget();
        var weak = new WeakReference(target);
        var subscription = coordinator.Observe(
            snapshot.OperationId,
            Guid.NewGuid().ToString("n"),
            target.OnOutcomeAsync);
        Assert.IsNotNull(subscription);
        return new(weak, subscription);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BarrierObserverAttachment AttachBarrierObserver(
        TopicSendCoordinator coordinator,
        TopicSendSnapshot snapshot,
        TaskCompletionSource release,
        string completion)
    {
        var counter = new BarrierObserverCounter();
        var target = new BarrierObserverTarget(release, completion, counter);
        var weak = new WeakReference(target);
        var subscription = coordinator.Observe(
            snapshot.OperationId,
            Guid.NewGuid().ToString("n"),
            target.OnOutcomeAsync);
        Assert.IsNotNull(subscription);
        return new(
            weak,
            target.Entered,
            subscription,
            counter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task<TopicSendSnapshot> PersistAcceptedOrUnknownAsync(
        InMemoryTopicSendIdentityStore store)
    {
        var coordinator = new TopicSendCoordinator(identityStore: store);
        var snapshot = CreateSnapshot(coordinator);
        coordinator.Submit(
            snapshot,
            (_, context) =>
            {
                context.MarkDurableBoundaryEntered();
                throw new TopicSendJournalCrashException("process loss");
            });
        await WaitUntilAsync(
            () => coordinator.RunningOperationCount == 0,
            TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();
        return snapshot;
    }

    private static TopicSendSnapshot CreateSnapshot(TopicSendCoordinator coordinator)
        => coordinator.CreateSnapshot(
            "thread",
            "device",
            1,
            "fingerprint",
            DateTimeOffset.UtcNow,
            "account-a");

    private static TopicSendDraftCleanup PersistedDraftCleanup()
        => new(_ => Task.FromResult(
            TopicSendDraftCleanupResult.DraftClearPersisted));

    private static TopicSendIdentityRecord TestJournalRecord(
        TopicSendSnapshot snapshot,
        long stateSequence,
        TopicSendJournalLifecycle lifecycle,
        TopicSendJournalCleanup cleanup,
        TopicSendOutcomeKind? outcome = null)
        => new(
            snapshot.LogicalIdentity,
            snapshot.ScopeIdentity,
            snapshot.SubmissionSequence,
            snapshot.ComposerRevision,
            snapshot.OperationId,
            snapshot.RunId,
            snapshot.LineId,
            snapshot.DraftFingerprint,
            outcome,
            Version: TopicSendIdentityRecord.CurrentVersion,
            Lifecycle: lifecycle,
            Cleanup: cleanup,
            AccountId: snapshot.AccountId,
            StateSequence: stateSequence);

    private static void SaveAtBarrier(
        ITopicSendIdentityStore store,
        TopicSendIdentityRecord record,
        ManualResetEventSlim start,
        ref int conflicts)
    {
        start.Wait();
        try
        {
            store.Save(record);
        }
        catch (TopicSendJournalConflictException)
        {
            Interlocked.Increment(ref conflicts);
        }
    }

    private static void AssertJournalConflict(Action action)
    {
        try
        {
            action();
            Assert.Fail("Expected the conflicting journal transition to be fenced.");
        }
        catch (TopicSendJournalConflictException)
        {
        }
    }

    private static KeyValueTopicSendIdentityStore CreatePersistedIdentityStore(
        Dictionary<string, string> persisted)
        => new(
            key => persisted.TryGetValue(key, out var value) ? value : null,
            (key, value) => persisted[key] = value,
            key => persisted.Remove(key));

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate() && stopwatch.Elapsed < timeout)
            await Task.Delay(10);
        Assert.IsTrue(predicate());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FinalizationProbe : ITopicSendLifecycleTestObserver
    {
        private readonly object gate = new();
        private readonly Dictionary<string, TaskCompletionSource> signals =
            new(StringComparer.Ordinal);

        public Task Track(string operationId)
        {
            lock (gate)
            {
                var signal = NewSignal();
                signals.Add(operationId, signal);
                return signal.Task;
            }
        }

        public void FinalizationCompleted(
            string operationId,
            TopicSendIdentityRecord record,
            bool cached)
        {
            TaskCompletionSource? signal;
            lock (gate)
                signals.TryGetValue(operationId, out signal);
            signal?.TrySetResult();
        }

        public void Checkpoint(string name)
        {
        }

        public void JournalWrite(string transition, bool disposalCompleted)
        {
        }

        public void CallbackQueued(string operationId, bool disposalCompleted)
        {
        }
    }

    private sealed class AfterCompactionBarrier : ITopicSendJournalFaultInjector
    {
        private int blocked;
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();

        public void Checkpoint(
            string transition,
            TopicSendJournalBoundary boundary,
            TopicSendIdentityRecord record)
        {
            if (!string.Equals(transition, "final-compaction", StringComparison.Ordinal)
                || boundary != TopicSendJournalBoundary.AfterCompaction
                || Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class ObserverTarget
    {
        public Task OnOutcomeAsync(TopicSendOutcome _)
            => Task.CompletedTask;
    }

    private sealed class BarrierObserverTarget(
        TaskCompletionSource release,
        string completion,
        BarrierObserverCounter counter)
    {
        public TaskCompletionSource Entered { get; } = NewSignal();

        public Task OnOutcomeAsync(TopicSendOutcome _)
            => CompleteAsync(release, completion, counter, Entered);

        private static async Task CompleteAsync(
            TaskCompletionSource release,
            string completion,
            BarrierObserverCounter counter,
            TaskCompletionSource entered)
        {
            Interlocked.Increment(ref counter.InvocationCount);
            entered.TrySetResult();
            await release.Task;
            if (completion == "error")
                throw new InvalidOperationException("callback failure");
            if (completion == "cancellation")
                throw new OperationCanceledException("callback cancellation");
        }
    }

    private sealed class BarrierObserverCounter
    {
        public int InvocationCount;
    }

    private sealed class SequencedReconciliationQuery(
        params TopicSendReconciliationKind[] results)
        : ITopicSendReconciliationQuery, ITopicSendAuthorizationAuthority
    {
        private int index;
        private readonly TopicSendAuthorizationScope scope =
            new("account-a", "database-a", 1);
        public int Calls => Volatile.Read(ref index);

        public ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref index) - 1;
            return ValueTask.FromResult(new TopicSendReconciliationResult(
                results[Math.Min(current, results.Length - 1)],
                AccountId: snapshot.AccountId,
                DatabaseIdentity: scope.DatabaseIdentity,
                DatabaseGeneration: scope.DatabaseGeneration,
                ObservationVersion: current + 1,
                ObservedAt: DateTimeOffset.UtcNow));
        }

        public bool TryConsume(TopicSendAuthorizationScope expected, Func<bool> consume)
            => IsCurrent(expected) && consume();

        public bool IsCurrent(TopicSendAuthorizationScope expected)
            => expected == scope;
    }

    private sealed class CancellableReconciliationQuery
        : ITopicSendReconciliationQuery
    {
        public TaskCompletionSource Entered { get; } = NewSignal();
        public bool CancellationObserved { get; private set; }

        public async ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new(TopicSendReconciliationKind.Unknown);
            }

            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

    }

    private sealed class AvailabilityReconciliationQuery(
        TopicSendReconciliationResult result)
        : ITopicSendReconciliationQuery,
          ITopicSendReconciliationAvailability,
          ITopicSendAuthorizationAuthority
    {
        private TopicSendReconciliationResult current = result;
        private int calls;
        private TopicSendAuthorizationScope scope =
            new("account-a", "database-a", 1);

        public event Action? AvailabilityChanged;
        public int Calls => Volatile.Read(ref calls);

        public ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = Interlocked.Increment(ref calls);
            var result = Volatile.Read(ref current);
            return ValueTask.FromResult(result with
            {
                AccountId = result.AccountId ?? snapshot.AccountId,
                DatabaseIdentity = result.DatabaseIdentity ?? scope.DatabaseIdentity,
                DatabaseGeneration = result.DatabaseGeneration == 0
                    ? scope.DatabaseGeneration
                    : result.DatabaseGeneration,
                ObservationVersion = result.ObservationVersion == 0
                    ? observation
                    : result.ObservationVersion,
                ObservedAt = result.ObservedAt == default
                    ? DateTimeOffset.UtcNow
                    : result.ObservedAt
            });
        }

        public sealed class PostObservationAvailabilityQuery
            : ITopicSendReconciliationQuery,
              ITopicSendReconciliationAvailability,
              ITopicSendAuthorizationAuthority
        {
            private readonly TopicSendAuthorizationScope scope =
                new("account-a", "database-a", 1);
            private int calls;
            private int authorizationsIssued;

            public event Action? AvailabilityChanged;
            public int Calls => Volatile.Read(ref calls);
            public int AuthorizationsIssued => Volatile.Read(ref authorizationsIssued);

            public ValueTask<TopicSendReconciliationResult> QueryAsync(
                TopicSendSnapshot snapshot,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref calls);
                var result = new TopicSendReconciliationResult(
                    call == 1
                        ? TopicSendReconciliationKind.NotFound
                        : TopicSendReconciliationKind.Accepted,
                    AuthoritativeRunId: call == 1 ? null : snapshot.RunId,
                    AuthoritativeLineId: call == 1 ? null : snapshot.LineId,
                    AuthoritativeOutboxId: call == 1 ? null : snapshot.RunId,
                    AccountId: snapshot.AccountId,
                    DatabaseIdentity: scope.DatabaseIdentity,
                    DatabaseGeneration: scope.DatabaseGeneration,
                    ObservationVersion: call,
                    ObservedAt: DateTimeOffset.UtcNow);
                if (call == 1)
                    AvailabilityChanged?.Invoke();
                return ValueTask.FromResult(result);
            }

            public TopicSendRetryAuthorization? IssueRetryAuthorization(
                TopicSendSnapshot snapshot,
                TopicSendReconciliationResult observation)
            {
                Interlocked.Increment(ref authorizationsIssued);
                return null;
            }

            public bool TryConsume(TopicSendAuthorizationScope expected, Func<bool> consume)
                => IsCurrent(expected) && consume();

            public bool IsCurrent(TopicSendAuthorizationScope expected)
                => expected == scope;
        }

        public sealed class DisposeBlockedNotFoundQuery
            : ITopicSendReconciliationQuery,
              ITopicSendAuthorizationAuthority
        {
            private readonly TopicSendAuthorizationScope scope =
                new("account-a", "database-a", 1);
            private int authorizationsIssued;

            public TaskCompletionSource Entered { get; } = NewSignal();
            public TaskCompletionSource Release { get; } = NewSignal();
            public TaskCompletionSource Returned { get; } = NewSignal();
            public int AuthorizationsIssued => Volatile.Read(ref authorizationsIssued);

            public async ValueTask<TopicSendReconciliationResult> QueryAsync(
                TopicSendSnapshot snapshot,
                CancellationToken cancellationToken)
            {
                Entered.TrySetResult();
                await Release.Task;
                Returned.TrySetResult();
                return new(
                    TopicSendReconciliationKind.NotFound,
                    AccountId: snapshot.AccountId,
                    DatabaseIdentity: scope.DatabaseIdentity,
                    DatabaseGeneration: scope.DatabaseGeneration,
                    ObservationVersion: 1,
                    ObservedAt: DateTimeOffset.UtcNow);
            }

            public TopicSendRetryAuthorization? IssueRetryAuthorization(
                TopicSendSnapshot snapshot,
                TopicSendReconciliationResult observation)
            {
                Interlocked.Increment(ref authorizationsIssued);
                return null;
            }

            public bool TryConsume(TopicSendAuthorizationScope expected, Func<bool> consume)
                => IsCurrent(expected) && consume();

            public bool IsCurrent(TopicSendAuthorizationScope expected)
                => expected == scope;
        }

        public sealed class BlockingAvailabilityReconciliationQuery
            : ITopicSendReconciliationQuery,
              ITopicSendReconciliationAvailability,
              ITopicSendAuthorizationAuthority
        {
            private readonly TopicSendAuthorizationScope scope =
                new("account-a", "database-a", 1);
            private TopicSendReconciliationResult next =
                new(TopicSendReconciliationKind.QueryFailed);
            private int calls;

            public event Action? AvailabilityChanged;
            public TaskCompletionSource Entered { get; } = NewSignal();
            public TaskCompletionSource Release { get; } = NewSignal();
            public int Calls => Volatile.Read(ref calls);

            public async ValueTask<TopicSendReconciliationResult> QueryAsync(
                TopicSendSnapshot snapshot,
                CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                    return Scoped(
                        new(TopicSendReconciliationKind.QueryFailed),
                        snapshot,
                        call);
                }
                return Scoped(Volatile.Read(ref next), snapshot, call);
            }

            public void SetNext(TopicSendReconciliationResult result)
                => Volatile.Write(ref next, result);

            public void SignalAvailability()
                => AvailabilityChanged?.Invoke();

            public bool TryConsume(TopicSendAuthorizationScope expected, Func<bool> consume)
                => IsCurrent(expected) && consume();

            public bool IsCurrent(TopicSendAuthorizationScope expected)
                => expected == scope;

            private TopicSendReconciliationResult Scoped(
                TopicSendReconciliationResult result,
                TopicSendSnapshot snapshot,
                long observation)
                => result with
                {
                    AccountId = snapshot.AccountId,
                    DatabaseIdentity = scope.DatabaseIdentity,
                    DatabaseGeneration = scope.DatabaseGeneration,
                    ObservationVersion = observation,
                    ObservedAt = DateTimeOffset.UtcNow
                };
        }

        public void Set(TopicSendReconciliationResult next)
            => Volatile.Write(ref current, next);

        public void SignalAvailability()
            => AvailabilityChanged?.Invoke();

        public void SetScope(TopicSendAuthorizationScope next)
            => Volatile.Write(ref scope, next);

        public bool TryConsume(TopicSendAuthorizationScope expected, Func<bool> consume)
            => IsCurrent(expected) && consume();

        public bool IsCurrent(TopicSendAuthorizationScope expected)
            => expected == Volatile.Read(ref scope);
    }

    private sealed class LifecycleProbe(
        string blockedCheckpoint,
        Exception? checkpointException = null) : ITopicSendLifecycleTestObserver
    {
        private int blocked;
        private int journalWritesAfterDisposal;
        private int callbacksAfterDisposal;

        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public int JournalWritesAfterDisposal => Volatile.Read(ref journalWritesAfterDisposal);
        public int CallbacksAfterDisposal => Volatile.Read(ref callbacksAfterDisposal);

        public void Checkpoint(string name)
        {
            if (!string.Equals(name, blockedCheckpoint, StringComparison.Ordinal)
                || Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            Entered.TrySetResult();
            if (checkpointException is not null)
                throw checkpointException;
            Release.Task.GetAwaiter().GetResult();
        }

        public void JournalWrite(string transition, bool disposalCompleted)
        {
            if (disposalCompleted)
                Interlocked.Increment(ref journalWritesAfterDisposal);
        }

        public void CallbackQueued(string operationId, bool disposalCompleted)
        {
            if (disposalCompleted)
                Interlocked.Increment(ref callbacksAfterDisposal);
        }
    }

    private sealed class BarrierDispatcher : ITopicSendObserverDispatcher
    {
        private readonly TaskCompletionSource completion = NewSignal();
        private Func<Task>? workItem;

        public TaskCompletionSource Queued { get; } = NewSignal();

        public Task InvokeAsync(Func<Task> candidate)
        {
            Assert.IsNull(Interlocked.CompareExchange(ref workItem, candidate, null));
            Queued.TrySetResult();
            return completion.Task;
        }

        public async Task RunAsync()
        {
            var candidate = Interlocked.Exchange(ref workItem, null);
            Assert.IsNotNull(candidate);
            try
            {
                await candidate();
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }

    private sealed class OppositeScheduleAuthority
        : ITopicSendReconciliationQuery,
          ITopicSendReconciliationAvailability,
          ITopicSendAuthorizationAuthority
    {
        private readonly object profileGate = new();
        private readonly TopicSendAuthorizationScope scope =
            new("account-a", "database-a", 1);
        private int observation;
        private int availabilityArmed;
        private TopicSendAuthorizationScope? consumedScope;

        public event Action? AvailabilityChanged;
        public TaskCompletionSource ConsumeEntered { get; } = NewSignal();
        public TaskCompletionSource AvailabilityCheckEntered { get; } = NewSignal();
        public TaskCompletionSource ReleaseConsume { get; } = NewSignal();
        public TopicSendAuthorizationScope? ConsumedScope
            => Volatile.Read(ref consumedScope);

        public ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = Interlocked.Increment(ref observation);
            return ValueTask.FromResult(new TopicSendReconciliationResult(
                TopicSendReconciliationKind.NotFound,
                AccountId: snapshot.AccountId,
                DatabaseIdentity: scope.DatabaseIdentity,
                DatabaseGeneration: scope.DatabaseGeneration,
                ObservationVersion: version,
                ObservedAt: DateTimeOffset.UtcNow));
        }

        public bool TryConsume(TopicSendAuthorizationScope expected, Func<bool> consume)
        {
            lock (profileGate)
            {
                ConsumeEntered.TrySetResult();
                ReleaseConsume.Task.GetAwaiter().GetResult();
                if (expected != scope || !consume()) return false;
                Volatile.Write(ref consumedScope, expected);
                return true;
            }
        }

        public bool IsCurrent(TopicSendAuthorizationScope expected)
        {
            if (Volatile.Read(ref availabilityArmed) != 0)
                AvailabilityCheckEntered.TrySetResult();
            lock (profileGate)
                return expected == scope;
        }

        public void ArmAvailabilityCheck()
            => Volatile.Write(ref availabilityArmed, 1);

        public void SignalAvailability()
            => AvailabilityChanged?.Invoke();
    }

    private sealed class CancellationAwareReconciliationQuery
        : ITopicSendReconciliationQuery
    {
        private readonly TaskCompletionSource never = NewSignal();

        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource CancellationObserved { get; } = NewSignal();

        public async ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await never.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The cancellation barrier unexpectedly completed.");
        }
    }

    private sealed class OneShotJournalCrash(
        string transition,
        TopicSendJournalBoundary boundary) : ITopicSendJournalFaultInjector
    {
        private int triggered;
        public bool Triggered => Volatile.Read(ref triggered) != 0;

        public void Checkpoint(
            string candidateTransition,
            TopicSendJournalBoundary candidateBoundary,
            TopicSendIdentityRecord record)
        {
            if (!string.Equals(candidateTransition, transition, StringComparison.Ordinal)
                || candidateBoundary != boundary
                || Interlocked.Exchange(ref triggered, 1) != 0)
                return;
            throw new TopicSendJournalCrashException("simulated crash");
        }
    }

    private sealed record CollectibleObserverAttachment(
        WeakReference Target,
        ITopicSendObserverSubscription Subscription);

    private sealed record BarrierObserverAttachment(
        WeakReference Target,
        TaskCompletionSource Entered,
        ITopicSendObserverSubscription Subscription,
        BarrierObserverCounter Counter);

    private sealed class ComponentLifecycle : IDisposable
    {
        private readonly TopicSendCoordinator coordinator;
        private readonly string lifecycleId = Guid.NewGuid().ToString("n");
        private readonly List<ITopicSendObserverSubscription> subscriptions = new();

        public ComponentLifecycle(TopicSendCoordinator coordinator)
        {
            this.coordinator = coordinator;
        }

        public bool Attach(
            TopicSendSnapshot snapshot,
            Func<TopicSendOutcome, Task> observer)
        {
            var subscription = coordinator.Observe(
                snapshot.OperationId,
                lifecycleId,
                observer);
            if (subscription is null) return false;
            subscriptions.Add(subscription);
            return true;
        }

        public void Dispose()
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
            subscriptions.Clear();
        }
    }

    private sealed class RestoreLifecycle : IDisposable
    {
        private readonly UiOperationCoordinator operations;
        private readonly CancellationTokenSource lifetime = new();
        private readonly string lifecycleId = Guid.NewGuid().ToString("n");
        private long schedule;

        public RestoreLifecycle(UiOperationCoordinator operations)
        {
            this.operations = operations;
        }

        public Task<string> RestoreAsync(
            string entityId,
            Func<CancellationToken, Task<string>> restore)
        {
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void Schedule()
            {
                var key = $"topic.open:{lifecycleId}:{entityId}:{Interlocked.Increment(ref schedule)}";
                Assert.IsTrue(operations.TryRun(
                    key,
                    "ui.topic.open",
                    restore,
                    outcome =>
                    {
                        if (outcome.Kind == UiOperationOutcomeKind.Succeeded)
                            completion.TrySetResult(outcome.Result!);
                        else if (outcome.Kind == UiOperationOutcomeKind.Failed)
                            Schedule();
                        return Task.CompletedTask;
                    },
                    lifetime.Token));
            }

            Schedule();
            return completion.Task;
        }

        public void Dispose()
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}
