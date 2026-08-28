using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Mesh.App.Components.Mobile;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.ComponentTests;

[TestClass]
[DoNotParallelize]
public sealed class MobileMeLifecycleComponentTests
{
    [TestMethod]
    public async Task AtomicRetry_AccountSwitchBeforeBegin_CommitsNowhereAndRequiresFreshReconciliation()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var accountA = state.ActiveAccountId!;
        using var sends = new TopicSendCoordinator(
            reconciliationQuery: new AppStateTopicSendReconciliationQuery(state));
        var snapshot = await PrepareAtomicRetryAsync(sends, accountA);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            SubmitAtomicRetry(sends, state, snapshot, entered, release).Kind);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accountB = state.ImportProfile(CreateAccountProfile("other-owner", "thread"));
        release.TrySetResult();
        await WaitUntilAsync(
            () => !sends.IsRunning(snapshot.OperationId),
            TimeSpan.FromSeconds(5));

        Assert.HasCount(0, state.ListTopicOutbox());
        Assert.IsTrue(state.SwitchAccount(accountA));
        Assert.HasCount(0, state.ListTopicOutbox());
        await sends.RequestReconciliationAsync(snapshot);
        await WaitUntilAsync(
            () => sends.TryGetOutcome(snapshot.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
            TimeSpan.FromSeconds(5));

        var committed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = sends.Submit(
            snapshot,
            (_, context) =>
            {
                var begin = context.BeginTopicRun(
                    CreateAtomicBeginCommand(snapshot),
                    () => state.BeginTopicRun(CreateAtomicBeginCommand(snapshot)));
                if (begin.DurableCommitted)
                    context.MarkDurableBoundaryEntered();
                committed.TrySetResult();
                return Task.FromResult(new TopicSendHandoff(
                    begin.DurableCommitted,
                    begin.Code));
            });
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(state.GetTopicOutbox(snapshot.RunId));
        Assert.IsTrue(state.SwitchAccount(accountB));
        Assert.HasCount(0, state.ListTopicOutbox());
    }

    [TestMethod]
    public async Task AtomicRetry_SameAccountDatabaseReplacementBeforeBegin_CommitsNowhere()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var account = state.ActiveAccountId!;
        using var sends = new TopicSendCoordinator(
            reconciliationQuery: new AppStateTopicSendReconciliationQuery(state));
        var snapshot = await PrepareAtomicRetryAsync(sends, account);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            SubmitAtomicRetry(sends, state, snapshot, entered, release).Kind);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.SignOut();
        Assert.IsTrue(state.SwitchAccount(account));
        release.TrySetResult();
        await WaitUntilAsync(
            () => !sends.IsRunning(snapshot.OperationId),
            TimeSpan.FromSeconds(5));

        Assert.IsNull(state.GetTopicOutbox(snapshot.RunId));
        Assert.AreEqual(
            TopicRunTriggerLookupKind.NotFound,
            state.QueryTopicRunTrigger(
                snapshot.OperationId,
                snapshot.RunId,
                snapshot.ThreadId,
                snapshot.LineId,
                snapshot.TargetDeviceId,
                account).Kind);
    }

    [TestMethod]
    public async Task AtomicRetry_TriggerEpochChangesAfterNotFound_CompareAndBeginRefusesStaleObservation()
    {
        var state = CreateFirstRunState(
            NewStateRoot(),
            new MemorySecretStore(),
            "thread");
        var account = state.ActiveAccountId!;
        using var sends = new TopicSendCoordinator(
            reconciliationQuery: new AppStateTopicSendReconciliationQuery(state));
        var snapshot = await PrepareAtomicRetryAsync(sends, account);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            SubmitAtomicRetry(sends, state, snapshot, entered, release).Kind);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var epochChange = CreateRemoteBeginCommand(
            "epoch-change-run",
            "thread",
            "epoch-change-line",
            DateTimeOffset.UtcNow,
            "epoch-change-operation");
        Assert.IsTrue(state.BeginTopicRun(epochChange).DurableCommitted);
        release.TrySetResult();
        await WaitUntilAsync(
            () => !sends.IsRunning(snapshot.OperationId),
            TimeSpan.FromSeconds(5));

        Assert.IsNull(state.GetTopicOutbox(snapshot.RunId));
        Assert.IsNotNull(state.GetTopicOutbox("epoch-change-run"));
    }

    [TestMethod]
    public void TriggerLookup_NullOrWrongAccountDatabase_IsUnavailableNeverNotFound()
    {
        var first = CreateFirstRunState(
            NewStateRoot(),
            new MemorySecretStore(),
            "thread");
        var expectedAccountId = first.ActiveAccountId!;
        first.SignOut();

        var unavailable = first.QueryTopicRunTrigger(
            "operation",
            "run",
            "thread",
            "line",
            "device",
            expectedAccountId);
        Assert.AreEqual(TopicRunTriggerLookupKind.Unavailable, unavailable.Kind);
        Assert.AreEqual("database_unavailable", unavailable.Reason);

        var wrong = CreateFirstRunState(
            NewStateRoot(),
            new MemorySecretStore(),
            "thread");
        var mismatch = wrong.QueryTopicRunTrigger(
            "operation",
            "run",
            "thread",
            "line",
            "device",
            expectedAccountId);
        Assert.AreEqual(TopicRunTriggerLookupKind.Unavailable, mismatch.Kind);
        Assert.AreEqual("account_mismatch", mismatch.Reason);
        wrong.SignOut();
    }

    [TestMethod]
    public async Task RenderedPreCommitNotFound_AccountSwitchInvalidatesRetryUntilFreshLookup()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var accountA = state.ActiveAccountId!;
        var identities = new InMemoryTopicSendIdentityStore();
        var harness = CreateHarness(
            new ControllableDeviceTransport(),
            identities: identities,
            state: state,
            stateRoot: root,
            secrets: secrets);
        await using var renderer = new ComponentRenderer(harness.Services);
        var rendered = await renderer.MountAsync<MobileMe>();
        MobileMe.BeforeBeginTopicRunCheckpointHook = _ =>
            throw new IOException("fail before durable begin");
        TopicSendSnapshot snapshot;
        try
        {
            await renderer.InputAsync(rendered.Id, "Message your assistant", "account fence");
            var revision = state.GetTopicDraftState("thread")!.Revision;
            await renderer.ClickAsync(rendered.Id, "Send");
            await WaitUntilAsync(
                () => harness.Sends.TryGetSnapshot(
                    "thread",
                    "remote-device",
                    revision,
                    out _,
                    accountA),
                TimeSpan.FromSeconds(5));
            Assert.IsTrue(harness.Sends.TryGetSnapshot(
                "thread",
                "remote-device",
                revision,
                out snapshot,
                accountA));
            Assert.IsNotNull(snapshot);
            await WaitUntilAsync(
                () => harness.Sends.TryGetOutcome(snapshot.OperationId, out var outcome)
                      && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            MobileMe.BeforeBeginTopicRunCheckpointHook = null;
        }

        var accountB = state.ImportProfile(CreateAccountProfile("other-owner", "thread"));
        Assert.AreNotEqual(accountA, accountB);
        await WaitUntilAsync(
            () => !harness.Sends.IsRunning(snapshot!.OperationId),
            TimeSpan.FromSeconds(5));
        var executions = 0;
        var blocked = harness.Sends.Submit(
            snapshot!,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreEqual(TopicSendSubmissionKind.ReconciliationRequired, blocked.Kind);
        Assert.AreEqual(0, executions);
        Assert.HasCount(0, state.ListTopicOutbox());

        Assert.IsTrue(state.SwitchAccount(accountA));
        await harness.Sends.RequestReconciliationAsync(snapshot!);
        var retry = harness.Sends.Submit(
            snapshot!,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            draftCleanup: new TopicSendDraftCleanup(_ => Task.FromResult(
                TopicSendDraftCleanupResult.DraftClearPersisted)));
        Assert.AreEqual(TopicSendSubmissionKind.Started, retry.Kind);
        await WaitUntilAsync(() => executions == 1, TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, executions);
        await renderer.UnmountAsync(rendered.Id);
    }

    [TestMethod]
    public async Task ProductionRouter_AccountSwitchAtDurableBeginFenceCannotWriteNewDatabase()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var accountA = state.ActiveAccountId!;
        var transport = new ControllableDeviceTransport();
        var harness = CreateHarness(
            transport,
            state: state,
            stateRoot: root,
            secrets: secrets,
            router: new TopicExecutionRouter(
                state,
                new NoopTurnRunner(),
                new DurableObservingTransport(
                    state,
                    new DurableTransportCounter())));
        await using var renderer = new ComponentRenderer(harness.Services);
        var rendered = await renderer.MountAsync<MobileMe>();
        TopicSendSnapshot snapshot;

        MobileMe.BeforeBeginTopicRunCheckpointHook = _ =>
            throw new IOException("fail before durable begin");
        try
        {
            await renderer.InputAsync(
                rendered.Id,
                "Message your assistant",
                "account switch at durable begin");
            var revision = state.GetTopicDraftState("thread")!.Revision;
            await renderer.ClickAsync(rendered.Id, "Send");
            await WaitUntilAsync(
                () => harness.Sends.TryGetSnapshot(
                    "thread",
                    "remote-device",
                    revision,
                    out _,
                    accountA),
                TimeSpan.FromSeconds(5));
            Assert.IsTrue(harness.Sends.TryGetSnapshot(
                "thread",
                "remote-device",
                revision,
                out snapshot,
                accountA));
            await WaitUntilAsync(
                () => harness.Sends.TryGetOutcome(snapshot.OperationId, out var outcome)
                      && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
                TimeSpan.FromSeconds(5));

            string? accountB = null;
            MobileMe.BeforeBeginTopicRunCheckpointHook = _ =>
                accountB = state.ImportProfile(CreateAccountProfile("other-owner", "thread"));
            await renderer.ClickAsync(rendered.Id, "Send");
            await WaitUntilAsync(
                () => accountB is not null && !harness.Sends.IsRunning(snapshot.OperationId),
                TimeSpan.FromSeconds(5));

            Assert.AreNotEqual(accountA, accountB);
            Assert.HasCount(0, state.ListTopicOutbox());
            Assert.IsTrue(state.SwitchAccount(accountA));
            Assert.HasCount(0, state.ListTopicOutbox());

            await harness.Sends.RequestReconciliationAsync(snapshot);
            await WaitUntilAsync(
                () => harness.Sends.TryGetOutcome(snapshot.OperationId, out var outcome)
                      && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
                TimeSpan.FromSeconds(5));
            MobileMe.BeforeBeginTopicRunCheckpointHook = null;
            await renderer.ClickAsync(rendered.Id, "Send");
            await WaitUntilAsync(
                () => state.ListTopicOutbox().Count == 1,
                TimeSpan.FromSeconds(5));
            Assert.HasCount(1, state.ListTopicOutbox());
        }
        finally
        {
            MobileMe.BeforeBeginTopicRunCheckpointHook = null;
            await renderer.UnmountAsync(rendered.Id);
            state.SignOut();
        }
    }

    [TestMethod]
    public async Task AuthoritativeNotFound_SameAccountDatabaseGenerationChangeInvalidatesToken()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var account = state.ActiveAccountId!;
        using var query = new BlockingAppStateReconciliationQuery(state);
        using var sends = new TopicSendCoordinator(reconciliationQuery: query);
        var snapshot = sends.CreateSnapshot(
            "thread",
            "remote-device",
            1,
            "database-generation",
            DateTimeOffset.UtcNow,
            account);
        var retryable = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sends.Submit(
            snapshot,
            (_, _) => Task.FromException<TopicSendHandoff>(
                new IOException("fail before durable begin")),
            outcome =>
            {
                if (outcome.Kind == TopicSendOutcomeKind.RetryableFailed)
                    retryable.TrySetResult();
                return Task.CompletedTask;
            });
        await retryable.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = state.QueryTopicRunTrigger(
            snapshot.OperationId,
            snapshot.RunId,
            snapshot.ThreadId,
            snapshot.LineId,
            snapshot.TargetDeviceId,
            account);

        query.BlockNext();
        state.SignOut();
        Assert.IsTrue(state.SwitchAccount(account));
        await query.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var after = state.QueryTopicRunTrigger(
            snapshot.OperationId,
            snapshot.RunId,
            snapshot.ThreadId,
            snapshot.LineId,
            snapshot.TargetDeviceId,
            account);
        Assert.AreEqual(before.DatabaseIdentity, after.DatabaseIdentity);
        Assert.AreNotEqual(before.DatabaseGeneration, after.DatabaseGeneration);

        var executions = 0;
        var blocked = sends.Submit(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "unexpected"));
            });
        Assert.AreNotEqual(TopicSendSubmissionKind.Started, blocked.Kind);
        Assert.AreEqual(0, executions);
        Assert.HasCount(0, state.ListTopicOutbox());
        query.Release();
        await WaitUntilAsync(
            () => !sends.IsRunning(snapshot.OperationId),
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(query.Calls >= 2);
        var refreshed = sends.Submit(
            snapshot,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new TopicSendHandoff(true, "accepted"));
            },
            draftCleanup: new TopicSendDraftCleanup(_ => Task.FromResult(
                TopicSendDraftCleanupResult.DraftClearPersisted)));
        Assert.AreEqual(TopicSendSubmissionKind.Started, refreshed.Kind);
        await WaitUntilAsync(() => executions == 1, TimeSpan.FromSeconds(5));
        state.SignOut();
    }

    [TestMethod]
    public async Task RenderedComposer_ClickDisposeRecreate_CompletesSameDurableSendExactOnce()
    {
        var transport = new ControllableDeviceTransport();
        var harness = CreateHarness(
            transport,
            reconciliation: TopicSendReconciliationKind.Accepted);
        await using var renderer = new ComponentRenderer(harness.Services);

        using var operationProbe = new UiOperationCompletionProbe(
            harness.Services.GetRequiredService<UiOperationCoordinator>());
        var disposed = await MountSubmitAndDisposeAsync(
            renderer, operationProbe, harness, transport);
        await disposed.ObserverDetached.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, harness.Sends.ObserverCount(disposed.Submission.OperationId));
        Assert.AreEqual(
            0,
            harness.Sends.ObserverReferenceCount(disposed.Submission.OperationId),
            "the coordinator retained a disposed component callback");
        Console.WriteLine(
            $"OBSERVER_DETACHED operation={disposed.Submission.OperationId} observers=0 delegates=0");

        var recreated = await renderer.MountAsync<MobileMe>();
        await operationProbe.WaitAsync(
            "ui.topic.open",
            ComponentLifecycleId(recreated.Component));
        Assert.AreEqual(1, transport.SubmitCount);

        transport.Release.TrySetResult();
        var completed = await WaitForAsync(
            () => harness.State.Profile.OwnThreads[0].Lines.Any(line =>
                      line.Id == transport.LastDraft?.TriggerLineId)
                  && renderer.AttributeValue(recreated.Id, "textarea", "value") == "",
            TimeSpan.FromSeconds(5));
        harness.Sends.TryGetOutcome(disposed.Submission.OperationId, out var observedOutcome);
        Assert.IsTrue(
            completed,
            $"draft={harness.State.GetTopicDraft("thread")};"
            + $"outcome={observedOutcome?.Kind};"
            + $"observers={harness.Sends.ObserverCount(disposed.Submission.OperationId)};"
            + renderer.RenderedText(recreated.Id));

        Assert.AreEqual(1, transport.SubmitCount);
        Assert.AreEqual("", harness.State.GetTopicDraft("thread"));
        await renderer.UnmountAsync(recreated.Id);
        ForceCollection(disposed.ComponentReference);
        Assert.IsFalse(
            disposed.ComponentReference.IsAlive,
            "Disposed component remained rooted after the coordinator detached every observer.");
    }

    [TestMethod]
    public async Task RenderedComposer_QueuedNotificationUnmountsBeforeRendererEntry()
    {
        var transport = new ControllableDeviceTransport();
        var dispatch = new BlockingObserverDispatcherFactory();
        var harness = CreateHarness(transport, observerDispatcherFactory: dispatch);
        await using var renderer = new ComponentRenderer(harness.Services);
        using var operationProbe = new UiOperationCompletionProbe(
            harness.Services.GetRequiredService<UiOperationCoordinator>());

        var componentReference = await RunQueuedNotificationUnmountAsync(
            renderer, operationProbe, harness, transport, dispatch);
        var replacement = await renderer.MountAsync<MobileMe>();
        await operationProbe.WaitAsync(
            "ui.topic.open",
            ComponentLifecycleId(replacement.Component));
        await renderer.UnmountAsync(replacement.Id);
        ForceCollection(componentReference);
        Assert.IsFalse(
            componentReference.IsAlive,
            "the rendered component remained rooted after callback quiescence");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunQueuedNotificationUnmountAsync(
        ComponentRenderer renderer,
        UiOperationCompletionProbe operationProbe,
        ComponentHarness harness,
        ControllableDeviceTransport transport,
        BlockingObserverDispatcherFactory dispatch)
    {
        var mounted = await renderer.MountAsync<MobileMe>();
        await operationProbe.WaitAsync(
            "ui.topic.open",
            ComponentLifecycleId(mounted.Component));
        await renderer.InputAsync(mounted.Id, "Message your assistant", "race");
        var revision = harness.State.GetTopicDraftState("thread")!.Revision;
        await harness.State.FlushTopicDraftAsync("thread", revision);
        await renderer.ClickAsync(mounted.Id, "Send");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(harness.Sends.TryGetSnapshot(
            "thread",
            transport.Device.DeviceId,
            revision,
            out var submission));
        Assert.IsNotNull(submission);
        var subscription = ObserverSubscription(
            mounted.Component,
            submission.OperationId);

        transport.Release.TrySetResult();
        await dispatch.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(
            0,
            subscription.InFlightCallbackCount,
            "queued work must not acquire the component callback before renderer entry");
        AssertQueuedWorkHasNoComponentDelegate(dispatch.QueuedWork);

        var reference = new WeakReference(mounted.Component);
        try
        {
            await renderer.UnmountAsync(mounted.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(
                ComponentDisposalTask(mounted.Component) is { IsCompleted: true },
                "renderer unmount did not invoke and complete MobileMe.DisposeAsync");
            Assert.AreEqual(0, harness.Sends.ObserverCount(submission.OperationId));
            Assert.AreEqual(0, subscription.InFlightCallbackCount);
            Assert.AreEqual(0, harness.Sends.ObserverReferenceCount(submission.OperationId));
        }
        finally
        {
            dispatch.Release.TrySetResult();
        }

        await dispatch.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, subscription.InFlightCallbackCount);
        Console.WriteLine(
            $"QUEUED_AFTER_DETACH operation={submission.OperationId} observers=0 inflight=0 delegates=0 callbacks=0");
        return reference;
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("error")]
    [DataRow("cancellation")]
    public async Task RendererUnmount_WaitsOnlyForExecutingCallback(string completion)
    {
        var coordinator = new TopicSendCoordinator();
        var snapshot = coordinator.CreateSnapshot(
            "thread",
            "device",
            1,
            "fingerprint",
            DateTimeOffset.UtcNow);
        var handoffRelease = NewSignal();
        Assert.IsTrue(coordinator.TrySubmit(
            snapshot,
            async _ =>
            {
                await handoffRelease.Task;
                return new TopicSendHandoff(true, "accepted");
            }));

        var control = new RendererObserverProbeControl(completion);
        var services = new ServiceCollection()
            .AddSingleton(coordinator)
            .AddSingleton(control)
            .AddLogging()
            .BuildServiceProvider();
        await using var renderer = new ComponentRenderer(services);
        var mounted = await renderer.MountAsync<RendererObserverProbe>();
        await control.Attached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        handoffRelease.TrySetResult();
        await control.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unmount = renderer.UnmountAsync(mounted.Id);
        await control.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(
            control.DisposeCompleted.Task.IsCompleted,
            "renderer disposal claimed quiescence while a callback was executing");
        Assert.AreEqual(1, control.Subscription!.InFlightCallbackCount);

        control.CallbackRelease.TrySetResult();
        await control.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await unmount.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, control.Subscription.InFlightCallbackCount);
        Assert.AreEqual(1, control.InvocationCount);
        Assert.AreEqual(0, coordinator.ObserverCount(snapshot.OperationId));
        Console.WriteLine(
            $"EXECUTING_CALLBACK completion={completion} disposal=completed inflight=0 invocations=1");
    }

    private static void AssertQueuedWorkHasNoComponentDelegate(Func<Task>? queuedWork)
    {
        Assert.IsNotNull(queuedWork);
        Assert.IsNotInstanceOfType<MobileMe>(queuedWork.Target);
        foreach (var field in queuedWork.Target?.GetType().GetFields(
                     System.Reflection.BindingFlags.Instance
                     | System.Reflection.BindingFlags.Public
                     | System.Reflection.BindingFlags.NonPublic) ?? [])
        {
            Assert.IsFalse(
                typeof(Delegate).IsAssignableFrom(field.FieldType),
                $"queued renderer work captured delegate field {field.Name}");
            Assert.IsFalse(
                typeof(MobileMe).IsAssignableFrom(field.FieldType),
                $"queued renderer work captured component field {field.Name}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<DisposedComposerEvidence> MountSubmitAndDisposeAsync(
        ComponentRenderer renderer,
        UiOperationCompletionProbe operationProbe,
        ComponentHarness harness,
        ControllableDeviceTransport transport)
    {
        var mounted = await renderer.MountAsync<MobileMe>();
        var lifecycleId = ComponentLifecycleId(mounted.Component);
        await operationProbe.WaitAsync("ui.topic.open", lifecycleId);
        await renderer.InputAsync(mounted.Id, "Message your assistant", "send me");
        var submittedRevision = harness.State.GetTopicDraftState("thread")!.Revision;
        await harness.State.FlushTopicDraftAsync("thread", submittedRevision);
        await renderer.ClickAsync(mounted.Id, "Send");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(
            "send me",
            harness.State.GetTopicDraft("thread"),
            "the accepted dispatch barrier must precede draft cleanup");
        Assert.IsTrue(harness.Sends.TryGetSnapshot(
            "thread",
            transport.Device.DeviceId,
            submittedRevision,
            out var submission));
        Assert.IsNotNull(submission);
        Assert.AreEqual(1, harness.Sends.ObserverCount(submission.OperationId));
        Assert.AreEqual(1, harness.Sends.ObserverReferenceCount(submission.OperationId));

        var detached = harness.Sends.WaitForObserverDetachedAsync(
            submission.OperationId,
            lifecycleId);
        var reference = new WeakReference(mounted.Component);
        await renderer.UnmountAsync(mounted.Id);
        return new(reference, submission, detached);
    }

    [TestMethod]
    public async Task RenderedComposer_DurableBoundaryCompletesWithoutDuplicateSubmit()
    {
        var transport = new ControllableDeviceTransport();
        var harness = CreateHarness(transport);
        await using var renderer = new ComponentRenderer(harness.Services);
        var rendered = await renderer.MountAsync<MobileMe>();

        await renderer.InputAsync(rendered.Id, "Message your assistant", "unknown outcome");
        await renderer.ClickAsync(rendered.Id, "Send");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Release.TrySetResult();

        await WaitUntilAsync(
            () => renderer.MarkupContains(rendered.Id, "Checking the durable handoff status")
                  || harness.Sends.CompletedIdentityCount == 1,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => harness.Sends.CompletedIdentityCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, transport.SubmitCount);
        Assert.AreEqual("", harness.State.GetTopicDraft("thread"));
        await renderer.UnmountAsync(rendered.Id);
    }

    [TestMethod]
    public async Task RenderedComposer_PreHandoffFailureRetriesUnchangedDraft()
    {
        var transport = new ControllableDeviceTransport();
        var identities = new FailingTopicSendIdentityStore(1);
        var harness = CreateHarness(transport, identities: identities);
        try
        {
            await using var renderer = new ComponentRenderer(harness.Services);
            var rendered = await renderer.MountAsync<MobileMe>();

            await renderer.InputAsync(rendered.Id, "Message your assistant", "retry unchanged");
            await renderer.ClickAsync(rendered.Id, "Send");
            await identities.SaveAttempted.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.Dispatcher.InvokeAsync(() => { });
            Assert.IsTrue(
                renderer.MarkupContains(rendered.Id, "send identity could not be saved"),
                renderer.RenderedText(rendered.Id));
            Assert.AreEqual(0, transport.SubmitCount);
            Assert.AreEqual("retry unchanged", harness.State.GetTopicDraft("thread"));

            await renderer.ClickAsync(rendered.Id, "Send");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, transport.SubmitCount);
            transport.Release.TrySetResult();
            await WaitUntilAsync(
                () => harness.State.GetTopicDraft("thread") == "",
                TimeSpan.FromSeconds(5));

            await renderer.UnmountAsync(rendered.Id);
        }
        finally
        {
            transport.Release.TrySetResult();
            await harness.Services.DisposeAsync();
            await harness.State.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RenderedComposer_BeginPersistenceFailureRetainsRetryableDraft()
    {
        var transport = new ControllableDeviceTransport
        {
            ImmediateResult = TopicDispatchResult.Reject(
                "local_persistence_failed",
                error: "The run could not be durably started.")
        };
        var identities = new InMemoryTopicSendIdentityStore();
        var harness = CreateHarness(transport, identities: identities);
        await using var renderer = new ComponentRenderer(harness.Services);
        var rendered = await renderer.MountAsync<MobileMe>();

        await renderer.InputAsync(rendered.Id, "Message your assistant", "durable retry");
        await renderer.ClickAsync(rendered.Id, "Send");
        var scope = TopicSendSnapshot.ScopeId(
            "thread",
            transport.Device.DeviceId);
        await WaitUntilAsync(
            () => identities.TryGetUnresolved(scope, out var identity)
                  && identity is not null
                  && harness.Sends.TryGetOutcome(identity.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual("durable retry", harness.State.GetTopicDraft("thread"));
        Assert.AreEqual(1, transport.SubmitCount);
        transport.ImmediateResult = null;
        await renderer.ClickAsync(rendered.Id, "Send");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Release.TrySetResult();
        await WaitUntilAsync(
            () => harness.State.GetTopicDraft("thread") == "",
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, transport.SubmitCount);
        Assert.AreEqual(
            1,
            harness.State.Profile.OwnThreads.Single()
                .Lines.Count(line => line.Text == "durable retry"));
        await renderer.UnmountAsync(rendered.Id);
    }

    [TestMethod]
    public async Task RenderedComposer_PostCommitCrash_ReopensDatabaseAndRecoversJournalIdentity()
    {
        var root = NewStateRoot();
        var journalRoot = Path.Combine(root, "send-journal");
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var receiverState = CreateFirstRunState(
            Path.Combine(root, "receiver"),
            new MemorySecretStore(),
            "thread");
        var accountId = state.ActiveAccountId!;
        var counter = new DurableTransportCounter();
        var router = new TopicExecutionRouter(
            state,
            new NoopTurnRunner(),
            new DurableObservingTransport(
                state,
                counter,
                receiverState,
                completeRemoteRun: true));
        var identities = new FileTopicSendIdentityStore(journalRoot);
        var harness = CreateHarness(
            new ControllableDeviceTransport(),
            identities: identities,
            state: state,
            stateRoot: root,
            secrets: secrets,
            router: router);
        var checkpoint = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MobileMe.DurableBeginCheckpointHook = operationId =>
        {
            checkpoint.TrySetResult(operationId);
            throw new TopicSendJournalCrashException(
                "simulated process loss after BeginTopicRun commit");
        };

        TopicSendIdentityRecord persisted;
        var scope = StableId(
            "scope",
            string.Join("\0", "topic-send-v3", "thread", "remote-device"));
        try
        {
            await using var renderer = new ComponentRenderer(harness.Services);
            var rendered = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(
                rendered.Id,
                "Message your assistant",
                "recover committed first run");
            await renderer.ClickAsync(rendered.Id, "Send");
            var checkpointOperation = await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await identities.WaitUntilAsync(
                () => identities.TryGetUnresolved(scope, out _),
                TimeSpan.FromSeconds(5));
            Assert.IsTrue(identities.TryGetUnresolved(scope, out var journal));
            persisted = journal!;
            Assert.AreEqual(checkpointOperation, persisted.OperationId);
            Assert.AreEqual(TopicSendJournalLifecycle.PreHandoff, persisted.Lifecycle);
            var lookup = state.QueryTopicRunTrigger(
                persisted.OperationId,
                persisted.RunId,
                "thread",
                persisted.LineId,
                "remote-device");
            Assert.AreEqual(TopicRunTriggerLookupKind.Found, lookup.Kind);
            await renderer.UnmountAsync(rendered.Id);
        }
        finally
        {
            MobileMe.DurableBeginCheckpointHook = null;
            await harness.Services.DisposeAsync();
            state.SignOut();
        }

        var restartedState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(root));
        var restartedRouter = new TopicExecutionRouter(
            restartedState,
            new NoopTurnRunner(),
            new DurableObservingTransport(restartedState, counter));
        var restartedStore = new FileTopicSendIdentityStore(journalRoot);
        var restarted = CreateHarness(
            new ControllableDeviceTransport(),
            identities: restartedStore,
            state: restartedState,
            stateRoot: root,
            secrets: secrets,
            router: restartedRouter,
            initializeState: false);
        var recoveredSubmission = restarted.Sends.CreateSnapshot(
            "thread",
            "remote-device",
            persisted.ComposerRevision,
            persisted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            accountId);
        await restarted.Sends.RequestReconciliationAsync(
            recoveredSubmission,
            draftCleanup: new TopicSendDraftCleanup(async _ =>
            {
                var result = await restartedState.CompareAndClearTopicDraftAsync(
                    "thread",
                    persisted.ComposerRevision,
                    CancellationToken.None);
                return result == MeshDb.ComposerDraftClearResult.Superseded
                    ? TopicSendDraftCleanupResult.DraftClearSuperseded
                    : TopicSendDraftCleanupResult.DraftClearPersisted;
            }));
        Assert.IsTrue(restarted.Sends.TryGetOutcome(
            persisted.OperationId,
            out var unavailableOutcome));
        Assert.AreEqual(TopicSendOutcomeKind.Reconciling, unavailableOutcome!.Kind);
        Assert.AreEqual(1, counter.ForwardCount);
        await using (var renderer = new ComponentRenderer(restarted.Services))
        {
            var rendered = await renderer.MountAsync<MobileMe>();
            Assert.AreEqual(1, counter.ForwardCount);
            Assert.IsTrue(restartedState.SwitchAccount(accountId));
            await restartedStore.WaitUntilAsync(
                () => restartedState.GetTopicDraft("thread") == ""
                      && !restartedStore.TryGetUnresolved(scope, out _),
                TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, counter.ForwardCount);
            Assert.AreEqual(1, counter.ObservedOutboxCount);
            Assert.AreEqual(1, counter.ObservedCorrelationCount);
            Assert.AreEqual(1, counter.RequestEnvelopeCount);
            Assert.AreEqual(1, counter.ExecutionCount);
            Assert.AreEqual(1, counter.TerminalCount);
            Assert.HasCount(0, restartedState.ListTopicOutbox());
            Assert.IsTrue(restartedState.IsRetainedTopicRunCorrelation(
                persisted.RunId,
                "thread",
                "remote-device"));
            Assert.AreEqual(
                1,
                restartedState.Profile.OwnThreads.Single(thread => thread.Id == "thread")
                    .Lines.Count(line => line.Id == persisted.LineId));
            Assert.AreEqual(
                1,
                receiverState.ListInboundTopicRuns().Count(item =>
                    item.RunId == persisted.RunId
                    && item.State == InboundTopicRunStates.Completed));
            var recoveredTrigger = restartedState.QueryTopicRunTrigger(
                persisted.OperationId,
                persisted.RunId,
                "thread",
                persisted.LineId,
                "remote-device");
            Assert.AreEqual(TopicRunTriggerLookupKind.Found, recoveredTrigger.Kind);
            Assert.AreEqual(persisted.RunId, recoveredTrigger.RunId);
            Assert.IsTrue(recoveredTrigger.Terminal);
            await renderer.UnmountAsync(rendered.Id);
        }
        await restarted.Services.DisposeAsync();
        restartedState.SignOut();
        receiverState.SignOut();
    }

    [TestMethod]
    public async Task RenderedComposer_PreCommitCrash_RestartRetriesSameJournalIdentity()
    {
        var root = NewStateRoot();
        var journalRoot = Path.Combine(root, "send-journal");
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "thread");
        var accountId = state.ActiveAccountId!;
        var counter = new DurableTransportCounter();
        var identities = new FileTopicSendIdentityStore(journalRoot);
        var harness = CreateHarness(
            new ControllableDeviceTransport(),
            identities: identities,
            state: state,
            stateRoot: root,
            secrets: secrets,
            router: new TopicExecutionRouter(
                state,
                new NoopTurnRunner(),
                new DurableObservingTransport(state, counter)));
        var scope = StableId(
            "scope",
            string.Join("\0", "topic-send-v3", "thread", "remote-device"));
        MobileMe.BeforeBeginTopicRunCheckpointHook = _ =>
            throw new TopicSendJournalCrashException(
                "simulated process loss before BeginTopicRun");
        TopicSendIdentityRecord persisted;
        try
        {
            await using var renderer = new ComponentRenderer(harness.Services);
            var rendered = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(
                rendered.Id,
                "Message your assistant",
                "retry same durable identity");
            await renderer.ClickAsync(rendered.Id, "Send");
            await identities.WaitUntilAsync(
                () => identities.TryGetUnresolved(scope, out _),
                TimeSpan.FromSeconds(5));
            Assert.IsTrue(identities.TryGetUnresolved(scope, out var journal));
            persisted = journal!;
            Assert.AreEqual(
                TopicRunTriggerLookupKind.NotFound,
                state.QueryTopicRunTrigger(
                    persisted.OperationId,
                    persisted.RunId,
                    "thread",
                    persisted.LineId,
                    "remote-device").Kind);
            await renderer.UnmountAsync(rendered.Id);
            await harness.Sends.DisposeAsync();

            var unavailableHarness = CreateHarness(
                new ControllableDeviceTransport(),
                identities: identities,
                state: state,
                reconciliation: TopicSendReconciliationKind.Unavailable);
            await using var unavailableRenderer =
                new ComponentRenderer(unavailableHarness.Services);
            var unavailableRender =
                await unavailableRenderer.MountAsync<MobileMe>();
            await WaitUntilAsync(
                () => unavailableRenderer.MarkupContains(
                    unavailableRender.Id,
                    "Retry status"),
                TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, counter.ForwardCount);
            await unavailableRenderer.ClickAsync(
                unavailableRender.Id,
                "Retry status");
            Assert.AreEqual(
                0,
                counter.ForwardCount,
                "Manual status retry must not submit while reconciliation is unavailable.");
            await unavailableRenderer.UnmountAsync(unavailableRender.Id);
            await unavailableHarness.Services.DisposeAsync();
        }
        finally
        {
            MobileMe.BeforeBeginTopicRunCheckpointHook = null;
            await harness.Services.DisposeAsync();
            state.SignOut();
        }

        var restartedState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(root));
        var restartedStore = new FileTopicSendIdentityStore(journalRoot);
        var restarted = CreateHarness(
            new ControllableDeviceTransport(),
            identities: restartedStore,
            state: restartedState,
            stateRoot: root,
            secrets: secrets,
            router: new TopicExecutionRouter(
                restartedState,
                new NoopTurnRunner(),
                new DurableObservingTransport(restartedState, counter)),
            initializeState: false);
        var recoveredSubmission = restarted.Sends.CreateSnapshot(
            "thread",
            "remote-device",
            persisted.ComposerRevision,
            persisted.DraftFingerprint,
            DateTimeOffset.UtcNow,
            accountId);
        await restarted.Sends.RequestReconciliationAsync(recoveredSubmission);
        Assert.IsTrue(restarted.Sends.TryGetOutcome(
            persisted.OperationId,
            out var unavailableOutcome));
        Assert.AreEqual(TopicSendOutcomeKind.Reconciling, unavailableOutcome!.Kind);
        Assert.AreEqual(0, counter.ForwardCount);
        await using (var renderer = new ComponentRenderer(restarted.Services))
        {
            var unavailableRender = await renderer.MountAsync<MobileMe>();
            Assert.AreEqual(0, counter.ForwardCount);
            await renderer.UnmountAsync(unavailableRender.Id);
            Assert.IsTrue(restartedState.SwitchAccount(accountId));
            var rendered = await renderer.MountAsync<MobileMe>();
            await WaitUntilAsync(
                () => restarted.Sends.TryGetOutcome(
                    persisted.OperationId,
                    out var outcome)
                      && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
                TimeSpan.FromSeconds(5));
            await renderer.ClickAsync(rendered.Id, "Send");
            await restartedStore.WaitUntilAsync(
                () => restartedState.GetTopicDraft("thread") == "",
                TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, counter.ForwardCount);
            Assert.AreEqual(
                TopicRunTriggerLookupKind.Found,
                restartedState.QueryTopicRunTrigger(
                    persisted.OperationId,
                    persisted.RunId,
                    "thread",
                    persisted.LineId,
                    "remote-device").Kind);
            await renderer.UnmountAsync(rendered.Id);
        }
        await restarted.Services.DisposeAsync();
    }

    [TestMethod]
    public async Task RenderedComposer_TerminalWriteCrash_RecreateCompletesCleanupExactOnce()
    {
        var transport = new ControllableDeviceTransport();
        var stateRoot = NewStateRoot();
        var journalRoot = Path.Combine(stateRoot, "send-journal");
        var identities = new FileTopicSendIdentityStore(journalRoot);
        var secrets = new MemorySecretStore();
        var crash = new OneShotJournalCrash(
            "terminal", TopicSendJournalBoundary.AfterWrite);
        var firstHarness = CreateHarness(
            transport,
            identities: identities,
            faultInjector: crash,
            stateRoot: stateRoot,
            secrets: secrets);
        await using (var renderer = new ComponentRenderer(firstHarness.Services))
        {
            var rendered = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(rendered.Id, "Message your assistant", "terminal crash");
            var revision = firstHarness.State.GetTopicDraftState("thread")!.Revision;
            await firstHarness.State.FlushTopicDraftAsync("thread", revision);
            await renderer.ClickAsync(rendered.Id, "Send");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Release.TrySetResult();
            await crash.TriggeredTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("terminal crash", firstHarness.State.GetTopicDraft("thread"));
            await renderer.UnmountAsync(rendered.Id);
        }

        var recoveredHarness = CreateHarness(
            transport,
            identities: identities,
            stateRoot: stateRoot,
            secrets: secrets);
        await using var recoveredRenderer = new ComponentRenderer(recoveredHarness.Services);
        Assert.AreEqual("terminal crash", recoveredHarness.State.GetTopicDraft("thread"));
        var recovered = await recoveredRenderer.MountAsync<MobileMe>();
        await identities.WaitUntilAsync(
            () => recoveredHarness.State.GetTopicDraft("thread") == ""
                  && !identities.TryGetUnresolved(
                StableId(
                    "scope",
                    string.Join(
                        "\0",
                        "topic-send-v3",
                        "thread",
                        transport.Device.DeviceId)),
                out _),
            TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, transport.SubmitCount);
        await recoveredRenderer.UnmountAsync(recovered.Id);

        var verifiedHarness = CreateHarness(
            transport,
            identities: new FileTopicSendIdentityStore(journalRoot),
            stateRoot: stateRoot,
            secrets: secrets);
        Assert.AreEqual("", verifiedHarness.State.GetTopicDraft("thread"));
        Assert.IsFalse(new FileTopicSendIdentityStore(journalRoot).TryGetUnresolved(
            StableId(
                "scope",
                string.Join(
                    "\0",
                    "topic-send-v3",
                    "thread",
                    transport.Device.DeviceId)),
            out _));
    }

    [TestMethod]
    public async Task RenderedComposer_DraftClearFailureAcrossFreshStores_RemainsFencedUntilDurableRetry()
    {
        var transport = new ControllableDeviceTransport();
        var stateRoot = NewStateRoot();
        var journalRoot = Path.Combine(stateRoot, "send-journal");
        var secrets = new MemorySecretStore();
        var firstHarness = CreateHarness(
            transport,
            identities: new FileTopicSendIdentityStore(journalRoot),
            stateRoot: stateRoot,
            secrets: secrets);
        var identityId = firstHarness.State.ActiveAccountId
                         ?? throw new InvalidOperationException("Test identity was not created.");
        await using var firstRenderer = new ComponentRenderer(firstHarness.Services);
        var first = await firstRenderer.MountAsync<MobileMe>();
        await firstRenderer.InputAsync(first.Id, "Message your assistant", "survive clear failure");
        await WaitUntilAsync(
            () => ReadDurableTopicDraft(stateRoot, secrets, identityId, "thread")
                  == "survive clear failure",
            TimeSpan.FromSeconds(5));
        await firstRenderer.ClickAsync(first.Id, "Send");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(stateRoot));
        Assert.IsTrue(secondState.SwitchAccount(identityId));
        using var blocker = OpenDatabaseBlocker(stateRoot, secrets, identityId);
        using var transaction = blocker.BeginTransaction(deferred: false);
        transport.Release.TrySetResult();
        await WaitUntilAsync(
            () => firstRenderer.RenderedText(first.Id).Contains(
                "draft cleanup failed", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));
        await firstRenderer.UnmountAsync(first.Id);

        var secondHarness = CreateHarness(
            transport,
            identities: new FileTopicSendIdentityStore(journalRoot),
            state: secondState,
            stateRoot: stateRoot,
            secrets: secrets);
        Assert.AreEqual("survive clear failure", secondHarness.State.GetTopicDraft("thread"));
        await using (var secondRenderer = new ComponentRenderer(secondHarness.Services))
        {
            var second = await secondRenderer.MountAsync<MobileMe>();
            await Task.Delay(200);
            Assert.AreEqual(1, transport.SubmitCount);
            Assert.IsTrue(new FileTopicSendIdentityStore(journalRoot).TryGetUnresolved(
                StableId(
                    "scope",
                    string.Join(
                        "\0",
                        "topic-send-v3",
                        "thread",
                        transport.Device.DeviceId)),
                out var pending));
            Assert.AreEqual(
                TopicSendJournalCleanup.DraftClearPending,
                pending!.Cleanup);
            await secondRenderer.UnmountAsync(second.Id);
        }

        transaction.Commit();
        var thirdState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(stateRoot));
        Assert.IsTrue(thirdState.SwitchAccount(identityId));
        var thirdIdentities = new FileTopicSendIdentityStore(journalRoot);
        var thirdHarness = CreateHarness(
            transport,
            identities: thirdIdentities,
            state: thirdState,
            stateRoot: stateRoot,
            secrets: secrets);
        await using var thirdRenderer = new ComponentRenderer(thirdHarness.Services);
        var third = await thirdRenderer.MountAsync<MobileMe>();
        await thirdIdentities.WaitUntilAsync(
            () => !thirdIdentities.TryGetUnresolved(
                StableId(
                    "scope",
                    string.Join(
                        "\0",
                        "topic-send-v3",
                        "thread",
                        transport.Device.DeviceId)),
                out _),
            TimeSpan.FromSeconds(15));
        var verifiedState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(stateRoot));
        Assert.IsTrue(verifiedState.SwitchAccount(identityId));
        var verifiedHarness = CreateHarness(
            transport,
            identities: new FileTopicSendIdentityStore(journalRoot),
            state: verifiedState,
            stateRoot: stateRoot,
            secrets: secrets);
        Assert.AreEqual("", verifiedHarness.State.GetTopicDraft("thread"));
        Assert.AreEqual(1, transport.SubmitCount);
        await thirdRenderer.UnmountAsync(third.Id);
    }

    [DataTestMethod]
    [DataRow(TopicSendJournalBoundary.BeforeWrite)]
    [DataRow(TopicSendJournalBoundary.AfterWrite)]
    public async Task RenderedComposer_CleanupWriteCrash_RecreateCompactsFinalRecord(
        TopicSendJournalBoundary boundary)
    {
        var transport = new ControllableDeviceTransport();
        var identities = new InMemoryTopicSendIdentityStore();
        var crash = new OneShotJournalCrash(
            "draft-clear-persisted", boundary);
        var firstHarness = CreateHarness(
            transport, identities: identities, faultInjector: crash);
        await using (var renderer = new ComponentRenderer(firstHarness.Services))
        {
            var rendered = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(rendered.Id, "Message your assistant", "cleanup crash");
            await renderer.ClickAsync(rendered.Id, "Send");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Release.TrySetResult();
            await crash.TriggeredTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("", firstHarness.State.GetTopicDraft("thread"));
            await renderer.UnmountAsync(rendered.Id);
        }

        var recoveredHarness = CreateHarness(
            transport, identities: identities, state: firstHarness.State);
        await using var recoveredRenderer = new ComponentRenderer(recoveredHarness.Services);
        var recovered = await recoveredRenderer.MountAsync<MobileMe>();
        await Task.Delay(100);
        Assert.AreEqual(1, transport.SubmitCount);
        await recoveredRenderer.UnmountAsync(recovered.Id);
    }

    [TestMethod]
    public async Task RenderedComposer_AcceptedUnknownRestart_ReconcilesWithoutSubmit()
    {
        var transport = new ControllableDeviceTransport();
        var identities = new InMemoryTopicSendIdentityStore();
        var crash = new OneShotJournalCrash(
            "accepted-or-unknown", TopicSendJournalBoundary.AfterWrite);
        var firstHarness = CreateHarness(
            transport, identities: identities, faultInjector: crash);
        await using (var renderer = new ComponentRenderer(firstHarness.Services))
        {
            var rendered = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(rendered.Id, "Message your assistant", "accepted restart");
            await renderer.ClickAsync(rendered.Id, "Send");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Release.TrySetResult();
            await crash.TriggeredTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, transport.SubmitCount);
            await renderer.UnmountAsync(rendered.Id);
        }

        var recoveredHarness = CreateHarness(
            transport,
            identities: identities,
            state: firstHarness.State,
            reconciliation: TopicSendReconciliationKind.Accepted);
        await using var recoveredRenderer = new ComponentRenderer(recoveredHarness.Services);
        var recovered = await recoveredRenderer.MountAsync<MobileMe>();
        await WaitUntilAsync(
            () => recoveredHarness.State.GetTopicDraft("thread") == "",
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, transport.SubmitCount);
        await recoveredRenderer.UnmountAsync(recovered.Id);
    }

    [TestMethod]
    public async Task RenderedComposer_NotFoundRestart_ClearsBeforeUnfencing()
    {
        var transport = new ControllableDeviceTransport();
        var identities = new InMemoryTopicSendIdentityStore();
        var crash = new OneShotJournalCrash(
            "accepted-or-unknown", TopicSendJournalBoundary.AfterWrite);
        var firstHarness = CreateHarness(
            transport, identities: identities, faultInjector: crash);
        await using (var renderer = new ComponentRenderer(firstHarness.Services))
        {
            var rendered = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(rendered.Id, "Message your assistant", "not found retry");
            await renderer.ClickAsync(rendered.Id, "Send");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Release.TrySetResult();
            await crash.TriggeredTask.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.UnmountAsync(rendered.Id);
        }

        var recoveredHarness = CreateHarness(
            transport,
            identities: identities,
            state: firstHarness.State,
            reconciliation: TopicSendReconciliationKind.NotFound);
        await using var recoveredRenderer = new ComponentRenderer(recoveredHarness.Services);
        var recovered = await recoveredRenderer.MountAsync<MobileMe>();
        await WaitUntilAsync(
            () => recoveredRenderer.MarkupContains(recovered.Id, "No durable handoff was found"),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => recoveredHarness.State.GetTopicDraft("thread") == "",
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, transport.SubmitCount);
        await recoveredRenderer.UnmountAsync(recovered.Id);
    }

    [TestMethod]
    public async Task RenderedComposer_MalformedLegacyJournal_ReconcilesBeforeRetry()
    {
        var transport = new ControllableDeviceTransport();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var scope = StableId(
            "scope",
            string.Join("\0", "topic-send-v3", "thread", transport.Device.DeviceId));
        values[$"mesh.ui.topic-send.v3.pending.{scope}"] = "{ malformed";
        var identities = new KeyValueTopicSendIdentityStore(
            key => values.GetValueOrDefault(key),
            (key, value) => values[key] = value,
            key => values.Remove(key));
        var migrationCheckpoint = new OneShotJournalCrash(
            "draft-clear-persisted",
            TopicSendJournalBoundary.AfterWrite);
        var harness = CreateHarness(
            transport,
            identities: identities,
            reconciliation: TopicSendReconciliationKind.NotFound,
            faultInjector: migrationCheckpoint);
        harness.State.SetTopicDraft("thread", "legacy retry");

        await using var renderer = new ComponentRenderer(harness.Services);
        var rendered = await renderer.MountAsync<MobileMe>();
        await WaitUntilAsync(
            () => renderer.MarkupContains(rendered.Id, "No durable handoff was found"),
            TimeSpan.FromSeconds(5));
        Assert.IsFalse(values.ContainsKey($"mesh.ui.topic-send.v4.pending.{scope}"));
        Assert.AreEqual("legacy retry", harness.State.GetTopicDraft("thread"));
        Assert.AreEqual(0, transport.SubmitCount);
        await renderer.UnmountAsync(rendered.Id);
    }

    [TestMethod]
    public async Task RenderedComposer_IdenticalRetypeUsesNewRevision()
    {
        var transport = new ControllableDeviceTransport();
        var harness = CreateHarness(transport);
        await using var renderer = new ComponentRenderer(harness.Services);
        var rendered = await renderer.MountAsync<MobileMe>();

        await renderer.InputAsync(rendered.Id, "Message your assistant", "same text");
        await renderer.ClickAsync(rendered.Id, "Send");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.InputAsync(rendered.Id, "Message your assistant", "same text changed");
        await renderer.InputAsync(rendered.Id, "Message your assistant", "same text");
        transport.Release.TrySetResult();
        await WaitUntilAsync(
            () => harness.Sends.CompletedIdentityCount == 1,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual("same text", harness.State.GetTopicDraft("thread"));

        await renderer.ClickAsync(rendered.Id, "Send");
        await WaitUntilAsync(() => transport.SubmitCount == 2, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => harness.State.GetTopicDraft("thread") == "",
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, transport.SubmitCount);
        await renderer.UnmountAsync(rendered.Id);
    }

    [TestMethod]
    public async Task FreshProcess_NewerCompleteSnapshotFlushesBeforeOldFenceSupersedes()
    {
        var transport = new ControllableDeviceTransport();
        var stateRoot = NewStateRoot();
        var secrets = new MemorySecretStore();
        var journalRoot = Path.Combine(stateRoot, "send-journal");
        var identities = new FileTopicSendIdentityStore(journalRoot);
        var scope = StableId(
            "scope",
            string.Join(
                "\0",
                "topic-send-v3",
                "thread",
                transport.Device.DeviceId));
        var firstHarness = CreateHarness(
            transport,
            identities: identities,
            stateRoot: stateRoot,
            secrets: secrets);
        var databaseRoot = stateRoot;
        var identityId = firstHarness.State.ActiveAccountId
                         ?? throw new InvalidOperationException("Test identity was not created.");
        var attachmentPath = Path.Combine(stateRoot, "revision-2.txt");
        await File.WriteAllTextAsync(attachmentPath, "revision two");
        var attachment = MeshDb.ComposerDraftAttachment.Create(
            "revision-2.txt",
            attachmentPath,
            new FileInfo(attachmentPath).Length);
        var newerSnapshot = new MeshDb.TopicComposerSnapshot(
            "identical",
            [attachment],
            false,
            "widget-r2",
            transport.Device.DeviceId,
            new MeshDb.ComposerDraftWidget(
                "widget-r2",
                "Revision two widget",
                "revision two",
                "<html><body>revision two</body></html>"));
        firstHarness.State.Profile.Widgets.Add(new Widget
        {
            Id = "widget-r2",
            Name = "Revision two widget",
            Prompt = "revision two",
            Html = "<html><body>revision two</body></html>"
        });
        firstHarness.State.Save();
        long submittedRevision;
        long newerRevision;
        try
        {
            await using var renderer = new ComponentRenderer(firstHarness.Services);
            var first = await renderer.MountAsync<MobileMe>();
            await renderer.InputAsync(first.Id, "Message your assistant", "identical");
            submittedRevision = firstHarness.State.GetTopicDraftState("thread")!.Revision;
            await firstHarness.State.FlushTopicDraftAsync(
                "thread",
                submittedRevision);
            await renderer.ClickAsync(first.Id, "Send");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var blocker = OpenDatabaseBlocker(databaseRoot, secrets, identityId);
            using var transaction = blocker.BeginTransaction(deferred: false);
            transport.Release.TrySetResult();
            await identities.WaitUntilAsync(
                () => identities.TryGetUnresolved(
                        scope,
                        out var pending)
                      && pending!.Cleanup
                      == TopicSendJournalCleanup.DraftClearPending,
                TimeSpan.FromSeconds(15));

            newerRevision = firstHarness.State.SetTopicDraftSnapshotRevision(
                "thread",
                newerSnapshot);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => firstHarness.State.FlushTopicDraftAsync(
                    "thread",
                    newerRevision));
            Assert.IsTrue(new FileTopicSendIdentityStore(journalRoot)
                .TryGetUnresolved(
                    scope,
                    out var stillPending));
            Assert.AreEqual(
                TopicSendJournalCleanup.DraftClearPending,
                stillPending!.Cleanup);
            transaction.Commit();
            firstHarness.State.RetryTopicDraft("thread");
            await firstHarness.State.FlushTopicDraftAsync(
                "thread",
                newerRevision);
            await renderer.UnmountAsync(first.Id);
        }
        finally
        {
            transport.Release.TrySetResult();
            await firstHarness.Services.DisposeAsync();
            await firstHarness.State.DisposeAsync();
        }

        await using (var replayedState = new AppState(
                         secrets,
                         storagePaths: StoragePaths.ForRoot(stateRoot)))
        {
            Assert.IsTrue(replayedState.SwitchAccount(identityId));
            var replayed = replayedState.GetTopicDraftState("thread");
            Assert.IsNotNull(replayed?.TopicSnapshot);
            Assert.AreEqual(newerRevision, replayed.Revision);
            Assert.AreEqual(newerSnapshot.Fingerprint, replayed.TopicSnapshot.Fingerprint);
        }

        var restartedState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(stateRoot));
        Assert.IsTrue(restartedState.SwitchAccount(identityId));
        var restartedStore = new FileTopicSendIdentityStore(journalRoot);
        Assert.IsTrue(restartedStore.TryGetUnresolved(scope, out var pendingCleanup));
        var restartedHarness = CreateHarness(
            transport,
            identities: restartedStore,
            state: restartedState,
            stateRoot: databaseRoot,
            secrets: secrets);
        try
        {
            var recoveredSubmission = restartedHarness.Sends.CreateSnapshot(
                "thread",
                transport.Device.DeviceId,
                pendingCleanup!.ComposerRevision,
                pendingCleanup.DraftFingerprint,
                DateTimeOffset.UtcNow,
                identityId);
            await restartedHarness.Sends.RequestReconciliationAsync(
                recoveredSubmission,
                draftCleanup: new TopicSendDraftCleanup(async _ =>
                {
                    var result = await restartedState.CompareAndClearTopicDraftAsync(
                        "thread",
                        pendingCleanup.ComposerRevision,
                        CancellationToken.None);
                    return result == MeshDb.ComposerDraftClearResult.Superseded
                        ? TopicSendDraftCleanupResult.DraftClearSuperseded
                        : TopicSendDraftCleanupResult.DraftClearPersisted;
                }));
            Assert.IsFalse(restartedStore.TryGetUnresolved(scope, out _));

            await using var restartedRenderer =
                new ComponentRenderer(restartedHarness.Services);
            var restarted = await restartedRenderer.MountAsync<MobileMe>();
            var restored = restartedHarness.State.GetTopicDraftState("thread");
            Assert.IsNotNull(restored?.TopicSnapshot);
            Assert.AreEqual(newerRevision, restored.Revision);
            Assert.AreEqual(
                newerSnapshot.Fingerprint,
                restored.TopicSnapshot.Fingerprint);
            Assert.AreEqual("identical", restored.Text);
            Assert.AreEqual("widget-r2", restored.TopicSnapshot.WidgetId);
            Assert.AreEqual(1, restored.TopicSnapshot.Attachments.Count);
            Assert.AreEqual(1, transport.SubmitCount);
            await WaitUntilAsync(
                () => restartedRenderer.AttributeValue(
                    restarted.Id,
                    "textarea",
                    "value") == "identical",
                TimeSpan.FromSeconds(5));

            await restartedRenderer.ClickAsync(restarted.Id, "Send");
            Assert.IsTrue(
                await WaitForAsync(
                    () => transport.SubmitCount == 2,
                    TimeSpan.FromSeconds(5)),
                restartedRenderer.RenderedText(restarted.Id));
            await restartedRenderer.Dispatcher.InvokeAsync(() => { });
            await restartedHarness.State.FlushPersistenceAsync();
            await WaitUntilAsync(
                () => restartedHarness.State.GetTopicDraft("thread") == "",
                TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, transport.SubmitCount);
            await restartedRenderer.UnmountAsync(restarted.Id);
        }
        finally
        {
            await restartedHarness.Services.DisposeAsync();
            await restartedHarness.State.DisposeAsync();
        }
    }

    [TestMethod]
    public void FreshGraphs_UseDistinctPhysicalDatabasePaths()
    {
        var firstRoot = NewStateRoot();
        var secondRoot = NewStateRoot();
        var first = CreateHarness(
            new ControllableDeviceTransport(),
            stateRoot: firstRoot,
            secrets: new MemorySecretStore());
        var second = CreateHarness(
            new ControllableDeviceTransport(),
            stateRoot: secondRoot,
            secrets: new MemorySecretStore());
        var firstPath = Path.Combine(
            firstRoot,
            "Data",
            $"identity-{first.State.ActiveAccountId}.meshdb");
        var secondPath = Path.Combine(
            secondRoot,
            "Data",
            $"identity-{second.State.ActiveAccountId}.meshdb");

        Assert.AreNotSame(first.State, second.State);
        Assert.AreNotSame(first.Services, second.Services);
        Assert.AreNotEqual(
            Path.GetFullPath(firstPath),
            Path.GetFullPath(secondPath));
        Assert.IsTrue(File.Exists(firstPath), firstPath);
        Assert.IsTrue(File.Exists(secondPath), secondPath);
    }

    [TestMethod]
    public async Task ProductionRouter_FirstRemoteRunCommitsBeforeTransportAndRestartDoesNotForwardTwice()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = new AppState(secrets, storagePaths: StoragePaths.ForRoot(root));
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            state.Profile.PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            state.Profile.PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        state.Profile.Handle = "owner";
        state.Profile.DeviceName = "Sender";
        state.Profile.Model.ApiKey = "configured";
        state.Profile.OwnThreads.Add(new OwnThread
        {
            Id = "first-run-thread",
            Title = "First run",
            ExecutionDeviceId = "remote-device",
            ExecutionDeviceName = "Remote",
            ExecutionDevicePlatform = DevicePlatforms.Windows
        });
        state.Save();
        var accountId = state.ActiveAccountId
                        ?? throw new InvalidOperationException("Identity was not created.");
        var counter = new DurableTransportCounter();
        var transport = new DurableObservingTransport(state, counter);
        var router = new TopicExecutionRouter(state, new NoopTurnRunner(), transport);
        var at = DateTimeOffset.UtcNow;
        var draft = new TopicTurnDraft(
            "first-run-id",
            "first-run-thread",
            "first-run-line",
            "owner",
            "private first prompt",
            at,
            TopicTurnMode.Single,
            "remote-device");

        var first = await router.SubmitAsync(draft, null, CancellationToken.None);

        Assert.IsTrue(first.Accepted);
        Assert.IsTrue(first.Durable);
        Assert.AreEqual(1, counter.ForwardCount);
        Assert.AreEqual(1, counter.ObservedCorrelatedCount);
        Assert.IsNotNull(state.GetTopicOutbox(draft.RunId));
        Assert.AreEqual(
            1,
            state.Profile.OwnThreads.Single(thread => thread.Id == draft.ThreadId)
                .Lines.Count(line => line.Id == draft.TriggerLineId));
        await state.FlushPersistenceAsync();
        await state.DisposeAsync();

        var restartedState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(root));
        Assert.IsTrue(restartedState.SwitchAccount(accountId));
        var restartedRouter = new TopicExecutionRouter(
            restartedState,
            new NoopTurnRunner(),
            new DurableObservingTransport(restartedState, counter));

        var retry = await restartedRouter.SubmitAsync(
            draft, null, CancellationToken.None);

        Assert.IsTrue(retry.Accepted);
        Assert.IsTrue(retry.Durable);
        Assert.AreEqual(1, counter.ForwardCount);
        Assert.AreEqual(
            1,
            restartedState.Profile.OwnThreads
                .Single(thread => thread.Id == draft.ThreadId)
                .Lines.Count(line => line.Id == draft.TriggerLineId));
        await restartedState.DisposeAsync();
    }

    [TestMethod]
    public void ProductionAppState_PostCommitProjectionFailureIsDurableAndRestartRecoverable()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "projection-thread");
        var accountId = state.ActiveAccountId!;
        var at = DateTimeOffset.UtcNow;
        var command = CreateRemoteBeginCommand(
            "projection-authority",
            "projection-thread",
            "projection-line",
            at);
        AppState.TopicProjectionCheckpointHook = checkpoint =>
        {
            if (checkpoint != TopicProjectionCheckpoint.AfterCommitBeforeProjection) return;
            state.Profile.OwnThreads.Clear();
            throw new InjectedProjectionFailure();
        };
        TopicRunBeginResult committed;
        try
        {
            committed = state.BeginTopicRun(command);
        }
        finally
        {
            AppState.TopicProjectionCheckpointHook = null;
        }

        Assert.IsTrue(committed.DurableCommitted);
        Assert.IsTrue(committed.Created);
        Assert.IsTrue(committed.ProjectionDeferred);
        Assert.AreEqual("projection_deferred", committed.ProjectionError);
        Assert.IsNotNull(state.GetTopicOutbox(command.Draft.RunId));

        state.SignOut();
        var restarted = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(root));
        Assert.IsTrue(restarted.SwitchAccount(accountId));
        var retry = CreateRemoteBeginCommand(
            "projection-new-proposal",
            command.Draft.ThreadId,
            command.Draft.TriggerLineId,
            at);
        var recovered = restarted.BeginTopicRun(retry);

        Assert.IsTrue(recovered.DurableCommitted);
        Assert.IsFalse(recovered.Created);
        Assert.IsTrue(recovered.ProjectionApplied);
        Assert.AreEqual(command.Draft.RunId, recovered.AuthoritativeRunId);
        Assert.HasCount(1, restarted.ListTopicOutbox());
        Assert.AreEqual(
            1,
            restarted.Profile.OwnThreads.Single(thread => thread.Id == command.Draft.ThreadId)
                .Lines.Count(line => line.Id == command.Draft.TriggerLineId));
    }

    [TestMethod]
    public async Task ProductionRouter_FailureAfterProjectionDoesNotLoseDurableOutbox()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = CreateFirstRunState(root, secrets, "pretransport-thread");
        var accountId = state.ActiveAccountId!;
        var at = DateTimeOffset.UtcNow;
        var draft = new TopicTurnDraft(
            "pretransport-authority",
            "pretransport-thread",
            "pretransport-line",
            "owner",
            "private first prompt",
            at,
            TopicTurnMode.Single,
            "remote-device");
        var counter = new DurableTransportCounter();
        var transport = new DurableObservingTransport(state, counter);
        TopicExecutionRouter.BeforeTransportCheckpointHook = _ =>
            throw new InjectedProjectionFailure();
        TopicDispatchResult failed;
        try
        {
            failed = await new TopicExecutionRouter(
                    state, new NoopTurnRunner(), transport)
                .SubmitAsync(draft, null, CancellationToken.None);
        }
        finally
        {
            TopicExecutionRouter.BeforeTransportCheckpointHook = null;
        }

        Assert.IsFalse(failed.Accepted);
        Assert.IsTrue(failed.Durable);
        Assert.AreEqual(0, counter.ForwardCount);
        Assert.IsNotNull(state.GetTopicOutbox(draft.RunId));

        state.SignOut();
        var restarted = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(root));
        Assert.IsTrue(restarted.SwitchAccount(accountId));
        var retry = draft with { RunId = "pretransport-new-proposal" };
        var recovered = await new TopicExecutionRouter(
                restarted,
                new NoopTurnRunner(),
                new DurableObservingTransport(restarted, counter))
            .SubmitAsync(retry, null, CancellationToken.None);

        Assert.IsTrue(recovered.Accepted, recovered.Error);
        Assert.AreEqual(draft.RunId, recovered.RunId);
        Assert.AreEqual(1, counter.ForwardCount);
        Assert.IsNull(restarted.GetTopicOutbox(retry.RunId));
    }

    [TestMethod]
    public async Task ProductionRouter_FirstLocalRunUsesDurableLocalAuthorityWithoutCorrelation()
    {
        var root = NewStateRoot();
        var secrets = new MemorySecretStore();
        var state = new AppState(secrets, storagePaths: StoragePaths.ForRoot(root));
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            state.Profile.PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            state.Profile.PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        state.Profile.Handle = "owner";
        state.Profile.DeviceName = "Local";
        state.Profile.Model.ApiKey = "configured";
        var localDeviceId = DeviceProtocol.DeviceId(state.Profile.PublicKey);
        state.Profile.OwnThreads.Add(new OwnThread
        {
            Id = "first-local-thread",
            Title = "First local",
            ExecutionDeviceId = localDeviceId,
            ExecutionDeviceName = "Local",
            ExecutionDevicePlatform = DevicePlatforms.Windows
        });
        state.Save();
        var accountId = state.ActiveAccountId
                        ?? throw new InvalidOperationException("Identity was not created.");
        var runner = new RecordingLocalRunner();
        var router = new TopicExecutionRouter(
            state, runner, new LocalOnlyDeviceTransport());
        var draft = new TopicTurnDraft(
            "first-local-run",
            "first-local-thread",
            "first-local-line",
            "owner",
            "private local prompt",
            DateTimeOffset.UtcNow,
            TopicTurnMode.Single,
            localDeviceId);

        var first = await router.SubmitAsync(draft, null, CancellationToken.None);
        await runner.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(first.Accepted);
        Assert.IsTrue(first.Durable);
        Assert.AreEqual(1, runner.ExecuteCount);
        Assert.IsNull(state.GetTopicOutbox(draft.RunId));
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.NotCorrelated,
            state.ApplyRemoteTopicUpdate(
                new TopicRunUpdatePayload(
                    draft.RunId,
                    draft.ThreadId,
                    TopicRunPhase.Queued,
                    Timestamp: DateTimeOffset.UtcNow),
                localDeviceId));
        await state.FlushPersistenceAsync();
        await state.DisposeAsync();

        var restartedState = new AppState(
            secrets,
            storagePaths: StoragePaths.ForRoot(root));
        Assert.IsTrue(restartedState.SwitchAccount(accountId));
        var restartedRunner = new RecordingLocalRunner();
        var retry = await new TopicExecutionRouter(
                restartedState,
                restartedRunner,
                new LocalOnlyDeviceTransport())
            .SubmitAsync(draft, null, CancellationToken.None);

        Assert.IsTrue(retry.Accepted);
        Assert.IsTrue(retry.Durable);
        Assert.AreEqual("already_completed", retry.Code);
        Assert.AreEqual(0, restartedRunner.ExecuteCount);
        Assert.IsNull(restartedState.GetTopicOutbox(draft.RunId));
        await restartedState.DisposeAsync();
    }

    private static ComponentHarness CreateHarness(
        ControllableDeviceTransport transport,
        int identitySaveFailures = 0,
        ITopicSendIdentityStore? identities = null,
        AppState? state = null,
        TopicSendReconciliationKind? reconciliation = null,
        ITopicSendJournalFaultInjector? faultInjector = null,
        string? stateRoot = null,
        MemorySecretStore? secrets = null,
        ITopicSendObserverDispatcherFactory? observerDispatcherFactory = null,
        ITopicExecutionRouter? router = null,
        bool initializeState = true)
    {
        stateRoot ??= NewStateRoot();
        var storagePaths = StoragePaths.ForRoot(stateRoot);
        state ??= new AppState(
            secrets ?? new MemorySecretStore(),
            storagePaths: storagePaths);
        if (initializeState && state.Profile.OwnThreads.Count == 0)
        {
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            state.Profile.PrivateKey =
                Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            state.Profile.PublicKey =
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        state.Profile.Handle = "owner";
        state.Profile.DeviceName = "Component Test";
        state.Profile.Model.ApiKey = "configured";
        state.Profile.OwnThreads.Add(new OwnThread
        {
            Id = "thread",
            Title = "Topic",
            ExecutionDeviceId = transport.Device.DeviceId,
            ExecutionDeviceName = transport.Device.Name,
            ExecutionDevicePlatform = transport.Device.Platform
        });
        state.Save();
        }

        var lifecycle = new TestLifecycle();
        var http = new TestHttpClientFactory();
        var runner = new NoopTurnRunner();
        var meter = new TokenMeter(state);
        var media = new AgentMedia();
        var memory = new MemoryService(state);
        var factory = new ModelFactory(http, state, meter, new BrowserModelService(), null!);
        var tools = new ToolRegistry(
            null!, null!, null!, http, null!, new LocalFileRegistry(),
            null!, media, null!, state, null!);
        var agent = new AgentService(
            state,
            factory,
            new FoundryLocalService(http),
            tools,
            meter,
            media,
            memory,
            new EmptyBuiltIns());
        var mesh = new MeshClient(
            state,
            agent,
            runner,
            http,
            new NoopPushService(),
            lifecycle,
            topicEnvelopeTransport: new NoopEnvelopeTransport());
        transport.State = state;
        ITopicExecutionRouter routed = router ?? transport;

        identities ??= identitySaveFailures == 0
            ? new InMemoryTopicSendIdentityStore()
            : new FailingTopicSendIdentityStore(identitySaveFailures);
        ITopicSendReconciliationQuery query = reconciliation is null
            ? new AppStateTopicSendReconciliationQuery(state)
            : new DelegateTopicSendReconciliationQuery((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    new TopicSendReconciliationResult(reconciliation.Value));
            });
        var sends = new TopicSendCoordinator(
            new TopicSendRetentionOptions
            {
                ReconciliationInitialBackoff = TimeSpan.FromMilliseconds(5),
                ReconciliationMaximumBackoff = TimeSpan.FromMilliseconds(20)
            },
            identityStore: identities,
            reconciliationQuery: query,
            journalFaultInjector: faultInjector);

        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton(agent);
        services.AddSingleton(mesh);
        services.AddSingleton<ITopicExecutionRouter>(routed);
        services.AddSingleton(new UiOperationCoordinator());
        services.AddSingleton(sends);
        services.AddSingleton(new ComposerRevisionGuard());
        services.AddSingleton(new LocalFileRegistry());
        services.AddSingleton<IMessageClipboard>(new MessageClipboard(_ => Task.CompletedTask));
        services.AddSingleton<IJSRuntime>(new NoopJsRuntime());
        services.AddSingleton<NavigationManager>(
            new TestNavigationManager("https://mesh.test/m/me?thread=thread"));
        services.AddSingleton<IAppLifecycleState>(lifecycle);
        services.AddSingleton(new NotificationViewState(lifecycle));
        services.AddSingleton(new MobileOverlayState());
        if (observerDispatcherFactory is not null)
            services.AddSingleton(observerDispatcherFactory);
        services.AddLogging();
        return new(services.BuildServiceProvider(), state, sends);
    }

    private static AppState CreateFirstRunState(
        string root,
        MemorySecretStore secrets,
        string threadId)
    {
        var state = new AppState(secrets, storagePaths: StoragePaths.ForRoot(root));
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            state.Profile.PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            state.Profile.PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        state.Profile.Handle = "owner";
        state.Profile.DeviceName = "Sender";
        state.Profile.Model.ApiKey = "configured";
        state.Profile.OwnThreads.Add(new OwnThread
        {
            Id = threadId,
            Title = "First run",
            ExecutionDeviceId = "remote-device",
            ExecutionDeviceName = "Remote",
            ExecutionDevicePlatform = DevicePlatforms.Windows
        });
        state.Save();
        return state;
    }

    private static MeshProfile CreateAccountProfile(string handle, string threadId)
    {
        var profile = new MeshProfile
        {
            Handle = handle,
            DisplayName = handle,
            DeviceName = "Alternate"
        };
        profile.Model.ApiKey = "configured";
        profile.OwnThreads.Add(new OwnThread
        {
            Id = threadId,
            Title = "Alternate",
            ExecutionDeviceId = "remote-device",
            ExecutionDeviceName = "Remote",
            ExecutionDevicePlatform = DevicePlatforms.Windows
        });
        return profile;
    }

    private static TopicRunBeginCommand CreateRemoteBeginCommand(
        string runId,
        string threadId,
        string triggerLineId,
        DateTimeOffset at,
        string? operationId = null)
    {
        var draft = new TopicTurnDraft(
            runId,
            threadId,
            triggerLineId,
            "owner",
            "private first prompt",
            at,
            TopicTurnMode.Single,
            "remote-device",
            TriggerOperationId: operationId);
        return new TopicRunBeginCommand(
            draft,
            new ExecutionDevice("remote-device", "Remote", DevicePlatforms.Windows),
            TopicRunBeginMode.Remote,
            new TopicRunUpdatePayload(
                runId,
                threadId,
                TopicRunPhase.Queued,
                "Queued",
                Timestamp: at,
                TriggerLineId: triggerLineId),
            new TopicRunRequestPayload(
                runId,
                threadId,
                triggerLineId,
                draft.TriggerHandle,
                draft.Prompt,
                at,
                "remote-device",
                TopicTurnMode.Single),
            []);
    }

    private static async Task<TopicSendSnapshot> PrepareAtomicRetryAsync(
        TopicSendCoordinator sends,
        string accountId)
    {
        var snapshot = sends.CreateSnapshot(
            "thread",
            "remote-device",
            1,
            $"atomic-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            accountId);
        sends.Submit(
            snapshot,
            _ => Task.FromException<TopicSendHandoff>(
                new IOException("fail before durable begin")));
        await WaitUntilAsync(
            () => sends.TryGetOutcome(snapshot.OperationId, out var outcome)
                  && outcome?.Kind == TopicSendOutcomeKind.RetryableFailed,
            TimeSpan.FromSeconds(5));
        return snapshot;
    }

    private static TopicSendSubmissionResult SubmitAtomicRetry(
        TopicSendCoordinator sends,
        AppState state,
        TopicSendSnapshot snapshot,
        TaskCompletionSource entered,
        TaskCompletionSource release)
        => sends.Submit(
            snapshot,
            async (_, context) =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                var command = CreateAtomicBeginCommand(snapshot);
                var begin = context.BeginTopicRun(
                    command,
                    () => state.BeginTopicRun(command));
                if (begin.DurableCommitted)
                    context.MarkDurableBoundaryEntered();
                return new TopicSendHandoff(begin.DurableCommitted, begin.Code);
            });

    private static TopicRunBeginCommand CreateAtomicBeginCommand(
        TopicSendSnapshot snapshot)
        => CreateRemoteBeginCommand(
            snapshot.RunId,
            snapshot.ThreadId,
            snapshot.LineId,
            snapshot.SubmittedAt,
            snapshot.OperationId);

    private static string NewStateRoot()
        => Path.Combine(
            FindRepositoryRoot(),
            "_artifacts",
            "clarissa-ui",
            "component-state",
            Guid.NewGuid().ToString("n"));

    private static SqliteConnection OpenDatabaseBlocker(
        string stateRoot,
        MemorySecretStore secrets,
        string identityId)
    {
        var databasePath = Path.Combine(
            stateRoot, "Data", $"identity-{identityId}.meshdb");
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        var key = secrets.GetDbKey(identityId)
                  ?? throw new InvalidOperationException("Database key is missing.");
        using (var keyCommand = connection.CreateCommand())
        {
            keyCommand.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
            keyCommand.ExecuteNonQuery();
        }
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 0; PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private static string ReadDurableTopicDraft(
        string stateRoot,
        MemorySecretStore secrets,
        string identityId,
        string threadId)
    {
        if (!File.Exists(Path.Combine(
                stateRoot,
                "Data",
                $"identity-{identityId}.meshdb")))
            return "";

        using var connection = OpenDatabaseBlocker(stateRoot, secrets, identityId);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT text FROM composer_drafts WHERE kind = 'topic' AND entity_id = $id;";
        command.Parameters.AddWithValue("$id", threadId);
        return command.ExecuteScalar() as string ?? "";
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stop = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && stop.Elapsed < timeout)
            await Task.Delay(10);
        Assert.IsTrue(predicate());
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stop = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && stop.Elapsed < timeout)
            await Task.Delay(10);
        return predicate();
    }

    private static string ComponentLifecycleId(IComponent component)
        => (string)(component.GetType().GetField(
                "componentLifecycleId",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(component)
            ?? throw new InvalidOperationException("Component lifecycle id was not initialized."));

    private static ITopicSendObserverSubscription ObserverSubscription(
        IComponent component,
        string operationId)
    {
        var subscriptions = component.GetType().GetField(
                "sendObserverSubscriptions",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(component) as IReadOnlyDictionary<string, ITopicSendObserverSubscription>;
        Assert.IsNotNull(subscriptions);
        Assert.IsTrue(subscriptions.TryGetValue(operationId, out var subscription));
        Assert.IsNotNull(subscription);
        return subscription;
    }

    private static Task? ComponentDisposalTask(IComponent component)
        => component.GetType().GetField(
                "disposalTask",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(component) as Task;

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection(WeakReference reference)
    {
        for (var attempt = 0; attempt < 5 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "Mesh.App")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string StableId(string kind, string identity)
    {
        var hash = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}\0{identity}"));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private readonly record struct MountedComponent<T>(int Id, T Component)
        where T : IComponent;

    private sealed record DisposedComposerEvidence(
        WeakReference ComponentReference,
        TopicSendSnapshot Submission,
        Task ObserverDetached);

    private sealed class UiOperationCompletionProbe : IDisposable
    {
        private readonly UiOperationCoordinator operations;
        private readonly System.Threading.Channels.Channel<(string Key, string Operation)> events =
            System.Threading.Channels.Channel.CreateUnbounded<(string, string)>();

        public UiOperationCompletionProbe(UiOperationCoordinator operations)
        {
            this.operations = operations;
            operations.OperationCompleted += OnCompleted;
        }

        public async Task WaitAsync(string operation, string lifecycleId)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var completed = await events.Reader.ReadAsync(cancellation.Token);
                if (string.Equals(completed.Operation, operation, StringComparison.Ordinal)
                    && completed.Key.Contains(lifecycleId, StringComparison.Ordinal)
                    && !operations.IsRunning(completed.Key))
                    return;
            }
        }

        private void OnCompleted(string key, string operation)
            => events.Writer.TryWrite((key, operation));

        public void Dispose()
            => operations.OperationCompleted -= OnCompleted;
    }

    private sealed class ComponentRenderer(IServiceProvider services)
        : Renderer(services, NullLoggerFactory.Instance), IAsyncDisposable
    {
        private readonly object renderSignalGate = new();
        private TaskCompletionSource renderChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Microsoft.AspNetCore.Components.Dispatcher Dispatcher { get; } =
            Microsoft.AspNetCore.Components.Dispatcher.CreateDefault();

        public Task<MountedComponent<T>> MountAsync<T>() where T : IComponent
            => MountAsync<T>(ParameterView.Empty);

        public Task<MountedComponent<T>> MountAsync<T>(ParameterView parameters)
            where T : IComponent
            => Dispatcher.InvokeAsync(async () =>
            {
                var component = (T)InstantiateComponent(typeof(T));
                var id = AssignRootComponentId(component);
                await RenderRootComponentAsync(id, parameters);
                return new MountedComponent<T>(id, component);
            });

        public Task UnmountAsync(int componentId)
            => Dispatcher.InvokeAsync(() => RemoveRootComponent(componentId));

        public Task InputAsync(int componentId, string ariaLabel, string value)
            => Dispatcher.InvokeAsync(async () =>
            {
                var callback = Attribute(
                    componentId, "textarea", "aria-label", ariaLabel, "oninput");
                await ((EventCallback)callback!).InvokeAsync(
                    new ChangeEventArgs { Value = value });
            });

        public async Task ClickAsync(int componentId, string ariaLabel)
        {
            await WaitUntilAsync(
                () => HasAttribute(
                    componentId,
                    "button",
                    "aria-label",
                    ariaLabel,
                    "onclick"),
                TimeSpan.FromSeconds(5));
            await Dispatcher.InvokeAsync(async () =>
            {
                var callback = Attribute(
                    componentId, "button", "aria-label", ariaLabel, "onclick");
                if (callback is EventCallback eventCallback)
                    await eventCallback.InvokeAsync();
                else if (callback is Func<Task> action)
                    await action();
                else
                    Assert.Fail($"Unsupported click callback type {callback?.GetType()}.");
            });
        }

        public string? AttributeValue(
            int componentId,
            string element,
            string attribute)
            => Dispatcher.InvokeAsync(() =>
                    Attribute(componentId, element, null, null, attribute)?.ToString())
                .GetAwaiter()
                .GetResult();

        public bool MarkupContains(int componentId, string value)
            => Dispatcher.InvokeAsync(() =>
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var i = 0; i < frames.Count; i++)
                {
                    var frame = frames.Array[i];
                    if (frame.FrameType == RenderTreeFrameType.Text
                        && frame.TextContent.Contains(value, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }).GetAwaiter().GetResult();

        public string RenderedText(int componentId)
            => Dispatcher.InvokeAsync(() =>
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                return string.Join(
                    " ",
                    Enumerable.Range(0, frames.Count)
                        .Select(index => frames.Array[index])
                        .Where(frame => frame.FrameType == RenderTreeFrameType.Text)
                        .Select(frame => frame.TextContent.Trim())
                        .Where(text => text.Length > 0));
            }).GetAwaiter().GetResult();

        public async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                Task changed;
                lock (renderSignalGate) changed = renderChanged.Task;
                if (predicate()) return;
                lock (renderSignalGate)
                    if (!ReferenceEquals(changed, renderChanged.Task)) continue;
                await changed.WaitAsync(cancellation.Token);
            }
        }

        private object? Attribute(
            int componentId,
            string element,
            string? matchAttribute,
            string? matchValue,
            string wantedAttribute,
            bool required = true)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Element
                    || !string.Equals(frame.ElementName, element, StringComparison.Ordinal))
                    continue;
                object? wanted = null;
                var matched = matchAttribute is null;
                for (var j = i + 1;
                     j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute;
                     j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == matchAttribute
                        && string.Equals(
                            attribute.AttributeValue?.ToString(),
                            matchValue,
                            StringComparison.OrdinalIgnoreCase))
                        matched = true;
                    if (attribute.AttributeName == wantedAttribute)
                        wanted = attribute.AttributeValue;
                }
                if (matched) return wanted;
            }
            if (required)
                Assert.Fail($"{element}[{matchAttribute}={matchValue}] attribute {wantedAttribute} was not rendered.");
            return null;
        }

        private bool HasAttribute(
            int componentId,
            string element,
            string? matchAttribute,
            string? matchValue,
            string wantedAttribute)
            => Dispatcher.InvokeAsync(
                    () => Attribute(
                        componentId,
                        element,
                        matchAttribute,
                        matchValue,
                        wantedAttribute,
                        required: false) is not null)
                .GetAwaiter()
                .GetResult();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            TaskCompletionSource completed;
            lock (renderSignalGate)
            {
                completed = renderChanged;
                renderChanged = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            completed.TrySetResult();
            return Task.CompletedTask;
        }

        protected override void HandleException(Exception exception)
            => throw exception;

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await Dispatcher.InvokeAsync(Dispose);
        }
    }

    private sealed class BlockingAppStateReconciliationQuery
        : ITopicSendReconciliationQuery,
          ITopicSendReconciliationAvailability,
          ITopicSendAuthorizationAuthority,
          IDisposable
    {
        private readonly AppStateTopicSendReconciliationQuery inner;
        private TaskCompletionSource? release;
        private int calls;

        public BlockingAppStateReconciliationQuery(AppState state)
        {
            inner = new(state);
            inner.AvailabilityChanged += ForwardAvailability;
        }

        public event Action? AvailabilityChanged;
        public TaskCompletionSource Blocked { get; private set; } = NewSignal();
        public int Calls => Volatile.Read(ref calls);

        public void BlockNext()
        {
            Blocked = NewSignal();
            release = NewSignal();
        }

        public void Release()
            => release?.TrySetResult();

        public async ValueTask<TopicSendReconciliationResult> QueryAsync(
            TopicSendSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var gate = Volatile.Read(ref release);
            if (gate is not null)
            {
                Blocked.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
                Interlocked.CompareExchange(ref release, null, gate);
            }
            return await inner.QueryAsync(snapshot, cancellationToken);
        }

        public bool TryConsume(
            TopicSendAuthorizationScope scope,
            Func<bool> consume)
            => inner.TryConsume(scope, consume);

        public bool IsCurrent(TopicSendAuthorizationScope scope)
            => inner.IsCurrent(scope);

        public void Dispose()
            => inner.AvailabilityChanged -= ForwardAvailability;

        private void ForwardAvailability()
            => AvailabilityChanged?.Invoke();
    }

    private sealed record ComponentHarness(
        ServiceProvider Services,
        AppState State,
        TopicSendCoordinator Sends);

    private sealed class BlockingObserverDispatcherFactory
        : ITopicSendObserverDispatcherFactory
    {
        private int blocked;
        public TaskCompletionSource Queued { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public TaskCompletionSource Completed { get; } = NewSignal();
        public Func<Task>? QueuedWork { get; private set; }

        public ITopicSendObserverDispatcher Create(
            Microsoft.AspNetCore.Components.Dispatcher dispatcher)
            => new BlockingObserverDispatcher(this, dispatcher);

        private sealed class BlockingObserverDispatcher(
            BlockingObserverDispatcherFactory owner,
            Microsoft.AspNetCore.Components.Dispatcher dispatcher)
            : ITopicSendObserverDispatcher
        {
            public async Task InvokeAsync(Func<Task> workItem)
            {
                if (Interlocked.Exchange(ref owner.blocked, 1) == 0)
                {
                    owner.QueuedWork = workItem;
                    owner.Queued.TrySetResult();
                    await owner.Release.Task;
                    workItem = owner.QueuedWork
                        ?? throw new InvalidOperationException("Queued renderer work was lost.");
                    owner.QueuedWork = null;
                }

                try
                {
                    await dispatcher.InvokeAsync(workItem);
                }
                finally
                {
                    owner.Completed.TrySetResult();
                }
            }
        }
    }

    private sealed class RendererObserverProbeControl(string completion)
    {
        public string Completion { get; } = completion;
        public TaskCompletionSource Attached { get; } = NewSignal();
        public TaskCompletionSource CallbackEntered { get; } = NewSignal();
        public TaskCompletionSource CallbackRelease { get; } = NewSignal();
        public TaskCompletionSource DisposeEntered { get; } = NewSignal();
        public TaskCompletionSource DisposeCompleted { get; } = NewSignal();
        public ITopicSendObserverSubscription? Subscription { get; set; }
        public int InvocationCount;
    }

    private sealed class RendererObserverProbe : ComponentBase, IAsyncDisposable
    {
        [Inject]
        private TopicSendCoordinator Coordinator { get; set; } = null!;

        [Inject]
        private RendererObserverProbeControl Control { get; set; } = null!;

        private readonly string observerId = Guid.NewGuid().ToString("n");

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RendererDispatcherCapture>(0);
            builder.AddComponentParameter(
                1,
                nameof(RendererDispatcherCapture.DispatcherCaptured),
                (Action<ITopicSendObserverDispatcher>)Attach);
            builder.CloseComponent();
        }

        private void Attach(ITopicSendObserverDispatcher dispatcher)
        {
            if (Control.Subscription is not null) return;
            var snapshot = Coordinator.TryGetSnapshot(
                "thread",
                "device",
                1,
                out var found)
                ? found
                : null;
            Control.Subscription = Coordinator.Observe(
                snapshot?.OperationId
                ?? throw new InvalidOperationException("Probe operation was not found."),
                observerId,
                dispatcher,
                OnOutcomeAsync);
            if (Control.Subscription is null)
                throw new InvalidOperationException("Probe observer could not attach.");
            Control.Attached.TrySetResult();
        }

        private async Task OnOutcomeAsync(TopicSendOutcome _)
        {
            Interlocked.Increment(ref Control.InvocationCount);
            Control.CallbackEntered.TrySetResult();
            await Control.CallbackRelease.Task;
            if (Control.Completion == "error")
                throw new InvalidOperationException("callback failure");
            if (Control.Completion == "cancellation")
                throw new OperationCanceledException("callback cancellation");
        }

        public async ValueTask DisposeAsync()
        {
            Control.DisposeEntered.TrySetResult();
            if (Control.Subscription is not null)
                await Control.Subscription.DisposeAsync();
            Control.DisposeCompleted.TrySetResult();
        }
    }

    private sealed class ControllableDeviceTransport : ITopicExecutionRouter
    {
        public AppState? State { private get; set; }
        public Mesh.Shared.DeviceInfo Device { get; } =
            new("remote-device", "Remote", true, DevicePlatforms.Windows, true);
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SubmitCount;
        public TopicTurnDraft? LastDraft { get; private set; }
        public TopicDispatchResult? ImmediateResult { get; set; }

        public async Task<TopicDispatchResult> SubmitAsync(
            TopicTurnDraft draft,
            IProgress<TopicRunUpdatePayload>? progress,
            CancellationToken cancellationToken,
            TopicSendHandoffContext? handoffContext = null)
        {
            Interlocked.Increment(ref SubmitCount);
            LastDraft = draft;
            if (ImmediateResult is not null)
                return ImmediateResult with { RunId = draft.RunId };
            if (!State!.Profile.OwnThreads[0].Lines.Any(line => line.Id == draft.TriggerLineId))
                State.AddOwnChatLine(draft.ThreadId, new ChatLine
                {
                    Id = draft.TriggerLineId,
                    Role = "user",
                    Text = draft.Prompt,
                    SenderHandle = draft.TriggerHandle,
                    At = draft.TriggerAt
                });
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            handoffContext?.AuthorizeDurableHandoff();
            return TopicDispatchResult.Ok(
                draft.RunId,
                TopicExecutionStatus.RelayAccepted,
                durable: true);
        }

        public Task<bool> CancelQueuedAsync(
            string threadId,
            string runId,
            string lineId,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> StopAsync(
            string threadId,
            string runId,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Mesh.Shared.DeviceInfo>>([Device]);
    }

    private sealed class DurableTransportCounter
    {
        public int ForwardCount;
        public int ObservedCorrelatedCount;
        public int ObservedOutboxCount;
        public int ObservedCorrelationCount;
        public int RequestEnvelopeCount;
        public int ExecutionCount;
        public int TerminalCount;
    }

    private sealed class InjectedProjectionFailure : Exception;

    private sealed class DurableObservingTransport(
        AppState state,
        DurableTransportCounter counter,
        AppState? receiverState = null,
        bool completeRemoteRun = false) : IDeviceTopicTransport
    {
        private readonly Mesh.Shared.DeviceInfo remote =
            new("remote-device", "Remote", true, DevicePlatforms.Windows, true);
        private readonly HashSet<string> observedOutboxes = new(StringComparer.Ordinal);
        private readonly HashSet<string> observedCorrelations = new(StringComparer.Ordinal);
        private readonly HashSet<string> requestEnvelopes = new(StringComparer.Ordinal);
        private readonly HashSet<string> executions = new(StringComparer.Ordinal);
        private readonly HashSet<string> terminals = new(StringComparer.Ordinal);

        public Task<TopicDispatchResult> DispatchAsync(
            string targetDeviceId,
            TopicRunRequestPayload request,
            IReadOnlyList<ChatAttachment> attachments,
            CancellationToken cancellationToken)
            => throw new AssertFailedException(
                "The router must dispatch the already-persisted outbox item.");

        public Task<TopicDispatchResult> DispatchPersistedAsync(
            MeshDb.TopicOutboxItem item,
            CancellationToken cancellationToken)
        {
            var persisted = state.GetTopicOutbox(item.RunId);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(item.EnvelopeId, persisted.EnvelopeId);
            if (observedOutboxes.Add(item.RunId))
                Interlocked.Increment(ref counter.ObservedOutboxCount);
            if (requestEnvelopes.Add(item.EnvelopeId))
                Interlocked.Increment(ref counter.RequestEnvelopeCount);
            if (!TopicOutboxStates.NeedsRemoteAcceptance(persisted.State))
                return Task.FromResult(TopicDispatchResult.Ok(
                    item.RunId, "local_pending", durable: true));

            Interlocked.Increment(ref counter.ForwardCount);
            var acceptance = TopicAcceptancePolicy.Create(
                item.Request, DateTimeOffset.UtcNow);
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                state.ApplyRemoteTopicUpdate(acceptance, item.TargetDeviceId));
            Interlocked.Increment(ref counter.ObservedCorrelatedCount);
            if (completeRemoteRun)
            {
                Assert.IsNotNull(receiverState);
                var durability = new TopicDurabilityHandler(
                    receiverState,
                    TimeProvider.System);
                _ = durability.AcceptRequest(item.Request, "sender-device");
                if (executions.Add(item.RunId))
                    Interlocked.Increment(ref counter.ExecutionCount);
                var terminal = new TopicRunUpdatePayload(
                    item.RunId,
                    item.ThreadId,
                    TopicRunPhase.Completed,
                    Timestamp: DateTimeOffset.UtcNow,
                    TriggerLineId: item.Request.TriggerLineId);
                _ = durability.CompleteRun(
                    item.RunId,
                    InboundTopicRunStates.Completed,
                    terminal,
                    "sender-device");
                Assert.AreEqual(
                    RemoteTopicUpdatePersistenceResult.Applied,
                    state.ApplyRemoteTopicUpdate(terminal, item.TargetDeviceId));
                if (state.IsRetainedTopicRunCorrelation(
                        item.RunId,
                        item.ThreadId,
                        item.TargetDeviceId)
                    && observedCorrelations.Add(item.RunId))
                    Interlocked.Increment(ref counter.ObservedCorrelationCount);
                if (terminals.Add(item.RunId))
                    Interlocked.Increment(ref counter.TerminalCount);
            }
            return Task.FromResult(TopicDispatchResult.Ok(
                item.RunId, TopicExecutionStatus.RelayAccepted, durable: true));
        }

        public Task<bool> CancelAsync(
            string targetDeviceId,
            TopicRunCancelPayload cancel,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Mesh.Shared.DeviceInfo>>([remote]);
    }

    private sealed class RecordingLocalRunner : ITopicTurnRunner
    {
        public int ExecuteCount;
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TopicRunCompletion> ExecuteAsync(
            TopicTurnDraft draft,
            IProgress<TopicRunUpdatePayload> progress,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? onStarted = null)
        {
            Interlocked.Increment(ref ExecuteCount);
            var at = DateTimeOffset.UtcNow;
            progress.Report(new TopicRunUpdatePayload(
                draft.RunId,
                draft.ThreadId,
                TopicRunPhase.Completed,
                Timestamp: at));
            Completed.TrySetResult();
            return Task.FromResult(new TopicRunCompletion(
                draft.RunId,
                draft.ThreadId,
                TopicRunPhase.Completed,
                at));
        }
    }

    private sealed class LocalOnlyDeviceTransport : IDeviceTopicTransport
    {
        public Task<TopicDispatchResult> DispatchAsync(
            string targetDeviceId,
            TopicRunRequestPayload request,
            IReadOnlyList<ChatAttachment> attachments,
            CancellationToken cancellationToken)
            => throw new AssertFailedException("A local run must not enter remote transport.");

        public Task<bool> CancelAsync(
            string targetDeviceId,
            TopicRunCancelPayload cancel,
            CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Mesh.Shared.DeviceInfo>>([]);
    }

    private sealed class FailingTopicSendIdentityStore(int saveFailures)
        : ITopicSendIdentityStore
    {
        private readonly InMemoryTopicSendIdentityStore inner = new();
        private int remainingSaveFailures = saveFailures;
        public Task SaveAttempted => saveAttempted.Task;
        private readonly TaskCompletionSource saveAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long NextSequence(string scopeIdentity)
            => inner.NextSequence(scopeIdentity);

        public bool TryGetUnresolved(
            string scopeIdentity,
            out TopicSendIdentityRecord? record)
            => inner.TryGetUnresolved(scopeIdentity, out record);

        public void Save(TopicSendIdentityRecord record)
        {
            saveAttempted.TrySetResult();
            if (Interlocked.Decrement(ref remainingSaveFailures) >= 0)
                throw new IOException("Local identity storage is unavailable.");
            inner.Save(record);
        }

        public void Remove(string scopeIdentity, string operationId)
            => inner.Remove(scopeIdentity, operationId);
    }

    private sealed class FileTopicSendIdentityStore(string directory)
        : ITopicSendIdentityStore
    {
        private sealed class ChangeSignal
        {
            public readonly object Gate = new();
            public TaskCompletionSource Changed = NewSignal();
        }

        private static readonly ConcurrentDictionary<string, ChangeSignal> Signals =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ChangeSignal signal = Signals.GetOrAdd(
            Path.GetFullPath(directory),
            static _ => new ChangeSignal());
        private object Gate => signal.Gate;

        public long NextSequence(string scopeIdentity)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "counter.txt");
                _ = long.TryParse(
                    File.Exists(path) ? File.ReadAllText(path) : null,
                    out var current);
                var next = checked(current + 1);
                File.WriteAllText(path, next.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                return next;
            }
        }

        public bool TryGetUnresolved(
            string scopeIdentity,
            out TopicSendIdentityRecord? record)
        {
            lock (Gate)
            {
                var path = RecordPath(scopeIdentity);
                if (!File.Exists(path))
                {
                    record = null;
                    return false;
                }
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                record = System.Text.Json.JsonSerializer.Deserialize<TopicSendIdentityRecord>(
                    stream,
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web));
                return record is not null;
            }
        }

        public void Save(TopicSendIdentityRecord record)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = RecordPath(record.ScopeIdentity);
                var pendingPath = $"{path}.{Guid.NewGuid():n}.pending";
                File.WriteAllText(
                    pendingPath,
                    System.Text.Json.JsonSerializer.Serialize(
                        record,
                        new System.Text.Json.JsonSerializerOptions(
                            System.Text.Json.JsonSerializerDefaults.Web)));
                File.Move(pendingPath, path, overwrite: true);
                Pulse();
            }
        }

        public void Remove(string scopeIdentity, string operationId)
        {
            lock (Gate)
            {
                if (!TryGetUnresolved(scopeIdentity, out var record)
                    || record is null
                    || !string.Equals(
                        record.OperationId,
                        operationId,
                        StringComparison.Ordinal))
                    return;
                File.Delete(RecordPath(scopeIdentity));
                Pulse();
            }
        }

        public async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                Task changed;
                lock (Gate)
                {
                    if (predicate()) return;
                    changed = signal.Changed.Task;
                }
                await changed.WaitAsync(cancellation.Token);
            }
        }

        private void Pulse()
        {
            var completed = signal.Changed;
            signal.Changed = NewSignal();
            completed.TrySetResult();
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string RecordPath(string scopeIdentity)
            => Path.Combine(directory, $"{scopeIdentity}.json");
    }

    private sealed class OneShotJournalCrash(
        string transition,
        TopicSendJournalBoundary boundary) : ITopicSendJournalFaultInjector
    {
        private int triggered;
        public bool Triggered => Volatile.Read(ref triggered) != 0;
        public Task TriggeredTask => triggeredSignal.Task;
        private readonly TaskCompletionSource triggeredSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Checkpoint(
            string candidateTransition,
            TopicSendJournalBoundary candidateBoundary,
            TopicSendIdentityRecord record)
        {
            if (!string.Equals(candidateTransition, transition, StringComparison.Ordinal)
                || candidateBoundary != boundary
                || Interlocked.Exchange(ref triggered, 1) != 0)
                return;
            triggeredSignal.TrySetResult();
            throw new TopicSendJournalCrashException(
                $"Injected crash at {transition}/{boundary}.");
        }
    }

    private sealed class NoopTurnRunner : ITopicTurnRunner
    {
        public Task<TopicRunCompletion> ExecuteAsync(
            TopicTurnDraft draft,
            IProgress<TopicRunUpdatePayload> progress,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? onStarted = null)
            => throw new InvalidOperationException("The test routes to the remote transport.");
    }

    private sealed class NoopEnvelopeTransport : ITopicEnvelopeTransport
    {
        public Task<MeshSendResult?> SendAsync(
            string targetDeviceId,
            string kind,
            string plaintext,
            string envelopeId,
            string? pushHint,
            CancellationToken cancellationToken)
            => Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> keys = new();
        public byte[] GetOrCreateDbKey(string identityId)
            => keys.TryGetValue(identityId, out var key)
                ? key
                : keys[identityId] = RandomNumberGenerator.GetBytes(32);
        public byte[]? GetDbKey(string identityId)
            => keys.GetValueOrDefault(identityId);
        public void PutDbKey(string identityId, byte[] key)
            => keys[identityId] = key.ToArray();
        public void DeleteDbKey(string identityId)
            => keys.Remove(identityId);
    }

    private sealed class EmptyBuiltIns : IBuiltInContentProvider
    {
        public IReadOnlyList<BuiltInPolicy> GetPolicies(AgentRole role) => [];
        public IReadOnlyList<KnowledgeItem> GetKnowledge(AgentRole role) => [];
        public IReadOnlyList<Skill> GetSkills(AgentRole role) => [];
        public KnowledgeItem? LoadKnowledge(string id) => null;
        public Skill? LoadSkill(string id) => null;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new TestHttpHandler()) { BaseAddress = new Uri("https://mesh.test") };
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class TestLifecycle : IAppLifecycleState
    {
        public bool IsForeground => true;
        public event Action<bool>? ForegroundChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri)
            => Initialize("https://mesh.test/", uri);
        protected override void NavigateToCore(string uri, NavigationOptions options)
            => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }

    private sealed class NoopJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            object?[]? args)
            => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
            => ValueTask.FromResult(default(TValue)!);
    }
}
