using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Talks to the relay: registers the handle (REST) and maintains a WebSocket
/// for sending/receiving <see cref="MeshEnvelope"/>s. Inbound messages are
/// dispatched to the agent (guest auto-respond) and surfaced to the UI.
/// </summary>
public sealed class MeshClient(AppState state, AgentService agent, IHttpClientFactory httpFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> keyCache = new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? socket;
    private CancellationTokenSource? cts;

    public bool Connected => socket?.State == WebSocketState.Open;
    public event Action? StateChanged;
    public event Action<string>? Log;

    public async Task<bool> RegisterAsync()
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var resp = await http.PostAsJsonAsync($"{p.RelayUrl.TrimEnd('/')}/handles",
                new RegisterHandleRequest(p.Handle, p.PublicKey, p.DisplayName));
            Log?.Invoke($"register {p.Handle}: {(int)resp.StatusCode}");
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
                // Handle claimed by a different device set, this device isn't linked to it.
                Log?.Invoke($"'{p.Handle}' is claimed by another identity; link this device or pick a new handle.");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log?.Invoke($"register failed: {ex.Message}"); return false; }
    }

    /// <summary>
    /// This (already-authorized) device creates a single-use invite so another device
    /// can join the same handle. Returns the raw code to show as a QR / short code.
    /// </summary>
    public async Task<(bool ok, string? code, string? error)> CreateLinkInviteAsync(TimeSpan? ttl = null)
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var code = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var codeHash = LinkProtocol.HashCode(code);
            var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds();
            var sig = IdentityService.Sign(p.PrivateKey, LinkProtocol.InviteMessage(p.Handle, codeHash, expires));

            var resp = await http.PostAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(AppState.Norm(p.Handle))}/link/invite",
                new LinkInviteRequest(AppState.Norm(p.Handle), p.PublicKey, codeHash, expires, sig));
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"relay {(int)resp.StatusCode}");
            return (true, code, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    /// <summary>
    /// This device redeems an invite to join an existing handle. It keeps its own
    /// keypair (now authorized under the handle) and adopts the handle + display name.
    /// </summary>
    public async Task<(bool ok, string? error)> RedeemLinkAsync(string relayUrl, string handle, string code)
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var h = AppState.Norm(handle);
            var sig = IdentityService.Sign(p.PrivateKey, LinkProtocol.RedeemMessage(h, code));
            var resp = await http.PostAsJsonAsync(
                $"{relayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/link/redeem",
                new LinkRedeemRequest(h, p.PublicKey, code, sig));
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"relay {(int)resp.StatusCode}: {body}");
            }
            var result = await resp.Content.ReadFromJsonAsync<LinkRedeemResponse>();
            // Adopt the linked identity: this device keeps its own keypair but takes the handle.
            state.Mutate(x =>
            {
                x.Handle = h;
                x.RelayUrl = relayUrl.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(result?.DisplayName)) x.DisplayName = result!.DisplayName!;
            });
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task ConnectAsync()
    {
        await DisconnectAsync();
        cts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoopAsync(cts.Token));
        await Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var p = state.Profile;
        var wsUrl = ToWs(p.RelayUrl) + $"/ws?handle={Uri.EscapeDataString(p.Handle)}";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(wsUrl), ct);

                if (!await AuthenticateAsync(socket, ct))
                {
                    Log?.Invoke("ws auth failed");
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
                    StateChanged?.Invoke();
                    if (!ct.IsCancellationRequested) await Task.Delay(3000, ct).ContinueWith(_ => { });
                    continue;
                }

                Log?.Invoke("ws connected + authenticated");
                StateChanged?.Invoke();
                await ReceiveLoopAsync(socket, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke($"ws error: {ex.Message}; retrying in 3s");
            }
            StateChanged?.Invoke();
            if (!ct.IsCancellationRequested) await Task.Delay(3000, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>Completes the relay's challenge/response handshake with the device key.</summary>
    private async Task<bool> AuthenticateAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        // 1. receive challenge
        var raw = await ReceiveTextAsync(ws, buffer, ct);
        var challenge = raw is null ? null : JsonSerializer.Deserialize<AuthChallenge>(raw, Json);
        if (challenge is null || challenge.Type != "auth.challenge") return false;

        // 2. sign nonce and reply
        var sig = IdentityService.Sign(state.Profile.PrivateKey, challenge.Nonce);
        var resp = new AuthResponse(state.Profile.PublicKey, sig);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(resp, Json), WebSocketMessageType.Text, true, ct);

        // 3. read result
        var resultRaw = await ReceiveTextAsync(ws, buffer, ct);
        var result = resultRaw is null ? null : JsonSerializer.Deserialize<AuthResult>(resultRaw, Json);
        return result is { Type: "auth.result", Ok: true };
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            MeshEnvelope? env;
            try { env = JsonSerializer.Deserialize<MeshEnvelope>(text, Json); } catch { continue; }
            if (env is not null) await HandleInboundAsync(env, ct);
        }
    }

    private async Task HandleInboundAsync(MeshEnvelope env, CancellationToken ct)
    {
        var from = AppState.Norm(env.From);

        // Client-side verification: check the sender's signature against their pinned signing
        // keys (trust on first use). This defends against a malicious or compromised relay
        // forging or tampering with messages. On first contact we fetch and pin the keys.
        var pinned = state.FindContact(from)?.SigningKeys.ToList() ?? new List<string>();
        if (pinned.Count == 0)
            pinned = (await ResolveDeviceKeysAsync(from)).ToList();
        if (pinned.Count > 0 && !MeshCrypto.VerifyAny(pinned, env.Body, env.Signature ?? ""))
        {
            Log?.Invoke($"dropped unverifiable message from @{from}");
            return;
        }

        // Decrypt end-to-end payloads addressed to this device. Plaintext bodies pass through.
        var text = env.Body;
        if (MessageCrypto.IsEncrypted(env.Body))
        {
            var (ok, plain) = MessageCrypto.TryDecrypt(env.Body, state.Profile.PrivateKey, state.Profile.PublicKey);
            text = ok ? plain! : "[encrypted message this device can't read]";
        }

        var contact = state.FindContact(from);
        var allowed = contact?.Allowed == true;

        // Record the inbound line. Agent replies are tagged "agent"; anything a person typed
        // (chat or a direct message) is "person".
        var via = env.Kind == MeshKinds.AgentResponse ? "agent" : "person";
        state.Mutate(x =>
        {
            var conv = state.GetOrCreateConversation(from);
            conv.Lines.Add(new ChatLine { Role = "user", Text = text, Via = via });
        });

        if (!allowed)
        {
            // Unknown/!allowed -> drop into request inbox, do NOT engage the agent.
            state.Mutate(x =>
            {
                if (!x.Requests.Any(r => r.From == from))
                    x.Requests.Add(new PendingRequest { From = from, Body = text });
            });
            Log?.Invoke($"inbound from @{from} held for approval");
            StateChanged?.Invoke();
            return;
        }

        // Allowed -> guest agent drafts a scoped reply, subject to the daily cost budget.
        if (env.Kind is MeshKinds.Chat or MeshKinds.AgentRequest && agent.IsModelReady)
        {
            if (!state.TryConsumeAgentReply())
            {
                // Cost control: the daily automatic-reply budget is spent. Hold the message as
                // a normal conversation line but do not invoke the paid model.
                Log?.Invoke($"agent reply to @{from} skipped: daily budget reached");
                StateChanged?.Invoke();
                return;
            }

            var conv = state.GetOrCreateConversation(from);
            var reply = await agent.RespondAsGuestAsync(from, conv.Lines.ToList(), ct);

            if (state.RequiresApproval(from))
            {
                // Human-in-the-loop: hold the draft for owner review, do NOT send yet.
                state.Mutate(x => x.Approvals.Add(new PendingApproval
                {
                    From = from,
                    RequestBody = text,
                    DraftReply = reply
                }));
                Log?.Invoke($"draft reply to @{from} awaiting approval");
            }
            else
            {
                state.Mutate(x =>
                {
                    var c = state.GetOrCreateConversation(from);
                    c.Lines.Add(new ChatLine { Role = "assistant", Text = reply });
                });
                await SendAsync(from, MeshKinds.AgentResponse, reply);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Resolves (and caches) a handle's device public keys from the relay directory. Used both
    /// to encrypt outbound messages to that handle and to pin its signing keys for verification.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveDeviceKeysAsync(string handle)
    {
        var h = AppState.Norm(handle);
        if (keyCache.TryGetValue(h, out var cached)) return cached;
        try
        {
            var http = httpFactory.CreateClient("relay");
            var info = await http.GetFromJsonAsync<HandleInfo>(
                $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}");
            var keys = info?.DevicePublicKeys?.ToList() ?? new List<string>();
            if (keys.Count > 0)
            {
                keyCache[h] = keys;
                state.PinAndGetKeys(h, keys); // trust on first use
            }
            return keys;
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Owner approves a held draft (optionally edited): record it and send.</summary>
    public async Task ApproveDraftAsync(string approvalId, string? editedReply = null)
    {
        var approval = state.Profile.Approvals.FirstOrDefault(a => a.Id == approvalId);
        if (approval is null) return;
        var text = string.IsNullOrWhiteSpace(editedReply) ? approval.DraftReply : editedReply!;
        state.Mutate(x =>
        {
            var c = state.GetOrCreateConversation(approval.From);
            c.Lines.Add(new ChatLine { Role = "assistant", Text = text });
            x.Approvals.RemoveAll(a => a.Id == approvalId);
        });
        await SendAsync(approval.From, MeshKinds.AgentResponse, text);
    }

    public void RejectDraft(string approvalId)
        => state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));


    public async Task SendAsync(string toHandle, string kind, string body)
    {
        if (socket?.State != WebSocketState.Open) { Log?.Invoke("send failed: not connected"); return; }
        var p = state.Profile;
        var to = AppState.Norm(toHandle);

        // End-to-end encrypt to the recipient's device keys when we can resolve them. The relay
        // only ever sees ciphertext. If keys are unavailable (recipient not in the directory yet)
        // we fall back to sending plaintext so messaging still works.
        var wire = body;
        var keys = await ResolveDeviceKeysAsync(to);
        if (keys.Count > 0)
        {
            var enc = MessageCrypto.Encrypt(body, keys);
            if (enc is not null) wire = enc;
        }

        var sig = IdentityService.Sign(p.PrivateKey, wire);
        var env = MeshEnvelope.Create(p.Handle, to, kind, wire, sig);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(env, Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task DisconnectAsync()
    {
        try { cts?.Cancel(); } catch { }
        keyCache.Clear();
        if (socket is { State: WebSocketState.Open })
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
        socket?.Dispose();
        socket = null;
        StateChanged?.Invoke();
    }

    private static string ToWs(string httpUrl)
        => httpUrl.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://");
}
