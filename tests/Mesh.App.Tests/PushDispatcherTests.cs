using Mesh.Relay.Backplane;
using Mesh.Relay.Push;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class PushDispatcherTests
{
    private sealed class CapturingSender(
        string platform,
        PushSendResult? result = null) : IPushSender
    {
        public string Platform { get; } = platform;
        public List<string> Sent { get; } = new();
        public List<PushAlert> Alerts { get; } = new();
        public TaskCompletionSource<(string Token, PushAlert Alert)> FirstDelivery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PushSendResult> SendAsync(
            string token,
            PushAlert alert,
            CancellationToken ct = default)
        {
            Sent.Add(token);
            Alerts.Add(alert);
            FirstDelivery.TrySetResult((token, alert));
            return Task.FromResult(result ?? PushSendResult.Sent());
        }
    }

    private static PushDispatcher NewDispatcher(
        IRelayStore store,
        IBackplane backplane,
        bool backgroundSyncEnabled = true,
        params IPushSender[] senders)
        => new(
            store,
            backplane,
            senders,
            new PushDispatchOptions(backgroundSyncEnabled),
            NullLogger<PushDispatcher>.Instance);

    private static async Task SeedHandleWithTwoPhonesAsync(InMemoryRelayStore store)
    {
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-ipad", DevicePlatforms.IOS, "tok-ipad", alertsEnabled: true);
    }

    [TestMethod]
    public void Classify_TopicResponseRequiresSameHandleDeviceTargetAndExplicitHint()
    {
        var response = MeshEnvelope.Create(
            "ifain",
            "ifain",
            MeshKinds.TopicRunUpdate,
            "ciphertext",
            fromDevice: "dev-desktop",
            toDevice: "dev-iphone",
            pushHint: PushHintProtocol.TopicResponse);

        Assert.AreEqual(PushCategory.TopicResponse, PushDispatcher.Classify(response));
        Assert.AreEqual(PushCategory.None, PushDispatcher.Classify(response with { PushHint = null }));
        Assert.AreEqual(PushCategory.None, PushDispatcher.Classify(response with { To = "alice" }));
        Assert.AreEqual(
            PushCategory.None,
            PushDispatcher.Classify(response with { ToDevice = response.FromDevice }));
        Assert.AreEqual(
            PushCategory.None,
            PushDispatcher.Classify(response with { Kind = MeshKinds.TopicRunRequest }));
    }

    [TestMethod]
    public async Task NotifyOffline_TopicResponsePushesOnlyTargetedDevice()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await SeedHandleWithTwoPhonesAsync(store);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);
        var response = MeshEnvelope.Create(
            "ifain",
            "ifain",
            MeshKinds.TopicRunUpdate,
            "ciphertext",
            fromDevice: "dev-desktop",
            toDevice: "dev-iphone",
            pushHint: PushHintProtocol.TopicResponse);

        dispatcher.NotifyOffline("ifain", "dev-iphone", response);

        var delivery = await apns.FirstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("tok-iphone", delivery.Token);
        Assert.AreEqual("Mesh", delivery.Alert.Title);
        Assert.AreEqual("Your agent replied in a topic", delivery.Alert.Body);
        Assert.AreEqual("topic", delivery.Alert.Category);
        Assert.AreEqual(PushDeliveryMode.AlertAndBackground, delivery.Alert.Mode);
        CollectionAssert.AreEqual(new[] { "tok-iphone" }, apns.Sent);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_PushesOfflineDevicesAndSkipsConnectedOnes()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await SeedHandleWithTwoPhonesAsync(store);
        await backplane.SetDevicePresenceAsync("ifain", "dev-ipad");

        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Chat, "alice");

        CollectionAssert.AreEquivalent(new[] { "tok-iphone" }, apns.Sent);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_NoPushWhenEveryTokenDeviceIsConnected()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await SeedHandleWithTwoPhonesAsync(store);
        await backplane.SetDevicePresenceAsync("ifain", "dev-iphone");
        await backplane.SetDevicePresenceAsync("ifain", "dev-ipad");

        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Chat, "alice");

        Assert.AreEqual(0, apns.Sent.Count);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_UnsupportedKindProducesNoPush()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await SeedHandleWithTwoPhonesAsync(store);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.TopicRunRequest, "ifain");

        Assert.AreEqual(0, apns.Sent.Count);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_ReceiptUsesRateLimitedBackgroundWake()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: true);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Receipt, "alice");
        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Receipt, "alice");

        Assert.AreEqual(1, apns.Sent.Count);
        Assert.AreEqual(PushDeliveryMode.Background, apns.Alerts[0].Mode);
    }

    [TestMethod]
    public async Task NotifyOffline_AlertPermissionDeniedUsesSilentWake()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: false);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);
        var message = MeshEnvelope.Create("alice", "ifain", MeshKinds.DirectMessage, "ciphertext");

        dispatcher.NotifyOffline("ifain", "dev-iphone", message);

        var delivery = await apns.FirstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(PushDeliveryMode.Background, delivery.Alert.Mode);
        Assert.AreEqual("", delivery.Alert.Title);
    }

    [TestMethod]
    public async Task AlertPermissionDeniedDoesNotWakeForForegroundOnlyWork()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: false);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.AgentRequest, "alice");

        Assert.AreEqual(0, apns.Sent.Count);
    }

    [TestMethod]
    public async Task ForegroundOnlyWorkUsesAlertWithoutBackgroundWake()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: true);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.AgentRequest, "alice");

        Assert.AreEqual(1, apns.Sent.Count);
        Assert.AreEqual(PushDeliveryMode.Alert, apns.Alerts[0].Mode);
    }

    [TestMethod]
    public async Task BackgroundWakeThrottleSurvivesAlertAuthorizationRefresh()
    {
        var store = new InMemoryRelayStore();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: true);
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");

        Assert.IsTrue(await store.TryAcquireBackgroundPushAsync(
            "ifain", "dev-iphone", now, TimeSpan.FromMinutes(20), TimeSpan.FromHours(1), 3));
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: false);

        Assert.IsFalse(await store.TryAcquireBackgroundPushAsync(
            "ifain", "dev-iphone", now.AddMinutes(1),
            TimeSpan.FromMinutes(20), TimeSpan.FromHours(1), 3));
    }

    [TestMethod]
    public async Task KillSwitchRestoresAlertOnlyBehavior()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: true);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, false, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Receipt, "alice");
        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.DirectMessage, "alice");

        Assert.AreEqual(1, apns.Sent.Count);
        Assert.AreEqual(PushDeliveryMode.Alert, apns.Alerts[0].Mode);
    }

    [TestMethod]
    public async Task InvalidTokenIsRemovedFromHandle()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone", alertsEnabled: true);
        var apns = new CapturingSender(
            DevicePlatforms.IOS,
            PushSendResult.InvalidToken(410, "Unregistered"));
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.DirectMessage, "alice");

        var handle = await store.GetHandleAsync("ifain");
        Assert.IsNotNull(handle);
        Assert.AreEqual(0, handle.DevicePushTokens.Count);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_SkipsTokensWithNoMatchingSender()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync(
            "ifain", "dev-droid", DevicePlatforms.Android, "tok-droid", alertsEnabled: true);
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, true, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Chat, "alice");

        Assert.AreEqual(0, apns.Sent.Count);
    }
}
