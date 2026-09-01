using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.ComponentTests;

public sealed partial class MobileMeLifecycleComponentTests
{
    [TestMethod]
    public async Task AssistantRequest_TerminalDominatesEveryLateDispatchOutcome()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var request = await CommitRequest(state, "terminal-run", "terminal-line");
        var scope = state.CaptureAssistantAiRequestMutationScope(request);

        state.CompleteAssistantAiRequest(request.RunId);
        foreach (var result in new[]
                 {
                     TopicDispatchResult.Ok(request.RunId),
                     TopicDispatchResult.Reject("timeout", request.RunId, "late timeout"),
                     TopicDispatchResult.Reject("offline", request.RunId, "late offline")
                 })
        {
            var transition = state.RecordAssistantAiDispatch(scope, result);
            Assert.AreEqual(
                AssistantAiRequestTransitionOutcome.TerminalNoOp,
                transition.Outcome);
            Assert.AreEqual(AssistantAiRequestState.Completed, transition.Request?.State);
            Assert.AreEqual(0, transition.Request?.DispatchAttempts);
        }

        Assert.IsNull(state.GetPendingAssistantAiRequest("thread"));
    }

    [TestMethod]
    public async Task AssistantRequest_RetryIsRetiredByTerminalAndCannotReturnAfterRestart()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var request = await CommitRequest(state, "restart-run", "restart-line");
        var scope = state.CaptureAssistantAiRequestMutationScope(request);
        var retry = state.RecordAssistantAiDispatch(
            scope,
            TopicDispatchResult.Reject("offline", request.RunId, "offline"));
        Assert.AreEqual(AssistantAiRequestState.RetryPending, retry.Request?.State);

        state.CompleteAssistantAiRequest(request.RunId);
        var late = state.RecordAssistantAiDispatch(
            scope,
            TopicDispatchResult.Reject("timeout", request.RunId, "late"));
        Assert.AreEqual(AssistantAiRequestState.Completed, late.Request?.State);
        Assert.IsNull(state.GetPendingAssistantAiRequest("thread"));
        Assert.AreEqual(
            AssistantAiRequestProjection.Empty,
            AssistantAiRequestReducer.Project(state.GetAssistantAiRequest(request.RunId)));

        await state.DisposeAsync();
        var restarted = new AppState(
            secrets,
            new AppShutdownState(),
            storagePaths: new StoragePathSet(root));
        Assert.IsNull(restarted.GetPendingAssistantAiRequest("thread"));
        var replayed = restarted.GetAssistantAiRequest(request.RunId);
        Assert.AreEqual(AssistantAiRequestState.Completed, replayed?.State);
        var replayedProjection = AssistantAiRequestReducer.Project(replayed);
        Assert.IsFalse(replayedProjection.Busy);
        Assert.IsFalse(replayedProjection.StopVisible);
        Assert.IsFalse(replayedProjection.RetryVisible);
        Assert.IsNull(replayedProjection.Error);
    }

    [TestMethod]
    public async Task AssistantRequest_ScopeBlocksAtoBAndAtoBtoANewGeneration()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var accountA = state.ActiveAccountId!;
        var requestA = await CommitRequest(state, "shared-run", "shared-line");
        var staleScopeA = state.CaptureAssistantAiRequestMutationScope(requestA);

        var accountB = state.ImportProfile(CreateAccountProfile("other-owner", "thread"));
        var requestB = await CommitRequest(state, "shared-run", "shared-line");
        var scopeB = state.CaptureAssistantAiRequestMutationScope(requestB);

        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var staleInB = state.RecordAssistantAiDispatch(
                staleScopeA,
                TopicDispatchResult.Reject("offline", requestA.RunId, "from A"));
            Assert.AreEqual(AssistantAiRequestTransitionOutcome.StaleIdentity, staleInB.Outcome);
        }
        Assert.AreEqual(0, state.GetPendingAssistantAiRequest("thread")?.DispatchAttempts);

        var validB = state.RecordAssistantAiDispatch(
            scopeB,
            TopicDispatchResult.Reject("offline", requestB.RunId, "from B"));
        Assert.AreEqual(AssistantAiRequestTransitionOutcome.Applied, validB.Outcome);
        Assert.AreEqual(1, validB.Request?.DispatchAttempts);

        Assert.IsTrue(state.SwitchAccount(accountA));
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var staleInNewA = state.RecordAssistantAiDispatch(
                staleScopeA,
                TopicDispatchResult.Reject("offline", requestA.RunId, "old A generation"));
            Assert.AreEqual(
                AssistantAiRequestTransitionOutcome.StaleIdentity,
                staleInNewA.Outcome);
        }
        Assert.AreEqual(0, state.GetPendingAssistantAiRequest("thread")?.DispatchAttempts);

        var restoredA = state.GetPendingAssistantAiRequest("thread")!;
        var newScopeA = state.CaptureAssistantAiRequestMutationScope(restoredA);
        var validA = state.RecordAssistantAiDispatch(
            newScopeA,
            TopicDispatchResult.Ok(restoredA.RunId));
        Assert.AreEqual(AssistantAiRequestTransitionOutcome.Applied, validA.Outcome);
        Assert.AreEqual(AssistantAiRequestState.Dispatched, validA.Request?.State);
        Assert.IsTrue(state.SwitchAccount(accountB));
        Assert.AreEqual(AssistantAiRequestState.RetryPending,
            state.GetPendingAssistantAiRequest("thread")?.State);
    }

    [TestMethod]
    public async Task AssistantRequest_CompletionAndRejectionRaceIsMonotonic()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var request = await CommitRequest(
                state,
                $"race-run-{iteration}",
                $"race-line-{iteration}");
            var scope = state.CaptureAssistantAiRequestMutationScope(request);
            await Task.WhenAll(
                Task.Run(() => state.CompleteAssistantAiRequest(request.RunId)),
                Task.Run(() => state.RecordAssistantAiDispatch(
                    scope,
                    TopicDispatchResult.Reject("timeout", request.RunId, "late"))));
            var late = state.RecordAssistantAiDispatch(
                scope,
                TopicDispatchResult.Reject("offline", request.RunId, "duplicate"));
            Assert.AreEqual(AssistantAiRequestState.Completed, late.Request?.State);
        }
    }

    [TestMethod]
    public async Task AssistantRequest_CancelledAbsorbsDuplicateCallbacksAndNewRunProceeds()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var cancelled = await CommitRequest(state, "cancelled-run", "cancelled-line");
        var cancelledScope = state.CaptureAssistantAiRequestMutationScope(cancelled);

        var terminal = state.CancelAssistantAiRequest(cancelledScope, "user stopped");
        Assert.AreEqual(AssistantAiRequestTransitionOutcome.Applied, terminal.Outcome);
        Assert.AreEqual(AssistantAiRequestState.Cancelled, terminal.Request?.State);

        foreach (var result in new[]
                 {
                     TopicDispatchResult.Ok(cancelled.RunId),
                     TopicDispatchResult.Reject("timeout", cancelled.RunId, "late timeout"),
                     TopicDispatchResult.Reject("offline", cancelled.RunId, "duplicate")
                 })
        {
            var late = state.RecordAssistantAiDispatch(cancelledScope, result);
            Assert.AreEqual(AssistantAiRequestTransitionOutcome.TerminalNoOp, late.Outcome);
            Assert.AreEqual(AssistantAiRequestState.Cancelled, late.Request?.State);
            Assert.AreEqual(0, late.Request?.DispatchAttempts);
        }

        var next = await CommitRequest(state, "next-run", "next-line");
        var nextScope = state.CaptureAssistantAiRequestMutationScope(next);
        var dispatched = state.RecordAssistantAiDispatch(
            nextScope,
            TopicDispatchResult.Ok(next.RunId));
        Assert.AreEqual(AssistantAiRequestTransitionOutcome.Applied, dispatched.Outcome);
        Assert.AreEqual(AssistantAiRequestState.Dispatched, dispatched.Request?.State);
    }

    [TestMethod]
    public async Task AgentHostMove_PausedAcrossAtoBtoAWithIdenticalIdsIsStale()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var accountA = state.ActiveAccountId!;
        var requestA = await CommitRequest(state, "host-run", "host-line");
        var staleThreadScope = state.CaptureActiveThreadMutationScope("thread");
        var staleRequestScope = state.CaptureAssistantAiRequestMutationScope(requestA);
        var target = new AgentExecutionHost(
            "target-device",
            "Target",
            DevicePlatforms.Windows);

        var accountB = state.ImportProfile(CreateAccountProfile("other-owner", "thread"));
        Assert.IsTrue(state.SwitchAccount(accountA));

        for (var iteration = 0; iteration < 1000; iteration++)
        {
            Assert.IsFalse(state.MoveOwnThreadAgentExecutionHost(
                staleThreadScope,
                target,
                staleRequestScope));
        }

        Assert.AreEqual("desktop", state.GetPendingAssistantAiRequest("thread")?.TargetDeviceId);
        var activeRequest = state.GetPendingAssistantAiRequest("thread")!;
        Assert.IsTrue(state.MoveOwnThreadAgentExecutionHost(
            state.CaptureActiveThreadMutationScope("thread"),
            target,
            state.CaptureAssistantAiRequestMutationScope(activeRequest)));
        Assert.AreEqual("target-device", state.GetPendingAssistantAiRequest("thread")?.TargetDeviceId);
        Assert.IsTrue(state.SwitchAccount(accountB));
    }

    [TestMethod]
    public async Task ScopedOperation_OldCatchAndFinallyCannotMutateReactivatedAccount()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var accountA = state.ActiveAccountId!;
        var request = await CommitRequest(state, "scope-run", "scope-line");
        var stale = state.CaptureScopedAsyncOperation(
            "component:send",
            "thread",
            request.TriggerLineId,
            request.OperationId,
            request.RunId);

        var accountB = state.ImportProfile(CreateAccountProfile("other-owner", "thread"));
        Assert.IsTrue(state.SwitchAccount(accountA));
        var current = state.CaptureScopedAsyncOperation("component:send", "thread");
        var error = "new scope";
        var busy = true;

        Assert.IsFalse(state.TryApplyScopedAsyncOperation(
            stale,
            () => error = "old catch"));
        Assert.IsFalse(state.TryCompleteScopedAsyncOperation(
            stale,
            () =>
            {
                busy = false;
                error = "old finally";
            }));
        Assert.AreEqual("new scope", error);
        Assert.IsTrue(busy);
        Assert.IsTrue(state.TryCompleteScopedAsyncOperation(
            current,
            () =>
            {
                busy = false;
                error = "current failure";
            }));
        Assert.AreEqual("current failure", error);
        Assert.IsFalse(busy);
        Assert.IsTrue(state.SwitchAccount(accountB));
    }

    [TestMethod]
    public async Task ScopedOperation_DisposeRecreateWithIdenticalIdsRejectsOldIdentity()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var first = CreateFirstRunState(root, secrets, "thread");
        var request = await CommitRequest(first, "same-run", "same-line");
        var stale = first.CaptureScopedAsyncOperation(
            "component:dispatch",
            "thread",
            request.TriggerLineId,
            request.OperationId,
            request.RunId);
        var staleThread = first.CaptureActiveThreadMutationScope("thread");
        await first.DisposeAsync();

        var recreated = new AppState(
            secrets,
            new AppShutdownState(),
            storagePaths: new StoragePathSet(root));
        var current = recreated.CaptureScopedAsyncOperation(
            "component:dispatch",
            "thread",
            request.TriggerLineId,
            request.OperationId,
            request.RunId);
        var visible = "unchanged";
        Assert.IsFalse(recreated.TryApplyScopedAsyncOperation(
            stale,
            () => visible = "old failure"));
        Assert.IsFalse(recreated.IsCurrentActiveThreadMutationScope(staleThread));
        Assert.IsTrue(recreated.TryCompleteScopedAsyncOperation(
            current,
            () => visible = "same-scope failure"));
        Assert.AreEqual("same-scope failure", visible);
        await recreated.DisposeAsync();
    }

    [TestMethod]
    public void ScopedOperation_TenThousandErrorContinuationsAreLatestWins()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var mutations = 0;
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var stale = state.CaptureScopedAsyncOperation(
                "component:error-stress",
                "thread");
            var current = state.CaptureScopedAsyncOperation(
                "component:error-stress",
                "thread");
            Assert.IsFalse(state.TryApplyScopedAsyncOperation(
                stale,
                () => mutations++));
            Assert.IsFalse(state.TryCompleteScopedAsyncOperation(
                stale,
                () => mutations++));
            Assert.IsTrue(state.TryCompleteScopedAsyncOperation(
                current,
                () => mutations++));
        }
        Assert.AreEqual(10_000, mutations);
    }

    [TestMethod]
    public void ScopedOperation_CompletionCannotRemoveSuccessorCreatedByCleanup()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var completing = state.CaptureScopedAsyncOperation("component:restore", "thread");
        ScopedAsyncOperation? successor = null;

        Assert.IsTrue(state.TryCompleteScopedAsyncOperation(
            completing,
            () => successor = state.CaptureScopedAsyncOperation(
                "component:restore",
                "thread")));
        Assert.IsNotNull(successor);
        Assert.IsTrue(state.IsCurrentScopedAsyncOperation(successor));
        Assert.IsTrue(state.TryCompleteScopedAsyncOperation(successor));
    }

    [TestMethod]
    public void ScopedOperation_AdvanceAtomicallyBindsTopicAndMessageIdentity()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        var initial = state.CaptureScopedAsyncOperation("component:send");

        Assert.IsTrue(state.TryAdvanceScopedAsyncOperation(
            initial,
            out var advanced,
            topicId: "thread",
            messageId: "reserved-line"));
        Assert.IsNotNull(advanced);
        Assert.IsFalse(state.IsCurrentScopedAsyncOperation(initial));
        Assert.IsTrue(state.TryCompleteScopedAsyncOperation(advanced));
    }

    [TestMethod]
    public void ScopedOperation_CommunicationIdentityRejectsReusedMessageId()
    {
        var state = CreateFirstRunState(NewStateRoot(), new MemorySecretStore(), "thread");
        state.Profile.Conversations.Add(new Conversation
        {
            Handle = "peer",
            Lines = [new ChatLine { Id = "message", Role = "assistant", Text = "hello" }]
        });
        var scope = state.CaptureScopedAsyncOperation(
            "component:communication-send:message",
            messageId: "message",
            conversationId: "peer");

        Assert.IsTrue(state.IsCurrentScopedAsyncOperation(scope));
        state.Profile.Conversations.Clear();
        state.Profile.Conversations.Add(new Conversation
        {
            Handle = "peer",
            Lines = [new ChatLine { Id = "different", Role = "assistant", Text = "new" }]
        });

        Assert.IsFalse(state.TryApplyScopedAsyncOperation(scope, Assert.Fail));
    }

    private static Task<AssistantAiRequest> CommitRequest(
        AppState state,
        string runId,
        string lineId)
        => state.CommitAssistantAiRequestAsync(
            "thread",
            new ChatLine
            {
                Id = lineId,
                Role = "user",
                Text = "content-free race probe",
                At = DateTimeOffset.UtcNow
            },
            runId,
            runId,
            new AgentExecutionHost("desktop", "Desktop", DevicePlatforms.Windows));
}
