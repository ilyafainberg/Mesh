using Mesh.Relay.Hub;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DurableRelayDeliveryTests
{
    [TestMethod]
    public void DeviceIds_RequireCanonicalLowercaseHex()
    {
        var deviceId = DeviceProtocol.DeviceId("device-public-key");

        Assert.IsTrue(DeviceProtocol.IsValidDeviceId(deviceId));
        Assert.IsFalse(DeviceProtocol.IsValidDeviceId(deviceId.ToUpperInvariant()));
        Assert.IsFalse(DeviceProtocol.IsValidDeviceId("123456789abg"));
    }

    [TestMethod]
    public void DurableHandshake_UsesPostReadyBoundedDrain()
    {
        Assert.IsTrue(RelayInboxPolicy.UsesClientInitiatedDrain(supportsDurableDelivery: true));
        Assert.IsFalse(RelayInboxPolicy.UsesClientInitiatedDrain(supportsDurableDelivery: false));
        Assert.IsTrue(
            RelayInboxPolicy.DeliveryWindow <= 4,
            "The initial Cosmos lease batch must stay below transport timeout budgets.");
    }

    [TestMethod]
    public async Task SnapshotRequest_RemainsQueuedAfterRepeatedDeliveryFailures()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        await store.EnqueueAsync(
            inbox, "snapshot-request", "sender", "payload", RelayInboxPriority.Control);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var leaseOwner = $"connection-{attempt}";
            var leased = await store.LeaseInboxAsync(inbox, leaseOwner);
            Assert.HasCount(1, leased);
            Assert.AreEqual(attempt, leased[0].DeliveryAttempts);
            await store.ReleaseInboxLeasesAsync(inbox, leaseOwner);
        }

        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "final-connection"));
    }

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
    public async Task InboxLease_DrainsCriticalControlNormalSyncAndBulkInOrder()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        await store.EnqueueAsync(inbox, "bulk", "sender", "bulk", RelayInboxPriority.Bulk);
        await store.EnqueueAsync(inbox, "sync", "sender", "sync", RelayInboxPriority.Sync);
        await store.EnqueueAsync(inbox, "normal", "sender", "normal", RelayInboxPriority.Normal);
        await store.EnqueueAsync(inbox, "control", "sender", "control", RelayInboxPriority.Control);
        await store.EnqueueAsync(inbox, "critical", "sender", "critical", RelayInboxPriority.Critical);

        var leased = await store.LeaseInboxAsync(inbox, "connection", maxItems: 5);

        CollectionAssert.AreEqual(
            new[] { "critical", "control", "normal", "sync", "bulk" },
            leased.Select(item => item.EnvelopeId).ToArray());
        Assert.AreEqual(5, leased.Count);
        Assert.AreEqual(RelayInboxPriority.Critical, RelayInboxPriority.ForKind(MeshKinds.TopicRunCancel));
        Assert.AreEqual(
            RelayInboxPriority.Bulk,
            RelayInboxPriority.ForKind(DeviceSyncKinds.EnvelopeSnapshotChunk));
    }

    [TestMethod]
    public async Task InboxLease_AgesOneLowPriorityItemAheadOfFreshControlTraffic()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRelayStore(clock);
        const string inbox = "owner";
        await store.EnqueueAsync(inbox, "bulk-aged", "sender", "bulk", RelayInboxPriority.Bulk);
        clock.Advance(RelayInboxPolicy.PriorityAgingThreshold + TimeSpan.FromSeconds(1));
        await store.EnqueueAsync(inbox, "control-fresh", "sender", "control", RelayInboxPriority.Control);
        await store.EnqueueAsync(inbox, "critical-fresh", "sender", "critical", RelayInboxPriority.Critical);

        var leased = await store.LeaseInboxAsync(inbox, "connection", maxItems: 2);

        CollectionAssert.AreEqual(
            new[] { "bulk-aged", "critical-fresh" },
            leased.Select(item => item.EnvelopeId).ToArray());
    }
    [TestMethod]
    public async Task DeviceRevocation_PurgesOnlyTheTargetDeviceInbox()
    {
        var store = new InMemoryRelayStore();
        const string handle = "owner";
        const string currentKey = "current-device-key";
        const string targetKey = "target-device-key";
        await store.UpsertHandleAsync(handle, currentKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var currentDeviceId = DeviceProtocol.DeviceId(currentKey);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);
        var currentInbox = MeshRouter.DeviceInboxKey(handle, currentDeviceId);
        var targetInbox = MeshRouter.DeviceInboxKey(handle, targetDeviceId);
        await store.EnqueueAsync(currentInbox, "current", handle, "current");
        await store.EnqueueAsync(targetInbox, "target-1", handle, "target-1");
        await store.EnqueueAsync(targetInbox, "target-2", handle, "target-2");
        await store.EnqueueAsync(handle, "shared", handle, "shared");

        var unauthorized = await store.RevokeDeviceAsync(
            handle, targetDeviceId, "not-an-authorized-key");
        Assert.IsFalse(unauthorized.Revoked);
        Assert.AreEqual(0, unauthorized.PurgedEnvelopes);

        var result = await store.RevokeDeviceAsync(handle, targetDeviceId, currentKey);

        Assert.IsTrue(result.Revoked);
        Assert.AreEqual(2, result.PurgedEnvelopes);
        var registration = await store.GetHandleAsync(handle);
        Assert.IsNotNull(registration);
        CollectionAssert.AreEqual(new[] { currentKey }, registration.DevicePublicKeys);
        Assert.HasCount(0, await store.LeaseInboxAsync(targetInbox, "target"));
        Assert.HasCount(1, await store.LeaseInboxAsync(currentInbox, "current"));
        Assert.HasCount(1, await store.LeaseInboxAsync(handle, "shared"));

        await store.EnqueueAsync(targetInbox, "late-target", handle, "late-target");
        var retry = await store.RevokeDeviceAsync(handle, targetDeviceId, currentKey);
        Assert.IsFalse(retry.Revoked);
        Assert.AreEqual(1, retry.PurgedEnvelopes);
        Assert.HasCount(0, await store.LeaseInboxAsync(targetInbox, "target-retry"));
    }

    [TestMethod]
    public async Task InboxCancellation_IsIdempotentAndSenderScopedBeforeDelivery()
    {
        var store = new InMemoryRelayStore();
        var inbox = MeshRouter.DeviceInboxKey("owner", "laptop");
        var queued = await store.EnqueueAsync(inbox, "run-2", "owner", "ciphertext");

        Assert.IsFalse(await store.CancelInboxAsync(inbox, queued.DeliveryId, "other"));
        Assert.IsTrue(await store.CancelInboxAsync(inbox, queued.DeliveryId, "owner"));
        Assert.IsFalse(await store.CancelInboxAsync(inbox, queued.DeliveryId, "owner"));
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection"));
    }

    [TestMethod]
    public async Task InboxCancellation_IsRefusedAfterAnyDeliveryAttempt()
    {
        var store = new InMemoryRelayStore();
        var inbox = MeshRouter.DeviceInboxKey("owner", "laptop");
        var queued = await store.EnqueueAsync(inbox, "run-attempted", "owner", "ciphertext");

        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "live-recipient"));
        Assert.IsFalse(await store.CancelInboxAsync(inbox, queued.DeliveryId, "owner"));
        await store.ReleaseInboxLeasesAsync(inbox, "live-recipient");
        Assert.IsFalse(await store.CancelInboxAsync(inbox, queued.DeliveryId, "owner"));
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "retry-recipient"));
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
        var delivered = await store.LeaseInboxAsync(inbox, "connection");
        Assert.HasCount(1, delivered);
        Assert.AreEqual(1, delivered[0].DeliveryAttempts);
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
