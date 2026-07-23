using System.Collections.Concurrent;
using System.Text;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class CopilotAcpPipeWriterTests
{
    [TestMethod]
    public async Task WriteLineAsync_DoesNotBlockCallerAndSerializesWrites()
    {
        using var output = new ControlledTextWriter();
        using var writer = new CopilotAcpPipeWriter(output);

        var first = writer.WriteLineAsync("first", CancellationToken.None);
        await output.WaitForWriteAsync();
        Assert.IsFalse(first.IsCompleted);

        var second = writer.WriteLineAsync("second", CancellationToken.None);
        await Task.Delay(50);
        CollectionAssert.AreEqual(new[] { "first" }, output.Started.ToArray());

        output.CompleteNext();
        await first.WaitAsync(TimeSpan.FromSeconds(1));
        await output.WaitForWriteAsync();
        CollectionAssert.AreEqual(new[] { "first", "second" }, output.Started.ToArray());

        output.CompleteNext();
        await second.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class ControlledTextWriter : TextWriter
    {
        private readonly ConcurrentQueue<TaskCompletionSource> pending = new();
        private readonly SemaphoreSlim started = new(0);

        public override Encoding Encoding => Encoding.UTF8;
        public ConcurrentQueue<string> Started { get; } = new();

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            Started.Enqueue(buffer.ToString());
            pending.Enqueue(completion);
            started.Release();
            return completion.Task;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WaitForWriteAsync()
            => started.WaitAsync(TimeSpan.FromSeconds(1));

        public void CompleteNext()
        {
            Assert.IsTrue(pending.TryDequeue(out var completion));
            completion.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) started.Dispose();
            base.Dispose(disposing);
        }
    }
}
