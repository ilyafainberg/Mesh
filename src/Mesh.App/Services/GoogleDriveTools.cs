using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>Searches the signed-in user's Google Drive for files. Live, read-only.</summary>
public sealed class SearchDriveTool(GoogleAuthService auth, IHttpClientFactory httpFactory, string? email, string label, string nameSuffix, IReadOnlyList<FolderRef>? folders = null) : IAgentTool
{
    public string Name => "search_drive" + nameSuffix;
    public string Description =>
        folders is { Count: > 0 }
            ? $"Search {label}'s Google Drive within these folders only: {string.Join(", ", folders.Select(f => f.Name))}. Returns file names and a ref you can pass to get_drive_file to read a document."
            : $"Search {label}'s Google Drive for files matching a query. Returns file names, types and a ref you can pass to get_drive_file to read a document. Use when the user asks about their Drive files/documents.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "File name or content keywords to search for." },
            top = new { type = "integer", description = "Max files to return (default 8)." }
        },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (ok, token, error) = await auth.GetTokenAsync(email, ct);
        if (!ok || token is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        var top = Math.Clamp(ToolArgs.GetInt(args, "top", 8), 1, 20);
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var http = httpFactory.CreateClient("google");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var esc = query.Replace("'", "\\'");
        var textClause = $"(fullText contains '{esc}' or name contains '{esc}') and trashed = false";
        // When scoped, restrict to files whose parent is one of the granted folders.
        var q = folders is { Count: > 0 }
            ? $"({string.Join(" or ", folders.Select(f => $"'{f.Id}' in parents"))}) and {textClause}"
            : textClause;
        var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&pageSize={top}&fields=files(id,name,mimeType,modifiedTime,webViewLink)&supportsAllDrives=true&includeItemsFromAllDrives=true";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Drive {(int)resp.StatusCode}: {Trim(body)}";
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
                return "No matching Drive files found.";
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var f in files.EnumerateArray())
            {
                n++;
                var name = f.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
                var id = f.TryGetProperty("id", out var idv) ? idv.GetString() : "";
                var mime = f.TryGetProperty("mimeType", out var mv) ? mv.GetString() : "";
                var when = f.TryGetProperty("modifiedTime", out var d) ? d.GetString() : "";
                sb.AppendLine($"{n}. {name} ({FriendlyType(mime)}, modified {when}) [ref: {id}] [mime: {mime}]");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    internal static string FriendlyType(string? mime) => mime switch
    {
        "application/vnd.google-apps.document" => "Google Doc",
        "application/vnd.google-apps.spreadsheet" => "Google Sheet",
        "application/vnd.google-apps.presentation" => "Google Slides",
        "application/pdf" => "PDF",
        _ => mime?.Split('/').LastOrDefault() ?? "file"
    };

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Reads the text of a Google Drive file by its ref (from a search result).</summary>
public sealed class GetDriveFileTool(GoogleAuthService auth, IHttpClientFactory httpFactory, DocumentExtractor extractor, string? email, string label, string nameSuffix) : IAgentTool
{
    public string Name => "get_drive_file" + nameSuffix;
    public string Description =>
        $"Read the full text of a {label} Google Drive file. Pass the 'ref' from a search_drive result. Handles Google Docs/Sheets/Slides and uploaded PDF/Office/text files.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "The file id (ref) from a search_drive result." },
            mime = new { type = "string", description = "The file's mime type from the search result (optional but recommended)." },
            name = new { type = "string", description = "The file name (optional)." }
        },
        required = new[] { "ref" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (ok, token, error) = await auth.GetTokenAsync(email, ct);
        if (!ok || token is null) return "ERROR: " + error;
        var id = ToolArgs.GetString(args, "ref");
        var mime = ToolArgs.GetString(args, "mime");
        var name = ToolArgs.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(id)) return "ERROR: a file ref is required.";

        var http = httpFactory.CreateClient("google");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            // Native Google formats must be exported; uploaded files are downloaded as-is.
            string url; string asName;
            if (mime.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase))
            {
                var export = mime switch
                {
                    "application/vnd.google-apps.spreadsheet" => "text/csv",
                    _ => "text/plain"
                };
                url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}/export?mimeType={Uri.EscapeDataString(export)}";
                asName = export == "text/csv" ? "export.csv" : "export.txt";
            }
            else
            {
                url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media&supportsAllDrives=true";
                asName = string.IsNullOrWhiteSpace(name) ? MimeToName(mime) : name;
            }

            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return $"ERROR: Drive {(int)resp.StatusCode} reading the file.";
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!DocumentExtractor.IsSupported(asName)) return $"'{asName}' isn't a text-extractable type.";
            return extractor.Extract(bytes, asName);
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string MimeToName(string mime) => mime switch
    {
        "application/pdf" => "file.pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "file.docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "file.xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "file.pptx",
        _ => "file.txt"
    };
}
