using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UiOperationCoordinatorTests
{
    [TestMethod]
    public async Task RunningCancellation_ReturnsImmediatelyAndCompletesOffDispatcher()
    {
        var coordinator = new UiOperationCoordinator();
        var entered = NewSignal();
        var release = NewSignal();
        var completed = new TaskCompletionSource<UiOperationOutcome<bool>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var started = coordinator.TryRun(
            "topic.stop:thread:run",
            "ui.topic.stop",
            async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return true;
            },
            outcome =>
            {
                completed.TrySetResult(outcome);
                return Task.CompletedTask;
            });

        stopwatch.Stop();
        Assert.IsTrue(started);
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 250);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(coordinator.IsRunning("topic.stop:thread:run"));

        release.TrySetResult();
        var outcome = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(UiOperationOutcomeKind.Succeeded, outcome.Kind);
        Assert.IsTrue(outcome.Result);
    }

    [TestMethod]
    public async Task RepeatedCancellationTaps_ExecuteAndReconcileExactlyOnce()
    {
        var coordinator = new UiOperationCoordinator();
        var release = NewSignal();
        var completed = NewSignal();
        var executions = 0;
        var callbacks = 0;

        Assert.IsTrue(coordinator.TryRun(
            "topic.stop:thread:run",
            "ui.topic.stop",
            async ct =>
            {
                Interlocked.Increment(ref executions);
                await release.Task.WaitAsync(ct);
                return true;
            },
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                completed.TrySetResult();
                return Task.CompletedTask;
            }));
        Assert.IsFalse(coordinator.TryRun(
            "topic.stop:thread:run",
            "ui.topic.stop",
            _ => Task.FromResult(true)));

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref executions));
        Assert.AreEqual(1, Volatile.Read(ref callbacks));
    }

    [TestMethod]
    public async Task RestoreCollision_JoinsCurrentAndRunsOnlyLatestQueuedRevision()
    {
        var coordinator = new UiOperationCoordinator();
        var release = NewSignal();
        var firstDone = NewSignal();
        var joinedDone = NewSignal();
        var supersededDone = NewSignal();
        var latestDone = NewSignal();
        var firstExecutions = 0;
        var supersededExecutions = 0;
        var latestExecutions = 0;

        Assert.AreEqual(
            UiLatestOperationScheduleKind.Started,
            coordinator.RunLatest(
                "topic.open:component:thread",
                "revision-1",
                "observer-1",
                "ui.topic.open",
                async _ =>
                {
                    Interlocked.Increment(ref firstExecutions);
                    await release.Task;
                    return "draft-1";
                },
                outcome =>
                {
                    Assert.AreEqual(UiOperationOutcomeKind.Succeeded, outcome.Kind);
                    firstDone.TrySetResult();
                    return Task.CompletedTask;
                }));
        Assert.AreEqual(
            UiLatestOperationScheduleKind.Joined,
            coordinator.RunLatest(
                "topic.open:component:thread",
                "revision-1",
                "observer-2",
                "ui.topic.open",
                _ => Task.FromResult("must-not-run"),
                outcome =>
                {
                    Assert.AreEqual("draft-1", outcome.Result);
                    joinedDone.TrySetResult();
                    return Task.CompletedTask;
                }));
        Assert.AreEqual(
            UiLatestOperationScheduleKind.QueuedLatest,
            coordinator.RunLatest(
                "topic.open:component:thread",
                "revision-2",
                "observer-2",
                "ui.topic.open",
                _ =>
                {
                    Interlocked.Increment(ref supersededExecutions);
                    return Task.FromResult("draft-2");
                },
                outcome =>
                {
                    Assert.AreEqual(UiOperationOutcomeKind.Cancelled, outcome.Kind);
                    supersededDone.TrySetResult();
                    return Task.CompletedTask;
                }));
        Assert.AreEqual(
            UiLatestOperationScheduleKind.QueuedLatest,
            coordinator.RunLatest(
                "topic.open:component:thread",
                "revision-3",
                "observer-3",
                "ui.topic.open",
                _ =>
                {
                    Interlocked.Increment(ref latestExecutions);
                    return Task.FromResult("draft-3");
                },
                outcome =>
                {
                    Assert.AreEqual("draft-3", outcome.Result);
                    latestDone.TrySetResult();
                    return Task.CompletedTask;
                }));

        await supersededDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await Task.WhenAll(firstDone.Task, joinedDone.Task, latestDone.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref firstExecutions));
        Assert.AreEqual(0, Volatile.Read(ref supersededExecutions));
        Assert.AreEqual(1, Volatile.Read(ref latestExecutions));
        Assert.IsFalse(coordinator.IsRunning("topic.open:component:thread"));
    }

    [TestMethod]
    public async Task AcceptedRunningRowReattach_AfterRestartIgnoresDisposedNavigationCallback()
    {
        var coordinator = new UiOperationCoordinator();
        using var lifetime = new CancellationTokenSource();
        var release = NewSignal();
        var callbackCount = 0;

        Assert.IsTrue(coordinator.TryRun(
            "topic.open:thread:1",
            "ui.topic.open",
            async ct =>
            {
                await release.Task.WaitAsync(ct);
                return "draft";
            },
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            },
            lifetime.Token));

        lifetime.Cancel();
        release.TrySetResult();
        await WaitUntilAsync(
            () => !coordinator.IsRunning("topic.open:thread:1"),
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, Volatile.Read(ref callbackCount));

        var current = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(coordinator.TryRun(
            "topic.open:thread:2",
            "ui.topic.open",
            _ => Task.FromResult("restored"),
            outcome =>
            {
                if (outcome.Kind == UiOperationOutcomeKind.Succeeded)
                    current.TrySetResult(outcome.Result!);
                return Task.CompletedTask;
            }));
        Assert.AreEqual("restored", await current.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task CallbackCancellation_ImmediatelyReleasesDelegateWhileWorkerContinues()
    {
        var coordinator = new UiOperationCoordinator();
        using var callbackLifetime = new CancellationTokenSource();
        var release = NewSignal();
        var attachment = StartCollectibleCallback(
            coordinator,
            callbackLifetime.Token,
            release.Task);

        await attachment.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        callbackLifetime.Cancel();
        ForceCollection();

        Assert.IsFalse(attachment.Target.IsAlive);
        Assert.IsTrue(coordinator.IsRunning("callback-release"));
        release.TrySetResult();
        await WaitUntilAsync(
            () => !coordinator.IsRunning("callback-release"),
            TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CancellationTimeout_IsBoundedAndCancelsWorker()
    {
        var coordinator = new UiOperationCoordinator();
        var timedOut = NewSignal();
        var stopwatch = Stopwatch.StartNew();

        coordinator.TryRun(
            "topic.stop:bounded",
            "ui.topic.stop",
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            outcome =>
            {
                if (outcome.Kind == UiOperationOutcomeKind.TimedOut)
                    timedOut.TrySetResult();
                return Task.CompletedTask;
            },
            timeout: TimeSpan.FromMilliseconds(40));

        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 1000);
        await WaitUntilAsync(
            () => !coordinator.IsRunning("topic.stop:bounded"),
            TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task HeldCoordinationGate_DoesNotBlockUiActionStart()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "mesh-ui-gate-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.meshdb");
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            using var db = MeshDb.Open(path, key);
            using var release = new ManualResetEventSlim();
            using var held = new ManualResetEventSlim();
            var holder = db.ExecuteDurableWriteAsync(() =>
            {
                held.Set();
                release.Wait();
            });
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(2)));

            var coordinator = new UiOperationCoordinator();
            var completed = new TaskCompletionSource<UiOperationOutcome<bool>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = Stopwatch.StartNew();
            var started = coordinator.TryRun(
                "topic.open:held-gate",
                "ui.topic.open",
                ct => db.ExecuteDurableWriteAsync(() => true, ct),
                outcome =>
                {
                    completed.TrySetResult(outcome);
                    return Task.CompletedTask;
                });
            stopwatch.Stop();

            Assert.IsTrue(started);
            Assert.IsTrue(stopwatch.ElapsedMilliseconds < 250);
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(
                UiOperationOutcomeKind.Succeeded,
                (await completed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DesktopSelection_HeldWriterGateReturnsImmediatelyAndPersistsLatestValue()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "mesh-ui-selection-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.meshdb");
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            using (var db = MeshDb.Open(path, key))
            {
                await using var selections = new DesktopSelectionPersistenceCoordinator();
                using var release = new ManualResetEventSlim();
                using var held = new ManualResetEventSlim();
                var holder = db.ExecuteDurableWriteAsync(() =>
                {
                    held.Set();
                    release.Wait();
                });
                Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(2)));

                var stopwatch = Stopwatch.StartNew();
                selections.SetTopic(db, "topic-old");
                selections.SetTopic(db, "topic-latest");
                selections.SetConversation(db, "conversation-latest");
                stopwatch.Stop();

                Assert.IsTrue(
                    stopwatch.ElapsedMilliseconds < 250,
                    $"Selection staging blocked for {stopwatch.ElapsedMilliseconds} ms.");
                Assert.AreEqual("topic-latest", db.GetLastDesktopTopicId());
                Assert.AreEqual("conversation-latest", db.GetLastDesktopConversationKey());

                release.Set();
                await holder.WaitAsync(TimeSpan.FromSeconds(2));
                await selections.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            SqliteConnection.ClearAllPools();

            using var reopened = MeshDb.Open(path, key);
            Assert.AreEqual("topic-latest", reopened.GetLastDesktopTopicId());
            Assert.AreEqual("conversation-latest", reopened.GetLastDesktopConversationKey());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WorkerFailure_IsObservedAndReported()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "mesh-ui-failure-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var diagnostics = new RuntimeDiagnostics(directory);
            diagnostics.StartSession("test");
            diagnostics.InstallManagedHandlers();
            var coordinator = new UiOperationCoordinator();
            var completed = new TaskCompletionSource<UiOperationOutcome<bool>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            coordinator.TryRun<bool>(
                "topic.stop:failure",
                "ui.topic.stop",
                _ => Task.FromException<bool>(new InvalidOperationException("synthetic failure")),
                outcome =>
                {
                    completed.TrySetResult(outcome);
                    return Task.CompletedTask;
                });

            Assert.AreEqual(
                UiOperationOutcomeKind.Failed,
                (await completed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
            StringAssert.Contains(diagnostics.CreateReport(), "ui-operation-failed");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ManagedStallDiagnostics_IdentifyOperationWaitAndOwner()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "mesh-managed-stall-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var diagnostics = new RuntimeDiagnostics(directory);
            diagnostics.StartSession("test");
            diagnostics.InstallManagedHandlers();

            using (ManagedOperationDiagnostics.Begin(
                       "ui.topic.stop",
                       TimeSpan.FromMilliseconds(20)))
            using (ManagedOperationDiagnostics.Wait(
                       "meshdb.durable-write-gate",
                       () => "topic.persist#7",
                       TimeSpan.FromMilliseconds(20)))
            {
                await Task.Delay(100);
            }

            var report = diagnostics.CreateReport();
            StringAssert.Contains(report, "managed-stall");
            StringAssert.Contains(report, "managed-wait-stall");
            StringAssert.Contains(report, "operation=ui.topic.stop");
            StringAssert.Contains(report, "resource=meshdb.durable-write-gate");
            StringAssert.Contains(report, "owner=topic.persist_7");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void CriticalUiPaths_ContainNoSynchronousTaskWaits()
    {
        var root = FindRepositoryRoot();
        var paths = new[]
        {
            Path.Combine(root, "src", "Mesh.App", "Components", "Mobile", "MobileMe.razor"),
            Path.Combine(root, "src", "Mesh.App", "Components", "Pages", "Home.razor"),
            Path.Combine(root, "src", "Mesh.App", "Services", "TopicExecutionRouter.cs"),
            Path.Combine(root, "src", "Mesh.App", "Services", "MeshClient.TopicExecution.cs"),
            Path.Combine(root, "src", "Mesh.App", "Services", "DesktopSelectionPersistenceCoordinator.cs")
        };
        var blockingWait = new Regex(
            @"\.Wait\s*\(|GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(|Task\.Wait(All|Any)\s*\(",
            RegexOptions.CultureInvariant);

        foreach (var path in paths)
        {
            var source = File.ReadAllText(path);
            Assert.IsFalse(
                blockingWait.IsMatch(source),
                $"Synchronous task wait found in {Path.GetFileName(path)}.");
        }

        var mobile = File.ReadAllText(paths[0]);
        Assert.IsFalse(mobile.Contains(
            "inputValue = value is null ? \"\" : State.GetTopicDraft(value)",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void QueuePresentationState_IsSafeUnderConcurrentStatusRendering()
    {
        var state = new QueuedTopicRunState();
        Parallel.For(0, 2_000, index =>
        {
            var runId = $"run-{index % 16}";
            var lineId = $"line-{index % 16}";
            state.MarkWaiting("thread", runId, lineId);
            _ = state.FindByLine("thread", lineId);
            _ = state.WaitingCount("thread");
            state.SetStage("thread", runId, TopicQueueStage.Cancelling);
            state.Complete("thread", runId);
        });
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CallbackAttachment StartCollectibleCallback(
        UiOperationCoordinator coordinator,
        CancellationToken cancellationToken,
        Task release)
    {
        var target = new CallbackTarget();
        var weak = new WeakReference(target);
        var entered = NewSignal();
        Assert.IsTrue(coordinator.TryRun(
            "callback-release",
            "ui.callback-release",
            async _ =>
            {
                entered.TrySetResult();
                await release;
                return true;
            },
            target.NotifyAsync,
            cancellationToken));
        return new(weak, entered.Task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class CallbackTarget
    {
        public Task NotifyAsync(UiOperationOutcome<bool> outcome)
            => Task.CompletedTask;
    }

    private sealed record CallbackAttachment(WeakReference Target, Task Entered);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stop = Stopwatch.StartNew();
        while (!predicate() && stop.Elapsed < timeout)
            await Task.Delay(10);
        Assert.IsTrue(predicate());
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
