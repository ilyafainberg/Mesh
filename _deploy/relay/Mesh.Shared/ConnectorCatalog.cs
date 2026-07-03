namespace Mesh.Shared;

/// <summary>
/// Public OAuth metadata for a built-in connector. Shared by the client and the relay
/// so both agree on endpoints and the (public) client id. Client <b>secrets</b> are never
/// stored here, for confidential providers the relay injects the secret from server-side
/// configuration during the brokered token exchange.
/// </summary>
public sealed record ConnectorEndpoint(
    string Key,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    bool UseBasicAuth,
    bool Confidential);

/// <summary>
/// Single source of truth for Mesh's built-in connector OAuth apps. Client ids are public
/// identifiers (they appear in every authorize URL) and are safe to ship. Providers marked
/// <see cref="ConnectorEndpoint.Confidential"/> require a client secret at token exchange;
/// that exchange is brokered by the relay so the secret never ships in the client.
/// </summary>
public static class ConnectorCatalog
{
    public static readonly IReadOnlyDictionary<string, ConnectorEndpoint> Endpoints =
        new Dictionary<string, ConnectorEndpoint>(StringComparer.OrdinalIgnoreCase)
        {
            // Dropbox is a public (PKCE) client, no secret, exchanged directly by the client.
            ["dropbox"] = new("dropbox",
                "https://www.dropbox.com/oauth2/authorize",
                "https://api.dropboxapi.com/oauth2/token",
                ClientId: "e9hydz26ol0th7r",
                UseBasicAuth: false,
                Confidential: false),

            // Notion is a confidential client (HTTP Basic at token exchange), brokered by the relay.
            ["notion"] = new("notion",
                "https://api.notion.com/v1/oauth/authorize",
                "https://api.notion.com/v1/oauth/token",
                ClientId: "391d872b-594c-8152-87c3-003782d069bf",
                UseBasicAuth: true,
                Confidential: true),

            // Slack is a confidential client (client_secret in the form), brokered by the relay.
            ["slack"] = new("slack",
                "https://slack.com/oauth/v2/authorize",
                "https://slack.com/api/oauth.v2.access",
                ClientId: "11500284656598.11486994076135",
                UseBasicAuth: false,
                Confidential: true),

            // Google is a confidential (Web application) client, brokered by the relay for both
            // the initial code exchange and hourly refresh, so the secret never ships in the client.
            ["google"] = new("google",
                "https://accounts.google.com/o/oauth2/v2/auth",
                "https://oauth2.googleapis.com/token",
                ClientId: "151481598328-d82q4elsbo6bn37p2ishnhqjmflbsg61.apps.googleusercontent.com",
                UseBasicAuth: false,
                Confidential: true),
        };

    public static ConnectorEndpoint? Get(string key)
        => Endpoints.TryGetValue(key, out var e) ? e : null;
}
