using System.Collections.Concurrent;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ReplicationActivityTrackerTests
{
    [TestMethod]
    public async Task SnapshotActivity_StaysVisibleUntilTheQuietPeriodCompletes()
    {
        var delay = new ManualDelay();
        var tracker = new ReplicationActivityTracker(TimeSpan.FromSeconds(1), delay.WaitAsync);
        var transitions = new ConcurrentQueue<bool>();
        var becameInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Changed += () =>
        {
            var active = tracker.IsActive;
            transitions.Enqueue(active);
            if (!active) becameInactive.TrySetResult(true);
        };

        tracker.ObserveActivity();

        Assert.IsTrue(tracker.IsActive);
        Assert.AreEqual(1, delay.Count);
        delay.Complete(0);
        await becameInactive.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { true, false }, transitions.ToArray());
    }

    [TestMethod]
    public async Task LaterSnapshotActivity_RestartsTheQuietPeriodWithoutAnotherActiveTransition()
    {
        var delay = new ManualDelay();
        var tracker = new ReplicationActivityTracker(TimeSpan.FromSeconds(1), delay.WaitAsync);
        var transitions = new ConcurrentQueue<bool>();
        var becameInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Changed += () =>
        {
            var active = tracker.IsActive;
            transitions.Enqueue(active);
            if (!active) becameInactive.TrySetResult(true);
        };

        tracker.ObserveActivity();
        tracker.ObserveActivity();

        Assert.IsTrue(tracker.IsActive);
        Assert.AreEqual(2, delay.Count);
        delay.Complete(0);
        Assert.IsTrue(tracker.IsActive);
        delay.Complete(1);
        await becameInactive.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { true, false }, transitions.ToArray());
    }

    private sealed class ManualDelay
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource<bool>> pending = new();

        public int Count
        {
            get
            {
                lock (gate) return pending.Count;
            }
        }

        public Task WaitAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            lock (gate) pending.Add(completion);
            return completion.Task;
        }

        public void Complete(int index)
        {
            TaskCompletionSource<bool> completion;
            lock (gate) completion = pending[index];
            completion.TrySetResult(true);
        }
    }
}
