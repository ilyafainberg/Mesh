using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

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

var msgRatePerMin = int.TryParse(Config(builder.Configuration, "MESH_MSG_RATE_PER_MIN", "Mesh:MessageRatePerMinute"), out var rpm) ? rpm : 120;
var msgBurst = int.TryParse(Config(builder.Configuration, "MESH_MSG_BURST", "Mesh:MessageBurst"), out var mb) ? mb : 30;
var groupRatePerMin = int.TryParse(Config(builder.Configuration, "MESH_GROUP_RATE_PER_MIN", "Mesh:GroupMessageRatePerMinute"), out var grpm) ? grpm : msgRatePerMin;
var groupBurst = int.TryParse(Config(builder.Configuration, "MESH_GROUP_BURST", "Mesh:GroupMessageBurst"), out var gb) ? gb : msgBurst;
var maxFanoutRecipients = int.TryParse(Config(builder.Configuration, "MESH_MAX_FANOUT_RECIPIENTS", "Mesh:MaxFanoutRecipients"), out var mfr)
    ? Math.Clamp(mfr, 1, FanoutProtocol.MaxRecipients)
    : FanoutProtocol.MaxRecipients;
var policyCacheSeconds = int.TryParse(Config(builder.Configuration, "MESH_RATE_POLICY_CACHE_SECONDS", "Mesh:RatePolicyCacheSeconds"), out var pcs)
    ? Math.Max(1, pcs)
    : 60;
var defaultRatePolicy = new HandleRatePolicy(
    msgRatePerMin, msgBurst, groupRatePerMin, groupBurst, maxFanoutRecipients);
IRateLimitStore rateLimitStore = string.IsNullOrWhiteSpace(redisConn)
    ? new InMemoryRateLimitStore()
    : new RedisRateLimitStore(redisConn);
var ratePolicyProvider = new HandleRatePolicyProvider(
    store, defaultRatePolicy, TimeSpan.FromSeconds(policyCacheSeconds));
IMessageRateLimiter messageRateLimiter = new PerHandleRateLimiter(ratePolicyProvider, rateLimitStore);
var adminKey = Config(builder.Configuration, "MESH_ADMIN_KEY", "Mesh:AdminKey");

builder.Services.AddSingleton(store);
builder.Services.AddSingleton(backplane);
builder.Services.AddSingleton(quota);
builder.Services.AddSingleton<IRateLimitStore>(rateLimitStore);
builder.Services.AddSingleton<IHandleRatePolicyProvider>(ratePolicyProvider);
builder.Services.AddSingleton<IMessageRateLimiter>(messageRateLimiter);
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<MeshRouter>();
builder.Services.AddSingleton<Mesh.Relay.RelayConnectorCatalog>();
builder.Services.AddHostedService<PresenceRenewer>();

// Aggregate ops counters (no PII): scraped via GET /metrics.
var metrics = new RelayMetrics();
builder.Services.AddSingleton(metrics);

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
    o.AddPolicy("handle-key-batch", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
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
        return false;
    }
    return await router.DeliverLocalAsync(
        toHandle, envelopeJson, excludeConnectionId: null, toDevice: toDevice);
});

// ---- Health ---------------------------------------------------------------
var transportCapabilities = new
{
    protocolVersion = 3,
    sendResults = true,
    fanout = true,
    deviceSync = true,
    maxFanoutRecipients = FanoutProtocol.MaxRecipients
};
app.MapGet("/", () => Results.Ok(new
{
    service = "Mesh.Relay",
    status = "ok",
    instance = backplane.InstanceId,
    capabilities = transportCapabilities
}));
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow,
    capabilities = transportCapabilities
}));

// Camera-compatible device-link landing. Sensitive pairing data stays in the URL fragment, which
// browsers never send to the relay. This page validates and hands it to the app locally.
app.MapGet("/link", (HttpContext context) =>
{
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    SetSensitiveResponseHeaders(context,
        $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");
    var html = $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Open Mesh</title>
<style>body{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#f3f4f6;margin:0;color:#111827}.wrap{max-width:420px;margin:0 auto;padding:48px 16px}.brand{font-weight:700;font-size:20px;color:#2563eb;text-align:center;margin-bottom:24px}.card{background:#fff;border-radius:14px;padding:24px;box-shadow:0 1px 3px rgba(0,0,0,.1);text-align:center}h1{font-size:22px;margin:0 0 10px}.btn{display:block;width:100%;box-sizing:border-box;text-align:center;background:#2563eb;color:#fff;text-decoration:none;padding:12px;border:0;border-radius:10px;margin-top:12px;font:600 16px inherit;cursor:pointer}.btn.alt{background:#fff;color:#2563eb;border:1px solid #2563eb}.code{font-family:ui-monospace,Consolas,monospace;word-break:break-all;background:#f9fafb;border:1px solid #e5e7eb;border-radius:8px;padding:10px;margin-top:16px}.muted{color:#6b7280;font-size:13px}</style></head>
<body><div class="wrap"><div class="brand">Mesh</div><div class="card">
<h1>Link this device</h1><p>This single-use code expires shortly. Open Mesh to continue.</p>
<a class="btn" id="open" href="#">Open Mesh</a>
<button class="btn alt" id="copy" type="button">Copy link code</button>
<div class="code" id="code">Checking link...</div>
<p class="muted" id="status">If Mesh did not open, use the button above or copy the code.</p>
</div></div><script nonce="{{nonce}}">
const values=new URLSearchParams(location.hash.slice(1));
const handle=(values.get('handle')||'').trim().toLowerCase();
const code=(values.get('code')||'').trim();
const relay=(values.get('relay')||'').trim();
let relayUrl;try{relayUrl=new URL(relay);}catch{}
const validRelay=relayUrl&&relayUrl.protocol==='https:'&&!relayUrl.username&&!relayUrl.password&&!relayUrl.search&&!relayUrl.hash;
const validHandle=/^[a-z0-9_-]{2,64}$/.test(handle);
const validCode=/^[A-Za-z0-9_-]{6,512}$/.test(code);
const open=document.getElementById('open'),codeBox=document.getElementById('code'),status=document.getElementById('status');
if(validRelay&&validHandle&&validCode){
  const target='mesh://link?'+new URLSearchParams({handle,code,relay}).toString();
  open.href=target;codeBox.textContent=code;window.location.replace(target);
}else{
  open.hidden=true;document.getElementById('copy').hidden=true;codeBox.textContent='Invalid device-link';
  status.textContent='This link is incomplete or malformed. Generate a new link in Mesh Settings.';
}
document.getElementById('copy').addEventListener('click',async()=>{try{await navigator.clipboard.writeText(code);status.textContent='Link code copied.';}catch{status.textContent='Press and hold the code to copy it.';} });
</script></body></html>
""";
    return Results.Content(html, "text/html", Encoding.UTF8);
});

// Google redirects to this registered HTTPS URI on mobile. No token exchange, secret, state, or
// authorization code is retained here; validated transient values are immediately sent to the app.
app.MapGet("/oauth/google/callback", (HttpContext context) =>
{
    SetSensitiveResponseHeaders(context, "default-src 'none'; frame-ancestors 'none'");
    var code = context.Request.Query["code"].ToString();
    var stateValue = context.Request.Query["state"].ToString();
    var error = context.Request.Query["error"].ToString();
    var validState = stateValue.Length is >= 20 and <= 256 &&
                     stateValue.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
    var validCode = code.Length <= 4096 && code.All(c => !char.IsControl(c));
    var validError = error.Length <= 128 &&
                     error.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    if (!validState || !validCode || !validError ||
        (string.IsNullOrEmpty(code) == string.IsNullOrEmpty(error)))
        return Results.BadRequest(new { error = "invalid OAuth callback" });

    var values = new List<KeyValuePair<string, string?>>
    {
        new("state", stateValue)
    };
    if (!string.IsNullOrEmpty(code)) values.Add(new("code", code));
    if (!string.IsNullOrEmpty(error)) values.Add(new("error", error));
    var callback = "mesh://oauth/google" + QueryString.Create(values);
    return Results.Redirect(callback);
});

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
app.MapPost("/handles/resolve", async (HandleKeysBatchRequest req) =>
{
    if (req?.Handles is null || req.Handles.Count == 0)
        return Results.BadRequest(new { error = "at least one handle is required" });
    if (req.Handles.Count > FanoutProtocol.MaxRecipients)
        return Results.BadRequest(new { error = $"at most {FanoutProtocol.MaxRecipients} handles are allowed" });

    var handles = new List<string>(req.Handles.Count);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in req.Handles)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Results.BadRequest(new { error = "handles cannot be empty" });
        var handle = Normalize(raw);
        if (handle.Length == 0)
            return Results.BadRequest(new { error = "handles cannot be empty" });
        if (seen.Add(handle)) handles.Add(handle);
    }

    var resolved = await Task.WhenAll(handles.Select(async handle =>
    {
        var record = await store.GetHandleAsync(handle);
        return record is null
            ? null
            : new HandleKeysBatchEntry(handle, record.DevicePublicKeys);
    }));
    return Results.Ok(new HandleKeysBatchResponse(
        resolved.Where(x => x is not null).Cast<HandleKeysBatchEntry>().ToList()));
}).RequireRateLimiting("handle-key-batch");

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
        // Reserved system handles (for example "meshreport") cannot be claimed by ordinary users.
        // This only blocks a brand-new claim; the legitimate owner re-asserts via the path below.
        if (Mesh.Shared.ReservedHandles.IsReserved(handle))
            return Results.Conflict(new { error = "handle is reserved" });

        // First registration CLAIMS the handle for this device key.
        var (created, _) = await store.UpsertHandleAsync(handle, req.DevicePublicKey, req.DisplayName, allowNewDevice: true);
        // Capture the recovery public key at registration so a future device can recover the handle.
        if (!string.IsNullOrWhiteSpace(req.RecoveryPublicKey))
            await store.SetRecoveryKeyAsync(handle, req.RecoveryPublicKey);
        var deviceId = DeviceProtocol.DeviceId(req.DevicePublicKey);
        if (string.IsNullOrWhiteSpace(req.DevicePlatform))
        {
            if (!string.IsNullOrWhiteSpace(req.DeviceName))
                await store.SetDeviceNameAsync(handle, deviceId, req.DeviceName);
        }
        else
        {
            await store.SetDeviceMetadataAsync(
                handle,
                deviceId,
                req.DeviceName,
                req.DevicePlatform.Trim().ToLowerInvariant(),
                req.RemoteAgentEnabled);
        }
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
        var deviceId = DeviceProtocol.DeviceId(req.DevicePublicKey);
        if (string.IsNullOrWhiteSpace(req.DevicePlatform))
        {
            if (!string.IsNullOrWhiteSpace(req.DeviceName))
                await store.SetDeviceNameAsync(handle, deviceId, req.DeviceName);
        }
        else
        {
            await store.SetDeviceMetadataAsync(
                handle,
                deviceId,
                req.DeviceName,
                req.DevicePlatform.Trim().ToLowerInvariant(),
                req.RemoteAgentEnabled);
        }
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

        // Output-token cap for the upstream call. The client may request a larger budget (e.g. for
        // building a whole widget document); clamp it to a sane server-side ceiling so the free tier
        // cannot be pushed into unbounded generations, and default to a reasonable size when unset.
        const int DefaultMaxTokens = 2048;
        const int MaxAllowedTokens = 20480;
        var maxTokens = req.MaxTokens <= 0 ? DefaultMaxTokens : Math.Min(req.MaxTokens, MaxAllowedTokens);

        object payload = new { model, messages, max_tokens = maxTokens };
        if (!string.IsNullOrWhiteSpace(req.ToolsJson))
            payload = new { model, messages, max_tokens = maxTokens, tools = JsonDocument.Parse(req.ToolsJson!).RootElement.Clone() };

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
        var choice0 = doc.RootElement.GetProperty("choices")[0];
        var respMsg = choice0.GetProperty("message");
        var content = respMsg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var finishReason = choice0.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String
            ? frEl.GetString() : null;
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
        return Results.Ok(new HostedModelResponse(content, toolCallsJson, promptTokens, completionTokens, totalTokens, finishReason));
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

// Per-device directory: metadata is durable, while online state comes from cross-replica presence.
app.MapGet("/handles/{handle}/devices", async (string handle) =>
{
    var key = Normalize(handle);
    var rec = await store.GetHandleAsync(key);
    if (rec is null) return Results.NotFound();

    var deviceIds = rec.DevicePublicKeys
        .Select(pubkey => DeviceProtocol.DeviceId(pubkey))
        .Distinct()
        .ToArray();
    var owners = await Task.WhenAll(deviceIds.Select(deviceId =>
        backplane.GetInstanceForDeviceAsync(key, deviceId)));
    var devices = deviceIds
        .Select((deviceId, index) => new DeviceInfo(
            deviceId,
            rec.DeviceNames.GetValueOrDefault(deviceId),
            owners[index] is not null,
            rec.DevicePlatforms.GetValueOrDefault(deviceId, DevicePlatforms.Unknown),
            rec.DeviceRemoteAgentEnabled.GetValueOrDefault(deviceId)))
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

// Administrative rate-policy overrides. The configured key is never stored in Cosmos or logged.
app.MapGet("/admin/handles/{handle}/rate-policy", async (HttpContext ctx, string handle) =>
{
    if (!IsAdmin(ctx, adminKey)) return Results.Unauthorized();
    var key = Normalize(handle);
    if (string.IsNullOrEmpty(key)) return Results.BadRequest(new { error = "handle is required" });
    var configured = await store.GetHandleRatePolicyAsync(key);
    var effective = await ratePolicyProvider.GetPolicyAsync(key);
    return Results.Ok(new { handle = key, overridden = configured is not null, policy = effective });
});

app.MapPut("/admin/handles/{handle}/rate-policy", async (HttpContext ctx, string handle, HandleRatePolicy policy) =>
{
    if (!IsAdmin(ctx, adminKey)) return Results.Unauthorized();
    var key = Normalize(handle);
    if (string.IsNullOrEmpty(key)) return Results.BadRequest(new { error = "handle is required" });
    if (await store.GetHandleAsync(key) is null) return Results.NotFound(new { error = "handle not found" });
    var validationError = ValidateRatePolicy(policy);
    if (validationError is not null) return Results.BadRequest(new { error = validationError });

    await store.SetHandleRatePolicyAsync(key, policy);
    ratePolicyProvider.Invalidate(key);
    return Results.Ok(new { handle = key, policy });
});

app.MapDelete("/admin/handles/{handle}/rate-policy", async (HttpContext ctx, string handle) =>
{
    if (!IsAdmin(ctx, adminKey)) return Results.Unauthorized();
    var key = Normalize(handle);
    if (string.IsNullOrEmpty(key)) return Results.BadRequest(new { error = "handle is required" });
    var removed = await store.DeleteHandleRatePolicyAsync(key);
    ratePolicyProvider.Invalidate(key);
    return removed ? Results.NoContent() : Results.NotFound(new { error = "rate policy not found" });
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
        Category = ServiceCategories.Coerce(req.Category),
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

// ---- Public shareable landing pages (HTML) --------------------------------
// These render self-contained, mobile-friendly pages so an https link works for anyone, including
// people without the app installed. Each page first attempts the mesh:// deep link, then after a
// short fallback timer reveals install call-to-actions. No external assets, no private data leaked.

// Service landing page: previews public service metadata + reputation, deep-links into the app.
app.MapGet("/s/{handle}/{serviceId}", async (string handle, string serviceId) =>
{
    var svc = await store.GetServiceAsync(serviceId);
    if (svc is null)
    {
        var missing = $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Mesh</title>
<style>body{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#f3f4f6;margin:0;color:#111827}.wrap{max-width:420px;margin:0 auto;padding:48px 16px}.brand{font-weight:700;font-size:20px;color:#2563eb;text-align:center;margin-bottom:24px}.card{background:#fff;border-radius:14px;padding:24px;box-shadow:0 1px 3px rgba(0,0,0,.1);text-align:center}h1{font-size:19px;margin:0 0 8px}p{color:#6b7280;font-size:14px;line-height:1.5;margin:0 0 12px}.btn{display:block;text-align:center;background:#2563eb;color:#fff;text-decoration:none;padding:12px;border-radius:10px;margin-top:10px;font-weight:600}.muted{color:#9ca3af;font-size:13px;text-align:center;margin:12px 0 0}</style></head>
<body><div class="wrap"><div class="brand">Mesh</div><div class="card">
<h1>Service not found</h1><p>This service is no longer available or the link is incorrect.</p>
<a class="btn" href="https://meshrelaydl.blob.core.windows.net/releases/Mesh-Setup-latest.zip">Download for Windows</a>
<p class="muted">Also on Android</p></div></div></body></html>
""";
        return Results.Content(missing, "text/html", Encoding.UTF8, 404);
    }

    var listing = ToListing(svc);
    var deep = $"mesh://service?handle={Uri.EscapeDataString(listing.Handle)}&id={Uri.EscapeDataString(listing.ServiceId)}&name={Uri.EscapeDataString(listing.Name)}";
    var html = $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>{{Escape(listing.Name)}} on Mesh</title>
<style>body{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#f3f4f6;margin:0;color:#111827}.wrap{max-width:420px;margin:0 auto;padding:40px 16px}.brand{font-weight:700;font-size:20px;color:#2563eb;text-align:center;margin-bottom:24px}.card{background:#fff;border-radius:14px;padding:24px;box-shadow:0 1px 3px rgba(0,0,0,.1)}h1{font-size:20px;margin:0 0 4px}.by{color:#6b7280;font-size:14px;margin:0 0 12px}.desc{font-size:15px;line-height:1.5;margin:0 0 12px}.cat{display:inline-block;background:#eff6ff;color:#2563eb;border-radius:999px;padding:2px 10px;font-size:12px;margin-bottom:12px}.rating{color:#6b7280;font-size:13px;margin:0}.btn{display:block;text-align:center;background:#2563eb;color:#fff;text-decoration:none;padding:12px;border-radius:10px;margin-top:10px;font-weight:600}.btn.ghost{background:#fff;color:#2563eb;border:1px solid #2563eb}.muted{color:#9ca3af;font-size:13px;text-align:center;margin:12px 0 0}</style></head>
<body><div class="wrap"><div class="brand">Mesh</div><div class="card">
<h1>{{Escape(listing.Name)}}</h1><p class="by">by @{{Escape(listing.Handle)}}</p>
<span class="cat">{{Escape(listing.Category)}}</span>
<p class="desc">{{Escape(listing.Description)}}</p>
<p class="rating">{{listing.Upvotes}} up, {{listing.Downvotes}} down, {{listing.UniqueUsers}} users</p>
<div id="cta" style="display:none">
<a class="btn" href="https://meshrelaydl.blob.core.windows.net/releases/Mesh-Setup-latest.zip">Download for Windows</a>
<a class="btn ghost" href="{{deep}}">Open in Mesh</a>
<p class="muted">Also on Android</p></div></div></div>
<script>setTimeout(function(){document.getElementById('cta').style.display='block';},1500);window.location.href="{{deep}}";</script>
</body></html>
""";
    return Results.Content(html, "text/html");
});

// Handle landing page: PRIVACY GUARDRAIL - no backend lookup, no private data. Renders only the
// normalized @handle plus the deep link and install CTAs.
app.MapGet("/u/{handle}", (string handle) =>
{
    var normalized = Normalize(handle);
    var deep = $"mesh://user?handle={Uri.EscapeDataString(normalized)}";
    var html = $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>@{{Escape(normalized)}} on Mesh</title>
<style>body{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#f3f4f6;margin:0;color:#111827}.wrap{max-width:420px;margin:0 auto;padding:48px 16px}.brand{font-weight:700;font-size:20px;color:#2563eb;text-align:center;margin-bottom:24px}.card{background:#fff;border-radius:14px;padding:24px;box-shadow:0 1px 3px rgba(0,0,0,.1);text-align:center}h1{font-size:22px;margin:0 0 16px}.btn{display:block;text-align:center;background:#2563eb;color:#fff;text-decoration:none;padding:12px;border-radius:10px;margin-top:10px;font-weight:600}.btn.ghost{background:#fff;color:#2563eb;border:1px solid #2563eb}.muted{color:#9ca3af;font-size:13px;text-align:center;margin:12px 0 0}</style></head>
<body><div class="wrap"><div class="brand">Mesh</div><div class="card">
<h1>@{{Escape(normalized)}}</h1>
<div id="cta" style="display:none">
<a class="btn" href="https://meshrelaydl.blob.core.windows.net/releases/Mesh-Setup-latest.zip">Download for Windows</a>
<a class="btn ghost" href="{{deep}}">Open in Mesh</a>
<p class="muted">Also on Android</p></div></div></div>
<script>setTimeout(function(){document.getElementById('cta').style.display='block';},1500);window.location.href="{{deep}}";</script>
</body></html>
""";
    return Results.Content(html, "text/html");
});

// ---- SignalR transport hub ------------------------------------------------
app.MapHub<MeshHub>(MeshHubProtocol.Route);

app.Run();
return;

// ---- helpers --------------------------------------------------------------
static string Normalize(string handle)
    => handle.Trim().TrimStart('@').ToLowerInvariant();

static string Trim(string s) => s.Length > 300 ? s[..300] : s;

static void SetSensitiveResponseHeaders(HttpContext context, string contentSecurityPolicy)
{
    context.Response.Headers["Cache-Control"] = "no-store, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
}

static bool IsAdmin(HttpContext context, string? configuredKey)
{
    if (string.IsNullOrWhiteSpace(configuredKey)
        || !context.Request.Headers.TryGetValue("X-Mesh-Admin-Key", out var supplied))
        return false;
    var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
    return expectedBytes.Length == suppliedBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

static string? ValidateRatePolicy(HandleRatePolicy policy)
{
    const int maximumRate = 1_000_000;
    if (policy.MessagesPerMinute is < 1 or > maximumRate) return "messagesPerMinute must be between 1 and 1000000";
    if (policy.BurstCapacity is < 1 or > maximumRate) return "burstCapacity must be between 1 and 1000000";
    if (policy.GroupMessagesPerMinute is < 1 or > maximumRate) return "groupMessagesPerMinute must be between 1 and 1000000";
    if (policy.GroupBurstCapacity is < 1 or > maximumRate) return "groupBurstCapacity must be between 1 and 1000000";
    if (policy.MaxFanoutRecipients is < 1 or > FanoutProtocol.MaxRecipients)
        return $"maxFanoutRecipients must be between 1 and {FanoutProtocol.MaxRecipients}";
    return null;
}

// Minimal HTML-entity escaper for values interpolated into the public landing pages, so untrusted
// service metadata cannot break the markup or inject script.
static string Escape(string s)
    => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

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
