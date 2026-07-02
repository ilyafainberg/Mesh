using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>Base for Graph file tools that can download + extract document text.</summary>
public abstract class GraphFileTool(MsalAuthService auth, IHttpClientFactory httpFactory, DocumentExtractor extractor, string? accountId, string[] scopes) : IAgentTool
{
    protected DocumentExtractor Extractor => extractor;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract object ParametersSchema { get; }
    public abstract Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default);

    protected async Task<(HttpClient? http, string? error)> ClientAsync(CancellationToken ct)
    {
        var (ok, token, error) = await auth.GetTokenAsync(accountId, scopes, ct);
        if (!ok || token is null) return (null, error ?? "Not signed in to Microsoft.");
        var http = httpFactory.CreateClient("graph");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (http, null);
    }

    /// <summary>Downloads a drive item and extracts its text. ref = "driveId:itemId".</summary>
    protected async Task<string> FetchAndExtractAsync(HttpClient http, string driveId, string itemId, string name, CancellationToken ct)
    {
        if (!DocumentExtractor.IsSupported(name))
            return $"'{name}' is not a text-extractable document type.";
        var url = $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/content";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return $"ERROR: Graph {(int)resp.StatusCode} downloading '{name}'.";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        try { return Extractor.Extract(bytes, name); }
        catch (Exception ex) { return $"Couldn't read '{name}': {ex.Message}"; }
    }

    protected static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Searches the signed-in user's OneDrive for files. Live, read-only.</summary>
public sealed class SearchOneDriveTool(MsalAuthService auth, IHttpClientFactory httpFactory, DocumentExtractor extractor, string? accountId, string[] scopes, string label, string nameSuffix, IReadOnlyList<FolderRef>? folders = null)
    : GraphFileTool(auth, httpFactory, extractor, accountId, scopes)
{
    public override string Name => "search_onedrive" + nameSuffix;
    public override string Description =>
        folders is { Count: > 0 }
            ? $"Search {label}'s OneDrive within these folders only: {string.Join(", ", folders.Select(f => f.Name))}. Returns file names and a ref you can pass to get_file_content to read a document."
            : $"Search {label}'s OneDrive for files matching a query. Returns file names, folders and a ref you can pass to get_file_content to read a document. Use when the user asks about their files/documents.";
    public override object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "What to search for (file name or content keywords)." },
            top = new { type = "integer", description = "Max files to return (default 8)." }
        },
        required = new[] { "query" }
    };

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (http, error) = await ClientAsync(ct);
        if (http is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        var top = Math.Clamp(ToolArgs.GetInt(args, "top", 8), 1, 20);
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var q = Uri.EscapeDataString(query);
        // Whole-drive search, or a search scoped to each granted folder (recursive within it).
        var urls = folders is { Count: > 0 }
            ? folders.Select(f =>
              {
                  var (driveId, itemId) = SourceBrowser.SplitRef(f.Id);
                  return $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/search(q='{q}')?$top={top}&$select=id,name,webUrl,size,lastModifiedDateTime,parentReference,file,folder";
              }).ToList()
            : new List<string> { $"https://graph.microsoft.com/v1.0/me/drive/root/search(q='{q}')?$top={top}&$select=id,name,webUrl,size,lastModifiedDateTime,parentReference,file,folder" };

        try
        {
            var merged = new System.Text.StringBuilder();
            int n = 0;
            foreach (var url in urls)
            {
                if (n >= top) break;
                using var resp = await http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return $"ERROR: Graph {(int)resp.StatusCode}: {Trim(body)}";
                n = AppendItems(JsonDocument.Parse(body), merged, n, top);
            }
            return n == 0 ? "No matching files found." : merged.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    internal static int AppendItems(JsonDocument doc, System.Text.StringBuilder sb, int start, int max)
    {
        int n = start;
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (n >= max) break;
            n++;
            var name = item.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
            var isFolder = item.TryGetProperty("folder", out _);
            var id = item.TryGetProperty("id", out var idv) ? idv.GetString() : "";
            var driveId = item.TryGetProperty("parentReference", out var pr) && pr.TryGetProperty("driveId", out var dv) ? dv.GetString() : "";
            var path = item.TryGetProperty("parentReference", out var pr2) && pr2.TryGetProperty("path", out var pv)
                ? pv.GetString()?.Replace("/drive/root:", "") : "";
            var when = item.TryGetProperty("lastModifiedDateTime", out var d) ? d.GetString() : "";
            if (isFolder)
                sb.AppendLine($"{n}. [folder] {name} ({path})");
            else
                sb.AppendLine($"{n}. {name}, {path} (modified {when}) [ref: {driveId}:{id}]");
        }
        return n;
    }
}

/// <summary>Searches SharePoint (and shared drives) for files via the Microsoft Search API.</summary>
public sealed class SearchSharePointTool(MsalAuthService auth, IHttpClientFactory httpFactory, DocumentExtractor extractor, string? accountId, string[] scopes, string label, string nameSuffix)
    : GraphFileTool(auth, httpFactory, extractor, accountId, scopes)
{
    public override string Name => "search_sharepoint" + nameSuffix;
    public override string Description =>
        $"Search {label}'s SharePoint sites and shared document libraries for files. Returns file names, sites and a ref you can pass to get_file_content. Use when the user asks about team/company documents or a SharePoint site.";
    public override object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Keywords to search for across SharePoint documents." }
        },
        required = new[] { "query" }
    };

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (http, error) = await ClientAsync(ct);
        if (http is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var payload = new
        {
            requests = new[]
            {
                new { entityTypes = new[] { "driveItem" }, query = new { queryString = query }, from = 0, size = 10 }
            }
        };
        try
        {
            using var resp = await http.PostAsJsonAsync("https://graph.microsoft.com/v1.0/search/query", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Graph {(int)resp.StatusCode}: {Trim(body)}";
            using var doc = JsonDocument.Parse(body);
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var container in doc.RootElement.GetProperty("value").EnumerateArray())
            foreach (var hitsC in container.GetProperty("hitsContainers").EnumerateArray())
            {
                if (!hitsC.TryGetProperty("hits", out var hits)) continue;
                foreach (var hit in hits.EnumerateArray())
                {
                    if (!hit.TryGetProperty("resource", out var res)) continue;
                    n++;
                    var name = res.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
                    var id = res.TryGetProperty("id", out var idv) ? idv.GetString() : "";
                    var driveId = res.TryGetProperty("parentReference", out var pr) && pr.TryGetProperty("driveId", out var dv) ? dv.GetString() : "";
                    var summary = hit.TryGetProperty("summary", out var su) ? su.GetString() : "";
                    sb.AppendLine($"{n}. {name}, {Strip(summary)} [ref: {driveId}:{id}]");
                }
            }
            return n == 0 ? "No matching SharePoint files found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string Strip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}

/// <summary>Reads the text of a OneDrive/SharePoint document by its ref (from a search result).</summary>
public sealed class GetGraphFileTool(MsalAuthService auth, IHttpClientFactory httpFactory, DocumentExtractor extractor, string? accountId, string[] scopes, string label, string nameSuffix)
    : GraphFileTool(auth, httpFactory, extractor, accountId, scopes)
{
    public override string Name => "get_file_content" + nameSuffix;
    public override string Description =>
        $"Read the full text of a {label} OneDrive/SharePoint document. Pass the 'ref' from a search_onedrive or search_sharepoint result. Returns extracted text (PDF, Word, Excel, PowerPoint, or plain text).";
    public override object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "The file ref in the form driveId:itemId from a search result." },
            name = new { type = "string", description = "The file name (for choosing how to extract), e.g. report.docx." }
        },
        required = new[] { "ref", "name" }
    };

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (http, error) = await ClientAsync(ct);
        if (http is null) return "ERROR: " + error;
        var refStr = ToolArgs.GetString(args, "ref");
        var name = ToolArgs.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(refStr) || !refStr.Contains(':'))
            return "ERROR: a valid ref (driveId:itemId) is required.";
        var idx = refStr.IndexOf(':');
        var driveId = refStr[..idx];
        var itemId = refStr[(idx + 1)..];
        if (string.IsNullOrWhiteSpace(name)) name = "file.bin";
        return await FetchAndExtractAsync(http, driveId, itemId, name, ct);
    }
}
