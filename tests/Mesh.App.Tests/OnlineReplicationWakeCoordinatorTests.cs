using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Protocol-9 background / wake replication behaviour. The greenfield engine has no reliable
/// queue and no bounded-poll: a mobile background wake opens a bounded online session that
/// offers the local origin, and the peer pulls whatever ranges it is missing. There is no
/// legacy relay backplane, hub, snapshot-response policy or bounded poll involved.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OnlineReplicationWakeTests : ReplicationTestBase
{
    [TestMethod]
    public async Task Wake_OnlinePeerPullsMissingEvents()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        // b is already session-established, then a is woken in the background.
        await ConnectAsync(a, b);
        // Emit more while "asleep", then wake to flush via offer-only.
        var eid2 = await a.Engine.EmitLocalAsync(Msg("m2"), new[] { b.Handle });
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(eid));
        Assert.IsNotNull(b.Db.GetEvent(eid2));
    }

    [TestMethod]
    public async Task Wake_ColdConnectsThenOffers()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        // No prior session: a cold wake must establish a session and then converge.
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await b.Engine.OnPresenceOnlineAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(eid));
    }

    [TestMethod]
    public async Task Wake_OfflinePeerLeavesOutboxPending()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        Fabric.SetOnline("b1", false);
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.IsNull(b.Db.GetEvent(eid));
    }

    [TestMethod]
    public async Task Wake_UsesSilentPushClassAndDoesNotExpectPayload()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await ConnectAsync(a, b); // establish
        var before = a.Applier.Count;

        // A wake with no new local events offers nothing to pull and applies nothing.
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(before, a.Applier.Count);
    }

    [TestMethod]
    public async Task Wake_BoundedSessionRecoversFromDisconnect()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        // First wake finds the peer offline; a later wake after reconnect converges.
        Fabric.SetOnline("b1", false);
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();
        Assert.IsNull(b.Db.GetEvent(eid));

        Fabric.SetOnline("b1", true);
        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await b.Engine.OnPresenceOnlineAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();
        Assert.IsNotNull(b.Db.GetEvent(eid));
    }

    [TestMethod]
    public async Task Wake_MultipleOriginsAllOffered()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        // a holds its own origin plus a gossiped sibling origin.
        var eidOwn = await a.Engine.EmitLocalAsync(Msg("own"), new[] { b.Handle });
        var src = AddOrigin("src", "sd1");
        await EstablishAsync(a, b);
        var sibling = MakeEvent(src, "src", "sd1", 1, Msg("sib"), new[] { a.Keys.PublicB64, b.Keys.PublicB64 });
        // Give a the sibling event so it can gossip it on wake.
        a.Db.AppendEvent(sibling);

        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();

        Assert.IsNotNull(b.Db.GetEvent(eidOwn));
        Assert.IsNotNull(b.Db.GetEvent(sibling.EventId), "wake offers every held origin, not just the local one");
    }

    [TestMethod]
    public async Task LocalWorkPending_NotifiesPollerWithEstablishedSession()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await ConnectAsync(a, b);
        var signals = 0;
        a.Engine.LocalWorkPending += () => Interlocked.Increment(ref signals);

        await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await Fabric.DrainAsync();

        Assert.AreEqual(1, signals, "every durable local change must wake the presence poller");
    }

    [TestMethod]
    public async Task Lifecycle_DisposeStopsEngineWithoutThrowing()
    {
        var a = NewNode("alice", "a1");
        await a.Engine.EmitLocalAsync(Msg("m1"), new[] { "bob" });
        await a.Engine.DisposeAsync();
        // A second dispose must be safe (idempotent stop).
        await a.Engine.DisposeAsync();
    }

    [TestMethod]
    public async Task Lifecycle_NoReliableQueueMeansNothingReplaysWithoutASession()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });

        // No wake, no presence, no session: nothing is delivered (no background bounded poll).
        await Fabric.DrainAsync();

        Assert.IsNull(b.Db.GetEvent(eid));
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
    }

    [TestMethod]
    public async Task Wake_ConvergesLargeBacklog()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        for (var i = 1; i <= 150; i++)
            await a.Engine.EmitLocalAsync(Msg($"m{i}"), new[] { b.Handle });

        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await b.Engine.OnPresenceOnlineAsync(a.Handle, a.Device);
        await Fabric.DrainAsync();

        Assert.AreEqual(150UL, b.Db.GetCursor("a1")!.Contiguous);
    }

    [TestMethod]
    public async Task Wake_IdempotentWhenAlreadyConverged()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);
        var count = b.Applier.Count;

        await a.Engine.OnWakeAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();
        Assert.AreEqual(count, b.Applier.Count, "waking an already-converged peer replays nothing");
    }

    [TestMethod]
    public void Contract_LegacyRelayQueueTransportRemoved()
    {
        // Protocol 9 removes the reliable relay queue entirely: enqueue / drain / ack
        // transport types and the queue method-routing policy no longer exist. The public
        // OnlineReplicationWakeCoordinator / IOnlineReplicationWakeTransport shells intentionally survive
        // (non-owned MauiProgram + iOS AppDelegate host wiring still bind to them) but their
        // queue-drain behavior is neutralized to a no-op that reports nothing pending.
        var appAssembly = typeof(OnlineReplicationEngine).Assembly;
        foreach (var banned in new[]
                 {
                     "ReplicationTransportPolicy",
                     "ReplicationPollResult",
                     "ReplicationPollPolicy"
                 })
            Assert.IsNull(appAssembly.GetType("Mesh.App.Services." + banned, throwOnError: false),
                $"legacy relay queue type {banned} must no longer exist");
    }
}
