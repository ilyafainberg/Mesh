using System.Text.Json;
using System.Text.RegularExpressions;
using Mesh.App.Domain;
using Microsoft.Extensions.Logging;

namespace Mesh.App.Services;

/// <summary>
/// Connects Skill "marketplaces" to the user's local Skills list. Two catalog shapes are supported:
///
/// 1. Bespoke flat index (unknown fields ignored, optionals default to ""):
/// <code>
/// {
///   "name": "PowerCAT Community Skills",
///   "skills": [
///     { "id": "book-intro-call", "name": "Book a 30-min intro call",
///       "description": "...", "instructions": "...", "version": "1.2.0" }
///   ]
/// }
/// </code>
/// Per skill, <c>id</c> and <c>name</c> are required; the rest are optional. The top-level
/// <c>name</c> falls back to the URL host.
///
/// 2. Claude/Copilot plugin marketplace hosted as a GitHub repo. The index lives at
/// <c>&lt;repo&gt;/.claude-plugin/marketplace.json</c> (fetched from the raw host) and groups skills
/// under plugins:
/// <code>
/// {
///   "name": "example-skills", "displayName": "Example Skills",
///   "plugins": [
///     { "name": "example-plugin", "description": "...", "version": "1.0.0",
///       "skills": [ "./plugins/example-plugin/skills/do-something" ] }
///   ]
/// }
/// </code>
/// Each skill path points at a folder holding a markdown file (SKILL.md, else &lt;folder&gt;.md) whose
/// YAML frontmatter supplies name/description and whose body becomes the skill instructions.
/// </summary>
public sealed class SkillMarketplaceService
{
    private readonly AppState state;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<SkillMarketplaceService> log;

    private static readonly Regex OwnerRepoPattern =
        new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    public SkillMarketplaceService(AppState state, IHttpClientFactory httpFactory, ILogger<SkillMarketplaceService> log)
    {
        this.state = state;
        this.httpFactory = httpFactory;
        this.log = log;
    }

    public sealed record MarketplaceSkill(string Id, string Name, string Description, string Instructions, string Version);

    /// <summary>A group of skills offered together (Claude/Copilot "plugin").</summary>
    public sealed record MarketplacePlugin(string Name, string Description, IReadOnlyList<MarketplaceSkill> Skills);

    public sealed record MarketplaceIndex(string Name, IReadOnlyList<MarketplaceSkill> Skills)
    {
        /// <summary>Plugin groupings for Claude/Copilot marketplaces. Empty for flat bespoke indexes.</summary>
        public IReadOnlyList<MarketplacePlugin> Plugins { get; init; } = Array.Empty<MarketplacePlugin>();
    }

    /// <summary>Fetch and parse a marketplace index from a URL. Returns (null, friendly error) on failure.</summary>
    public async Task<(MarketplaceIndex? index, string? error)> FetchAsync(string url, CancellationToken ct = default)
    {
        var (index, error, _) = await FetchInternalAsync(url, ct);
        return (index, error);
    }

    /// <summary>
    /// Resolve the user input to a raw marketplace URL, fetch it, and parse it. Also returns the
    /// resolved URL so callers can persist the canonical location for later re-syncs.
    /// </summary>
    private async Task<(MarketplaceIndex? index, string? error, string resolvedUrl)> FetchInternalAsync(string url, CancellationToken ct)
    {
        var trimmed = (url ?? "").Trim();
        var (candidates, resolveError) = ResolveCandidates(trimmed);
        if (resolveError is not null || candidates.Count == 0)
            return (null, resolveError ?? "That does not look like a valid marketplace URL.", trimmed);

        try
        {
            var http = httpFactory.CreateClient("updater");

            // Guard the whole operation (index + any per-skill markdown fetches) to 30 seconds.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return (null, "That does not look like a valid http(s) URL.", candidate);

                using var resp = await http.GetAsync(uri, timeout.Token);

                // Branch probing: fall through to the next candidate only on a 404.
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && i < candidates.Count - 1)
                    continue;

                if (!resp.IsSuccessStatusCode)
                    return (null, $"Marketplace returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.", candidate);

                var json = await resp.Content.ReadAsStringAsync(timeout.Token);
                var index = await ParseIndexAsync(json, uri, http, timeout.Token);
                if (index is null)
                    return (null, "The marketplace did not return a valid skills index.", candidate);

                return (index, null, candidate);
            }

            return (null, "Could not find a marketplace index at that location.", candidates[^1]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, "Fetch cancelled.", trimmed);
        }
        catch (OperationCanceledException)
        {
            return (null, "Timed out contacting the marketplace.", trimmed);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to fetch marketplace {Url}", trimmed);
            return (null, "Could not reach the marketplace: " + ex.Message, trimmed);
        }
    }

    /// <summary>
    /// Turn arbitrary user input into an ordered list of candidate marketplace URLs to try.
    /// Supports: a raw marketplace.json URL, a github.com/OWNER/REPO(/tree/BRANCH) URL, a bare
    /// OWNER/REPO string, and a direct URL to a bespoke flat JSON. When no branch is given for a
    /// GitHub repo, "main" is tried first and "master" second.
    /// </summary>
    private static (IReadOnlyList<string> candidates, string? error) ResolveCandidates(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (Array.Empty<string>(), "Enter a marketplace URL.");

        // Bare OWNER/REPO (no scheme).
        if (!input.Contains("://", StringComparison.Ordinal) && OwnerRepoPattern.IsMatch(input))
        {
            var slash = input.IndexOf('/');
            var owner = input[..slash];
            var repo = TrimGitSuffix(input[(slash + 1)..]);
            return (BranchCandidates(owner, repo, null), null);
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (Array.Empty<string>(), "That does not look like a valid http(s) URL or OWNER/REPO.");

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 2)
                return (Array.Empty<string>(), "That GitHub URL does not include an OWNER/REPO.");

            var owner = segs[0];
            var repo = TrimGitSuffix(segs[1]);
            string? branch = null;
            if (segs.Length >= 4 && segs[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
                branch = segs[3];

            return (BranchCandidates(owner, repo, branch), null);
        }

        // Any other host (raw.githubusercontent.com or a bespoke flat-JSON host): use as-is.
        return (new[] { input }, null);
    }

    private static string TrimGitSuffix(string repo)
        => repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    private static IReadOnlyList<string> BranchCandidates(string owner, string repo, string? branch)
    {
        static string Raw(string owner, string repo, string branch)
            => $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/.claude-plugin/marketplace.json";

        return string.IsNullOrWhiteSpace(branch)
            ? new[] { Raw(owner, repo, "main"), Raw(owner, repo, "master") }
            : new[] { Raw(owner, repo, branch!) };
    }

    /// <summary>Detect the catalog shape and parse it. Returns null when the JSON is unusable.</summary>
    private async Task<MarketplaceIndex?> ParseIndexAsync(string json, Uri uri, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // Claude/Copilot format: a top-level "plugins" array.
            if (root.TryGetProperty("plugins", out var pluginsEl) && pluginsEl.ValueKind == JsonValueKind.Array)
                return await ParseClaudeAsync(root, pluginsEl, uri, http, ct);

            // Bespoke flat format: a top-level "skills" array (or neither, yielding an empty index).
            return ParseFlat(root, uri);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Malformed marketplace JSON from {Host}", uri.Host);
            return null;
        }
    }

    private MarketplaceIndex ParseFlat(JsonElement root, Uri uri)
    {
        var name = ReadString(root, "name");
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

    private async Task<MarketplaceIndex?> ParseClaudeAsync(JsonElement root, JsonElement pluginsEl, Uri uri, HttpClient http, CancellationToken ct)
    {
        var name = ReadString(root, "displayName");
        if (string.IsNullOrWhiteSpace(name)) name = ReadString(root, "name");
        if (string.IsNullOrWhiteSpace(name)) name = uri.Host;

        var repoRootRaw = RepoRootRawBase(uri);

        var plugins = new List<MarketplacePlugin>();
        var all = new List<MarketplaceSkill>();

        foreach (var pl in pluginsEl.EnumerateArray())
        {
            if (ct.IsCancellationRequested) break;
            if (pl.ValueKind != JsonValueKind.Object) continue;

            var pluginName = ReadString(pl, "name");
            var pluginDesc = ReadString(pl, "description");
            var version = ReadString(pl, "version");

            if (!pl.TryGetProperty("skills", out var skillsEl) || skillsEl.ValueKind != JsonValueKind.Array)
                continue;

            var pluginSkills = new List<MarketplaceSkill>();
            foreach (var sp in skillsEl.EnumerateArray())
            {
                if (ct.IsCancellationRequested) break;
                if (sp.ValueKind != JsonValueKind.String) continue;

                var path = sp.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;

                var skill = await FetchSkillAsync(repoRootRaw, path!, version, http, ct);
                if (skill is null) continue; // could not fetch/parse this skill: skip it, keep going

                pluginSkills.Add(skill);
                all.Add(skill);
            }

            if (pluginSkills.Count > 0)
                plugins.Add(new MarketplacePlugin(
                    string.IsNullOrWhiteSpace(pluginName) ? "Plugin" : pluginName,
                    pluginDesc,
                    pluginSkills));
        }

        return new MarketplaceIndex(name, all) { Plugins = plugins };
    }

    /// <summary>
    /// Fetch and parse a single skill folder. Tries "&lt;folder&gt;/SKILL.md" then
    /// "&lt;folder&gt;/&lt;folder&gt;.md". Returns null (logged) if the markdown can't be fetched.
    /// </summary>
    private async Task<MarketplaceSkill?> FetchSkillAsync(string repoRootRaw, string path, string version, HttpClient http, CancellationToken ct)
    {
        try
        {
            var rel = path.Replace('\\', '/').Trim().TrimStart('.', '/').TrimEnd('/');
            if (rel.Length == 0) return null;

            var lastSlash = rel.LastIndexOf('/');
            var folderName = lastSlash >= 0 ? rel[(lastSlash + 1)..] : rel;
            var folderBase = repoRootRaw + rel + "/";

            var mdCandidates = new[] { folderBase + "SKILL.md", folderBase + folderName + ".md" };

            string? md = null;
            foreach (var mdUrl in mdCandidates)
            {
                if (ct.IsCancellationRequested) return null;
                if (!Uri.TryCreate(mdUrl, UriKind.Absolute, out var mdUri)) continue;

                using var resp = await http.GetAsync(mdUri, ct);
                if (!resp.IsSuccessStatusCode) continue; // e.g. SKILL.md 404s, fall back to <folder>.md

                md = await resp.Content.ReadAsStringAsync(ct);
                break;
            }

            if (string.IsNullOrWhiteSpace(md))
            {
                log.LogInformation("No skill markdown found for {Folder}", folderName);
                return null;
            }

            var (fmName, fmDesc, body) = ParseFrontmatter(md!);
            var displayName = string.IsNullOrWhiteSpace(fmName) ? folderName : fmName;
            var instructions = string.IsNullOrWhiteSpace(body) ? md!.Trim() : body;

            return new MarketplaceSkill(folderName, displayName, fmDesc, instructions, version);
        }
        catch (OperationCanceledException)
        {
            return null; // per-skill guard: never abort the whole index
        }
        catch (Exception ex)
        {
            log.LogInformation(ex, "Failed to import skill from {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Derive the repo's raw base URL (skill paths are relative to the repo root). The marketplace
    /// index lives at "&lt;root&gt;/.claude-plugin/marketplace.json"; strip that suffix to get the root.
    /// </summary>
    private static string RepoRootRawBase(Uri marketplaceUri)
    {
        var abs = marketplaceUri.GetLeftPart(UriPartial.Path);
        const string suffix = "/.claude-plugin/marketplace.json";
        if (abs.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return abs[..^suffix.Length] + "/";

        var slash = abs.LastIndexOf('/');
        return slash >= 0 ? abs[..(slash + 1)] : abs + "/";
    }

    /// <summary>
    /// Parse simple YAML frontmatter: the block between the first pair of "---" lines. Reads
    /// name/description (quoted or unquoted scalars); everything after the closing "---" is the body.
    /// If there is no frontmatter, the whole document is treated as the body.
    /// </summary>
    private static (string name, string description, string body) ParseFrontmatter(string md)
    {
        var text = md.TrimStart('\uFEFF');
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        int start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0) start++;
        if (start >= lines.Length || lines[start].Trim() != "---")
            return ("", "", normalized.Trim());

        int end = -1;
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { end = i; break; }
        }
        if (end < 0)
            return ("", "", normalized.Trim()); // unterminated frontmatter: treat all as body

        string name = "", description = "";
        for (int i = start + 1; i < end; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var val = Unquote(line[(colon + 1)..].Trim());

            if (name.Length == 0 && key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = val;
            else if (description.Length == 0 && key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = val;
        }

        var body = string.Join("\n", lines.Skip(end + 1)).Trim();
        return (name, description, body);
    }

    private static string Unquote(string v)
        => v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))
            ? v[1..^1]
            : v;

    private static string ReadString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "")
            : "";

    /// <summary>Add a marketplace (validated by fetching it first). Returns (created, null) or (null, error).</summary>
    public async Task<(SkillMarketplace? added, string? error)> AddMarketplaceAsync(string url, CancellationToken ct = default)
    {
        var (index, error, resolvedUrl) = await FetchInternalAsync((url ?? "").Trim(), ct);
        if (index is null)
            return (null, error ?? "Could not add that marketplace.");

        // Persist the resolved raw URL so a github.com or OWNER/REPO input keeps re-syncing correctly.
        if (state.Profile.SkillMarketplaces.Any(m => string.Equals(m.Url, resolvedUrl, StringComparison.OrdinalIgnoreCase)))
            return (null, "That marketplace is already added.");

        var fallbackName = Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var u) ? u.Host : resolvedUrl;
        var market = new SkillMarketplace
        {
            Name = string.IsNullOrWhiteSpace(index.Name) ? fallbackName : index.Name,
            Url = resolvedUrl,
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
