using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Relay.Observability;
using Mesh.Relay.Quota;
using Mesh.Relay.RateLimiting;
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

// Durable per-handle free-model quota: Redis in production (exact + shared across replicas),
// in-memory as the single-instance default.
IQuotaStore quota = string.IsNullOrWhiteSpace(redisConn)
    ? new InMemoryQuotaStore()
    : new RedisQuotaStore(redisConn);

builder.Services.AddSingleton(store);
builder.Services.AddSingleton(backplane);
builder.Services.AddSingleton(quota);
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<MeshRouter>();
builder.Services.AddSingleton<Mesh.Relay.RelayConnectorCatalog>();
builder.Services.AddHostedService<PresenceRenewer>();

// Aggregate ops counters (no PII): scraped via GET /metrics.
var metrics = new RelayMetrics();
builder.Services.AddSingleton(metrics);

// Per-handle message rate limiter for the hub (in-memory, no external service required).
// MESH_MSG_RATE_PER_MIN: steady messages/minute per handle (default 120).
// MESH_MSG_BURST:        back-to-back burst capacity per handle (default 30).
var msgRatePerMin = int.TryParse(Config(builder.Configuration, "MESH_MSG_RATE_PER_MIN", "Mesh:MessageRatePerMinute"), out var rpm) ? rpm : 120;
var msgBurst = int.TryParse(Config(builder.Configuration, "MESH_MSG_BURST", "Mesh:MessageBurst"), out var mb) ? mb : 30;
builder.Services.AddSingleton(new PerHandleRateLimiter(msgRatePerMin, msgBurst));

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

app.UseRateLimiter();

// When another instance forwards a message to this one, deliver it to the local hub connections.
var router = app.Services.GetRequiredService<MeshRouter>();
var connectorCatalog = app.Services.GetRequiredService<Mesh.Relay.RelayConnectorCatalog>();
await backplane.StartAsync(async (toHandle, envelopeJson) =>
{
    // A cross-instance forward may target one specific device (MeshEnvelope.ToDevice). Parse it out of
    // the envelope JSON so the owning instance re-applies the same per-device filter on local delivery.
    string? toDevice = null;
    try
    {
        toDevice = JsonSerializer.Deserialize<MeshEnvelope>(envelopeJson, json)?.ToDevice;
    }
    catch (JsonException)
    {
        toDevice = null; // Malformed payload: fall back to a normal broadcast.
    }
    await router.DeliverLocalAsync(toHandle, envelopeJson, excludeConnectionId: null, toDevice: toDevice);
});

// ---- Health ---------------------------------------------------------------
app.MapGet("/", () => Results.Ok(new { service = "Mesh.Relay", status = "ok", instance = backplane.InstanceId }));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

// ---- Metrics (aggregate counts only, no handles/PII) ----------------------
// Unauthenticated read so ops can scrape it; exposes only process-wide totals + a live gauge.
app.MapGet("/metrics", () =>
{
    var s = metrics.Snapshot();
    return Results.Ok(new
    {
        handlesRegistered = s.HandlesRegistered,
        messagesRouted = s.MessagesRouted,
        hostedModelCalls = s.HostedModelCalls,
        rateLimitRejections = s.RateLimitRejections,
        connected = s.Connected,
        time = DateTimeOffset.UtcNow
    });
});

// ---- Handle registry (REST) ----------------------------------------------
app.MapPost("/handles", async (RegisterHandleRequest req) =>
{
    var handle = Normalize(req.Handle);
    if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(req.DevicePublicKey))
        return Results.BadRequest(new { error = "handle and devicePublicKey are required" });

    // Collision avoidance / proof of possession: the registrant must sign the claim with the
    // device PRIVATE key. This stops anyone claiming or re-asserting a handle with a key they do
    // not control (for example pre-registering someone else's known key). Taking over a handle
    // held by a DIFFERENT key still requires device linking or recovery, enforced below.
    if (string.IsNullOrWhiteSpace(req.Signature)
        || !MeshCrypto.Verify(req.DevicePublicKey, ClaimProtocol.Message(handle, req.DevicePublicKey), req.Signature))
    {
        app.Logger.LogWarning("register rejected (invalid claim signature): {Handle}", handle);
        return Results.BadRequest(new { error = "invalid or missing claim signature" });
    }

    var existing = await store.GetHandleAsync(handle);
    if (existing is null)
    {
        // First registration CLAIMS the handle for this device key.
        var (created, _) = await store.UpsertHandleAsync(handle, req.DevicePublicKey, req.DisplayName, allowNewDevice: true);
        // Capture the recovery public key at registration so a future device can recover the handle.
        if (!string.IsNullOrWhiteSpace(req.RecoveryPublicKey))
            await store.SetRecoveryKeyAsync(handle, req.RecoveryPublicKey);
        // Record the friendly device name (if provided) so the per-device directory can show it.
        if (!string.IsNullOrWhiteSpace(req.DeviceName))
            await store.SetDeviceNameAsync(handle, DeviceProtocol.DeviceId(req.DevicePublicKey), req.DeviceName);
        metrics.HandleRegistered();
        app.Logger.LogInformation("handle registered: {Handle}", handle);
        return Results.Ok(new RegisterHandleResponse(handle, DeviceProtocol.DeviceId(req.DevicePublicKey), created.RegisteredAt));
    }

    if (existing.DevicePublicKeys.Contains(req.DevicePublicKey))
    {
        // Re-asserting an already authorized device is idempotent (normal launch).
        if (req.DisplayName is not null) await store.SetDisplayNameAsync(handle, req.DisplayName);
        // First-writer-wins: adopt a recovery key on re-register only if none is stored yet.
        if (existing.RecoveryPublicKey is null && !string.IsNullOrWhiteSpace(req.RecoveryPublicKey))
            await store.SetRecoveryKeyAsync(handle, req.RecoveryPublicKey);
        // Refresh the friendly device name (if provided) on re-register.
        if (!string.IsNullOrWhiteSpace(req.DeviceName))
            await store.SetDeviceNameAsync(handle, DeviceProtocol.DeviceId(req.DevicePublicKey), req.DeviceName);
        return Results.Ok(new RegisterHandleResponse(handle, DeviceProtocol.DeviceId(req.DevicePublicKey), existing.RegisteredAt));
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
    return Results.Ok(new LinkRedeemResponse(key, DeviceProtocol.DeviceId(req.NewPublicKey), updated.DisplayName));
});

// Handle recovery: a brand-new device authorizes itself under an existing handle by proving
// possession of the handle's recovery private key. Used when no existing device is available to
// issue a link invite. Covered by the per-IP rate limiter like every other REST endpoint.
app.MapPost("/handles/{handle}/recover", async (string handle, RecoverHandleRequest req) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(rec.RecoveryPublicKey))
    {
        app.Logger.LogWarning("recover failed (not available): {Handle}", key);
        return Results.BadRequest(new { error = "recovery not available for this handle" });
    }

    if (string.IsNullOrWhiteSpace(req.NewPublicKey))
    {
        app.Logger.LogWarning("recover failed (missing key): {Handle}", key);
        return Results.BadRequest(new { error = "newPublicKey is required" });
    }

    var message = RecoveryProtocol.Message(key, req.NewPublicKey);
    if (!MeshCrypto.Verify(rec.RecoveryPublicKey, message, req.RecoverySignature))
    {
        app.Logger.LogWarning("recover failed (invalid signature): {Handle}", key);
        return Results.BadRequest(new { error = "invalid recovery signature" });
    }

    var (updated, _) = await store.UpsertHandleAsync(key, req.NewPublicKey, displayName: null, allowNewDevice: true);
    app.Logger.LogInformation("recover succeeded: {Handle}", key);
    return Results.Ok(new RegisterHandleResponse(key, DeviceProtocol.DeviceId(req.NewPublicKey), updated.RegisteredAt));
});

// ---- Connectors: public catalog + token broker ---------------------------
// Public connector metadata (authorize/token URLs + public client ids), so the client does not
// ship any OAuth app ids itself. No secrets are exposed here.
app.MapGet("/connectors", (Mesh.Relay.RelayConnectorCatalog catalog) => Results.Ok(catalog.All));

app.MapPost("/connectors/{provider}/token", async (string provider, ConnectorTokenRequest req) =>
{
    var ep = connectorCatalog.Get(provider);
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

    // The hosted free model is an OpenAI-compatible chat endpoint (OpenRouter by default). Only a
    // key + endpoint are needed; there is no provider branching. Tools execute on the client,
    // so the relay just forwards tool definitions and returns the model's tool_calls.
    var apiKey = Config(app.Configuration, "MODEL_API_KEY", "Model:ApiKey");
    var endpoint = Config(app.Configuration, "MODEL_ENDPOINT", "Model:Endpoint") ?? "https://openrouter.ai/api";
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new { error = "hosted model not configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    // Durable per-handle daily TOKEN quota (Redis in prod). Tokens are the primary cost currency,
    // so metering counts the upstream-reported token usage, not the request count. Check the
    // running daily total before serving; only successful completions add to it below.
    var tokenLimit = long.TryParse(Config(app.Configuration, "MODEL_DAILY_TOKEN_LIMIT", "Model:DailyTokenLimit"), out var tl) ? tl : 100000L;
    var usedTokens = await quota.GetDailyAsync(handleKey);
    if (tokenLimit > 0 && usedTokens >= tokenLimit)
        return Results.Json(new { error = "daily free-model token limit reached" }, statusCode: StatusCodes.Status429TooManyRequests);

    var model = Config(app.Configuration, "MODEL_NAME", "Model:Model") ?? "openrouter/auto";

    try
    {
        // Build messages with tool support: an assistant turn may carry tool_calls, and a
        // "tool" role message carries a tool result (tool_call_id + content).
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

        using var upstream = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/v1/chat/completions");
        upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // OpenRouter uses these optional headers for app attribution/ranking; harmless on other
        // OpenAI-compatible providers, which ignore unknown headers.
        upstream.Headers.TryAddWithoutValidation("HTTP-Referer", "https://meshrelay.net");
        upstream.Headers.TryAddWithoutValidation("X-Title", "Mesh");
        upstream.Content = JsonContent.Create(payload);

        using var resp = await modelHttp.SendAsync(upstream);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // Upstream failure (bad/expired shared key, upstream rate limit, provider outage) is
            // a server-side problem, not the user's fault: there is nothing to meter on failure,
            // so surface a single "temporarily unavailable" status (503), distinct from the
            // per-user 429.
            app.Logger.LogWarning("hosted model upstream failed ({Status}): {Detail}", (int)resp.StatusCode, Trim(body));
            return Results.Json(new { error = "hosted model temporarily unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        using var doc = JsonDocument.Parse(body);
        var respMsg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = respMsg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        string? toolCallsJson = null;
        if (respMsg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
            toolCallsJson = tcs.GetRawText();

        // Meter the completion in tokens as reported by the upstream "usage" object. When
        // total_tokens is absent, fall back to prompt + completion. Only successful completions
        // are counted.
        var (promptTokens, completionTokens, totalTokens) = ReadUsage(doc.RootElement);
        await quota.AddDailyAsync(handleKey, totalTokens);
        metrics.HostedModelCall();
        app.Logger.LogInformation("hosted model call: {Handle} tokens={Tokens}", handleKey, totalTokens);
        return Results.Ok(new HostedModelResponse(content, toolCallsJson, promptTokens, completionTokens, totalTokens));
    }
    catch (Exception ex)
    {
        // Network/parse failure: there is nothing to meter, so just surface a 503.
        app.Logger.LogWarning(ex, "hosted model proxy failed");
        return Results.Json(new { error = "hosted model temporarily unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
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

// Per-device directory: one DeviceInfo per registered device key, with its friendly name (if set)
// and whether it is currently connected. Lets a client offer a "home device" picker and route a
// remote-agent request to exactly one device. Online is best-effort per-instance presence for now.
app.MapGet("/handles/{handle}/devices", async (string handle, ConnectionRegistry registry) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();

    var onlineDevices = registry.OnlineDeviceIds(key);
    var devices = rec.DevicePublicKeys
        .Select(pubkey => DeviceProtocol.DeviceId(pubkey))
        .Distinct()
        .Select(deviceId => new DeviceInfo(
            deviceId,
            rec.DeviceNames.GetValueOrDefault(deviceId),
            onlineDevices.Contains(deviceId)))
        .ToArray();

    return Results.Ok(devices);
});

app.MapDelete("/handles/{handle}", async (string handle, [Microsoft.AspNetCore.Mvc.FromBody] DeleteHandleRequest req) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();

    // Only a device currently authorized under the handle can release it. Verify the presented key
    // is registered AND that it signed the delete request (proof of possession), so nobody else can
    // free someone's name.
    if (string.IsNullOrWhiteSpace(req.DevicePublicKey)
        || !rec.DevicePublicKeys.Contains(req.DevicePublicKey)
        || string.IsNullOrWhiteSpace(req.Signature)
        || !MeshCrypto.Verify(req.DevicePublicKey, DeleteProtocol.Message(key), req.Signature))
    {
        app.Logger.LogWarning("delete rejected (unauthorized): {Handle}", key);
        return Results.BadRequest(new { error = "not authorized to delete this handle" });
    }

    var removed = await store.DeleteHandleAsync(key);
    await backplane.ClearPresenceAsync(key);
    app.Logger.LogInformation("handle deleted: {Handle}", key);
    return removed ? Results.Ok(new { deleted = key }) : Results.NotFound();
});

// ---- Capability directory + reputation (REST) -----------------------------
// A public directory of published services with usage-gated up/down voting. Publishing, unpublishing,
// voting, and usage attestation are all authenticated with a device key registered under the acting
// handle and a signature over the relevant canonical message (same device-key auth as the connector
// broker / hosted model). Reads (list + get) are public: they expose only public metadata + relay
// computed reputation, never private capability content.

// Public discovery: list services, optionally filtered by a free-text query over name/description/
// category. Ranked by the Wilson score (a conservative lower bound of the positive vote proportion),
// then by unique attested users, both descending, so well-reviewed and widely-used services surface.
app.MapGet("/capabilities", async (string? q) =>
{
    var services = await store.ListServicesAsync(q);
    var listings = services
        .Select(ToListing)
        .OrderByDescending(l => l.Score)
        .ThenByDescending(l => l.UniqueUsers)
        .ToList();
    return Results.Ok(listings);
});

// Single service lookup by id.
app.MapGet("/capabilities/{serviceId}", async (string serviceId) =>
{
    var svc = await store.GetServiceAsync(serviceId);
    return svc is null ? Results.NotFound() : Results.Ok(ToListing(svc));
});

// Publish (or update) a service. Verifies the device key is registered under the handle AND that it
// signed the publish claim, so nobody can publish under a handle they do not control. An update
// preserves the service's existing reputation (votes + attested users).
app.MapPost("/capabilities", async (PublishServiceRequest req) =>
{
    var handle = Normalize(req.Handle);
    if (string.IsNullOrWhiteSpace(handle)
        || string.IsNullOrWhiteSpace(req.ServiceId)
        || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "handle, serviceId and name are required" });

    var rec = await store.GetHandleAsync(handle);
    if (rec is null
        || string.IsNullOrWhiteSpace(req.DevicePublicKey)
        || !rec.DevicePublicKeys.Contains(req.DevicePublicKey))
        return Results.Json(new { error = "unknown device for handle" }, statusCode: StatusCodes.Status401Unauthorized);

    var message = ServiceDirectoryProtocol.PublishMessage(handle, req.ServiceId, req.Name);
    if (string.IsNullOrWhiteSpace(req.Signature) || !MeshCrypto.Verify(req.DevicePublicKey, message, req.Signature))
        return Results.Json(new { error = "invalid signature" }, statusCode: StatusCodes.Status401Unauthorized);

    await store.UpsertServiceAsync(new StoredService
    {
        ServiceId = req.ServiceId,
        Handle = handle,
        Name = req.Name,
        Description = req.Description ?? "",
        Category = req.Category ?? "",
        PublishedAt = DateTimeOffset.UtcNow
    });
    app.Logger.LogInformation("service published: {ServiceId} by {Handle}", req.ServiceId, handle);
    return Results.Ok(new { published = req.ServiceId });
});

// Unpublish a service. Only the owning handle (verified by registered device key + signature) can
// remove it. Returns 404 when the service does not exist or is not owned by the caller.
app.MapDelete("/capabilities/{serviceId}", async (string serviceId, [Microsoft.AspNetCore.Mvc.FromBody] UnpublishServiceRequest req) =>
{
    var handle = Normalize(req.Handle);
    var rec = await store.GetHandleAsync(handle);
    if (rec is null
        || string.IsNullOrWhiteSpace(req.DevicePublicKey)
        || !rec.DevicePublicKeys.Contains(req.DevicePublicKey))
        return Results.Json(new { error = "unknown device for handle" }, statusCode: StatusCodes.Status401Unauthorized);

    var message = ServiceDirectoryProtocol.UnpublishMessage(handle, serviceId);
    if (string.IsNullOrWhiteSpace(req.Signature) || !MeshCrypto.Verify(req.DevicePublicKey, message, req.Signature))
        return Results.Json(new { error = "invalid signature" }, statusCode: StatusCodes.Status401Unauthorized);

    var removed = await store.RemoveServiceAsync(handle, serviceId);
    if (!removed) return Results.NotFound();
    app.Logger.LogInformation("service unpublished: {ServiceId} by {Handle}", serviceId, handle);
    return Results.Ok(new { unpublished = serviceId });
});

// Attest usage: the consumer's client calls this right after a successful service invocation. Because
// a ServiceRequest envelope's body is end-to-end encrypted, the relay cannot observe the serviceId
// while routing, so usage is SELF-ATTESTED-BUT-SIGNED for the MVP: a signed claim by a real handle
// registered under a device key it controls. This unlocks that handle's ability to vote. A future
// version can make usage relay-observed once the serviceId is exposed in a cleartext routing header.
// The signed message reuses the vote canonical form with vote=0 (VoteMessage(handle, serviceId, 0)).
app.MapPost("/capabilities/{serviceId}/used", async (string serviceId, ServiceVoteRequest req) =>
{
    var handle = Normalize(req.VoterHandle);
    var rec = await store.GetHandleAsync(handle);
    if (rec is null
        || string.IsNullOrWhiteSpace(req.DevicePublicKey)
        || !rec.DevicePublicKeys.Contains(req.DevicePublicKey))
        return Results.Json(new { error = "unknown device for handle" }, statusCode: StatusCodes.Status401Unauthorized);

    // Vote value is ignored here; the signed message is VoteMessage(handle, serviceId, 0).
    var message = ServiceDirectoryProtocol.VoteMessage(handle, serviceId, 0);
    if (string.IsNullOrWhiteSpace(req.Signature) || !MeshCrypto.Verify(req.DevicePublicKey, message, req.Signature))
        return Results.Json(new { error = "invalid signature" }, statusCode: StatusCodes.Status401Unauthorized);

    var svc = await store.GetServiceAsync(serviceId);
    if (svc is null) return Results.NotFound();

    await store.RecordServiceUsageAsync(serviceId, handle);
    return Results.Ok(new { used = serviceId });
});

// Cast, change, or clear a usage-gated vote. Verifies the voter's registered device key + signature,
// then enforces usage-gating: the voter must have an attested usage event for the service (403
// otherwise). Vote is clamped to {-1, 0, 1}; 0 clears the voter's vote. One updatable vote per voter.
app.MapPost("/capabilities/{serviceId}/vote", async (string serviceId, ServiceVoteRequest req) =>
{
    var handle = Normalize(req.VoterHandle);
    var rec = await store.GetHandleAsync(handle);
    if (rec is null
        || string.IsNullOrWhiteSpace(req.DevicePublicKey)
        || !rec.DevicePublicKeys.Contains(req.DevicePublicKey))
        return Results.Json(new { error = "unknown device for handle" }, statusCode: StatusCodes.Status401Unauthorized);

    var message = ServiceDirectoryProtocol.VoteMessage(handle, serviceId, req.Vote);
    if (string.IsNullOrWhiteSpace(req.Signature) || !MeshCrypto.Verify(req.DevicePublicKey, message, req.Signature))
        return Results.Json(new { error = "invalid signature" }, statusCode: StatusCodes.Status401Unauthorized);

    var svc = await store.GetServiceAsync(serviceId);
    if (svc is null) return Results.NotFound();

    // Usage-gating: only a handle that has actually invoked the service (attested usage) may vote.
    // This is the core anti-fake-reputation mechanism.
    if (!await store.HasUsedServiceAsync(serviceId, handle))
        return Results.Json(new { error = "must use the service before voting" }, statusCode: StatusCodes.Status403Forbidden);

    var vote = Math.Sign(req.Vote); // clamp to {-1, 0, 1}
    await store.SetServiceVoteAsync(serviceId, handle, vote);
    return Results.Ok(new { voted = serviceId, vote });
});

// ---- SignalR transport hub ------------------------------------------------
app.MapHub<MeshHub>(MeshHubProtocol.Route);

app.Run();
return;

// ---- helpers --------------------------------------------------------------
static string Normalize(string handle)
    => handle.Trim().TrimStart('@').ToLowerInvariant();

static string Trim(string s) => s.Length > 300 ? s[..300] : s;

// Projects a stored service + its reputation to the public directory listing shape. Upvotes/Downvotes
// are counted from the per-voter vote map, UniqueUsers from the attested-usage set, and Score is the
// Wilson lower bound used for ranking. Verified is false for now (reserved for a future trust signal).
static ServiceListing ToListing(StoredService s)
{
    var up = s.Votes.Values.Count(v => v > 0);
    var down = s.Votes.Values.Count(v => v < 0);
    return new ServiceListing(
        s.ServiceId,
        s.Handle,
        s.Name,
        s.Description,
        s.Category,
        up,
        down,
        s.Users.Count,
        ServiceDirectoryProtocol.WilsonScore(up, down),
        false,
        s.PublishedAt);
}

// Reads the OpenAI-compatible "usage" object from an upstream chat completion. Returns prompt,
// completion, and total token counts, defaulting each to 0 when absent. When total_tokens is
// missing it falls back to prompt + completion.
static (int prompt, int completion, int total) ReadUsage(JsonElement root)
{
    if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        return (0, 0, 0);

    static int Read(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    var prompt = Read(usage, "prompt_tokens");
    var completion = Read(usage, "completion_tokens");
    var total = Read(usage, "total_tokens");
    if (total == 0) total = prompt + completion;
    return (prompt, completion, total);
}

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
