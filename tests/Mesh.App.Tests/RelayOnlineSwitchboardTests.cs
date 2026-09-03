using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Relay.LiveFaults;
using Mesh.Relay.Observability;
using Mesh.Relay.Push;
using Mesh.Relay.RateLimiting;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Greenfield Protocol 9 online-only relay switchboard tests. The relay is an authenticated opaque
/// forwarder: it authenticates a device against handle metadata, stamps sender identity from the
/// connection, and forwards an opaque encrypted frame to the target's live socket(s) right now, or
/// answers not_online (optionally emitting a contentless push wake). It NEVER persists a message,
/// sync, attachment or agent payload; there is no offline mailbox, lease, ack, assembler or routing store.
///
/// These tests drive the real <see cref="MeshHub"/> and <see cref="MeshRouter"/> through a fake
/// SignalR surface (no socket) so the exact handshake, delivery, fanout, offline NACK, size/rate
/// bounds, transient backplane, contentless push and zero-persistence guarantees are all exercised.
/// </summary>
[TestClass]
public sealed class RelayOnlineSwitchboardTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Handshake (exact Protocol 9) ---------------------------------------

    [TestMethod]
    public async Task Connect_RejectsNonProtocol9WithVersionMismatch()
    {
        var node = RelayNode.Create();
        var (handle, deviceId, keys) = await node.RegisterAsync("owner");
        var rec = (await node.Store.GetHandleAsync(handle))!;

        var conn = node.NewConnection(handle, deviceId, keys, rec.AuthGeneration, rec.CustodyHead, protocolVersion: 8);
        await conn.Hub.OnConnectedAsync();

        Assert.IsTrue(conn.Aborted, "a non-v9 client must be aborted");
        Assert.IsNull(node.Registry.Get(conn.ConnectionId), "no connection state before a valid handshake");
        var handshake = conn.Received[0];
        Assert.AreEqual(MeshHubProtocol.Handshake, handshake.Method);
        var response = (HandshakeResponse)handshake.Args[0]!;
        Assert.AreEqual(HandshakeResult.VersionMismatch, response.Result);
        Assert.AreEqual(MeshProtocol.Version, response.ServerVersion);
    }

    [TestMethod]
    public async Task Connect_RejectsUnknownHandleBeforePresence()
    {
        var node = RelayNode.Create();
        var keys = KeyPair.New();
        var deviceId = DeviceProtocol.DeviceId(keys.PublicB64);

        var conn = node.NewConnection("ghost", deviceId, keys, authGeneration: 0, custodyHead: "");
        await conn.Hub.OnConnectedAsync();

        Assert.IsTrue(conn.Aborted);
        Assert.IsNull(node.Registry.Get(conn.ConnectionId));
        Assert.IsNull(await node.Backplane.GetInstanceForAsync("ghost"), "presence must not be set for an unknown handle");
    }

    [TestMethod]
    public async Task Connect_RejectsUnauthorizedDeviceBeforePresence()
    {
        var node = RelayNode.Create();
        var (handle, _, _) = await node.RegisterAsync("owner");
        var strangerKeys = KeyPair.New();
        var strangerDeviceId = DeviceProtocol.DeviceId(strangerKeys.PublicB64);
        var rec = (await node.Store.GetHandleAsync(handle))!;

        var conn = node.NewConnection(handle, strangerDeviceId, strangerKeys, rec.AuthGeneration, rec.CustodyHead);
        await conn.Hub.OnConnectedAsync();

        Assert.IsTrue(conn.Aborted, "a device that is not authorized under the handle must be aborted");
        Assert.IsNull(node.Registry.Get(conn.ConnectionId));
    }

    [TestMethod]
    public async Task Connect_RejectsStaleAuthGenerationOrCustodyHead()
    {
        var node = RelayNode.Create();
        var (handle, deviceId, keys) = await node.RegisterAsync("owner");
        var rec = (await node.Store.GetHandleAsync(handle))!;

        var staleGen = node.NewConnection(handle, deviceId, keys, rec.AuthGeneration + 5, rec.CustodyHead);
        await staleGen.Hub.OnConnectedAsync();
        Assert.IsTrue(staleGen.Aborted, "a mismatched auth generation must be rejected");

        var staleCustody = node.NewConnection(handle, deviceId, keys, rec.AuthGeneration, "tampered-head");
        await staleCustody.Hub.OnConnectedAsync();
        Assert.IsTrue(staleCustody.Aborted, "a mismatched custody head must be rejected");
    }

    [TestMethod]
    public async Task Handshake_ValidSignatureSetsPresenceAndConfirms()
    {
        var node = RelayNode.Create();
        var conn = await node.ConnectAsync("owner");

        var state = node.Registry.Get(conn.ConnectionId);
        Assert.IsNotNull(state);
        Assert.IsTrue(state!.Authenticated);
        Assert.IsFalse(conn.Aborted);
        Assert.IsNotNull(await node.Backplane.GetInstanceForAsync(conn.Handle));
        Assert.IsNotNull(await node.Backplane.GetInstanceForDeviceAsync(conn.Handle, conn.DeviceId));

        var confirmed = conn.Received.Single(m => m.Method == MeshHubProtocol.PresenceConfirmed);
        var payload = (PresenceConfirmed)confirmed.Args[0]!;
        Assert.AreEqual(conn.Handle, payload.Handle);
        Assert.AreEqual(conn.DeviceId, payload.DeviceId);
        Assert.IsTrue(payload.ExpiresAt > payload.ConnectedAt, "presence is ephemeral with a TTL");
    }

    [TestMethod]
    public async Task Handshake_RejectsSignatureOverWrongChallenge()
    {
        var node = RelayNode.Create();
        var (handle, deviceId, keys) = await node.RegisterAsync("owner");
        var rec = (await node.Store.GetHandleAsync(handle))!;
        var conn = node.NewConnection(handle, deviceId, keys, rec.AuthGeneration, rec.CustodyHead);
        await conn.Hub.OnConnectedAsync();

        // Sign a canonical string with the wrong nonce; the hub must reject it.
        var forged = RelayConnectChallenge.Canonical(
            "not-the-nonce", handle, deviceId, MeshProtocol.Version, rec.AuthGeneration, rec.CustodyHead);
        await conn.Hub.Authenticate(keys.PublicB64, Sign(keys.PrivateB64, forged));

        Assert.IsTrue(conn.Aborted);
        var state = node.Registry.Get(conn.ConnectionId);
        Assert.IsFalse(state?.Authenticated == true, "presence must not be set without a valid signature");
    }

    // ---- Online delivery ----------------------------------------------------

    [TestMethod]
    public async Task Relay_DirectedFrameDeliveredOnlineAndSenderStamped()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        var target = await node.ConnectAsync("bob");

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            ToHandle: "bob",
            ToDevice: target.DeviceId,
            FrameId: "frame-1",
            PushClass: OnlinePushClasses.Normal,
            Ciphertext: "opaque-ciphertext"));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.Delivered, result.Code);

        var delivery = target.LastDelivery();
        Assert.IsNotNull(delivery);
        Assert.AreEqual("alice", delivery!.FromHandle);
        Assert.AreEqual(sender.DeviceId, delivery.FromDevice);
        Assert.AreEqual("bob", delivery.ToHandle);
        Assert.AreEqual(target.DeviceId, delivery.ToDevice);
        Assert.AreEqual("opaque-ciphertext", delivery.Ciphertext);
        Assert.AreEqual(1, node.Metrics.Snapshot().OnlineDelivered);
    }

    [TestMethod]
    public async Task Relay_IgnoresAnySpoofedSenderAndStampsAuthenticatedIdentity()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        var target = await node.ConnectAsync("bob");

        // The client frame has no sender fields at all (spoof resistance is structural): whatever the
        // caller puts in the frame, the delivery is stamped from the authenticated connection only.
        await sender.Hub.Relay(new OnlineRelayFrame("bob", target.DeviceId, "f", OnlinePushClasses.Normal, "c"));

        var delivery = target.LastDelivery()!;
        Assert.AreEqual("alice", delivery.FromHandle);
        Assert.AreEqual(sender.DeviceId, delivery.FromDevice);
        Assert.AreNotEqual("bob", delivery.FromHandle, "the target can never appear as the sender");
    }

    [TestMethod]
    public async Task Relay_AccountFrameFansOutToAllOnlineAuthorizedDevices()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");

        var (handle, device1, keys1) = await node.RegisterAsync("bob");
        var device2Keys = KeyPair.New();
        await node.AddDeviceAsync(handle, device2Keys);
        var target1 = await node.ConnectExistingAsync(handle, device1, keys1);
        var target2 = await node.ConnectExistingAsync(handle, DeviceProtocol.DeviceId(device2Keys.PublicB64), device2Keys);

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", ToDevice: null, "fan-1", OnlinePushClasses.Normal, "c"));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.Delivered, result.Code);
        Assert.IsNotNull(target1.LastDelivery());
        Assert.IsNotNull(target2.LastDelivery());
        Assert.AreEqual(2, node.Metrics.Snapshot().OnlineDelivered);
    }

    [TestMethod]
    public async Task ResolvePresence_ReportsOnlineDevices()
    {
        var node = RelayNode.Create();
        var asker = await node.ConnectAsync("alice");
        var bob = await node.ConnectAsync("bob");

        var snapshot = await asker.Hub.ResolvePresence(new[] { "bob", "nobody" });

        var bobPresence = snapshot.Handles.Single(h => h.Handle == "bob");
        Assert.IsTrue(bobPresence.Online);
        CollectionAssert.Contains(bobPresence.Devices.ToArray(), bob.DeviceId);
        Assert.IsFalse(snapshot.Handles.Single(h => h.Handle == "nobody").Online);
    }

    // ---- Offline NACK + zero persistence ------------------------------------

    [TestMethod]
    public async Task Relay_OfflineTargetReturnsNotOnlineAndPersistsNothing()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        await node.RegisterAsync("bob"); // registered but never connects

        var rec = (await node.Store.GetHandleAsync("bob"))!;
        var bobDevice = DeviceProtocol.DeviceId(rec.DevicePublicKeys[0]);

        var directed = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", bobDevice, "frame-off", OnlinePushClasses.Normal, "c"));
        Assert.IsFalse(directed.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.NotOnline, directed.Code);

        var account = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", null, "frame-off-2", OnlinePushClasses.Normal, "c"));
        Assert.AreEqual(OnlineRelaySendCodes.NotOnline, account.Code);

        Assert.IsTrue(node.Metrics.Snapshot().OfflineNacks >= 2);
        // Nothing about the frame was written: the metadata record is unchanged and holds no payload.
        var after = (await node.Store.GetHandleAsync("bob"))!;
        Assert.AreEqual(rec.AuthGeneration, after.AuthGeneration);
        Assert.AreEqual(0, after.DevicePushTokens.Count);
    }

    [TestMethod]
    public async Task Relay_UnknownTargetHandleIsNotOnline()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "does-not-exist", null, "f", OnlinePushClasses.Normal, "c"));

        Assert.AreEqual(OnlineRelaySendCodes.NotOnline, result.Code);
    }

    [TestMethod]
    public async Task Relay_DirectedToUnknownDeviceIsTargetDeviceUnknown()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        await node.ConnectAsync("bob");

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", "deadbeefdead", "f", OnlinePushClasses.Normal, "c"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.TargetDeviceUnknown, result.Code);
    }

    [TestMethod]
    public async Task Relay_RevokedSenderDeviceIsRejected()
    {
        var node = RelayNode.Create();
        // Give alice two devices so the sender's device can be revoked (last device cannot be revoked).
        var (handle, device1, keys1) = await node.RegisterAsync("alice");
        var device2Keys = KeyPair.New();
        await node.AddDeviceAsync(handle, device2Keys);
        var sender = await node.ConnectExistingAsync(handle, device1, keys1);

        await node.Store.RevokeDeviceAsync(handle, device1);

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "alice", null, "f", OnlinePushClasses.Normal, "c"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.DeviceRevoked, result.Code);
    }

    // ---- Size + rate bounds -------------------------------------------------

    [TestMethod]
    public async Task Relay_OversizeCiphertextIsRejected()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        await node.ConnectAsync("bob");

        var tooBig = new string('a', OnlineReplicationLimits.MaxTransportBytes + 1);
        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", null, "big", OnlinePushClasses.Normal, tooBig));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.TooLarge, result.Code);
        Assert.AreEqual(1, node.Metrics.Snapshot().FramesRejectedTooLarge);
    }

    [TestMethod]
    public async Task Relay_RateLimitBoundsAFloodOfDirectFrames()
    {
        var node = RelayNode.Create(rateLimiter: TightRateLimiter());
        var sender = await node.ConnectAsync("alice");
        var target = await node.ConnectAsync("bob");

        var first = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", target.DeviceId, "r1", OnlinePushClasses.Normal, "c"));
        var second = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", target.DeviceId, "r2", OnlinePushClasses.Normal, "c"));

        Assert.IsTrue(first.Accepted);
        Assert.IsFalse(second.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.RateLimited, second.Code);
        Assert.IsTrue(second.RetryAfterMs is > 0);
        Assert.IsTrue(node.Metrics.Snapshot().RateLimitRejections >= 1);
    }

    [TestMethod]
    public async Task Relay_RateLimitedFrameRetry_IsNeverReportedDeliveredByDedup()
    {
        var node = RelayNode.Create(rateLimiter: TightRateLimiter());
        var sender = await node.ConnectAsync("alice");
        var target = await node.ConnectAsync("bob");

        await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", target.DeviceId, "consume-token", OnlinePushClasses.Normal, "c"));
        var frame = new OnlineRelayFrame(
            "bob", target.DeviceId, "retry-same-id", OnlinePushClasses.Normal, "c");

        var first = await sender.Hub.Relay(frame);
        var retry = await sender.Hub.Relay(frame);

        Assert.IsFalse(first.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.RateLimited, first.Code);
        Assert.IsFalse(retry.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.RateLimited, retry.Code);
        Assert.AreEqual(1, target.DeliveryCount());
    }

    [TestMethod]
    public async Task Relay_DuplicateFrameIdWithinWindowIsNotReForwarded()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        var target = await node.ConnectAsync("bob");

        var first = await sender.Hub.Relay(new OnlineRelayFrame("bob", target.DeviceId, "dup", OnlinePushClasses.Normal, "c"));
        var second = await sender.Hub.Relay(new OnlineRelayFrame("bob", target.DeviceId, "dup", OnlinePushClasses.Normal, "c"));

        Assert.AreEqual(OnlineRelaySendCodes.Delivered, first.Code);
        Assert.AreEqual(OnlineRelaySendCodes.Delivered, second.Code, "a duplicate is treated as already handled");
        Assert.AreEqual(1, target.DeliveryCount(), "the frame is forwarded to the target exactly once");
    }

    // ---- Transient backplane (cross-instance) -------------------------------

    [TestMethod]
    public async Task Relay_CrossInstanceForwardsThroughTransientBackplane()
    {
        var store = new InMemoryRelayStore();
        var (nodeA, nodeB) = RelayNode.CreateLinkedPair(store);

        var sender = await nodeA.ConnectAsync("alice");
        var (handle, deviceId, keys) = await nodeB.RegisterAsync("bob");
        var target = await nodeB.ConnectExistingAsync(handle, deviceId, keys);

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", target.DeviceId, "x-frame", OnlinePushClasses.Normal, "opaque"));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.Delivered, result.Code);
        var delivery = target.LastDelivery();
        Assert.IsNotNull(delivery);
        Assert.AreEqual("opaque", delivery!.Ciphertext);
        Assert.AreEqual(1, nodeA.Metrics.Snapshot().BackplaneForwards, "the forward is one directed transient pub/sub, no store");
    }

    // ---- Contentless push wake ----------------------------------------------

    [TestMethod]
    public void PushWake_PayloadIsContentlessSyncV9()
    {
        var payload = ApnsPushSender.SerializeWakePayload(PushWakeMode.SyncOnly, "wake-1");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.AreEqual(1, root.GetProperty("aps").GetProperty("content-available").GetInt32());
        var mesh = root.GetProperty("mesh");
        Assert.AreEqual("sync", mesh.GetProperty("type").GetString());
        Assert.AreEqual(MeshProtocol.Version, mesh.GetProperty("v").GetInt32());
        Assert.AreEqual("wake-1", mesh.GetProperty("wake_id").GetString());
        Assert.IsFalse(root.GetProperty("aps").TryGetProperty("alert", out _));

        AssertContentlessWake(payload);
    }

    [TestMethod]
    public void PushWake_VisiblePayloadAndFcmDataRemainGeneric()
    {
        var payload = ApnsPushSender.SerializeWakePayload(PushWakeMode.AlertAndSync, "wake-2");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var alert = root.GetProperty("aps").GetProperty("alert");

        Assert.AreEqual("Mesh", alert.GetProperty("title").GetString());
        Assert.AreEqual("New activity", alert.GetProperty("body").GetString());

        AssertContentlessWake(payload);

        var fcm = FcmPushSender.BuildWakeData(PushWakeMode.AlertAndSync, "wake-3");
        Assert.AreEqual(MeshProtocol.Version.ToString(), fcm["mesh_version"]);
        Assert.AreEqual("sync", fcm["mesh_type"]);
        Assert.AreEqual("wake-3", fcm["wake_id"]);
        Assert.AreEqual("1", fcm["show_alert"]);
        AssertContentlessWake(JsonSerializer.Serialize(fcm));
    }

    private static void AssertContentlessWake(string payload)
    {
        foreach (var forbidden in new[] { "from", "sender", "messageId", "frameId", "ciphertext", "event" })
            Assert.IsFalse(payload.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"wake must not contain '{forbidden}'");
    }

    [TestMethod]
    public void PushWake_ThrottleMatchesPerModeLimits()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), PushDispatcher.VisibleWakeMinimumInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(30), PushDispatcher.SilentWakeMinimumInterval);
        Assert.AreEqual(
            PushDispatcher.VisibleWakeMinimumInterval,
            PushDispatcher.WakeDeduplicationInterval);
        Assert.AreEqual(60, PushDispatcher.MaxVisibleWakesPerWindow);
        Assert.AreEqual(12, PushDispatcher.MaxSilentWakesPerWindow);
    }

    [TestMethod]
    public async Task PushWake_ModeThrottlesAreIndependentAndSurviveTokenRefresh()
    {
        var node = RelayNode.Create(withPush: true);
        var (handle, deviceId, _) = await node.RegisterAsync("bob");
        var now = DateTimeOffset.UtcNow;
        await node.Store.SetDevicePushTokenAsync(
            handle, deviceId, DevicePlatforms.IOS, "token-1", alertsEnabled: true);

        Assert.IsTrue(await node.Store.TryAcquireBackgroundPushAsync(
            handle, deviceId, PushWakeMode.SyncOnly, now,
            PushDispatcher.SilentWakeMinimumInterval, TimeSpan.FromHours(1),
            PushDispatcher.MaxSilentWakesPerWindow));
        Assert.IsTrue(await node.Store.TryAcquireBackgroundPushAsync(
            handle, deviceId, PushWakeMode.AlertAndSync, now,
            PushDispatcher.VisibleWakeMinimumInterval, TimeSpan.FromHours(1),
            PushDispatcher.MaxVisibleWakesPerWindow));

        await node.Store.SetDevicePushTokenAsync(
            handle, deviceId, DevicePlatforms.IOS, "token-2", alertsEnabled: true);

        Assert.IsFalse(await node.Store.TryAcquireBackgroundPushAsync(
            handle, deviceId, PushWakeMode.SyncOnly, now.AddSeconds(29),
            PushDispatcher.SilentWakeMinimumInterval, TimeSpan.FromHours(1),
            PushDispatcher.MaxSilentWakesPerWindow));
        Assert.IsFalse(await node.Store.TryAcquireBackgroundPushAsync(
            handle, deviceId, PushWakeMode.AlertAndSync, now.AddSeconds(4),
            PushDispatcher.VisibleWakeMinimumInterval, TimeSpan.FromHours(1),
            PushDispatcher.MaxVisibleWakesPerWindow));
        Assert.IsTrue(await node.Store.TryAcquireBackgroundPushAsync(
            handle, deviceId, PushWakeMode.SyncOnly, now.AddSeconds(30),
            PushDispatcher.SilentWakeMinimumInterval, TimeSpan.FromHours(1),
            PushDispatcher.MaxSilentWakesPerWindow));
        Assert.IsTrue(await node.Store.TryAcquireBackgroundPushAsync(
            handle, deviceId, PushWakeMode.AlertAndSync, now.AddSeconds(5),
            PushDispatcher.VisibleWakeMinimumInterval, TimeSpan.FromHours(1),
            PushDispatcher.MaxVisibleWakesPerWindow));
    }

    [TestMethod]
    public async Task PushWake_StableIdCoalescesAcrossModeClaims()
    {
        var node = RelayNode.Create(withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob");
        await node.Store.SetDevicePushTokenAsync(
            handle, deviceId, DevicePlatforms.IOS, "tok-123", alertsEnabled: true);

        var silent = await sender.Hub.Wake(new OnlineWakeRequest(handle, deviceId, "same-wake"));
        var visible = await sender.Hub.Wake(new OnlineWakeRequest(
            handle, deviceId, "same-wake", NotificationWorthy: true));

        Assert.IsTrue(silent.Accepted);
        Assert.IsTrue(visible.Accepted);
        Assert.AreEqual(1, node.PushSender!.Sent.Count);
        Assert.AreEqual(PushWakeMode.SyncOnly, node.PushSender.Sent[0].Mode);
    }

    [TestMethod]
    public async Task PushWake_StableIdCanRetryAfterDeliveryThrottleWindow()
    {
        var node = RelayNode.Create(withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob");
        await node.Store.SetDevicePushTokenAsync(
            handle, deviceId, DevicePlatforms.IOS, "tok-retry", alertsEnabled: true);
        var request = new OnlineWakeRequest(
            handle, deviceId, "retry-wake", NotificationWorthy: true);

        Assert.IsTrue((await sender.Hub.Wake(request)).Accepted);
        await Task.Delay(PushDispatcher.WakeDeduplicationInterval + TimeSpan.FromMilliseconds(500));
        Assert.IsTrue((await sender.Hub.Wake(request)).Accepted);

        Assert.AreEqual(2, node.PushSender!.Sent.Count);
        Assert.IsTrue(node.PushSender.Sent.All(item => item.Mode == PushWakeMode.AlertAndSync));
    }

    [TestMethod]
    public async Task Wake_AuthenticatedCallerUsesDirectRateLimitAndPreciseResults()
    {
        var node = RelayNode.Create(rateLimiter: TightRateLimiter(), withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob");
        await node.Store.SetDevicePushTokenAsync(handle, deviceId, DevicePlatforms.IOS, "tok-123", alertsEnabled: true);

        var accepted = await sender.Hub.Wake(new OnlineWakeRequest(handle, deviceId, "wake-1"));
        var limited = await sender.Hub.Wake(new OnlineWakeRequest(handle, deviceId, "wake-2"));

        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual(OnlineWakeCodes.Accepted, accepted.Code);
        Assert.IsFalse(limited.Accepted);
        Assert.AreEqual(OnlineWakeCodes.RateLimited, limited.Code);
        Assert.AreEqual(1, node.Metrics.Snapshot().RateLimitRejections);
    }

    [TestMethod]
    public async Task Wake_KnownDeviceWithoutPushRegistrationIsUnavailable()
    {
        var node = RelayNode.Create(withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob");

        var result = await sender.Hub.Wake(new OnlineWakeRequest(handle, deviceId, "wake-1"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OnlineWakeCodes.TargetUnavailable, result.Code);
    }
    [TestMethod]
    public async Task Relay_OfflineTargetNeverTriggersPushImplicitly()
    {
        var node = RelayNode.Create(withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob");
        await node.Store.SetDevicePushTokenAsync(handle, deviceId, DevicePlatforms.IOS, "tok-123", alertsEnabled: false);

        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", deviceId, "wake-me", OnlinePushClasses.High, "c"));

        Assert.AreEqual(OnlineRelaySendCodes.NotOnline, result.Code, "a wake is never delivery; custody stays with sender");
        Assert.AreEqual(0, node.Metrics.Snapshot().PushWakes);
        Assert.AreEqual(0, node.PushSender!.Sent.Count);
    }

    [TestMethod]
    public async Task Wake_NotificationWorthinessSelectsVisibleOrSilentMode()
    {
        var node = RelayNode.Create(withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob");
        await node.Store.SetDevicePushTokenAsync(
            handle, deviceId, DevicePlatforms.IOS, "tok-123", alertsEnabled: true);

        var silent = await sender.Hub.Wake(new OnlineWakeRequest(handle, deviceId, "silent-1"));
        var visible = await sender.Hub.Wake(new OnlineWakeRequest(
            handle, deviceId, "visible-1", NotificationWorthy: true));

        Assert.IsTrue(silent.Accepted);
        Assert.IsTrue(visible.Accepted);
        CollectionAssert.AreEqual(
            new[] { PushWakeMode.SyncOnly, PushWakeMode.AlertAndSync },
            node.PushSender!.Sent.Select(item => item.Mode).ToArray());
    }

    [TestMethod]
    public async Task Wake_OnlineTargetIsAcceptedWithoutPush()
    {
        var node = RelayNode.Create(withPush: true);
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, keys) = await node.RegisterAsync("bob");
        await node.Store.SetDevicePushTokenAsync(
            handle, deviceId, DevicePlatforms.IOS, "tok-123", alertsEnabled: true);
        _ = await node.ConnectExistingAsync(handle, deviceId, keys);

        var result = await sender.Hub.Wake(new OnlineWakeRequest(
            handle, deviceId, "online-1", NotificationWorthy: true));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(0, node.PushSender!.Sent.Count);
    }

    // ---- Agent request uses the same opaque forwarding ----------------------

    [TestMethod]
    public async Task AgentRequestToOfflineDeviceIsNotOnline()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, _) = await node.RegisterAsync("bob"); // agent host registered but offline

        // An agent request is just another opaque frame; there is no separate dispatch path.
        var result = await sender.Hub.Relay(new OnlineRelayFrame(
            "bob", deviceId, "agent-req-1", OnlinePushClasses.High, "opaque-agent-request"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OnlineRelaySendCodes.NotOnline, result.Code);
    }

    // ---- Reconnect never replays a queued payload ---------------------------

    [TestMethod]
    public async Task Reconnect_DoesNotReplayAnyQueuedFrame()
    {
        var node = RelayNode.Create();
        var sender = await node.ConnectAsync("alice");
        var (handle, deviceId, keys) = await node.RegisterAsync("bob");

        // Send while bob is offline: not_online, nothing stored.
        await sender.Hub.Relay(new OnlineRelayFrame("bob", deviceId, "missed", OnlinePushClasses.Normal, "c"));

        // Bob now connects. There must be no queued Deliver waiting for it.
        var target = await node.ConnectExistingAsync(handle, deviceId, keys);
        Assert.AreEqual(0, target.DeliveryCount(), "the relay has no queue; a reconnect pulls nothing");
    }

    // ---- Concurrency + memory bounds ----------------------------------------

    [TestMethod]
    public async Task Relay_FiveThousandConcurrentOnlineFramesAllDeliverExactlyOnce()
    {
        var node = RelayNode.Create(rateLimiter: new AllowAllRateLimiter());
        var sender = await node.ConnectAsync("alice");
        var target = await node.ConnectAsync("bob");

        const int total = 5000;
        var sends = Enumerable.Range(0, total).Select(i => Task.Run(() =>
            sender.Hub.Relay(new OnlineRelayFrame(
                "bob", target.DeviceId, $"c-{i}", OnlinePushClasses.Normal, "c"))));
        var results = await Task.WhenAll(sends);

        Assert.IsTrue(results.All(r => r.Accepted && r.Code == OnlineRelaySendCodes.Delivered));
        Assert.AreEqual(total, target.DeliveryCount());
        Assert.AreEqual(total, node.Metrics.Snapshot().OnlineDelivered);
    }

    [TestMethod]
    public void ConnectionOutboundBufferIsBounded()
    {
        var state = new ConnectionRegistry.ConnState();
        var reserved = 0;
        for (var i = 0; i < ConnectionRegistry.MaxOutboundInFlight; i++)
            if (state.TryReserveOutbound()) reserved++;

        Assert.AreEqual(ConnectionRegistry.MaxOutboundInFlight, reserved);
        Assert.IsFalse(state.TryReserveOutbound(), "the outbound buffer is hard-capped so a slow consumer cannot grow it unbounded");
        state.ReleaseOutbound();
        Assert.IsTrue(state.TryReserveOutbound(), "releasing a slot frees exactly one");
    }

    // ---- No payload persistence API (contract + schema) ---------------------

    [TestMethod]
    public void MetadataStoreExposesNoPayloadPersistenceApi()
    {
        string[] forbidden =
        {
            "Enqueue", "Dequeue", "Lease", "Drain", "Acknowledge", "Queue", "In" + "box",
            "Dispatch", "Outbox", "Attachment", "Blob", "Envelope", "Receipt", "Stage"
        };

        foreach (var type in new[] { typeof(IRelayStore), typeof(InMemoryRelayStore), typeof(CosmosRelayStore) })
        {
            foreach (var method in type.GetMethods())
            {
                foreach (var bad in forbidden)
                {
                    Assert.IsFalse(
                        method.Name.Contains(bad, StringComparison.OrdinalIgnoreCase),
                        $"{type.Name}.{method.Name} looks like a payload persistence API ('{bad}')");
                }
            }
        }
    }

    [TestMethod]
    public void CosmosProvisionsOnlyMetadataContainersAndNoPayloadContainers()
    {
        // The invariant must hold: none of the provisioned containers is a forbidden payload container.
        CosmosRelayStore.AssertNoPayloadContainers();

        foreach (var provisioned in CosmosRelayStore.ProvisionedContainers)
            Assert.IsFalse(
                CosmosRelayStore.ForbiddenContainerNames.Contains(provisioned, StringComparer.OrdinalIgnoreCase),
                $"'{provisioned}' is a payload container and must never be provisioned");

        CollectionAssert.AreEquivalent(
            new[] { "handles", "rate-policies", "invites", "services" },
            CosmosRelayStore.ProvisionedContainers.ToArray(),
            "only identity/authorization metadata containers may exist");

        foreach (var payload in new[] { "in" + "box", "device-queues", "agent-" + "dispatches", "attachments", "blobs", "messages" })
            CollectionAssert.Contains(CosmosRelayStore.ForbiddenContainerNames.ToArray(), payload);
    }

    // ======================================================================
    // Test harness: a fake, socket-free SignalR surface over the real relay.
    // ======================================================================

    private static string Sign(string privateKeyB64, string message)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    private static IMessageRateLimiter TightRateLimiter()
    {
        var store = new InMemoryRelayStore();
        var provider = new HandleRatePolicyProvider(
            store, new HandleRatePolicy(1, 1, 1, 1, 64));
        return new PerHandleRateLimiter(provider, new InMemoryRateLimitStore());
    }

    private sealed class AllowAllRateLimiter : IMessageRateLimiter
    {
        private static readonly HandleRatePolicy Policy = new(1_000_000, 1_000_000, 1_000_000, 1_000_000, 4096);

        public Task<(RateLimitDecision Decision, HandleRatePolicy Policy)> TryAcquireAsync(
            string handle, MessageRateBucket bucket, CancellationToken ct = default)
            => Task.FromResult((new RateLimitDecision(true, 0, 1_000_000), Policy));
    }

    private sealed class RecordingPushSender : IPushSender
    {
        private readonly ConcurrentQueue<(PushWakeMode Mode, string WakeId)> sent = new();
        public string Platform => DevicePlatforms.IOS;
        public IReadOnlyList<(PushWakeMode Mode, string WakeId)> Sent => sent.ToArray();

        public Task<PushSendResult> SendWakeAsync(
            string token,
            PushWakeMode mode,
            string wakeId,
            CancellationToken ct = default)
        {
            sent.Enqueue((mode, wakeId));
            return Task.FromResult(PushSendResult.Sent());
        }
    }

    private sealed class DeliverySink
    {
        private readonly ConcurrentDictionary<string, List<(string Method, object?[] Args)>> messages =
            new(StringComparer.Ordinal);

        public void Record(string connectionId, string method, object?[] args)
        {
            var list = messages.GetOrAdd(connectionId, _ => new());
            lock (list) list.Add((method, args));
        }

        public IReadOnlyList<(string Method, object?[] Args)> For(string connectionId)
        {
            if (!messages.TryGetValue(connectionId, out var list)) return Array.Empty<(string, object?[])>();
            lock (list) return list.ToArray();
        }
    }

    private sealed class SinkProxy(DeliverySink sink, string connectionId) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            sink.Record(connectionId, method, args);
            return Task.CompletedTask;
        }
    }

    private sealed class CallerClients(DeliverySink sink, string connectionId) : IHubCallerClients
    {
        public IClientProxy Caller => new SinkProxy(sink, connectionId);
        public IClientProxy Client(string id) => new SinkProxy(sink, id);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Group(string group) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string group, IReadOnlyList<string> excluded) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groups) => throw new NotSupportedException();
        public IClientProxy Others => throw new NotSupportedException();
        public IClientProxy OthersInGroup(string group) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RouterClients(DeliverySink sink) : IHubClients
    {
        public IClientProxy Client(string id) => new SinkProxy(sink, id);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Group(string group) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string group, IReadOnlyList<string> excluded) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groups) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RouterHubContext(DeliverySink sink) : IHubContext<MeshHub>
    {
        public IHubClients Clients { get; } = new RouterClients(sink);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    // IHttpContextFeature is not part of Mesh.Relay's public surface, so it is not
    // in the test project's compile-time reference closure. We satisfy SignalR's
    // GetHttpContext() extension (which reads Features.Get<IHttpContextFeature>())
    // by registering a DispatchProxy over the runtime interface type, keyed by that
    // Type in the FeatureCollection. No compile-time reference to the type is needed.
    public class HttpContextFeatureProxy : DispatchProxy
    {
        public HttpContext? Context;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_HttpContext":
                    return Context;
                case "set_HttpContext":
                    Context = args?[0] as HttpContext;
                    return null;
                default:
                    return null;
            }
        }
    }

    private static readonly Type? HttpContextFeatureType = LoadHttpContextFeatureType();

    private static Type? LoadHttpContextFeatureType()
    {
        // SignalR's Context.GetHttpContext() reads the connection feature
        // Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature (NOT the
        // standard Microsoft.AspNetCore.Http.Features one). That type is not in this test
        // project's compile-time reference closure, so we resolve it reflectively and
        // satisfy the feature via a DispatchProxy keyed by this runtime Type.
        const string connectionsFeature = "Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature";
        try
        {
            var loaded = Assembly.Load("Microsoft.AspNetCore.Http.Connections").GetType(connectionsFeature);
            if (loaded is not null)
            {
                return loaded;
            }
        }
        catch
        {
            // fall through to scanning loaded assemblies
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rex) { types = rex.Types; }
            catch { continue; }
            var match = types.FirstOrDefault(t => t?.FullName == connectionsFeature);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static object BuildHttpContextFeature(HttpContext http)
    {
        var create = typeof(DispatchProxy).GetMethods()
            .Single(m => m.Name == nameof(DispatchProxy.Create)
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2
                && m.GetParameters().Length == 0)
            .MakeGenericMethod(HttpContextFeatureType!, typeof(HttpContextFeatureProxy));
        var proxy = create.Invoke(null, null)!;
        ((HttpContextFeatureProxy)proxy).Context = http;
        return proxy;
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        private readonly CancellationTokenSource cts = new();
        private readonly FeatureCollection features = new();
        public bool AbortedFlag { get; private set; }

        public FakeCallerContext(string connectionId, string queryString)
        {
            ConnectionId = connectionId;
            var http = new DefaultHttpContext();
            http.Request.QueryString = new QueryString(queryString);
            if (HttpContextFeatureType is not null)
            {
                features[HttpContextFeatureType] = BuildHttpContextFeature(http);
            }
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => features;
        public override CancellationToken ConnectionAborted => cts.Token;

        public override void Abort()
        {
            AbortedFlag = true;
            cts.Cancel();
        }
    }

    private sealed class RelayConnection
    {
        public required string ConnectionId { get; init; }
        public required string Handle { get; init; }
        public required string DeviceId { get; init; }
        public required MeshHub Hub { get; init; }
        public required FakeCallerContext Context { get; init; }
        public required DeliverySink Sink { get; init; }

        public bool Aborted => Context.AbortedFlag;
        public IReadOnlyList<(string Method, object?[] Args)> Received => Sink.For(ConnectionId);

        public int DeliveryCount() => Received.Count(m => m.Method == OnlineRelayMethods.Deliver);

        public OnlineRelayDelivery? LastDelivery()
        {
            var last = Received.LastOrDefault(m => m.Method == OnlineRelayMethods.Deliver);
            if (last.Method is null) return null;
            return last.Args[0] as OnlineRelayDelivery;
        }
    }

    private sealed class RelayNode
    {
        public required InMemoryRelayStore Store { get; init; }
        public required IBackplane Backplane { get; init; }
        public required ConnectionRegistry Registry { get; init; }
        public required MeshRouter Router { get; init; }
        public required RelayFrameDedup Dedup { get; init; }
        public required RelayMetrics Metrics { get; init; }
        public required LiveFaultStore Faults { get; init; }
        public required IMessageRateLimiter RateLimiter { get; init; }
        public required PushDispatcher Push { get; init; }
        public RecordingPushSender? PushSender { get; init; }
        public required DeliverySink Sink { get; init; }

        public static RelayNode Create(
            InMemoryRelayStore? store = null,
            IBackplane? backplane = null,
            IMessageRateLimiter? rateLimiter = null,
            bool withPush = false)
        {
            store ??= new InMemoryRelayStore();
            backplane ??= new InMemoryBackplane();
            var sink = new DeliverySink();
            var registry = new ConnectionRegistry();
            var metrics = new RelayMetrics();
            var faults = new LiveFaultStore(new LiveFaultOptions { Enabled = false });
            var router = new MeshRouter(new RouterHubContext(sink), registry, backplane, metrics, faults);
            var pushSender = withPush ? new RecordingPushSender() : null;
            IEnumerable<IPushSender> senders = pushSender is null
                ? Array.Empty<IPushSender>() : new IPushSender[] { pushSender };
            var push = new PushDispatcher(store, senders, NullLogger<PushDispatcher>.Instance);
            var node = new RelayNode
            {
                Store = store,
                Backplane = backplane,
                Registry = registry,
                Router = router,
                Dedup = new RelayFrameDedup(),
                Metrics = metrics,
                Faults = faults,
                RateLimiter = rateLimiter ?? new AllowAllRateLimiter(),
                Push = push,
                PushSender = pushSender,
                Sink = sink
            };
            backplane.StartAsync(node.Router.DeliverFromBackplaneAsync).GetAwaiter().GetResult();
            return node;
        }

        public static (RelayNode A, RelayNode B) CreateLinkedPair(InMemoryRelayStore store)
        {
            var shared = new LinkedBackplane.Shared();
            var a = Create(store, new LinkedBackplane(shared));
            var b = Create(store, new LinkedBackplane(shared));
            return (a, b);
        }

        public async Task<(string Handle, string DeviceId, KeyPair Keys)> RegisterAsync(string handle)
        {
            var keys = KeyPair.New();
            var deviceId = DeviceProtocol.DeviceId(keys.PublicB64);
            await Store.UpsertHandleAsync(handle, keys.PublicB64, "display", allowNewDevice: true);
            await Store.SetDeviceMetadataAsync(handle, deviceId, "device", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
            return (handle, deviceId, keys);
        }

        public async Task AddDeviceAsync(string handle, KeyPair keys)
        {
            var deviceId = DeviceProtocol.DeviceId(keys.PublicB64);
            await Store.UpsertHandleAsync(handle, keys.PublicB64, "display", allowNewDevice: true);
            await Store.SetDeviceMetadataAsync(handle, deviceId, "device", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
        }

        public RelayConnection NewConnection(
            string handle, string deviceId, KeyPair keys, long authGeneration, string custodyHead, int protocolVersion = MeshProtocol.Version)
        {
            var connectionId = Guid.NewGuid().ToString("n");
            var query =
                $"?handle={Uri.EscapeDataString(handle)}&deviceId={Uri.EscapeDataString(deviceId)}" +
                $"&protocolVersion={protocolVersion}&authGeneration={authGeneration}&custodyHead={Uri.EscapeDataString(custodyHead)}";
            var context = new FakeCallerContext(connectionId, query);
            var hub = new MeshHub(
                Registry, Router, Store, Backplane, RateLimiter, Dedup, Push, Metrics,
                TimeProvider.System, Faults, new LiveFaultHandshakeObserver(),
                new LiveFaultTransportObserver(), NullLogger<MeshHub>.Instance)
            {
                Context = context,
                Clients = new CallerClients(Sink, connectionId)
            };
            return new RelayConnection
            {
                ConnectionId = connectionId,
                Handle = handle,
                DeviceId = deviceId,
                Hub = hub,
                Context = context,
                Sink = Sink
            };
        }

        public async Task<RelayConnection> ConnectAsync(string handle)
        {
            var (h, deviceId, keys) = await RegisterAsync(handle);
            return await ConnectExistingAsync(h, deviceId, keys);
        }

        public async Task<RelayConnection> ConnectExistingAsync(string handle, string deviceId, KeyPair keys)
        {
            var rec = (await Store.GetHandleAsync(handle))!;
            var conn = NewConnection(handle, deviceId, keys, rec.AuthGeneration, rec.CustodyHead);
            await conn.Hub.OnConnectedAsync();
            var state = Registry.Get(conn.ConnectionId)!;
            var canonical = RelayConnectChallenge.Canonical(
                state.Nonce, handle, deviceId, MeshProtocol.Version, rec.AuthGeneration, rec.CustodyHead);
            await conn.Hub.Authenticate(keys.PublicB64, Sign(keys.PrivateB64, canonical));
            return conn;
        }
    }

    /// <summary>
    /// A two-instance in-memory backplane that shares ephemeral presence and routes a directed
    /// forward to the peer instance's local delivery callback. It holds NO payloads: a published
    /// frame is a transient in-flight delivery handed straight to the owning instance's sockets.
    /// </summary>
    private sealed class LinkedBackplane(LinkedBackplane.Shared shared) : IBackplane
    {
        public sealed class Shared
        {
            public readonly ConcurrentDictionary<string, string> HandleOwner = new(StringComparer.OrdinalIgnoreCase);
            public readonly ConcurrentDictionary<string, string> DeviceOwner = new(StringComparer.OrdinalIgnoreCase);
            public readonly ConcurrentDictionary<string, Func<string, string, Task<BackplaneDeliveryReceipt>>> Deliverers = new();
        }

        public string InstanceId { get; } = Guid.NewGuid().ToString("n")[..8];

        public Task StartAsync(Func<string, string, Task<BackplaneDeliveryReceipt>> deliverLocal, CancellationToken ct = default)
        {
            shared.Deliverers[InstanceId] = deliverLocal;
            return Task.CompletedTask;
        }

        public Task SetPresenceAsync(string handle, CancellationToken ct = default)
        {
            shared.HandleOwner[handle] = InstanceId;
            return Task.CompletedTask;
        }

        public Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
        {
            shared.DeviceOwner[Key(handle, deviceId)] = InstanceId;
            return Task.CompletedTask;
        }

        public Task ClearPresenceAsync(string handle, CancellationToken ct = default)
        {
            if (shared.HandleOwner.TryGetValue(handle, out var owner) && owner == InstanceId)
                shared.HandleOwner.TryRemove(handle, out _);
            return Task.CompletedTask;
        }

        public Task ClearDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
        {
            var key = Key(handle, deviceId);
            if (shared.DeviceOwner.TryGetValue(key, out var owner) && owner == InstanceId)
                shared.DeviceOwner.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<string?> GetInstanceForAsync(string handle, CancellationToken ct = default)
            => Task.FromResult(shared.HandleOwner.TryGetValue(handle, out var owner) ? owner : null);

        public Task<string?> GetInstanceForDeviceAsync(string handle, string deviceId, CancellationToken ct = default)
            => Task.FromResult(shared.DeviceOwner.TryGetValue(Key(handle, deviceId), out var owner) ? owner : null);

        public Task<BackplaneDeliveryReceipt> PublishToOwnerAsync(
            string instanceId, string toHandle, string deliveryJson, CancellationToken ct = default)
            => shared.Deliverers.TryGetValue(instanceId, out var deliver)
                ? deliver(toHandle, deliveryJson)
                : Task.FromResult(BackplaneDeliveryReceipt.NotDelivered);

        private static string Key(string handle, string deviceId) => $"{handle}\u001f{deviceId}";
    }
}
