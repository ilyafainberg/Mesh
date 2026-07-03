using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

// Verifies the relay-hosted free model end to end: registers a handle, signs a
// HostedModelRequest, calls /model/chat, and confirms a real completion returns.
var relay = (args.Length > 0 ? args[0] : "https://mesh-relay.whiteground-796c60f9.northeurope.azurecontainerapps.io").TrimEnd('/');
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var http = new HttpClient();

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

var (priv, pub) = Gen();
var handle = "hm" + Random.Shared.Next(10000, 99999);
var reg = await http.PostAsJsonAsync($"{relay}/handles", new RegisterHandleRequest(handle, pub, "HostedTest"));
Console.WriteLine($"register: {(int)reg.StatusCode}");

var sys = "You are a helpful assistant. Answer in one short sentence.";
var msgs = new List<HostedModelMessage> { new("user", "Say the single word: pong") };
var promptHash = HostedModelProtocol.PromptHash(sys, msgs);
var sig = Sign(priv, HostedModelProtocol.Message(handle, promptHash));
var req = new HostedModelRequest(LinkProtocol.Normalize(handle), pub, sig, sys, msgs);

var resp = await http.PostAsJsonAsync($"{relay}/model/chat", req);
var body = await resp.Content.ReadAsStringAsync();
Console.WriteLine($"/model/chat: {(int)resp.StatusCode}");
if (resp.IsSuccessStatusCode)
{
    var r = JsonSerializer.Deserialize<HostedModelResponse>(body, web);
    Console.WriteLine("REPLY: " + (r?.Content ?? "(empty)"));
    Console.WriteLine(string.IsNullOrWhiteSpace(r?.Content) ? "FAIL: empty completion" : "PASS: hosted free model returned a completion");
}
else
{
    Console.WriteLine("FAIL body: " + (body.Length > 300 ? body[..300] : body));
}
