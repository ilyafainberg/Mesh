using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

// End-to-end tool-calling test for the relay-hosted free model. Simulates what
// MeshHostedModel.CompleteWithToolsAsync does: sends tools, gets back tool_calls,
// executes the tool locally, sends the result, and expects a grounded final answer.
var relay = (args.Length > 0 ? args[0] : "https://meshrelay.net").TrimEnd('/');
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
var handle = "tool" + Random.Shared.Next(10000, 99999);
await http.PostAsJsonAsync($"{relay}/handles", new RegisterHandleRequest(handle, pub, "ToolTest"));

// A tool the "agent" exposes. The model should call it, we return a secret number,
// and the model must report that exact number back, proving the round-trip worked.
var secret = "42931";
var toolsJson = JsonSerializer.Serialize(new object[] { new {
    type = "function",
    function = new {
        name = "get_account_balance",
        description = "Returns the user's current account balance in euros.",
        parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
    }
}}, web);

var sys = "You are a helpful assistant. When asked about balance, you MUST call the get_account_balance tool, then state the number.";
var msgs = new List<HostedModelMessage> { new("user", "What is my account balance? Use the tool, then tell me the exact number.") };

async Task<HostedModelResponse?> Call(List<HostedModelMessage> messages)
{
    var ph = HostedModelProtocol.PromptHash(sys, messages);
    var sg = Sign(priv, HostedModelProtocol.Message(handle, ph));
    var reqObj = new HostedModelRequest(LinkProtocol.Normalize(handle), pub, sg, sys, messages, toolsJson);
    var r = await http.PostAsJsonAsync($"{relay}/model/chat", reqObj);
    if (!r.IsSuccessStatusCode) { Console.WriteLine($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}"); return null; }
    return JsonSerializer.Deserialize<HostedModelResponse>(await r.Content.ReadAsStringAsync(), web);
}

var toolWasCalled = false;
for (var round = 0; round < 5; round++)
{
    var res = await Call(msgs);
    if (res is null) { Console.WriteLine("FAIL: no response"); return; }

    if (string.IsNullOrWhiteSpace(res.ToolCallsJson))
    {
        Console.WriteLine("FINAL: " + res.Content);
        // Accept the number with or without thousands separators/currency (a smarter model may
        // format it as "42,931" or "EUR 42,931").
        var normalized = res.Content.Replace(",", "").Replace(".", "");
        var ok = toolWasCalled && (res.Content.Contains(secret) || normalized.Contains(secret));
        Console.WriteLine(ok ? "PASS: hosted free model called the tool and reported the tool's value"
                             : $"FAIL: toolCalled={toolWasCalled}, containsSecret={res.Content.Contains(secret)}");
        return;
    }

    // Model requested tools: record its turn, execute locally, append results.
    toolWasCalled = true;
    Console.WriteLine("MODEL requested tool_calls: " + res.ToolCallsJson);
    msgs.Add(new HostedModelMessage("assistant", res.Content ?? "", ToolCallsJson: res.ToolCallsJson));
    using var calls = JsonDocument.Parse(res.ToolCallsJson);
    foreach (var call in calls.RootElement.EnumerateArray())
    {
        var id = call.GetProperty("id").GetString();
        var name = call.GetProperty("function").GetProperty("name").GetString();
        var result = name == "get_account_balance" ? $"{{\"balance_eur\": {secret}}}" : "unknown tool";
        msgs.Add(new HostedModelMessage("tool", result, ToolCallId: id));
    }
}
Console.WriteLine("FAIL: exceeded tool rounds");
