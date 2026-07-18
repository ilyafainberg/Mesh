using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Read-only, owner-only search over the current encrypted Mesh profile.</summary>
public sealed class SearchMeshDataTool(AppState state) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> ValidScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "chats", "topics", "contacts", "widgets", "knowledge", "skills",
        "services", "sources", "tools", "settings"
    };

    public string Name => "search_mesh_data";

    public string Description =>
        "Search the owner's private, locally encrypted Mesh data. Covers direct/group/service chats, "
        + "Me topics, contacts, widgets, knowledge, skills, published services, connected sources, "
        + "tool configuration, and sanitized settings. Read-only; secrets, keys, tokens, and widget HTML are never returned.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                description = "Words or phrase to find. Leave empty to list recent or current items in the selected scope."
            },
            scope = new
            {
                type = "string",
                @enum = ValidScopes.OrderBy(x => x).ToArray(),
                description = "Data area to search. Defaults to all."
            },
            max_results = new
            {
                type = "integer",
                minimum = 1,
                maximum = 50,
                description = "Maximum results to return. Defaults to 20."
            }
        }
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var query = ToolArgs.GetString(args, "query").Trim();
        var scope = ToolArgs.GetString(args, "scope", "all").Trim().ToLowerInvariant();
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 20), 1, 50);

        if (!ValidScopes.Contains(scope))
            return Task.FromResult($"ERROR: Unknown scope '{scope}'. Use: {string.Join(", ", ValidScopes.OrderBy(x => x))}.");

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<SearchResult>(maxResults);

        bool Wants(string name) => scope == "all" || scope == name;
        bool Full() => results.Count >= maxResults;
        bool Matches(params string?[] values)
        {
            if (terms.Length == 0) return true;
            var text = string.Join('\n', values.Where(v => !string.IsNullOrWhiteSpace(v)));
            return terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        void Add(string type, string id, string title, string preview, DateTimeOffset? at = null)
        {
            if (!Full())
                results.Add(new SearchResult(type, id, title, CleanPreview(preview), at));
        }

        var profile = state.Profile;

        if (Wants("topics"))
        {
            foreach (var topic in profile.OwnThreads.OrderByDescending(t => t.Lines.LastOrDefault()?.At ?? t.CreatedAt))
            {
                ct.ThrowIfCancellationRequested();
                var matchingLines = terms.Length == 0
                    ? topic.Lines.Where(l => !l.Internal).TakeLast(1)
                    : topic.Lines.Where(l => !l.Internal && Matches(l.Text, l.Reasoning)).TakeLast(3);
                if (Matches(topic.Title) || matchingLines.Any())
                {
                    var preview = matchingLines.LastOrDefault()?.Text ?? $"{topic.Lines.Count(l => !l.Internal)} messages";
                    Add("me_topic", topic.Id, topic.Title, preview, topic.Lines.LastOrDefault()?.At ?? topic.CreatedAt);
                }
                if (Full()) break;
            }
        }

        if (Wants("chats") && !Full())
        {
            foreach (var chat in profile.Conversations.OrderByDescending(c => c.Lines.LastOrDefault()?.At))
            {
                ct.ThrowIfCancellationRequested();
                var title = chat.IsGroup
                    ? chat.GroupName ?? "Unnamed group"
                    : chat.IsService
                        ? chat.ServiceName ?? chat.Handle
                        : state.DisplayNameFor(chat.Handle);
                var kind = chat.IsGroup ? "group_chat" : chat.IsService ? "service_chat" : "direct_chat";
                var matchingLines = terms.Length == 0
                    ? chat.Lines.TakeLast(1)
                    : chat.Lines.Where(l => Matches(l.Text, l.SenderHandle)).TakeLast(3);

                if (Matches(title, chat.Handle, chat.GroupName, chat.ServiceName) || matchingLines.Any())
                {
                    var preview = matchingLines.LastOrDefault()?.Text ?? $"{chat.Lines.Count} messages";
                    Add(kind, chat.Handle, title, preview, chat.Lines.LastOrDefault()?.At);
                }
                if (Full()) break;
            }
        }

        if (Wants("contacts") && !Full())
        {
            foreach (var contact in profile.Contacts.OrderBy(c => c.DisplayName ?? c.Handle))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(contact.DisplayName, contact.Handle, string.Join(' ', contact.Circles))) continue;
                var flags = new List<string> { contact.Allowed ? "allowed" : "not allowed" };
                if (contact.Muted) flags.Add("muted");
                if (contact.Blocked) flags.Add("blocked");
                Add("contact", contact.Handle, contact.DisplayName ?? contact.Handle,
                    $"@{contact.Handle}; circles: {JoinOrNone(contact.Circles)}; {string.Join(", ", flags)}");
                if (Full()) break;
            }
        }

        if (Wants("widgets") && !Full())
        {
            foreach (var widget in profile.Widgets.OrderByDescending(w => w.ModifiedAt))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(widget.Name, widget.Prompt, widget.Visibility)) continue;
                Add("widget", widget.Id, widget.Name,
                    $"{widget.Prompt}; visibility: {widget.Visibility}", widget.ModifiedAt);
                if (Full()) break;
            }
        }

        if (Wants("knowledge") && !Full())
        {
            foreach (var item in profile.Knowledge.OrderByDescending(k => k.UpdatedAt))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(item.Title, item.Content, item.SourceRef, item.Visibility)) continue;
                Add("knowledge", item.Id, item.Title,
                    $"{item.Content}; source: {item.Source}; visibility: {item.Visibility}", item.UpdatedAt);
                if (Full()) break;
            }
        }

        if (Wants("skills") && !Full())
        {
            foreach (var skill in profile.Skills.OrderBy(s => s.Name))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(skill.Name, skill.Description, skill.Instructions, skill.Visibility)) continue;
                Add("skill", skill.Id, skill.Name,
                    $"{skill.Description}; enabled: {skill.Enabled}; visibility: {skill.Visibility}");
                if (Full()) break;
            }
        }

        if (Wants("services") && !Full())
        {
            foreach (var service in profile.PublishedServices.OrderBy(s => s.Name))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(service.Name, service.Description, service.Category, service.Persona)) continue;
                Add("service", service.Id, service.Name,
                    $"{service.Description}; category: {service.Category}; published: {service.Published}; "
                    + $"tokens: {service.SpentTokens}/{FormatLimit(service.TotalTokenBudget)}; "
                    + $"daily requests per handle: {FormatLimit(service.PerHandleDailyRequestLimit)}",
                    service.CreatedAt);
                if (Full()) break;
            }
        }

        if (Wants("sources") && !Full())
        {
            foreach (var source in profile.Sources.OrderBy(s => s.Provider).ThenBy(s => s.ConnectedAs))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(source.Provider.ToString(), source.ConnectedAs, source.Visibility)) continue;
                Add("source", source.Id, source.Provider.ToString(),
                    $"account: {source.ConnectedAs ?? "not available"}; enabled: {source.Enabled}; "
                    + $"visibility: {source.Visibility}; mail grants: {source.Folders.Count}; drive grants: {source.DrivePaths.Count}");
                if (Full()) break;
            }
        }

        if (Wants("tools") && !Full())
        {
            foreach (var (kind, setting) in profile.LocalTools.OrderBy(x => x.Key.ToString()))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(kind.ToString(), setting.Visibility, setting.ApprovalLevel.ToString())) continue;
                Add("local_tool", kind.ToString(), kind.ToString(),
                    $"enabled: {setting.Enabled}; visibility: {(kind == LocalToolKind.MeshData ? "private" : setting.Visibility)}; approval: {setting.ApprovalLevel}");
                if (Full()) break;
            }

            foreach (var server in profile.CustomMcpServers.OrderBy(s => s.Name))
            {
                if (Full()) break;
                ct.ThrowIfCancellationRequested();
                if (!Matches(server.Name, server.Transport.ToString(), server.Visibility)) continue;
                Add("mcp_server", server.Id, server.Name,
                    $"transport: {server.Transport}; enabled: {server.Enabled}; visibility: {server.Visibility}; approval: {server.ApprovalLevel}");
            }
        }

        if (Wants("settings") && !Full())
        {
            var settings = new[]
            {
                new SearchResult("setting", "identity", "Identity",
                    $"display name: {profile.DisplayName}; handle: @{profile.Handle}; device: {profile.DeviceName}", null),
                new SearchResult("setting", "model", "Model",
                    $"provider: {profile.Model.Provider}; model: {profile.Model.Model}; reasoning: {profile.Model.ReasoningEffort}; configured: {profile.Model.IsConfigured}", null),
                new SearchResult("setting", "relay", "Relay",
                    $"URL: {profile.RelayUrl}", null),
                new SearchResult("setting", "privacy", "Privacy and notifications",
                    $"interactive apps: {profile.AllowInteractiveApps}; do not disturb: {profile.DoNotDisturb}; notification sound: {profile.NotificationSound}", null),
                new SearchResult("setting", "agent", "Agent behavior",
                    $"approval mode: {profile.ApprovalMode}; daily auto-reply budget: {FormatLimit(profile.AgentDailyReplyBudget)}; "
                    + $"used today: {profile.AgentRepliesUsedToday}; answer other devices: {profile.ActAsRemoteAgent}; home device selected: {profile.HomeDeviceId is not null}", null),
                new SearchResult("setting", "circles", "Circles",
                    JoinOrNone(profile.Circles.Select(c => c.Name)), null)
            };
            foreach (var setting in settings.Where(s => Matches(s.Title, s.Preview)))
            {
                Add(setting.Type, setting.Id, setting.Title, setting.Preview, setting.At);
                if (Full()) break;
            }
        }

        var payload = new
        {
            query,
            scope,
            count = results.Count,
            results
        };
        return Task.FromResult(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string CleanPreview(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > 300 ? clean[..300].TrimEnd() + "..." : clean;
    }

    private static string JoinOrNone(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static string FormatLimit(long value) => value == 0 ? "unlimited" : value.ToString();

    private sealed record SearchResult(
        string Type,
        string Id,
        string Title,
        string Preview,
        DateTimeOffset? At);
}
