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

        state.BeginSteps("thread", "run");
        state.ReportStep("thread", "run", first);
        var snapshot = state.StepsFor("thread");

        state.ReportStep("thread", "run", Step("two"));

        CollectionAssert.AreEqual(new[] { first }, snapshot.ToArray());
        Assert.AreEqual(2, state.StepsFor("thread").Count);
    }

    [TestMethod]
    public void DraftSnapshotsRemainStableAfterUpdates()
    {
        var state = new LiveAgentRenderState();
        state.BeginDraft("thread", "run");
        state.AppendDraft("thread", "run", new AgentDelta(AgentDeltaKind.Reasoning, "thinking"));
        state.AppendDraft("thread", "run", new AgentDelta(AgentDeltaKind.Answer, "hello"));

        var snapshot = state.DraftFor("thread");
        state.AppendDraft("thread", "run", new AgentDelta(AgentDeltaKind.Answer, " world"));

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
        state.BeginSteps("thread", "run");
        state.BeginDraft("thread", "run");
        using var start = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < updateCount; i++)
            {
                state.ReportStep("thread", "run", Step(i.ToString()));
                state.AppendDraft("thread", "run", new AgentDelta(AgentDeltaKind.Answer, "x"));
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
        state.BeginSteps("thread", "run");
        state.BeginDraft("thread", "run");
        state.ReportStep("thread", "run", Step("one"));
        state.AppendDraft("thread", "run", new AgentDelta(AgentDeltaKind.Answer, "first"));

        var snapshot = state.Capture("thread");

        state.ReportStep("thread", "run", Step("two"));
        state.AppendDraft("thread", "run", new AgentDelta(AgentDeltaKind.Answer, " second"));

        Assert.AreEqual(1, snapshot.Steps.Count);
        Assert.AreEqual("one", snapshot.Steps[0].Label);
        Assert.AreEqual("first", snapshot.Draft?.Answer);
    }

    [TestMethod]
    public void TerminalRunRejectsDelayedCallbacksAndDuplicateTerminal()
    {
        var state = new LiveAgentRenderState();
        state.BeginSteps("thread", "run-1");
        state.BeginDraft("thread", "run-1");
        Assert.IsTrue(state.ReportStep("thread", "run-1", Step("running")));
        Assert.IsTrue(state.AppendDraft(
            "thread", "run-1", new AgentDelta(AgentDeltaKind.Answer, "answer")));

        Assert.IsTrue(state.CompleteRun("thread", "run-1"));
        Assert.IsFalse(state.CompleteRun("thread", "run-1"));
        Assert.IsFalse(state.ReportStep("thread", "run-1", Step("late-thinking")));
        Assert.IsFalse(state.AppendDraft(
            "thread", "run-1", new AgentDelta(AgentDeltaKind.Reasoning, "late")));

        var snapshot = state.Capture("thread");
        Assert.IsEmpty(snapshot.Steps);
        Assert.IsNull(snapshot.Draft);
    }

    [TestMethod]
    public void TerminalBeforeRenderSuppressesDelayedThinkingButAllowsNextRun()
    {
        var state = new LiveAgentRenderState();

        Assert.IsTrue(state.CompleteRun("thread", "run-1"));
        Assert.IsFalse(state.BeginSteps("thread", "run-1"));
        Assert.IsFalse(state.BeginDraft("thread", "run-1"));
        Assert.IsFalse(state.ReportStep("thread", "run-1", Step("late-thinking")));
        Assert.IsFalse(state.AppendDraft(
            "thread", "run-1", new AgentDelta(AgentDeltaKind.Reasoning, "late")));

        Assert.IsTrue(state.BeginSteps("thread", "run-2"));
        Assert.IsTrue(state.BeginDraft("thread", "run-2"));
        Assert.IsTrue(state.ReportStep("thread", "run-2", Step("next-thinking")));
        Assert.IsTrue(state.AppendDraft(
            "thread", "run-2", new AgentDelta(AgentDeltaKind.Reasoning, "valid")));
    }

    [TestMethod]
    public void NewRunSurvivesRecreatedRendererAndRejectsPriorRunCallbacks()
    {
        var state = new LiveAgentRenderState();
        state.BeginSteps("thread", "run-1");
        state.BeginDraft("thread", "run-1");
        state.CompleteRun("thread", "run-1");

        Assert.IsTrue(state.BeginSteps("thread", "run-2"));
        Assert.IsTrue(state.BeginDraft("thread", "run-2"));
        Assert.IsTrue(state.ReportStep("thread", "run-2", Step("valid-thinking")));
        Assert.IsTrue(state.AppendDraft(
            "thread", "run-2", new AgentDelta(AgentDeltaKind.Answer, "next")));
        Assert.IsFalse(state.ReportStep("thread", "run-1", Step("old-late-thinking")));
        Assert.IsFalse(state.AppendDraft(
            "thread", "run-1", new AgentDelta(AgentDeltaKind.Reasoning, "late")));
        Assert.IsFalse(state.BeginSteps("thread", "run-1"));
        Assert.IsFalse(state.BeginDraft("thread", "run-1"));

        var recreatedRendererSnapshot = state.Capture("thread");
        Assert.HasCount(1, recreatedRendererSnapshot.Steps);
        Assert.AreEqual("valid-thinking", recreatedRendererSnapshot.Steps[0].Label);
        Assert.AreEqual("next", recreatedRendererSnapshot.Draft?.Answer);
    }

    [TestMethod]
    public void ClosedStreamsRejectSameDeviceCallbacksQueuedBeforeTerminal()
    {
        var state = new LiveAgentRenderState();
        state.BeginSteps("thread", "run");
        state.BeginDraft("thread", "run");

        state.EndSteps("thread", "run");
        state.EndDraft("thread", "run");

        Assert.IsFalse(state.ReportStep("thread", "run", Step("late-progress")));
        Assert.IsFalse(state.AppendDraft(
            "thread", "run", new AgentDelta(AgentDeltaKind.Answer, "late")));
        Assert.IsEmpty(state.Capture("thread").Steps);
        Assert.IsNull(state.Capture("thread").Draft);
    }

    [TestMethod]
    public void AgentRunPhasesMoveForwardAndTerminalPhasesDominate()
    {
        var transient = new[]
        {
            AgentRunPhase.Planning,
            AgentRunPhase.Executing,
            AgentRunPhase.Hyperscaling,
            AgentRunPhase.Integrating,
            AgentRunPhase.Verifying
        };
        var terminal = new[]
        {
            AgentRunPhase.Completed,
            AgentRunPhase.Failed,
            AgentRunPhase.Cancelled
        };

        foreach (var current in terminal)
        foreach (var next in transient.Concat(terminal))
            Assert.IsFalse(
                AgentRunLifecycle.CanTransition(current, next),
                $"{current} must dominate late {next}");
        for (var currentIndex = 0; currentIndex < transient.Length; currentIndex++)
        for (var nextIndex = 0; nextIndex < transient.Length; nextIndex++)
            Assert.AreEqual(
                nextIndex >= currentIndex,
                AgentRunLifecycle.CanTransition(
                    transient[currentIndex], transient[nextIndex]),
                $"{transient[currentIndex]} -> {transient[nextIndex]}");
        foreach (var current in transient)
        foreach (var next in terminal)
            Assert.IsTrue(
                AgentRunLifecycle.CanTransition(current, next),
                $"{current} should allow terminal {next}");
    }

    [TestMethod]
    public void SameProcessAccountSwitchAllowsCollidingTopicAndRunIds()
    {
        var state = new LiveAgentRenderState();
        state.BeginDraft("shared-topic", "shared-run");
        state.AppendDraft(
            "shared-topic",
            "shared-run",
            new AgentDelta(AgentDeltaKind.Answer, "account-a"));
        state.CompleteRun("shared-topic", "shared-run");

        state.ResetForAccount();
        Assert.IsTrue(state.BeginDraft("shared-topic", "shared-run"));
        Assert.IsTrue(state.AppendDraft(
            "shared-topic",
            "shared-run",
            new AgentDelta(AgentDeltaKind.Reasoning, "account-b")));
        Assert.AreEqual("account-b", state.DraftFor("shared-topic")?.Reasoning);
    }

    [TestMethod]
    public async Task AccountSwitchRejectsQueuedCallbacksFromPriorDatabaseGeneration()
    {
        var scopes = new AgentRuntimeScopeTracker();
        var state = new LiveAgentRenderState();
        scopes.Activate("account-a-database");
        var accountA = scopes.CaptureCurrent();
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> staleCallback;
        using (scopes.Enter(accountA))
        {
            state.BeginDraft("shared-topic", "shared-run");
            staleCallback = Task.Run(async () =>
            {
                await releaseCallback.Task;
                return scopes.IsCurrentContext
                       && state.AppendDraft(
                           "shared-topic",
                           "shared-run",
                           new AgentDelta(AgentDeltaKind.Answer, "stale-a"));
            });
        }

        scopes.Deactivate();
        state.ResetForAccount();
        scopes.Activate("account-b-database");
        var accountB = scopes.CaptureCurrent();
        using (scopes.Enter(accountB))
        {
            Assert.IsTrue(state.BeginDraft("shared-topic", "shared-run"));
            Assert.IsTrue(state.AppendDraft(
                "shared-topic",
                "shared-run",
                new AgentDelta(AgentDeltaKind.Answer, "account-b")));
        }
        releaseCallback.SetResult();

        Assert.IsFalse(await staleCallback);
        Assert.AreEqual("account-b", state.DraftFor("shared-topic")?.Answer);

        scopes.Deactivate();
        state.ResetForAccount();
        scopes.Activate("account-a-database");
        var returnedAccountA = scopes.CaptureCurrent();
        Assert.AreNotEqual(accountA, returnedAccountA);
        using (scopes.Enter(returnedAccountA))
            Assert.IsTrue(state.BeginDraft("shared-topic", "shared-run"));
    }

    [TestMethod]
    public void DisposeAndRecreateDoesNotRetainTerminalRunTombstones()
    {
        var beforeDispose = new LiveAgentRenderState();
        beforeDispose.BeginDraft("shared-topic", "shared-run");
        beforeDispose.CompleteRun("shared-topic", "shared-run");

        var recreated = new LiveAgentRenderState();

        Assert.IsTrue(recreated.BeginDraft("shared-topic", "shared-run"));
    }

    private static AgentStep Step(string id)
        => new(id, id, AgentStepState.Started, null, null, null);
}
