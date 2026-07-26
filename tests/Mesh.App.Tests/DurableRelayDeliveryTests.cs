using Mesh.Relay.Hub;
using Mesh.Relay.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DurableRelayDeliveryTests
{
    [TestMethod]
    public async Task InboxLease_RedeliversUntilRecipientAcknowledges()
    {
        var store = new InMemoryRelayStore();
        var inbox = MeshRouter.DeviceInboxKey("owner", "laptop");
        var first = await store.EnqueueAsync(inbox, "run-1", "owner", "{\"id\":1}");
        var duplicate = await store.EnqueueAsync(inbox, "run-1", "owner", "{\"id\":2}");

        Assert.IsTrue(first.Created);
        Assert.IsFalse(duplicate.Created);
        Assert.AreEqual(first.DeliveryId, duplicate.DeliveryId);
        var leased = await store.LeaseInboxAsync(inbox, "connection-a");
        Assert.HasCount(1, leased);
        Assert.AreEqual(1, leased[0].DeliveryAttempts);
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection-a"));
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection-b"));

        await store.ReleaseInboxLeasesAsync(inbox, "connection-a");
        var redelivery = await store.LeaseInboxAsync(inbox, "connection-b");
        Assert.HasCount(1, redelivery);
        Assert.AreEqual(first.DeliveryId, redelivery[0].Id);
        Assert.AreEqual(2, redelivery[0].DeliveryAttempts);

        Assert.IsNotNull(await store.AcknowledgeInboxAsync(inbox, first.DeliveryId));
        Assert.IsNull(await store.AcknowledgeInboxAsync(inbox, first.DeliveryId));
        await store.ReleaseInboxLeasesAsync(inbox, "connection-b");
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection-c"));
    }

    [TestMethod]
    public async Task InboxCancellation_IsIdempotentAndSenderScoped()
    {
        var store = new InMemoryRelayStore();
        var inbox = MeshRouter.DeviceInboxKey("owner", "laptop");
        var queued = await store.EnqueueAsync(inbox, "run-2", "owner", "ciphertext");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "live-recipient"));

        Assert.IsFalse(await store.CancelInboxAsync(inbox, queued.DeliveryId, "other"));
        Assert.IsTrue(await store.CancelInboxAsync(inbox, queued.DeliveryId, "owner"));
        Assert.IsFalse(await store.CancelInboxAsync(inbox, queued.DeliveryId, "owner"));
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection"));
    }

    [TestMethod]
    public async Task InboxLease_ReplenishesWindowWithoutRedeliveringOutstandingItems()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        for (var index = 0; index < RelayInboxPolicy.DeliveryWindow + 5; index++)
            await store.EnqueueAsync(inbox, $"message-{index}", "sender", $"payload-{index}");

        var initial = await store.LeaseInboxAsync(inbox, "connection");
        Assert.HasCount(RelayInboxPolicy.DeliveryWindow, initial);
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection", maxItems: 1));

        Assert.IsNotNull(await store.AcknowledgeInboxAsync(inbox, initial[0].Id));
        var replenished = await store.LeaseInboxAsync(inbox, "connection");
        Assert.HasCount(1, replenished);
        Assert.IsFalse(initial.Any(item => item.Id == replenished[0].Id));
    }

    [TestMethod]
    public async Task LiveDeliveryLease_BlocksConcurrentDrainUntilReleased()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        var queued = await store.EnqueueAsync(inbox, "message", "sender", "payload");

        Assert.IsTrue(await store.TryLeaseInboxItemAsync(
            inbox, queued.DeliveryId, "live-attempt"));
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection"));

        await store.ReleaseInboxLeaseAsync(inbox, queued.DeliveryId, "live-attempt");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "connection"));
    }
    [TestMethod]
    public async Task LiveDeliveryLease_BecomesAvailableAfterExpiry()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRelayStore(clock);
        const string inbox = "owner";
        var queued = await store.EnqueueAsync(inbox, "message", "sender", "payload");

        Assert.IsTrue(await store.TryLeaseInboxItemAsync(
            inbox, queued.DeliveryId, "live-attempt", TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "connection"));
    }
    [TestMethod]
    public async Task InboxRetention_IsMeasuredFromOriginalEnqueueTime()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRelayStore(clock);
        const string inbox = "owner";
        await store.EnqueueAsync(inbox, "message", "sender", "payload");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "connection"));

        clock.Advance(TimeSpan.FromDays(13));
        await store.ReleaseInboxLeasesAsync(inbox, "connection");
        clock.Advance(TimeSpan.FromDays(2));

        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection-2"));
        Assert.AreEqual(0, (await store.GetInboxStatsAsync()).QueuedItems);
    }

    [TestMethod]
    public async Task ReservedHandleInbox_DoesNotExpire()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRelayStore(clock);
        const string inbox = "meshreport";
        await store.EnqueueAsync(inbox, "message", "sender", "payload");

        clock.Advance(TimeSpan.FromDays(30));

        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "connection"));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
