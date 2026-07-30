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

var connections = new List<HubConnection>();
async Task<(HubConnection conn, ConcurrentQueue<MeshEnvelope> inbox, Task ready)> ConnectAsync(
    string handle,
    string priv,
    string pub,
    bool deliveryAck = true)
{
    var deliveryQuery = deliveryAck ? "&deliveryAck=1" : "";
    var conn = new HubConnectionBuilder()
        .WithUrl($"{relay}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(handle)}{deliveryQuery}&protocolVersion={MeshProtocol.Version}")
        .Build();
    connections.Add(conn);
    var inbox = new ConcurrentQueue<MeshEnvelope>();
    var readyTcs = new TaskCompletionSource();
    conn.On<HandshakeResponse>(MeshHubProtocol.Handshake, response =>
    {
        if (response.Result != HandshakeResult.Accepted || response.ServerVersion != MeshProtocol.Version)
            readyTcs.TrySetException(new InvalidOperationException(
                response.Error ?? $"relay protocol mismatch: expected {MeshProtocol.Version}, got {response.ServerVersion}"));
    });
    conn.On<string>(MeshHubProtocol.Challenge, async nonce =>
    {
        var sig = Sign(priv, nonce);
        await conn.InvokeAsync(MeshHubProtocol.Authenticate, pub, sig);
    });
    conn.On<PresenceConfirmed>(MeshHubProtocol.PresenceConfirmed, async _ =>
    {
        try
        {
            if (deliveryAck)
                await conn.InvokeAsync<int>(MeshHubProtocol.RequestPendingDeliveries);
            readyTcs.TrySetResult();
        }
        catch (Exception ex) { readyTcs.TrySetException(ex); }
    });
    conn.On<string>(MeshHubProtocol.Receive, json =>
    {
        var e = JsonSerializer.Deserialize<MeshEnvelope>(json, web);
        if (e is not null) inbox.Enqueue(e);
    });
    await conn.StartAsync();
    return (conn, inbox, readyTcs.Task);
}

static async Task<bool> Within(Task t, int ms) => await Task.WhenAny(t, Task.Delay(ms)) == t;

async Task<int?> ConnectedCount()
{
    using var metricsResponse = await http.GetAsync($"{relay}/metrics");
    if (!metricsResponse.IsSuccessStatusCode) return null;
    using var metrics = await JsonDocument.ParseAsync(await metricsResponse.Content.ReadAsStreamAsync());
    return metrics.RootElement.TryGetProperty("connected", out var connected)
        ? connected.GetInt32()
        : null;
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

async Task<MeshEnvelope?> WaitForEnvelope(ConcurrentQueue<MeshEnvelope> inbox, string id, int ms = 10000)
{
    var skipped = new List<MeshEnvelope>();
    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(ms);
    try
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            while (inbox.TryDequeue(out var envelope))
            {
                if (envelope.Id == id) return envelope;
                skipped.Add(envelope);
            }
            await Task.Delay(50);
        }
        return null;
    }
    finally
    {
        foreach (var envelope in skipped) inbox.Enqueue(envelope);
    }
}

bool SnapshotMatches(string? json, GroupSnapshotPayload expected)
{
    try
    {
        var actual = JsonSerializer.Deserialize<GroupSnapshotPayload>(json!, web);
        return actual is not null
            && actual.GroupId == expected.GroupId
            && actual.Name == expected.Name
            && actual.OwnerHandle == expected.OwnerHandle
            && actual.MemberHandles.SequenceEqual(expected.MemberHandles)
            && actual.Version == expected.Version;
    }
    catch (JsonException)
    {
        return false;
    }
}

bool GroupMessageMatches(string? json, GroupMessagePayload expected)
{
    try
    {
        var actual = JsonSerializer.Deserialize<GroupMessagePayload>(json!, web);
        return actual == expected;
    }
    catch (JsonException)
    {
        return false;
    }
}

// 2. All online: challenge/response auth then routed delivery.
var alice = await ConnectAsync(aliceHandle, aPriv, aPub);
var bob = await ConnectAsync(bobHandle, bPriv, bPub);
var charlie = await ConnectAsync(charlieHandle, cPriv, cPub);
Check(await Within(alice.ready, 10000), "alice authenticated (challenge/response)");
Check(await Within(bob.ready, 10000), "bob authenticated (challenge/response)");
Check(await Within(charlie.ready, 10000), "charlie authenticated (challenge/response)");

// Alice sends an E2E-encrypted, signed message to Bob.
var plaintext = "hello bob, this is end to end encrypted";
var wire = MessageCrypto.Encrypt(plaintext, new[] { bPub }) ?? plaintext;
Check(MessageCrypto.IsEncrypted(wire), "message body is encrypted on the wire");
var env = MeshEnvelope.Create(aliceHandle, bobHandle, MeshKinds.Chat, wire, Sign(aPriv, wire));
var onlineResult = await alice.conn.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, env);
Check(onlineResult.Accepted && onlineResult.Code == DurableDeliveryCodes.Delivered && onlineResult.RecipientCount == 1,
    "direct online send returns accepted result");

var recv = await WaitForEnvelope(bob.inbox, env.Id);
var gotOnline = recv is not null;
Check(gotOnline, "bob received message while online");
if (gotOnline)
{
    var (ok, decrypted) = MessageCrypto.TryDecrypt(recv!.Body, bPriv, bPub);
    Check(ok && decrypted == plaintext, "bob decrypts E2E payload to original plaintext");
    Check(recv!.From == bobHandle ? false : recv.From == aliceHandle, "relay stamped From = alice");
    Check(MeshCrypto.Verify(aPub, recv!.Body, recv.Signature ?? ""), "bob can verify alice signature");
}

// 3. Offline inbox: Bob disconnects, Alice sends, Bob reconnects and drains.
await bob.conn.StopAsync();
await bob.conn.DisposeAsync();
await Task.Delay(500);

var offlineText = "queued while you were away";
var wire2 = MessageCrypto.Encrypt(offlineText, new[] { bPub }) ?? offlineText;
var env2 = MeshEnvelope.Create(aliceHandle, bobHandle, MeshKinds.Chat, wire2, Sign(aPriv, wire2));
var offlineResult = await alice.conn.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, env2);
Check(offlineResult.Accepted && offlineResult.Code == DurableDeliveryCodes.RelayQueued,
    "direct offline send returns accepted result");

var bob2 = await ConnectAsync(bobHandle, bPriv, bPub);
Check(await Within(bob2.ready, 10000), "bob reconnected + authenticated");
var recv2 = await WaitForEnvelope(bob2.inbox, env2.Id);
var gotOffline = recv2 is not null;
Check(gotOffline, "bob received queued offline message on reconnect");
if (gotOffline)
{
    var (ok2, dec2) = MessageCrypto.TryDecrypt(recv2!.Body, bPriv, bPub);
    Check(ok2 && dec2 == offlineText, "offline message decrypts correctly");
}

// 4. Forged signature is rejected by the hub (bad body signature).
var badEnv = MeshEnvelope.Create(aliceHandle, bobHandle, MeshKinds.Chat, "tampered", "not-a-valid-signature");
var badDirectResult = await alice.conn.InvokeAsync<MeshSendResult>(MeshHubProtocol.SendEnvelope, badEnv);
Check(!badDirectResult.Accepted && badDirectResult.Code == "invalid_signature",
    "invalid direct signature returns invalid_signature");
Check(await WaitForEnvelope(bob2.inbox, badEnv.Id, 1000) is null,
    "invalid direct signature produces no delivery");

// 5. Stateless group snapshot fan-out: one ciphertext and one hub call.
var groupId = Guid.NewGuid().ToString("n");
var groupName = "Private smoke trio " + Guid.NewGuid().ToString("n");
var snapshot = new GroupSnapshotPayload(
    groupId,
    groupName,
    aliceHandle,
    new[] { aliceHandle, bobHandle, charlieHandle },
    1);
var snapshotJson = JsonSerializer.Serialize(snapshot, web);
var snapshotContent = JsonSerializer.Serialize(
    new MeshFanoutContent(MeshKinds.GroupControl, snapshotJson), web);
var snapshotWire = MessageCrypto.Encrypt(snapshotContent, new[] { bPub, cPub });
Check(MessageCrypto.IsEncrypted(snapshotWire), "group snapshot is encrypted once to bob + charlie keys");
var snapshotSignature = Sign(aPriv, snapshotWire!);
var snapshotRequest = new MeshFanoutRequest(
    Guid.NewGuid().ToString("n"),
    new[] { bobHandle, charlieHandle },
    snapshotWire!,
    snapshotSignature,
    DateTimeOffset.UtcNow);
var privateGroupValues = new[] { groupId, groupName, aliceHandle, bobHandle, charlieHandle };
Check(privateGroupValues.All(value =>
        !snapshotRequest.Body.Contains(value, StringComparison.Ordinal)),
    "group id, name, and member handles are absent from relay-visible ciphertext");
Check(snapshotRequest.Recipients.SequenceEqual(new[] { bobHandle, charlieHandle })
        && !snapshotRequest.Id.Contains(groupId, StringComparison.Ordinal)
        && !snapshotRequest.Id.Contains(groupName, StringComparison.Ordinal),
    "group metadata is absent from routing fields outside transient recipients");
Check(MeshCrypto.Verify(aPub, snapshotRequest.Body, snapshotRequest.Signature ?? ""),
    "group snapshot ciphertext is signed once");

var snapshotResult = await alice.conn.InvokeAsync<MeshSendResult>(
    MeshHubProtocol.SendFanout, snapshotRequest);
Check(snapshotResult.Accepted && snapshotResult.Code == "accepted"
        && snapshotResult.RecipientCount == 2,
    "one snapshot fanout call is accepted for two recipients");
var bobSnapshotReceived = await WaitForEnvelope(bob2.inbox, snapshotRequest.Id);
var charlieSnapshotReceived = await WaitForEnvelope(charlie.inbox, snapshotRequest.Id);
Check(bobSnapshotReceived is not null && charlieSnapshotReceived is not null,
    "bob + charlie each receive the group snapshot");
if (bobSnapshotReceived is not null && charlieSnapshotReceived is not null)
{
    Check(bobSnapshotReceived.Kind == MeshKinds.Fanout
            && charlieSnapshotReceived.Kind == MeshKinds.Fanout
            && bobSnapshotReceived.Id == charlieSnapshotReceived.Id
            && bobSnapshotReceived.Body == charlieSnapshotReceived.Body
            && bobSnapshotReceived.Signature == charlieSnapshotReceived.Signature
            && bobSnapshotReceived.SentAt == charlieSnapshotReceived.SentAt
            && bobSnapshotReceived.FromDevice == charlieSnapshotReceived.FromDevice
            && bobSnapshotReceived.ToDevice == DeviceProtocol.DeviceId(bPub)
            && charlieSnapshotReceived.ToDevice == DeviceProtocol.DeviceId(cPub)
            && bobSnapshotReceived.To == bobHandle
            && charlieSnapshotReceived.To == charlieHandle,
        "relay clones fanout ciphertext while individualizing handle + device routing");
    Check(privateGroupValues.All(value =>
            !bobSnapshotReceived.Body.Contains(value, StringComparison.Ordinal)
            && !charlieSnapshotReceived.Body.Contains(value, StringComparison.Ordinal))
        && bobSnapshotReceived.From == aliceHandle
        && charlieSnapshotReceived.From == aliceHandle,
        "fanout clones expose no group metadata in ciphertext or routing fields");
    var (bobSnapshotOk, bobSnapshotPlain) =
        MessageCrypto.TryDecrypt(bobSnapshotReceived.Body, bPriv, bPub);
    var (charlieSnapshotOk, charlieSnapshotPlain) =
        MessageCrypto.TryDecrypt(charlieSnapshotReceived.Body, cPriv, cPub);
    var bobFanout = bobSnapshotOk
        ? JsonSerializer.Deserialize<MeshFanoutContent>(bobSnapshotPlain!, web)
        : null;
    var charlieFanout = charlieSnapshotOk
        ? JsonSerializer.Deserialize<MeshFanoutContent>(charlieSnapshotPlain!, web)
        : null;
    Check(bobSnapshotOk && charlieSnapshotOk
            && bobFanout?.Kind == MeshKinds.GroupControl
            && charlieFanout?.Kind == MeshKinds.GroupControl
            && SnapshotMatches(bobFanout.Payload, snapshot)
            && SnapshotMatches(charlieFanout.Payload, snapshot),
        "bob + charlie decrypt equivalent group snapshots");
}

// A fan-out addressed to one handle reaches its online device now and its offline sibling later.
var recovered1 = await ConnectAsync(recHandle, d1Priv, d1Pub);
Check(await Within(recovered1.ready, 10000), "first linked recovery device authenticated");
var linkedContent = JsonSerializer.Serialize(
    new MeshFanoutContent(MeshKinds.GroupMessage, "{\"linkedDevice\":true}"), web);
var linkedWire = MessageCrypto.Encrypt(linkedContent, new[] { d1Pub, d2Pub })!;
var linkedRequest = new MeshFanoutRequest(
    Guid.NewGuid().ToString("n"),
    new[] { recHandle },
    linkedWire,
    Sign(aPriv, linkedWire),
    DateTimeOffset.UtcNow);
var linkedResult = await alice.conn.InvokeAsync<MeshSendResult>(
    MeshHubProtocol.SendFanout, linkedRequest);
Check(linkedResult.Accepted && linkedResult.RecipientCount == 1,
    "one fanout targets a multi-device handle as one recipient");
var linkedOnline = await WaitForEnvelope(recovered1.inbox, linkedRequest.Id);
Check(linkedOnline?.ToDevice == DeviceProtocol.DeviceId(d1Pub),
    "online linked device receives its targeted fanout immediately");

var recovered2 = await ConnectAsync(recHandle, d2Priv, d2Pub);
Check(await Within(recovered2.ready, 10000), "offline linked recovery device reconnects");
var linkedOffline = await WaitForEnvelope(recovered2.inbox, linkedRequest.Id);
Check(linkedOffline?.ToDevice == DeviceProtocol.DeviceId(d2Pub)
      && linkedOffline.Body == linkedOnline?.Body
      && linkedOffline.Signature == linkedOnline?.Signature,
    "offline sibling drains its own device-specific fanout without another device consuming it");

// 6. One group-message fanout routes to online Bob and offline Charlie.
var connectedBeforeCharlieStop = await ConnectedCount();
await charlie.conn.StopAsync();
Check(connectedBeforeCharlieStop is not null
        && await WaitForDisconnect(connectedBeforeCharlieStop.Value),
    "relay observes charlie offline before fanout");

var groupMessage = new GroupMessagePayload(
    groupId,
    Guid.NewGuid().ToString("n"),
    aliceHandle,
    "hello private group",
    snapshot.Version,
    DateTimeOffset.UtcNow);
var groupMessageJson = JsonSerializer.Serialize(groupMessage, web);
var groupMessageContent = JsonSerializer.Serialize(
    new MeshFanoutContent(MeshKinds.GroupMessage, groupMessageJson), web);
var groupMessageWire = MessageCrypto.Encrypt(groupMessageContent, new[] { bPub, cPub });
var groupMessageRequest = new MeshFanoutRequest(
    Guid.NewGuid().ToString("n"),
    new[] { bobHandle, charlieHandle },
    groupMessageWire!,
    Sign(aPriv, groupMessageWire!),
    DateTimeOffset.UtcNow);
Check(MessageCrypto.IsEncrypted(groupMessageWire),
    "group message is encrypted once to online + offline recipient keys");
var groupMessageResult = await alice.conn.InvokeAsync<MeshSendResult>(
    MeshHubProtocol.SendFanout, groupMessageRequest);
Check(groupMessageResult.Accepted && groupMessageResult.Code == "accepted"
        && groupMessageResult.RecipientCount == 2,
    "one group-message fanout call is accepted for online + offline recipients");
var bobGroupReceived = await WaitForEnvelope(bob2.inbox, groupMessageRequest.Id);
Check(bobGroupReceived is not null, "online bob receives group message immediately");
if (bobGroupReceived is not null)
{
    var (bobGroupOk, bobGroupPlain) = MessageCrypto.TryDecrypt(bobGroupReceived.Body, bPriv, bPub);
    var bobFanout = bobGroupOk
        ? JsonSerializer.Deserialize<MeshFanoutContent>(bobGroupPlain!, web)
        : null;
    Check(bobGroupOk && bobGroupReceived.Body == groupMessageWire
            && bobGroupReceived.Kind == MeshKinds.Fanout
            && bobFanout?.Kind == MeshKinds.GroupMessage
            && GroupMessageMatches(bobFanout.Payload, groupMessage),
        "bob decrypts the unchanged group message payload");
}

var charlie2 = await ConnectAsync(charlieHandle, cPriv, cPub);
Check(await Within(charlie2.ready, 10000), "charlie reconnected + authenticated");
var charlieGroupReceived = await WaitForEnvelope(charlie2.inbox, groupMessageRequest.Id);
Check(charlieGroupReceived is not null, "charlie drains queued group message on reconnect");
if (charlieGroupReceived is not null)
{
    var (charlieGroupOk, charlieGroupPlain) =
        MessageCrypto.TryDecrypt(charlieGroupReceived.Body, cPriv, cPub);
    var charlieFanout = charlieGroupOk
        ? JsonSerializer.Deserialize<MeshFanoutContent>(charlieGroupPlain!, web)
        : null;
    Check(charlieGroupOk && charlieGroupReceived.Body == groupMessageWire
            && bobGroupReceived?.Body == charlieGroupReceived.Body
            && bobGroupReceived?.Signature == charlieGroupReceived.Signature
            && charlieGroupReceived.Kind == MeshKinds.Fanout
            && charlieFanout?.Kind == MeshKinds.GroupMessage
            && GroupMessageMatches(charlieFanout.Payload, groupMessage),
        "offline inbox preserves identical ciphertext and decrypts charlie's payload");
}

// 7. Fanout hard cap and signature failures return explicit results and do not deliver.
var tooManyRecipients = new[] { bobHandle }
    .Concat(Enumerable.Range(0, FanoutProtocol.MaxRecipients)
        .Select(i => $"fanout-limit-{i}-{Guid.NewGuid():n}"))
    .ToArray();
var tooManyBody = MessageCrypto.Encrypt("fanout limit probe", new[] { bPub })!;
var tooManyRequest = new MeshFanoutRequest(
    Guid.NewGuid().ToString("n"),
    tooManyRecipients,
    tooManyBody,
    Sign(aPriv, tooManyBody),
    DateTimeOffset.UtcNow);
var tooManyResult = await alice.conn.InvokeAsync<MeshSendResult>(
    MeshHubProtocol.SendFanout, tooManyRequest);
Check(!tooManyResult.Accepted && tooManyResult.Code == "too_many_recipients",
    "fanout with 129 distinct recipients returns too_many_recipients");
Check(await WaitForEnvelope(bob2.inbox, tooManyRequest.Id, 1000) is null,
    "rejected oversized fanout produces no delivery");

var badFanoutRequest = new MeshFanoutRequest(
    Guid.NewGuid().ToString("n"),
    new[] { bobHandle, charlieHandle },
    snapshotWire!,
    "not-a-valid-signature",
    DateTimeOffset.UtcNow);
var badFanoutResult = await alice.conn.InvokeAsync<MeshSendResult>(
    MeshHubProtocol.SendFanout, badFanoutRequest);
Check(!badFanoutResult.Accepted && badFanoutResult.Code == "invalid_signature",
    "invalid fanout signature returns invalid_signature");
var badFanoutBob = await WaitForEnvelope(bob2.inbox, badFanoutRequest.Id, 1000);
var badFanoutCharlie = await WaitForEnvelope(charlie2.inbox, badFanoutRequest.Id, 1000);
Check(badFanoutBob is null && badFanoutCharlie is null,
    "invalid fanout signature produces no delivery");


// 8. Protocol 8 reliability: QueueEnqueue/QueueDrain/QueueAck, DeviceQueueAvailable notification,
// revocation mechanics, and legacy rejection path through a production relay.

// --- Setup: register a handle with two devices (owner + peer via recovery key) ---
var (p8RecoveryPriv, p8RecoveryPub) = Gen();
var (p8OwnerPriv, p8OwnerPub) = Gen();
var (p8PeerPriv, p8PeerPub) = Gen();
var p8Handle = "p8" + Guid.NewGuid().ToString("n")[..10];
var p8OwnerId = DeviceProtocol.DeviceId(p8OwnerPub);
var p8PeerId  = DeviceProtocol.DeviceId(p8PeerPub);

var p8Registration = await Register(
    p8Handle, p8OwnerPub, p8OwnerPriv, "Protocol 8 Smoke Owner", p8RecoveryPub);

var p8PeerRecovery = await http.PostAsJsonAsync(
    $"{relay}/handles/{p8Handle}/recover",
    new RecoverHandleRequest(p8Handle, p8PeerPub,
        Sign(p8RecoveryPriv, RecoveryProtocol.Message(p8Handle, p8PeerPub))));

var p8DeviceDir = await http.GetFromJsonAsync<DeviceInfo[]>($"{relay}/handles/{p8Handle}/devices");
Check(p8Registration.IsSuccessStatusCode
      && p8PeerRecovery.IsSuccessStatusCode
      && p8DeviceDir?.Any(d => d.DeviceId == p8OwnerId && d.ProtocolVersion == MeshProtocol.Version) == true
      && p8DeviceDir?.Any(d => d.DeviceId == p8PeerId) == true,
    "protocol 8 handle: owner registered with version 8 in device directory; peer linked via recovery");

// Connect both devices
var p8Owner = await ConnectAsync(p8Handle, p8OwnerPriv, p8OwnerPub);
Check(await Within(p8Owner.ready, 10000), "p8 owner connects and authenticates");

var p8Peer = await ConnectAsync(p8Handle, p8PeerPriv, p8PeerPub);
Check(await Within(p8Peer.ready, 10000), "p8 peer connects and authenticates");

// --- 8a: QueueEnqueue idempotency ---
var syncOpId = Guid.NewGuid().ToString("n");
var r8a1 = await p8Owner.conn.InvokeAsync<QueueEnqueueResult>(
    MeshHubProtocol.QueueEnqueue,
    new QueueEnqueue(p8OwnerId, p8PeerId, syncOpId, "p8-payload"));
Check(r8a1.Accepted && r8a1.Created,
    "8a: first QueueEnqueue is accepted and Created=true");

var r8a2 = await p8Owner.conn.InvokeAsync<QueueEnqueueResult>(
    MeshHubProtocol.QueueEnqueue,
    new QueueEnqueue(p8OwnerId, p8PeerId, syncOpId, "p8-payload"));
Check(r8a2.Accepted && !r8a2.Created && r8a2.EntryId == r8a1.EntryId,
    "8a: duplicate enqueue (same operationId) is idempotent: Accepted=true, Created=false, same EntryId");

// --- 8b: DeviceQueueAvailable notification when target is online ---
var queueAvailTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
p8Peer.conn.On(MeshHubProtocol.DeviceQueueAvailable, () => queueAvailTcs.TrySetResult());
var notifyOpId = Guid.NewGuid().ToString("n");
await p8Owner.conn.InvokeAsync<QueueEnqueueResult>(
    MeshHubProtocol.QueueEnqueue,
    new QueueEnqueue(p8OwnerId, p8PeerId, notifyOpId, "notify-payload"));
Check(await Within(queueAvailTcs.Task, 5000),
    "8b: online target receives DeviceQueueAvailable after enqueue");

// --- 8c: Own-queue isolation ---
var drain8c = await p8Peer.conn.InvokeAsync<QueueDrainResponse>(
    MeshHubProtocol.QueueDrain, new QueueDrainRequest(p8OwnerId, 64));
Check(drain8c.Entries.Count == 0,
    "8c: own-queue isolation: QueueDrain for a different device ID returns empty");

// --- 8d: Disconnect without ACK, then redeliver on reconnect ---
var preDrain8d = await p8Peer.conn.InvokeAsync<QueueDrainResponse>(
    MeshHubProtocol.QueueDrain, new QueueDrainRequest(p8PeerId, 64));
Check(preDrain8d.Entries.Count >= 1 && preDrain8d.Entries.All(e => e.TargetDeviceId == p8PeerId),
    "8d: peer drains its queue; all entries target the peer");

// Disconnect without ACKing; the server releases leases on OnDisconnected.
await p8Peer.conn.StopAsync();
await Task.Delay(400); // allow server-side OnDisconnectedAsync to release leases

// Reconnect; the same entries must be redeliverable.
var p8PeerRestart = await ConnectAsync(p8Handle, p8PeerPriv, p8PeerPub);
Check(await Within(p8PeerRestart.ready, 10000), "8d: peer reconnects after unacked disconnect");
var reDrain8d = await p8PeerRestart.conn.InvokeAsync<QueueDrainResponse>(
    MeshHubProtocol.QueueDrain, new QueueDrainRequest(p8PeerId, 64));
Check(reDrain8d.Entries.Count >= 1,
    "8d: entries are redelivered after disconnect without ACK");

// --- 8e: ACK removes entry exactly once, then subsequent drain is empty ---
foreach (var entry in reDrain8d.Entries)
{
    var ackOk = await p8PeerRestart.conn.InvokeAsync<bool>(
        MeshHubProtocol.QueueAck, new QueueAck(entry.EntryId, p8PeerId));
    Check(ackOk, $"8e: ACK for entry {entry.EntryId[..8]} returns true");
}
var postAck8e = await p8PeerRestart.conn.InvokeAsync<QueueDrainResponse>(
    MeshHubProtocol.QueueDrain, new QueueDrainRequest(p8PeerId, 64));
Check(postAck8e.Entries.Count == 0,
    "8e: drain after full ACK returns empty; entries removed exactly once");

// --- 8f: Third-device add/revoke preserves peer's queued work ---
var (p8ThirdPriv, p8ThirdPub) = Gen();
var p8ThirdId = DeviceProtocol.DeviceId(p8ThirdPub);

var p8ThirdRecovery = await http.PostAsJsonAsync(
    $"{relay}/handles/{p8Handle}/recover",
    new RecoverHandleRequest(p8Handle, p8ThirdPub,
        Sign(p8RecoveryPriv, RecoveryProtocol.Message(p8Handle, p8ThirdPub))));
Check(p8ThirdRecovery.IsSuccessStatusCode, "8f: third device added via recovery");

// Enqueue new work for peer before revoking third device.
var preserveOpId = Guid.NewGuid().ToString("n");
var preserveEntryId = DeviceQueueEntryIdProtocol.Create(p8OwnerId, p8PeerId, preserveOpId);
await p8Owner.conn.InvokeAsync<QueueEnqueueResult>(
    MeshHubProtocol.QueueEnqueue,
    new QueueEnqueue(p8OwnerId, p8PeerId, preserveOpId, "preserve-payload"));

// Revoke third device using owner's key.
var revokeThirdSig = Sign(p8OwnerPriv, DeviceRevocationProtocol.Message(p8Handle, p8ThirdId));
var revokeThirdResp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
    $"{relay}/handles/{p8Handle}/devices/{p8ThirdId}")
{
    Content = JsonContent.Create(new RevokeDeviceRequest(p8OwnerPub, p8ThirdId, revokeThirdSig))
});
Check(revokeThirdResp.IsSuccessStatusCode, "8f: third device revocation HTTP 200");

// Peer's queued work must still be drainable.
var survive8f = await p8PeerRestart.conn.InvokeAsync<QueueDrainResponse>(
    MeshHubProtocol.QueueDrain, new QueueDrainRequest(p8PeerId, 64));
Check(survive8f.Entries.Any(e => e.EntryId == preserveEntryId),
    "8f: revoking an unrelated device preserves the peer's queued work");
// ACK so queue is clean before next section.
foreach (var e in survive8f.Entries)
    await p8PeerRestart.conn.InvokeAsync<bool>(MeshHubProtocol.QueueAck, new QueueAck(e.EntryId, p8PeerId));

// --- 8g: Revoke target device ---
// Enqueue one item for the peer so there is something to purge on revocation.
var purgeOpId = Guid.NewGuid().ToString("n");
await p8Owner.conn.InvokeAsync<QueueEnqueueResult>(
    MeshHubProtocol.QueueEnqueue,
    new QueueEnqueue(p8OwnerId, p8PeerId, purgeOpId, "will-be-purged"));

var revokePeerSig = Sign(p8OwnerPriv, DeviceRevocationProtocol.Message(p8Handle, p8PeerId));
var revokePeerResp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
    $"{relay}/handles/{p8Handle}/devices/{p8PeerId}")
{
    Content = JsonContent.Create(new RevokeDeviceRequest(p8OwnerPub, p8PeerId, revokePeerSig))
});
var revokePeerBody = revokePeerResp.IsSuccessStatusCode
    ? await revokePeerResp.Content.ReadFromJsonAsync<RevokeDeviceResponse>()
    : null;
Check(revokePeerResp.IsSuccessStatusCode && revokePeerBody?.Revoked == true,
    "8g: revoke peer HTTP 200, Revoked=true");

// Future enqueue to revoked target must be rejected.
var postRevokeEnqueue = await p8Owner.conn.InvokeAsync<QueueEnqueueResult>(
    MeshHubProtocol.QueueEnqueue,
    new QueueEnqueue(p8OwnerId, p8PeerId, Guid.NewGuid().ToString("n"), "post-revoke"));
Check(!postRevokeEnqueue.Accepted && postRevokeEnqueue.Error == "sync_target_unknown",
    "8g: enqueue to revoked device returns sync_target_unknown");

// QueueDrain on revoked device's existing socket must return empty.
var revokedDrain = await p8PeerRestart.conn.InvokeAsync<QueueDrainResponse>(
    MeshHubProtocol.QueueDrain, new QueueDrainRequest(p8PeerId, 64));
Check(revokedDrain.Entries.Count == 0,
    "8g: revoked device's existing socket gets empty drain");

// Revoked device cannot authenticate on a new connection.
var revokedReconnect = await ConnectAsync(p8Handle, p8PeerPriv, p8PeerPub);
Check(!await Within(revokedReconnect.ready, 2000),
    "8g: revoked device cannot re-authenticate after reconnect");

// --- 8h: SendEnvelope with DeviceSyncKinds returns device_sync_use_queue ---
var syncKindBody = "sync-kind-test";
var syncKindEnv = MeshEnvelope.Create(
    p8Handle, p8Handle, DeviceSyncKinds.EnvelopeOperation,
    syncKindBody, Sign(p8OwnerPriv, syncKindBody));
var syncKindResult = await p8Owner.conn.InvokeAsync<MeshSendResult>(
    MeshHubProtocol.SendEnvelope, syncKindEnv);
Check(!syncKindResult.Accepted && syncKindResult.Code == "device_sync_use_queue",
    "8h: SendEnvelope with DeviceSyncKinds kind is rejected with device_sync_use_queue");

// --- 8i: Protocol 7 handshake returns VersionMismatch rejection ---
var (p7mmPriv, p7mmPub) = Gen();
var p7mmHandle = "p7mm" + Guid.NewGuid().ToString("n")[..8];
await Register(p7mmHandle, p7mmPub, p7mmPriv, "Protocol 7 Mismatch Test");
var mismatchReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var mmConn = new HubConnectionBuilder()
    .WithUrl($"{relay}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(p7mmHandle)}&deliveryAck=0&protocolVersion=7")
    .Build();
connections.Add(mmConn);
mmConn.On<HandshakeResponse>(MeshHubProtocol.Handshake, response =>
{
    if (response.Result == HandshakeResult.VersionMismatch)
        mismatchReadyTcs.TrySetResult();
    else
        mismatchReadyTcs.TrySetException(new InvalidOperationException(
            $"expected VersionMismatch, got {response.Result}"));
});
await mmConn.StartAsync();
Check(await Within(mismatchReadyTcs.Task, 5000),
    "8i: connecting with protocol version 7 is rejected with VersionMismatch");
// Clean up the mismatch handle immediately.
await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{relay}/handles/{p7mmHandle}")
{
    Content = JsonContent.Create(new DeleteHandleRequest(
        p7mmHandle, p7mmPub, Sign(p7mmPriv, DeleteProtocol.Message(p7mmHandle))))
});

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
await CleanupHandle(p8Handle, p8OwnerPub, p8OwnerPriv);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL SMOKE TESTS PASSED" : $"{failures} SMOKE TEST(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
