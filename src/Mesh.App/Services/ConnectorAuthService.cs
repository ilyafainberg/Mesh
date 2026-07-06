using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>Client-side OAuth behaviour for a tier-2 connector. Endpoints and the (public)
/// client id live in the shared <see cref="ConnectorCatalog"/>; this holds only the bits the
/// client needs to build the authorize request.</summary>
public sealed record ConnectorConfig(
    string Key, string Scope, bool UsePkce, bool Refreshable, string ExtraAuthorizeParams);

/// <summary>
/// One-click "Sign in with X" OAuth for the built-in connectors (Dropbox, Notion, Slack).
/// Mesh ships its own registered OAuth apps (public client ids only). Confidential providers
/// (Notion, Slack) never hold a client secret in the app, their code→token exchange is brokered
/// by the Mesh relay, which holds the secret server-side. Dropbox is a public PKCE client and is
/// exchanged directly. Users may optionally supply their own app (advanced) for a direct exchange.
/// Tokens are persisted DPAPI-encrypted in the profile so connections survive restarts.
/// </summary>
public sealed class ConnectorAuthService(IHttpClientFactory httpFactory, AppState state, ConnectorBroker broker, ConnectorCatalogService catalog)
{
    // Dropbox/Notion/Slack require an OAuth app to allow-list an EXACT redirect URI, so we use a
    // fixed loopback port (unlike Google/Entra which special-case loopback and accept any port).
    public const int RedirectPort = 8971;
    public const string RedirectUri = "http://localhost:8971/";

    public static readonly Dictionary<SourceProvider, ConnectorConfig> Configs = new()
    {
        [SourceProvider.Dropbox] = new("dropbox",
            "files.metadata.read files.content.read account_info.read",
            UsePkce: true, Refreshable: true, ExtraAuthorizeParams: "&token_access_type=offline"),
        [SourceProvider.Notion] = new("notion",
            "", UsePkce: false, Refreshable: false, ExtraAuthorizeParams: "&owner=user"),
        [SourceProvider.Slack] = new("slack",
            "", UsePkce: false, Refreshable: false,
            // Slack search is a *user* token scope.
            ExtraAuthorizeParams: "&user_scope=search:read"),
    };

    // Built-in apps are one-click, but only once the relay's connector catalog is available (it
    // provides the OAuth client id). If the catalog hasn't loaded yet (never been online), the
    // provider is shown as temporarily unavailable rather than failing mid sign-in.
    public bool IsConfigured(SourceProvider p)
        => Configs.TryGetValue(p, out var cfg)
           && (catalog.Get(cfg.Key) is not null
               || !string.IsNullOrWhiteSpace(state.Profile.ConnectorClientIds.GetValueOrDefault(cfg.Key, "")));

    private ConnectorEndpoint? Endpoint(string key) => catalog.Get(key);

    /// <summary>User-supplied app id if present (advanced), otherwise Mesh's built-in app id from the relay catalog.</summary>
    private string ClientId(SourceProvider p)
    {
        var user = state.Profile.ConnectorClientIds.GetValueOrDefault(Configs[p].Key, "");
        if (!string.IsNullOrWhiteSpace(user)) return user;
        return Endpoint(Configs[p].Key)?.ClientId ?? "";
    }

    /// <summary>True when the user brought their own OAuth app (direct exchange with their secret).</summary>
    private bool UsesOwnApp(SourceProvider p)
        => !string.IsNullOrWhiteSpace(state.Profile.ConnectorClientIds.GetValueOrDefault(Configs[p].Key, ""));

    private string UserSecret(SourceProvider p)
        => state.Profile.ConnectorClientSecrets.GetValueOrDefault(Configs[p].Key, "");

    public void SaveApp(SourceProvider p, string clientId, string clientSecret)
        => state.Mutate(pr =>
        {
            pr.ConnectorClientIds[Configs[p].Key] = clientId.Trim();
            pr.ConnectorClientSecrets[Configs[p].Key] = clientSecret.Trim();
        });

    /// <summary>Runs the interactive loopback flow and returns the connected account label.</summary>
    public async Task<(bool ok, string? account, string? error)> SignInAsync(SourceProvider provider, CancellationToken ct = default)
    {
        if (!Configs.TryGetValue(provider, out var cfg)) return (false, null, "Unsupported connector.");
        var ep = Endpoint(cfg.Key);
        if (ep is null)
            return (false, null, "Connector setup isn't available yet. Connect to a relay (get online) and try again.");
        var clientId = ClientId(provider);
        if (string.IsNullOrWhiteSpace(clientId)) return (false, null, $"Add your {cfg.Key} OAuth client id first.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var listener = new HttpListener();
        var redirect = RedirectUri;
        try { listener.Prefixes.Add(redirect); listener.Start(); }
        catch (HttpListenerException ex)
        {
            return (false, null, $"Couldn't open the local sign-in port {RedirectPort} ({ex.Message}). " +
                "Close whatever is using it and try again.");
        }

        // Abort the listener promptly on cancellation so the loopback port is freed for a retry.
        using var cancelReg = ct.Register(() => { try { listener.Abort(); } catch { } });

        var authUrl = $"{ep.AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}" +
            (string.IsNullOrEmpty(cfg.Scope) ? "" : $"&scope={Uri.EscapeDataString(cfg.Scope)}") +
            (cfg.UsePkce ? $"&code_challenge={challenge}&code_challenge_method=S256" : "") +
            cfg.ExtraAuthorizeParams;

        System.Diagnostics.Process? browser = null;
        try
        {
            browser = BrowserLauncher.Open(authUrl);
            var ctxTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(3), ct));
            if (completed != ctxTask)
            {
                // Either the 3 minute timeout elapsed or the user cancelled. Either way, free the port.
                ct.ThrowIfCancellationRequested();
                return (false, null, "Sign-in timed out.");
            }
            var context = await ctxTask;
            var code = context.Request.QueryString["code"];
            var err = context.Request.QueryString["error"];
            await RespondAsync(context, err is null ? "Signed in. You can close this window and return to Mesh." : $"Sign-in failed: {err}");
            if (code is null) return (false, null, err ?? "No authorization code returned.");

            string body;
            var http = httpFactory.CreateClient("connector");
            if (ep.Confidential && !UsesOwnApp(provider))
            {
                // Confidential app: the relay holds the secret and performs the exchange.
                var (bok, tokenJson, berr) = await broker.ExchangeCodeAsync(cfg.Key, code, redirect, cfg.UsePkce ? verifier : null, ct);
                if (!bok || tokenJson is null) return (false, null, berr ?? "Token broker failed.");
                body = tokenJson;
            }
            else
            {
                // Public (PKCE) app, or the user's own app with their own secret: exchange directly.
                using var req = new HttpRequestMessage(HttpMethod.Post, ep.TokenUrl);
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirect,
                };
                if (cfg.UsePkce) form["code_verifier"] = verifier;
                if (ep.UseBasicAuth)
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{UserSecret(provider)}")));
                else
                {
                    form["client_id"] = clientId;
                    if (!string.IsNullOrWhiteSpace(UserSecret(provider))) form["client_secret"] = UserSecret(provider);
                }
                req.Content = new FormUrlEncodedContent(form);

                using var resp = await http.SendAsync(req, ct);
                body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return (false, null, $"Token exchange failed: {Trim(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Provider-specific token + identity extraction.
            var (account, accessToken, refreshToken) = provider switch
            {
                SourceProvider.Dropbox => (await DropboxIdentityAsync(http, root, ct),
                    root.GetProperty("access_token").GetString(),
                    root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null),
                SourceProvider.Notion => (root.TryGetProperty("workspace_name", out var w) ? w.GetString() : "Notion",
                    root.GetProperty("access_token").GetString(), (string?)null),
                SourceProvider.Slack => (SlackTeam(root),
                    root.TryGetProperty("authed_user", out var au) && au.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                    (string?)null),
                _ => (null, null, null)
            };
            if (accessToken is null) return (false, null, "No access token returned (check scopes).");
            account ??= cfg.Key;

            var key = $"{cfg.Key}:{account}";
            state.Mutate(p =>
            {
                // For refreshable providers store the refresh token; else store the access token directly.
                p.ConnectorTokens[key] = cfg.Refreshable && refreshToken is not null ? refreshToken : accessToken;
            });
            return (true, account, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
        finally
        {
            listener.Stop();
            _ = Task.Run(async () => { await Task.Delay(600); BrowserLauncher.CloseAuthWindow(); try { if (browser is { HasExited: false }) browser.CloseMainWindow(); } catch { } });
        }
    }

    /// <summary>Returns a usable access token for a connected account (refreshing where supported).</summary>
    public async Task<(bool ok, string? token, string? error)> GetTokenAsync(SourceProvider provider, string? account, CancellationToken ct = default)
    {
        if (!Configs.TryGetValue(provider, out var cfg)) return (false, null, "Unsupported connector.");
        var key = $"{cfg.Key}:{account}";
        if (account is null || !state.Profile.ConnectorTokens.TryGetValue(key, out var stored) || string.IsNullOrWhiteSpace(stored))
            return (false, null, $"Not signed in to this {cfg.Key} account. Reconnect it in Knowledge → Connect a source.");

        if (!cfg.Refreshable) return (true, stored, null); // stored value IS the access token

        // Refreshable (Dropbox): exchange the stored refresh token for a fresh access token.
        // Dropbox is a public PKCE client, so no secret is needed (only a user's own app would add one).
        var http = httpFactory.CreateClient("connector");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = stored,
            ["client_id"] = ClientId(provider),
        };
        if (UsesOwnApp(provider) && !string.IsNullOrWhiteSpace(UserSecret(provider)))
            form["client_secret"] = UserSecret(provider);
        var tokenUrl = Endpoint(cfg.Key)?.TokenUrl;
        if (tokenUrl is null) return (false, null, "Connector setup isn't available (get online and try again).");
        using var resp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return (false, null, $"Token refresh failed: {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        return (true, doc.RootElement.GetProperty("access_token").GetString(), null);
    }

    private static async Task<string?> DropboxIdentityAsync(HttpClient http, JsonElement tokenRoot, CancellationToken ct)
    {
        var access = tokenRoot.GetProperty("access_token").GetString();
        if (access is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        req.Content = new StringContent("null", Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return "Dropbox";
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : "Dropbox";
    }

    private static string SlackTeam(JsonElement root)
        => root.TryGetProperty("team", out var t) && t.TryGetProperty("name", out var n) ? n.GetString() ?? "Slack" : "Slack";

    private static async Task RespondAsync(HttpListenerContext ctx, string message)
    {
        var html = BrowserLauncher.SuccessHtml(message);
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
