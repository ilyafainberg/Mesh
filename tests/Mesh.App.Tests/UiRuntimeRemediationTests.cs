using System.Diagnostics;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class UiRuntimeRemediationTests
{
    [TestMethod]
    public async Task TopicSend_HeldHandoffReturnsPromptlyAndRepeatedTapIsSingleFlight()
    {
        var coordinator = new TopicSendCoordinator();
        var entered = NewSignal();
        var release = NewSignal();
        var completed = NewSignal();
        var executions = 0;
        var callbacks = 0;
        var snapshot = CreateSubmission(coordinator);

        var stopwatch = Stopwatch.StartNew();
        var started = coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                await release.Task;
                return new TopicSendHandoff(true, "pending_local");
            },
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                completed.TrySetResult();
                return Task.CompletedTask;
            });
        stopwatch.Stop();

        Assert.IsTrue(started);
        Assert.IsTrue(
            stopwatch.ElapsedMilliseconds < 100,
            $"Send scheduling blocked for {stopwatch.ElapsedMilliseconds} ms.");
        Assert.IsFalse(coordinator.TrySubmit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted"))));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref executions));
        Assert.AreEqual(1, Volatile.Read(ref callbacks));
        Assert.IsFalse(coordinator.TrySubmit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "duplicate-after-accept"))));
    }

    [TestMethod]
    public void TopicSendIdentity_IsStableForTheSameComposerRevision()
    {
        var coordinator = new TopicSendCoordinator();
        var first = CreateSubmission(coordinator);
        var second = coordinator.CreateSnapshot(
            "thread",
            "device",
            7,
            "fingerprint",
            first.SubmittedAt.AddSeconds(1));

        Assert.AreEqual(first.OperationId, second.OperationId);
        Assert.AreEqual(first.RunId, second.RunId);
        Assert.AreEqual(first.LineId, second.LineId);
    }

    [TestMethod]
    public async Task TopicSendPreHandoffFailure_SameSnapshotIsRetryable()
    {
        var coordinator = new TopicSendCoordinator(
            reconciliationQuery: new ScopedNotFoundReconciliationQuery());
        var firstCompleted = new TaskCompletionSource<TopicSendOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retryCompleted = new TaskCompletionSource<TopicSendOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var executions = 0;
        var snapshot = coordinator.CreateSnapshot(
            "thread",
            "device",
            7,
            "fingerprint",
            DateTimeOffset.UtcNow,
            "account-a");

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromException<TopicSendHandoff>(
                    new InvalidOperationException("held storage failed"));
            },
            outcome =>
            {
                Interlocked.Increment(ref callbackCount);
                firstCompleted.TrySetResult(outcome);
                return Task.CompletedTask;
            }));

        Assert.AreEqual(
            TopicSendOutcomeKind.RetryableFailed,
            (await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            outcome =>
            {
                Interlocked.Increment(ref callbackCount);
                retryCompleted.TrySetResult(outcome);
                return Task.CompletedTask;
            }));

        Assert.AreEqual(
            TopicSendOutcomeKind.Accepted,
            (await retryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
        Assert.AreEqual(2, Volatile.Read(ref executions));
        Assert.AreEqual(2, Volatile.Read(ref callbackCount));
    }

    [TestMethod]
    public async Task TopicSendRejection_SameRevisionCanRetry()
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = CreateSubmission(coordinator);
        var rejected = new TaskCompletionSource<TopicSendOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<TopicSendOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(false, "not_ready")),
            outcome =>
            {
                Interlocked.Increment(ref callbacks);
                rejected.TrySetResult(outcome);
                return Task.CompletedTask;
            }));
        Assert.AreEqual(
            TopicSendOutcomeKind.Rejected,
            (await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);

        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            outcome =>
            {
                Interlocked.Increment(ref callbacks);
                accepted.TrySetResult(outcome);
                return Task.CompletedTask;
            }));
        Assert.AreEqual(
            TopicSendOutcomeKind.Accepted,
            (await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
        Assert.AreEqual(2, Volatile.Read(ref callbacks));
    }

    [TestMethod]
    public async Task TopicSend_ComponentRecreationReattachesToStableInFlightIdentity()
    {
        var coordinator = new TopicSendCoordinator();
        var entered = NewSignal();
        var release = NewSignal();
        var completed = NewSignal();
        var recreatedNotified = NewSignal();
        var executions = 0;
        var disposedObserverCount = 0;
        var recreatedObserverCount = 0;
        var first = coordinator.CreateSnapshot(
            "thread",
            "device",
            7,
            "original-with-attachment",
            DateTimeOffset.UtcNow);

        Assert.IsTrue(coordinator.TrySubmit(
            first,
            async _ =>
            {
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                await release.Task;
                return new TopicSendHandoff(true, "accepted");
            },
            _ =>
            {
                completed.TrySetResult();
                return Task.CompletedTask;
            }));
        var firstComponent = new SendLifecycleHarness(coordinator);
        Assert.IsTrue(firstComponent.Attach(
            first,
            _ =>
            {
                Interlocked.Increment(ref disposedObserverCount);
                return Task.CompletedTask;
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        firstComponent.Dispose();

        var recreated = coordinator.CreateSnapshot(
            "thread",
            "device",
            7,
            "recreated-without-attachment",
            DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.AreEqual(first.OperationId, recreated.OperationId);
        Assert.AreEqual(first.RunId, recreated.RunId);
        Assert.AreEqual(first.LineId, recreated.LineId);
        Assert.IsTrue(coordinator.IsRunning("thread", "device", 7));
        using var recreatedComponent = new SendLifecycleHarness(coordinator);
        Assert.IsTrue(recreatedComponent.Attach(
            recreated,
            _ =>
            {
                Interlocked.Increment(ref recreatedObserverCount);
                recreatedNotified.TrySetResult();
                return Task.CompletedTask;
            }));
        Assert.IsFalse(coordinator.TrySubmit(
            recreated,
            _ => Task.FromResult(new TopicSendHandoff(true, "duplicate"))));

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await recreatedNotified.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref executions));
        Assert.AreEqual(0, Volatile.Read(ref disposedObserverCount));
        Assert.AreEqual(1, Volatile.Read(ref recreatedObserverCount));
    }

    [TestMethod]
    public async Task TopicSend_DisposalAfterAcceptanceStillReconcilesSubmittedRevision()
    {
        var coordinator = new TopicSendCoordinator();
        var revisions = new ComposerRevisionGuard();
        var submitted = revisions.Capture("thread", "send me");
        var snapshot = coordinator.CreateSnapshot(
            "thread",
            "device",
            submitted.Revision,
            "fingerprint",
            DateTimeOffset.UtcNow);
        var release = NewSignal();
        var reconciled = NewSignal();
        var observerCount = 0;

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
                {
                    Assert.IsTrue(revisions.TryClear(submitted, out _));
                    reconciled.TrySetResult();
                }
                return Task.CompletedTask;
            }));
        var component = new SendLifecycleHarness(coordinator);
        Assert.IsTrue(component.Attach(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref observerCount);
                return Task.CompletedTask;
            }));

        component.Dispose();
        release.TrySetResult();
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("", revisions.GetOrCreate("thread", "unexpected").Text);
        Assert.AreEqual(0, Volatile.Read(ref observerCount));
    }

    [TestMethod]
    public async Task TopicSend_AcceptedCompletionNeverClearsNewerDraft()
    {
        var coordinator = new TopicSendCoordinator();
        var revisions = new ComposerRevisionGuard();
        var submitted = revisions.Capture("thread", "send me");
        var snapshot = coordinator.CreateSnapshot(
            "thread",
            "device",
            submitted.Revision,
            "fingerprint",
            DateTimeOffset.UtcNow);
        var completed = NewSignal();
        var cleared = false;

        revisions.Track("thread", "newer text");
        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.Accepted)
                    cleared = revisions.TryClear(submitted, out _);
                completed.TrySetResult();
                return Task.CompletedTask;
            }));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(cleared);
        Assert.AreEqual("newer text", revisions.GetOrCreate("thread", "").Text);
    }

    [TestMethod]
    public void ComposerRevision_ClearAndRestoreNeverOverwriteNewerTyping()
    {
        var revisions = new ComposerRevisionGuard();
        var submitted = revisions.Capture("thread", "send me");

        Assert.IsTrue(revisions.TryClear(submitted, out var clearToken));
        Assert.IsNotNull(clearToken);
        revisions.Track("thread", "newer typing");
        Assert.IsFalse(revisions.TryRestore(clearToken!, out _));
        Assert.IsFalse(revisions.TryReplace(submitted, "", out _));

        var retry = revisions.Capture("thread", "newer typing");
        Assert.IsTrue(revisions.TryClear(retry, out var retryClear));
        Assert.IsTrue(revisions.TryRestore(retryClear!, out _));
        var restored = revisions.Capture("thread", "newer typing");
        Assert.AreEqual("newer typing", restored.Text);
    }

    [TestMethod]
    public void ComposerRevision_NonTextSnapshotChangeProtectsTheNewerDraft()
    {
        var revisions = new ComposerRevisionGuard();
        var submitted = revisions.Capture("thread", "same text");

        var changedSnapshotRevision = revisions.Track("thread", "same text");

        Assert.AreNotEqual(submitted.Revision, changedSnapshotRevision);
        Assert.IsFalse(revisions.TryClear(submitted, out _));
        Assert.AreEqual("same text", revisions.GetOrCreate("thread", "").Text);
    }

    [TestMethod]
    public async Task DeviceRefreshBurst_RunsCurrentAndOnlyLatestRequest()
    {
        using var coordinator = new LatestWinsRefreshCoordinator();
        var entered = NewSignal();
        var release = NewSignal();
        var executions = 0;

        async Task Refresh(CancellationToken cancellationToken)
        {
            var execution = Interlocked.Increment(ref executions);
            if (execution == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        }

        Assert.IsTrue(coordinator.Request(Refresh));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 100; index++)
            coordinator.Request(Refresh);

        release.TrySetResult();
        await coordinator.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, Volatile.Read(ref executions));
    }

    [TestMethod]
    public async Task UiTimeout_ActionThatFinishesDurablyReportsOnlySuccess()
    {
        var coordinator = new UiOperationCoordinator();
        var completed = new TaskCompletionSource<UiOperationOutcome<bool>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;

        coordinator.TryRun(
            "durable",
            "ui.durable",
            async _ =>
            {
                await Task.Delay(60);
                return true;
            },
            outcome =>
            {
                Interlocked.Increment(ref callbacks);
                completed.TrySetResult(outcome);
                return Task.CompletedTask;
            },
            timeout: TimeSpan.FromMilliseconds(10));

        var outcome = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(UiOperationOutcomeKind.Succeeded, outcome.Kind);
        Assert.AreEqual(1, Volatile.Read(ref callbacks));
    }

    [TestMethod]
    public void MobileLandscapeTopicList_ReservesBottomNavigationInset()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mesh.App",
            "Components",
            "Mobile",
            "MobileMe.razor.css"));

        StringAssert.Contains(css, "@media (orientation: landscape)");
        StringAssert.Contains(
            css,
            "padding-bottom: calc(68px + env(safe-area-inset-bottom, 0px));");
        StringAssert.Contains(
            css,
            "scroll-padding-bottom: calc(68px + env(safe-area-inset-bottom, 0px));");
    }

    private static TopicSendSnapshot CreateSubmission(TopicSendCoordinator coordinator)
        => coordinator.CreateSnapshot(
            "thread",
            "device",
            7,
            "fingerprint",
            DateTimeOffset.UtcNow);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ScopedNotFoundReconciliationQuery
        : ITopicSendReconciliationQuery,
          ITopicSendAuthorizationAuthority
    {
        private static readonly TopicSendAuthorizationScope Scope =
            new("account-a", "database-a", 1);
        private long observation;

        public ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new TopicSendReconciliationResult(
                TopicSendReconciliationKind.NotFound,
                AccountId: snapshot.AccountId,
                DatabaseIdentity: Scope.DatabaseIdentity,
                DatabaseGeneration: Scope.DatabaseGeneration,
                ObservationVersion: Interlocked.Increment(ref observation),
                ObservedAt: DateTimeOffset.UtcNow));
        }

        public bool TryConsume(
            TopicSendAuthorizationScope expected,
            Func<bool> consume)
            => IsCurrent(expected) && consume();

        public bool IsCurrent(TopicSendAuthorizationScope expected)
            => expected == Scope;
    }

    private sealed class SendLifecycleHarness : IDisposable
    {
        private readonly TopicSendCoordinator coordinator;
        private readonly string lifecycleId = Guid.NewGuid().ToString("n");
        private readonly List<ITopicSendObserverSubscription> subscriptions = new();

        public SendLifecycleHarness(TopicSendCoordinator coordinator)
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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "Mesh.App")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
