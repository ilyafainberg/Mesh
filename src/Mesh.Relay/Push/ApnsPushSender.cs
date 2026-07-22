using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

namespace Mesh.Relay.Push;

/// <summary>
/// Sends alerts to iOS devices via APNs (Apple Push Notification service) over HTTP/2, authenticated with a
/// provider JWT signed by an APNs auth key (ES256, .p8). The JWT is cached and refreshed hourly per Apple's rules.
/// A single bad device token logs a warning and is swallowed, so it never breaks a fan-out to other tokens.
/// </summary>
public sealed class ApnsPushSender : IPushSender, IDisposable
{
    private readonly HttpClient http;
    private readonly string keyId;
    private readonly string teamId;
    private readonly string bundleId;
    private readonly ECDsa key;
    private readonly ILogger logger;
    private readonly object gate = new();
    private string? cachedJwt;
    private DateTimeOffset jwtIssuedAt;

    public string Platform => DevicePlatforms.IOS;

    /// <param name="keyId">The 10-character APNs auth-key id.</param>
    /// <param name="teamId">The Apple Developer team id.</param>
    /// <param name="bundleId">The app bundle id, sent as the apns-topic.</param>
    /// <param name="p8PrivateKey">The PEM contents of the APNs .p8 auth key (PKCS#8 EC private key).</param>
    /// <param name="production">true => api.push.apple.com; false => api.sandbox.push.apple.com.</param>
    public ApnsPushSender(string keyId, string teamId, string bundleId, string p8PrivateKey, bool production, ILogger logger)
    {
        this.keyId = keyId;
        this.teamId = teamId;
        this.bundleId = bundleId;
        this.logger = logger;
        key = ECDsa.Create();
        key.ImportFromPem(p8PrivateKey);
        http = new HttpClient
        {
            BaseAddress = new Uri(production ? "https://api.push.apple.com" : "https://api.sandbox.push.apple.com"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    public async Task SendAsync(string token, PushAlert alert, CancellationToken ct = default)
    {
        var payload = new
        {
            aps = new
            {
                alert = new { title = alert.Title, body = alert.Body },
                sound = "default",
                category = alert.Category,
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/3/device/{token}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("authorization", "bearer " + GetJwt());
        req.Headers.TryAddWithoutValidation("apns-topic", bundleId);
        req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        req.Headers.TryAddWithoutValidation("apns-priority", "10");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("APNs push rejected {Status}: {Body}", (int)resp.StatusCode, body);
        }
    }

    // Provider JWT: header {alg:ES256, kid}, claims {iss:teamId, iat}. Apple requires reuse for <= 1 hour; we
    // rotate every 50 minutes. ECDsa.SignData emits the raw r||s (IEEE P1363) form JWS/ES256 expects.
    private string GetJwt()
    {
        lock (gate)
        {
            if (cachedJwt is not null && DateTimeOffset.UtcNow - jwtIssuedAt < TimeSpan.FromMinutes(50))
                return cachedJwt;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId }));
            var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = teamId, iat = now }));
            var signingInput = header + "." + claims;
            var sig = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            cachedJwt = signingInput + "." + B64Url(sig);
            jwtIssuedAt = DateTimeOffset.UtcNow;
            return cachedJwt;
        }
    }

    private static string B64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        http.Dispose();
        key.Dispose();
    }
}
