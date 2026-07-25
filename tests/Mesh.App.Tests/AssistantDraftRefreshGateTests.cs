using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AssistantDraftRefreshGateTests
{
    [TestMethod]
    public void SameChannelIsLimitedToConfiguredInterval()
    {
        var gate = new AssistantDraftRefreshGate(50);

        Assert.IsTrue(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_000));
        Assert.IsFalse(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_049));
        Assert.IsTrue(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_050));
    }

    [TestMethod]
    public void ChannelChangesPublishImmediately()
    {
        var gate = new AssistantDraftRefreshGate(50);

        Assert.IsTrue(gate.ShouldPublish("thread", AgentDeltaKind.Reasoning, 1_000));
        Assert.IsTrue(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_001));
        Assert.IsFalse(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_002));
    }

    [TestMethod]
    public void ResetMakesTheNextDeltaImmediate()
    {
        var gate = new AssistantDraftRefreshGate(50);

        Assert.IsTrue(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_000));
        gate.Reset("thread");

        Assert.IsTrue(gate.ShouldPublish("thread", AgentDeltaKind.Answer, 1_001));
    }

    [TestMethod]
    public void ThreadsAreLimitedIndependently()
    {
        var gate = new AssistantDraftRefreshGate(50);

        Assert.IsTrue(gate.ShouldPublish("one", AgentDeltaKind.Answer, 1_000));
        Assert.IsTrue(gate.ShouldPublish("two", AgentDeltaKind.Answer, 1_001));
        Assert.IsFalse(gate.ShouldPublish("one", AgentDeltaKind.Answer, 1_002));
    }
}
