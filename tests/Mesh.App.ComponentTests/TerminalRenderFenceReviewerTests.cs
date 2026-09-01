using Mesh.App.Components.Pages;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;

namespace Mesh.App.ComponentTests;

public sealed partial class MobileMeLifecycleComponentTests
{
    [TestMethod]
    public async Task DesktopLateDispatchFailureAfterCompletionDoesNotRestoreFailureUi()
    {
        var transport = new ControllableDeviceTransport();
        var router = new BlockingThrowingRouter(transport.Device);
        var harness = CreateHarness(transport, router: router);
        await using var services = harness.Services;
        await using var renderer = new ComponentRenderer(harness.Services);
        var mounted = await renderer.MountAsync<Home>();

        await renderer.InputAsync(mounted.Id, "Message your assistant", "terminal wins");
        var send = renderer.ClickAsync(mounted.Id, "Ask AI on agent host");
        await router.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var request = AssertSinglePendingRequest(harness.State);
        harness.State.AddOwnChatLine(
            request.ThreadId,
            new ChatLine
            {
                Role = "assistant",
                Text = "completed",
                ReplyToLineId = request.TriggerLineId
            },
            terminalRunId: request.RunId);
        router.Release.TrySetResult();
        await send;

        await renderer.Dispatcher.InvokeAsync(() => { });
        Assert.IsNull(harness.State.GetPendingAssistantAiRequest(request.ThreadId));
        Assert.IsFalse(
            renderer.MarkupContains(mounted.Id, "AI unavailable"),
            $"A late dispatch failure mutated terminal UI: {renderer.RenderedText(mounted.Id)}");
        Assert.IsFalse(
            renderer.MarkupContains(mounted.Id, "Retry AI response"),
            $"A late dispatch failure recreated a retry action: {renderer.RenderedText(mounted.Id)}");
    }

    [TestMethod]
    public async Task DesktopCommunicationMoveStartedInOldAIsBlockedAfterAtoBtoA()
    {
        var transport = new ControllableDeviceTransport();
        var destination = new Mesh.Shared.DeviceInfo(
            "phone-target",
            "Phone",
            true,
            DevicePlatforms.IOS,
            true,
            AgentHostEnabled: false);
        var router = new BlockingRosterRouter([transport.Device, destination]);
        var harness = CreateHarness(transport, router: router);
        await using var services = harness.Services;
        await using var renderer = new ComponentRenderer(harness.Services);
        var mounted = await renderer.MountAsync<Home>();
        var accountA = harness.State.ActiveAccountId!;

        router.BlockNextList();
        var moveMethod = mounted.Component.GetType().GetMethod(
            "MoveCommunicationTo",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(moveMethod);
        var move = renderer.Dispatcher.InvokeAsync(
            () => (Task)moveMethod.Invoke(mounted.Component, [destination])!);
        await router.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var accountB = harness.State.ImportProfile(
            CreateAccountProfile("other-owner", "thread"));
        Assert.AreNotEqual(accountA, accountB);
        Assert.IsTrue(harness.State.SwitchAccount(accountA));
        router.Release.TrySetResult();
        await move;

        Assert.IsNull(
            harness.State.Profile.OwnThreads.Single(thread => thread.Id == "thread")
                .CommunicationDestinationDeviceId,
            "An A-generation move continuation mutated the reactivated A database.");
    }

    private static AssistantAiRequest AssertSinglePendingRequest(AppState state)
    {
        var request = state.GetPendingAssistantAiRequest("thread");
        Assert.IsNotNull(request);
        return request;
    }

    private sealed class BlockingThrowingRouter(Mesh.Shared.DeviceInfo device) : ITopicExecutionRouter
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TopicDispatchResult> SubmitAsync(
            TopicTurnDraft draft,
            IProgress<TopicRunUpdatePayload>? progress,
            CancellationToken cancellationToken,
            TopicSendHandoffContext? handoffContext = null)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("reviewer late dispatch failure");
        }

        public Task<bool> CancelQueuedAsync(
            ScopedAsyncOperation operation,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> StopAsync(
            ScopedAsyncOperation operation,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Mesh.Shared.DeviceInfo>>([device]);

        public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Mesh.Shared.DeviceInfo>>([device]);
    }

    private sealed class BlockingRosterRouter(
        IReadOnlyList<Mesh.Shared.DeviceInfo> devices) : ITopicExecutionRouter
    {
        private int blockNextList;
        public TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextList()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref blockNextList, 1);
        }

        public Task<TopicDispatchResult> SubmitAsync(
            TopicTurnDraft draft,
            IProgress<TopicRunUpdatePayload>? progress,
            CancellationToken cancellationToken,
            TopicSendHandoffContext? handoffContext = null)
            => Task.FromResult(TopicDispatchResult.Ok(draft.RunId));

        public Task<bool> CancelQueuedAsync(
            ScopedAsyncOperation operation,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> StopAsync(
            ScopedAsyncOperation operation,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Mesh.Shared.DeviceInfo>>(
                DeviceExecutionEligibility.EligibleHosts(devices));

        public async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNextList, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return devices;
        }
    }
}
