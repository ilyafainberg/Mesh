using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Mesh.Shared;

var relay = "http://127.0.0.1:8790";
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

// 1. Register both handles.
var r1 = await http.PostAsJsonAsync($"{relay}/handles", new RegisterHandleRequest(aliceHandle, aPub, "Alice"));
var r2 = await http.PostAsJsonAsync($"{relay}/handles", new RegisterHandleRequest(bobHandle, bPub, "Bob"));
Check(r1.IsSuccessStatusCode && r2.IsSuccessStatusCode, "register alice + bob");

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
