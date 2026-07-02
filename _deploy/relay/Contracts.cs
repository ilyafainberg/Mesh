namespace Mesh.Shared;

/// <summary>Register (or re-assert) a handle together with a device public key.</summary>
public record RegisterHandleRequest(
    string Handle,
    string DevicePublicKey,
    string? DisplayName);

public record RegisterHandleResponse(
    string Handle,
    string DeviceId,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Device-linking: an already-authorized device creates a short-lived, single-use
/// invite so another device can join the same handle. The relay only stores the
/// hash of the code; the raw code travels out-of-band (QR) to the new device.
/// Signature is over <c>link-invite|handle|codeHash|expiresAtUnix</c> by the creator key.
/// </summary>
public record LinkInviteRequest(
    string Handle,
    string CreatorPublicKey,
    string CodeHash,
    long ExpiresAtUnix,
    string Signature);

public record LinkInviteResponse(string Handle, long ExpiresAtUnix);

/// <summary>
/// Device-linking redemption by the new device. Presents the raw invite code plus
/// its own new public key, signed to prove key possession.
/// Signature is over <c>link-redeem|handle|code</c> by <see cref="NewPublicKey"/>.
/// </summary>
public record LinkRedeemRequest(
    string Handle,
    string NewPublicKey,
    string Code,
    string Signature);

public record LinkRedeemResponse(string Handle, string DeviceId, string? DisplayName);

/// <summary>Canonical strings + hashing used by both client and relay for device-linking.</summary>
public static class LinkProtocol
{
    public static string InviteMessage(string handle, string codeHash, long expiresAtUnix)
        => $"link-invite|{Normalize(handle)}|{codeHash}|{expiresAtUnix}";

    public static string RedeemMessage(string handle, string code)
        => $"link-redeem|{Normalize(handle)}|{code}";

    public static string HashCode(string code)
        => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(code)));

    public static string Normalize(string handle)
        => handle.Trim().TrimStart('@').ToLowerInvariant();
}

/// <summary>
/// Brokered token exchange: for confidential connectors (Google, Notion, Slack, …) the client
/// forwards an OAuth grant to the relay, which holds the client secret and performs the exchange.
/// Supports both the initial <c>authorization_code</c> exchange and hourly <c>refresh_token</c>
/// refresh. The client authenticates with a device key registered under its handle, so only real
/// Mesh users can use the shared OAuth apps.
/// Signature is over <c>connector-token|provider|handle|grantType|secretHash|redirectUri</c> by the
/// device key, where <c>secretHash</c> hashes the code (auth code grant) or the refresh token.
/// </summary>
public record ConnectorTokenRequest(
    string Provider,
    string Handle,
    string DevicePublicKey,
    string GrantType,
    string? Code,
    string? RedirectUri,
    string? CodeVerifier,
    string? RefreshToken,
    string Signature);

/// <summary>The provider's raw token response, passed back verbatim as JSON.</summary>
public record ConnectorTokenResponse(string TokenJson);

/// <summary>Canonical strings used by both client and relay for the connector token broker.</summary>
public static class ConnectorProtocol
{
    public const string GrantAuthCode = "authorization_code";
    public const string GrantRefresh = "refresh_token";

    /// <summary>The value bound into the signature: the code (auth code grant) or refresh token.</summary>
    public static string SecretMaterial(string grantType, string? code, string? refreshToken)
        => grantType == GrantRefresh ? (refreshToken ?? "") : (code ?? "");

    public static string TokenMessage(string provider, string handle, string grantType, string secretHash, string? redirectUri)
        => $"connector-token|{provider.ToLowerInvariant()}|{LinkProtocol.Normalize(handle)}|{grantType}|{secretHash}|{redirectUri ?? ""}";
}

/// <summary>Public directory view of a handle (no private data).</summary>
public record HandleInfo(
    string Handle,
    string? DisplayName,
    IReadOnlyList<string> DevicePublicKeys,
    bool Online,
    DateTimeOffset RegisteredAt);

/// <summary>
/// An end-to-end message routed by the relay between two handles.
/// The relay treats <see cref="Body"/> as opaque and never inspects it.
/// </summary>
public record MeshEnvelope(
    string Id,
    string From,
    string To,
    string Kind,
    string Body,
    string? Signature,
    DateTimeOffset SentAt)
{
    public static MeshEnvelope Create(string from, string to, string kind, string body, string? signature = null)
        => new(Guid.NewGuid().ToString("n"), from, to, kind, body, signature, DateTimeOffset.UtcNow);
}

/// <summary>Well-known envelope kinds for the prototype.</summary>
public static class MeshKinds
{
    public const string Chat = "chat";
    public const string AgentRequest = "agent.request";
    public const string AgentResponse = "agent.response";
    public const string System = "system";
}
