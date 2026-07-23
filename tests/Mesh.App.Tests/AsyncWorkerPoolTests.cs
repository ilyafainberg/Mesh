using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AsyncWorkerPoolTests
{
    [TestMethod]
    public async Task UseAsync_AllowsBoundedConcurrencyAndReusesWorkers()
    {
        var pool = new AsyncWorkerPool<int>([1, 2, 3]);
        using var release = new SemaphoreSlim(0, 3);
        var started = new SemaphoreSlim(0, 4);

        async Task<int> RunAsync()
            => await pool.UseAsync(async worker =>
            {
                started.Release();
                await release.WaitAsync();
                return worker;
            }, CancellationToken.None);

        var first = RunAsync();
        var second = RunAsync();
        var third = RunAsync();
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)));

        var fourth = RunAsync();
        Assert.IsFalse(await started.WaitAsync(TimeSpan.FromMilliseconds(100)));

        release.Release();
        await (await Task.WhenAny(first, second, third).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)));

        release.Release(3);
        var workers = new[]
        {
            await first.WaitAsync(TimeSpan.FromSeconds(1)),
            await second.WaitAsync(TimeSpan.FromSeconds(1)),
            await third.WaitAsync(TimeSpan.FromSeconds(1)),
            await fourth.WaitAsync(TimeSpan.FromSeconds(1))
        };

        CollectionAssert.Contains(workers, 1);
        CollectionAssert.Contains(workers, 2);
        CollectionAssert.Contains(workers, 3);
    }
}
