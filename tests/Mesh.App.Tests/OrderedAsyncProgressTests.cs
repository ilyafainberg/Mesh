using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class OrderedAsyncProgressTests
{
    [TestMethod]
    public async Task CompleteAsync_DrainsEveryReportInOrderBeforeReturning()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<int>();
        var progress = new OrderedAsyncProgress<int>(async value =>
        {
            if (value == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            received.Add(value);
        });

        progress.Report(1);
        progress.Report(2);
        progress.Report(3);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var completion = progress.CompleteAsync();
        Assert.IsFalse(completion.IsCompleted);

        releaseFirst.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, received);
    }
}
