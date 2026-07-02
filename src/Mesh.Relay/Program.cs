using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Mesh.Relay.Backplane;
using Mesh.Relay.Storage;
using Mesh.Shared;

var builder = WebApplication.CreateBuilder(args);

// Cap REST request bodies. Message attachments travel over the WebSocket, not REST, so
// REST payloads (registration, link, token broker, model prompt) are always small.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512 * 1024);

// ---- Durable storage + backplane (config-gated, in-memory by default) ------
// Cosmos connection => durable handle registry / invites / offline inbox.
// Redis connection  => multi-replica presence + socket routing.
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

// Per-IP rate limiting on every REST endpoint.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

// Live sockets held by THIS instance. Sockets cannot be persisted, so this is per-instance;
// the backplane routes across instances by presence.
var connections = new ConcurrentDictionary<string, WebSocket>(StringComparer.OrdinalIgnoreCase);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var brokerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var modelHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

// Per-handle daily counter for the hosted free model (cost control on the server side too).
var modelUsage = new ConcurrentDictionary<string, (string day, int count)>(StringComparer.OrdinalIgnoreCase);

app.UseWebSockets();
app.UseRateLimiter();

// The backplane delivers messages for sockets that live on this instance.
await backplane.StartAsync(async (toHandle, envelopeJson) =>
{
    if (connections.TryGetValue(toHandle, out var sock) && sock.State == WebSocketState.Open)
        await SendRawAsync(sock, envelopeJson);
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
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        return Results.Json(new { error = "hosted model not configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    // Per-handle daily quota.
    var dailyLimit = int.TryParse(Config(app.Configuration, "MODEL_DAILY_LIMIT", "Model:DailyLimit"), out var dl) ? dl : 50;
    var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
    var usage = modelUsage.AddOrUpdate(handleKey,
        _ => (today, 1),
        (_, cur) => cur.day == today ? (today, cur.count + 1) : (today, 1));
    if (dailyLimit > 0 && usage.count > dailyLimit)
        return Results.Json(new { error = "daily free-model limit reached" }, statusCode: StatusCodes.Status429TooManyRequests);

    // Build an OpenAI-style chat payload. Supports Azure OpenAI (api-key header + deployment URL)
    // and OpenAI-compatible endpoints (Bearer + /v1/chat/completions), selected by Model:Kind.
    var kind = (Config(app.Configuration, "MODEL_KIND", "Model:Kind") ?? "azure").ToLowerInvariant();
    var model = Config(app.Configuration, "MODEL_NAME", "Model:Deployment")
                ?? Config(app.Configuration, "MODEL_NAME2", "Model:Model") ?? "gpt-4o-mini";

    var messages = new List<object> { new { role = "system", content = req.SystemPrompt } };
    messages.AddRange(req.Messages.Select(m => (object)new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content }));

    string url;
    using var upstream = new HttpRequestMessage(HttpMethod.Post, (Uri?)null);
    if (kind == "azure")
    {
        var apiVersion = Config(app.Configuration, "MODEL_API_VERSION", "Model:ApiVersion") ?? "2024-08-01-preview";
        url = $"{endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version={apiVersion}";
        upstream.Headers.TryAddWithoutValidation("api-key", apiKey);
    }
    else
    {
        url = $"{endpoint.TrimEnd('/')}/v1/chat/completions";
        upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
    upstream.RequestUri = new Uri(url);
    upstream.Content = JsonContent.Create(new { model, messages, max_tokens = 1024 });

    try
    {
        using var resp = await modelHttp.SendAsync(upstream);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { error = "upstream model error", detail = Trim(body) }, statusCode: StatusCodes.Status502BadGateway);
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return Results.Ok(new HostedModelResponse(content));
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
    return Results.Ok(new HandleInfo(
        rec.Handle, rec.DisplayName, rec.DevicePublicKeys, connections.ContainsKey(key), rec.RegisteredAt));
});

// ---- WebSocket relay ------------------------------------------------------
app.MapGet("/ws", async (HttpContext ctx, string handle) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var me = Normalize(handle);
    var record = await store.GetHandleAsync(me);
    if (record is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64 * 1024];
    const int maxMessageBytes = 12 * 1024 * 1024; // room for an encrypted attachment payload

    // ---- Auth handshake: challenge -> signed response -> verify -----------
    var nonce = MeshCrypto.NewNonce();
    await SendJsonAsync(socket, new AuthChallenge(nonce), json);

    var authRaw = await ReceiveFullAsync(socket, buffer, maxMessageBytes);
    string authedKey;
    try
    {
        var auth = authRaw is null ? null : JsonSerializer.Deserialize<AuthResponse>(authRaw, json);
        if (auth is null || auth.Type != "auth.response"
            || !record.DevicePublicKeys.Contains(auth.PublicKey)
            || !MeshCrypto.Verify(auth.PublicKey, nonce, auth.Signature))
        {
            await SendJsonAsync(socket, new AuthResult(false, "authentication failed"), json);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth failed", CancellationToken.None);
            return;
        }
        authedKey = auth.PublicKey;
    }
    catch
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth error", CancellationToken.None);
        return;
    }
    await SendJsonAsync(socket, new AuthResult(true), json);

    connections[me] = socket;
    await backplane.SetPresenceAsync(me);

    // Renew presence periodically so the backplane's TTL keeps this handle routable here.
    using var presenceCts = new CancellationTokenSource();
    var renew = Task.Run(async () =>
    {
        try
        {
            while (!presenceCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), presenceCts.Token);
                await backplane.SetPresenceAsync(me);
            }
        }
        catch (OperationCanceledException) { }
    });

    // Flush any queued offline messages.
    foreach (var pending in await store.DrainInboxAsync(me))
        await SendRawAsync(socket, pending);

    // Simple per-connection message rate limit: at most 30 frames per 10 second window.
    var recentFrames = new Queue<DateTimeOffset>();

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await ReceiveFullAsync(socket, buffer, maxMessageBytes);
            if (result is null) break; // closed or oversized

            var now = DateTimeOffset.UtcNow;
            recentFrames.Enqueue(now);
            while (recentFrames.Count > 0 && (now - recentFrames.Peek()).TotalSeconds > 10) recentFrames.Dequeue();
            if (recentFrames.Count > 30) continue; // drop excess frames from a chatty client

            MeshEnvelope? env;
            try { env = JsonSerializer.Deserialize<MeshEnvelope>(result, json); }
            catch { continue; }
            if (env is null) continue;

            // Verify the message signature against the connection's authenticated key.
            if (!MeshCrypto.Verify(authedKey, env.Body, env.Signature ?? ""))
                continue; // drop forged/tampered messages

            var stamped = env with { From = me }; // relay asserts the authenticated sender
            await RouteAsync(stamped);
        }
    }
    catch (WebSocketException) { /* client dropped */ }
    finally
    {
        presenceCts.Cancel();
        try { await renew; } catch { }
        connections.TryRemove(me, out _);
        await backplane.ClearPresenceAsync(me);
    }
});

app.Run();
return;

// ---- helpers --------------------------------------------------------------
static async Task SendJsonAsync<T>(WebSocket socket, T payload, JsonSerializerOptions opts)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, opts);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task SendRawAsync(WebSocket socket, string text)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

async Task RouteAsync(MeshEnvelope env)
{
    var to = Normalize(env.To);
    var envelopeJson = JsonSerializer.Serialize(env, json);

    // Deliver to a socket on this instance if present.
    if (connections.TryGetValue(to, out var dest) && dest.State == WebSocketState.Open)
    {
        await SendRawAsync(dest, envelopeJson);
        return;
    }

    // Otherwise ask the backplane which instance holds the recipient's socket.
    var owner = await backplane.GetInstanceForAsync(to);
    if (owner is not null && owner != backplane.InstanceId
        && await backplane.PublishToOwnerAsync(owner, to, envelopeJson))
        return;

    // Nobody is holding the socket: queue for delivery on next connect.
    await store.EnqueueAsync(to, envelopeJson);
}

static async Task<string?> ReceiveFullAsync(WebSocket socket, byte[] buffer, int maxBytes)
{
    using var ms = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            return null;
        }
        ms.Write(buffer, 0, result.Count);
        if (ms.Length > maxBytes)
        {
            // Abusive/oversized frame: close the connection rather than buffer unbounded memory.
            try { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None); } catch { }
            return null;
        }
    } while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(ms.ToArray());
}

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
