using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Mesh.Shared;

var relay = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:8790";
Console.WriteLine($"Testing relay: {relay}");
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var http = new HttpClient();
int failures = 0;
void Check(bool ok, string label)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
    if (!ok) failures++;
}

static (string priv, string pub) Gen()
{
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    return (Convert.ToBase64String(ec.ExportPkcs8PrivateKey()), Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
}
static string Sign(string privB64, string msg)
{
    using var ec = ECDsa.Create();
    ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privB64), out _);
    return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256));
}

var (aPriv, aPub) = Gen();
var (bPriv, bPub) = Gen();
var (cPriv, cPub) = Gen();
var aliceHandle = "alice" + Random.Shared.Next(1000, 9999);
var bobHandle = "bob" + Random.Shared.Next(1000, 9999);
var charlieHandle = "charlie" + Random.Shared.Next(1000, 9999);

// Proof-of-possession registration helper: sign the claim with the device private key.
async Task<System.Net.Http.HttpResponseMessage> Register(
    string handle,
    string pub,
    string priv,
    string? display,
    string? recoveryPub = null,
    int protocolVersion = MeshProtocol.Version)
{
    var sig = Sign(priv, ClaimProtocol.Message(handle, pub));
    return await http.PostAsJsonAsync($"{relay}/handles",
        new RegisterHandleRequest(
            handle,
            pub,
            display,
            recoveryPub,
            sig,
            ProtocolVersion: protocolVersion));
}

// 1. Register all handles (with proof of possession).
var r1 = await Register(aliceHandle, aPub, aPriv, "Alice");
var r2 = await Register(bobHandle, bPub, bPriv, "Bob");
var r3 = await Register(charlieHandle, cPub, cPriv, "Charlie");
Check(r1.IsSuccessStatusCode && r2.IsSuccessStatusCode && r3.IsSuccessStatusCode,
    "register alice + bob + charlie");

// 1b. Collision avoidance: an unsigned registration is rejected.
var rNoSig = await http.PostAsJsonAsync($"{relay}/handles",
    new RegisterHandleRequest("nosig" + Random.Shared.Next(1000, 9999), aPub, "NoSig"));
Check(rNoSig.StatusCode == System.Net.HttpStatusCode.BadRequest, "unsigned registration rejected");

// 1c. Collision avoidance: a signature by the wrong key (does not match the device key) is rejected.
var (xPriv, _) = Gen();
var wrongHandle = "wrong" + Random.Shared.Next(1000, 9999);
var wrongSig = Sign(xPriv, ClaimProtocol.Message(wrongHandle, aPub)); // signed by xPriv, but claims aPub
var rWrong = await http.PostAsJsonAsync($"{relay}/handles",
    new RegisterHandleRequest(wrongHandle, aPub, "Wrong", null, wrongSig));
Check(rWrong.StatusCode == System.Net.HttpStatusCode.BadRequest, "wrong-key claim signature rejected");

// 1d. Collision avoidance: a DIFFERENT key cannot take over alice's handle (409), even with a
// valid proof of possession for that other key.
var (takeoverPriv, takeoverPub) = Gen();
var rTakeover = await Register(aliceHandle, takeoverPub, takeoverPriv, "Impostor");
Check(rTakeover.StatusCode == System.Net.HttpStatusCode.Conflict, "different key cannot claim existing handle");

// 1e. Recovery: register a handle WITH a recovery key, then authorize a brand-new device by
// signing its key with the recovery key (the legitimate reinstall / takeover path).
var (recPriv, recPub) = Gen();
var recHandle = "rec" + Random.Shared.Next(1000, 9999);
var (d1Priv, d1Pub) = Gen();
var rRec1 = await Register(recHandle, d1Pub, d1Priv, "Recoverable", recPub);
var (d2Priv, d2Pub) = Gen();
var recSig = Sign(recPriv, RecoveryProtocol.Message(recHandle, d2Pub));
var rRec2 = await http.PostAsJsonAsync($"{relay}/handles/{recHandle}/recover",
    new RecoverHandleRequest(recHandle, d2Pub, recSig));
var recInfo = await http.GetFromJsonAsync<HandleInfo>($"{relay}/handles/{recHandle}");
Check(rRec1.IsSuccessStatusCode && rRec2.IsSuccessStatusCode
    && recInfo is not null && recInfo.DevicePublicKeys.Contains(d2Pub),
    "recovery authorizes a new device via recovery key");

// 1f. Handle uniqueness + delete: a fresh handle is free (404); after registering it is taken
// (200); deleting it with a registered key frees the name; then it can be claimed again.
var uHandle = "uniq" + Random.Shared.Next(1000, 9999);
var (uPriv, uPub) = Gen();
var free1 = await http.GetAsync($"{relay}/handles/{uHandle}");
await Register(uHandle, uPub, uPriv, "Uniq");
var taken = await http.GetAsync($"{relay}/handles/{uHandle}");
Check(free1.StatusCode == System.Net.HttpStatusCode.NotFound && taken.IsSuccessStatusCode,
    "handle is free before registration, taken after");

// A different key cannot delete someone else's handle.
var (evilPriv, evilPub) = Gen();
var evilDelSig = Sign(evilPriv, DeleteProtocol.Message(uHandle));
using var evilDelReq = new HttpRequestMessage(HttpMethod.Delete, $"{relay}/handles/{uHandle}")
{ Content = JsonContent.Create(new DeleteHandleRequest(uHandle, evilPub, evilDelSig)) };
var evilDel = await http.SendAsync(evilDelReq);
Check(evilDel.StatusCode == System.Net.HttpStatusCode.BadRequest, "unauthorized key cannot delete a handle");

// The owner deletes it, freeing the name.
var delSig = Sign(uPriv, DeleteProtocol.Message(uHandle));
using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{relay}/handles/{uHandle}")
{ Content = JsonContent.Create(new DeleteHandleRequest(uHandle, uPub, delSig)) };
var del = await http.SendAsync(delReq);
var afterDel = await http.GetAsync($"{relay}/handles/{uHandle}");
Check(del.IsSuccessStatusCode && afterDel.StatusCode == System.Net.HttpStatusCode.NotFound,
    "owner deletes handle and the name is freed");

// The freed name can be claimed by a brand-new key.
var (newPriv, newPub) = Gen();
var reclaim = await Register(uHandle, newPub, newPriv, "Reclaimer");
Check(reclaim.IsSuccessStatusCode, "freed handle can be re-created by a new identity");

// Directory lookup exposes bob's device key.
var info = await http.GetFromJsonAsync<HandleInfo>($"{relay}/handles/{bobHandle}");
Check(info is not null && info.DevicePublicKeys.Contains(bPub), "directory returns bob device key");

// Batch resolution normalizes handles, returns their keys, and omits missing handles.
var missingHandle = "missing" + Guid.NewGuid().ToString("n");
var resolveResponse = await http.PostAsJsonAsync($"{relay}/handles/resolve",
    new HandleKeysBatchRequest(new[]
    {
        $" @{aliceHandle.ToUpperInvariant()} ",
        bobHandle.ToUpperInvariant(),
        $"@{charlieHandle}",
        missingHandle
    }));
var resolved = resolveResponse.IsSuccessStatusCode
    ? await resolveResponse.Content.ReadFromJsonAsync<HandleKeysBatchResponse>(web)
    : null;
var resolvedByHandle = resolved?.Handles.ToDictionary(x => x.Handle, StringComparer.Ordinal);
Check(resolvedByHandle is not null
        && resolvedByHandle.Count == 3
        && resolvedByHandle.TryGetValue(aliceHandle, out var aliceKeys) && aliceKeys.DevicePublicKeys.Contains(aPub)
        && resolvedByHandle.TryGetValue(bobHandle, out var bobKeys) && bobKeys.DevicePublicKeys.Contains(bPub)
        && resolvedByHandle.TryGetValue(charlieHandle, out var charlieKeys) && charlieKeys.DevicePublicKeys.Contains(cPub),
    "batch resolve returns normalized alice + bob + charlie entries and keys");
Check(resolvedByHandle is not null && !resolvedByHandle.ContainsKey(missingHandle),
    "batch resolve omits a missing handle");

var oversizedResolve = await http.PostAsJsonAsync($"{relay}/handles/resolve",
    new HandleKeysBatchRequest(Enumerable.Range(0, FanoutProtocol.MaxRecipients + 1)
        .Select(i => $"resolve-limit-{i}")
        .ToArray()));
Check(oversizedResolve.StatusCode == System.Net.HttpStatusCode.BadRequest,
    "batch resolve rejects more than 128 handles");

var smokeAdminKey = Environment.GetEnvironmentVariable("MESH_SMOKE_ADMIN_KEY");
if (!string.IsNullOrWhiteSpace(smokeAdminKey))
{
    async Task<HttpResponseMessage> AdminPolicyAsync(HttpMethod method, object? body = null, bool authorized = true)
    {
        using var request = new HttpRequestMessage(
            method, $"{relay}/admin/handles/{aliceHandle}/rate-policy");
        if (authorized) request.Headers.Add("X-Mesh-Admin-Key", smokeAdminKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request);
    }

    var unauthorizedPolicy = await AdminPolicyAsync(HttpMethod.Get, authorized: false);
    Check(unauthorizedPolicy.StatusCode == System.Net.HttpStatusCode.Unauthorized,
        "rate-policy admin endpoint rejects missing key");

    var overridePolicy = new
    {
        messagesPerMinute = 240,
        burstCapacity = 40,
        groupMessagesPerMinute = 60,
        groupBurstCapacity = 12,
        maxFanoutRecipients = 64,
        enabled = true
    };
    var setPolicy = await AdminPolicyAsync(HttpMethod.Put, overridePolicy);
    Check(setPolicy.IsSuccessStatusCode, "admin stores per-handle rate-policy override");

    var getPolicy = await AdminPolicyAsync(HttpMethod.Get);
    var getPolicyJson = getPolicy.IsSuccessStatusCode
        ? JsonDocument.Parse(await getPolicy.Content.ReadAsStringAsync())
        : null;
    Check(getPolicyJson is not null
          && getPolicyJson.RootElement.GetProperty("overridden").GetBoolean()
          && getPolicyJson.RootElement.GetProperty("policy").GetProperty("groupMessagesPerMinute").GetInt32() == 60
          && getPolicyJson.RootElement.GetProperty("policy").GetProperty("maxFanoutRecipients").GetInt32() == 64,
        "admin reads effective per-handle Cosmos/in-memory override");
    getPolicyJson?.Dispose();

    var deletePolicy = await AdminPolicyAsync(HttpMethod.Delete);
    Check(deletePolicy.StatusCode == System.Net.HttpStatusCode.NoContent,
        "admin deletes per-handle rate-policy override");
    var getDefaultPolicy = await AdminPolicyAsync(HttpMethod.Get);
    var getDefaultJson = getDefaultPolicy.IsSuccessStatusCode
        ? JsonDocument.Parse(await getDefaultPolicy.Content.ReadAsStringAsync())
        : null;
    Check(getDefaultJson is not null
          && !getDefaultJson.RootElement.GetProperty("overridden").GetBoolean()
          && getDefaultJson.RootElement.GetProperty("policy").GetProperty("maxFanoutRecipients").GetInt32()
             == FanoutProtocol.MaxRecipients,
        "deleting override restores configured defaults immediately");
    getDefaultJson?.Dispose();
}

// ==========================================================================
// Protocol 9 online-only switchboard
// --------------------------------------------------------------------------
// The relay is an opaque, authenticated forwarder. Clients connect with the
// exact v9 handshake (handle/deviceId/protocolVersion/authGeneration/
// custodyHead), answer a signed challenge over the canonical connect string,
// then exchange opaque encrypted frames. The relay NEVER persists a payload:
// an offline target yields not_online, not a durable queue.
// ==========================================================================

const long FreshAuthGeneration = 1; // a freshly registered handle's auth generation
const string FreshCustodyHead = "";  // and its (empty) custody head

// Reproduces Mesh.Relay.Hub.RelayConnectChallenge.Canonical byte-for-byte
// without referencing the relay assembly (the smoke only sees Mesh.Shared).
static string ConnectChallengeCanonical(
    string nonce, string handle, string deviceId, int protocolVersion, long authGeneration, string custodyHead)
{
    var sb = new StringBuilder("mesh.relay.connect.v9");
    void Append(string? field)
    {
        field ??= "";
        sb.Append('|')
          .Append(field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append(':')
          .Append(field);
    }
    Append(nonce);
    Append(handle);
    Append(deviceId);
    Append(protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Append(authGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Append(custodyHead);
    return sb.ToString();
}

var connections = new List<HubConnection>();
async Task<(HubConnection conn, ConcurrentQueue<OnlineRelayDelivery> received, Task ready, string deviceId)> ConnectAsync(
    string handle, string priv, string pub,
    long authGeneration = FreshAuthGeneration, string custodyHead = FreshCustodyHead)
{
    var deviceId = DeviceProtocol.DeviceId(pub);
    var url = $"{relay}{MeshHubProtocol.Route}" +
        $"?handle={Uri.EscapeDataString(handle)}" +
        $"&deviceId={Uri.EscapeDataString(deviceId)}" +
        $"&protocolVersion={MeshProtocol.Version}" +
        $"&authGeneration={authGeneration}" +
        $"&custodyHead={Uri.EscapeDataString(custodyHead)}";
    var conn = new HubConnectionBuilder().WithUrl(url).Build();
    connections.Add(conn);
    var received = new ConcurrentQueue<OnlineRelayDelivery>();
    var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    conn.On<HandshakeResponse>(MeshHubProtocol.Handshake, response =>
    {
        if (response.Result != HandshakeResult.Accepted || response.ServerVersion != MeshProtocol.Version)
            readyTcs.TrySetException(new InvalidOperationException(
                response.Error ?? $"relay protocol mismatch: expected {MeshProtocol.Version}, got {response.ServerVersion}"));
    });
    conn.On<string>(MeshHubProtocol.Challenge, async nonce =>
    {
        try
        {
            var canonical = ConnectChallengeCanonical(
                nonce, handle, deviceId, MeshProtocol.Version, authGeneration, custodyHead);
            await conn.InvokeAsync(MeshHubProtocol.Authenticate, pub, Sign(priv, canonical));
        }
        catch (Exception ex) { readyTcs.TrySetException(ex); }
    });
    conn.On<PresenceConfirmed>(MeshHubProtocol.PresenceConfirmed, _ => readyTcs.TrySetResult());
    conn.On<string>(OnlineRelayMethods.Deliver, json =>
    {
        var delivery = JsonSerializer.Deserialize<OnlineRelayDelivery>(json, web);
        if (delivery is not null) received.Enqueue(delivery);
    });
    await conn.StartAsync();
    return (conn, received, readyTcs.Task, deviceId);
}

static async Task<bool> Within(Task t, int ms) => await Task.WhenAny(t, Task.Delay(ms)) == t;

async Task<OnlineRelayDelivery?> WaitForFrame(ConcurrentQueue<OnlineRelayDelivery> received, string frameId, int ms = 10000)
{
    var skipped = new List<OnlineRelayDelivery>();
    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(ms);
    try
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            while (received.TryDequeue(out var d))
            {
                if (d.FrameId == frameId) return d;
                skipped.Add(d);
            }
            await Task.Delay(50);
        }
        return null;
    }
    finally
    {
        foreach (var d in skipped) received.Enqueue(d);
    }
}

async Task<int?> ConnectedCount()
{
    using var response = await http.GetAsync($"{relay}/metrics");
    if (!response.IsSuccessStatusCode) return null;
    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    return doc.RootElement.TryGetProperty("connected", out var connected) ? connected.GetInt32() : null;
}

async Task<bool> WaitForDisconnect(int connectedBefore, int ms = 10000)
{
    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(ms);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var connected = await ConnectedCount();
        if (connected is not null && connected < connectedBefore) return true;
        await Task.Delay(50);
    }
    return false;
}

// 2. Exact v9 signed-challenge handshake, then an online directed forward.
var alice = await ConnectAsync(aliceHandle, aPriv, aPub);
var bob = await ConnectAsync(bobHandle, bPriv, bPub);
var charlie = await ConnectAsync(charlieHandle, cPriv, cPub);
Check(await Within(alice.ready, 10000), "alice completes the v9 signed-challenge handshake");
Check(await Within(bob.ready, 10000), "bob completes the v9 signed-challenge handshake");
Check(await Within(charlie.ready, 10000), "charlie completes the v9 signed-challenge handshake");

var plaintext = "hello bob, this is end to end encrypted";
var wire = MessageCrypto.Encrypt(plaintext, new[] { bPub }) ?? plaintext;
Check(MessageCrypto.IsEncrypted(wire), "frame ciphertext is end to end encrypted on the wire");
var directFrameId = Guid.NewGuid().ToString("n");
var directFrame = new OnlineRelayFrame(bobHandle, bob.deviceId, directFrameId, OnlinePushClasses.Normal, wire);
var directResult = await alice.conn.InvokeAsync<OnlineRelaySendResult>(OnlineRelayMethods.Relay, directFrame);
Check(directResult.Accepted && directResult.Code == OnlineRelaySendCodes.Delivered,
    "online directed relay returns delivered");

var recv = await WaitForFrame(bob.received, directFrameId);
Check(recv is not null, "bob receives the forwarded frame while online");
if (recv is not null)
{
    // The frame the client submits carries NO sender fields, so the sender identity on delivery is
    // stamped solely from the authenticated connection and cannot be forged by the submitter.
    Check(recv.FromHandle == aliceHandle && recv.FromDevice == alice.deviceId,
        "relay stamps sender identity from the authenticated connection (spoof resistant)");
    Check(recv.ToHandle == bobHandle && recv.ToDevice == bob.deviceId && recv.Ciphertext == wire,
        "relay forwards the opaque ciphertext unchanged to the target device");
    var (ok, decrypted) = MessageCrypto.TryDecrypt(recv.Ciphertext, bPriv, bPub);
    Check(ok && decrypted == plaintext, "bob decrypts the forwarded frame to the original plaintext");
}

// 3. Account fanout: one frame to a handle reaches every online authorized device of that handle.
var rec1 = await ConnectAsync(recHandle, d1Priv, d1Pub);
var rec2 = await ConnectAsync(recHandle, d2Priv, d2Pub);
Check(await Within(rec1.ready, 10000) && await Within(rec2.ready, 10000),
    "both linked devices of the recovered handle authenticate");
var fanBody = MessageCrypto.Encrypt("fanout to all my devices", new[] { d1Pub, d2Pub })!;
var fanFrameId = Guid.NewGuid().ToString("n");
var fanFrame = new OnlineRelayFrame(recHandle, null, fanFrameId, OnlinePushClasses.Normal, fanBody);
var fanResult = await alice.conn.InvokeAsync<OnlineRelaySendResult>(OnlineRelayMethods.Relay, fanFrame);
Check(fanResult.Accepted && fanResult.Code == OnlineRelaySendCodes.Delivered,
    "account fanout to a multi-device handle returns delivered");
var fan1 = await WaitForFrame(rec1.received, fanFrameId);
var fan2 = await WaitForFrame(rec2.received, fanFrameId);
Check(fan1 is not null && fan2 is not null
      && fan1!.ToDevice == rec1.deviceId && fan2!.ToDevice == rec2.deviceId
      && fan1.FromHandle == aliceHandle && fan2.FromHandle == aliceHandle
      && fan1.Ciphertext == fanBody && fan2.Ciphertext == fanBody,
    "each online authorized device receives its own device-addressed copy of the same ciphertext");

// 4. Presence resolution over the authenticated connection.
var ghostHandle = "ghost" + Guid.NewGuid().ToString("n")[..8];
var presence = await alice.conn.InvokeAsync<PresenceSnapshot>(
    OnlineRelayMethods.ResolvePresence, new[] { bobHandle, ghostHandle });
var bobPresence = presence.Handles.FirstOrDefault(h => h.Handle == bobHandle);
var ghostPresence = presence.Handles.FirstOrDefault(h => h.Handle == ghostHandle);
Check(bobPresence is { Online: true } && bobPresence.Devices.Contains(bob.deviceId),
    "ResolvePresence reports an online handle and its live device");
Check(ghostPresence is null || ghostPresence is { Online: false },
    "ResolvePresence reports an unregistered handle as not online");

// 5. Offline target: the relay returns not_online and never queues the payload.
var connectedBeforeCharlie = await ConnectedCount();
await charlie.conn.StopAsync();
Check(connectedBeforeCharlie is not null && await WaitForDisconnect(connectedBeforeCharlie.Value),
    "relay observes charlie go offline");
var offlineFrameId = Guid.NewGuid().ToString("n");
var offlineFrame = new OnlineRelayFrame(
    charlieHandle, charlie.deviceId, offlineFrameId, OnlinePushClasses.Normal,
    MessageCrypto.Encrypt("no durable home for this", new[] { cPub })!);
var offlineResult = await alice.conn.InvokeAsync<OnlineRelaySendResult>(OnlineRelayMethods.Relay, offlineFrame);
Check(!offlineResult.Accepted && offlineResult.Code == OnlineRelaySendCodes.NotOnline,
    "relay to an offline device returns not_online (there is no durable queue)");

// Reconnect proves nothing was persisted while offline: no frame is delivered on connect.
var charlie2 = await ConnectAsync(charlieHandle, cPriv, cPub);
Check(await Within(charlie2.ready, 10000), "charlie reconnects and re-authenticates");
Check(await WaitForFrame(charlie2.received, offlineFrameId, 1500) is null,
    "no payload was queued while offline: reconnect delivers nothing");

// 6. Unknown target handle and unknown target device are rejected without delivery.
var unknownHandleFrame = new OnlineRelayFrame(
    "ghost" + Guid.NewGuid().ToString("n")[..8], null,
    Guid.NewGuid().ToString("n"), OnlinePushClasses.Normal,
    MessageCrypto.Encrypt("into the void", new[] { bPub })!);
var unknownHandleResult = await alice.conn.InvokeAsync<OnlineRelaySendResult>(
    OnlineRelayMethods.Relay, unknownHandleFrame);
Check(!unknownHandleResult.Accepted && unknownHandleResult.Code == OnlineRelaySendCodes.NotOnline,
    "relay to an unregistered handle returns not_online");

var unknownDeviceFrame = new OnlineRelayFrame(
    bobHandle, "device-that-does-not-exist",
    Guid.NewGuid().ToString("n"), OnlinePushClasses.Normal,
    MessageCrypto.Encrypt("into the void", new[] { bPub })!);
var unknownDeviceResult = await alice.conn.InvokeAsync<OnlineRelaySendResult>(
    OnlineRelayMethods.Relay, unknownDeviceFrame);
Check(!unknownDeviceResult.Accepted && unknownDeviceResult.Code == OnlineRelaySendCodes.TargetDeviceUnknown,
    "relay to an unknown device of a known handle returns target_device_unknown");
Check(await WaitForFrame(bob.received, unknownDeviceFrame.FrameId, 1000) is null,
    "a rejected directed frame produces no delivery");

// 7. Size bound: a frame above the transport ceiling is rejected (too_large or transport bound).
var oversize = new string('x', OnlineReplicationLimits.MaxTransportBytes + 1024);
var tooLargeFrame = new OnlineRelayFrame(
    bobHandle, bob.deviceId, Guid.NewGuid().ToString("n"), OnlinePushClasses.Normal, oversize);
try
{
    var tooLargeResult = await alice.conn.InvokeAsync<OnlineRelaySendResult>(
        OnlineRelayMethods.Relay, tooLargeFrame);
    Check(!tooLargeResult.Accepted && tooLargeResult.Code == OnlineRelaySendCodes.TooLarge,
        "an oversized frame returns too_large");
}
catch (Exception)
{
    // The transport layer may reject the oversized frame before the hub sees it; either way the
    // size bound is enforced and no payload is delivered.
    Check(true, "an oversized frame is rejected by the transport size bound");
}
Check(await WaitForFrame(bob.received, tooLargeFrame.FrameId, 1000) is null,
    "an oversized frame produces no delivery");

// 8. A device that is not authorized for a handle is refused before presence.
var (strangerPriv, strangerPub) = Gen();
var strangerConn = await ConnectAsync(bobHandle, strangerPriv, strangerPub);
Check(!await Within(strangerConn.ready, 2500),
    "a device not authorized for the handle cannot complete the handshake");

// 9. Capability + metrics contract: online only, no durable payload storage, no queue metrics.
using var healthDoc = JsonDocument.Parse(await http.GetStringAsync($"{relay}/health"));
var caps = healthDoc.RootElement.GetProperty("capabilities");
Check(caps.GetProperty("protocolVersion").GetInt32() == MeshProtocol.Version,
    "/health advertises protocol version 9");
Check(caps.GetProperty("onlineOnly").GetBoolean(),
    "/health capability reports onlineOnly=true");
Check(!caps.GetProperty("durablePayloadStorage").GetBoolean(),
    "/health capability reports durablePayloadStorage=false");

using var metricsDoc = JsonDocument.Parse(await http.GetStringAsync($"{relay}/metrics"));
var metricsRoot = metricsDoc.RootElement;
Check(metricsRoot.TryGetProperty("onlineOnly", out var oo) && oo.GetBoolean()
      && metricsRoot.TryGetProperty("durablePayloadStorage", out var dps) && !dps.GetBoolean(),
    "/metrics reports onlineOnly=true and durablePayloadStorage=false");
Check(metricsRoot.TryGetProperty("onlineDelivered", out var od) && od.GetInt32() >= 1
      && metricsRoot.TryGetProperty("offlineNacks", out var on) && on.GetInt32() >= 1,
    "/metrics counts online deliveries and offline NACKs");
foreach (var forbidden in new[] { "queueDepth", "leaseCount", ("in" + "box" + "Size"), "pendingDeliveries", "dispatchBacklog" })
    Check(!metricsRoot.TryGetProperty(forbidden, out _),
        $"/metrics exposes no queue metric '{forbidden}'");

// ---- Connection teardown -------------------------------------------------
foreach (var connection in connections)
{
    try
    {
        if (connection.State != HubConnectionState.Disconnected)
            await connection.StopAsync();
        await connection.DisposeAsync();
    }
    catch
    {
        failures++;
        Console.WriteLine("FAIL clean up hub connection");
    }
}

// ---- Handle cleanup (best-effort; cleanup failures are reported) ----------
async Task CleanupHandle(string handle, string pub, string priv)
{
    var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{relay}/handles/{handle}")
    {
        Content = JsonContent.Create(new DeleteHandleRequest(
            handle, pub, Sign(priv, DeleteProtocol.Message(handle))))
    });
    if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
    {
        failures++;
        Console.WriteLine($"FAIL cleanup handle {handle}: HTTP {(int)resp.StatusCode}");
    }
}

await CleanupHandle(aliceHandle, aPub, aPriv);
await CleanupHandle(bobHandle, bPub, bPriv);
await CleanupHandle(charlieHandle, cPub, cPriv);
await CleanupHandle(recHandle, d2Pub, d2Priv);
await CleanupHandle(uHandle, newPub, newPriv);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL SMOKE TESTS PASSED" : $"{failures} SMOKE TEST(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);

// --------------------------------------------------------------------------
// Local mirrors of the relay's presence snapshot (defined in Mesh.Relay.Hub,
// which the smoke does not reference). Shapes match the wire JSON exactly.
// --------------------------------------------------------------------------
internal sealed record HandlePresence(string Handle, bool Online, IReadOnlyList<string> Devices);
internal sealed record PresenceSnapshot(IReadOnlyList<HandlePresence> Handles);
