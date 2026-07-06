using Mesh.Shared;

namespace Mesh.Relay;

/// <summary>
/// The relay's source of truth for built-in connector OAuth apps. Client ids (public identifiers)
/// and secrets live in the relay's configuration, not in the shared/open code or the client binary.
///
/// A relay ships sensible defaults for the well-known providers so it works out of the box, but any
/// value can be overridden via configuration, so a self-hoster can plug in their OWN OAuth apps:
///   Connectors:{key}:clientId        (or env CONNECTOR_{KEY}_CLIENT_ID)
///   Connectors:{key}:authorizeUrl / :tokenUrl / :useBasicAuth / :confidential
/// The matching client secret (confidential providers only) is read separately at token-exchange
/// time and is never exposed by the public <c>GET /connectors</c> endpoint.
/// </summary>
public sealed class RelayConnectorCatalog
{
    private readonly IReadOnlyDictionary<string, ConnectorEndpoint> endpoints;

    public RelayConnectorCatalog(IConfiguration config)
    {
        // Provider-fixed defaults (public client ids). Any field is overridable via config so a
        // self-hosted relay can substitute its own registered OAuth apps.
        var defaults = new (string key, string auth, string token, string clientId, bool basic, bool conf)[]
        {
            ("dropbox", "https://www.dropbox.com/oauth2/authorize", "https://api.dropboxapi.com/oauth2/token",
                "e9hydz26ol0th7r", false, false),
            ("notion", "https://api.notion.com/v1/oauth/authorize", "https://api.notion.com/v1/oauth/token",
                "391d872b-594c-8152-87c3-003782d069bf", true, true),
            ("slack", "https://slack.com/oauth/v2/authorize", "https://slack.com/api/oauth.v2.access",
                "11500284656598.11486994076135", false, true),
            ("google", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
                "151481598328-d82q4elsbo6bn37p2ishnhqjmflbsg61.apps.googleusercontent.com", false, true),
        };

        var map = new Dictionary<string, ConnectorEndpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults)
        {
            map[d.key] = new ConnectorEndpoint(
                d.key,
                Get(config, d.key, "authorizeUrl", d.auth),
                Get(config, d.key, "tokenUrl", d.token),
                Get(config, d.key, "clientId", d.clientId),
                GetBool(config, d.key, "useBasicAuth", d.basic),
                GetBool(config, d.key, "confidential", d.conf));
        }
        endpoints = map;
    }

    public ConnectorEndpoint? Get(string key)
        => endpoints.TryGetValue(key, out var e) ? e : null;

    /// <summary>All connector endpoints (public metadata only; no secrets).</summary>
    public IReadOnlyCollection<ConnectorEndpoint> All => endpoints.Values.ToArray();

    private static string Get(IConfiguration config, string key, string field, string fallback)
    {
        var val = config[$"Connectors:{key}:{field}"]
            ?? Environment.GetEnvironmentVariable($"CONNECTOR_{key.ToUpperInvariant()}_{ToEnv(field)}");
        return string.IsNullOrWhiteSpace(val) ? fallback : val;
    }

    private static bool GetBool(IConfiguration config, string key, string field, bool fallback)
    {
        var raw = config[$"Connectors:{key}:{field}"]
            ?? Environment.GetEnvironmentVariable($"CONNECTOR_{key.ToUpperInvariant()}_{ToEnv(field)}");
        return bool.TryParse(raw, out var b) ? b : fallback;
    }

    // useBasicAuth -> USE_BASIC_AUTH, clientId -> CLIENT_ID, etc.
    private static string ToEnv(string field)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in field)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
