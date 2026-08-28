using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AppShutdownCoordinatorTests
{
    [TestMethod]
    public async Task GlobalCancellationIsSignalledBeforeShutdownWaits()
    {
        var state = new AppShutdownState();
        var coordinator = new AppShutdownCoordinator(state);
        var observedCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Register("worker", async cancellationToken =>
        {
            observedCancellation.TrySetResult(state.Token.IsCancellationRequested);
            await release.Task;
        });

        var shutdown = coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(shutdown.IsCompleted);
        release.TrySetResult();
        await shutdown;
    }

    [TestMethod]
    public async Task ShutdownWaitsForTrackedBackgroundWork()
    {
        var state = new AppShutdownState();
        var coordinator = new AppShutdownCoordinator(state);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Track(release.Task, "test work");

        var shutdown = coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(25);
        Assert.IsFalse(shutdown.IsCompleted);
        release.TrySetResult();
        await shutdown;
    }

    [TestMethod]
    public async Task FinalDrainRunsAfterTrackedWorkCompletes()
    {
        var state = new AppShutdownState();
        var coordinator = new AppShutdownCoordinator(state);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = false;
        coordinator.Track(release.Task, "test work");
        coordinator.RegisterDrain("renderer", _ =>
        {
            drained = true;
            return Task.CompletedTask;
        });

        var shutdown = coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(25);
        Assert.IsFalse(drained);

        release.TrySetResult();
        await shutdown;
        Assert.IsTrue(drained);
    }

    [TestMethod]
    public async Task ConcurrentShutdownRequestsRunRegistrationsOnce()
    {
        var state = new AppShutdownState();
        var coordinator = new AppShutdownCoordinator(state);
        var calls = 0;
        coordinator.Register("worker", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        var first = coordinator.ShutdownAsync();
        var second = coordinator.ShutdownAsync();
        await Task.WhenAll(first, second);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(state.IsStopping);
    }
}
