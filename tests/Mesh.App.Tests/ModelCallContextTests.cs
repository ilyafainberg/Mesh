using Mesh.App.Services;
using Mesh.App.Domain;
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

    [TestMethod]
    public async Task PostedStreamCallbackCannotRecreateDraftAfterTerminalCompletion()
    {
        var context = new QueuedSynchronizationContext();
        var render = new LiveAgentRenderState();
        render.BeginDraft("thread", "run");
        var accepted = 0;
        var progress = ModelCallDispatcher.MarshalProgress(
            new InlineProgress<AgentDelta>(delta =>
            {
                if (render.AppendDraft("thread", "run", delta))
                    accepted++;
            }),
            context)!;

        await Task.Run(() =>
            progress.Report(new AgentDelta(AgentDeltaKind.Reasoning, "late thinking")));
        Assert.AreEqual(1, context.PostCount);

        render.EndDraft("thread", "run");
        render.CompleteRun("thread", "run");
        context.Drain();

        Assert.AreEqual(0, accepted);
        Assert.IsNull(render.Capture("thread").Draft);
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

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        public int PostCount => callbacks.Count;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (callbacks)
                callbacks.Enqueue((callback, state));
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (callbacks)
                {
                    if (callbacks.Count == 0) return;
                    item = callbacks.Dequeue();
                }
                item.Callback(item.State);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
