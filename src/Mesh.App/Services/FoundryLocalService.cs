using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mesh.App.Services;

/// <summary>A tool-capable on-device model the user can pick.</summary>
public sealed record FoundryModel(string Id, string Alias, int SizeMb)
{
    public string SizeText => SizeMb >= 1024 ? $"{SizeMb / 1024.0:0.0} GB" : $"{SizeMb} MB";

    /// <summary>
    /// Rough 1–5 capability rating derived from download size (a decent proxy for
    /// parameter count). More stars = smarter but needs more RAM/CPU.
    /// </summary>
    public int Stars => SizeMb switch
    {
        < 1200 => 1,   // ~0.5–1B
        < 2600 => 2,   // ~1.5–2B
        < 5200 => 3,   // ~3–4B
        < 9500 => 4,   // ~7–8B
        _ => 5          // 12B+
    };

    public string StarText => new string('★', Stars) + new string('☆', 5 - Stars);
    public string Label => $"{StarText}  {Alias} ({SizeText})";
}

/// <summary>
/// Discovers the Foundry Local OpenAI-compatible endpoint by invoking the
/// `foundry` CLI in the background (its port is dynamic per session). Also
/// resolves the concrete loaded model id from the endpoint's /v1/models.
/// </summary>
public sealed partial class FoundryLocalService(IHttpClientFactory httpFactory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string? cachedEndpoint;
    private DateTimeOffset cachedAt;

    public bool Available { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Cached list of tool-capable on-device models, populated once at startup.</summary>
    public IReadOnlyList<FoundryModel> CachedModels { get; private set; } = Array.Empty<FoundryModel>();
    public bool ModelsLoaded { get; private set; }

    /// <summary>Raised when the cached model list changes (e.g., after startup preload).</summary>
    public event Action? ModelsChanged;

    /// <summary>
    /// Loads the on-device model catalog once and caches it. Safe to call repeatedly;
    /// only the first successful load populates the cache (unless <paramref name="force"/>).
    /// </summary>
    public async Task PreloadModelsAsync(bool force = false, CancellationToken ct = default)
    {
        if (ModelsLoaded && !force) return;
        var models = await ListToolModelsAsync(ct);
        if (models.Count > 0 || !ModelsLoaded)
        {
            CachedModels = models;
            ModelsLoaded = true;
            ModelsChanged?.Invoke();
        }
    }

    // Reliable on-device default: CPU variant avoids flaky OpenVINO/NPU inference.
    // ~1.8GB, tool-capable, good quality/speed balance on an average laptop.
    public const string DefaultModel = "qwen2.5-1.5b-instruct-generic-cpu";

    [GeneratedRegex(@"https?://[0-9A-Za-z\.\-]+:\d+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@":\d+$")]
    private static partial Regex VersionSuffixRegex();

    /// <summary>Strips a trailing version suffix (":4") so the id can be run/downloaded and prefix-matched.</summary>
    private static string Normalize(string? id)
        => string.IsNullOrWhiteSpace(id) ? DefaultModel : VersionSuffixRegex().Replace(id.Trim(), "");

    /// <summary>
    /// Lists tool-capable CPU models from the Foundry catalog so the user can pick
    /// one. Only CPU (CPUExecutionProvider) variants that support tool calling are
    /// returned, NPU/OpenVINO variants are excluded (unreliable inference).
    /// </summary>
    public async Task<IReadOnlyList<FoundryModel>> ListToolModelsAsync(CancellationToken ct = default)
    {
        var endpoint = await GetEndpointAsync(ct: ct);
        if (endpoint is null) return Array.Empty<FoundryModel>();
        try
        {
            var http = httpFactory.CreateClient("model");
            using var resp = await http.GetAsync($"{endpoint.TrimEnd('/')}/foundry/list", ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<FoundryModel>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new List<FoundryModel>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!(e.TryGetProperty("supportsToolCalling", out var t) && t.ValueKind == JsonValueKind.True)) continue;
                if (!e.TryGetProperty("runtime", out var rt)) continue;
                var device = rt.TryGetProperty("deviceType", out var d) ? d.GetString() : null;
                var ep = rt.TryGetProperty("executionProvider", out var x) ? x.GetString() : null;
                if (!string.Equals(device, "CPU", StringComparison.OrdinalIgnoreCase)) continue;
                if (ep is null || !ep.Contains("CPU", StringComparison.OrdinalIgnoreCase)) continue; // exclude OpenVINO on CPU

                var id = e.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                var alias = e.TryGetProperty("alias", out var al) ? al.GetString() ?? id : id;
                var size = e.TryGetProperty("fileSizeMb", out var fs) && fs.TryGetInt32(out var mb) ? mb : 0;
                models.Add(new FoundryModel(id, alias, size));
            }
            return models.OrderBy(m => m.SizeMb).ToList();
        }
        catch { return Array.Empty<FoundryModel>(); }
    }

    /// <summary>
    /// Ensures Foundry Local is installed and the requested (tool-capable, CPU)
    /// model is downloaded and loaded. When <paramref name="requestedModel"/> is
    /// empty, falls back to any safe loaded model or the default. Reports progress.
    /// </summary>
    public async Task<(bool ok, string? endpoint, string? model, string? error)> EnsureReadyAsync(
        string? requestedModel, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Checking for Foundry Local…");
        if (!await FoundryExistsAsync(ct))
        {
            progress?.Report("Installing Foundry Local (winget)…");
            var install = await RunRawAsync("winget", "install --id Microsoft.FoundryLocal --accept-source-agreements --accept-package-agreements --disable-interactivity", ct, TimeSpan.FromMinutes(10));
            if (!await FoundryExistsAsync(ct))
                return (false, null, null, "Couldn't install Foundry Local automatically. Install it from https://aka.ms/foundry-local and try again.\n" + Trim(install));
        }

        progress?.Report("Starting Foundry Local…");
        var endpoint = await GetEndpointAsync(forceRefresh: true, ct: ct);
        if (endpoint is null)
            return (false, null, null, LastError ?? "Foundry Local service could not be started.");

        var explicitRequest = !string.IsNullOrWhiteSpace(requestedModel);
        var target = Normalize(requestedModel);
        var loaded = await ListLoadedAsync(endpoint, ct);

        // If the target (or, with no explicit request, any safe model) is already loaded, use it.
        var already = loaded.FirstOrDefault(id => id.StartsWith(target, StringComparison.OrdinalIgnoreCase) && !IsNpu(id));
        if (already is null && !explicitRequest)
            already = loaded.FirstOrDefault(id => !IsNpu(id));
        if (already is not null)
        {
            Available = true;
            return (true, endpoint, already, null);
        }

        // Ensure the target model is downloaded (blocking).
        var cache = await RunFoundryAsync("cache list", ct);
        if (!cache.Contains(target, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report($"Downloading {target}…");
            var dl = await RunFoundryAsync($"model download {target}", ct, TimeSpan.FromMinutes(40));
            if (dl.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return (false, endpoint, null, "Model download failed: " + Trim(dl));
        }

        // Load it into the service. `model run` is interactive, so fire-and-forget and poll.
        progress?.Report("Loading model (first time can take a minute)…");
        _ = RunFoundryAsync($"model run {target}", ct, TimeSpan.FromMinutes(10));

        string? ready = null;
        for (var i = 0; i < 40 && ready is null; i++)
        {
            await Task.Delay(3000, ct);
            var now = await ListLoadedAsync(endpoint, ct);
            ready = now.FirstOrDefault(id => id.StartsWith(target, StringComparison.OrdinalIgnoreCase) && !IsNpu(id))
                    ?? now.FirstOrDefault(id => !IsNpu(id));
        }
        if (ready is null)
            return (false, endpoint, null, "The local model didn't finish loading. Try again in a moment.");

        Available = true;
        return (true, endpoint, ready, null);
    }

    private static bool IsNpu(string id)
    {
        var l = id.ToLowerInvariant();
        return l.Contains("npu") || l.Contains("openvino");
    }

    private async Task<List<string>> ListLoadedAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("model");
            using var resp = await http.GetAsync($"{endpoint.TrimEnd('/')}/v1/models", ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(e => e.GetProperty("id").GetString())
                .Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();
        }
        catch { return new(); }
    }

    private async Task<bool> FoundryExistsAsync(CancellationToken ct)
    {
        var v = await RunFoundryAsync("--version", ct, TimeSpan.FromSeconds(20));
        return !string.IsNullOrWhiteSpace(v) && !v.StartsWith("ERROR");
    }

    private static bool HasCachedModel(string cacheOutput)
        => !string.IsNullOrWhiteSpace(cacheOutput)
           && !cacheOutput.Contains("No models", StringComparison.OrdinalIgnoreCase)
           && Regex.IsMatch(cacheOutput, @"[a-z0-9]+-[a-z0-9]", RegexOptions.IgnoreCase);

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;

    /// <summary>Returns the base endpoint (scheme://host:port), starting the service if needed.</summary>
    public async Task<string?> GetEndpointAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && cachedEndpoint is not null && DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromMinutes(2))
            return cachedEndpoint;

        await Gate.WaitAsync(ct);
        try
        {
            // 1. Ask the running service where it is.
            var status = await RunFoundryAsync("service status", ct);
            var url = ExtractBase(status);

            // 2. If not running, start it (idempotent) and parse the printed URL.
            if (url is null)
            {
                var start = await RunFoundryAsync("service start", ct);
                url = ExtractBase(start);
            }

            Available = url is not null;
            LastError = url is null ? "Could not locate Foundry Local. Is it installed? (winget install Microsoft.FoundryLocal)" : null;
            if (url is not null) { cachedEndpoint = url; cachedAt = DateTimeOffset.UtcNow; }
            return url;
        }
        catch (Exception ex)
        {
            Available = false;
            LastError = ex.Message;
            return null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Resolves the model id to actually use at the endpoint. Prefers an exact/prefix
    /// match for the requested id, but always prefers CPU (generic-cpu) or GPU variants
    /// over NPU/OpenVINO ones, which can fail inference on some hardware.
    /// </summary>
    public async Task<string> ResolveModelAsync(string endpoint, string requested, CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient("model");
            using var resp = await http.GetAsync($"{endpoint.TrimEnd('/')}/v1/models", ct);
            if (!resp.IsSuccessStatusCode) return requested;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var ids = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(e => e.GetProperty("id").GetString())
                .Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();
            if (ids.Count == 0) return requested;

            // Rank: CPU best, GPU next, NPU/OpenVINO last (it crashes on some machines).
            static int Rank(string id)
            {
                var l = id.ToLowerInvariant();
                if (l.Contains("npu") || l.Contains("openvino")) return 2;
                if (l.Contains("cpu")) return 0;
                return 1;
            }

            // Candidates matching the request, else all loaded, ordered by safest EP.
            var matching = ids.Where(id =>
                id.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(requested, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(requested) && id.Contains(BaseAlias(requested), StringComparison.OrdinalIgnoreCase))).ToList();
            var pool = matching.Count > 0 ? matching : ids;
            return pool.OrderBy(Rank).First();
        }
        catch { return requested; }
    }

    private static string BaseAlias(string id)
    {
        // "qwen2.5-1.5b-instruct-generic-cpu" -> "qwen2.5-1.5b"
        var i = id.IndexOf("-instruct", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? id[..i] : id;
    }

    private static string? ExtractBase(string output)
    {
        var m = UrlRegex().Match(output ?? "");
        return m.Success ? m.Value : null;
    }

    private static Task<string> RunFoundryAsync(string args, CancellationToken ct)
        => RunFoundryAsync(args, ct, TimeSpan.FromSeconds(60));

    private static Task<string> RunFoundryAsync(string args, CancellationToken ct, TimeSpan timeout)
        => RunRawAsync("foundry", args, ct, timeout);

    private static async Task<string> RunRawAsync(string file, string args, CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, // resolved via PATH (foundry: WindowsApps alias; winget: system)
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            using (timeoutCts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
                await proc.WaitForExitAsync(timeoutCts.Token);
            return (await outTask) + "\n" + (await errTask);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }
}
