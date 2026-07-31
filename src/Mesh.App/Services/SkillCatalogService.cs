using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>A single search hit from the built-in skill catalog, scoped to the current device.</summary>
public sealed class SkillCatalogResult
{
    /// <summary>Catalog-native id/slug (unique within its <see cref="Source"/>).</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";

    /// <summary>Human-formatted star count as shown in the catalog (e.g. "37.3K"). Empty if unknown.</summary>
    public string Stars { get; init; } = "";
    /// <summary>Parsed numeric star count (0 if unknown).</summary>
    public long StarCount { get; init; }

    public string RepositoryUrl { get; init; } = "";
    public string SkillsPageUrl { get; init; } = "";

    /// <summary>Which catalog produced the hit: "skills.sh" or "agentskill.sh".</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Advisory compatibility inferred from the skill's Skill.md (OS, device class, required CLI
    /// tools). This is a heuristic only; the authoritative verdict is produced by
    /// <see cref="SkillCompatibilityChecker"/> when the real package is parsed at install time. Null
    /// when the catalog could not be scanned.
    /// </summary>
    public SkillCompatibility? Advisory { get; init; }
}

/// <summary>Tuning for the built-in catalog adapter.</summary>
public sealed class SkillCatalogOptions
{
    public string SkillsShBaseUrl { get; set; } = "https://skills.sh";
    public string SkillsShPageBaseUrl { get; set; } = "https://www.skills.sh";
    public string AgentSkillsBaseUrl { get; set; } = "https://agentskill.sh";

    /// <summary>Optional GitHub token provider (bearer). Returns null when no token is configured.</summary>
    public Func<string?>? GitHubTokenProvider { get; set; }

    /// <summary>How long search and scan results are cached.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of cached entries (per cache) before the oldest are evicted.</summary>
    public int CacheCapacity { get; set; } = 64;

    public const string AgentUserAgent = "mesh-skills/1.17";
}

/// <summary>Self-contained, mockable built-in skill catalog.</summary>
public interface ISkillCatalogService
{
    /// <summary>
    /// Search skills.sh and agentskill.sh, dedupe, and return hits with advisory OS/CLI metadata. When
    /// <paramref name="forCurrentDevice"/> is true, hits the current device cannot possibly use are
    /// filtered out (advisory only; install re-checks authoritatively).
    /// </summary>
    Task<IReadOnlyList<SkillCatalogResult>> SearchAsync(
        string keyword, int maxResults = 20, bool forCurrentDevice = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lazily download the selected skill and parse its full folder structure through the safe archive
    /// parser, returning a validated <see cref="SkillPackageContent"/>.
    /// </summary>
    Task<SkillPackageContent> DownloadPackageAsync(
        SkillCatalogResult result, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mesh-native, self-contained implementation of <see cref="ISkillCatalogService"/>. It talks to the
/// public skills.sh and agentskill.sh catalogs and to the GitHub REST/raw APIs directly through a
/// single injected <see cref="HttpClient"/> (the named "skillcatalog" client, 12s timeout), setting
/// per-request headers so the one client serves all three hosts. Behavior mirrors the community
/// SkillsLib client (endpoints, star parsing, OS/CLI heuristic) but the code is original and carries
/// no external project reference. Search and scan results are cached with a bounded, expiring store.
/// </summary>
public sealed class SkillCatalogService : ISkillCatalogService
{
    private readonly HttpClient _http;
    private readonly SkillCatalogOptions _opts;

    private readonly ExpiringCache<string, IReadOnlyList<SkillCatalogResult>> _searchCache;
    private readonly ExpiringCache<string, SkillPackageContent> _packageCache;

    public SkillCatalogService(HttpClient http, SkillCatalogOptions? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _opts = options ?? new SkillCatalogOptions();
        _searchCache = new ExpiringCache<string, IReadOnlyList<SkillCatalogResult>>(_opts.CacheCapacity, _opts.CacheDuration);
        _packageCache = new ExpiringCache<string, SkillPackageContent>(_opts.CacheCapacity, _opts.CacheDuration);
    }

    public async Task<IReadOnlyList<SkillCatalogResult>> SearchAsync(
        string keyword, int maxResults = 20, bool forCurrentDevice = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Keyword must not be empty.", nameof(keyword));
        maxResults = Math.Clamp(maxResults, 1, 100);

        var cacheKey = $"{keyword.Trim().ToLowerInvariant()}|{maxResults}|{forCurrentDevice}";
        if (_searchCache.TryGet(cacheKey, out var cached))
            return cached;

        var merged = new List<SkillCatalogResult>();

        var shResults = await SafeSearch(() => SearchSkillsShAsync(keyword, maxResults, cancellationToken)).ConfigureAwait(false);
        merged.AddRange(shResults);

        var agentResults = await SafeSearch(() => SearchAgentSkillShAsync(keyword, maxResults, cancellationToken)).ConfigureAwait(false);
        foreach (var r in agentResults)
        {
            if (!merged.Any(x =>
                string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.RepositoryUrl, r.RepositoryUrl, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(r);
            }
        }

        var scoped = merged.Take(maxResults).ToList();

        if (forCurrentDevice)
        {
            var checker = SkillCompatibilityChecker.ForCurrentDevice();
            scoped = scoped
                .Where(r => r.Advisory is null || checker.Check(r.Advisory).CanInstall)
                .ToList();
        }

        IReadOnlyList<SkillCatalogResult> result = scoped;
        _searchCache.Set(cacheKey, result);
        return result;
    }

    public async Task<SkillPackageContent> DownloadPackageAsync(
        SkillCatalogResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var cacheKey = $"{result.Source}|{result.Id}|{result.RepositoryUrl}";
        if (_packageCache.TryGet(cacheKey, out var cached))
            return cached;

        var files = result.Source == "agentskill.sh"
            ? await DownloadAgentSkillFilesAsync(result, cancellationToken).ConfigureAwait(false)
            : await DownloadSkillsShFilesAsync(result, cancellationToken).ConfigureAwait(false);

        var zipBytes = BuildZip(files);

        // Derive advisory compatibility from the actual Skill.md within the downloaded set, then let the
        // archive parser validate everything. The authoritative device check happens later at install.
        var mdText = FindSkillMarkdown(files);
        var hasSupporting = files.Count(f => !IsSkillMarkdown(f.Path)) > 0;
        var advisory = Analyze(mdText ?? "", hasSupporting);

        var content = SkillPackageArchive.Parse(
            zipBytes,
            advisory,
            version: null,
            source: string.IsNullOrEmpty(result.RepositoryUrl) ? result.SkillsPageUrl : result.RepositoryUrl,
            trust: SkillPackageTrust.Community);

        _packageCache.Set(cacheKey, content);
        return content;
    }

    // ---- skills.sh ---------------------------------------------------------

    private async Task<List<SkillCatalogResult>> SearchSkillsShAsync(string keyword, int limit, CancellationToken ct)
    {
        var url = $"{_opts.SkillsShBaseUrl.TrimEnd('/')}/api/search?q={Uri.EscapeDataString(keyword)}&limit={limit}";
        using var resp = await _http.SendAsync(Get(url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return new();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("skills", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();

        var results = new List<SkillCatalogResult>();
        foreach (var e in arr.EnumerateArray().Take(limit))
        {
            var r = await EnrichSkillsShAsync(e, ct).ConfigureAwait(false);
            if (r is not null) results.Add(r);
        }
        return results;
    }

    private async Task<SkillCatalogResult?> EnrichSkillsShAsync(JsonElement e, CancellationToken ct)
    {
        try
        {
            var name = Str(e, "name");
            var slug = Str(e, "id");
            if (string.IsNullOrEmpty(slug)) slug = Str(e, "slug");
            var source = Str(e, "source");
            var pageUrl = $"{_opts.SkillsShPageBaseUrl.TrimEnd('/')}/{slug}";

            using var pageResp = await _http.SendAsync(Get(pageUrl), ct).ConfigureAwait(false);
            if (!pageResp.IsSuccessStatusCode) return null;
            var html = await pageResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var description = "";
            var meta = Regex.Match(html, "<meta name=\"description\" content=\"([^\"]+)\"");
            if (meta.Success) description = System.Net.WebUtility.HtmlDecode(meta.Groups[1].Value);

            var stars = "";
            var starsMatch = Regex.Match(html,
                @"<span>GitHub Stars</span></div><div[^>]*><svg[\s\S]*?</svg><span>([\d.,]+[kKmM]?)</span>",
                RegexOptions.Singleline);
            if (starsMatch.Success) stars = starsMatch.Groups[1].Value;

            var repoUrl = "";
            var repoMatch = Regex.Match(html, "<a[^>]+href=\"(https://github\\.com/[^\"]+)\"[^>]+rel=\"ugc");
            if (repoMatch.Success) repoUrl = repoMatch.Groups[1].Value.TrimEnd('/');

            var category = "";
            if (!string.IsNullOrEmpty(source))
            {
                var parts = source.Split('/');
                category = parts.Length >= 2 ? parts[^1].Replace("-", " ") : source;
            }

            var advisory = await TryScanSkillsShAsync(name, repoUrl, ct).ConfigureAwait(false);

            return new SkillCatalogResult
            {
                Id = slug,
                Name = name,
                Description = description,
                Category = category,
                Stars = stars,
                StarCount = ParseStars(stars),
                RepositoryUrl = repoUrl,
                SkillsPageUrl = pageUrl,
                Source = "skills.sh",
                Advisory = advisory
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<SkillCompatibility?> TryScanSkillsShAsync(string name, string repoUrl, CancellationToken ct)
    {
        try
        {
            var files = await DownloadSkillsShFilesAsync(
                new SkillCatalogResult { Name = name, RepositoryUrl = repoUrl, Source = "skills.sh" }, ct)
                .ConfigureAwait(false);
            var md = FindSkillMarkdown(files);
            if (md is null) return null;
            var hasSupporting = files.Count(f => !IsSkillMarkdown(f.Path)) > 0;
            return Analyze(md, hasSupporting);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<(string Path, byte[] Bytes)>> DownloadSkillsShFilesAsync(
        SkillCatalogResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result.RepositoryUrl))
            throw new InvalidOperationException("A repository URL is required to download a skills.sh skill.");

        var uri = new Uri(result.RepositoryUrl);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
            throw new InvalidOperationException($"Cannot parse owner/repo from '{result.RepositoryUrl}'.");
        var owner = parts[0];
        var repo = parts[1];
        var slug = string.Join("-",
            result.Name.ToLowerInvariant().Split(new[] { ' ', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));

        var repoJson = await GitHubGetStringAsync($"https://api.github.com/repos/{owner}/{repo}", ct).ConfigureAwait(false);
        using var repoDoc = JsonDocument.Parse(repoJson);
        var branch = repoDoc.RootElement.TryGetProperty("default_branch", out var brEl)
            ? brEl.GetString() ?? "main" : "main";

        var treeJson = await GitHubGetStringAsync(
            $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branch}?recursive=1", ct).ConfigureAwait(false);
        using var treeDoc = JsonDocument.Parse(treeJson);
        if (!treeDoc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub tree response is missing its 'tree' array.");

        string? skillPath = null;
        foreach (var node in tree.EnumerateArray())
        {
            if (Str(node, "type") != "tree") continue;
            var path = Str(node, "path");
            var last = path.Split('/').Last().ToLowerInvariant();
            if (!string.IsNullOrEmpty(slug) && (last == slug || last.Contains(slug)))
            {
                skillPath = path;
                break;
            }
        }
        if (skillPath is null)
            throw new InvalidOperationException($"Could not locate a folder for skill '{result.Name}' in {owner}/{repo}.");

        var targets = new List<(string Path, string Url)>();
        foreach (var node in tree.EnumerateArray())
        {
            if (Str(node, "type") != "blob") continue;
            var path = Str(node, "path");
            if (path != skillPath && !path.StartsWith(skillPath + "/", StringComparison.Ordinal)) continue;
            var rel = path.Length > skillPath.Length ? path[(skillPath.Length + 1)..] : path.Split('/').Last();
            targets.Add((rel, $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}"));
        }
        if (targets.Count == 0)
            throw new InvalidOperationException($"No files found under '{skillPath}' in {owner}/{repo}.");

        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var (path, dlUrl) in targets)
        {
            using var resp = await _http.SendAsync(GitHubGet(dlUrl), ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            files.Add((path, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)));
        }
        return files;
    }

    // ---- agentskill.sh -----------------------------------------------------

    private async Task<List<SkillCatalogResult>> SearchAgentSkillShAsync(string keyword, int limit, CancellationToken ct)
    {
        var baseUrl = _opts.AgentSkillsBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/api/agent/search?q={Uri.EscapeDataString(keyword)}&limit={limit}";
        using var resp = await _http.SendAsync(AgentGet(url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return new();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("skills", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();

        var results = new List<SkillCatalogResult>();
        foreach (var e in arr.EnumerateArray().Take(limit))
        {
            var r = await EnrichAgentSkillAsync(e, baseUrl, ct).ConfigureAwait(false);
            if (r is not null) results.Add(r);
        }
        return results;
    }

    private async Task<SkillCatalogResult?> EnrichAgentSkillAsync(JsonElement e, string baseUrl, CancellationToken ct)
    {
        try
        {
            var slug = Str(e, "slug");
            var name = Str(e, "name");
            var description = Str(e, "description");
            var owner = Str(e, "owner");

            var category = "";
            var stars = "";
            var repoUrl = "";

            var detailUrl = $"{baseUrl}/api/skills/{Uri.EscapeDataString(slug)}";
            using (var detailResp = await _http.SendAsync(AgentGet(detailUrl), ct).ConfigureAwait(false))
            {
                if (detailResp.IsSuccessStatusCode)
                {
                    var detailJson = await detailResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var detailDoc = JsonDocument.Parse(detailJson);
                    if (detailDoc.RootElement.TryGetProperty("data", out var data))
                    {
                        category = Str(data, "category");
                        if (data.TryGetProperty("githubStars", out var s) && s.ValueKind == JsonValueKind.Number)
                            stars = FormatStars(s.GetInt64());
                        repoUrl = Str(data, "repositoryUrl");
                    }
                }
            }
            if (string.IsNullOrEmpty(repoUrl) && !string.IsNullOrEmpty(owner))
                repoUrl = $"https://github.com/{owner}";

            var advisory = await TryScanAgentSkillAsync(slug, name, ct).ConfigureAwait(false);

            return new SkillCatalogResult
            {
                Id = slug,
                Name = name,
                Description = description,
                Category = category,
                Stars = stars,
                StarCount = ParseStars(stars),
                RepositoryUrl = repoUrl,
                SkillsPageUrl = $"{baseUrl}/{slug}",
                Source = "agentskill.sh",
                Advisory = advisory
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<SkillCompatibility?> TryScanAgentSkillAsync(string slug, string name, CancellationToken ct)
    {
        try
        {
            var files = await DownloadAgentSkillFilesAsync(
                new SkillCatalogResult { Id = slug, Name = name, Source = "agentskill.sh" }, ct).ConfigureAwait(false);
            var md = FindSkillMarkdown(files);
            if (md is null) return null;
            var hasSupporting = files.Count(f => !IsSkillMarkdown(f.Path)) > 0;
            return Analyze(md, hasSupporting);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<(string Path, byte[] Bytes)>> DownloadAgentSkillFilesAsync(
        SkillCatalogResult result, CancellationToken ct)
    {
        var baseUrl = _opts.AgentSkillsBaseUrl.TrimEnd('/');
        var slug = result.Id.StartsWith('@') ? result.Id[1..] : result.Id;
        var installUrl = $"{baseUrl}/api/agent/skills/{Uri.EscapeDataString(slug)}/install";

        using var resp = await _http.SendAsync(AgentGet(installUrl), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"agentskill.sh install returned {(int)resp.StatusCode} for '{slug}'.");

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var skillMd = root.TryGetProperty("skillMd", out var mdEl) ? mdEl.GetString() ?? "" : "";
        var files = new List<(string Path, byte[] Bytes)>
        {
            ("Skill.md", Encoding.UTF8.GetBytes(skillMd))
        };

        if (root.TryGetProperty("skillFiles", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesEl.EnumerateArray())
            {
                var path = Str(file, "path");
                if (string.IsNullOrEmpty(path) || IsSkillMarkdown(path)) continue;
                var contentText = Str(file, "content");
                files.Add((path, Encoding.UTF8.GetBytes(contentText)));
            }
        }
        return files;
    }

    // ---- OS / CLI heuristic ------------------------------------------------

    private static readonly string[] CliPatterns =
    {
        @"\bgit\b", @"\bnpm\b", @"\bnode\b", @"\bnpx\b", @"\bpython\b", @"\bpip\b",
        @"\bdocker\b", @"\bkubectl\b", @"\bterraform\b", @"\baws\b", @"\bgcloud\b",
        @"\bazure\b", @"\bssh\b", @"\bcurl\b", @"\bwget\b", @"\bbash\b", @"\bzsh\b",
        @"\bpowershell\b", @"\bmake\b", @"\bcargo\b", @"\bruby\b", @"\bmvn\b",
        @"\bcomposer\b", @"\bphp\b", @"\bffmpeg\b", @"\bgo\b", @"\bsed\b", @"\bawk\b"
    };

    /// <summary>
    /// Advisory OS/device/CLI heuristic over a Skill.md body. Desktop OS flags come from mentions; if
    /// nothing OS-specific is found the skill is assumed cross-platform desktop. iOS/Android are marked
    /// supported ONLY when the package is Skill.md-only (no supporting files) and no CLI tool is
    /// detected - otherwise mobile is excluded. The authoritative check remains
    /// <see cref="SkillCompatibilityChecker"/>.
    /// </summary>
    public static SkillCompatibility Analyze(string skillMarkdown, bool hasSupportingFiles)
    {
        var lower = (skillMarkdown ?? "").ToLowerInvariant();

        var os = SkillOperatingSystems.None;
        if (Regex.IsMatch(lower, @"\bwindows\b|\bwin32\b|\bpowershell\b|\bcmd\.exe\b|\bbat\b"))
            os |= SkillOperatingSystems.Windows;
        if (Regex.IsMatch(lower, @"\blinux\b|\bubuntu\b|\bdebian\b|\bfedora\b|\bapt\b|\byum\b|\bdnf\b"))
            os |= SkillOperatingSystems.Linux;
        if (Regex.IsMatch(lower, @"\bmacos\b|\bmac os\b|\bdarwin\b|\bbrew\b|\bhomebrew\b|\bosx\b"))
            os |= SkillOperatingSystems.MacOS;

        if (os == SkillOperatingSystems.None)
            os = SkillOperatingSystems.AllDesktop;

        var cli = new List<string>();
        foreach (var pattern in CliPatterns)
        {
            var m = Regex.Match(lower, pattern);
            if (m.Success)
            {
                var tool = m.Value.Trim();
                if (!cli.Contains(tool)) cli.Add(tool);
            }
        }

        var mobileEligible = cli.Count == 0 && !hasSupportingFiles;
        if (mobileEligible)
            os |= SkillOperatingSystems.AllMobile;

        var deviceClass = cli.Count > 0
            ? SkillDeviceClass.Desktop
            : mobileEligible ? SkillDeviceClass.Universal : SkillDeviceClass.Desktop;

        return new SkillCompatibility
        {
            OperatingSystems = os,
            DeviceClass = deviceClass,
            RequiredCliTools = cli
        };
    }

    // ---- HTTP + parsing helpers -------------------------------------------

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private static HttpRequestMessage AgentGet(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(SkillCatalogOptions.AgentUserAgent);
        return req;
    }

    private HttpRequestMessage GitHubGet(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(SkillCatalogOptions.AgentUserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var token = _opts.GitHubTokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<string> GitHubGetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(GitHubGet(url), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<SkillCatalogResult>> SafeSearch(Func<Task<List<SkillCatalogResult>>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new();
        }
    }

    private static string? FindSkillMarkdown(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        foreach (var f in files)
            if (IsSkillMarkdown(f.Path))
                return Encoding.UTF8.GetString(f.Bytes);
        return null;
    }

    private static bool IsSkillMarkdown(string path)
    {
        var name = path.Replace('\\', '/');
        name = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        return string.Equals(name, "skill.md", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildZip(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = zip.CreateEntry(path.Replace('\\', '/'), CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Parse a human star string ("37.3K", "1.2M", "875") to a numeric count.</summary>
    public static long ParseStars(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Trim().Replace(",", "").ToLowerInvariant();
        try
        {
            if (text.EndsWith('k'))
                return (long)(double.Parse(text.TrimEnd('k'), CultureInfo.InvariantCulture) * 1_000);
            if (text.EndsWith('m'))
                return (long)(double.Parse(text.TrimEnd('m'), CultureInfo.InvariantCulture) * 1_000_000);
            return long.TryParse(text, out var v) ? v : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string FormatStars(long count)
    {
        if (count >= 1_000_000) return (count / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (count >= 1_000) return (count / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return count.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>A tiny bounded, time-expiring cache used to keep catalog search/scan results.</summary>
internal sealed class ExpiringCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset Expires)> _map = new();

    public ExpiringCache(int capacity, TimeSpan ttl)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            value = entry.Value;
            return true;
        }
        _map.TryRemove(key, out _);
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.Count >= _capacity)
        {
            // Evict expired first, then the soonest-to-expire, to stay bounded.
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _map)
                if (kv.Value.Expires <= now) _map.TryRemove(kv.Key, out _);
            while (_map.Count >= _capacity)
            {
                var oldest = _map.OrderBy(kv => kv.Value.Expires).FirstOrDefault();
                if (oldest.Key is null) break;
                _map.TryRemove(oldest.Key, out _);
            }
        }
        _map[key] = (value, DateTimeOffset.UtcNow.Add(_ttl));
    }
}
