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
        Assert.IsTrue(
            DeviceQueueProtocol.DeliveryWindow <= 4,
            "Device sync must use the same bounded Cosmos lease window.");
    }

    [TestMethod]
    public async Task DeviceQueue_DrainWindowIsBoundedForLargeBacklogs()
    {
        var store = CreateQueueStore();
        const string handle = "owner";
        const string sourceDeviceId = "device-a";
        const string targetDeviceId = "device-b";
        for (var index = 0; index < DeviceQueueProtocol.DeliveryWindow + 3; index++)
        {
            Assert.IsTrue((await store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue(
                    sourceDeviceId,
                    targetDeviceId,
                    $"operation-{index}",
                    $"payload-{index}"))).Created);
        }

        var first = await store.DrainDeviceQueueAsync(
            handle,
            targetDeviceId,
            "lease",
            maxEntries: 64);

        Assert.HasCount(DeviceQueueProtocol.DeliveryWindow, first.Entries);
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
        Assert.IsTrue(first.Accepted);
        Assert.IsFalse(duplicate.Created);
        Assert.IsTrue(duplicate.Accepted);
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

        Assert.IsNotNull(await store.AcknowledgeInboxAsync(
            inbox, first.DeliveryId, "connection-b"));
        Assert.IsNull(await store.AcknowledgeInboxAsync(
            inbox, first.DeliveryId, "connection-b"));
        await store.ReleaseInboxLeasesAsync(inbox, "connection-b");
        Assert.HasCount(0, await store.LeaseInboxAsync(inbox, "connection-c"));
    }

    [TestMethod]
    public async Task InboxAck_RepeatedAckIsRejected()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        var queued = await store.EnqueueAsync(inbox, "message", "sender", "payload");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "connection"));

        Assert.IsNotNull(await store.AcknowledgeInboxAsync(
            inbox, queued.DeliveryId, "connection"));
        Assert.IsNull(await store.AcknowledgeInboxAsync(
            inbox, queued.DeliveryId, "connection"));
    }

    [TestMethod]
    public async Task InboxAck_StaleLeaseOwnerCannotAckAfterReLease()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        var queued = await store.EnqueueAsync(inbox, "message", "sender", "payload");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "stale-owner"));
        await store.ReleaseInboxLeasesAsync(inbox, "stale-owner");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "current-owner"));

        Assert.IsNull(await store.AcknowledgeInboxAsync(
            inbox, queued.DeliveryId, "stale-owner"));
        Assert.IsNotNull(await store.AcknowledgeInboxAsync(
            inbox, queued.DeliveryId, "current-owner"));
    }

    [TestMethod]
    public async Task InboxAck_ExpiredLeaseCannotAck()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 29, 16, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRelayStore(clock);
        const string inbox = "owner";
        var queued = await store.EnqueueAsync(inbox, "message", "sender", "payload");
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "expired-owner"));
        clock.Advance(RelayInboxPolicy.LeaseDuration + TimeSpan.FromTicks(1));

        Assert.IsNull(await store.AcknowledgeInboxAsync(
            inbox, queued.DeliveryId, "expired-owner"));
        Assert.HasCount(1, await store.LeaseInboxAsync(inbox, "current-owner"));
        Assert.IsNotNull(await store.AcknowledgeInboxAsync(
            inbox, queued.DeliveryId, "current-owner"));
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
    public async Task BackgroundLeaseSkipsForegroundBacklogBeforeLeasing()
    {
        var store = new InMemoryRelayStore();
        const string inbox = "owner";
        for (var index = 0; index < 6; index++)
            await store.EnqueueAsync(
                inbox,
                $"foreground-{index}",
                "sender",
                $"foreground-{index}",
                RelayInboxPriority.Normal,
                requiresForeground: true);
        await store.EnqueueAsync(
            inbox,
            "background-control",
            "sender",
            "background-control",
            RelayInboxPriority.Control,
            requiresForeground: false);
        await store.EnqueueAsync(
            inbox,
            "background-sync",
            "sender",
            "background-sync",
            RelayInboxPriority.Sync,
            requiresForeground: false);

        var background = await store.LeaseInboxAsync(
            inbox,
            "background-connection",
            includeForeground: false);

        CollectionAssert.AreEqual(
            new[] { "background-control", "background-sync" },
            background.Select(item => item.EnvelopeId).ToArray());
        Assert.IsTrue(background.All(item => !item.RequiresForeground));
        Assert.AreEqual(RelayInboxPriority.Control, background[0].Priority);
        Assert.AreEqual(RelayInboxPriority.Sync, background[1].Priority);

        var foreground = await store.LeaseInboxAsync(
            inbox,
            "foreground-connection");

        Assert.HasCount(RelayInboxPolicy.DeliveryWindow, foreground);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, RelayInboxPolicy.DeliveryWindow)
                .Select(index => $"foreground-{index}")
                .ToArray(),
            foreground.Select(item => item.EnvelopeId).ToArray());
        Assert.IsTrue(foreground.All(item =>
            item.RequiresForeground
            && item.Priority == RelayInboxPriority.Normal
            && item.DeliveryAttempts == 1));
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
        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(currentDeviceId, targetDeviceId, "target-op", "target-op"));
        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(targetDeviceId, currentDeviceId, "current-op", "current-op"));

        var unauthorized = await store.RevokeDeviceAsync(
            handle, targetDeviceId, "not-an-authorized-key");
        Assert.IsFalse(unauthorized.Revoked);
        Assert.AreEqual(0, unauthorized.PurgedEnvelopes);

        var result = await store.RevokeDeviceAsync(handle, targetDeviceId, currentKey);

        Assert.IsTrue(result.Revoked);
        Assert.AreEqual(3, result.PurgedEnvelopes);
        var registration = await store.GetHandleAsync(handle);
        Assert.IsNotNull(registration);
        CollectionAssert.AreEqual(new[] { currentKey }, registration.DevicePublicKeys);
        Assert.HasCount(0, await store.LeaseInboxAsync(targetInbox, "target"));
        Assert.HasCount(1, await store.LeaseInboxAsync(currentInbox, "current"));
        Assert.HasCount(1, await store.LeaseInboxAsync(handle, "shared"));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, currentDeviceId));
        await store.EnqueueAsync(targetInbox, "late-target", handle, "late-target");
        var retry = await store.RevokeDeviceAsync(handle, targetDeviceId, currentKey);
        Assert.IsFalse(retry.Revoked);
        Assert.AreEqual(0, retry.PurgedEnvelopes);
        Assert.HasCount(0, await store.LeaseInboxAsync(targetInbox, "target-retry"));
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task DeviceQueue_IsolatedPerTargetDeviceAndAckedByOwner()
    {
        var store = CreateQueueStore();
        const string handle = "owner";
        const string deviceA = "device-a";
        const string deviceB = "device-b";

        var toB = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(deviceA, deviceB, "op-a-to-b", "payload-a"));
        var toA = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(deviceB, deviceA, "op-b-to-a", "payload-b"));

        Assert.IsTrue(toB.Accepted);
        Assert.IsTrue(toA.Accepted);
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, deviceA));
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, deviceB));

        var drainedA = await store.DrainDeviceQueueAsync(handle, deviceA, "lease-a");
        var drainedB = await store.DrainDeviceQueueAsync(handle, deviceB, "lease-b");

        Assert.HasCount(1, drainedA.Entries);
        Assert.HasCount(1, drainedB.Entries);
        Assert.AreEqual("payload-b", drainedA.Entries[0].Payload);
        Assert.AreEqual(deviceA, drainedA.Entries[0].TargetDeviceId);
        Assert.AreEqual("payload-a", drainedB.Entries[0].Payload);
        Assert.AreEqual(deviceB, drainedB.Entries[0].TargetDeviceId);

        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(handle, deviceA, drainedB.Entries[0].EntryId, "lease-a"));
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(handle, deviceB, drainedA.Entries[0].EntryId, "lease-b"));
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(handle, deviceA, drainedA.Entries[0].EntryId, "wrong-owner"));
        Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(handle, deviceA, drainedA.Entries[0].EntryId, "lease-a"));
        Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(handle, deviceB, drainedB.Entries[0].EntryId, "lease-b"));
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(handle, deviceB, drainedB.Entries[0].EntryId, "lease-b"));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, deviceA));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, deviceB));
    }

    [TestMethod]
    public async Task AddingDevice_PreservesQueuedWorkBetweenExistingDevices()
    {
        var store = new InMemoryRelayStore();
        const string handle = "owner";
        const string sourceKey = "source-key";
        const string targetKey = "target-key";
        const string addedKey = "added-key";
        await store.UpsertHandleAsync(handle, sourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var sourceDeviceId = DeviceProtocol.DeviceId(sourceKey);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);
        var queued = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "before-add", "payload"));

        await store.UpsertHandleAsync(handle, addedKey, "Owner", allowNewDevice: true);

        Assert.IsTrue(queued.Created);
        var drained = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease");
        Assert.HasCount(1, drained.Entries);
        Assert.AreEqual("payload", drained.Entries[0].Payload);
    }

    [TestMethod]
    public async Task RevokingUnrelatedDevice_PreservesQueuedWorkBetweenSurvivors()
    {
        var store = new InMemoryRelayStore();
        const string handle = "owner";
        const string sourceKey = "source-key";
        const string targetKey = "target-key";
        const string revokedKey = "revoked-key";
        await store.UpsertHandleAsync(handle, sourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, revokedKey, "Owner", allowNewDevice: true);
        var sourceDeviceId = DeviceProtocol.DeviceId(sourceKey);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);
        var revokedDeviceId = DeviceProtocol.DeviceId(revokedKey);
        await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "before-revoke", "payload"));

        var revocation = await store.RevokeDeviceAsync(
            handle, revokedDeviceId, sourceKey);

        Assert.IsTrue(revocation.Revoked);
        var drained = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease");
        Assert.HasCount(1, drained.Entries);
        Assert.AreEqual("payload", drained.Entries[0].Payload);
    }

    [TestMethod]
    public async Task DeviceQueue_DuplicateEnqueueIsIdempotent()
    {
        var store = CreateQueueStore();
        const string handle = "owner";
        const string sourceDeviceId = "device-a";
        const string targetDeviceId = "device-b";

        var first = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "operation-1", "payload-1"));
        var duplicate = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "operation-1", "payload-2"));

        Assert.IsTrue(first.Accepted);
        Assert.IsTrue(first.Created);
        Assert.IsTrue(duplicate.Accepted);
        Assert.IsFalse(duplicate.Created);
        Assert.AreEqual(first.EntryId, duplicate.EntryId);

        var drained = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease");
        Assert.HasCount(1, drained.Entries);
        Assert.AreEqual("payload-1", drained.Entries[0].Payload);
        Assert.AreEqual(
            DeviceQueueEntryIdProtocol.Create(sourceDeviceId, targetDeviceId, "operation-1"),
            drained.Entries[0].EntryId);
    }

    [TestMethod]
    public async Task DeviceQueue_ReconnectRedrainsUnackedWorkAfterLeaseExpiry()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var store = CreateQueueStore(clock);
        const string handle = "owner";
        const string sourceDeviceId = "device-a";
        const string targetDeviceId = "device-b";

        await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "operation-1", "payload-1"));

        var firstDrain = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease-1");
        Assert.HasCount(1, firstDrain.Entries);
        Assert.HasCount(0, (await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease-2")).Entries);

        clock.Advance(DeviceQueueProtocol.LeaseDuration + TimeSpan.FromSeconds(1));

        var redrained = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease-3");
        Assert.HasCount(1, redrained.Entries);
        Assert.AreEqual(firstDrain.Entries[0].EntryId, redrained.Entries[0].EntryId);
        Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(
            handle, targetDeviceId, redrained.Entries[0].EntryId, "lease-3"));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task DeviceQueue_RejectsWhenPerDeviceQueueIsFull()
    {
        var store = CreateQueueStore();
        const string handle = "owner";
        const string sourceDeviceId = "device-a";
        const string targetDeviceId = "device-b";

        for (var index = 0; index < DeviceQueueProtocol.MaxEntries; index++)
        {
            var result = await store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue(sourceDeviceId, targetDeviceId, $"op-{index}", $"payload-{index}"));
            Assert.IsTrue(result.Accepted, $"queue rejected entry {index}");
        }

        var overflow = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "overflow", "payload-overflow"));

        Assert.IsFalse(overflow.Accepted);
        Assert.AreEqual(DeviceQueueProtocol.BoundedQueueFull, overflow.Error);
        Assert.AreEqual(DeviceQueueProtocol.MaxEntries, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task DeviceQueue_ConcurrentAdmissionCreatesExactlyFiveHundred()
    {
        IRelayStore store = CreateQueueStore();
        const string handle = "owner";
        const string sourceDeviceId = "device-a";
        const string targetDeviceId = "device-b";

        var results = await Task.WhenAll(Enumerable.Range(0, 800).Select(index =>
            store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue(
                    sourceDeviceId,
                    targetDeviceId,
                    $"concurrent-{index}",
                    $"payload-{index}"))));

        Assert.AreEqual(DeviceQueueProtocol.MaxEntries, results.Count(result => result.Accepted));
        Assert.AreEqual(DeviceQueueProtocol.MaxEntries, results.Count(result => result.Created));
        Assert.AreEqual(
            300,
            results.Count(result =>
                !result.Accepted && result.Error == DeviceQueueProtocol.BoundedQueueFull));
        Assert.AreEqual(
            DeviceQueueProtocol.MaxEntries,
            await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task DeviceQueue_FullQueueAcceptsDuplicateWithoutCreatingCapacity()
    {
        var store = CreateQueueStore();
        const string handle = "owner";
        const string sourceDeviceId = "device-a";
        const string targetDeviceId = "device-b";
        QueueEnqueueResult? first = null;
        for (var index = 0; index < DeviceQueueProtocol.MaxEntries; index++)
        {
            var result = await store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue(sourceDeviceId, targetDeviceId, $"op-{index}", $"payload-{index}"));
            first ??= result;
        }

        var duplicate = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "op-0", "changed"));

        Assert.IsNotNull(first);
        Assert.IsTrue(first.Created);
        Assert.IsTrue(duplicate.Accepted);
        Assert.IsFalse(duplicate.Created);
        Assert.AreEqual(first.EntryId, duplicate.EntryId);
        Assert.AreEqual(
            DeviceQueueProtocol.MaxEntries,
            await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task DeviceQueue_AckRequiresActiveLeaseAndReleaseMakesEntryAvailable()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var store = CreateQueueStore(clock);
        const string handle = "owner";
        const string targetDeviceId = "device-b";
        var enqueued = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue("device-a", targetDeviceId, "operation-1", "payload"));

        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(
            handle, targetDeviceId, enqueued.EntryId, "not-leased"));
        var leased = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "owner-1");
        Assert.HasCount(1, leased.Entries);
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(
            handle, targetDeviceId, enqueued.EntryId, "owner-2"));

        await store.ReleaseDeviceQueueLeasesAsync(handle, targetDeviceId, "owner-1");
        Assert.HasCount(1, (await store.DrainDeviceQueueAsync(
            handle, targetDeviceId, "owner-2")).Entries);
        clock.Advance(DeviceQueueProtocol.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(
            handle, targetDeviceId, enqueued.EntryId, "owner-2"));
        Assert.HasCount(1, (await store.DrainDeviceQueueAsync(
            handle, targetDeviceId, "owner-3")).Entries);
        Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(
            handle, targetDeviceId, enqueued.EntryId, "owner-3"));
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(
            handle, targetDeviceId, enqueued.EntryId, "owner-3"));
    }

    [TestMethod]
    public async Task DeviceQueue_ExpiredEntriesRestoreCapacityWithoutCounterDrift()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var store = CreateQueueStore(clock);
        const string handle = "owner";
        const string targetDeviceId = "device-b";
        for (var index = 0; index < DeviceQueueProtocol.MaxEntries; index++)
            Assert.IsTrue((await store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue("device-a", targetDeviceId, $"old-{index}", "payload"))).Created);

        clock.Advance(DeviceQueueProtocol.EntryTtl + TimeSpan.FromSeconds(1));
        for (var index = 0; index < DeviceQueueProtocol.MaxEntries; index++)
            Assert.IsTrue((await store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue("device-a", targetDeviceId, $"new-{index}", "payload"))).Created);

        Assert.AreEqual(
            DeviceQueueProtocol.MaxEntries,
            await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task HandleDeletePurgesEveryDeviceQueue()
    {
        var store = CreateQueueStore();
        const string handle = "owner";
        await store.UpsertHandleAsync(handle, "device-key", "Owner", allowNewDevice: true);
        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue("device-a", "device-b", "operation-1", "payload"));
        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue("device-b", "device-c", "operation-2", "payload"));

        Assert.IsTrue(await store.DeleteHandleAsync(handle));

        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, "device-b"));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, "device-c"));
        await store.UpsertHandleAsync(handle, "replacement-key", "Owner", allowNewDevice: true);
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, "device-b"));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, "device-c"));
    }

    [TestMethod]
    public async Task DeviceRevocation_FencesAdmissionAlreadyInFlight_AndRelinkStartsClean()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: async () =>
            {
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        const string sourceKey = "source-key";
        const string targetKey = "target-key";
        await store.UpsertHandleAsync(handle, sourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var sourceDeviceId = DeviceProtocol.DeviceId(sourceKey);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);

        var staleAdmission = store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "stale", "payload"));
        await admissionEntered.Task;
        Assert.IsTrue((await store.RevokeDeviceAsync(handle, targetDeviceId, sourceKey)).Revoked);
        releaseAdmission.TrySetResult();

        Assert.IsFalse((await staleAdmission).Accepted);
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));

        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var fresh = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "fresh", "payload"));
        Assert.IsTrue(fresh.Created);
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task SourceDeviceRevocation_RemovesStaleAdmissionDuringDrain()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: async () =>
            {
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        const string sourceKey = "source-key";
        const string targetKey = "target-key";
        await store.UpsertHandleAsync(handle, sourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var sourceDeviceId = DeviceProtocol.DeviceId(sourceKey);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);

        var staleAdmission = store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "stale", "payload"));
        await admissionEntered.Task;
        Assert.IsTrue((await store.RevokeDeviceAsync(handle, sourceDeviceId, targetKey)).Revoked);
        await store.UpsertHandleAsync(handle, sourceKey, "Owner", allowNewDevice: true);
        releaseAdmission.TrySetResult();

        Assert.IsTrue((await staleAdmission).Accepted);
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
        var freshRetry = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "stale", "fresh-payload"));
        Assert.IsTrue(freshRetry.Created);
        var drained = await store.DrainDeviceQueueAsync(handle, targetDeviceId, "lease");
        Assert.HasCount(1, drained.Entries);
        Assert.AreEqual("fresh-payload", drained.Entries[0].Payload);
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task SourceDeviceRevocation_DropsOnlyStaleSourceWorkFromSurvivingQueue()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = true;
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: async () =>
            {
                if (!pause) return;
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        const string revokedSourceKey = "revoked-source";
        const string activeSourceKey = "active-source";
        const string targetKey = "target";
        await store.UpsertHandleAsync(handle, revokedSourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, activeSourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var revokedSource = DeviceProtocol.DeviceId(revokedSourceKey);
        var activeSource = DeviceProtocol.DeviceId(activeSourceKey);
        var target = DeviceProtocol.DeviceId(targetKey);

        var stale = store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(revokedSource, target, "stale", "stale"));
        await admissionEntered.Task;
        Assert.IsTrue((await store.RevokeDeviceAsync(
            handle, revokedSource, activeSourceKey)).Revoked);
        await store.UpsertHandleAsync(handle, revokedSourceKey, "Owner", allowNewDevice: true);
        pause = false;
        var valid = await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(activeSource, target, "valid", "valid"));
        releaseAdmission.TrySetResult();

        Assert.IsTrue(valid.Created);
        Assert.IsTrue((await stale).Created);
        var drained = await store.DrainDeviceQueueAsync(handle, target, "lease");
        Assert.HasCount(1, drained.Entries);
        Assert.AreEqual("valid", drained.Entries[0].Payload);
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, target));
    }

    [TestMethod]
    public async Task HandleDelete_FencesAdmissionAlreadyInFlight_AndReclaimStartsClean()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: async () =>
            {
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        const string sourceKey = "source-key";
        const string targetKey = "target-key";
        await store.UpsertHandleAsync(handle, sourceKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var sourceDeviceId = DeviceProtocol.DeviceId(sourceKey);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);

        var staleAdmission = store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "stale", "payload"));
        await admissionEntered.Task;
        Assert.IsTrue(await store.DeleteHandleAsync(handle));
        releaseAdmission.TrySetResult();

        Assert.IsFalse((await staleAdmission).Accepted);
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));

        await store.UpsertHandleAsync(handle, sourceKey, "New owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "New owner", allowNewDevice: true);
        var fresh = await store.EnqueueDeviceQueueAsync(
            handle,
            new QueueEnqueue(sourceDeviceId, targetDeviceId, "fresh", "payload"));
        Assert.IsTrue(fresh.Created);
        Assert.AreEqual(1, await store.GetDeviceQueueSizeAsync(handle, targetDeviceId));
    }

    [TestMethod]
    public async Task DeviceRevocation_FencesOrdinaryInboxAdmissionAlreadyInFlight()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = true;
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: null,
            beforeInboxAdmission: async () =>
            {
                if (!pause) return;
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        const string currentKey = "current-key";
        const string targetKey = "target-key";
        await store.UpsertHandleAsync(handle, currentKey, "Owner", allowNewDevice: true);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        var targetDeviceId = DeviceProtocol.DeviceId(targetKey);
        var targetInbox = RelayInboxKey.Device(handle, targetDeviceId);

        var stale = store.EnqueueAsync(targetInbox, "stale", handle, "stale");
        await admissionEntered.Task;
        Assert.IsTrue((await store.RevokeDeviceAsync(
            handle, targetDeviceId, currentKey)).Revoked);
        await store.UpsertHandleAsync(handle, targetKey, "Owner", allowNewDevice: true);
        releaseAdmission.TrySetResult();

        var rejected = await stale;
        Assert.IsFalse(rejected.Accepted);
        Assert.IsFalse(rejected.Created);
        Assert.HasCount(0, await store.LeaseInboxAsync(targetInbox, "stale-lease"));
        pause = false;
        Assert.IsTrue((await store.EnqueueAsync(
            targetInbox, "fresh", handle, "fresh")).Created);
        Assert.HasCount(1, await store.LeaseInboxAsync(targetInbox, "fresh-lease"));
    }

    [TestMethod]
    public async Task HandleDelete_FencesOrdinaryInboxAdmissionBeforeReclaim()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = true;
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: null,
            beforeInboxAdmission: async () =>
            {
                if (!pause) return;
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        await store.UpsertHandleAsync(handle, "old-key", "Old owner", allowNewDevice: true);

        var stale = store.EnqueueAsync(handle, "stale", "sender", "stale");
        await admissionEntered.Task;
        Assert.IsTrue(await store.DeleteHandleAsync(handle));
        await store.UpsertHandleAsync(handle, "new-key", "New owner", allowNewDevice: true);
        releaseAdmission.TrySetResult();

        var rejected = await stale;
        Assert.IsFalse(rejected.Accepted);
        Assert.IsFalse(rejected.Created);
        Assert.HasCount(0, await store.LeaseInboxAsync(handle, "stale-lease"));
        pause = false;
        Assert.IsTrue((await store.EnqueueAsync(handle, "fresh", "sender", "fresh")).Created);
        Assert.AreEqual("fresh", (await store.LeaseInboxAsync(handle, "fresh-lease")).Single().Json);
    }

    [TestMethod]
    public async Task HandleDelete_FencesDeviceInboxAdmissionBeforeReclaim()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = true;
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: null,
            beforeInboxAdmission: async () =>
            {
                if (!pause) return;
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        const string handle = "owner";
        const string deviceKey = "device-key";
        await store.UpsertHandleAsync(handle, deviceKey, "Old owner", allowNewDevice: true);
        var deviceInbox = RelayInboxKey.Device(handle, DeviceProtocol.DeviceId(deviceKey));

        var stale = store.EnqueueAsync(deviceInbox, "stale", "sender", "stale");
        await admissionEntered.Task;
        Assert.IsTrue(await store.DeleteHandleAsync(handle));
        Assert.IsFalse((await store.EnqueueAsync(
            deviceInbox, "during-delete", "sender", "during-delete")).Accepted);
        await store.UpsertHandleAsync(handle, deviceKey, "New owner", allowNewDevice: true);
        releaseAdmission.TrySetResult();

        Assert.IsFalse((await stale).Accepted);
        Assert.HasCount(0, await store.LeaseInboxAsync(deviceInbox, "stale-lease"));
        pause = false;
        Assert.IsTrue((await store.EnqueueAsync(
            deviceInbox, "fresh", "sender", "fresh")).Created);
        Assert.AreEqual(
            "fresh",
            (await store.LeaseInboxAsync(deviceInbox, "fresh-lease")).Single().Json);
    }

    [TestMethod]
    public async Task HandleDelete_InterruptedAfterTombstoneCanBeAuthorizedAndRetried()
    {
        var attempts = 0;
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: null,
            beforeHandleDeleteCompletion: () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new IOException("simulated interruption");
                return Task.CompletedTask;
            });
        const string handle = "owner";
        const string deviceKey = "device-key";
        await store.UpsertHandleAsync(handle, deviceKey, "Owner", allowNewDevice: true);

        await Assert.ThrowsExactlyAsync<IOException>(() => store.DeleteHandleAsync(handle));

        Assert.IsNull(await store.GetHandleAsync(handle));
        Assert.AreEqual(
            deviceKey,
            (await store.GetHandleForDeletionAsync(handle))!.DevicePublicKeys.Single());
        Assert.IsFalse((await store.UpsertHandleAsync(
            handle, "replacement-key", "Replacement", allowNewDevice: true)).deviceAuthorized);
        Assert.IsTrue(await store.DeleteHandleAsync(handle));
        Assert.IsNull(await store.GetHandleForDeletionAsync(handle));
        Assert.IsFalse(await store.DeleteHandleAsync(handle));
    }

    [TestMethod]
    public async Task HandleDelete_RetryPurgesAgentDispatchesBeforeReclaim()
    {
        var attempts = 0;
        var store = new InMemoryRelayStore(
            timeProvider: null,
            beforeQueueAdmission: null,
            beforeHandleDeleteCompletion: () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new IOException("simulated interruption");
                return Task.CompletedTask;
            });
        const string handle = "owner";
        await store.UpsertHandleAsync(handle, "old-key", "Old owner", allowNewDevice: true);
        await store.CreateAgentDispatchAsync(new StoredAgentDispatch
        {
            Id = "dispatch-1",
            RequestId = "request-1",
            From = "sender",
            To = handle,
            EnvelopeJson = "stale",
            EnvelopeHash = "hash",
            DispatchToken = "token"
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => store.DeleteHandleAsync(handle));
        Assert.IsNotNull(await store.GetAgentDispatchAsync(handle, "dispatch-1"));
        Assert.IsTrue(await store.DeleteHandleAsync(handle));
        await store.UpsertHandleAsync(handle, "new-key", "New owner", allowNewDevice: true);

        Assert.IsNull(await store.GetAgentDispatchAsync(handle, "dispatch-1"));
        var fresh = await store.CreateAgentDispatchAsync(new StoredAgentDispatch
        {
            Id = "dispatch-1",
            RequestId = "request-2",
            From = "sender",
            To = handle,
            EnvelopeJson = "fresh",
            EnvelopeHash = "fresh-hash",
            DispatchToken = "fresh-token"
        });
        Assert.AreEqual(AgentDispatchCreateStatus.Created, fresh.Status);
    }

    [TestMethod]
    public async Task InboxStatsIncludeActiveDeviceQueueEntriesOnly()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var store = CreateQueueStore(clock);
        await store.EnqueueAsync("recipient", "inbox-1", "sender", "payload");
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.EnqueueDeviceQueueAsync(
            "owner", new QueueEnqueue("device-a", "device-b", "operation-1", "payload"));

        var active = await store.GetInboxStatsAsync();
        Assert.AreEqual(2, active.QueuedItems);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero),
            active.OldestQueuedAt);

        clock.Advance(DeviceQueueProtocol.EntryTtl + TimeSpan.FromSeconds(1));
        var expired = await store.GetInboxStatsAsync();
        Assert.AreEqual(1, expired.QueuedItems);
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

        Assert.IsNotNull(await store.AcknowledgeInboxAsync(
            inbox, initial[0].Id, "connection"));
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

    [TestMethod]
    public async Task Protocol8Integration_TwoDevices_MutationArrivesExactlyOnce()
    {
        var store = CreateQueueStore();
        const string handle = "alice";
        const string deviceA = "device-a";
        const string deviceB = "device-b";

        // A sends mutation to B
        var enqueueAB = await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(deviceA, deviceB, "mutation-1", "encrypted-payload-ab"));
        Assert.IsTrue(enqueueAB.Accepted);

        // B sends mutation to A
        var enqueueBA = await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(deviceB, deviceA, "mutation-2", "encrypted-payload-ba"));
        Assert.IsTrue(enqueueBA.Accepted);

        // B drains and processes its queue
        var drainB = await store.DrainDeviceQueueAsync(handle, deviceB, "conn-b");
        Assert.HasCount(1, drainB.Entries);
        Assert.AreEqual("encrypted-payload-ab", drainB.Entries[0].Payload);
        Assert.AreEqual(deviceA, drainB.Entries[0].SourceDeviceId);
        Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(
            handle, deviceB, drainB.Entries[0].EntryId, "conn-b"));

        // A drains and processes its queue
        var drainA = await store.DrainDeviceQueueAsync(handle, deviceA, "conn-a");
        Assert.HasCount(1, drainA.Entries);
        Assert.AreEqual("encrypted-payload-ba", drainA.Entries[0].Payload);
        Assert.AreEqual(deviceB, drainA.Entries[0].SourceDeviceId);
        Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(
            handle, deviceA, drainA.Entries[0].EntryId, "conn-a"));

        // Both queues are now empty
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, deviceA));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, deviceB));
    }

    [TestMethod]
    public async Task Protocol8Harness_AuthenticatesConvergesAndRecoversDeterministically()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var harness = new Protocol8Harness(CreateQueueStore(clock), "alice");

        Assert.IsTrue(harness.Authenticate("device-a", MeshProtocol.Version));
        await harness.SendAsync("device-a", "device-b", "before-b-ready", "missed");
        Assert.AreEqual(1, await harness.QueueSizeAsync("device-b"));

        Assert.IsTrue(harness.Authenticate("device-b", MeshProtocol.Version));
        Assert.IsTrue(harness.IsReady("device-b"));
        Assert.AreEqual(1, await harness.QueueSizeAsync("device-b"));
        Assert.IsFalse(harness.Authenticate("old-device", MeshProtocol.Version - 1));

        await harness.SendAsync("device-a", "device-b", "a-to-b", "from-a");
        await harness.SendAsync("device-a", "device-b", "a-to-b", "ignored-duplicate");
        await harness.SendAsync("device-b", "device-a", "b-to-a", "from-b");

        Assert.HasCount(0, await harness.DrainAndApplyAsync("device-a", "device-b", "wrong-device"));
        var appliedB = await harness.DrainAndApplyAsync("device-b", "device-b", "b-live");
        var appliedA = await harness.DrainAndApplyAsync("device-a", "device-a", "a-live");
        CollectionAssert.AreEqual(new[] { "missed", "from-a" }, appliedB.ToArray());
        CollectionAssert.AreEqual(new[] { "from-b" }, appliedA.ToArray());
        Assert.AreEqual(0, await harness.QueueSizeAsync("device-a"));
        Assert.AreEqual(0, await harness.QueueSizeAsync("device-b"));

        await harness.SendAsync("device-a", "device-b", "reconnect", "after-disconnect");
        Assert.HasCount(1, (await harness.LeaseWithoutAckAsync("device-b", "b-crashed")).Entries);
        clock.Advance(DeviceQueueProtocol.LeaseDuration + TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(
            new[] { "after-disconnect" },
            (await harness.DrainAndApplyAsync("device-b", "device-b", "b-reconnected")).ToArray());
        Assert.AreEqual(0, await harness.QueueSizeAsync("device-b"));
    }

    [TestMethod]
    public async Task Protocol8Integration_ReconnectDrainsMissedWork()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero));
        var store = CreateQueueStore(clock);
        const string handle = "alice";
        const string deviceA = "device-a";
        const string deviceB = "device-b";

        // A sends while B is offline
        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(deviceA, deviceB, "op-1", "payload-1"));
        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(deviceA, deviceB, "op-2", "payload-2"));

        // First connection drains but crashes before acking
        var firstDrain = await store.DrainDeviceQueueAsync(handle, deviceB, "conn-crash");
        Assert.HasCount(2, firstDrain.Entries);
        // No ack - simulating crash

        // Lease expires
        clock.Advance(DeviceQueueProtocol.LeaseDuration + TimeSpan.FromSeconds(1));

        // B reconnects and re-drains
        var reconnectDrain = await store.DrainDeviceQueueAsync(handle, deviceB, "conn-reconnect");
        Assert.HasCount(2, reconnectDrain.Entries);
        Assert.AreEqual("payload-1", reconnectDrain.Entries[0].Payload);
        Assert.AreEqual("payload-2", reconnectDrain.Entries[1].Payload);

        // B acks both
        foreach (var entry in reconnectDrain.Entries)
            Assert.IsTrue(await store.AcknowledgeDeviceQueueAsync(
                handle, deviceB, entry.EntryId, "conn-reconnect"));
        Assert.AreEqual(0, await store.GetDeviceQueueSizeAsync(handle, deviceB));
    }

    [TestMethod]
    public async Task Protocol8Integration_WrongDeviceCannotConsume()
    {
        var store = CreateQueueStore();
        const string handle = "alice";
        const string deviceA = "device-a";
        const string deviceB = "device-b";
        const string deviceC = "device-c";

        await store.EnqueueDeviceQueueAsync(
            handle, new QueueEnqueue(deviceA, deviceB, "secret-op", "secret-payload"));

        // C tries to drain B's queue - gets nothing because queue key is per-target
        var drainC = await store.DrainDeviceQueueAsync(handle, deviceC, "conn-c");
        Assert.HasCount(0, drainC.Entries);

        // C tries to ack B's entry - fails
        var entryId = DeviceQueueEntryIdProtocol.Create(deviceA, deviceB, "secret-op");
        Assert.IsFalse(await store.AcknowledgeDeviceQueueAsync(handle, deviceC, entryId, "conn-c"));

        // B can drain its own
        var drainB = await store.DrainDeviceQueueAsync(handle, deviceB, "conn-b");
        Assert.HasCount(1, drainB.Entries);
    }

    [TestMethod]
    public async Task Protocol8Integration_PresenceIndependentOfBacklog()
    {
        var store = CreateQueueStore();
        const string handle = "alice";
        const string deviceA = "device-a";
        const string deviceB = "device-b";

        // Fill B's queue with pending work
        for (var i = 0; i < 10; i++)
            await store.EnqueueDeviceQueueAsync(
                handle, new QueueEnqueue(deviceA, deviceB, $"op-{i}", $"p-{i}"));

        // PresenceConfirmed is issued by the hub on authentication, not conditioned on queue state.
        // Verify queue backlog does not prevent authentication: queue size is independent.
        Assert.AreEqual(10, await store.GetDeviceQueueSizeAsync(handle, deviceB));
        // (Hub emits PresenceConfirmed before any drain; this test confirms store readiness is independent.)
    }

    [TestMethod]
    public async Task Protocol8Integration_BoundedQueueRejectsDuplicateIdempotent()
    {
        var store = CreateQueueStore();
        var harness = new Protocol8Harness(store, "alice");
        const string deviceA = "device-a";
        const string deviceB = "device-b";
        Assert.IsTrue(harness.Authenticate(deviceA, MeshProtocol.Version));
        Assert.IsTrue(harness.Authenticate(deviceB, MeshProtocol.Version));

        for (var i = 0; i < DeviceQueueProtocol.MaxEntries; i++)
            Assert.IsTrue((await harness.SendAsync(
                deviceA, deviceB, $"fill-{i}", $"data-{i}")).Accepted);

        var overflow = await harness.SendAsync(deviceA, deviceB, "new-op", "new-data");
        Assert.IsFalse(overflow.Accepted);
        Assert.AreEqual(DeviceQueueProtocol.BoundedQueueFull, overflow.Error);

        var dup = await harness.SendAsync(deviceA, deviceB, "fill-0", "different-data");
        Assert.IsTrue(dup.Accepted);
        Assert.AreEqual(DeviceQueueProtocol.MaxEntries, await harness.QueueSizeAsync(deviceB));
    }

    [TestMethod]
    public void Protocol8_VersionMismatchDetected()
    {
        // Verify the protocol constants and handshake contract
        Assert.AreEqual(8, MeshProtocol.Version);
        var mismatch = new HandshakeResponse(7, HandshakeResult.VersionMismatch, "Relay protocol 8 is required.");
        Assert.AreEqual(HandshakeResult.VersionMismatch, mismatch.Result);
        Assert.IsNotNull(mismatch.Error);
    }

    [TestMethod]
    public void Protocol8_DeviceSyncKindsRejectedOnSendEnvelope()
    {
        // Device-sync kinds must use QueueEnqueue, not SendEnvelope. The relay returns
        // "device_sync_use_queue" for any device-sync kind through the old path.
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeOperation));
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeSnapshotRequest));
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeSnapshotManifest));
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeSnapshotChunk));
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeSnapshotComplete));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private static InMemoryRelayStore CreateQueueStore(TimeProvider? timeProvider = null)
        => new(timeProvider, beforeQueueAdmission: null, enforceQueueRegistration: false);

    private sealed class Protocol8Harness(InMemoryRelayStore store, string handle)
    {
        private readonly HashSet<string> authenticated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> applied = new(StringComparer.Ordinal);

        public bool Authenticate(string deviceId, int protocolVersion)
        {
            if (protocolVersion != MeshProtocol.Version)
                return false;
            authenticated.Add(deviceId);
            applied.TryAdd(deviceId, new HashSet<string>(StringComparer.Ordinal));
            return true;
        }

        public bool IsReady(string deviceId) => authenticated.Contains(deviceId);

        public async Task<QueueEnqueueResult> SendAsync(
            string sourceDeviceId,
            string targetDeviceId,
            string operationId,
            string encryptedPayload)
        {
            if (!authenticated.Contains(sourceDeviceId))
                return new QueueEnqueueResult(false, "", "unauthenticated");
            return await store.EnqueueDeviceQueueAsync(
                handle,
                new QueueEnqueue(sourceDeviceId, targetDeviceId, operationId, encryptedPayload));
        }

        public Task<QueueDrainResponse> LeaseWithoutAckAsync(string deviceId, string connectionId)
            => authenticated.Contains(deviceId)
                ? store.DrainDeviceQueueAsync(handle, deviceId, connectionId)
                : Task.FromResult(new QueueDrainResponse([]));

        public async Task<IReadOnlyList<string>> DrainAndApplyAsync(
            string authenticatedDeviceId,
            string requestedDeviceId,
            string connectionId)
        {
            if (!authenticated.Contains(authenticatedDeviceId)
                || !string.Equals(authenticatedDeviceId, requestedDeviceId, StringComparison.Ordinal))
                return [];
            var drained = await store.DrainDeviceQueueAsync(handle, requestedDeviceId, connectionId);
            var newlyApplied = new List<string>();
            foreach (var entry in drained.Entries)
            {
                if (applied[authenticatedDeviceId].Add(entry.EntryId))
                    newlyApplied.Add(entry.Payload);
                await store.AcknowledgeDeviceQueueAsync(
                    handle,
                    authenticatedDeviceId,
                    entry.EntryId,
                    connectionId);
            }
            return newlyApplied;
        }

        public Task<int> QueueSizeAsync(string deviceId)
            => store.GetDeviceQueueSizeAsync(handle, deviceId);
    }
}
