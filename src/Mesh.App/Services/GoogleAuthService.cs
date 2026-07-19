using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Shared;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;

namespace Mesh.App.Services;

/// <summary>
/// One-click "Sign in with Google" for Gmail + Drive (read-only). Mesh ships its own registered
/// Google OAuth app (public client id only); the client secret lives on the Mesh relay, which
/// brokers both the initial code exchange and hourly refresh, so no secret ships in the client and
/// the user pastes nothing. Refresh tokens are held DPAPI-encrypted in the profile.
/// </summary>
public sealed class GoogleAuthService(IHttpClientFactory httpFactory, AppState state, ConnectorBroker broker, ConnectorCatalogService catalog)
{
    private const string ProviderKey = "google";
    private const string Scope = "https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/drive.readonly openid email";

    // Google's Web-application client requires an exact redirect URI; we register + use this one.
    public const string RedirectUri = ConnectorAuthService.RedirectUri;
    public const string MobileCallbackUri = "mesh://oauth/google";

    private string? ClientId => catalog.Get(ProviderKey)?.ClientId;

    // Google is one-click once the relay's connector catalog is available (provides the client id).
    public bool IsConfigured => catalog.Get(ProviderKey) is not null;

    /// <summary>Runs the interactive loopback flow and returns the signed-in email plus the scopes Google actually granted.</summary>
    public async Task<(bool ok, string? email, string[] scopes, string? error)> SignInInteractiveAsync(CancellationToken ct = default)
    {
        var clientId = ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            return (false, null, Array.Empty<string>(), "Google setup isn't available yet. Connect to a relay (get online) and try again.");
        var verifier = RandomUrl(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var oauthState = RandomUrl(32);

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            return await SignInMobileAsync(clientId, verifier, challenge, oauthState, ct);

        using var listener = new HttpListener();
        var redirect = RedirectUri;
        try { listener.Prefixes.Add(redirect); listener.Start(); }
        catch (HttpListenerException ex)
        {
            return (false, null, Array.Empty<string>(), $"Couldn't open the local sign-in port ({ex.Message}). Close whatever is using it and try again.");
        }

        var authUrl = BuildAuthorizeUrl(clientId, redirect, challenge, oauthState);

        System.Diagnostics.Process? browser = null;
        try
        {
            browser = BrowserLauncher.Open(authUrl);

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3), ct));
            if (completed != contextTask) return (false, null, Array.Empty<string>(), "Sign-in timed out.");

            var context = await contextTask;
            var code = context.Request.QueryString["code"];
            var err = context.Request.QueryString["error"];
            var returnedState = context.Request.QueryString["state"];
            var stateValid = StateMatches(oauthState, returnedState);
            await RespondAsync(context, !stateValid
                ? "Sign-in failed: invalid OAuth state."
                : err is null
                    ? "Signed in. You can close this window and return to Mesh."
                    : $"Sign-in failed: {err}");
            if (!stateValid)
                return (false, null, Array.Empty<string>(), "Google sign-in was rejected because the OAuth state did not match. Try again.");
            if (code is null) return (false, null, Array.Empty<string>(), err ?? "No authorization code returned.");

            // Confidential Web-app client: the relay holds the secret and exchanges the code.
            var (bok, tokenJson, berr) = await broker.ExchangeCodeAsync(ProviderKey, code, redirect, verifier, ct);
            if (!bok || tokenJson is null) return (false, null, Array.Empty<string>(), berr ?? "Token broker failed.");

            using var doc = JsonDocument.Parse(tokenJson);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString();
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var granted = root.TryGetProperty("scope", out var sc) && sc.GetString() is string s
                ? s.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            var http = httpFactory.CreateClient("google");
            var email = EmailFromIdToken(root) ?? await FetchEmailAsync(http, access, ct);
            if (email is null) return (false, null, granted, "Could not read account email.");

            if (refresh is not null)
                state.Mutate(p => p.GoogleRefreshTokens[email] = refresh);
            return (true, email, granted, null);
        }
        catch (Exception ex) { return (false, null, Array.Empty<string>(), ex.Message); }
        finally
        {
            listener.Stop();
            // Give the success page a moment to render, then close the window ourselves.
            _ = Task.Run(async () => { await Task.Delay(600); BrowserLauncher.CloseAuthWindow(); TryClose(browser); });
        }
    }

    private async Task<(bool ok, string? email, string[] scopes, string? error)> SignInMobileAsync(
        string clientId, string verifier, string challenge, string oauthState, CancellationToken ct)
    {
        if (!TryGetMobileRedirect(out var redirect, out var configError))
            return (false, null, Array.Empty<string>(), configError);

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions
                {
                    Url = new Uri(BuildAuthorizeUrl(clientId, redirect, challenge, oauthState)),
                    CallbackUrl = new Uri(MobileCallbackUri),
                    PrefersEphemeralWebBrowserSession = true
                },
                ct);

            result.Properties.TryGetValue("state", out var returnedState);
            if (!StateMatches(oauthState, returnedState))
                return (false, null, Array.Empty<string>(), "Google sign-in was rejected because the OAuth state did not match. Try again.");

            result.Properties.TryGetValue("error", out var providerError);
            if (!string.IsNullOrWhiteSpace(providerError))
            {
                var message = providerError.Equals("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase)
                    ? $"Google mobile sign-in is not registered. Add this authorized redirect URI in Google Cloud: {redirect}"
                    : $"Google sign-in failed: {Trim(providerError)}";
                return (false, null, Array.Empty<string>(), message);
            }
            if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                return (false, null, Array.Empty<string>(), "Google did not return an authorization code.");

            var (brokerOk, tokenJson, brokerError) =
                await broker.ExchangeCodeAsync(ProviderKey, code, redirect, verifier, ct);
            if (!brokerOk || tokenJson is null)
                return (false, null, Array.Empty<string>(), brokerError ?? "The relay could not complete Google sign-in.");

            using var doc = JsonDocument.Parse(tokenJson);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString();
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var granted = root.TryGetProperty("scope", out var sc) && sc.GetString() is string s
                ? s.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            var http = httpFactory.CreateClient("google");
            var email = EmailFromIdToken(root) ?? await FetchEmailAsync(http, access, ct);
            if (email is null)
                return (false, null, granted, "Google signed in, but the account email could not be read.");
            if (refresh is not null)
                state.Mutate(p => p.GoogleRefreshTokens[email] = refresh);
            return (true, email, granted, null);
        }
        catch (TaskCanceledException)
        {
            return (false, null, Array.Empty<string>(), "Google sign-in was canceled.");
        }
        catch (FeatureNotSupportedException)
        {
            return (false, null, Array.Empty<string>(), "Google sign-in is not available on this device.");
        }
        catch (Exception ex)
        {
            return (false, null, Array.Empty<string>(), $"Google sign-in could not complete: {Trim(ex.Message)}");
        }
    }

    private bool TryGetMobileRedirect(out string redirect, out string? error)
    {
        redirect = "";
        error = null;
        var relay = state.Profile.RelayUrl?.Trim();
        if (!Uri.TryCreate(relay, UriKind.Absolute, out var relayUri) ||
            !string.IsNullOrEmpty(relayUri.UserInfo) || !string.IsNullOrEmpty(relayUri.Query) ||
            !string.IsNullOrEmpty(relayUri.Fragment) ||
            (relayUri.Scheme != Uri.UriSchemeHttps && !(relayUri.Scheme == Uri.UriSchemeHttp && relayUri.IsLoopback)))
        {
            error = "Google mobile sign-in requires a reachable HTTPS relay URL. Configure the relay, reconnect, and try again.";
            return false;
        }

        redirect = new UriBuilder(relayUri.Scheme, relayUri.Host, relayUri.IsDefaultPort ? -1 : relayUri.Port,
            "/oauth/google/callback").Uri.AbsoluteUri;
        return true;
    }

    private static string BuildAuthorizeUrl(string clientId, string redirect, string challenge, string oauthState)
        => "https://accounts.google.com/o/oauth2/v2/auth" +
           $"?client_id={Uri.EscapeDataString(clientId)}" +
           $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
           "&response_type=code" +
           $"&scope={Uri.EscapeDataString(Scope)}" +
           $"&code_challenge={challenge}&code_challenge_method=S256" +
           $"&state={Uri.EscapeDataString(oauthState)}" +
           "&access_type=offline&prompt=consent";

    private static bool StateMatches(string expected, string? actual)
    {
        if (actual is null) return false;
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void TryClose(System.Diagnostics.Process? proc)
    {
        try { if (proc is { HasExited: false }) proc.CloseMainWindow(); } catch { }
    }

    /// <summary>Returns a fresh access token for a previously signed-in email (refresh brokered by the relay).</summary>
    public async Task<(bool ok, string? token, string? error)> GetTokenAsync(string? email, CancellationToken ct = default)
    {
        if (email is null || !state.Profile.GoogleRefreshTokens.TryGetValue(email, out var refresh))
            return (false, null, "Not signed in to this Google account. Reconnect it in Knowledge → Connect a source.");

        var (bok, tokenJson, berr) = await broker.RefreshAsync(ProviderKey, refresh, ct);
        if (!bok || tokenJson is null) return (false, null, berr ?? "Token refresh failed.");
        using var doc = JsonDocument.Parse(tokenJson);
        return (true, doc.RootElement.GetProperty("access_token").GetString(), null);
    }

    private static async Task<string?> FetchEmailAsync(HttpClient http, string? access, CancellationToken ct)
    {
        if (access is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        req.Headers.Authorization = new("Bearer", access);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    private static string? EmailFromIdToken(JsonElement token)
    {
        if (!token.TryGetProperty("id_token", out var idt) || idt.GetString() is not string jwt) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task RespondAsync(HttpListenerContext ctx, string message)
    {
        var html = BrowserLauncher.SuccessHtml(message);
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    }

    private static string RandomUrl(int bytes)
    {
        var buf = RandomNumberGenerator.GetBytes(bytes);
        return Base64Url(buf);
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
