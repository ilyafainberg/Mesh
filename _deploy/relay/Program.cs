using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ---- In-memory state (prototype only) -------------------------------------
var handles = new ConcurrentDictionary<string, HandleRecord>(StringComparer.OrdinalIgnoreCase);
var connections = new ConcurrentDictionary<string, WebSocket>(StringComparer.OrdinalIgnoreCase);
var inboxes = new ConcurrentDictionary<string, ConcurrentQueue<MeshEnvelope>>(StringComparer.OrdinalIgnoreCase);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// Used by the connector token broker to call provider token endpoints.
var brokerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

app.UseWebSockets();

// ---- Health ---------------------------------------------------------------
app.MapGet("/", () => Results.Ok(new { service = "Mesh.Relay", status = "ok", handles = handles.Count }));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

// ---- Handle registry (REST) ----------------------------------------------
app.MapPost("/handles", (RegisterHandleRequest req) =>
{
    var handle = Normalize(req.Handle);
    if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(req.DevicePublicKey))
        return Results.BadRequest(new { error = "handle and devicePublicKey are required" });

    // First registration CLAIMS the handle for this device key.
    if (!handles.TryGetValue(handle, out var rec))
    {
        rec = new HandleRecord(handle, req.DisplayName, DateTimeOffset.UtcNow);
        rec.AddDevice(req.DevicePublicKey);
        handles[handle] = rec;
        return Results.Ok(new RegisterHandleResponse(handle, DeviceIdOf(req.DevicePublicKey), rec.RegisteredAt));
    }

    // Re-asserting a device key that is already authorized is idempotent (normal launch).
    if (rec.HasDevice(req.DevicePublicKey))
    {
        if (req.DisplayName is not null) rec.DisplayName = req.DisplayName;
        return Results.Ok(new RegisterHandleResponse(handle, DeviceIdOf(req.DevicePublicKey), rec.RegisteredAt));
    }

    // A DIFFERENT key cannot silently join a claimed handle, must use device linking.
    return Results.Conflict(new { error = "handle already claimed; use device linking to add another device" });
});

// Device linking, an authorized device issues a short-lived, single-use invite.
app.MapPost("/handles/{handle}/link/invite", (string handle, LinkInviteRequest req) =>
{
    var key = Normalize(handle);
    if (!handles.TryGetValue(key, out var rec)) return Results.NotFound();

    if (!rec.HasDevice(req.CreatorPublicKey))
        return Results.Json(new { error = "creator is not an authorized device" }, statusCode: StatusCodes.Status403Forbidden);

    var expires = DateTimeOffset.FromUnixTimeSeconds(req.ExpiresAtUnix);
    if (expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddMinutes(15))
        return Results.BadRequest(new { error = "invalid expiry (must be in the future, within 15 minutes)" });

    var message = LinkProtocol.InviteMessage(key, req.CodeHash, req.ExpiresAtUnix);
    if (!MeshCrypto.Verify(req.CreatorPublicKey, message, req.Signature))
        return Results.BadRequest(new { error = "invalid signature" });

    rec.AddInvite(req.CodeHash, expires);
    return Results.Ok(new LinkInviteResponse(key, req.ExpiresAtUnix));
});

// Device linking, the new device redeems the invite with its own key.
app.MapPost("/handles/{handle}/link/redeem", (string handle, LinkRedeemRequest req) =>
{
    var key = Normalize(handle);
    if (!handles.TryGetValue(key, out var rec)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.NewPublicKey) || string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "newPublicKey and code are required" });

    // Prove the redeemer holds the private key for the new device key.
    if (!MeshCrypto.Verify(req.NewPublicKey, LinkProtocol.RedeemMessage(key, req.Code), req.Signature))
        return Results.BadRequest(new { error = "invalid signature" });

    var codeHash = LinkProtocol.HashCode(req.Code);
    if (!rec.ConsumeInvite(codeHash))
        return Results.BadRequest(new { error = "invite invalid, already used, or expired" });

    rec.AddDevice(req.NewPublicKey);
    return Results.Ok(new LinkRedeemResponse(key, DeviceIdOf(req.NewPublicKey), rec.DisplayName));
});

// ---- Connector token broker ----------------------------------------------
// For confidential connectors (Google, Notion, Slack, …) the client forwards an OAuth
// grant here; the relay holds the client secret (server-side config) and performs the
// exchange, so the secret never ships in the client. Handles both the initial
// authorization_code exchange and hourly refresh_token refresh. The caller must prove it
// owns a device key registered under its handle.
app.MapPost("/connectors/{provider}/token", async (string provider, ConnectorTokenRequest req) =>
{
    var ep = ConnectorCatalog.Get(provider);
    if (ep is null || !ep.Confidential)
        return Results.BadRequest(new { error = "unknown or non-brokered connector" });
    if (req.GrantType is not (ConnectorProtocol.GrantAuthCode or ConnectorProtocol.GrantRefresh))
        return Results.BadRequest(new { error = "unsupported grant_type" });

    // Authenticate the caller: the device key must be authorized under the handle,
    // and the request must be signed by that key.
    var handleKey = Normalize(req.Handle);
    if (!handles.TryGetValue(handleKey, out var rec) || !rec.HasDevice(req.DevicePublicKey))
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

    // Build the grant and inject the server-side secret.
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
        exchange.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
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

app.MapGet("/handles/{handle}", (string handle) =>
{
    var key = Normalize(handle);
    if (!handles.TryGetValue(key, out var rec)) return Results.NotFound();
    return Results.Ok(new HandleInfo(
        rec.Handle, rec.DisplayName, rec.DevicePublicKeys, connections.ContainsKey(key), rec.RegisteredAt));
});

// ---- WebSocket relay ------------------------------------------------------
// Connect with:  GET /ws?handle=@alice   (Upgrade: websocket)
// Send JSON MeshEnvelope frames; relay routes them to envelope.To.
app.MapGet("/ws", async (HttpContext ctx, string handle) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var me = Normalize(handle);
    if (!handles.TryGetValue(me, out var record))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64 * 1024];

    // ---- Auth handshake: challenge -> signed response -> verify -----------
    var nonce = MeshCrypto.NewNonce();
    await SendJsonAsync(socket, new AuthChallenge(nonce), json);

    var authRaw = await ReceiveFullAsync(socket, buffer);
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

    // Flush any queued offline messages.
    if (inboxes.TryGetValue(me, out var queued))
        while (queued.TryDequeue(out var pending))
            await SendAsync(socket, pending, json);

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await ReceiveFullAsync(socket, buffer);
            if (result is null) break; // closed

            MeshEnvelope? env;
            try { env = JsonSerializer.Deserialize<MeshEnvelope>(result, json); }
            catch { continue; } // ignore malformed frames

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
        connections.TryRemove(me, out _);
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

async Task RouteAsync(MeshEnvelope env)
{
    var to = Normalize(env.To);
    if (connections.TryGetValue(to, out var dest) && dest.State == WebSocketState.Open)
    {
        await SendAsync(dest, env, json);
    }
    else
    {
        // Offline: queue for delivery on next connect.
        var q = inboxes.GetOrAdd(to, _ => new ConcurrentQueue<MeshEnvelope>());
        q.Enqueue(env);
    }
}

static async Task SendAsync(WebSocket socket, MeshEnvelope env, JsonSerializerOptions opts)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(env, opts);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
}

static async Task<string?> ReceiveFullAsync(WebSocket socket, byte[] buffer)
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
    } while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(ms.ToArray());
}

static string Normalize(string handle)
    => handle.Trim().TrimStart('@').ToLowerInvariant();

static string DeviceIdOf(string publicKey)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(publicKey)))[..12].ToLowerInvariant();

// Server-side connector client secret, from configuration (env: Connectors__notion__secret)
// or a CONNECTOR_NOTION_SECRET fallback. Never shipped in the client.
string? ConnectorSecret(string provider)
    => app.Configuration[$"Connectors:{provider.ToLowerInvariant()}:secret"]
       ?? Environment.GetEnvironmentVariable($"CONNECTOR_{provider.ToUpperInvariant()}_SECRET");

// ---- in-memory record -----------------------------------------------------
sealed class HandleRecord(string handle, string? displayName, DateTimeOffset registeredAt)
{
    private readonly ConcurrentDictionary<string, byte> devices = new();
    // Pending link invites: code hash -> expiry. Single-use, short-lived.
    private readonly ConcurrentDictionary<string, DateTimeOffset> invites = new();

    public string Handle { get; } = handle;
    public string? DisplayName { get; set; } = displayName;
    public DateTimeOffset RegisteredAt { get; } = registeredAt;
    public IReadOnlyList<string> DevicePublicKeys => devices.Keys.ToList();

    public bool HasDevice(string publicKey) => devices.ContainsKey(publicKey);

    public string AddDevice(string publicKey)
    {
        devices.TryAdd(publicKey, 0);
        return DeviceId(publicKey);
    }

    public void AddInvite(string codeHash, DateTimeOffset expires)
    {
        PurgeExpiredInvites();
        invites[codeHash] = expires;
    }

    /// <summary>Consumes a live invite by code hash (single use). False if missing/expired.</summary>
    public bool ConsumeInvite(string codeHash)
    {
        PurgeExpiredInvites();
        return invites.TryRemove(codeHash, out var exp) && exp > DateTimeOffset.UtcNow;
    }

    private void PurgeExpiredInvites()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in invites)
            if (kv.Value <= now) invites.TryRemove(kv.Key, out _);
    }

    private static string DeviceId(string publicKey)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(publicKey)))[..12].ToLowerInvariant();
}
