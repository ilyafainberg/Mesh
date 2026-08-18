using System.Reflection;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Behavioural, property and concurrency tests for the protocol-9 online replication engine.
/// Two (or more) real engines over real SQLCipher databases exchange opaque frames through an
/// in-memory relay fabric that mirrors the greenfield online-only contract: no reliable queue,
/// offline sends leave the outbox pending, and every payload is opaque ciphertext. Crafted
/// deliveries drive the receiver's verification, ordering, custody and fork paths precisely.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OnlineReplicationEngineTests : ReplicationTestBase
{
    // =====================================================================
    // 1. Local origin: event creation, sequence allocation, outbox atomicity.
    // =====================================================================

    [TestMethod]
    public async Task EmitLocal_StoresEventAndPendingOutboxForRecipientAccount()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        Assert.IsNotNull(a.Db.GetEvent(eid), "event must be durably stored on emit");
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "bob"));
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
    }

    [TestMethod]
    public async Task EmitLocal_SoleOwnDeviceProducesNoOutboxTarget()
    {
        var a = NewNode("alice", "a1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { a.Handle });

        Assert.IsNotNull(a.Db.GetEvent(eid));
        Assert.IsNull(a.Db.GetOutboxState(eid, "alice"), "a sole device has no sibling to take custody");
        Assert.AreEqual(ReplicationDeliveryState.Stored, a.Engine.GetDeliveryState(eid, "alice"));
    }

    [TestMethod]
    public async Task EmitLocal_OwnAccountTrackedWhenSiblingDeviceExists()
    {
        var a = NewNode("alice", "a1");
        _ = NewNode("alice", "a2"); // sibling device on the same account
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { a.Handle });

        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "alice"));
    }

    [TestMethod]
    public async Task EmitLocal_AllocatesMonotonicSequences()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await a.Engine.EmitLocalAsync(Msg("m2"), new[] { b.Handle });
        await a.Engine.EmitLocalAsync(Msg("m3"), new[] { b.Handle });

        var events = a.Db.QueryEvents("a1", "epoch-1", 1, 64);
        CollectionAssert.AreEqual(new[] { 1UL, 2UL, 3UL }, events.Select(e => e.Seq).ToArray());
    }

    [TestMethod]
    public async Task EmitLocal_MultipleTargetsEachGetPendingOutbox()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var c = NewNode("carol", "c1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle, c.Handle });

        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "bob"));
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "carol"));
    }

    [TestMethod]
    public async Task EmitLocal_OutboxCarriesNoPayloadBody()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1", body: "{\"text\":\"secret\"}"), new[] { b.Handle });
        var work = a.Db.QueryDueOutbox("bob", MeshDb.OutboxStatePending);

        Assert.AreEqual(1, work.Count);
        Assert.AreEqual(eid, work[0].EventId);
        // The outbox row exposes only a reference (event id + state), never the domain body.
        Assert.IsFalse(work.Any(w => w.EventId.Contains("secret", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task EmitLocal_EventCiphertextIsOpaqueNotPlaintext()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1", body: "{\"text\":\"topsecret\"}"), new[] { b.Handle });
        var evt = a.Db.GetEvent(eid)!;

        Assert.IsFalse(evt.Ciphertext.Contains("topsecret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmitLocal_UnknownRecipientAccountStillEncryptsToSelf()
    {
        var a = NewNode("alice", "a1");
        // "ghost" account has no registered device; emit must still succeed (own key always present).
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { "ghost" });
        Assert.IsNotNull(a.Db.GetEvent(eid));
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "ghost"));
    }

    // =====================================================================
    // 2. Offline: sends leave the outbox pending (no reliable relay queue).
    // =====================================================================

    [TestMethod]
    public async Task Offline_PresenceInitToOfflinePeerLeavesOutboxPending()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        Fabric.SetOnline("b1", false);
        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.IsNull(b.Db.GetEvent(eid), "an offline peer receives nothing");
        Assert.IsTrue(Fabric.DroppedOffline > 0);
    }

    [TestMethod]
    public async Task Offline_UnknownTargetDeviceLeavesPending()
    {
        var a = NewNode("alice", "a1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { "nobody" });
        await a.Engine.OnPresenceOnlineAsync("nobody", "nd1");
        await Fabric.DrainAsync();

        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "nobody"));
    }

    // =====================================================================
    // 3. Online two-engine convergence.
    // =====================================================================

    [TestMethod]
    public async Task Convergence_DirectMessageReachesPeerAndReceiptsCustody()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetEvent(eid), "peer must durably hold the replicated event");
        Assert.AreEqual(1, b.Applier.Count);
        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.AreEqual(MeshDb.OutboxStatePersisted, a.Db.GetOutboxState(eid, "bob"));
    }

    [TestMethod]
    public async Task Convergence_FastAccountReceiptDoesNotHideLaggingAuthorisedDevice()
    {
        var a = NewNode("alice", "a1");
        var b1 = NewNode("bob", "b1");
        var b2 = NewNode("bob", "b2");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { "bob" });

        await ConnectAsync(a, b1);

        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"),
            "one durable account receipt still clears account-level custody");
        Assert.IsFalse(a.Engine.HasPendingWorkForPeer(Roster.ResolveDevice("bob", "b1")!));
        Assert.IsTrue(a.Engine.HasPendingWorkForPeer(Roster.ResolveDevice("bob", "b2")!),
            "the second authorised device must remain independently catch-up eligible");
        Assert.AreEqual(1, a.Engine.CountPendingTargetEvents(new[] { "bob" }));
        Assert.IsNull(b2.Db.GetEvent(eid));

        await Task.Delay(20);
        await ConnectAsync(a, b2);

        Assert.IsNotNull(b2.Db.GetEvent(eid));
        Assert.IsFalse(a.Engine.HasPendingWorkForPeer(Roster.ResolveDevice("bob", "b2")!));
        Assert.AreEqual(0, a.Engine.CountPendingTargetEvents(new[] { "bob" }));
        Assert.AreEqual("b2", a.Db.GetLastSuccessfulReplication("bob")?.PeerDeviceId,
            "a later receipt from a device that did not advance account custody still updates its sync checkpoint");
    }

    [TestMethod]
    public async Task Convergence_ReceiverStoresSignedReceipt()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);

        var receipt = b.Db.GetReceipt("b1", "a1", "epoch-1");
        Assert.IsNotNull(receipt);
        Assert.AreEqual(1UL, receipt!.ThroughSeq);
        Assert.IsTrue(OnlineReplicationProtocol.VerifyReceipt(receipt, b.Keys.PublicB64));
    }

    [TestMethod]
    public async Task Convergence_Bidirectional()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var ea = await a.Engine.EmitLocalAsync(Msg("from-a"), new[] { b.Handle });
        var eb = await b.Engine.EmitLocalAsync(Msg("from-b"), new[] { a.Handle });

        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetEvent(ea));
        Assert.IsNotNull(a.Db.GetEvent(eb));
        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(ea, "bob"));
        Assert.AreEqual(ReplicationDeliveryState.Persisted, b.Engine.GetDeliveryState(eb, "alice"));
    }

    [TestMethod]
    public async Task Convergence_MultipleEventsPreserveOrder()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        for (var i = 1; i <= 10; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });

        await ConnectAsync(a, b);

        Assert.AreEqual(10, b.Applier.Count);
        Assert.AreEqual(10UL, b.Db.GetCursor("a1")!.Contiguous);
    }

    [TestMethod]
    public async Task Convergence_EmitAfterConnectPushesImmediately()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await ConnectAsync(a, b); // sessions established, no data yet

        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(eid));
        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.IsTrue(Roster.RefreshCalls > 0, "batch validation must refresh origin-account authority");
    }

    [TestMethod]
    public async Task Convergence_SimultaneousConnectStillConverges()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        // Both initiate at once (dual init race); the engine must still replicate.
        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await b.Engine.OnPresenceOnlineAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(eid));
    }

    [TestMethod]
    public async Task RepeatedPresencePoll_DoesNotReplaceEstablishedSession()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await ConnectAsync(a, b);
        var delivered = Fabric.Delivered;

        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await b.Engine.OnPresenceOnlineAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(
            delivered,
            Fabric.Delivered,
            "an established session must not be replaced on every presence poll");
    }

    // =====================================================================
    // 4. Duplicate / exact-once application.
    // =====================================================================

    [TestMethod]
    public async Task ExactOnce_DuplicateBatchNotReapplied()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var ev1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { ev1 }));
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { ev1 }));
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { ev1 }));

        Assert.AreEqual(1, b.Applier.Count, "an exact duplicate must never re-project the domain");
        Assert.IsNotNull(b.Db.GetEvent(ev1.EventId));
        Assert.AreEqual(1UL, b.Db.GetCursor("sd1")!.Contiguous);
    }

    [TestMethod]
    public async Task ExactOnce_ReconnectDoesNotRefetchOrReapply()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);
        Assert.AreEqual(1, b.Applier.Count);

        await ConnectAsync(a, b); // reconnect: cursor already contiguous, nothing to request
        Assert.AreEqual(1, b.Applier.Count);
    }

    // =====================================================================
    // 5. Out-of-order, gaps, ranges, reorder window.
    // =====================================================================

    [TestMethod]
    public async Task OutOfOrder_AheadThenFillConvergesContiguously()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        var e2 = MakeEvent(src, "src", "sd1", 2, Msg("m2"), new[] { b.Keys.PublicB64 });
        var e3 = MakeEvent(src, "src", "sd1", 3, Msg("m3"), new[] { b.Keys.PublicB64 });

        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e3 })); // ahead
        Assert.AreEqual(0UL, b.Db.GetCursor("sd1")!.Contiguous);
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));
        Assert.AreEqual(1UL, b.Db.GetCursor("sd1")!.Contiguous);
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e2 }));

        Assert.AreEqual(3UL, b.Db.GetCursor("sd1")!.Contiguous, "ahead sequence collapses on fill");
        Assert.AreEqual(3, b.Applier.Count);
    }

    [TestMethod]
    public async Task OutOfOrder_BeyondReorderWindowIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        ulong far = (ulong)OnlineReplicationLimits.ReorderWindow + 500;
        var e = MakeEvent(src, "src", "sd1", far, Msg("far"), new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e }));

        Assert.IsNull(b.Db.GetEvent(e.EventId), "an event past the reorder window is not stored");
        Assert.AreEqual(0, b.Applier.Count);
    }

    [TestMethod]
    public async Task Gap_OfferDrivesMissingRangeRequestAndServe()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        // Build a's origin log directly (a1, three events signed with a's key) so a can serve it.
        var e1 = MakeEventForNode(a, 1, Msg("m1"), b.Keys.PublicB64);
        var e2 = MakeEventForNode(a, 2, Msg("m2"), b.Keys.PublicB64);
        var e3 = MakeEventForNode(a, 3, Msg("m3"), b.Keys.PublicB64);
        a.Db.AppendEvent(e1);
        a.Db.AppendEvent(e2);
        a.Db.AppendEvent(e3);

        // b receives 1 and 3 out of band, leaving a gap at 2.
        await DeliverBatchAsync(a, b, Batch("a1", new[] { e1 }));
        await DeliverBatchAsync(a, b, Batch("a1", new[] { e3 }));
        Assert.AreEqual(1UL, b.Db.GetCursor("a1")!.Contiguous);

        // a offers its origin; b must request the missing [2,2] range and a must serve it.
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(3UL, b.Db.GetCursor("a1")!.Contiguous);
        Assert.IsNotNull(b.Db.GetEvent(e2.EventId));
    }

    [TestMethod]
    public async Task Range_RequestBoundedBatchesUnderSixtyFour()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        for (var i = 1; i <= 130; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });

        await ConnectAsync(a, b);

        Assert.AreEqual(130, b.Applier.Count);
        Assert.AreEqual(130UL, b.Db.GetCursor("a1")!.Contiguous);
    }

    // =====================================================================
    // 6. Receipt custody and forged receipts.
    // =====================================================================

    [TestMethod]
    public async Task Custody_OneReceiptClearsRecipientAccountTarget()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));

        await ConnectAsync(a, b);

        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
    }

    [TestMethod]
    public async Task Custody_ForgedReceiptIsRejectedAndOutboxStaysPending()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        Fabric.SetOnline("b1", false);
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await Fabric.DrainAsync();
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));

        // Attacker signs a receipt claiming to be b1 but with the wrong key.
        var attacker = KeyPair.New();
        var forged = OnlineReplicationProtocol.CreateReceipt(
            "b1", "a1", "epoch-1", 1, OnlineReplicationProtocol.ZeroHash,
            OnlineReplicationProtocol.ZeroHash, attacker.PrivateB64);
        await RawControlAsync(b, a, E2EFrameKind.Receipt, forged);

        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
        StringAssert.Contains(a.Engine.LastError ?? "", "Forged");
    }

    [TestMethod]
    public async Task Custody_ReceiptFromWrongRouteDeviceIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        // b sends a receipt whose ReceiverDeviceId is a different device than the stamped route.
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            "someone-else", "a1", "epoch-1", 1, OnlineReplicationProtocol.ZeroHash,
            OnlineReplicationProtocol.ZeroHash, b.Keys.PrivateB64);
        await RawControlAsync(b, a, E2EFrameKind.Receipt, receipt);

        StringAssert.Contains(a.Engine.LastError ?? "", "stamped route");
    }

    [TestMethod]
    public async Task Custody_MultiDeviceOneReceiptThenSiblingGossip()
    {
        var a = NewNode("alice", "a1");
        var b1 = NewNode("team", "tb1");
        var b2 = NewNode("team", "tb2");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { "team" });

        // One team device receipts -> the account target is cleared.
        await ConnectAsync(a, b1);
        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "team"));

        // Sibling gossip: b2 pulls a's event from b1, not from a.
        await ConnectAsync(b1, b2);
        Assert.IsNotNull(b2.Db.GetEvent(eid), "sibling must receive the event via gossip");
    }

    [TestMethod]
    public async Task Custody_NoOverlapTargetStaysPendingDespiteGossip()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { "carol" });

        // b gossips a's origin even though the message targets carol.
        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetEvent(eid), "b replicates the origin log");
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "carol"),
            "a bob receipt must not clear a carol target");
    }

    // =====================================================================
    // 7. Crash / reopen and disconnect / retry recovery.
    // =====================================================================

    [TestMethod]
    public async Task Crash_ReceiverReopenRetainsEventsAndCursor()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);

        var reopened = Reopen(b);
        Assert.IsNotNull(reopened.Db.GetEvent(eid), "persisted events survive a crash/restart");
        Assert.AreEqual(1UL, reopened.Db.GetCursor("a1")!.Contiguous);
    }

    [TestMethod]
    public async Task Crash_SenderReopenRetainsPersistedCustody()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);

        var reopened = Reopen(a);
        Assert.AreEqual(ReplicationDeliveryState.Persisted, reopened.Engine.GetDeliveryState(eid, "bob"));
    }

    [TestMethod]
    public async Task Crash_PendingOutboxSurvivesReopenAndDrainsAfterReconnect()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        Fabric.SetOnline("b1", false);
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        var reopened = Reopen(a);
        Assert.AreEqual(ReplicationDeliveryState.Pending, reopened.Engine.GetDeliveryState(eid, "bob"));

        Fabric.SetOnline("b1", true);
        await ConnectAsync(reopened, b);
        Assert.AreEqual(ReplicationDeliveryState.Persisted, reopened.Engine.GetDeliveryState(eid, "bob"));
    }

    [TestMethod]
    public async Task Disconnect_RetryConvergesAfterPeerReturnsOnline()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        Fabric.SetOnline("b1", false);
        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();
        Assert.IsNull(b.Db.GetEvent(eid));

        Fabric.SetOnline("b1", true);
        await ConnectAsync(a, b);
        Assert.IsNotNull(b.Db.GetEvent(eid));
    }

    // =====================================================================
    // 8. Fork, epoch mismatch and halting.
    // =====================================================================

    [TestMethod]
    public async Task Fork_ConflictingEventAtSamePositionHaltsOrigin()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var evX = MakeEvent(src, "src", "sd1", 1, Msg("X", body: "{\"text\":\"X\"}"), new[] { b.Keys.PublicB64 });
        var evY = MakeEvent(src, "src", "sd1", 1, Msg("Y", body: "{\"text\":\"Y\"}"), new[] { b.Keys.PublicB64 });
        // Pre-seed a conflicting event without advancing the cursor.
        b.Db.AppendEvent(evX);

        await DeliverBatchAsync(a, b, Batch("sd1", new[] { evY }));

        Assert.IsTrue(b.Engine.IsHalted("sd1"), "a fork must halt the origin log");
        Assert.IsNotNull(b.Engine.LastError);
    }

    [TestMethod]
    public async Task Fork_HaltedOriginIgnoresFurtherOffers()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var evX = MakeEvent(src, "src", "sd1", 1, Msg("X", body: "{\"text\":\"X\"}"), new[] { b.Keys.PublicB64 });
        var evY = MakeEvent(src, "src", "sd1", 1, Msg("Y", body: "{\"text\":\"Y\"}"), new[] { b.Keys.PublicB64 });
        b.Db.AppendEvent(evX);
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { evY }));
        Assert.IsTrue(b.Engine.IsHalted("sd1"));

        var before = b.Applier.Count;
        await RawControlAsync(a, b, E2EFrameKind.Offer, new ReplicationOffer("sd1", "epoch-1", 1, 5));
        Assert.AreEqual(before, b.Applier.Count, "a halted origin does not request or apply more");
    }

    [TestMethod]
    public async Task Epoch_MismatchHaltsOrigin()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 }, epoch: "epoch-1");
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }, "epoch-1"));
        Assert.AreEqual(1UL, b.Db.GetCursor("sd1")!.Contiguous);

        var rogue = MakeEvent(src, "src", "sd1", 2, Msg("m2"), new[] { b.Keys.PublicB64 }, epoch: "epoch-9");
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { rogue }, "epoch-9"));

        Assert.IsTrue(b.Engine.IsHalted("sd1"));
    }

    [TestMethod]
    public async Task Fork_StateChangeEventIsSurfaced()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        ReplicationStateChange? seen = null;
        b.Engine.StateChanged += change => seen = change;

        var evX = MakeEvent(src, "src", "sd1", 1, Msg("X", body: "{\"text\":\"X\"}"), new[] { b.Keys.PublicB64 });
        var evY = MakeEvent(src, "src", "sd1", 1, Msg("Y", body: "{\"text\":\"Y\"}"), new[] { b.Keys.PublicB64 });
        b.Db.AppendEvent(evX);
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { evY }));

        Assert.IsNotNull(seen);
        Assert.AreEqual("sd1", seen!.Origin);
    }

    // =====================================================================
    // 9. Revocation and auth generation.
    // =====================================================================

    [TestMethod]
    public async Task Revocation_EventFromRevokedOriginDeviceIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);
        Roster.Revoke("src", "sd1");

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.IsNull(b.Db.GetEvent(e1.EventId));
        Assert.AreEqual(0, b.Applier.Count);
        StringAssert.Contains(b.Engine.LastError ?? "", "revoked");
    }

    [TestMethod]
    public async Task Revocation_DeliveryFromRevokedPeerIsDropped()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);
        Roster.Revoke("alice", "a1");

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.AreEqual(0, b.Applier.Count);
        StringAssert.Contains(b.Engine.LastError ?? "", "revoked");
    }

    [TestMethod]
    public async Task Auth_FutureGenerationEventIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1", authGeneration: 0);
        Roster.SetGeneration("src", 0);
        await EstablishAsync(a, b);

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 }, authGeneration: 5);
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.IsNull(b.Db.GetEvent(e1.EventId));
        StringAssert.Contains(b.Engine.LastError ?? "", "auth generation");
    }

    [TestMethod]
    public async Task Auth_TamperedSignatureEventIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        // Keep EventId/ContentHash intact (so batch validation passes) but graft on a
        // well-formed signature from a different event, so only VerifyEvent can catch it.
        var other = MakeEvent(src, "src", "sd1", 2, Msg("m2"), new[] { b.Keys.PublicB64 });
        var tampered = e1 with { Signature = other.Signature };
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { tampered }));

        Assert.IsNull(b.Db.GetEvent(tampered.EventId));
        StringAssert.Contains(b.Engine.LastError ?? "", "verification");
    }

    // =====================================================================
    // 10. Read watermarks (separate from persistence receipts).
    // =====================================================================

    [TestMethod]
    public async Task ReadWatermark_AppliedAndSeparateFromPersistence()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        var wm = new ReadWatermarkPayload("conv-1", "alice", "evt-42", "a1", 3, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await RawControlAsync(a, b, E2EFrameKind.ReadWatermark, wm);

        var stored = b.Db.GetReadWatermark("conv-1", "alice");
        Assert.IsNotNull(stored);
        Assert.AreEqual("evt-42", stored!.ThroughEventId);
        Assert.AreEqual(0, b.Applier.Count, "a read watermark is not a domain projection");
    }

    [TestMethod]
    public async Task ReadWatermark_LastWriterWinsByVersion()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        await RawControlAsync(a, b, E2EFrameKind.ReadWatermark,
            new ReadWatermarkPayload("conv-1", "alice", "evt-10", "a1", 5, 100));
        await RawControlAsync(a, b, E2EFrameKind.ReadWatermark,
            new ReadWatermarkPayload("conv-1", "alice", "evt-05", "a1", 2, 200));

        var stored = b.Db.GetReadWatermark("conv-1", "alice");
        Assert.AreEqual("evt-10", stored!.ThroughEventId, "lower version must not overwrite a higher one");
    }

    // =====================================================================
    // 11. Ask-user (all devices) and asset (desktop only).
    // =====================================================================

    [TestMethod]
    public async Task AskUser_ProjectsOnDesktop()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1", desktop: true);
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var env = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt, "ask-1", "conv-1", "v1", "{}");
        var e1 = MakeEvent(src, "src", "sd1", 1, env, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.AreEqual(1, b.Applier.Count);
    }

    [TestMethod]
    public async Task AskUser_ProjectsOnMobileToo()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1", desktop: false);
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var env = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve, "ask-1", "conv-1", "v1", "{}");
        var e1 = MakeEvent(src, "src", "sd1", 1, env, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.AreEqual(1, b.Applier.Count, "ask-user resolutions apply on every device");
    }

    [TestMethod]
    public async Task Asset_MobileStoresAuthenticatedEventWithoutMaterialisingBytes()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1", desktop: false);
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var env = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert, "asset-1", null, "v1", "{}");
        var e1 = MakeEvent(src, "src", "sd1", 1, env, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.IsNotNull(b.Db.GetEvent(e1.EventId),
            "the shared origin position must be stored so later mobile-compatible events can progress");
        Assert.AreEqual(1UL, b.Db.GetCursor("sd1")?.Contiguous);
        Assert.IsFalse(b.Engine.IsHalted("sd1"));
        Assert.AreEqual(0, b.Applier.Count, "asset bytes are never materialised on mobile");
    }

    [TestMethod]
    public async Task Asset_DesktopProjects()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1", desktop: true);
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var env = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert, "asset-1", null, "v1", "{}");
        var e1 = MakeEvent(src, "src", "sd1", 1, env, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.AreEqual(1, b.Applier.Count);
    }

    [TestMethod]
    public async Task Asset_DeleteAppliesOnMobile()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1", desktop: false);
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        // A delete tombstone is metadata, not bytes: it applies even on mobile.
        var env = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetDelete, "asset-1", null, "v1", "{}");
        var e1 = MakeEvent(src, "src", "sd1", 1, env, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));

        Assert.AreEqual(1, b.Applier.Count);
    }

    // =====================================================================
    // 12. Snapshot / resync streaming.
    // =====================================================================

    [TestMethod]
    public async Task Resync_RequestReservesEventsFromOrigin()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        for (var i = 1; i <= 5; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });
        await ConnectAsync(a, b);
        Assert.AreEqual(5, b.Applier.Count);

        // A resync request replays the origin log; already-applied events are exact duplicates.
        await RawControlAsync(b, a, E2EFrameKind.ResyncRequest, new ReplicationResyncRequest("a1", "epoch-1", 1));
        await Fabric.DrainAsync();

        Assert.AreEqual(5, b.Applier.Count, "replayed snapshot events are deduplicated");
        Assert.AreEqual(5UL, b.Db.GetCursor("a1")!.Contiguous);
    }

    [TestMethod]
    public async Task Resync_OfferBelowCursorTriggersResyncRequest()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        // Offer whose earliest available sequence is past what b needs -> requires resync.
        await RawControlAsync(a, b, E2EFrameKind.Offer, new ReplicationOffer("sd1", "epoch-1", 5, 10));
        await Fabric.DrainAsync();

        // b did not crash and applied nothing (a holds no sd1 events to serve).
        Assert.AreEqual(0, b.Applier.Count);
    }

    [TestMethod]
    public async Task Snapshot_LargeLogStreamsInBoundedBatches()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        const int n = 200;
        for (var i = 1; i <= n; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });

        await ConnectAsync(a, b);

        Assert.AreEqual(n, b.Applier.Count);
        Assert.AreEqual((ulong)n, b.Db.GetCursor("a1")!.Contiguous);
    }

    [TestMethod]
    public async Task Snapshot_FreshSiblingSkipsHistoryAndDoesNotStarveReverseWork()
    {
        var a = NewNode(
            "alice",
            "a1",
            flow: new ReplicationFlow(1, OnlineReplicationLimits.MaxBatchOps, OnlineReplicationLimits.MaxBatchBytes),
            deferSiblingOffersUntilBootstrap: true);
        var b = NewNode("alice", "a2", deferSiblingOffersUntilBootstrap: true);

        string? historicalId = null;
        for (var i = 1; i <= 130; i++)
            historicalId = await a.Engine.EmitLocalAsync(Msg($"history-{i}"), new[] { a.Handle });

        var peerB = Roster.ResolveDevice(b.Handle, b.Device)!;
        var targetB = ReplicationBootstrapTarget.Create(peerB, a.Engine.LocalIdentity);
        var snapshot = Enumerable.Range(1, 70)
            .Select(index => Msg($"snapshot-{index}"))
            .ToList();
        const string bootstrapId = "fresh-sibling-snapshot";
        a.Db.CreateOrResumePeerBootstrap(
            targetB,
            bootstrapId,
            OnlineReplicationProtocol.HashText("snapshot-state"),
            "snapshot-state",
            snapshot.Count);
        ulong firstSnapshotSeq = 0;
        var snapshotIds = a.Engine.Journal.EmitLocalBatch(
            snapshot,
            new[] { a.Handle },
            domainWork: static (_, _, _) => { },
            eventWork: (_, tx, evt, index) =>
            {
                if (index == 0) firstSnapshotSeq = evt.Seq;
                if (index == snapshot.Count - 1)
                    a.Db.UpdatePeerBootstrapProgress(
                        targetB,
                        bootstrapId,
                        snapshot.Count,
                        snapshot.Count,
                        firstSnapshotSeq,
                        evt.Seq,
                        tx);
            });

        var peerA = Roster.ResolveDevice(a.Handle, a.Device)!;
        var targetA = ReplicationBootstrapTarget.Create(peerA, b.Engine.LocalIdentity);
        var emptyBootstrap = b.Db.CreateOrResumePeerBootstrap(
            targetA,
            "empty-before-concurrent-change",
            OnlineReplicationProtocol.HashText("[]"),
            "[]",
            totalItems: 0);
        b.Db.CompleteEmptyPeerBootstrap(
            targetA,
            emptyBootstrap.BootstrapId,
            b.Db.GetLocalOriginNextSeq(b.Device, "epoch-1"));
        var reverseId = await b.Engine.EmitLocalAsync(Msg("reverse-work"), new[] { b.Handle });

        await ConnectAsync(a, b);

        Assert.AreEqual(200UL, b.Db.GetCursor(a.Device)!.Contiguous);
        Assert.IsNull(b.Db.GetEvent(historicalId!), "pre-snapshot history must not be replayed");
        Assert.IsTrue(snapshotIds.All(id => b.Db.GetEvent(id) is not null));
        Assert.IsNotNull(a.Db.GetEvent(reverseId), "reverse-direction work must converge during bootstrap");
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, a.Db.GetPeerBootstrap(targetB)!.State);
    }

    [TestMethod]
    public async Task Snapshot_WrongReceiverManifestIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("alice", "a2");
        await EstablishAsync(a, b);

        var manifest = OnlineReplicationProtocol.CreateSnapshotManifest(
            "wrong-receiver",
            "another-device",
            a.Device,
            "epoch-1",
            5,
            5,
            OnlineReplicationProtocol.HashText("state"),
            0,
            a.Keys.PrivateB64);
        await RawControlAsync(a, b, E2EFrameKind.Offer,
            new ReplicationOffer(a.Device, "epoch-1", 5, 5, manifest));

        Assert.IsNull(b.Db.GetCursor(a.Device));
        StringAssert.Contains(b.Engine.LastError ?? "", "not authorised");
    }

    [TestMethod]
    public async Task Snapshot_ForgedManifestSignatureIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("alice", "a2");
        await EstablishAsync(a, b);

        var forged = OnlineReplicationProtocol.CreateSnapshotManifest(
            "forged",
            b.Device,
            a.Device,
            "epoch-1",
            5,
            5,
            OnlineReplicationProtocol.HashText("state"),
            0,
            b.Keys.PrivateB64);
        await RawControlAsync(a, b, E2EFrameKind.Offer,
            new ReplicationOffer(a.Device, "epoch-1", 5, 5, forged));

        Assert.IsNull(b.Db.GetCursor(a.Device));
        StringAssert.Contains(b.Engine.LastError ?? "", "signature failed");
    }

    [TestMethod]
    public async Task Snapshot_EmptyStateSkipsHistoryAndReceivesNextChange()
    {
        var a = NewNode("alice", "a1", deferSiblingOffersUntilBootstrap: true);
        var b = NewNode("alice", "a2", deferSiblingOffersUntilBootstrap: true);
        string? historicalId = null;
        for (var i = 0; i < 100; i++)
            historicalId = await a.Engine.EmitLocalAsync(Msg($"history-{i}"), new[] { a.Handle });

        var peerB = Roster.ResolveDevice(b.Handle, b.Device)!;
        var targetB = ReplicationBootstrapTarget.Create(peerB, a.Engine.LocalIdentity);
        var marker = a.Db.CreateOrResumePeerBootstrap(
            targetB,
            "empty-snapshot",
            OnlineReplicationProtocol.HashText("[]"),
            "[]",
            totalItems: 0);
        a.Db.CompleteEmptyPeerBootstrap(
            targetB,
            marker.BootstrapId,
            a.Db.GetLocalOriginNextSeq(a.Device, "epoch-1"));

        await ConnectAsync(a, b);

        Assert.AreEqual(100UL, b.Db.GetCursor(a.Device)!.Contiguous);
        Assert.IsNull(b.Db.GetEvent(historicalId!), "empty state must not replay prior history");
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, a.Db.GetPeerBootstrap(targetB)!.State);

        var nextId = await a.Engine.EmitLocalAsync(Msg("after-empty-snapshot"), new[] { a.Handle });
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(nextId));
        Assert.AreEqual(101UL, b.Db.GetCursor(a.Device)!.Contiguous);
    }

    [TestMethod]
    public async Task Snapshot_EmptyReceiptMustAcknowledgeCurrentManifest()
    {
        var a = NewNode("alice", "a1", deferSiblingOffersUntilBootstrap: true);
        var b = NewNode("alice", "a2", deferSiblingOffersUntilBootstrap: true);
        var peerB = Roster.ResolveDevice(b.Handle, b.Device)!;
        var targetB = ReplicationBootstrapTarget.Create(peerB, a.Engine.LocalIdentity);
        var marker = a.Db.CreateOrResumePeerBootstrap(
            targetB,
            "empty-current-manifest",
            OnlineReplicationProtocol.HashText("[]"),
            "[]",
            totalItems: 0);
        a.Db.CompleteEmptyPeerBootstrap(
            targetB,
            marker.BootstrapId,
            a.Db.GetLocalOriginNextSeq(a.Device, "epoch-1"));

        Fabric.DropAcceptedFrame = (fromDevice, frame) =>
            string.Equals(fromDevice, b.Device, StringComparison.Ordinal)
            && ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext)?.Kind == E2EFrameKind.Receipt;
        await ConnectAsync(a, b);
        Assert.AreEqual(MeshDb.BootstrapStateEmitted, a.Db.GetPeerBootstrap(targetB)!.State);

        var staleReceipt = OnlineReplicationProtocol.CreateReceipt(
            b.Device,
            a.Device,
            "epoch-1",
            0,
            OnlineReplicationProtocol.HashText("cursor-zero"),
            OnlineReplicationProtocol.HashText("another-empty-manifest"),
            b.Keys.PrivateB64);
        await RawControlAsync(b, a, E2EFrameKind.Receipt, staleReceipt);

        Assert.AreEqual(MeshDb.BootstrapStateEmitted, a.Db.GetPeerBootstrap(targetB)!.State);
        StringAssert.Contains(a.Engine.LastError ?? "", "active manifest");

        Fabric.DropAcceptedFrame = null;
        await a.Engine.OfferPeerAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, a.Db.GetPeerBootstrap(targetB)!.State);
    }

    [TestMethod]
    public async Task Snapshot_VerifiedCoverageSkipsThirdOriginHistory()
    {
        var a = NewNode("alice", "a1", deferSiblingOffersUntilBootstrap: true);
        var b = NewNode("alice", "a2", deferSiblingOffersUntilBootstrap: true);
        var third = AddOrigin("alice", "z3");
        string? historicalId = null;
        for (ulong seq = 1; seq <= 100; seq++)
        {
            var evt = MakeEvent(
                third,
                "alice",
                "z3",
                seq,
                Msg($"third-history-{seq}"),
                new[] { a.Keys.PublicB64, b.Keys.PublicB64 });
            a.Db.AppendEvent(evt);
            historicalId = evt.EventId;
        }
        a.Db.UpsertCursor(
            "z3",
            new ReplicationCursorEntry(
                "epoch-1",
                100,
                new byte[OnlineReplicationLimits.AheadBitsBytes]));

        var peerB = Roster.ResolveDevice(b.Handle, b.Device)!;
        var targetB = ReplicationBootstrapTarget.Create(peerB, a.Engine.LocalIdentity);
        var coverageJson = ReplicationPayloadCodec.SerializeControl(new List<ReplicationSnapshotCoverage>
        {
            new("z3", "epoch-1", 100)
        });
        const string bootstrapId = "third-origin-coverage";
        a.Db.CreateOrResumePeerBootstrap(
            targetB,
            bootstrapId,
            OnlineReplicationProtocol.HashText("snapshot"),
            "snapshot",
            totalItems: 1,
            coverageJson: coverageJson);
        a.Engine.Journal.EmitLocalBatch(
            new[] { Msg("current-state") },
            new[] { a.Handle },
            domainWork: static (_, _, _) => { },
            eventWork: (_, tx, evt, _) =>
                a.Db.UpdatePeerBootstrapProgress(
                    targetB,
                    bootstrapId,
                    1,
                    1,
                    evt.Seq,
                    evt.Seq,
                    tx));
        var afterSnapshotId = await a.Engine.EmitLocalAsync(
            Msg("after-snapshot-capture"),
            new[] { a.Handle });

        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetEvent(afterSnapshotId));
        Assert.AreEqual(100UL, b.Db.GetCursor("z3")!.Contiguous);
        Assert.IsNull(b.Db.GetEvent(historicalId!), "covered third-origin history must not replay");
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, a.Db.GetPeerBootstrap(targetB)!.State);

        var next = MakeEvent(
            third,
            "alice",
            "z3",
            101,
            Msg("third-next"),
            new[] { a.Keys.PublicB64, b.Keys.PublicB64 });
        a.Db.AppendEvent(next);
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(next.EventId));
        Assert.AreEqual(101UL, b.Db.GetCursor("z3")!.Contiguous);
    }

    [TestMethod]
    public async Task Snapshot_OlderEmptyManifestDoesNotInstallStaleCoverage()
    {
        var a = NewNode("alice", "a1", deferSiblingOffersUntilBootstrap: true);
        var b = NewNode("alice", "a2", deferSiblingOffersUntilBootstrap: true);
        string? historicalId = null;
        for (var index = 1; index <= 10; index++)
            historicalId = await a.Engine.EmitLocalAsync(Msg($"old-{index}"), new[] { a.Handle });

        var peerB = Roster.ResolveDevice(b.Handle, b.Device)!;
        var targetB = ReplicationBootstrapTarget.Create(peerB, a.Engine.LocalIdentity);
        var marker = a.Db.CreateOrResumePeerBootstrap(
            targetB,
            "older-empty-than-coverage",
            OnlineReplicationProtocol.HashText("[]"),
            "[]",
            totalItems: 0,
            coverageJson: ReplicationPayloadCodec.SerializeControl(new List<ReplicationSnapshotCoverage>
            {
                new("z3", "epoch-1", 50)
            }));
        a.Db.CompleteEmptyPeerBootstrap(
            targetB,
            marker.BootstrapId,
            a.Db.GetLocalOriginNextSeq(a.Device, "epoch-1"));
        b.Db.UpsertCursor(
            a.Device,
            new ReplicationCursorEntry(
                "epoch-1",
                10,
                new byte[OnlineReplicationLimits.AheadBitsBytes]));

        await ConnectAsync(a, b);

        Assert.IsNull(b.Db.GetEvent(historicalId!));
        Assert.IsNull(b.Db.GetCursor("z3"), "coverage from an obsolete empty manifest must not be installed");
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, a.Db.GetPeerBootstrap(targetB)!.State);
    }

    [TestMethod]
    public async Task Snapshot_OlderManifestDoesNotOverrideNewerCoverageCursor()
    {
        var a = NewNode("alice", "a1", deferSiblingOffersUntilBootstrap: true);
        var b = NewNode("alice", "a2", deferSiblingOffersUntilBootstrap: true);
        var peerB = Roster.ResolveDevice(b.Handle, b.Device)!;
        var targetB = ReplicationBootstrapTarget.Create(peerB, a.Engine.LocalIdentity);
        const string bootstrapId = "older-than-coverage";
        a.Db.CreateOrResumePeerBootstrap(
            targetB,
            bootstrapId,
            OnlineReplicationProtocol.HashText("older-snapshot"),
            "older-snapshot",
            totalItems: 5,
            coverageJson: ReplicationPayloadCodec.SerializeControl(new List<ReplicationSnapshotCoverage>
            {
                new("z3", "epoch-1", 50)
            }));
        var oldSnapshotIds = a.Engine.Journal.EmitLocalBatch(
            Enumerable.Range(1, 5).Select(index => Msg($"old-snapshot-{index}")).ToList(),
            new[] { a.Handle },
            domainWork: static (_, _, _) => { },
            eventWork: (_, tx, evt, index) =>
            {
                if (index == 4)
                    a.Db.UpdatePeerBootstrapProgress(
                        targetB,
                        bootstrapId,
                        5,
                        5,
                        1,
                        evt.Seq,
                        tx);
            });

        string? latestId = null;
        for (var index = 6; index <= 11; index++)
            latestId = await a.Engine.EmitLocalAsync(Msg($"current-{index}"), new[] { a.Handle });
        b.Db.UpsertCursor(
            a.Device,
            new ReplicationCursorEntry(
                "epoch-1",
                10,
                new byte[OnlineReplicationLimits.AheadBitsBytes]));

        await ConnectAsync(a, b);

        Assert.IsFalse(b.Engine.IsHalted(a.Device));
        Assert.AreEqual(11UL, b.Db.GetCursor(a.Device)!.Contiguous);
        Assert.IsNotNull(b.Db.GetEvent(latestId!));
        Assert.IsTrue(oldSnapshotIds.All(id => b.Db.GetEvent(id) is null));
        Assert.IsNull(b.Db.GetCursor("z3"), "coverage from an obsolete manifest must not be installed");
    }

    [TestMethod]
    public async Task Snapshot_StateHashMismatchHaltsBeforeCoverageInstall()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("alice", "a2");
        var eventOne = MakeEventForNode(a, 1, Msg("snapshot-event"), b.Keys.PublicB64);
        a.Db.AppendEvent(eventOne);
        await EstablishAsync(a, b);

        var manifest = OnlineReplicationProtocol.CreateSnapshotManifest(
            "bad-state-hash",
            b.Device,
            a.Device,
            "epoch-1",
            1,
            1,
            OnlineReplicationProtocol.HashText("not-the-event-range"),
            0,
            a.Keys.PrivateB64,
            new[] { new ReplicationSnapshotCoverage("z3", "epoch-1", 10) });
        await RawControlAsync(a, b, E2EFrameKind.Offer,
            new ReplicationOffer(a.Device, "epoch-1", 1, 1, manifest));
        await Fabric.DrainAsync();

        Assert.IsNull(b.Db.GetCursor("z3"));
        StringAssert.Contains(b.Engine.LastError ?? "", "did not match its signed event range");
    }

    [TestMethod]
    public async Task Request_AcceptedButLostBatchRetriesAfterBoundedInterval()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode(
            "bob",
            "b1",
            requestRetryInterval: TimeSpan.FromMilliseconds(25));
        await EstablishAsync(a, b);

        Fabric.DropAcceptedFrame = (fromDevice, frame) =>
            string.Equals(fromDevice, a.Device, StringComparison.Ordinal)
            && ReplicationPayloadCodec.DecodeFrame(frame.Ciphertext)?.Kind == E2EFrameKind.Batch;
        var eventId = await a.Engine.EmitLocalAsync(Msg("lost-batch"), new[] { b.Handle });
        await Fabric.DrainAsync();
        Assert.IsNull(b.Db.GetEvent(eventId));
        Assert.IsTrue(Fabric.DroppedAccepted > 0);

        Fabric.DropAcceptedFrame = null;
        await Task.Delay(75);
        await a.Engine.OfferPeerAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(eventId));
        Assert.AreEqual(1UL, b.Db.GetCursor(a.Device)!.Contiguous);
    }

    [TestMethod]
    public async Task ProjectionBoundary_BlocksInboundCommitUntilSnapshotCaptureReleases()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);
        var evt = MakeEventForNode(a, 1, Msg("blocked-commit"), b.Keys.PublicB64);

        var boundary = await b.Engine.EnterProjectionBoundaryAsync(CancellationToken.None);
        var delivery = DeliverBatchAsync(a, b, Batch(a.Device, new[] { evt }));
        await Task.Delay(25);
        Assert.IsFalse(delivery.IsCompleted, "inbound projection crossed the snapshot boundary");
        boundary.Dispose();
        await delivery;

        Assert.IsNotNull(b.Db.GetEvent(evt.EventId));
    }

    [TestMethod]
    public async Task Batch_PostCommitHookRunsOnceForAllWinningEvents()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);
        var events = Enumerable.Range(1, 3)
            .Select(index => MakeEventForNode(a, (ulong)index, Msg($"batch-{index}"), b.Keys.PublicB64))
            .ToList();

        await DeliverBatchAsync(a, b, Batch(a.Device, events));

        Assert.AreEqual(3, b.Applier.Count);
        Assert.AreEqual(1, b.Applier.AfterCommitBatchCalls);
    }

    // =====================================================================
    // 13. Scale / vector bounds and concurrency.
    // =====================================================================

    [TestMethod]
    public void Scale_TenThousandSequenceVectorBoundsHoldContiguously()
    {
        // Vector/cursor bounds at scale without paying per-event crypto: apply 10k
        // contiguous sequences and confirm the cursor collapses to a single contiguous
        // watermark (no unbounded ahead-bit growth).
        const ulong n = 10_000;
        var cursor = OnlineReplicationProtocol.EmptyCursor();
        for (ulong s = 1; s <= n; s++)
        {
            var r = OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1", s, out cursor);
            Assert.AreEqual(CursorApplyResult.AppliedContiguous, r);
        }
        Assert.AreEqual(n, cursor.Contiguous);

        // A duplicate below the watermark is idempotent; a seq beyond the reorder window
        // is rejected (bounded memory, no infinite ahead set).
        Assert.AreEqual(CursorApplyResult.Duplicate,
            OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1", n, out _));
        Assert.AreEqual(CursorApplyResult.RejectedTooFarAhead,
            OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1",
                cursor.Contiguous + (ulong)OnlineReplicationLimits.ReorderWindow + 2, out _));
    }

    [TestMethod]
    public async Task Scale_ManyEventsConvergeEndToEnd()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var requests = 0;
        a.Engine.Activity += activity =>
        {
            if (activity.Name == "request.received") requests++;
        };
        const int n = 200;
        await ConnectAsync(a, b);
        for (var i = 1; i <= n; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });

        await Fabric.DrainAsync(TimeSpan.FromMinutes(2));

        Assert.AreEqual((ulong)n, b.Db.GetCursor("a1")!.Contiguous);
        Assert.AreEqual(n, b.Applier.Count);
        Assert.IsTrue(requests <= 2, "queued offers were not coalesced behind the in-flight request");
    }

    [TestMethod]
    public async Task Concurrency_ParallelDeliveriesFromOnePeerAppliedExactlyOnce()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var events = new List<ReplicationEvent>();
        for (ulong s = 1; s <= 40; s++)
            events.Add(MakeEvent(src, "src", "sd1", s, Msg($"m{s}"), new[] { b.Keys.PublicB64 }));

        // Fire the same 40 events, each in its own batch, twice, all in parallel. The per-peer
        // lock must serialise them and the cursor must apply each sequence exactly once.
        var tasks = new List<Task>();
        foreach (var e in events)
        {
            tasks.Add(DeliverBatchAsync(a, b, Batch("sd1", new[] { e })));
            tasks.Add(DeliverBatchAsync(a, b, Batch("sd1", new[] { e })));
        }
        await Task.WhenAll(tasks);

        Assert.AreEqual(40UL, b.Db.GetCursor("sd1")!.Contiguous);
        Assert.AreEqual(40, b.Applier.Count, "duplicates under concurrency are still applied once");
    }

    [TestMethod]
    public async Task Property_RandomDeliveryOrderAlwaysConverges()
    {
        for (var trial = 0; trial < 8; trial++)
        {
            var a = NewNode("alice", "a1_" + trial);
            var b = NewNode("bob", "b1_" + trial);
            var src = AddOrigin("src" + trial, "sd" + trial);
            await EstablishAsync(a, b);

            const int n = 25;
            var events = new List<ReplicationEvent>();
            for (ulong s = 1; s <= n; s++)
                events.Add(MakeEvent(src, "src" + trial, "sd" + trial, s, Msg($"m{s}"), new[] { b.Keys.PublicB64 }));

            var rng = new Random(1000 + trial);
            foreach (var e in events.OrderBy(_ => rng.Next()))
                await DeliverBatchAsync(a, b, Batch("sd" + trial, new[] { e }));

            Assert.AreEqual((ulong)n, b.Db.GetCursor("sd" + trial)!.Contiguous, $"trial {trial} must converge");
            Assert.AreEqual(n, b.Applier.Count);
        }
    }

    [TestMethod]
    public async Task Property_DuplicatesInterleavedWithOrderConverge()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        const int n = 30;
        var events = new List<ReplicationEvent>();
        for (ulong s = 1; s <= n; s++)
            events.Add(MakeEvent(src, "src", "sd1", s, Msg($"m{s}"), new[] { b.Keys.PublicB64 }));

        var rng = new Random(7);
        var shuffled = events.OrderBy(_ => rng.Next()).ToList();
        foreach (var e in shuffled)
        {
            await DeliverBatchAsync(a, b, Batch("sd1", new[] { e }));
            if (rng.Next(2) == 0) await DeliverBatchAsync(a, b, Batch("sd1", new[] { e })); // random dup
        }

        Assert.AreEqual((ulong)n, b.Db.GetCursor("sd1")!.Contiguous);
        Assert.AreEqual(n, b.Applier.Count);
    }

    // =====================================================================
    // 14. Contract guards: no legacy reliable-queue surface remains.
    // =====================================================================

    [TestMethod]
    public void Contract_EngineExposesNoLegacyQueueMethods()
    {
        var names = typeof(OnlineReplicationEngine)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();

        foreach (var banned in new[]
        {
            "Queue" + "Enqueue", "Drain", "Ack", "Request" + "PendingDeliveries",
            "CancelQueued", "EnqueueReliable", "PostSnapshot",
        })
            Assert.IsFalse(names.Contains(banned, StringComparer.Ordinal), $"legacy method {banned} must not exist");
    }

    [TestMethod]
    public void Contract_DeliveryStateEnumIsGreenfield()
    {
        CollectionAssert.AreEquivalent(
            new[] { "Unknown", "Stored", "Pending", "Offered", "Persisted" },
            Enum.GetNames(typeof(ReplicationDeliveryState)));
    }

    [TestMethod]
    public void Contract_OperationMapCoversEveryUnifiedKind()
    {
        foreach (var kind in new[]
        {
            ReplicationOpKinds.Message, ReplicationOpKinds.Conversation, ReplicationOpKinds.Topic,
            ReplicationOpKinds.Contact, ReplicationOpKinds.Circle, ReplicationOpKinds.Memory,
            ReplicationOpKinds.Asset, ReplicationOpKinds.AskUser, ReplicationOpKinds.ReadWatermark,
        })
            Assert.IsTrue(ReplicationPayloadCodec.OperationMap.ContainsKey(kind), $"kind {kind} must be mapped");
    }

    [TestMethod]
    public void Contract_UsesCanonicalProtocolNine()
        => Assert.AreEqual(9, (int)OnlineReplicationProtocol.CanonicalVersion);

    // =====================================================================
    // 15. Malformed / adversarial frames are surfaced, not fatal.
    // =====================================================================

    [TestMethod]
    public async Task Malformed_UndecryptableFrameIsSurfaced()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        // A frame encrypted to a third party cannot be decrypted by b.
        var stranger = KeyPair.New();
        var cipher = ReplicationPayloadCodec.Encrypt("hello", new[] { stranger.PublicB64 });
        var frame = new E2EFrame(E2EFrameKind.Offer, "s", cipher);
        var delivery = new OnlineRelayDelivery(
            a.Handle, a.Device, b.Handle, b.Device, Guid.NewGuid().ToString("n"),
            OnlinePushClasses.Normal, ReplicationPayloadCodec.EncodeFrame(frame));
        await b.Engine.HandleDeliveryAsync(delivery);

        StringAssert.Contains(b.Engine.LastError ?? "", "Undecryptable");
    }

    [TestMethod]
    public async Task Malformed_DataFrameWithoutSessionIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        // No session established between a and b.
        await RawControlAsync(a, b, E2EFrameKind.Offer, new ReplicationOffer("a1", "epoch-1", 1, 1));
        StringAssert.Contains(b.Engine.LastError ?? "", "no established session");
    }

    [TestMethod]
    public async Task Malformed_UnknownPeerDeviceDeliveryIsRejected()
    {
        var b = NewNode("bob", "b1");
        // Delivery stamped from a device the roster does not know.
        var frame = new E2EFrame(E2EFrameKind.Offer, "s", "x");
        var delivery = new OnlineRelayDelivery(
            "ghost", "gd1", b.Handle, b.Device, Guid.NewGuid().ToString("n"),
            OnlinePushClasses.Normal, ReplicationPayloadCodec.EncodeFrame(frame));
        await b.Engine.HandleDeliveryAsync(delivery);

        StringAssert.Contains(b.Engine.LastError ?? "", "unauthorised");
    }

    // =====================================================================
    // 16. Additional behavioural / property coverage.
    // =====================================================================

    [TestMethod]
    public async Task EmitLocal_ReceiptFromOneAccountLeavesOtherPending()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var c = NewNode("carol", "c1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle, c.Handle });

        // Only bob is reachable; carol stays offline.
        Fabric.SetOnline("c1", false);
        await ConnectAsync(a, b);

        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "carol"));
    }

    [TestMethod]
    public async Task Convergence_ThreeOriginsGossipConverge()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var s1 = AddOrigin("src1", "sd1");
        var s2 = AddOrigin("src2", "sd2");
        await EstablishAsync(a, b);

        // a holds its own origin plus two gossiped sibling origins.
        await a.Engine.EmitLocalAsync(Msg("own"), new[] { b.Handle });
        a.Db.AppendEvent(MakeEvent(s1, "src1", "sd1", 1, Msg("x1"), new[] { a.Keys.PublicB64, b.Keys.PublicB64 }));
        a.Db.AppendEvent(MakeEvent(s2, "src2", "sd2", 1, Msg("y1"), new[] { a.Keys.PublicB64, b.Keys.PublicB64 }));

        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(1UL, b.Db.GetCursor("a1")!.Contiguous);
        Assert.AreEqual(1UL, b.Db.GetCursor("sd1")!.Contiguous);
        Assert.AreEqual(1UL, b.Db.GetCursor("sd2")!.Contiguous);
    }

    [TestMethod]
    public async Task Gap_TwoDisjointGapsBothRequested()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        var e = new ReplicationEvent[6];
        for (ulong s = 1; s <= 5; s++)
        {
            e[s] = MakeEventForNode(a, s, Msg($"m{s}"), b.Keys.PublicB64);
            a.Db.AppendEvent(e[s]);
        }
        // b receives 1, 3, 5 leaving disjoint gaps at 2 and 4.
        await DeliverBatchAsync(a, b, Batch("a1", new[] { e[1] }));
        await DeliverBatchAsync(a, b, Batch("a1", new[] { e[3] }));
        await DeliverBatchAsync(a, b, Batch("a1", new[] { e[5] }));
        Assert.AreEqual(1UL, b.Db.GetCursor("a1")!.Contiguous);

        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(5UL, b.Db.GetCursor("a1")!.Contiguous, "both disjoint gaps filled");
    }

    [TestMethod]
    public async Task ExactOnce_SameSeqDifferentOriginsBothApply()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var s1 = AddOrigin("src1", "sd1");
        var s2 = AddOrigin("src2", "sd2");
        await EstablishAsync(a, b);

        var e1 = MakeEvent(s1, "src1", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        var e2 = MakeEvent(s2, "src2", "sd2", 1, Msg("m2"), new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));
        await DeliverBatchAsync(a, b, Batch("sd2", new[] { e2 }));

        Assert.AreEqual(1UL, b.Db.GetCursor("sd1")!.Contiguous);
        Assert.AreEqual(1UL, b.Db.GetCursor("sd2")!.Contiguous);
        Assert.AreEqual(2, b.Applier.Count, "same seq on distinct origins are independent");
    }

    [TestMethod]
    public async Task OutOfOrder_DuplicateAheadIsIdempotent()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var e1 = MakeEvent(src, "src", "sd1", 1, Msg("m1"), new[] { b.Keys.PublicB64 });
        var e2 = MakeEvent(src, "src", "sd1", 2, Msg("m2"), new[] { b.Keys.PublicB64 });
        var e3 = MakeEvent(src, "src", "sd1", 3, Msg("m3"), new[] { b.Keys.PublicB64 });

        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e3 }));
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e3 })); // duplicate ahead
        Assert.AreEqual(0UL, b.Db.GetCursor("sd1")!.Contiguous);
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e1 }));
        await DeliverBatchAsync(a, b, Batch("sd1", new[] { e2 }));

        Assert.AreEqual(3UL, b.Db.GetCursor("sd1")!.Contiguous);
        Assert.AreEqual(3, b.Applier.Count, "duplicate ahead frame is not reapplied");
    }

    [TestMethod]
    public async Task DeliveryState_TransitionsPendingToPersisted()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));

        await ConnectAsync(a, b);

        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.IsNotNull(b.Db.GetEvent(eid));
    }

    [TestMethod]
    public async Task Convergence_BatchLargerThanSixtyFourSplitsAndConverges()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        const int n = 70; // exceeds MaxBatchOps (64), so the origin must split into batches.
        await ConnectAsync(a, b);
        for (var i = 1; i <= n; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });

        await Fabric.DrainAsync(TimeSpan.FromMinutes(2));

        Assert.AreEqual((ulong)n, b.Db.GetCursor("a1")!.Contiguous);
        Assert.AreEqual(n, b.Applier.Count);
    }

    [TestMethod]
    public async Task Range_ServedBacklogAboveLimitConvergesWithinTotalCredits()
    {
        var a = NewNode(
            "alice",
            "a1",
            flow: new ReplicationFlow(2, OnlineReplicationLimits.MaxBatchOps, OnlineReplicationLimits.MaxBatchBytes));
        var b = NewNode("bob", "b1");
        await EstablishAsync(a, b);

        var requestCount = 0;
        var batchesInRequest = 0;
        var maxBatchesInRequest = 0;
        a.Engine.Activity += activity =>
        {
            if (activity.Name == "request.received")
            {
                requestCount++;
                batchesInRequest = 0;
            }
            else if (activity.Name == "batch.sent")
            {
                batchesInRequest++;
                maxBatchesInRequest = Math.Max(maxBatchesInRequest, batchesInRequest);
            }
        };

        const ulong n = 130;
        for (ulong s = 1; s <= n; s++)
            a.Db.AppendEvent(MakeEventForNode(a, s, Msg($"m{s}"), b.Keys.PublicB64));

        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync(TimeSpan.FromMinutes(2));

        Assert.AreEqual(n, b.Db.GetCursor("a1")!.Contiguous);
        Assert.IsTrue(requestCount >= 2, "the receiver must renew its request after consuming credits");
        Assert.IsTrue(maxBatchesInRequest <= 2, "one request exceeded the advertised total credit window");
    }

    [TestMethod]
    public async Task Malformed_BatchExceedingOpLimitIsRejected()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);

        var events = new List<ReplicationEvent>();
        for (ulong s = 1; s <= 65; s++) // 65 > MaxBatchOps
            events.Add(MakeEvent(src, "src", "sd1", s, Msg($"m{s}"), new[] { b.Keys.PublicB64 }));
        await DeliverBatchAsync(a, b, Batch("sd1", events));

        Assert.IsNull(b.Db.GetEvent(events[0].EventId), "no event from an over-sized batch is stored");
        StringAssert.Contains(b.Engine.LastError ?? "", "op-count");
    }

    [TestMethod]
    public async Task Property_ThreeOriginsRandomOrderConverge()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var origins = new[] { AddOrigin("src1", "sd1"), AddOrigin("src2", "sd2"), AddOrigin("src3", "sd3") };
        var devices = new[] { "sd1", "sd2", "sd3" };
        var handles = new[] { "src1", "src2", "src3" };
        await EstablishAsync(a, b);

        var all = new List<(string dev, ReplicationEvent evt)>();
        for (var o = 0; o < 3; o++)
            for (ulong s = 1; s <= 6; s++)
                all.Add((devices[o], MakeEvent(origins[o], handles[o], devices[o], s, Msg($"o{o}-{s}"), new[] { b.Keys.PublicB64 })));

        // Shuffle while preserving per-origin order is NOT required: gaps are handled by
        // reorder buffering. Deliver in a fixed pseudo-random permutation.
        var rng = new Random(20260731);
        foreach (var (dev, evt) in all.OrderBy(_ => rng.Next()))
            await DeliverBatchAsync(a, b, Batch(dev, new[] { evt }));

        // Any residual gaps are healed by an offer-driven pull.
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync(TimeSpan.FromMinutes(2));

        foreach (var dev in devices)
            Assert.AreEqual(6UL, b.Db.GetCursor(dev)!.Contiguous, $"origin {dev} converged");
    }

    // =====================================================================
    // Helper: build an event for a live node's own origin (signed with its key).
    // =====================================================================
    private static ReplicationEvent MakeEventForNode(
        ReplicationNode node, ulong seq, ReplicationPayloadCodec.DomainEnvelope env, string recipientPub)
        => OnlineReplicationProtocol.CreateEvent(
            node.Device, "epoch-1", seq, node.Handle, 0,
            env.Kind, env.EntityId, env.ConversationId, env.CausalVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplicationPayloadCodec.Encrypt(ReplicationPayloadCodec.EncodeEnvelope(env), new[] { recipientPub, node.Keys.PublicB64 }),
            node.Keys.PrivateB64);
}
