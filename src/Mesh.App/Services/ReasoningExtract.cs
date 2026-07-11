using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mesh.App.Services;

/// <summary>
/// Provider-agnostic extraction of model reasoning ("thinking") from a reply, plus a friendly label
/// for a tool name. Reasoning shows up in two shapes across providers: inline think tags in the
/// content (DeepSeek-R1 and many local/open reasoning models), or a separate reasoning field in the
/// response JSON (OpenRouter, Anthropic, Gemini). This helper handles the inline-tag case so any model
/// that emits it gets its reasoning surfaced, including auto-routed and hosted models whose capability
/// cannot be known ahead of time. Providers that return a structured reasoning field pass it in
/// directly via <see cref="Combine"/>.
/// </summary>
public static partial class ReasoningExtract
{
    [GeneratedRegex(@"<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkTag();

    // An unterminated leading <think> with no closing tag: treat the whole remainder as reasoning
    // only when it opens at the very start (some models stream reasoning first then the answer).
    [GeneratedRegex(@"^\s*<think>(.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OpenThink();

    /// <summary>
    /// Splits inline think-tag reasoning out of <paramref name="content"/>. Returns the reasoning text
    /// (null when none) and the content with the reasoning removed. Safe on any string.
    /// </summary>
    public static (string? reasoning, string content) FromText(string? content)
    {
        if (string.IsNullOrEmpty(content)) return (null, content ?? "");

        var matches = ThinkTag().Matches(content);
        if (matches.Count > 0)
        {
            var reasoning = string.Join("\n\n", matches.Select(m => m.Groups[1].Value.Trim()))
                .Trim();
            var cleaned = ThinkTag().Replace(content, "").Trim();
            return (string.IsNullOrWhiteSpace(reasoning) ? null : reasoning, cleaned);
        }

        // A think block that opened but never closed (truncated / streaming): everything after the
        // opening tag is reasoning, and there is no answer yet.
        var open = OpenThink().Match(content);
        if (open.Success)
        {
            var reasoning = open.Groups[1].Value.Trim();
            return (string.IsNullOrWhiteSpace(reasoning) ? null : reasoning, "");
        }

        return (null, content);
    }

    /// <summary>
    /// Combines any inline-tag reasoning found in <paramref name="content"/> with a provider-supplied
    /// reasoning field (either may be null). Returns the merged reasoning and the cleaned content.
    /// </summary>
    public static (string? reasoning, string content) Combine(string? content, string? providerReasoning)
    {
        var (inline, cleaned) = FromText(content);
        var parts = new[] { providerReasoning?.Trim(), inline }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        var reasoning = parts.Length == 0 ? null : string.Join("\n\n", parts);
        return (reasoning, cleaned);
    }

    /// <summary>
    /// Wraps a provider-supplied reasoning string back into an inline think tag in front of the
    /// content, so the single downstream extractor (<see cref="FromText"/>) can surface it uniformly
    /// regardless of whether the provider returned reasoning inline or in a separate field.
    /// </summary>
    public static string Wrap(string? reasoning, string content)
        => string.IsNullOrWhiteSpace(reasoning)
            ? content ?? ""
            : "<think>\n" + reasoning.Trim() + "\n</think>\n" + (content ?? "");

    /// <summary>
    /// Reads an OpenAI-style assistant message element, folding any provider reasoning field
    /// (OpenRouter's "reasoning", some providers' "reasoning_content") into an inline think tag so it
    /// gets surfaced as reasoning downstream. Returns the content when no reasoning field is present.
    /// </summary>
    public static string WithOpenAiReasoning(JsonElement msg)
    {
        var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        string? reasoning = null;
        if (msg.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
            reasoning = r.GetString();
        else if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            reasoning = rc.GetString();
        return Wrap(reasoning, content);
    }

    /// <summary>Maps a tool name to a friendly present-tense label for the step trace.</summary>
    public static string Label(string tool)
    {
        var t = (tool ?? "").ToLowerInvariant();
        if (t.Contains("python")) return "Ran Python";
        if (t.Contains("powershell") || t == "pwsh") return "Ran PowerShell";
        if (t == "cmd" || t.Contains("command")) return "Ran a command";
        if (t.Contains("csharp") || t == "cs" || t.Contains("script")) return "Ran C# script";
        if (t.Contains("browser") || t.Contains("playwright") || t.Contains("web")) return "Browsed the web";
        if (t.Contains("search")) return "Searched";
        if (t.Contains("file") || t.Contains("read") || t.Contains("write")) return "Worked with files";
        if (t.Contains("widget")) return "Built a widget";
        if (t.Contains("workiq") || t.Contains("m365") || t.Contains("office")) return "Queried Microsoft 365";
        if (t.Contains("knowledge") || t.Contains("kb")) return "Searched Knowledge";
        // Fall back to a cleaned tool name.
        var pretty = (tool ?? "tool").Replace('_', ' ').Replace('-', ' ').Trim();
        return "Used " + (pretty.Length == 0 ? "a tool" : pretty);
    }
}
