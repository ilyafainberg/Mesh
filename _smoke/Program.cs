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
var aliceHandle = "alice" + Random.Shared.Next(1000, 9999);
var bobHandle = "bob" + Random.Shared.Next(1000, 9999);

// Proof-of-possession registration helper: sign the claim with the device private key.
async Task<System.Net.Http.HttpResponseMessage> Register(string handle, string pub, string priv, string? display, string? recoveryPub = null)
{
    var sig = Sign(priv, ClaimProtocol.Message(handle, pub));
    return await http.PostAsJsonAsync($"{relay}/handles",
        new RegisterHandleRequest(handle, pub, display, recoveryPub, sig));
}

// 1. Register both handles (with proof of possession).
var r1 = await Register(aliceHandle, aPub, aPriv, "Alice");
var r2 = await Register(bobHandle, bPub, bPriv, "Bob");
Check(r1.IsSuccessStatusCode && r2.IsSuccessStatusCode, "register alice + bob");

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
var (cPriv, cPub) = Gen();
var rTakeover = await Register(aliceHandle, cPub, cPriv, "Impostor");
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

async Task<(HubConnection conn, ConcurrentQueue<MeshEnvelope> inbox, Task ready)> ConnectAsync(string handle, string priv, string pub)
{
    var conn = new HubConnectionBuilder()
        .WithUrl($"{relay}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(handle)}")
        .Build();
    var inbox = new ConcurrentQueue<MeshEnvelope>();
    var readyTcs = new TaskCompletionSource();
    conn.On<string>(MeshHubProtocol.Challenge, async nonce =>
    {
        var sig = Sign(priv, nonce);
        await conn.InvokeAsync(MeshHubProtocol.Authenticate, pub, sig);
    });
    conn.On(MeshHubProtocol.Ready, () => readyTcs.TrySetResult());
    conn.On<string>(MeshHubProtocol.Receive, json =>
    {
        var e = JsonSerializer.Deserialize<MeshEnvelope>(json, web);
        if (e is not null) inbox.Enqueue(e);
    });
    await conn.StartAsync();
    return (conn, inbox, readyTcs.Task);
}

static async Task<bool> Within(Task t, int ms) => await Task.WhenAny(t, Task.Delay(ms)) == t;

// 2. Both online: challenge/response auth then routed delivery.
var alice = await ConnectAsync(aliceHandle, aPriv, aPub);
var bob = await ConnectAsync(bobHandle, bPriv, bPub);
Check(await Within(alice.ready, 10000), "alice authenticated (challenge/response)");
Check(await Within(bob.ready, 10000), "bob authenticated (challenge/response)");

// Alice sends an E2E-encrypted, signed message to Bob.
var plaintext = "hello bob, this is end to end encrypted";
var wire = MessageCrypto.Encrypt(plaintext, new[] { bPub }) ?? plaintext;
Check(MessageCrypto.IsEncrypted(wire), "message body is encrypted on the wire");
var env = MeshEnvelope.Create(aliceHandle, bobHandle, MeshKinds.Chat, wire, Sign(aPriv, wire));
await alice.conn.InvokeAsync(MeshHubProtocol.SendEnvelope, env);

await Task.Delay(1200);
var gotOnline = bob.inbox.TryDequeue(out var recv);
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
await alice.conn.InvokeAsync(MeshHubProtocol.SendEnvelope, env2);
await Task.Delay(800);

var bob2 = await ConnectAsync(bobHandle, bPriv, bPub);
Check(await Within(bob2.ready, 10000), "bob reconnected + authenticated");
await Task.Delay(1200);
var gotOffline = bob2.inbox.TryDequeue(out var recv2);
Check(gotOffline, "bob received queued offline message on reconnect");
if (gotOffline)
{
    var (ok2, dec2) = MessageCrypto.TryDecrypt(recv2!.Body, bPriv, bPub);
    Check(ok2 && dec2 == offlineText, "offline message decrypts correctly");
}

// 4. Forged signature is rejected by the hub (bad body signature).
var badEnv = MeshEnvelope.Create(aliceHandle, bobHandle, MeshKinds.Chat, "tampered", "not-a-valid-signature");
await alice.conn.InvokeAsync(MeshHubProtocol.SendEnvelope, badEnv);
await Task.Delay(800);
Check(bob2.inbox.IsEmpty, "hub drops message with invalid signature");

await alice.conn.DisposeAsync();
await bob2.conn.DisposeAsync();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL SMOKE TESTS PASSED" : $"{failures} SMOKE TEST(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
