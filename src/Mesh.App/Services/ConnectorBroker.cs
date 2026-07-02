using System.Net.Http;
using System.Net.Http.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Client side of the relay token broker. For confidential connectors (Google, Notion, Slack)
/// the client never holds the OAuth client secret, it forwards the grant (authorization code
/// or refresh token) to the Mesh relay, which injects the secret and returns the provider's raw
/// token JSON. Requests are signed with the device key registered under the user's handle.
/// </summary>
public sealed class ConnectorBroker(IHttpClientFactory httpFactory, AppState state)
{
    public async Task<(bool ok, string? tokenJson, string? error)> ExchangeCodeAsync(
        string providerKey, string code, string redirectUri, string? codeVerifier, CancellationToken ct = default)
        => await PostAsync(providerKey, ConnectorProtocol.GrantAuthCode, code: code,
            redirectUri: redirectUri, codeVerifier: codeVerifier, refreshToken: null, ct);

    public async Task<(bool ok, string? tokenJson, string? error)> RefreshAsync(
        string providerKey, string refreshToken, CancellationToken ct = default)
        => await PostAsync(providerKey, ConnectorProtocol.GrantRefresh, code: null,
            redirectUri: null, codeVerifier: null, refreshToken: refreshToken, ct);

    private async Task<(bool ok, string? tokenJson, string? error)> PostAsync(
        string providerKey, string grant, string? code, string? redirectUri, string? codeVerifier,
        string? refreshToken, CancellationToken ct)
    {
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey))
            return (false, null, "Set up your Mesh identity first, a device key is required to connect this source.");
        if (string.IsNullOrWhiteSpace(p.RelayUrl))
            return (false, null, "No relay configured.");

        var secretMaterial = ConnectorProtocol.SecretMaterial(grant, code, refreshToken);
        var secretHash = LinkProtocol.HashCode(secretMaterial);
        var message = ConnectorProtocol.TokenMessage(providerKey, p.Handle, grant, secretHash, redirectUri);
        var signature = IdentityService.Sign(p.PrivateKey, message);
        var request = new ConnectorTokenRequest(
            providerKey, AppState.Norm(p.Handle), p.PublicKey, grant, code, redirectUri, codeVerifier, refreshToken, signature);

        var http = httpFactory.CreateClient("relay");
        try
        {
            var resp = await http.PostAsJsonAsync(
                $"{p.RelayUrl.TrimEnd('/')}/connectors/{providerKey}/token", request, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"Relay token exchange failed ({(int)resp.StatusCode}).");
            var result = await resp.Content.ReadFromJsonAsync<ConnectorTokenResponse>(cancellationToken: ct);
            return result is null ? (false, null, "Empty relay response.") : (true, result.TokenJson, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }
}
