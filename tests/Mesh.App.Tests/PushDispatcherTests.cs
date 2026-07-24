using Mesh.Relay.Backplane;
using Mesh.Relay.Push;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Covers the relay's "wake offline siblings" push path: when a notifiable message is delivered live
/// to one of a handle's devices, the handle's OTHER (offline) devices are still woken by push, while
/// devices that are currently connected on any instance are skipped.
/// </summary>
[TestClass]
public sealed class PushDispatcherTests
{
    private sealed class CapturingSender(string platform) : IPushSender
    {
        public string Platform { get; } = platform;
        public List<string> Sent { get; } = new();

        public Task SendAsync(string token, PushAlert alert, CancellationToken ct = default)
        {
            Sent.Add(token);
            return Task.CompletedTask;
        }
    }

    private static PushDispatcher NewDispatcher(
        IRelayStore store, IBackplane backplane, params IPushSender[] senders)
        => new(store, backplane, senders, NullLogger<PushDispatcher>.Instance);

    private static async Task SeedHandleWithTwoPhonesAsync(InMemoryRelayStore store)
    {
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync("ifain", "dev-iphone", DevicePlatforms.IOS, "tok-iphone");
        await store.SetDevicePushTokenAsync("ifain", "dev-ipad", DevicePlatforms.IOS, "tok-ipad");
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_PushesOfflineDevicesAndSkipsConnectedOnes()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await SeedHandleWithTwoPhonesAsync(store);

        // The iPad is connected somewhere (e.g. the message was just delivered live to it); the
        // iPhone is offline and must still be woken.
        await backplane.SetDevicePresenceAsync("ifain", "dev-ipad");

        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, apns);

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
        var dispatcher = NewDispatcher(store, backplane, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Chat, "alice");

        Assert.AreEqual(0, apns.Sent.Count);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_NonNotifiableKindProducesNoPush()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await SeedHandleWithTwoPhonesAsync(store);

        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, apns);

        // A receipt is not a user-facing message, so no alert is composed and nothing is sent.
        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Receipt, "alice");

        Assert.AreEqual(0, apns.Sent.Count);
    }

    [TestMethod]
    public async Task WakeOfflineSiblings_SkipsTokensWithNoMatchingSender()
    {
        var store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();
        await store.UpsertHandleAsync("ifain", "key-desktop", "Ilya", allowNewDevice: true);
        await store.SetDevicePushTokenAsync("ifain", "dev-droid", DevicePlatforms.Android, "tok-droid");

        // Only an APNs (iOS) sender is configured; the offline Android token has no sender.
        var apns = new CapturingSender(DevicePlatforms.IOS);
        var dispatcher = NewDispatcher(store, backplane, apns);

        await dispatcher.WakeOfflineSiblingsAsync("ifain", MeshKinds.Chat, "alice");

        Assert.AreEqual(0, apns.Sent.Count);
    }
}
