using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Shared;

namespace Mesh.Relay.Push;

/// <summary>Sends alert, alert-plus-background, and silent background APNs notifications over HTTP/2.</summary>
public sealed class ApnsPushSender : IPushSender, IDisposable
{
    private static readonly HashSet<string> InvalidTokenReasons = new(StringComparer.Ordinal)
    {
        "BadDeviceToken",
        "DeviceTokenNotForTopic",
        "Unregistered"
    };

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

    public ApnsPushSender(
        string keyId,
        string teamId,
        string bundleId,
        string p8PrivateKey,
        bool production,
        ILogger logger)
        : this(
            keyId,
            teamId,
            bundleId,
            p8PrivateKey,
            production,
            logger,
            new SocketsHttpHandler())
    {
    }

    internal ApnsPushSender(
        string keyId,
        string teamId,
        string bundleId,
        string p8PrivateKey,
        bool production,
        ILogger logger,
        HttpMessageHandler handler)
    {
        this.keyId = keyId;
        this.teamId = teamId;
        this.bundleId = bundleId;
        this.logger = logger;
        key = ECDsa.Create();
        key.ImportFromPem(p8PrivateKey);
        http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(production ? "https://api.push.apple.com" : "https://api.sandbox.push.apple.com"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    public async Task<PushSendResult> SendWakeAsync(
        string token,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/3/device/{token}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(SerializeWakePayload(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("authorization", "bearer " + GetJwt());
        req.Headers.TryAddWithoutValidation("apns-topic", bundleId);

        // A wake is always a silent, collapsible background notification: no alert, no content.
        req.Headers.TryAddWithoutValidation("apns-push-type", "background");
        req.Headers.TryAddWithoutValidation("apns-priority", "5");
        req.Headers.TryAddWithoutValidation("apns-collapse-id", "mesh-sync");
        req.Headers.TryAddWithoutValidation("apns-expiration", "0");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return PushSendResult.Sent();

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var reason = ParseReason(body);
        logger.LogWarning("APNs push rejected {Status}: {Reason}", (int)resp.StatusCode, reason ?? body);
        return resp.StatusCode == HttpStatusCode.Gone || reason is not null && InvalidTokenReasons.Contains(reason)
            ? PushSendResult.InvalidToken((int)resp.StatusCode, reason)
            : PushSendResult.Rejected((int)resp.StatusCode, reason);
    }

    internal static string SerializeWakePayload()
    {
        // Contentless wake: silent background push, no alert/sound/category, no sender or frame id.
        var payload = new Dictionary<string, object?>
        {
            ["aps"] = new Dictionary<string, object?> { ["content-available"] = 1 },
            ["mesh"] = new Dictionary<string, object?>
            {
                ["type"] = "sync",
                ["v"] = MeshProtocol.Version,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string? ParseReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("reason", out var reason)
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
            var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            cachedJwt = signingInput + "." + B64Url(signature);
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
