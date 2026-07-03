using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Relay.Storage;
using Mesh.Shared;

var builder = WebApplication.CreateBuilder(args);

// Cap REST request bodies. Message attachments travel over the hub (WebSocket), not REST, so
// REST payloads (registration, link, token broker, model prompt) are always small.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512 * 1024);

// ---- Durable storage + directed backplane (config-gated, in-memory by default) ------
// Cosmos connection => durable handle registry / invites / offline inbox.
// Redis connection  => multi-replica presence + directed per-node message forwarding.
var cosmosConn = Config(builder.Configuration, "COSMOS_CONNECTION", "Cosmos:Connection");
var redisConn = Config(builder.Configuration, "REDIS_CONNECTION", "Redis:Connection");

IRelayStore store = string.IsNullOrWhiteSpace(cosmosConn)
    ? new InMemoryRelayStore()
    : new CosmosRelayStore(cosmosConn, Config(builder.Configuration, "COSMOS_DB", "Cosmos:Database") ?? "mesh");

IBackplane backplane = string.IsNullOrWhiteSpace(redisConn)
    ? new InMemoryBackplane()
    : new RedisBackplane(redisConn);

builder.Services.AddSingleton(store);
builder.Services.AddSingleton(backplane);
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<MeshRouter>();
builder.Services.AddHostedService<PresenceRenewer>();

// SignalR provides the transport (connection, framing, keepalive, reconnection). Cross-node
// routing is done by MeshRouter + the directed backplane, NOT by a SignalR fan-out backplane,
// so we do NOT call AddStackExchangeRedis here on purpose.
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 12 * 1024 * 1024; // room for an encrypted attachment payload
    o.EnableDetailedErrors = false;
});

// Per-IP rate limiting on every REST endpoint (the hub has its own per-connection guards).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var brokerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var modelHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

// Per-handle daily counter for the hosted free model (cost control on the server side too).
var modelUsage = new ConcurrentDictionary<string, (string day, int count)>(StringComparer.OrdinalIgnoreCase);

app.UseRateLimiter();

// When another instance forwards a message to this one, deliver it to the local hub connections.
var router = app.Services.GetRequiredService<MeshRouter>();
await backplane.StartAsync(async (toHandle, envelopeJson) =>
{
    await router.DeliverLocalAsync(toHandle, envelopeJson);
});

// ---- Health ---------------------------------------------------------------
app.MapGet("/", () => Results.Ok(new { service = "Mesh.Relay", status = "ok", instance = backplane.InstanceId }));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

// ---- Handle registry (REST) ----------------------------------------------
app.MapPost("/handles", async (RegisterHandleRequest req) =>
{
    var handle = Normalize(req.Handle);
    if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(req.DevicePublicKey))
        return Results.BadRequest(new { error = "handle and devicePublicKey are required" });

    var existing = await store.GetHandleAsync(handle);
    if (existing is null)
    {
        // First registration CLAIMS the handle for this device key.
        var (created, _) = await store.UpsertHandleAsync(handle, req.DevicePublicKey, req.DisplayName, allowNewDevice: true);
        return Results.Ok(new RegisterHandleResponse(handle, DeviceIdOf(req.DevicePublicKey), created.RegisteredAt));
    }

    if (existing.DevicePublicKeys.Contains(req.DevicePublicKey))
    {
        // Re-asserting an already authorized device is idempotent (normal launch).
        if (req.DisplayName is not null) await store.SetDisplayNameAsync(handle, req.DisplayName);
        return Results.Ok(new RegisterHandleResponse(handle, DeviceIdOf(req.DevicePublicKey), existing.RegisteredAt));
    }

    // A different key cannot silently join a claimed handle; it must use device linking.
    return Results.Conflict(new { error = "handle already claimed; use device linking to add another device" });
});

// Device linking: an authorized device issues a short-lived, single-use invite.
app.MapPost("/handles/{handle}/link/invite", async (string handle, LinkInviteRequest req) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();

    if (!rec.DevicePublicKeys.Contains(req.CreatorPublicKey))
        return Results.Json(new { error = "creator is not an authorized device" }, statusCode: StatusCodes.Status403Forbidden);

    var expires = DateTimeOffset.FromUnixTimeSeconds(req.ExpiresAtUnix);
    if (expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddMinutes(15))
        return Results.BadRequest(new { error = "invalid expiry (must be in the future, within 15 minutes)" });

    var message = LinkProtocol.InviteMessage(key, req.CodeHash, req.ExpiresAtUnix);
    if (!MeshCrypto.Verify(req.CreatorPublicKey, message, req.Signature))
        return Results.BadRequest(new { error = "invalid signature" });

    await store.AddInviteAsync(new StoredInvite { Handle = key, CodeHash = req.CodeHash, ExpiresAt = expires });
    return Results.Ok(new LinkInviteResponse(key, req.ExpiresAtUnix));
});

// Device linking: the new device redeems the invite with its own key.
app.MapPost("/handles/{handle}/link/redeem", async (string handle, LinkRedeemRequest req) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.NewPublicKey) || string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "newPublicKey and code are required" });

    if (!MeshCrypto.Verify(req.NewPublicKey, LinkProtocol.RedeemMessage(key, req.Code), req.Signature))
        return Results.BadRequest(new { error = "invalid signature" });

    var codeHash = LinkProtocol.HashCode(req.Code);
    if (!await store.ConsumeInviteAsync(key, codeHash))
        return Results.BadRequest(new { error = "invite invalid, already used, or expired" });

    var (updated, _) = await store.UpsertHandleAsync(key, req.NewPublicKey, displayName: null, allowNewDevice: true);
    return Results.Ok(new LinkRedeemResponse(key, DeviceIdOf(req.NewPublicKey), updated.DisplayName));
});

// ---- Connector token broker ----------------------------------------------
app.MapPost("/connectors/{provider}/token", async (string provider, ConnectorTokenRequest req) =>
{
    var ep = ConnectorCatalog.Get(provider);
    if (ep is null || !ep.Confidential)
        return Results.BadRequest(new { error = "unknown or non-brokered connector" });
    if (req.GrantType is not (ConnectorProtocol.GrantAuthCode or ConnectorProtocol.GrantRefresh))
        return Results.BadRequest(new { error = "unsupported grant_type" });

    var handleKey = Normalize(req.Handle);
    var rec = await store.GetHandleAsync(handleKey);
    if (rec is null || !rec.DevicePublicKeys.Contains(req.DevicePublicKey))
        return Results.Json(new { error = "unknown device for handle" }, statusCode: StatusCodes.Status403Forbidden);

    var secretMaterial = ConnectorProtocol.SecretMaterial(req.GrantType, req.Code, req.RefreshToken);
    if (string.IsNullOrWhiteSpace(secretMaterial))
        return Results.BadRequest(new { error = "missing code or refresh_token" });
    var secretHash = LinkProtocol.HashCode(secretMaterial);
    var message = ConnectorProtocol.TokenMessage(provider, handleKey, req.GrantType, secretHash, req.RedirectUri);
    if (!MeshCrypto.Verify(req.DevicePublicKey, message, req.Signature))
        return Results.BadRequest(new { error = "invalid signature" });

    var secret = ConnectorSecret(provider);
    if (string.IsNullOrWhiteSpace(secret))
        return Results.Json(new { error = "connector not configured on relay" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    using var exchange = new HttpRequestMessage(HttpMethod.Post, ep.TokenUrl);
    var form = new Dictionary<string, string> { ["grant_type"] = req.GrantType };
    if (req.GrantType == ConnectorProtocol.GrantAuthCode)
    {
        form["code"] = req.Code!;
        if (!string.IsNullOrWhiteSpace(req.RedirectUri)) form["redirect_uri"] = req.RedirectUri!;
        if (!string.IsNullOrWhiteSpace(req.CodeVerifier)) form["code_verifier"] = req.CodeVerifier!;
    }
    else
    {
        form["refresh_token"] = req.RefreshToken!;
    }
    if (ep.UseBasicAuth)
        exchange.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ep.ClientId}:{secret}")));
    else
    {
        form["client_id"] = ep.ClientId;
        form["client_secret"] = secret;
    }
    exchange.Content = new FormUrlEncodedContent(form);

    using var resp = await brokerHttp.SendAsync(exchange);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        return Results.Json(new { error = "provider token exchange failed", detail = body }, statusCode: StatusCodes.Status502BadGateway);

    return Results.Ok(new ConnectorTokenResponse(body));
});

// ---- Hosted free model proxy ---------------------------------------------
// The relay holds the upstream model key server-side and proxies a completion so first-launch
// users get a working model with no key of their own. Authenticated by device key, rate limited
// per handle per day. Returns 503 when the relay has no model key configured.
app.MapPost("/model/chat", async (HostedModelRequest req) =>
{
    var handleKey = Normalize(req.Handle);
    var rec = await store.GetHandleAsync(handleKey);
    if (rec is null || !rec.DevicePublicKeys.Contains(req.DevicePublicKey))
        return Results.Json(new { error = "unknown device for handle" }, statusCode: StatusCodes.Status403Forbidden);

    var promptHash = HostedModelProtocol.PromptHash(req.SystemPrompt, req.Messages);
    if (!MeshCrypto.Verify(req.DevicePublicKey, HostedModelProtocol.Message(handleKey, promptHash), req.Signature))
        return Results.BadRequest(new { error = "invalid signature" });

    var apiKey = Config(app.Configuration, "MODEL_API_KEY", "Model:ApiKey");
    var endpoint = Config(app.Configuration, "MODEL_ENDPOINT", "Model:Endpoint");
    var kind = (Config(app.Configuration, "MODEL_KIND", "Model:Kind") ?? "azure").ToLowerInvariant();
    // Every provider needs a key. Azure and OpenAI-compatible providers also need an endpoint;
    // Gemini defaults to Google's public endpoint, so an endpoint is optional there.
    if (string.IsNullOrWhiteSpace(apiKey) || (kind != "gemini" && string.IsNullOrWhiteSpace(endpoint)))
        return Results.Json(new { error = "hosted model not configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    // Per-handle daily quota.
    var dailyLimit = int.TryParse(Config(app.Configuration, "MODEL_DAILY_LIMIT", "Model:DailyLimit"), out var dl) ? dl : 50;
    var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
    var usage = modelUsage.AddOrUpdate(handleKey,
        _ => (today, 1),
        (_, cur) => cur.day == today ? (today, cur.count + 1) : (today, 1));
    if (dailyLimit > 0 && usage.count > dailyLimit)
        return Results.Json(new { error = "daily free-model limit reached" }, statusCode: StatusCodes.Status429TooManyRequests);

    var model = Config(app.Configuration, "MODEL_NAME", "Model:Deployment")
                ?? Config(app.Configuration, "MODEL_NAME2", "Model:Model")
                ?? (kind == "gemini" ? "gemini-2.0-flash" : "gpt-4o-mini");

    try
    {
        // Google Gemini uses a different request/response shape (contents/parts, not OpenAI chat).
        // The client's own GeminiModel uses the same native API, so this mirrors it server-side.
        if (kind == "gemini")
        {
            var gBase = string.IsNullOrWhiteSpace(endpoint)
                ? "https://generativelanguage.googleapis.com" : endpoint!.TrimEnd('/');
            var gUrl = $"{gBase}/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey!)}";
            var contents = req.Messages
                .Where(m => m.Role is "user" or "assistant")
                .Select(m => (object)new { role = m.Role == "assistant" ? "model" : "user", parts = new[] { new { text = m.Content } } })
                .ToList();
            if (contents.Count == 0) contents.Add(new { role = "user", parts = new[] { new { text = "Hello" } } });
            var gPayload = new { system_instruction = new { parts = new[] { new { text = req.SystemPrompt } } }, contents };

            using var gResp = await modelHttp.PostAsJsonAsync(gUrl, gPayload);
            var gBody = await gResp.Content.ReadAsStringAsync();
            if (!gResp.IsSuccessStatusCode)
                return Results.Json(new { error = "upstream model error", detail = Trim(gBody) }, statusCode: StatusCodes.Status502BadGateway);
            using var gDoc = JsonDocument.Parse(gBody);
            var gText = gDoc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            return Results.Ok(new HostedModelResponse(gText));
        }

        // OpenAI-style chat payload: Azure OpenAI (api-key header + deployment URL) or an
        // OpenAI-compatible endpoint (Bearer + /v1/chat/completions).
        // Build messages with tool support: an assistant turn may carry tool_calls, and a
        // "tool" role message carries a tool result (tool_call_id + content). Tools themselves
        // are executed on the CLIENT; the relay only forwards definitions and returns tool_calls.
        var messages = new List<object> { new { role = "system", content = req.SystemPrompt } };
        foreach (var m in req.Messages)
        {
            if (m.Role == "tool" && m.ToolCallId is not null)
                messages.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content });
            else if (m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.ToolCallsJson))
                messages.Add(new { role = "assistant", content = (string?)m.Content, tool_calls = JsonDocument.Parse(m.ToolCallsJson!).RootElement.Clone() });
            else
                messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
        }

        object payload = new { model, messages, max_tokens = 1024 };
        if (!string.IsNullOrWhiteSpace(req.ToolsJson))
            payload = new { model, messages, max_tokens = 1024, tools = JsonDocument.Parse(req.ToolsJson!).RootElement.Clone() };

        string url;
        using var upstream = new HttpRequestMessage(HttpMethod.Post, (Uri?)null);
        if (kind == "azure")
        {
            var apiVersion = Config(app.Configuration, "MODEL_API_VERSION", "Model:ApiVersion") ?? "2024-08-01-preview";
            url = $"{endpoint!.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version={apiVersion}";
            upstream.Headers.TryAddWithoutValidation("api-key", apiKey);
        }
        else
        {
            url = $"{endpoint!.TrimEnd('/')}/v1/chat/completions";
            upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        upstream.RequestUri = new Uri(url);
        upstream.Content = JsonContent.Create(payload);

        using var resp = await modelHttp.SendAsync(upstream);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { error = "upstream model error", detail = Trim(body) }, statusCode: StatusCodes.Status502BadGateway);
        using var doc = JsonDocument.Parse(body);
        var respMsg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = respMsg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        string? toolCallsJson = null;
        if (respMsg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
            toolCallsJson = tcs.GetRawText();
        return Results.Ok(new HostedModelResponse(content, toolCallsJson));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "model proxy failed", detail = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/handles/{handle}", async (string handle) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();
    var online = await backplane.GetInstanceForAsync(key) is not null;
    return Results.Ok(new HandleInfo(rec.Handle, rec.DisplayName, rec.DevicePublicKeys, online, rec.RegisteredAt));
});

// ---- SignalR transport hub ------------------------------------------------
app.MapHub<MeshHub>(MeshHubProtocol.Route);

app.Run();
return;

// ---- helpers --------------------------------------------------------------
static string Normalize(string handle)
    => handle.Trim().TrimStart('@').ToLowerInvariant();

static string DeviceIdOf(string publicKey)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(publicKey)))[..12].ToLowerInvariant();

static string Trim(string s) => s.Length > 300 ? s[..300] : s;

// Config lookup: environment variable first, then configuration key.
static string? Config(IConfiguration cfg, string envVar, string configKey)
{
    var v = Environment.GetEnvironmentVariable(envVar);
    return !string.IsNullOrWhiteSpace(v) ? v : cfg[configKey];
}

// Server-side connector client secret, from configuration (env: Connectors__notion__secret)
// or a CONNECTOR_NOTION_SECRET fallback. Never shipped in the client.
string? ConnectorSecret(string provider)
    => app.Configuration[$"Connectors:{provider.ToLowerInvariant()}:secret"]
       ?? Environment.GetEnvironmentVariable($"CONNECTOR_{provider.ToUpperInvariant()}_SECRET");
