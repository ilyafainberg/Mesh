using System.Net.Http.Json;
using System.Text.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Client-side source of connector OAuth metadata. The catalog (authorize/token URLs and public
/// client ids for the built-in connectors) is served by the relay at <c>GET /connectors</c>, not
/// compiled into the client, so the client ships no OAuth app credentials. The last successful
/// fetch is cached in the profile so connectors remain usable across brief offline periods; only
/// the live OAuth exchange (which needs the internet anyway) truly requires connectivity.
/// </summary>
public sealed class ConnectorCatalogService(IHttpClientFactory httpFactory, AppState state)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Dictionary<string, ConnectorEndpoint>? cache;

    /// <summary>Raised when the catalog is (re)loaded, so UI can refresh availability.</summary>
    public event Action? Changed;

    /// <summary>True once a catalog is available (from the relay this session or the persisted cache).</summary>
    public bool IsLoaded => Endpoints().Count > 0;

    /// <summary>Returns the endpoint for a provider key, or null if the catalog has no entry / is unavailable.</summary>
    public ConnectorEndpoint? Get(string key)
        => Endpoints().TryGetValue(key, out var e) ? e : null;

    private Dictionary<string, ConnectorEndpoint> Endpoints()
    {
        if (cache is not null) return cache;
        // Fall back to the persisted cache from a previous online session.
        var raw = state.Profile.ConnectorCatalogCache;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<ConnectorEndpoint>>(raw, Json);
                if (list is not null)
                    cache = list.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
            }
            catch { /* corrupt cache: ignore, will refetch */ }
        }
        return cache ?? new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetches the catalog from the current relay and caches it (memory + persisted profile).
    /// Best-effort: on failure the previously cached catalog (if any) is kept. Returns true if a
    /// catalog is available afterwards (freshly fetched or from cache).
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var relay = state.Profile.RelayUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(relay)) return IsLoaded;
        try
        {
            var http = httpFactory.CreateClient("relay");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var list = await http.GetFromJsonAsync<List<ConnectorEndpoint>>($"{relay}/connectors", Json, cts.Token);
            if (list is { Count: > 0 })
            {
                cache = list.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
                var serialized = JsonSerializer.Serialize(list, Json);
                if (serialized != state.Profile.ConnectorCatalogCache)
                    state.Mutate(p => p.ConnectorCatalogCache = serialized);
                Changed?.Invoke();
            }
        }
        catch { /* offline or relay without the endpoint: keep whatever cache we have */ }
        return IsLoaded;
    }
}
