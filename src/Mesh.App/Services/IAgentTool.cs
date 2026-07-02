using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>A tool the agent can call. Definition is provider-agnostic JSON schema.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON-schema object describing the tool's parameters.</summary>
    object ParametersSchema { get; }
    Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default);
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
