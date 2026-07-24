using Mesh.App.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AgentDeltaCoalescerTests
{
    [TestMethod]
    public void BuffersUntilCharBudgetThenFlushesAndDrainsTail()
    {
        var coalescer = new AgentDeltaCoalescer(flushChars: 5, flushMillis: 10_000);

        Assert.IsNull(coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "ab"), 0));
        Assert.IsNull(coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "cd"), 1));
        var flushed = coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "ef"), 2);

        Assert.IsNotNull(flushed);
        Assert.AreEqual(AgentDeltaKind.Answer, flushed!.Kind);
        Assert.AreEqual("abcdef", flushed.Text);

        Assert.IsNull(coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "g"), 3));
        var tail = coalescer.Flush();
        Assert.IsNotNull(tail);
        Assert.AreEqual("g", tail!.Text);
        Assert.IsNull(coalescer.Flush());
    }

    [TestMethod]
    public void FlushesBufferedStreamBeforeSwitchingKind()
    {
        var coalescer = new AgentDeltaCoalescer(flushChars: 100, flushMillis: 10_000);

        Assert.IsNull(coalescer.Accept(new AgentDelta(AgentDeltaKind.Reasoning, "think"), 0));
        var flushed = coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "hi"), 1);

        Assert.IsNotNull(flushed);
        Assert.AreEqual(AgentDeltaKind.Reasoning, flushed!.Kind);
        Assert.AreEqual("think", flushed.Text);

        var tail = coalescer.Flush();
        Assert.IsNotNull(tail);
        Assert.AreEqual(AgentDeltaKind.Answer, tail!.Kind);
        Assert.AreEqual("hi", tail.Text);
    }

    [TestMethod]
    public void FlushesOnTimeBudgetForSlowStreams()
    {
        var coalescer = new AgentDeltaCoalescer(flushChars: 1000, flushMillis: 100);

        Assert.IsNull(coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "a"), 0));
        var flushed = coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, "b"), 150);

        Assert.IsNotNull(flushed);
        Assert.AreEqual("ab", flushed!.Text);
    }

    [TestMethod]
    public void IgnoresEmptyFragments()
    {
        var coalescer = new AgentDeltaCoalescer(flushChars: 5, flushMillis: 10_000);

        Assert.IsNull(coalescer.Accept(new AgentDelta(AgentDeltaKind.Answer, ""), 0));
        Assert.IsNull(coalescer.Flush());
    }
}
