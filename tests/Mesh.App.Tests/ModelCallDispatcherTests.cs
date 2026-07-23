using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ModelCallDispatcherTests
{
    [TestMethod]
    public async Task RunAsync_OffloadsSynchronousProviderSetup()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource<(int CallerThread, Task<string> Call)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var uiThread = new Thread(() =>
        {
            var call = ModelCallDispatcher.RunAsync(async () =>
            {
                started.TrySetResult(Environment.CurrentManagedThreadId);
                release.Wait();
                await Task.Yield();
                return "ok";
            }, CancellationToken.None);
            invoked.TrySetResult((Environment.CurrentManagedThreadId, call));
        });
        uiThread.Start();

        var (callerThread, call) = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var providerThread = await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        uiThread.Join();
        Assert.IsFalse(call.IsCompleted);
        Assert.AreNotEqual(callerThread, providerThread);

        release.Set();
        Assert.AreEqual("ok", await call.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
