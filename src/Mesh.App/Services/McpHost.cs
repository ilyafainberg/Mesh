using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Mesh.App.Services;

/// <summary>Definition of a bundled MCP tool server Mesh can launch and talk to over stdio.</summary>
public sealed record McpServerDef(
    string Id,
    string DisplayName,
    string Description,
    string Icon,
    Func<string?> ResolveCommand,
    string[] Arguments);

/// <summary>
/// The MCP servers Mesh ships with. Each is launched on demand as a child process and its tools are
/// surfaced to the agent (owner-gated, optionally shared with a circle, like the native local tools).
/// TotalControl is the desktop-control server, bundled next to the Windows client so there is a
/// single source of truth for desktop automation instead of a second copy inside Mesh.
/// </summary>
public static class McpServerRegistry
{
    public static readonly McpServerDef TotalControl = new(
        Id: "totalcontrol",
        DisplayName: "TotalControl (desktop control)",
        Description: "Control the mouse, keyboard, windows and screen capture on this machine. Windows only.",
        Icon: "bi-mouse",
        ResolveCommand: ResolveTotalControl,
        Arguments: Array.Empty<string>());

    public static IReadOnlyList<McpServerDef> All { get; } = new[] { TotalControl };

    public static McpServerDef? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>Builds a launchable server definition from a user-added custom server.</summary>
    public static McpServerDef FromCustom(Mesh.App.Domain.CustomMcpServer c) => new(
        Id: "custom:" + c.Id,
        DisplayName: string.IsNullOrWhiteSpace(c.Name) ? c.Command : c.Name,
        Description: $"Custom MCP server: {c.Command} {string.Join(' ', c.Arguments)}".Trim(),
        Icon: "bi-plugin",
        ResolveCommand: () => string.IsNullOrWhiteSpace(c.Command) ? null : c.Command,
        Arguments: c.Arguments.ToArray());

    /// <summary>Locates the bundled TotalControl.exe shipped under mcp/totalcontrol next to the app.</summary>
    private static string? ResolveTotalControl()
    {
        // Only meaningful on Windows; the server is a Windows desktop-control exe.
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "mcp", "totalcontrol", "TotalControl.exe"),
            Path.Combine(AppContext.BaseDirectory, "TotalControl", "TotalControl.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Manages live connections to the bundled MCP servers. Each server is spawned lazily (stdio) on
/// first use and reused for the app session; its tools are wrapped as <see cref="IAgentTool"/> so the
/// agent can call them uniformly. Connection failures are surfaced as an empty tool list (the agent
/// simply does not get those tools) rather than crashing the turn.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Connection>>> connections = new();

    private sealed record Connection(McpClient? Client, IReadOnlyList<IAgentTool> Tools, string? Error);

    /// <summary>Whether this machine can run the given server (its command resolves to a real file).</summary>
    public bool IsAvailable(McpServerDef def) => def.ResolveCommand() is not null;

    /// <summary>Returns the adapted tools for a server, connecting on first use. Empty on failure.</summary>
    public async Task<IReadOnlyList<IAgentTool>> GetToolsAsync(McpServerDef def, CancellationToken ct = default)
    {
        var lazy = connections.GetOrAdd(def.Id, _ => new Lazy<Task<Connection>>(() => ConnectAsync(def)));
        try
        {
            var conn = await lazy.Value.WaitAsync(ct);
            return conn.Tools;
        }
        catch
        {
            // Drop a failed attempt so a later call can retry (e.g. after the user installs the server).
            connections.TryRemove(def.Id, out _);
            return Array.Empty<IAgentTool>();
        }
    }

    private static async Task<Connection> ConnectAsync(McpServerDef def)
    {
        var command = def.ResolveCommand();
        if (command is null)
            return new Connection(null, Array.Empty<IAgentTool>(), $"{def.DisplayName} is not installed on this machine.");

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = def.DisplayName,
                Command = command,
                Arguments = def.Arguments,
            });
            var client = await McpClient.CreateAsync(transport);
            var mcpTools = await client.ListToolsAsync();
            var adapted = mcpTools
                .Select(t => (IAgentTool)new McpToolAdapter(client, t))
                .ToList();
            return new Connection(client, adapted, null);
        }
        catch (Exception ex)
        {
            return new Connection(null, Array.Empty<IAgentTool>(), ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in connections.Values)
        {
            try
            {
                if (lazy.IsValueCreated)
                {
                    var conn = await lazy.Value;
                    if (conn.Client is IAsyncDisposable ad) await ad.DisposeAsync();
                }
            }
            catch { }
        }
        connections.Clear();
    }
}

/// <summary>Wraps a single MCP server tool as a Mesh <see cref="IAgentTool"/>, forwarding calls over MCP.</summary>
public sealed class McpToolAdapter(McpClient client, McpClientTool tool) : IAgentTool
{
    public string Name => tool.Name;
    public string Description => tool.Description ?? "";
    public object ParametersSchema => tool.JsonSchema;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        try
        {
            var arguments = new Dictionary<string, object?>();
            if (args.ValueKind == JsonValueKind.Object)
                foreach (var prop in args.EnumerateObject())
                    arguments[prop.Name] = ToClrValue(prop.Value);

            var result = await client.CallToolAsync(tool.Name, arguments, cancellationToken: ct);
            return Flatten(result);
        }
        catch (Exception ex) { return $"ERROR calling {tool.Name}: {ex.Message}"; }
    }

    private static object? ToClrValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.GetRawText()
    };

    private static string Flatten(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
                sb.AppendLine(text.Text);
            else if (block is ImageContentBlock image)
                sb.AppendLine($"[image {image.MimeType}, {image.Data.Length} base64 chars]");
        }
        var s = sb.ToString().Trim();
        if (result.IsError == true) return "ERROR: " + (s.Length == 0 ? "tool reported an error." : s);
        return s.Length == 0 ? "(no output)" : s;
    }
}
