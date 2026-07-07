using System.Text.Json;
using Mesh.App.Domain;
using Microsoft.Extensions.Logging;

namespace Mesh.App.Services;

/// <summary>
/// Connects Skill "marketplaces" (remote JSON catalogs) to the user's local Skills list.
///
/// A marketplace URL returns a JSON index of this shape (unknown fields are ignored, optionals default to ""):
/// <code>
/// {
///   "name": "PowerCAT Community Skills",
///   "skills": [
///     {
///       "id": "book-intro-call",
///       "name": "Book a 30-min intro call",
///       "description": "Offers two slots and confirms by email.",
///       "instructions": "Offer two time slots in the next 3 business days...",
///       "version": "1.2.0"
///     }
///   ]
/// }
/// </code>
/// Per skill, <c>id</c> and <c>name</c> are required; <c>description</c>, <c>instructions</c> and
/// <c>version</c> are optional. The top-level <c>name</c> is optional and falls back to the URL host.
/// </summary>
public sealed class SkillMarketplaceService
{
    private readonly AppState state;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<SkillMarketplaceService> log;

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SkillMarketplaceService(AppState state, IHttpClientFactory httpFactory, ILogger<SkillMarketplaceService> log)
    {
        this.state = state;
        this.httpFactory = httpFactory;
        this.log = log;
    }

    public sealed record MarketplaceSkill(string Id, string Name, string Description, string Instructions, string Version);
    public sealed record MarketplaceIndex(string Name, IReadOnlyList<MarketplaceSkill> Skills);

    /// <summary>Fetch and parse a marketplace index from a URL. Returns (null, friendly error) on failure.</summary>
    public async Task<(MarketplaceIndex? index, string? error)> FetchAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (null, "Enter a marketplace URL.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, "That does not look like a valid http(s) URL.");

        try
        {
            var http = httpFactory.CreateClient("updater");

            // Guard against clients without a short timeout: cap the fetch to 30 seconds.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            using var resp = await http.GetAsync(uri, timeout.Token);
            if (!resp.IsSuccessStatusCode)
                return (null, $"Marketplace returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            var json = await resp.Content.ReadAsStringAsync(timeout.Token);
            var index = Parse(json, uri);
            if (index is null)
                return (null, "The marketplace did not return a valid skills index.");

            return (index, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, "Fetch cancelled.");
        }
        catch (OperationCanceledException)
        {
            return (null, "Timed out contacting the marketplace.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to fetch marketplace {Url}", url);
            return (null, "Could not reach the marketplace: " + ex.Message);
        }
    }

    private MarketplaceIndex? Parse(string json, Uri uri)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string name = "";
            if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                name = nameEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                name = uri.Host;

            var skills = new List<MarketplaceSkill>();
            if (root.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in skillsEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;

                    var id = ReadString(el, "id");
                    var skillName = ReadString(el, "name");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(skillName))
                        continue; // id + name are required per skill

                    skills.Add(new MarketplaceSkill(
                        id.Trim(),
                        skillName.Trim(),
                        ReadString(el, "description"),
                        ReadString(el, "instructions"),
                        ReadString(el, "version")));
                }
            }

            return new MarketplaceIndex(name, skills);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Malformed marketplace JSON from {Host}", uri.Host);
            return null;
        }
    }

    private static string ReadString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "")
            : "";

    /// <summary>Add a marketplace (validated by fetching it first). Returns (created, null) or (null, error).</summary>
    public async Task<(SkillMarketplace? added, string? error)> AddMarketplaceAsync(string url, CancellationToken ct = default)
    {
        var trimmed = (url ?? "").Trim();
        var (index, error) = await FetchAsync(trimmed, ct);
        if (index is null)
            return (null, error ?? "Could not add that marketplace.");

        if (state.Profile.SkillMarketplaces.Any(m => string.Equals(m.Url, trimmed, StringComparison.OrdinalIgnoreCase)))
            return (null, "That marketplace is already added.");

        var market = new SkillMarketplace
        {
            Name = string.IsNullOrWhiteSpace(index.Name) ? new Uri(trimmed).Host : index.Name,
            Url = trimmed,
            LastSyncedAt = DateTimeOffset.UtcNow
        };
        state.Mutate(p => p.SkillMarketplaces.Add(market));
        return (market, null);
    }

    /// <summary>
    /// Remove a marketplace. Imported skills from it are kept but become "orphaned" (their
    /// SourceMarketplaceId stays, they just no longer auto-update). The user's skills are never deleted.
    /// </summary>
    public void RemoveMarketplace(string marketplaceId)
    {
        if (string.IsNullOrEmpty(marketplaceId)) return;
        state.Mutate(p =>
        {
            var market = p.SkillMarketplaces.FirstOrDefault(m => m.Id == marketplaceId);
            if (market is not null) p.SkillMarketplaces.Remove(market);
        });
    }

    /// <summary>
    /// Import selected skills (by their marketplace skill id) from a fetched index into Profile.Skills.
    /// Already-imported skills (same SourceMarketplaceId + SourceSkillId) are skipped. New skills default
    /// to Visibility="private", Enabled=true, and are tagged with source + version.
    /// </summary>
    public void ImportSkills(string marketplaceId, MarketplaceIndex index, IEnumerable<string> skillIds)
    {
        if (string.IsNullOrEmpty(marketplaceId) || index is null) return;
        var wanted = new HashSet<string>(skillIds ?? Enumerable.Empty<string>());
        if (wanted.Count == 0) return;

        state.Mutate(p =>
        {
            foreach (var ms in index.Skills)
            {
                if (!wanted.Contains(ms.Id)) continue;

                var alreadyImported = p.Skills.Any(s =>
                    s.SourceMarketplaceId == marketplaceId && s.SourceSkillId == ms.Id);
                if (alreadyImported) continue;

                p.Skills.Add(new Skill
                {
                    Name = ms.Name,
                    Description = ms.Description,
                    Instructions = ms.Instructions,
                    Visibility = "private",
                    Enabled = true,
                    SourceMarketplaceId = marketplaceId,
                    SourceSkillId = ms.Id,
                    Version = string.IsNullOrWhiteSpace(ms.Version) ? null : ms.Version
                });
            }
        });
    }

    /// <summary>
    /// Startup auto-update: for each marketplace, fetch it and refresh every imported skill's
    /// Name/Description/Instructions/Version from the matching marketplace skill, preserving the
    /// user's Enabled and Visibility choices. Failed fetches are skipped (logged, never thrown).
    /// Local (non-imported) skills are never touched. Safe to fire-and-forget at startup.
    /// </summary>
    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        // Snapshot ids so we iterate stably even if the profile changes underneath us.
        var markets = state.Profile.SkillMarketplaces.ToList();
        foreach (var market in markets)
        {
            if (ct.IsCancellationRequested) break;

            var (index, error) = await FetchAsync(market.Url, ct);
            if (index is null)
            {
                log.LogInformation("Skipping marketplace {Name} during startup sync: {Error}", market.Name, error);
                continue;
            }

            state.Mutate(p =>
            {
                var live = p.SkillMarketplaces.FirstOrDefault(m => m.Id == market.Id);
                if (live is null) return; // removed while syncing

                if (!string.IsNullOrWhiteSpace(index.Name))
                    live.Name = index.Name;

                foreach (var skill in p.Skills)
                {
                    if (skill.SourceMarketplaceId != live.Id) continue;

                    var ms = index.Skills.FirstOrDefault(x => x.Id == skill.SourceSkillId);
                    if (ms is null) continue; // no longer offered; leave the local copy intact

                    skill.Name = ms.Name;
                    skill.Description = ms.Description;
                    skill.Instructions = ms.Instructions;
                    skill.Version = string.IsNullOrWhiteSpace(ms.Version) ? null : ms.Version;
                    // Enabled and Visibility are the user's choices: preserve them.
                }

                live.LastSyncedAt = DateTimeOffset.UtcNow;
            });
        }
    }
}
