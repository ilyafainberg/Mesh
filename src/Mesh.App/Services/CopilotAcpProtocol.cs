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

public readonly record struct CopilotAcpUsage(
    long? PromptTokens,
    long? CompletionTokens,
    long? UsedTokens);

public readonly record struct CopilotAcpUsageDelta(long PromptTokens, long CompletionTokens);

public sealed class CopilotAcpUsageAccumulator
{
    private enum UsageMode { None, Explicit, Used }

    private readonly object gate = new();
    private UsageMode mode;
    private long? promptTokens;
    private long? completionTokens;
    private long? usedTokens;

    public CopilotAcpUsageDelta Apply(CopilotAcpUsage usage)
    {
        lock (gate)
        {
            var hasExplicit = usage.PromptTokens.HasValue || usage.CompletionTokens.HasValue;
            if (mode == UsageMode.None)
                mode = hasExplicit ? UsageMode.Explicit : UsageMode.Used;

            if (mode == UsageMode.Explicit)
            {
                if (!hasExplicit
                    || IsDecreasing(usage.PromptTokens, promptTokens)
                    || IsDecreasing(usage.CompletionTokens, completionTokens))
                    return default;

                var promptDelta = PositiveDelta(usage.PromptTokens, promptTokens);
                var completionDelta = PositiveDelta(usage.CompletionTokens, completionTokens);
                if (usage.PromptTokens.HasValue) promptTokens = usage.PromptTokens;
                if (usage.CompletionTokens.HasValue) completionTokens = usage.CompletionTokens;
                return new(promptDelta, completionDelta);
            }

            if (!usage.UsedTokens.HasValue || IsDecreasing(usage.UsedTokens, usedTokens))
                return default;
            var usedDelta = PositiveDelta(usage.UsedTokens, usedTokens);
            usedTokens = usage.UsedTokens;
            return new(usedDelta, 0);
        }
    }

    private static bool IsDecreasing(long? current, long? previous)
        => current.HasValue && previous.HasValue && current.Value < previous.Value;

    private static long PositiveDelta(long? current, long? previous)
        => current.HasValue ? Math.Max(0, current.Value - (previous ?? 0)) : 0;
}

public static class CopilotAcpProtocol
{
    private static readonly string[] MojibakeMarkers =
    [
        "\u00e2\u20ac",
        "\u00e2\u0080",
        "\u00f0\u0178",
        "\u00f0\u009f",
        "\u00c3",
        "\u00c2",
        "\u00ef\u00bf\u00bd"
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding Windows1252;

    private static readonly HashSet<string> Efforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "minimal", "low", "medium", "high", "xhigh", "max"
    };

    private static readonly HashSet<string> PromptTokenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "inputTokens", "input_tokens", "inputTokenCount",
        "prompt", "promptTokens", "prompt_tokens", "promptTokenCount",
        "lastCallInputTokens"
    };

    private static readonly HashSet<string> CompletionTokenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "output", "outputTokens", "output_tokens", "outputTokenCount",
        "completion", "completionTokens", "completion_tokens", "completionTokenCount",
        "lastCallOutputTokens"
    };

    private static readonly HashSet<string> UsedTokenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "used", "usedTokens", "tokensUsed"
    };

    static CopilotAcpProtocol()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(
            1252,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

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

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text) || MojibakeScore(text) == 0)
            return text ?? "";

        var originalScore = MojibakeScore(text);
        var best = text;
        var bestScore = originalScore;
        TryRepair(Windows1252, text, ref best, ref bestScore);
        TryRepair(Encoding.Latin1, text, ref best, ref bestScore);
        TryRepairSegments(Windows1252, text, ref best, ref bestScore);
        TryRepairSegments(Encoding.Latin1, text, ref best, ref bestScore);
        return bestScore < originalScore ? best : text;
    }

    public static bool TryParseUsage(JsonElement element, out CopilotAcpUsage usage)
    {
        long? prompt = null;
        long? completion = null;
        long? used = null;
        FindUsageValues(element, ref prompt, ref completion, ref used);
        usage = new(prompt, completion, used);
        return prompt.HasValue || completion.HasValue || used.HasValue;
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

    private static void TryRepair(
        Encoding sourceEncoding,
        string text,
        ref string best,
        ref int bestScore)
    {
        try
        {
            var candidate = StrictUtf8.GetString(sourceEncoding.GetBytes(text));
            var score = MojibakeScore(candidate);
            if (score >= bestScore) return;
            best = candidate;
            bestScore = score;
        }
        catch (EncoderFallbackException) { }
        catch (DecoderFallbackException) { }
    }

    private static int MojibakeScore(string text)
    {
        var score = 0;
        score += Count(text, "\u00e2\u20ac") * 3;
        score += Count(text, "\u00e2\u0080") * 3;
        score += Count(text, "\u00f0\u0178") * 3;
        score += Count(text, "\u00f0\u009f") * 3;
        score += Count(text, "\u00c3") * 2;
        score += Count(text, "\u00c2") * 2;
        score += Count(text, "\u00ef\u00bf\u00bd") * 3;
        return score;
    }

    private static void TryRepairSegments(
        Encoding sourceEncoding,
        string text,
        ref string best,
        ref int bestScore)
    {
        var output = new StringBuilder(text.Length);
        var changed = false;
        for (var offset = 0; offset < text.Length;)
        {
            if (!MojibakeMarkers.Any(marker => text.AsSpan(offset).StartsWith(marker)))
            {
                output.Append(text[offset++]);
                continue;
            }

            string? replacement = null;
            var consumed = 0;
            var sourceScore = int.MaxValue;
            for (var length = 1; length <= Math.Min(12, text.Length - offset); length++)
            {
                var source = text.Substring(offset, length);
                var scoreBefore = MojibakeScore(source);
                if (scoreBefore == 0) continue;
                try
                {
                    var candidate = StrictUtf8.GetString(sourceEncoding.GetBytes(source));
                    var scoreAfter = MojibakeScore(candidate);
                    if (scoreAfter >= scoreBefore
                        || scoreAfter > sourceScore
                        || (scoreAfter == sourceScore && length <= consumed))
                        continue;
                    replacement = candidate;
                    consumed = length;
                    sourceScore = scoreAfter;
                }
                catch (EncoderFallbackException) { }
                catch (DecoderFallbackException) { }
            }

            if (replacement is null)
            {
                output.Append(text[offset++]);
                continue;
            }
            output.Append(replacement);
            offset += consumed;
            changed = true;
        }

        if (!changed) return;
        var repaired = output.ToString();
        var repairedScore = MojibakeScore(repaired);
        if (repairedScore >= bestScore) return;
        best = repaired;
        bestScore = repairedScore;
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }
        return count;
    }

    private static void FindUsageValues(
        JsonElement element,
        ref long? prompt,
        ref long? completion,
        ref long? used)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                FindUsageValues(item, ref prompt, ref completion, ref used);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            if (!TryReadNonNegativeInt64(property.Value, out var value)) continue;
            if (!prompt.HasValue && PromptTokenNames.Contains(property.Name))
                prompt = value;
            else if (!completion.HasValue && CompletionTokenNames.Contains(property.Name))
                completion = value;
            else if (!used.HasValue && UsedTokenNames.Contains(property.Name))
                used = value;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                FindUsageValues(property.Value, ref prompt, ref completion, ref used);
        }
    }

    private static bool TryReadNonNegativeInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
            return value >= 0;
        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), out value))
            return value >= 0;
        value = 0;
        return false;
    }
}
