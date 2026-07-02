using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Searches the connected Dropbox for files. Live, read-only.</summary>
public sealed class DropboxSearchTool(ConnectorAuthService auth, IHttpClientFactory httpFactory, string? account, string label, string nameSuffix) : IAgentTool
{
    public string Name => "search_dropbox" + nameSuffix;
    public string Description =>
        $"Search {label}'s Dropbox for files matching a query. Returns file names, paths and a ref you can pass to get_dropbox_file to read a document.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new { query = new { type = "string", description = "Filename or content keywords." } },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (ok, token, error) = await auth.GetTokenAsync(SourceProvider.Dropbox, account, ct);
        if (!ok || token is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var http = httpFactory.CreateClient("connector");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var payload = new { query, options = new { max_results = 10 } };
            using var resp = await http.PostAsJsonAsync("https://api.dropboxapi.com/2/files/search_v2", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Dropbox {(int)resp.StatusCode}: {Trim(body)}";
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            int n = 0;
            foreach (var m in doc.RootElement.GetProperty("matches").EnumerateArray())
            {
                if (!m.TryGetProperty("metadata", out var md) || !md.TryGetProperty("metadata", out var meta)) continue;
                n++;
                var name = meta.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
                var path = meta.TryGetProperty("path_lower", out var pl) ? pl.GetString() : "";
                sb.AppendLine($"{n}. {name}, {path} [ref: {path}]");
            }
            return n == 0 ? "No matching Dropbox files found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Reads the text of a Dropbox file by path (from a search result).</summary>
public sealed class GetDropboxFileTool(ConnectorAuthService auth, IHttpClientFactory httpFactory, DocumentExtractor extractor, string? account, string label, string nameSuffix) : IAgentTool
{
    public string Name => "get_dropbox_file" + nameSuffix;
    public string Description =>
        $"Read the full text of a {label} Dropbox document (PDF/Word/Excel/PowerPoint/text). Pass the 'ref' (path) from a search_dropbox result.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new { @ref = new { type = "string", description = "The file path (ref) from a search_dropbox result." } },
        required = new[] { "ref" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (ok, token, error) = await auth.GetTokenAsync(SourceProvider.Dropbox, account, ct);
        if (!ok || token is null) return "ERROR: " + error;
        var path = ToolArgs.GetString(args, "ref");
        if (string.IsNullOrWhiteSpace(path)) return "ERROR: a file ref (path) is required.";
        var name = Path.GetFileName(path);
        if (!DocumentExtractor.IsSupported(name)) return $"'{name}' isn't a text-extractable type.";

        var http = httpFactory.CreateClient("connector");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            req.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path }));
            req.Content = new ByteArrayContent(Array.Empty<byte>());
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Dropbox {(int)resp.StatusCode} downloading '{name}'.";
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return extractor.Extract(bytes, name);
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }
}

/// <summary>Searches connected Notion workspace pages and databases. Live, read-only.</summary>
public sealed class NotionSearchTool(ConnectorAuthService auth, IHttpClientFactory httpFactory, string? account, string label, string nameSuffix) : IAgentTool
{
    public string Name => "search_notion" + nameSuffix;
    public string Description =>
        $"Search {label}'s Notion workspace for pages and databases matching a query. Returns titles and links.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new { query = new { type = "string", description = "Keywords to search Notion pages/databases." } },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (ok, token, error) = await auth.GetTokenAsync(SourceProvider.Notion, account, ct);
        if (!ok || token is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var http = httpFactory.CreateClient("connector");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
        try
        {
            using var resp = await http.PostAsJsonAsync("https://api.notion.com/v1/search", new { query, page_size = 10 }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Notion {(int)resp.StatusCode}: {Trim(body)}";
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            int n = 0;
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                n++;
                var url = r.TryGetProperty("url", out var u) ? u.GetString() : "";
                sb.AppendLine($"{n}. {NotionTitle(r)}, {url}");
            }
            return n == 0 ? "No matching Notion pages found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string NotionTitle(JsonElement obj)
    {
        // Title lives under properties.*.title[].plain_text (pages) or top-level title[] (databases).
        if (obj.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject())
                if (p.Value.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Array)
                    return JoinPlain(t);
        if (obj.TryGetProperty("title", out var dt) && dt.ValueKind == JsonValueKind.Array)
            return JoinPlain(dt);
        return "(untitled)";
    }

    private static string JoinPlain(JsonElement arr)
    {
        var sb = new StringBuilder();
        foreach (var e in arr.EnumerateArray())
            if (e.TryGetProperty("plain_text", out var pt)) sb.Append(pt.GetString());
        var s = sb.ToString();
        return string.IsNullOrWhiteSpace(s) ? "(untitled)" : s;
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Searches connected Slack messages (user scope search:read). Live, read-only.</summary>
public sealed class SlackSearchTool(ConnectorAuthService auth, IHttpClientFactory httpFactory, string? account, string label, string nameSuffix) : IAgentTool
{
    public string Name => "search_slack" + nameSuffix;
    public string Description =>
        $"Search {label}'s Slack messages for text matching a query. Returns matching messages with sender and channel.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new { query = new { type = "string", description = "Text to search for in Slack messages." } },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (ok, token, error) = await auth.GetTokenAsync(SourceProvider.Slack, account, ct);
        if (!ok || token is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var http = httpFactory.CreateClient("connector");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var url = $"https://slack.com/api/search.messages?query={Uri.EscapeDataString(query)}&count=10";
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Slack {(int)resp.StatusCode}: {Trim(body)}";
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && !okEl.GetBoolean())
                return "ERROR: Slack: " + (doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "request failed");
            if (!doc.RootElement.TryGetProperty("messages", out var msgs) || !msgs.TryGetProperty("matches", out var matches))
                return "No matching Slack messages found.";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var m in matches.EnumerateArray())
            {
                n++;
                var user = m.TryGetProperty("username", out var u) ? u.GetString() : "?";
                var chan = m.TryGetProperty("channel", out var c) && c.TryGetProperty("name", out var cn) ? cn.GetString() : "";
                var text = m.TryGetProperty("text", out var t) ? t.GetString() : "";
                sb.AppendLine($"{n}. #{chan} @{user}: {text}");
            }
            return n == 0 ? "No matching Slack messages found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
