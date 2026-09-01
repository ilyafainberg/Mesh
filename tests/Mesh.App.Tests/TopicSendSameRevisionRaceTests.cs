using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class TopicSendSameRevisionRaceTests
{
    [TestMethod]
    public async Task RejectedCallbackCanRetrySameRevisionBeforeReturning()
    {
        await RunImmediateRetryAsync(1);
    }

    [TestMethod]
    public async Task RejectedCallbackCanRetrySameRevisionTenThousandTimes()
    {
        await RunImmediateRetryAsync(10_000);
    }

    private static async Task RunImmediateRetryAsync(int iterations)
    {
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await using var coordinator = new TopicSendCoordinator();
            var snapshot = coordinator.CreateSnapshot(
                $"thread-{iteration}",
                "device",
                1,
                $"fingerprint-{iteration}",
                DateTimeOffset.UtcNow);
            var accepted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var retryDecision = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.IsTrue(coordinator.TrySubmit(
                snapshot,
                _ => Task.FromResult(new TopicSendHandoff(false, "not_ready")),
                _ =>
                {
                    var retryStarted = coordinator.TrySubmit(
                        snapshot,
                        _ => Task.FromResult(new TopicSendHandoff(true, "accepted")),
                        _ =>
                        {
                            accepted.TrySetResult();
                            return Task.CompletedTask;
                        });
                    retryDecision.TrySetResult(retryStarted);
                    return Task.CompletedTask;
                }));

            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var retryStarted = await retryDecision.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(
                retryStarted,
                $"Iteration {iteration}: callback observed the terminal rejection before identity retirement.");
        }
    }
}
