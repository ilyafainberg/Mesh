using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

namespace Mesh.Relay.Push;

/// <summary>
/// Sends alerts to Android devices via FCM (Firebase Cloud Messaging) HTTP v1. Authenticates with an OAuth2
/// access token minted from a Google service account (RS256 JWT bearer grant), cached and refreshed hourly.
/// A single bad device token logs a warning and is swallowed, so it never breaks a fan-out to other tokens.
/// </summary>
public sealed class FcmPushSender : IPushSender, IDisposable
{
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly HttpClient http = new();
    private readonly string projectId;
    private readonly string clientEmail;
    private readonly string tokenUri;
    private readonly RSA key;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessTokenExpiry;

    public string Platform => DevicePlatforms.Android;

    /// <param name="serviceAccountJson">
    /// The Google service-account JSON (contains project_id, client_email, private_key, and token_uri).
    /// </param>
    public FcmPushSender(string serviceAccountJson, ILogger logger)
    {
        this.logger = logger;
        using var doc = JsonDocument.Parse(serviceAccountJson);
        var root = doc.RootElement;
        projectId = root.GetProperty("project_id").GetString()
            ?? throw new InvalidOperationException("FCM service account is missing project_id");
        clientEmail = root.GetProperty("client_email").GetString()
            ?? throw new InvalidOperationException("FCM service account is missing client_email");
        tokenUri = root.TryGetProperty("token_uri", out var tu) ? (tu.GetString() ?? DefaultTokenUri) : DefaultTokenUri;
        var pem = root.GetProperty("private_key").GetString()
            ?? throw new InvalidOperationException("FCM service account is missing private_key");
        key = RSA.Create();
        key.ImportFromPem(pem);
    }

    public async Task SendAsync(string token, PushAlert alert, CancellationToken ct = default)
    {
        var access = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        var message = new
        {
            message = new
            {
                token,
                notification = new { title = alert.Title, body = alert.Body },
                android = new { priority = "high" },
            },
        };
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send")
        {
            Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("FCM push rejected {Status}: {Body}", (int)resp.StatusCode, body);
        }
    }

    // OAuth2 service-account flow: sign an RS256 JWT bearer assertion and exchange it for an access token.
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (accessToken is not null && DateTimeOffset.UtcNow < accessTokenExpiry)
            return accessToken;

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (accessToken is not null && DateTimeOffset.UtcNow < accessTokenExpiry)
                return accessToken;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = clientEmail,
                scope = Scope,
                aud = tokenUri,
                iat = now,
                exp = now + 3600,
            }));
            var signingInput = header + "." + claims;
            var sig = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var assertion = signingInput + "." + B64Url(sig);

            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
            };
            using var resp = await http.SendAsync(tokenReq, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            accessToken = root.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("FCM token endpoint returned no access_token");
            var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
            accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string B64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        http.Dispose();
        key.Dispose();
        gate.Dispose();
    }
}
