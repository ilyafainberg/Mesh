using System.Reflection;
using Mesh.App.Components.Mobile;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.ComponentTests;

public sealed partial class MobileMeLifecycleComponentTests
{
    [TestMethod]
    public async Task MobileHostLookupFailureFromOldAIsNotPresentedAfterAtoBtoA()
    {
        var transport = new ControllableDeviceTransport();
        var host = transport.Device;
        var router = new BlockingThrowingListRouter(host);
        var harness = CreateHarness(transport, router: router);
        await using var services = harness.Services;
        await using var renderer = new ComponentRenderer(harness.Services);
        var mounted = await renderer.MountAsync<MobileMe>();
        var accountA = harness.State.ActiveAccountId!;

        router.BlockNextEligibleList();
        var chooseHost = mounted.Component.GetType().GetMethod(
            "ChooseExecutionHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(chooseHost);
        var choose = renderer.Dispatcher.InvokeAsync(
            () => (Task)chooseHost.Invoke(mounted.Component, [host])!);
        await router.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var accountB = harness.State.ImportProfile(
            CreateAccountProfile("other-owner", "thread"));
        Assert.AreNotEqual(accountA, accountB);
        Assert.IsTrue(harness.State.SwitchAccount(accountA));
        router.Release.TrySetResult();
        await choose;

        var errorField = mounted.Component.GetType().GetField(
            "attachError",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(errorField);
        Assert.IsNull(
            errorField.GetValue(mounted.Component),
            "A failed host lookup from the retired A epoch mutated the reactivated A UI.");
    }

    [TestMethod]
    public async Task MobileHostLookupFailureInCurrentScopeRemainsVisible()
    {
        var transport = new ControllableDeviceTransport();
        var host = transport.Device;
        var router = new BlockingThrowingListRouter(host);
        var harness = CreateHarness(transport, router: router);
        await using var services = harness.Services;
        await using var renderer = new ComponentRenderer(harness.Services);
        var mounted = await renderer.MountAsync<MobileMe>();

        router.BlockNextEligibleList();
        var chooseHost = mounted.Component.GetType().GetMethod(
            "ChooseExecutionHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(chooseHost);
        var choose = renderer.Dispatcher.InvokeAsync(
            () => (Task)chooseHost.Invoke(mounted.Component, [host])!);
        await router.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        router.Release.TrySetResult();
        await choose;

        var errorField = mounted.Component.GetType().GetField(
            "attachError",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(errorField);
        var applyProjection = mounted.Component.GetType().GetMethod(
            "ApplyAssistantProjection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(applyProjection);
        await renderer.Dispatcher.InvokeAsync(
            () => applyProjection.Invoke(mounted.Component, new object?[] { null }));
        var persistComposer = mounted.Component.GetType().GetMethod(
            "ScheduleComposerSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(persistComposer);
        await renderer.Dispatcher.InvokeAsync(
            () => persistComposer.Invoke(mounted.Component, null));
        StringAssert.Contains(
            (string?)errorField.GetValue(mounted.Component),
            "Could not verify the AI host");
    }

    [TestMethod]
    public async Task DesktopHostLookupFailureFromOldAIsNotPresentedAfterAtoBtoA()
    {
        var transport = new ControllableDeviceTransport();
        var host = transport.Device;
        var router = new BlockingThrowingListRouter(host);
        var harness = CreateHarness(transport, router: router);
        await using var services = harness.Services;
        await using var renderer = new ComponentRenderer(harness.Services);
        var mounted = await renderer.MountAsync<Mesh.App.Components.Pages.Home>();
        var accountA = harness.State.ActiveAccountId!;

        router.BlockNextList();
        var requestMove = mounted.Component.GetType().GetMethod(
            "RequestMoveAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(requestMove);
        var move = renderer.Dispatcher.InvokeAsync(
            () => (Task)requestMove.Invoke(
                mounted.Component,
                [new AgentExecutionHost(host.DeviceId, host.Name, host.Platform)])!);
        await router.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var accountB = harness.State.ImportProfile(
            CreateAccountProfile("other-owner", "thread"));
        Assert.AreNotEqual(accountA, accountB);
        Assert.IsTrue(harness.State.SwitchAccount(accountA));
        router.Release.TrySetResult();
        await move;

        var errorField = mounted.Component.GetType().GetField(
            "dispatchError",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(errorField);
        Assert.IsNull(
            errorField.GetValue(mounted.Component),
            "A failed Desktop host lookup from the retired A epoch mutated the new A UI.");
    }

    private sealed class BlockingThrowingListRouter(
        Mesh.Shared.DeviceInfo device) : ITopicExecutionRouter
    {
        private int blockNextList;
        private int blockNextEligibleList;
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

        public void BlockNextEligibleList()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref blockNextEligibleList, 1);
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

        public async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNextEligibleList, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("reviewer stale roster failure");
            }
            return [device];
        }

        public async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListDevicesAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNextList, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("reviewer stale roster failure");
            }
            return [device];
        }
    }
}
