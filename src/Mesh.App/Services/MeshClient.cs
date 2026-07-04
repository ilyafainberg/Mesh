using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Talks to the relay: registers the handle (REST) and maintains a SignalR hub connection
/// for sending/receiving <see cref="MeshEnvelope"/>s. SignalR handles transport, framing,
/// keepalive and automatic reconnection; this client adds the device-key auth handshake,
/// end-to-end encryption, and dispatch of inbound messages to the agent and UI.
/// </summary>
public sealed class MeshClient(AppState state, AgentService agent, IHttpClientFactory httpFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> keyCache = new(StringComparer.OrdinalIgnoreCase);
    private HubConnection? hub;
    private volatile bool authenticated;

    public bool Connected => hub?.State == HubConnectionState.Connected && authenticated;
    public event Action? StateChanged;
    public event Action<string>? Log;

    public async Task<bool> RegisterAsync()
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var resp = await http.PostAsJsonAsync($"{p.RelayUrl.TrimEnd('/')}/handles",
                new RegisterHandleRequest(p.Handle, p.PublicKey, p.DisplayName, NullIfBlank(p.RecoveryPublicKey)));
            Log?.Invoke($"register {p.Handle}: {(int)resp.StatusCode}");
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
                // Handle claimed by a different device set, this device isn't linked to it.
                Log?.Invoke($"'{p.Handle}' is claimed by another identity; link this device or pick a new handle.");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log?.Invoke($"register failed: {ex.Message}"); return false; }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Re-authorizes THIS device under an existing handle using the handle's recovery key (carried
    /// in an imported profile). Used when no other device is available to issue a link invite. The
    /// device signs its own fresh public key with the recovery private key; the relay verifies it
    /// against the recovery public key stored at registration and authorizes this device.
    /// </summary>
    public async Task<(bool ok, string? error)> RecoverHandleAsync()
    {
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.RecoveryPrivateKey))
            return (false, "This profile has no recovery key, so it can't recover a handle on a new device.");
        var http = httpFactory.CreateClient("relay");
        try
        {
            var h = AppState.Norm(p.Handle);
            var sig = IdentityService.Sign(p.RecoveryPrivateKey, RecoveryProtocol.Message(h, p.PublicKey));
            var resp = await http.PostAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/recover",
                new RecoverHandleRequest(h, p.PublicKey, sig));
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"relay {(int)resp.StatusCode}: {body}");
            }
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
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
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.RelayUrl)) return;

        var url = $"{p.RelayUrl.TrimEnd('/')}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(AppState.Norm(p.Handle))}";
        var connection = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        // The relay opens with a nonce challenge; sign it with the device key to authenticate.
        connection.On<string>(MeshHubProtocol.Challenge, async nonce =>
        {
            try
            {
                var sig = IdentityService.Sign(state.Profile.PrivateKey, nonce);
                await connection.InvokeAsync(MeshHubProtocol.Authenticate, state.Profile.PublicKey, sig);
            }
            catch (Exception ex) { Log?.Invoke($"auth failed: {ex.Message}"); }
        });

        connection.On(MeshHubProtocol.Ready, () =>
        {
            authenticated = true;
            Log?.Invoke("hub connected + authenticated");
            StateChanged?.Invoke();
        });

        connection.On<string>(MeshHubProtocol.Receive, async envelopeJson =>
        {
            MeshEnvelope? env;
            try { env = JsonSerializer.Deserialize<MeshEnvelope>(envelopeJson, Json); }
            catch { return; }
            if (env is not null) await HandleInboundAsync(env, CancellationToken.None);
        });

        // A reconnect re-runs the server's challenge automatically (the handler stays registered),
        // so we just reflect the transient unauthenticated state in the UI.
        connection.Reconnecting += _ => { authenticated = false; StateChanged?.Invoke(); return Task.CompletedTask; };
        connection.Reconnected += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
        connection.Closed += _ => { authenticated = false; StateChanged?.Invoke(); return Task.CompletedTask; };

        hub = connection;
        try
        {
            await connection.StartAsync();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"hub connect failed: {ex.Message}");
            StateChanged?.Invoke();
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
        state.AddChatLine(from, new ChatLine { Role = "user", Text = text, Via = via });

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

            // If the model could not produce a real answer (unavailable, over limit, provider
            // error), do NOT send the error text to the peer as if it were the agent's reply.
            // Refund the consumed budget and leave the inbound message in the conversation for
            // the owner to see and handle.
            if (ModelReply.IsFailure(reply))
            {
                state.RefundAgentReply();
                Log?.Invoke($"agent reply to @{from} skipped: model unavailable");
                StateChanged?.Invoke();
                return;
            }

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
                state.AddChatLine(from, new ChatLine { Role = "assistant", Text = reply });
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
        state.AddChatLine(approval.From, new ChatLine { Role = "assistant", Text = text });
        state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));
        await SendAsync(approval.From, MeshKinds.AgentResponse, text);
    }

    public void RejectDraft(string approvalId)
        => state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));


    public async Task SendAsync(string toHandle, string kind, string body)
    {
        if (hub is null || hub.State != HubConnectionState.Connected || !authenticated)
        {
            Log?.Invoke("send failed: not connected");
            return;
        }
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
        try { await hub.InvokeAsync(MeshHubProtocol.SendEnvelope, env); }
        catch (Exception ex) { Log?.Invoke($"send failed: {ex.Message}"); }
    }

    public async Task DisconnectAsync()
    {
        authenticated = false;
        keyCache.Clear();
        var current = hub;
        hub = null;
        if (current is not null)
        {
            try { await current.StopAsync(); } catch { }
            try { await current.DisposeAsync(); } catch { }
        }
        StateChanged?.Invoke();
    }
}
