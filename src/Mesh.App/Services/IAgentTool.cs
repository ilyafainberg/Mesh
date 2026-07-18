using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>A tool the agent can call. Definition is provider-agnostic JSON schema.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON-schema object describing the tool's parameters.</summary>
    object ParametersSchema { get; }
    ToolOperationKind Classify(JsonElement args) => ToolRiskClassifier.Classify(Name, args);
    Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default);
}

public enum ToolOperationKind
{
    Read,
    Write
}

/// <summary>Conservative classification for approval level 2. Unknown actions require approval.</summary>
public static class ToolRiskClassifier
{
    private static readonly string[] ReadPrefixes =
    [
        "search_", "get_", "read_", "list_", "fetch_", "query_", "show_", "describe_"
    ];

    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search", "geolocation", "ask_workiq"
    };

    public static ToolOperationKind Classify(string toolName, JsonElement args)
    {
        if (toolName is "browser" or "headless_browser")
        {
            var action = ToolArgs.GetString(args, "action").ToLowerInvariant();
            return action is "navigate" or "text" or "html" or "screenshot"
                ? ToolOperationKind.Read
                : ToolOperationKind.Write;
        }

        if (toolName == "file_system")
        {
            var action = ToolArgs.GetString(args, "action").ToLowerInvariant();
            return action is "list" or "read" or "info"
                ? ToolOperationKind.Read
                : ToolOperationKind.Write;
        }

        if (ReadTools.Contains(toolName)
            || ReadPrefixes.Any(prefix => toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return ToolOperationKind.Read;

        return ToolOperationKind.Write;
    }
}

/// <summary>Applies one tool's approval policy before delegating execution.</summary>
public sealed class ApprovalTool(
    IAgentTool inner,
    ToolApprovalLevel approvalLevel,
    ToolApprovalService approvals) : IAgentTool
{
    public string Name => inner.Name;
    public string Description => inner.Description;
    public object ParametersSchema => inner.ParametersSchema;
    public ToolOperationKind Classify(JsonElement args) => inner.Classify(args);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var operation = Classify(args);
        var canRun = approvalLevel == ToolApprovalLevel.AutoApproveAll
            || (approvalLevel == ToolApprovalLevel.ReadOnlyAuto && operation == ToolOperationKind.Read);

        if (!canRun && !await approvals.RequestAsync(Name, Description, operation, args, ct))
            return $"ERROR: Permission to run {Name} was denied.";

        return await inner.ExecuteAsync(args, ct);
    }
}

/// <summary>Helper for reading tool arguments defensively.</summary>
public static class ToolArgs
{
    public static string GetString(JsonElement args, string name, string fallback = "")
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    public static int GetInt(JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i : fallback;
}
