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
public sealed class MeshClient(AppState state, AgentService agent, IHttpClientFactory httpFactory, INotifier notifier)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> keyCache = new(StringComparer.OrdinalIgnoreCase);
    private HubConnection? hub;
    private volatile bool authenticated;
    private volatile bool wantConnected;   // the user intends to be connected; drives auto-recovery
    private int reconnectScheduled;         // 0/1 guard so only one recovery loop runs at a time

    public bool Connected => hub?.State == HubConnectionState.Connected && authenticated;
    public event Action? StateChanged;
    public event Action<string>? Log;

    /// <summary>
    /// This device's stable id, derived from its public signing key. Same derivation the relay uses,
    /// so both agree on the id that targets one specific device (MeshEnvelope.ToDevice). Empty when
    /// this profile has no key yet.
    /// </summary>
    public string MyDeviceId =>
        string.IsNullOrWhiteSpace(state.Profile.PublicKey) ? "" : DeviceProtocol.DeviceId(state.Profile.PublicKey);

    /// <summary>
    /// Retry policy that never gives up: SignalR's built-in reconnect stops after a fixed schedule,
    /// which leaves the client permanently offline after a longer network drop (sleep, wifi switch).
    /// This backs off up to 30s and keeps trying for as long as the user wants to be connected.
    /// </summary>
    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context)
        {
            var seconds = context.PreviousRetryCount switch
            {
                0 => 0,
                1 => 2,
                2 => 5,
                3 => 10,
                _ => 30
            };
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public async Task<bool> RegisterAsync()
    {
        var p = state.Profile;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var h = AppState.Norm(p.Handle);
            // Proof of possession: sign the claim with this device's private key so the relay can
            // confirm we control the key we are registering (collision avoidance).
            var sig = IdentityService.Sign(p.PrivateKey, ClaimProtocol.Message(h, p.PublicKey));
            var deviceName = EnsureDeviceName();
            var resp = await http.PostAsJsonAsync($"{p.RelayUrl.TrimEnd('/')}/handles",
                new RegisterHandleRequest(p.Handle, p.PublicKey, p.DisplayName, NullIfBlank(p.RecoveryPublicKey), sig, deviceName));
            Log?.Invoke($"register {p.Handle}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return true;

            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // The handle is claimed by a different device key. If this profile carries the
                // handle's recovery key (for example after a reinstall or profile import), prove
                // ownership and re-authorize this device automatically instead of stranding it.
                if (!string.IsNullOrWhiteSpace(p.RecoveryPrivateKey))
                {
                    Log?.Invoke($"'{p.Handle}' claimed by another device; attempting recovery with the recovery key.");
                    var (ok, err) = await RecoverHandleAsync();
                    if (ok) { Log?.Invoke($"recovered @{p.Handle}: this device is now authorized."); return true; }
                    Log?.Invoke($"recovery failed for @{p.Handle}: {err}");
                }
                else
                {
                    Log?.Invoke($"'{p.Handle}' is claimed by another identity; link this device or restore your backup.");
                }
            }
            return false;
        }
        catch (Exception ex) { Log?.Invoke($"register failed: {ex.Message}"); return false; }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Returns the device name to register, defaulting it the first time. If the profile has no
    /// device name yet, pick a sensible one (the OS device name where available, else the machine
    /// name) and persist it so the relay can show a friendly label in the device picker.
    /// </summary>
    private string EnsureDeviceName()
    {
        var current = state.Profile.DeviceName;
        if (!string.IsNullOrWhiteSpace(current)) return current;

        var name = "";
        try { name = Microsoft.Maui.Devices.DeviceInfo.Current.Name; } catch { }
        if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(name)) return "";

        state.Mutate(x => x.DeviceName = name);
        return name;
    }

    /// <summary>
    /// Checks whether a handle is already claimed on a relay. Returns true if taken, false if free,
    /// and null if the relay could not be reached (caller should treat null as "unknown" and not
    /// let creation proceed blindly). Used at identity creation to prevent claiming a taken handle.
    /// </summary>
    public async Task<bool?> IsHandleTakenAsync(string handle, string? relayUrl = null)
    {
        var url = (relayUrl ?? state.Profile.RelayUrl).TrimEnd('/');
        var h = AppState.Norm(handle);
        var http = httpFactory.CreateClient("relay");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await http.GetAsync($"{url}/handles/{Uri.EscapeDataString(h)}", cts.Token);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false; // free
            if (resp.IsSuccessStatusCode) return true;                                // claimed
            return null;                                                              // unknown
        }
        catch { return null; }
    }

    /// <summary>
    /// Releases a handle on the relay so its name is free to claim again, authenticated with a
    /// device key registered under it. Best-effort: returns false if the relay rejects it (for
    /// example this device's key was never the registered one) or is unreachable.
    /// </summary>
    public async Task<bool> DeleteHandleAsync(string handle, string privateKey, string publicKey, string? relayUrl = null)
    {
        var url = (relayUrl ?? state.Profile.RelayUrl).TrimEnd('/');
        var h = AppState.Norm(handle);
        if (string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(publicKey)) return false;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var sig = IdentityService.Sign(privateKey, DeleteProtocol.Message(h));
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{url}/handles/{Uri.EscapeDataString(h)}")
            {
                Content = JsonContent.Create(new DeleteHandleRequest(h, publicKey, sig))
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await http.SendAsync(req, cts.Token);
            Log?.Invoke($"delete handle {h}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log?.Invoke($"delete handle failed: {ex.Message}"); return false; }
    }

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
        wantConnected = true;
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.RelayUrl)) return;

        var url = $"{p.RelayUrl.TrimEnd('/')}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(AppState.Norm(p.Handle))}";
        var connection = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect(new ForeverRetry())
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
        connection.Closed += _ =>
        {
            authenticated = false;
            StateChanged?.Invoke();
            // SignalR's own auto-reconnect has given up by the time Closed fires. If the user still
            // wants to be connected, keep trying ourselves so a long drop does not strand us offline.
            ScheduleRecovery();
            return Task.CompletedTask;
        };

        hub = connection;
        try
        {
            await connection.StartAsync();
            StateChanged?.Invoke();
            StartAuthWatchdog(connection);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"hub connect failed: {ex.Message}");
            StateChanged?.Invoke();
            ScheduleRecovery();
        }
    }

    /// <summary>
    /// Guards the auth handshake: if the connection is up but the challenge/response never completes
    /// (a mid-handshake hiccup leaves us connected-but-not-authenticated, with no Closed event to
    /// trigger recovery), force a fresh reconnect so we do not sit silently offline.
    /// </summary>
    private void StartAuthWatchdog(HubConnection connection)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (!ReferenceEquals(hub, connection)) return; // superseded by a newer connection
            if (wantConnected && !authenticated && connection.State == HubConnectionState.Connected)
            {
                Log?.Invoke("auth watchdog: connected but not authenticated, reconnecting");
                ScheduleRecovery();
                try { await connection.StopAsync(); } catch { } // triggers Closed -> recovery loop
            }
        });
    }

    /// <summary>
    /// Background recovery: while the user wants to be connected but the hub is not connected, keep
    /// reconnecting with backoff. Only one loop runs at a time. This covers the case where SignalR's
    /// automatic reconnect has exhausted and fired Closed (e.g. after a long sleep or network loss).
    /// </summary>
    private void ScheduleRecovery()
    {
        if (!wantConnected) return;
        if (Interlocked.Exchange(ref reconnectScheduled, 1) == 1) return; // already running
        _ = Task.Run(async () =>
        {
            try
            {
                var delay = TimeSpan.FromSeconds(3);
                while (wantConnected && (hub is null || hub.State == HubConnectionState.Disconnected))
                {
                    await Task.Delay(delay);
                    if (!wantConnected) break;
                    if (hub is not null && hub.State != HubConnectionState.Disconnected) break;
                    try
                    {
                        Log?.Invoke("recovery: reconnecting to relay");
                        await ConnectAsync();
                        break; // ConnectAsync rebuilds the hub + its own recovery hooks
                    }
                    catch (Exception ex) { Log?.Invoke($"recovery attempt failed: {ex.Message}"); }
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
            finally { Interlocked.Exchange(ref reconnectScheduled, 0); }
        });
    }

    private async Task HandleInboundAsync(MeshEnvelope env, CancellationToken ct)
    {
        var from = AppState.Norm(env.From);

        // A delivery receipt: mark our matching outgoing line as delivered. Receipts are plaintext
        // (they carry only a message id, no content) and are verified below like any other envelope.
        if (env.Kind == MeshKinds.Receipt)
        {
            var pinnedR = state.FindContact(from)?.SigningKeys.ToList() ?? new List<string>();
            if (pinnedR.Count == 0) pinnedR = (await ResolveDeviceKeysAsync(from)).ToList();
            if (pinnedR.Count > 0 && !MeshCrypto.VerifyAny(pinnedR, env.Body, env.Signature ?? "")) return;
            var msgId = ReceiptProtocol.MessageId(env.Body);
            if (!string.IsNullOrEmpty(msgId)) state.SetLineStatus(msgId!, "delivered");
            return;
        }

        // Client-side verification: check the sender's signature against their pinned signing
        // keys (trust on first use). This defends against a malicious or compromised relay
        // forging or tampering with messages. On first contact we fetch and pin the keys.
        var pinned = state.FindContact(from)?.SigningKeys.ToList() ?? new List<string>();
        if (pinned.Count == 0)
            pinned = (await ResolveDeviceKeysAsync(from)).ToList();
        if (pinned.Count > 0 && !MeshCrypto.VerifyAny(pinned, env.Body, env.Signature ?? ""))
        {
            // The sender's keys no longer match what we pinned: the contact's identity may have
            // changed (rotation, reinstall, or an impostor). Surface it for re-verification instead
            // of silently dropping, and do not auto-repin (that would defeat trust on first use).
            state.FlagContactKeyChanged(from);
            Log?.Invoke($"identity change: message from @{from} did not match pinned keys (re-verify)");
            return;
        }

        // Decrypt end-to-end payloads addressed to this device. Plaintext bodies pass through.
        var text = env.Body;
        if (MessageCrypto.IsEncrypted(env.Body))
        {
            var (ok, plain) = MessageCrypto.TryDecrypt(env.Body, state.Profile.PrivateKey, state.Profile.PublicKey);
            text = ok ? plain! : "[encrypted message this device can't read]";
        }

        // Remote-to-desktop: a request from one of the owner's OWN devices (same handle) to run the
        // full owner agent here and reply. Only honored when this device opted in and the sender is
        // our own handle (the relay only routes same-handle envelopes between our linked devices).
        if (env.Kind == MeshKinds.RemoteAgentRequest)
        {
            // Belt-and-suspenders: the relay already delivers a targeted request only to the chosen
            // device, but if this envelope names a different device than ours, ignore it.
            if (env.ToDevice is not null && env.ToDevice != MyDeviceId) return;
            if (state.Profile.ActAsRemoteAgent && from == AppState.Norm(state.Profile.Handle))
            {
                var answer = await agent.AskAsRemoteAsync(text, ct);
                // Target the answer back to the device that asked so only it receives the response.
                await SendAsync(from, MeshKinds.RemoteAgentResponse, answer, toDevice: env.FromDevice);
            }
            return;
        }
        if (env.Kind == MeshKinds.RemoteAgentResponse)
        {
            // The mobile side records the home agent's answer as an incoming assistant line.
            state.AddChatLine(from, new ChatLine { Role = "user", Text = text, Via = "agent", AddressedToAgent = true });
            state.MarkUnread(from);
            if (ShouldNotify(state.FindContact(from)))
                notifier.Notify("Your home agent replied", Preview(text), NotifyKind.Message, "messages");
            return;
        }

        // Public service invocation. Handled BEFORE the allow-list gate below: any handle may invoke a
        // public-listed service, so it must not be dropped into the request inbox for non-contacts.
        // The answer comes from a hard-sandboxed, service-scoped agent (public KB/Skills/Widgets only,
        // no tools of any kind).
        if (env.Kind == MeshKinds.ServiceRequest)
        {
            var (serviceId, turns) = ServiceProtocol.ParseRequest(text);
            var svc = state.Profile.PublishedServices.FirstOrDefault(s => s.Id == serviceId);
            if (svc is null || !svc.Published) return;                 // not a live service here
            if (!agent.IsModelReady) return;                           // no model to answer with

            // Token-budget gate (provider-side cost control; the relay never sees the E2E-encrypted
            // token spend, so the owner enforces their own budget here). Refuse politely when the
            // service's lifetime total budget is exhausted or this caller has hit their daily cap.
            if (svc.IsBudgetExhausted(from))
            {
                await SendAsync(from, MeshKinds.ServiceResponse, ServiceProtocol.Body(serviceId,
                    "This service has reached its usage budget and is not accepting requests right now."));
                Log?.Invoke($"service '{serviceId}' refused for @{from}: budget exhausted");
                return;
            }

            // Rate-limit gate: cap the number of requests one caller can make per day, independent of
            // token cost, so nobody can flood the service with cheap requests (anti-abuse).
            if (svc.IsRateLimited(from))
            {
                await SendAsync(from, MeshKinds.ServiceResponse, ServiceProtocol.Body(serviceId,
                    "You have reached this service's daily request limit. Please try again tomorrow."));
                Log?.Invoke($"service '{serviceId}' refused for @{from}: daily rate limit");
                return;
            }

            if (!state.TryConsumeAgentReply()) return;                 // daily budget spent; don't burn the model

            // Count this accepted request against the caller's daily rate limit.
            state.Mutate(_ => svc.RecordRequest(from));

            // The consumer supplies the (windowed) transcript so a follow-up has context; the provider
            // stays stateless per caller. Map turns to chat lines the sandboxed agent understands.
            var svcHistory = turns
                .Select(t => new ChatLine { Role = t.Role == "user" ? "user" : "assistant", Text = t.Text, Via = "agent" })
                .ToList();
            if (svcHistory.Count == 0) svcHistory.Add(new ChatLine { Role = "user", Text = "" });

            var reply = await agent.RespondAsServiceAsync(serviceId, from, svcHistory, ct);

            // Do not send model-failure text to the caller as if it were a real answer; refund budget.
            if (ModelReply.IsFailure(reply.Text))
            {
                state.RefundAgentReply();
                Log?.Invoke($"service '{serviceId}' reply to @{from} skipped: model unavailable");
                return;
            }

            // Charge the tokens this reply cost against the service's budget (lifetime total + daily per-handle).
            if (reply.Tokens > 0)
                state.Mutate(_ => svc.RecordSpend(from, reply.Tokens));

            await SendAsync(from, MeshKinds.ServiceResponse, ServiceProtocol.Body(serviceId, reply.Text));
            return;
        }
        if (env.Kind == MeshKinds.ServiceResponse)
        {
            var (svcId, answer) = ServiceProtocol.Parse(text);
            // Land the reply in the dedicated service thread (not the provider's person DM), so the
            // consumer can keep a real multi-turn conversation with the service.
            var conv = state.FindConversation(AppState.ServiceKey(from, svcId))
                       ?? state.GetOrCreateServiceConversation(from, svcId, null);
            state.ClearAwaiting(conv.Handle);
            state.AddChatLine(conv.Handle, new ChatLine { Role = "user", Text = answer, Via = "agent", AddressedToAgent = true });
            state.MarkUnread(conv.Handle);
            notifier.Notify($"{conv.ServiceName} replied", Preview(answer), NotifyKind.Message, "messages");
            return;
        }

        if (env.Kind == MeshKinds.Report)
        {
            // Inbound AI-content report (this device is signed in as the reserved report handle).
            // Render it as a readable message from the reporter so the operator can review it.
            var payload = ReportProtocol.Parse(text);
            var rendered = payload is null ? text : FormatReport(payload);
            state.AddChatLine(from, new ChatLine { Role = "user", Text = rendered, Via = "person" });
            state.MarkUnread(from);
            notifier.Notify("New report", Preview(rendered), NotifyKind.Message, "messages");
            return;
        }

        var contact = state.FindContact(from);
        var allowed = contact?.Allowed == true;
        var display = state.DisplayNameFor(from);

        // Blocked contact: drop entirely (no record, no agent, no toast).
        if (contact?.Blocked == true)
        {
            Log?.Invoke($"dropped message from blocked @{from}");
            return;
        }

        // Record the inbound line. Anything routed through an agent (a reply from the peer's
        // agent, or a request their agent addressed to ours) is tagged "agent"; a message a
        // person typed to the human (chat or a direct message) is "person". Chat still engages
        // our guest agent below, but for labeling/history it is treated as person-authored.
        var via = env.Kind is MeshKinds.AgentResponse or MeshKinds.AgentRequest ? "agent" : "person";
        state.AddChatLine(from, new ChatLine { Role = "user", Text = text, Via = via, AddressedToAgent = via == "agent" });

        // Acknowledge receipt of any real message so the sender sees "delivered".
        if (env.Kind is MeshKinds.DirectMessage or MeshKinds.Chat or MeshKinds.AgentRequest or MeshKinds.AgentResponse)
            _ = SendReceiptAsync(from, env.Id);

        // A person-to-person message to the human: mark unread and toast the owner (unless muted/DND).
        if (env.Kind == MeshKinds.DirectMessage)
        {
            state.MarkUnread(from);
            if (ShouldNotify(contact))
                notifier.Notify($"Message from {display}", Preview(text), NotifyKind.Message, "messages");
        }

        if (!allowed)
        {
            // Unknown/!allowed -> drop into request inbox, do NOT engage the agent.
            var isNew = false;
            state.Mutate(x =>
            {
                if (!x.Requests.Any(r => r.From == from))
                {
                    x.Requests.Add(new PendingRequest { From = from, Body = text });
                    isNew = true;
                }
            });
            if (isNew && !state.Profile.DoNotDisturb)
                notifier.Notify($"Request from @{from}", Preview(text), NotifyKind.Request, "contacts");
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
                if (!state.Profile.DoNotDisturb)
                    notifier.Notify("Reply needs your approval",
                        $"Your agent drafted a reply to {display}.", NotifyKind.Approval, "messages");
                Log?.Invoke($"draft reply to @{from} awaiting approval");
            }
            else
            {
                // The guest agent's own reply travels on the agent channel (Via defaults to
                // "agent" so it stays in the guest history and shows the agent icon), but it
                // answers the requesting person, so it is AddressedToAgent == false -> "to them".
                state.AddChatLine(from, new ChatLine { Role = "assistant", Text = reply, AddressedToAgent = false });
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

    /// <summary>
    /// Re-verifies a contact whose keys changed: fetches the handle's current device keys from the
    /// relay directory (bypassing the cache) and re-pins them, clearing the key-changed flag. This
    /// is an explicit, user-initiated trust decision.
    /// </summary>
    public async Task<bool> ReverifyContactAsync(string handle)
    {
        var h = AppState.Norm(handle);
        try
        {
            var http = httpFactory.CreateClient("relay");
            var info = await http.GetFromJsonAsync<HandleInfo>(
                $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}");
            var keys = info?.DevicePublicKeys?.ToList() ?? new List<string>();
            if (keys.Count == 0) return false;
            keyCache[h] = keys;
            state.ReverifyContact(h, keys);
            StateChanged?.Invoke();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Owner approves a held draft (optionally edited): record it and send.</summary>
    public async Task ApproveDraftAsync(string approvalId, string? editedReply = null)
    {
        var approval = state.Profile.Approvals.FirstOrDefault(a => a.Id == approvalId);
        if (approval is null) return;
        var text = string.IsNullOrWhiteSpace(editedReply) ? approval.DraftReply : editedReply!;
        // An approved draft is the agent's reply to the contact who asked, so it reads "to them".
        var line = new ChatLine { Role = "assistant", Text = text, AddressedToAgent = false };
        state.AddChatLine(approval.From, line);
        state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));
        await SendAsync(approval.From, MeshKinds.AgentResponse, text, line.Id);
    }

    public void RejectDraft(string approvalId)
        => state.Mutate(x => x.Approvals.RemoveAll(a => a.Id == approvalId));

    /// <summary>
    /// Sends a request to the owner's OTHER devices asking a remote (home) agent to answer with its
    /// full local toolset. Routed to the same handle; the relay excludes this device, so a linked
    /// desktop that opted in (ActAsRemoteAgent) responds. Returns false when not connected.
    /// </summary>
    public async Task<bool> AskHomeAgentAsync(string prompt)
        => await SendAsync(state.Profile.Handle, MeshKinds.RemoteAgentRequest, prompt, toDevice: state.Profile.HomeDeviceId);

    /// <summary>
    /// Lists the devices currently registered under this handle (from the relay directory) so the
    /// Settings UI can offer a "home device" picker. Best-effort: returns an empty list on any error
    /// (unreachable relay, bad response) rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListMyDevicesAsync(CancellationToken ct = default)
    {
        var h = AppState.Norm(state.Profile.Handle);
        if (string.IsNullOrWhiteSpace(h)) return Array.Empty<Mesh.Shared.DeviceInfo>();
        try
        {
            var http = httpFactory.CreateClient("relay");
            var devices = await http.GetFromJsonAsync<Mesh.Shared.DeviceInfo[]>(
                $"{state.Profile.RelayUrl.TrimEnd('/')}/handles/{Uri.EscapeDataString(h)}/devices", Json, ct);
            return devices ?? Array.Empty<Mesh.Shared.DeviceInfo>();
        }
        catch { return Array.Empty<Mesh.Shared.DeviceInfo>(); }
    }

    /// <summary>
    /// Sends a user-submitted report of inappropriate AI content to the reserved report handle as an
    /// end-to-end encrypted message (Microsoft Store Policy 11.16). The caller has shown the user the
    /// exact transcript and obtained explicit consent before calling this.
    /// </summary>
    public async Task<bool> SendReportAsync(string target, string category, string? note, string? serviceId, IReadOnlyList<ReportLine> transcript)
    {
        var payload = new ReportPayload(
            Target: target,
            Category: category,
            Note: string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
            Model: state.CurrentModelKey(),
            ServiceId: serviceId,
            AppVersion: AppVersionString(),
            At: DateTimeOffset.UtcNow,
            Transcript: transcript);
        return await SendAsync(ReservedHandles.Report, MeshKinds.Report, ReportProtocol.Body(payload));
    }

    private static string AppVersionString()
    {
        try { return Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString; }
        catch { return "unknown"; }
    }

    // Renders an inbound report into readable text for the operator's Messages view.
    private static string FormatReport(ReportPayload p)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[AI content report]\n\n");
        sb.Append("Target: ").Append(p.Target).Append('\n');
        sb.Append("Category: ").Append(p.Category).Append('\n');
        if (!string.IsNullOrWhiteSpace(p.Note)) sb.Append("Note: ").Append(p.Note).Append('\n');
        if (!string.IsNullOrWhiteSpace(p.Model)) sb.Append("Model: ").Append(p.Model).Append('\n');
        sb.Append("App: ").Append(p.AppVersion).Append("  ").Append(p.At.ToString("u")).Append("\n\n");
        sb.Append("Transcript:\n");
        foreach (var l in p.Transcript)
            sb.Append("- ").Append(l.Author).Append(": ").Append(l.Text).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Sends a ServiceRequest for the given service thread, carrying a windowed transcript so the
    /// provider's sandboxed agent has multi-turn context for follow-ups. The caller has already
    /// appended the user's prompt line to <paramref name="conv"/>. No contact relationship is required;
    /// the request routes to the real provider handle behind the synthetic thread key.
    /// </summary>
    public async Task<bool> SendServiceRequestAsync(Conversation conv)
    {
        if (conv.ServiceId is null || string.IsNullOrWhiteSpace(conv.ProviderHandle)) return false;
        // Show a processing indicator on this thread until the reply arrives (the response is
        // asynchronous for a remote service, or produced in-process for a service you own).
        state.SetAwaiting(conv.Handle);
        // From the provider agent's point of view, my outgoing lines (Role "assistant") are the user,
        // and the service's prior answers (Role "user") are the assistant.
        var window = conv.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .TakeLast(20)
            .Select(l => new ServiceTurn(l.Role == "assistant" ? "user" : "assistant", l.Text))
            .ToList();

        // Self-owned service: the relay deliberately does NOT echo a message back to the sending
        // device when it is addressed to your own handle (so home-calls reach your OTHER devices).
        // That means a provider invoking their OWN service would never get a reply. Answer locally
        // instead, running the same sandboxed service agent in-process.
        if (AppState.Norm(conv.ProviderHandle!) == AppState.Norm(state.Profile.Handle))
        {
            await AnswerOwnServiceLocallyAsync(conv, window);
            return true;
        }

        var body = ServiceProtocol.RequestBody(conv.ServiceId, window);
        var ok = await SendAsync(conv.ProviderHandle!, MeshKinds.ServiceRequest, body);
        // If the request could not be sent, do not leave a stuck indicator.
        if (!ok) state.ClearAwaiting(conv.Handle);
        return ok;
    }

    /// <summary>
    /// Answers a service the current user owns, locally (no relay round-trip), so a provider can use
    /// and test their own service. Runs the same hard-sandboxed service agent as the remote path
    /// (public-listed capabilities only), so what the owner sees matches what other handles get. No
    /// budget or rate-limit gating is applied to the owner using their own service.
    /// </summary>
    private async Task AnswerOwnServiceLocallyAsync(Conversation conv, IReadOnlyList<ServiceTurn> window)
    {
        var svc = state.Profile.PublishedServices.FirstOrDefault(s => s.Id == conv.ServiceId);
        if (svc is null) { state.ClearAwaiting(conv.Handle); return; }
        if (!agent.IsModelReady)
        {
            state.AddChatLine(conv.Handle, new ChatLine
            {
                Role = "user",
                Text = "No model is configured, so this service cannot answer yet. Set one up in Settings.",
                Via = "agent",
                AddressedToAgent = true
            });
            state.ClearAwaiting(conv.Handle);
            return;
        }

        var me = AppState.Norm(state.Profile.Handle);
        var svcHistory = window
            .Select(t => new ChatLine { Role = t.Role == "user" ? "user" : "assistant", Text = t.Text, Via = "agent" })
            .ToList();

        var reply = await agent.RespondAsServiceAsync(conv.ServiceId!, me, svcHistory, CancellationToken.None);
        state.ClearAwaiting(conv.Handle);
        if (ModelReply.IsFailure(reply.Text)) return;

        state.AddChatLine(conv.Handle, new ChatLine { Role = "user", Text = reply.Text, Via = "agent", AddressedToAgent = true });
        state.MarkRead(conv.Handle);
    }


    public async Task<bool> SendAsync(string toHandle, string kind, string body, string? lineId = null, string? toDevice = null)
    {
        if (hub is null || hub.State != HubConnectionState.Connected || !authenticated)
        {
            Log?.Invoke("send failed: not connected");
            if (lineId is not null) state.SetLineStatus(lineId, "failed");
            return false;
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
        var env = MeshEnvelope.Create(p.Handle, to, kind, wire, sig, toDevice: toDevice);
        try
        {
            await hub.InvokeAsync(MeshHubProtocol.SendEnvelope, env);
            if (lineId is not null) state.SetLineStatus(lineId, "sent");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"send failed: {ex.Message}");
            if (lineId is not null) state.SetLineStatus(lineId, "failed");
            return false;
        }
    }

    private bool ShouldNotify(Domain.Contact? contact)
        => !state.Profile.DoNotDisturb && contact?.Muted != true;

    /// <summary>Sends a lightweight delivery receipt (message id only, signed, no content) to a sender.</summary>
    private async Task SendReceiptAsync(string toHandle, string messageId)
    {
        try
        {
            if (hub is null || hub.State != HubConnectionState.Connected || !authenticated) return;
            var p = state.Profile;
            var body = ReceiptProtocol.Body(messageId);
            var sig = IdentityService.Sign(p.PrivateKey, body);
            var env = MeshEnvelope.Create(p.Handle, AppState.Norm(toHandle), MeshKinds.Receipt, body, sig);
            await hub.InvokeAsync(MeshHubProtocol.SendEnvelope, env);
        }
        catch { /* receipts are best-effort */ }
    }

    private static string Preview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no content)";
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length > 120 ? clean[..120] + "…" : clean;
    }

    public async Task DisconnectAsync()
    {
        // Clear intent first so the Closed handler from StopAsync does not trigger auto-recovery.
        // ConnectAsync calls this then re-sets wantConnected, so a reconnect is unaffected.
        wantConnected = false;
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
