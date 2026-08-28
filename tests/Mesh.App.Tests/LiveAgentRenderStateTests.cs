using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class LiveAgentRenderStateTests
{
    [TestMethod]
    public void StepSnapshotsRemainStableAfterUpdates()
    {
        var state = new LiveAgentRenderState();
        var first = Step("one");

        state.BeginSteps("thread");
        state.ReportStep("thread", first);
        var snapshot = state.StepsFor("thread");

        state.ReportStep("thread", Step("two"));

        CollectionAssert.AreEqual(new[] { first }, snapshot.ToArray());
        Assert.AreEqual(2, state.StepsFor("thread").Count);
    }

    [TestMethod]
    public void DraftSnapshotsRemainStableAfterUpdates()
    {
        var state = new LiveAgentRenderState();
        state.BeginDraft("thread");
        state.AppendDraft("thread", new AgentDelta(AgentDeltaKind.Reasoning, "thinking"));
        state.AppendDraft("thread", new AgentDelta(AgentDeltaKind.Answer, "hello"));

        var snapshot = state.DraftFor("thread");
        state.AppendDraft("thread", new AgentDelta(AgentDeltaKind.Answer, " world"));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual("thinking", snapshot.Value.Reasoning);
        Assert.AreEqual("hello", snapshot.Value.Answer);
        Assert.AreEqual("hello world", state.DraftFor("thread")?.Answer);
    }

    [TestMethod]
    public async Task ConcurrentReadsAndWritesRemainSafe()
    {
        const int updateCount = 1_000;
        var state = new LiveAgentRenderState();
        state.BeginSteps("thread");
        state.BeginDraft("thread");
        using var start = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < updateCount; i++)
            {
                state.ReportStep("thread", Step(i.ToString()));
                state.AppendDraft("thread", new AgentDelta(AgentDeltaKind.Answer, "x"));
            }
        });
        var reader = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < updateCount; i++)
            {
                var snapshot = state.Capture("thread");
                foreach (var step in snapshot.Steps)
                    _ = step.Label;
                _ = snapshot.Draft?.Answer.Length;
            }
        });

        start.Set();
        await Task.WhenAll(writer, reader);

        Assert.AreEqual(updateCount, state.StepsFor("thread").Count);
        Assert.AreEqual(updateCount, state.DraftFor("thread")?.Answer.Length);
    }

    [TestMethod]
    public void CombinedSnapshotRemainsStableAfterBothStreamsChange()
    {
        var state = new LiveAgentRenderState();
        state.BeginSteps("thread");
        state.BeginDraft("thread");
        state.ReportStep("thread", Step("one"));
        state.AppendDraft("thread", new AgentDelta(AgentDeltaKind.Answer, "first"));

        var snapshot = state.Capture("thread");

        state.ReportStep("thread", Step("two"));
        state.AppendDraft("thread", new AgentDelta(AgentDeltaKind.Answer, " second"));

        Assert.AreEqual(1, snapshot.Steps.Count);
        Assert.AreEqual("one", snapshot.Steps[0].Label);
        Assert.AreEqual("first", snapshot.Draft?.Answer);
    }

    private static AgentStep Step(string id)
        => new(id, id, AgentStepState.Started, null, null, null);
}
