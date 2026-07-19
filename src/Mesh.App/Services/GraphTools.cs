using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Base for Microsoft Graph tools, acquires the account's token on demand.</summary>
public abstract class GraphTool(MsalAuthService auth, IHttpClientFactory httpFactory, string? accountId, string[] scopes) : IAgentTool
{
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

    protected static string Strip(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > 600 ? text[..600] + "…" : text;
    }
}

/// <summary>A specific mail folder / label a search tool is scoped to.</summary>
public sealed record FolderRef(string Id, string Name);

/// <summary>Searches the signed-in user's recent email for a query. Live, scoped, read-only.</summary>
public sealed class SearchEmailTool(MsalAuthService auth, IHttpClientFactory httpFactory, string? accountId, string[] scopes, string label, string nameSuffix, IReadOnlyList<FolderRef>? folders = null)
    : GraphTool(auth, httpFactory, accountId, scopes)
{
    public override string Name => "search_email" + nameSuffix;
    public override string Description =>
        folders is { Count: > 0 }
            ? $"Search {label}'s email in these folders only: {string.Join(", ", folders.Select(f => f.Name))}. Returns subject, sender, date and a preview. Use only when the user asks about their email."
            : $"Search {label}'s Outlook/Exchange email for messages matching a query. Returns subject, sender, date and a preview. Use only when the user asks about their email.";
    public override object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "What to search for (keywords, sender, topic)." },
            top = new { type = "integer", description = "Max messages to return (default 5)." }
        },
        required = new[] { "query" }
    };

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (http, error) = await ClientAsync(ct);
        if (http is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        var top = Math.Clamp(ToolArgs.GetInt(args, "top", 5), 1, 15);
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        // Whole-mailbox search, or per-folder search when scoped to specific folders.
        var searchQuery = Uri.EscapeDataString(query);
        var urls = folders is { Count: > 0 }
            ? folders.Select(f => (f.Name, Url: $"https://graph.microsoft.com/v1.0/me/mailFolders/{Uri.EscapeDataString(f.Id)}/messages?$top={top}&$select=subject,from,receivedDateTime,bodyPreview&$search=\"{searchQuery}\"")).ToList()
            : new List<(string Name, string Url)> { ("", $"https://graph.microsoft.com/v1.0/me/messages?$top={top}&$select=subject,from,receivedDateTime,bodyPreview&$search=\"{searchQuery}\"") };

        try
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var (folderName, url) in urls)
            {
                if (n >= top) break;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("ConsistencyLevel", "eventual");
                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return $"ERROR: Graph {(int)resp.StatusCode}: {Trim(body)}";
                using var doc = JsonDocument.Parse(body);
                foreach (var m in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (n >= top) break;
                    n++;
                    var subject = m.TryGetProperty("subject", out var s) ? s.GetString() : "(no subject)";
                    var from = m.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object
                        ? f.GetProperty("emailAddress").GetProperty("name").GetString() : "?";
                    var when = m.TryGetProperty("receivedDateTime", out var d) ? d.GetString() : "";
                    var preview = m.TryGetProperty("bodyPreview", out var b) ? b.GetString() : "";
                    var folderTag = string.IsNullOrEmpty(folderName) ? "" : $" [{folderName}]";
                    sb.AppendLine($"{n}. \"{subject}\", from {from} ({when}){folderTag}\n   {preview}");
                }
            }
            return n == 0 ? "No matching emails found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Searches the signed-in user's Teams chats/messages for a query. Live, scoped, read-only.</summary>
public sealed class SearchTeamsTool(MsalAuthService auth, IHttpClientFactory httpFactory, string? accountId, string[] scopes, string label, string nameSuffix)
    : GraphTool(auth, httpFactory, accountId, scopes)
{
    public override string Name => "search_teams" + nameSuffix;
    public override string Description =>
        $"Search {label}'s Microsoft Teams messages for text matching a query. Returns matching messages with sender and a snippet. Use only when the user asks about their Teams chats.";
    public override object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Text to search for in Teams messages." }
        },
        required = new[] { "query" }
    };

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var (http, error) = await ClientAsync(ct);
        if (http is null) return "ERROR: " + error;
        var query = ToolArgs.GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        // Microsoft Search API over chatMessage entities.
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    entityTypes = new[] { "chatMessage" },
                    query = new { queryString = query },
                    from = 0,
                    size = 8
                }
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
                    n++;
                    var summary = hit.TryGetProperty("summary", out var su) ? su.GetString() : "";
                    sb.AppendLine($"{n}. {Strip(summary)}");
                }
            }
            return n == 0 ? "No matching Teams messages found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Searches the signed-in user's Gmail for a query. Live, scoped, read-only.</summary>
public sealed class GmailSearchTool(GoogleAuthService auth, IHttpClientFactory httpFactory, string? email, string label, string nameSuffix, IReadOnlyList<FolderRef>? labels = null) : IAgentTool
{
    public string Name => "search_gmail" + nameSuffix;
    public string Description =>
        labels is { Count: > 0 }
            ? $"Search {label}'s Gmail in these labels only: {string.Join(", ", labels.Select(l => l.Name))}. Returns subject, sender, date and a snippet. Use only when the user asks about their Gmail."
            : $"Search {label}'s Gmail for messages matching a query (Gmail search syntax works, e.g. from:alice invoice). Returns subject, sender, date and a snippet. Use only when the user asks about their Gmail.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Gmail search query (keywords, from:, subject:, etc.)." },
            top = new { type = "integer", description = "Max messages to return (default 5)." }
        },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        if (auth is null) return "ERROR: Gmail is not configured.";
        var (ok, token, error) = await auth.GetTokenAsync(email, ct);
        if (!ok || token is null) return "ERROR: " + error;

        var query = ToolArgs.GetString(args, "query");
        var top = Math.Clamp(ToolArgs.GetInt(args, "top", 5), 1, 15);
        if (string.IsNullOrWhiteSpace(query)) return "ERROR: query is required.";

        var http = httpFactory.CreateClient("google");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            // Restrict to specific labels when scoped, else search the whole mailbox.
            var labelParam = labels is { Count: > 0 }
                ? string.Concat(labels.Select(l => $"&labelIds={Uri.EscapeDataString(l.Id)}"))
                : "";
            var listUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={top}&q={Uri.EscapeDataString(query)}{labelParam}";
            using var listResp = await http.GetAsync(listUrl, ct);
            var listBody = await listResp.Content.ReadAsStringAsync(ct);
            if (!listResp.IsSuccessStatusCode) return $"ERROR: Gmail {(int)listResp.StatusCode}: {Trim(listBody)}";
            using var listDoc = JsonDocument.Parse(listBody);
            if (!listDoc.RootElement.TryGetProperty("messages", out var msgs) || msgs.GetArrayLength() == 0)
                return "No matching Gmail messages found.";

            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var m in msgs.EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                var getUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date";
                using var getResp = await http.GetAsync(getUrl, ct);
                if (!getResp.IsSuccessStatusCode) continue;
                using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync(ct));
                var payload = getDoc.RootElement.GetProperty("payload");
                string H(string name) => payload.GetProperty("headers").EnumerateArray()
                    .FirstOrDefault(h => string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    is { ValueKind: JsonValueKind.Object } hv ? hv.GetProperty("value").GetString() ?? "" : "";
                var snippet = getDoc.RootElement.TryGetProperty("snippet", out var s) ? s.GetString() : "";
                n++;
                sb.AppendLine($"{n}. \"{H("Subject")}\", from {H("From")} ({H("Date")})\n   {snippet}");
            }
            return n == 0 ? "No matching Gmail messages found." : sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Builds the set of tools available from the user's connected sources.</summary>
public sealed class ToolRegistry(MsalAuthService auth, GoogleAuthService google, ConnectorAuthService connectors, IHttpClientFactory httpFactory, DocumentExtractor extractor, LocalFileRegistry localFiles, McpHost mcpHost, AgentMedia media, ToolApprovalService approvals, AppState state, LocationPermissionService locationPermission)
{
    private IAgentTool GuardReadTool(IAgentTool tool)
        => new ApprovalTool(tool, ToolApprovalLevel.ReadOnlyAuto, approvals);

    /// <summary>Whether a provider exposes an email/message search tool.</summary>
    private static bool HasEmail(SourceProvider p) => p is
        SourceProvider.MicrosoftGraph or SourceProvider.MicrosoftPersonal or SourceProvider.Google;

    /// <summary>All tools for a source (owner path, no folder scoping).</summary>
    public IReadOnlyList<IAgentTool> ToolsFor(ConnectedSource src)
    {
        var label = LabelFor(src);
        var suffix = NameSuffix(src);
        var tools = new List<IAgentTool>();
        if (HasEmail(src.Provider)) tools.Add(EmailTool(src, label, suffix, null));
        if (src.Provider == SourceProvider.MicrosoftGraph)
            tools.Add(GuardReadTool(new SearchTeamsTool(auth, httpFactory, src.AccountId, MsalAuthService.WorkScopes, label, suffix)));
        tools.AddRange(FileTools(src, label, suffix));
        return tools;
    }

    /// <summary>Cloud-file tools (OneDrive/SharePoint/Drive) for a source, when it offers them.</summary>
    /// <param name="driveFolders">When set, OneDrive/Drive search is scoped to just these folders (guest path).</param>
    private IReadOnlyList<IAgentTool> FileTools(ConnectedSource src, string label, string suffix, IReadOnlyList<FolderRef>? driveFolders = null)
    {
        var list = new List<IAgentTool>();
        if (src.Provider is SourceProvider.MicrosoftGraph or SourceProvider.MicrosoftPersonal)
        {
            var scopes = src.Provider == SourceProvider.MicrosoftGraph ? MsalAuthService.WorkScopes : MsalAuthService.PersonalScopes;
            list.Add(GuardReadTool(new SearchOneDriveTool(auth, httpFactory, extractor, src.AccountId, scopes, label, suffix, driveFolders)));
            // SharePoint is broad; only offer it with full-source access (not folder-scoped grants).
            if (src.Provider == SourceProvider.MicrosoftGraph && driveFolders is null)
                list.Add(GuardReadTool(new SearchSharePointTool(auth, httpFactory, extractor, src.AccountId, scopes, label, suffix)));
            list.Add(GuardReadTool(new GetGraphFileTool(auth, httpFactory, extractor, src.AccountId, scopes, label, suffix)));
        }
        else if (src.Provider == SourceProvider.Google)
        {
            var acct = src.AccountId ?? src.ConnectedAs;
            list.Add(GuardReadTool(new SearchDriveTool(google, httpFactory, acct, label, suffix, driveFolders)));
            list.Add(GuardReadTool(new GetDriveFileTool(google, httpFactory, extractor, acct, label, suffix)));
        }
        else if (src.Provider == SourceProvider.Dropbox)
        {
            var acct = src.AccountId ?? src.ConnectedAs;
            list.Add(GuardReadTool(new DropboxSearchTool(connectors, httpFactory, acct, label, suffix)));
            list.Add(GuardReadTool(new GetDropboxFileTool(connectors, httpFactory, extractor, acct, label, suffix)));
        }
        else if (src.Provider == SourceProvider.Notion)
        {
            list.Add(GuardReadTool(new NotionSearchTool(connectors, httpFactory, src.AccountId ?? src.ConnectedAs, label, suffix)));
        }
        else if (src.Provider == SourceProvider.Slack)
        {
            list.Add(GuardReadTool(new SlackSearchTool(connectors, httpFactory, src.AccountId ?? src.ConnectedAs, label, suffix)));
        }
        return list;
    }

    /// <summary>Owner path: every enabled source, full access, plus local-file reading and enabled local tools.</summary>
    public IReadOnlyList<IAgentTool> OwnerTools(
        IEnumerable<ConnectedSource> sources,
        IReadOnlyDictionary<LocalToolKind, LocalToolSetting>? localTools = null)
    {
        var tools = new List<IAgentTool>();
        // The owner can attach local files in their private chat; let the agent open them by path.
        tools.Add(GuardReadTool(new ReadLocalFileTool(localFiles, extractor)));
        if (localTools is not null)
            tools.AddRange(LocalTools(localTools, owner: true, circles: null));
        foreach (var src in sources)
            if (src.Enabled) tools.AddRange(ToolsFor(src));
        return tools;
    }

    /// <summary>
    /// Guest path: a source's email tool is offered whole-mailbox when the source
    /// itself is visible to the guest's circles, else scoped to just the folders
    /// shared with those circles. Teams + files are only offered when the whole source is visible.
    /// </summary>
    public IReadOnlyList<IAgentTool> GuestTools(
        IEnumerable<ConnectedSource> sources,
        List<string> circles,
        IReadOnlyDictionary<LocalToolKind, LocalToolSetting>? localTools = null)
    {
        static bool Vis(string v, List<string> cs) =>
            v == "public" || (v.StartsWith("shared:") && cs.Contains(v["shared:".Length..]));

        var tools = new List<IAgentTool>();
        // Local tools the owner has explicitly shared with one of this guest's circles.
        if (localTools is not null)
            tools.AddRange(LocalTools(localTools, owner: false, circles: circles));
        foreach (var src in sources)
        {
            if (!src.Enabled) continue;
            var label = LabelFor(src);
            var suffix = NameSuffix(src);
            var sourceVisible = Vis(src.Visibility, circles);

            if (sourceVisible)
            {
                if (HasEmail(src.Provider)) tools.Add(EmailTool(src, label, suffix, null));
                if (src.Provider == SourceProvider.MicrosoftGraph)
                    tools.Add(GuardReadTool(new SearchTeamsTool(auth, httpFactory, src.AccountId, MsalAuthService.WorkScopes, label, suffix)));
                tools.AddRange(FileTools(src, label, suffix));
                continue;
            }

            // Not fully visible: offer per-grant scoped tools (mail folders and/or drive paths).
            var visibleFolders = src.Folders
                .Where(f => Vis(f.Visibility, circles))
                .Select(f => new FolderRef(f.Id, f.Name))
                .ToList();
            if (HasEmail(src.Provider) && visibleFolders.Count > 0)
                tools.Add(EmailTool(src, label, suffix, visibleFolders));

            var visiblePaths = src.DrivePaths
                .Where(f => Vis(f.Visibility, circles))
                .Select(f => new FolderRef(f.Id, f.Name))
                .ToList();
            if (visiblePaths.Count > 0)
                tools.AddRange(FileTools(src, label, suffix, visiblePaths));
        }
        return tools;
    }

    private IAgentTool EmailTool(ConnectedSource src, string label, string suffix, IReadOnlyList<FolderRef>? folders)
        => src.Provider == SourceProvider.Google
            ? GuardReadTool(new GmailSearchTool(google, httpFactory, src.AccountId ?? src.ConnectedAs, label, suffix, folders))
            : GuardReadTool(new SearchEmailTool(auth, httpFactory, src.AccountId,
                src.Provider == SourceProvider.MicrosoftGraph ? MsalAuthService.WorkScopes : MsalAuthService.PersonalScopes,
                label, suffix, folders));

    /// <summary>
    /// Builds the enabled local-machine tools (scripts, browser, desktop, files). For the OWNER,
    /// every enabled tool is included. For a GUEST, only tools whose visibility is shared with one of
    /// the guest's circles (or public) are included. Off-by-default: absent settings mean disabled.
    /// </summary>
    private IReadOnlyList<IAgentTool> LocalTools(
        IReadOnlyDictionary<LocalToolKind, LocalToolSetting> settings,
        bool owner,
        List<string>? circles)
    {
        static bool Vis(string v, List<string> cs) =>
            v == "public" || (v.StartsWith("shared:") && cs.Contains(v["shared:".Length..]));

        var tools = new List<IAgentTool>();
        foreach (var (kind, setting) in settings)
        {
            if (setting is null || !setting.Enabled) continue;
            // Mesh data contains the owner's private chats and configuration. It is never exposed
            // through a guest agent, even if an old profile contains a non-private visibility value.
            if (!owner && kind == LocalToolKind.MeshData) continue;
            // Desktop-only tools (scripts, browsers) cannot run on a phone, so never offer them to the
            // agent on mobile. This gates both the owner and guest paths since both flow through here.
            if (PlatformCaps.IsMobile && kind.IsDesktopOnly()) continue;
            if (!owner)
            {
                // Guest: only if explicitly shared with one of their circles (or public).
                if (circles is null || !Vis(setting.Visibility, circles)) continue;
            }
            var tool = MakeLocalTool(kind);
            if (tool is not null)
                tools.Add(new ApprovalTool(tool, setting.ApprovalLevel, approvals));
        }
        return tools;
    }

    private IAgentTool? MakeLocalTool(LocalToolKind kind) => kind switch
    {
        LocalToolKind.PowerShell => new RunPowerShellTool(),
        LocalToolKind.Cmd => new RunCmdTool(),
        LocalToolKind.Python => new RunPythonTool(),
        LocalToolKind.CSharpScript => new RunCSharpScriptTool(),
        LocalToolKind.Browser => new BrowserTool(media),
        LocalToolKind.HeadlessBrowser => new HeadlessBrowserTool(media),
        LocalToolKind.WebSearch => new WebSearchTool(),
        LocalToolKind.Geolocation => new GeoLocationTool(locationPermission),
        LocalToolKind.FileSystem => new FileSystemTool(extractor),
        LocalToolKind.WorkIq => new AskWorkIqTool(),
        LocalToolKind.MeshData => new SearchMeshDataTool(state),
        _ => null
    };

    /// <summary>
    /// Tools from the bundled MCP servers (e.g. TotalControl), gated the same way as local tools:
    /// the owner gets every enabled server's tools; a guest gets a server's tools only when the owner
    /// shared that server with one of the guest's circles. Connects to enabled servers on demand.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> McpToolsAsync(
        IReadOnlyDictionary<string, LocalToolSetting>? servers,
        IReadOnlyList<CustomMcpServer>? customServers,
        bool owner,
        List<string>? circles,
        CancellationToken ct = default)
    {
        static bool Vis(string v, List<string> cs) =>
            v == "public" || (v.StartsWith("shared:") && cs.Contains(v["shared:".Length..]));

        var tools = new List<IAgentTool>();

        // Bundled servers (e.g. TotalControl), governed by per-server grants.
        if (servers is not null)
            foreach (var (id, setting) in servers)
            {
                if (setting is null || !setting.Enabled) continue;
                if (!owner && (circles is null || !Vis(setting.Visibility, circles))) continue;
                var def = McpServerRegistry.Find(id);
                if (def is null || !mcpHost.IsAvailable(def)) continue;
                tools.AddRange((await mcpHost.GetToolsAsync(def, ct))
                    .Select(tool => new ApprovalTool(tool, setting.ApprovalLevel, approvals)));
            }

        // User-added custom servers.
        if (customServers is not null)
            foreach (var c in customServers)
            {
                if (!c.Enabled) continue;
                if (!owner && (circles is null || !Vis(c.Visibility, circles))) continue;
                var def = McpServerRegistry.FromCustom(c);
                if (!mcpHost.IsAvailable(def)) continue;
                tools.AddRange((await mcpHost.GetToolsAsync(def, ct))
                    .Select(tool => new ApprovalTool(tool, c.ApprovalLevel, approvals)));
            }

        return tools;
    }

    private static string LabelFor(ConnectedSource src)
        => string.IsNullOrWhiteSpace(src.ConnectedAs) ? DisplayName(src.Provider) : src.ConnectedAs!;

    /// <summary>A short, tool-name-safe discriminator so multiple accounts don't collide.</summary>
    private static string NameSuffix(ConnectedSource src)
    {
        var basis = src.ConnectedAs ?? src.Id;
        var local = basis.Split('@')[0];
        var clean = new string(local.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return clean.Length == 0 ? "" : "_" + clean[..Math.Min(clean.Length, 12)];
    }

    /// <summary>True when this provider supports per-folder / per-label mail grants.</summary>
    public static bool SupportsFolders(SourceProvider p) => p is
        SourceProvider.MicrosoftGraph or SourceProvider.MicrosoftPersonal or SourceProvider.Google;

    /// <summary>True when this provider supports per-path drive/file grants (OneDrive / Google Drive).</summary>
    public static bool SupportsDrivePaths(SourceProvider p) => p is
        SourceProvider.MicrosoftGraph or SourceProvider.MicrosoftPersonal or SourceProvider.Google;

    public static string DisplayName(SourceProvider p) => p switch
    {
        SourceProvider.MicrosoftGraph => "Microsoft 365 (email + Teams + files)",
        SourceProvider.MicrosoftPersonal => "Microsoft account (Outlook.com + OneDrive)",
        SourceProvider.Google => "Google (Gmail + Drive)",
        SourceProvider.Dropbox => "Dropbox",
        SourceProvider.Notion => "Notion",
        SourceProvider.Slack => "Slack",
        _ => p.ToString()
    };
}
