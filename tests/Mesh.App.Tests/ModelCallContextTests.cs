using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ModelCallContextTests
{
    [TestMethod]
    public async Task MarshalProgress_PostsToCapturedContext()
    {
        var context = new RecordingSynchronizationContext();
        var reported = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = ModelCallDispatcher.MarshalProgress(
            new InlineProgress<int>(value => reported.TrySetResult(value)),
            context)!;

        await Task.Run(() => progress.Report(42));

        Assert.AreEqual(1, context.PostCount);
        Assert.AreEqual(42, await reported.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCount++;
            callback(state);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
