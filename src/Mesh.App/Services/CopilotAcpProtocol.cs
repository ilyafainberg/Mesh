using System.Text;
using System.Text.Json;

namespace Mesh.App.Services;

public sealed record CopilotModelOption(
    string Id,
    string Name,
    string? Description,
    string? Usage,
    string? PriceCategory,
    bool Enabled);

public sealed record CopilotAcpConfig(
    string Executable,
    string Model,
    string Effort,
    string ToolFilter = "");

public static class CopilotAcpProtocol
{
    private static readonly HashSet<string> Efforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "minimal", "low", "medium", "high", "xhigh", "max"
    };

    public static IReadOnlyList<string> BuildServerArguments(
        string? model,
        string? effort,
        string? toolFilter = null)
    {
        var args = new List<string> { "--acp", "--stdio", $"--available-tools={toolFilter?.Trim() ?? ""}" };
        var normalizedModel = NormalizeModel(model);
        if (normalizedModel != "auto")
        {
            args.Add("--model");
            args.Add(normalizedModel);
        }

        var normalizedEffort = NormalizeEffort(effort);
        if (normalizedEffort != "auto")
        {
            args.Add("--effort");
            args.Add(normalizedEffort);
        }
        return args;
    }

    public static string NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? "auto" : model.Trim();

    public static string NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort) || effort.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "auto";
        var normalized = effort.Trim().ToLowerInvariant();
        if (!Efforts.Contains(normalized))
            throw new ArgumentException($"Unsupported Copilot effort '{effort}'.", nameof(effort));
        return normalized;
    }

    public static string ComposePrompt(
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> history,
        bool toolsAvailable = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SYSTEM INSTRUCTIONS:");
        sb.AppendLine(systemPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine("CONVERSATION:");
        foreach (var (role, text) in history)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            sb.Append(role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "ASSISTANT: " : "USER: ");
            sb.AppendLine(text.Trim());
        }
        sb.AppendLine();
        sb.Append(toolsAvailable
            ? "Use only tools supplied by Mesh. Their permission decisions are authoritative. Return the assistant answer."
            : "Do not use tools or access files. Return only the assistant answer.");
        return sb.ToString();
    }

    public static IReadOnlyList<CopilotModelOption> ParseModels(JsonElement sessionResult)
    {
        var models = new List<CopilotModelOption>
        {
            new("auto", "Automatic", "Let Copilot choose the model", null, null, true)
        };
        if (!sessionResult.TryGetProperty("models", out var modelState)
            || !modelState.TryGetProperty("availableModels", out var available)
            || available.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var item in available.EnumerateArray())
        {
            var id = Text(item, "modelId");
            if (string.IsNullOrWhiteSpace(id)
                || models.Any(existing => existing.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                continue;
            var name = Text(item, "name") ?? id;
            string? usage = null;
            string? price = null;
            var enabled = true;
            if (item.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                usage = Text(meta, "copilotUsage");
                price = Text(meta, "copilotPriceCategory");
                var enablement = Text(meta, "copilotEnablement");
                enabled = string.IsNullOrWhiteSpace(enablement)
                    || enablement.Equals("enabled", StringComparison.OrdinalIgnoreCase);
            }
            models.Add(new(id, name, Text(item, "description"), usage, price, enabled));
        }
        return models;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
