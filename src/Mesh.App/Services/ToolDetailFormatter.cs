using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mesh.App.Services;

internal enum ToolDetailDirection { Input, Output }

internal enum ToolDetailTone { Neutral, Success, Warning, Error }

internal sealed record ToolDetailSection(
    string? Label,
    string Text,
    string Language,
    ToolDetailTone Tone = ToolDetailTone.Neutral);

internal sealed record ToolDetailDocument(
    string Raw,
    string RawLanguage,
    IReadOnlyList<ToolDetailSection> Sections,
    bool HasFormattedView);

/// <summary>Turns raw tool traces into readable, language-aware sections without losing the original text.</summary>
internal static class ToolDetailFormatter
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly Regex LocalExitCode = new(
        @"^\s*exit code:\s*(-?\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AcpExitCode = new(
        @"^\s*Process exited with code\s+(-?\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlStart = new(
        @"^\s*(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|WITH)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ToolDetailDocument Format(
        string? toolName,
        ToolDetailDirection direction,
        string? content)
    {
        var raw = content ?? "";
        var tool = NormalizeToolName(toolName);
        var rawLanguage = DetectLanguage(tool, direction, raw);

        if (direction == ToolDetailDirection.Input
            && TryFormatStructuredInput(tool, raw, out var inputSections))
            return new ToolDetailDocument(raw, rawLanguage, inputSections, HasFormattedView: true);

        var display = direction == ToolDetailDirection.Output
            ? UnwrapAcpPayload(raw)
            : raw;

        if (direction == ToolDetailDirection.Output
            && TryFormatProcessOutput(display, out var processSections))
            return new ToolDetailDocument(raw, rawLanguage, processSections, HasFormattedView: true);

        if (TryUnwrapFence(display, out var fenced, out var fenceLanguage))
            return new ToolDetailDocument(
                raw,
                rawLanguage,
                [new ToolDetailSection(null, fenced, fenceLanguage)],
                HasFormattedView: true);

        if (TryPrettyJson(display, out var prettyJson))
            return new ToolDetailDocument(
                raw,
                rawLanguage,
                [new ToolDetailSection(null, prettyJson, "json")],
                HasFormattedView: !string.Equals(raw, prettyJson, StringComparison.Ordinal));

        var language = DetectLanguage(tool, direction, display);
        return new ToolDetailDocument(
            raw,
            rawLanguage,
            [new ToolDetailSection(null, display, language)],
            HasFormattedView: !string.Equals(raw, display, StringComparison.Ordinal));
    }

    internal static string NormalizeToolName(string? toolName)
    {
        var name = toolName?.Trim() ?? "";
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        if (name.StartsWith("mesh-", StringComparison.OrdinalIgnoreCase))
            name = name["mesh-".Length..];
        else if (name.StartsWith("mesh_", StringComparison.OrdinalIgnoreCase))
            name = name["mesh_".Length..];
        return name.Replace('-', '_').ToLowerInvariant();
    }

    private static bool TryFormatStructuredInput(
        string tool,
        string raw,
        out IReadOnlyList<ToolDetailSection> sections)
    {
        sections = Array.Empty<ToolDetailSection>();
        if (!TryParseJsonObject(raw, out var document)) return false;
        using (document)
        {
            var root = document.RootElement;
            if (TryGetCodeField(tool, root, out var codeProperty, out var language))
            {
                sections = BuildInputSections(root, codeProperty, language);
                return true;
            }

            if (TryGetFileContentField(root, out var contentProperty, out language))
            {
                sections = BuildInputSections(root, contentProperty, language);
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<ToolDetailSection> BuildInputSections(
        JsonElement root,
        JsonProperty contentProperty,
        string language)
    {
        var sections = new List<ToolDetailSection>
        {
            new(contentProperty.Name, NormalizeNewlines(contentProperty.Value.GetString() ?? ""), language)
        };
        var options = PrettyJsonWithout(root, contentProperty.Name);
        if (!string.IsNullOrWhiteSpace(options))
            sections.Add(new ToolDetailSection("options", options, "json"));
        return sections;
    }

    private static bool TryGetCodeField(
        string tool,
        JsonElement root,
        out JsonProperty property,
        out string language)
    {
        string? field = null;
        language = "plaintext";
        switch (tool)
        {
            case "run_powershell":
                field = "script";
                language = "powershell";
                break;
            case "run_cmd":
                field = "command";
                language = "dos";
                break;
            case "run_python":
                field = "code";
                language = "python";
                break;
            case "run_csharp_script":
                field = "code";
                language = "csharp";
                break;
            default:
                if (tool.Contains("browser", StringComparison.Ordinal)
                    && TryGetStringProperty(root, ["script", "expression"], out property))
                {
                    language = "javascript";
                    return true;
                }
                property = default;
                return false;
        }

        return TryGetStringProperty(root, [field], out property);
    }

    private static bool TryGetFileContentField(
        JsonElement root,
        out JsonProperty contentProperty,
        out string language)
    {
        contentProperty = default;
        language = "plaintext";
        if (!TryGetStringProperty(root, ["path", "file_path", "file", "name"], out var pathProperty))
            return false;
        language = LanguageFromPath(pathProperty.Value.GetString());
        return language != "plaintext"
            && TryGetStringProperty(root, ["content", "text", "source"], out contentProperty);
    }

    private static bool TryGetStringProperty(
        JsonElement root,
        IReadOnlyList<string> names,
        out JsonProperty property)
    {
        foreach (var candidate in root.EnumerateObject())
        {
            if (candidate.Value.ValueKind != JsonValueKind.String) continue;
            if (names.Any(name => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                property = candidate;
                return true;
            }
        }
        property = default;
        return false;
    }

    private static string? PrettyJsonWithout(JsonElement root, string excludedProperty)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            var count = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, excludedProperty, StringComparison.Ordinal)) continue;
                property.WriteTo(writer);
                count++;
            }
            writer.WriteEndObject();
            writer.Flush();
            if (count == 0) return null;
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string UnwrapAcpPayload(string raw)
    {
        var current = raw;
        for (var depth = 0; depth < 3; depth++)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(current); }
            catch (JsonException) { break; }

            using (document)
            {
                string? next = null;
                if (document.RootElement.ValueKind == JsonValueKind.String)
                    next = document.RootElement.GetString();
                else if (TryExtractAcpText(document.RootElement, out var extracted))
                    next = extracted;

                if (string.IsNullOrEmpty(next)
                    || string.Equals(next, current, StringComparison.Ordinal))
                    break;
                current = next;
            }
        }
        return current;
    }

    private static bool TryExtractAcpText(JsonElement root, out string text)
    {
        text = "";
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("content", out var content))
            return false;

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? "";
            return true;
        }

        var parts = new List<string>();
        CollectTypedText(content, parts);
        if (parts.Count == 0) return false;
        text = string.Join("\n", parts);
        return true;
    }

    private static void CollectTypedText(JsonElement element, List<string> parts)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            parts.Add(element.GetString() ?? "");
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTypedText(item, parts);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        if (element.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
            && element.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            parts.Add(text.GetString() ?? "");
            return;
        }
        if (element.TryGetProperty("content", out var nested))
            CollectTypedText(nested, parts);
    }

    private static bool TryFormatProcessOutput(
        string value,
        out IReadOnlyList<ToolDetailSection> sections)
    {
        sections = Array.Empty<ToolDetailSection>();
        var lines = NormalizeNewlines(value).Split('\n');
        if (TryFindExitCode(lines, LocalExitCode, out var exitIndex, out var exitCode))
        {
            sections = FormatLocalProcess(lines, exitIndex, exitCode);
            return true;
        }
        if (TryFindExitCode(lines, AcpExitCode, out exitIndex, out exitCode))
        {
            sections = FormatAcpProcess(lines, exitIndex, exitCode);
            return true;
        }
        return false;
    }

    private static bool TryFindExitCode(
        IReadOnlyList<string> lines,
        Regex pattern,
        out int lineIndex,
        out int exitCode)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var match = pattern.Match(lines[i]);
            if (!match.Success) continue;
            lineIndex = i;
            exitCode = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return true;
        }
        lineIndex = -1;
        exitCode = 0;
        return false;
    }

    private static IReadOnlyList<ToolDetailSection> FormatLocalProcess(
        string[] lines,
        int exitIndex,
        int exitCode)
    {
        var timedOut = lines.Take(exitIndex).Any(line =>
            string.Equals(line.Trim(), "[timed out]", StringComparison.OrdinalIgnoreCase));
        var sections = new List<ToolDetailSection>
        {
            StatusSection(exitCode, timedOut)
        };

        var metadata = Slice(lines, 0, exitIndex, line =>
            !string.Equals(line.Trim(), "[timed out]", StringComparison.OrdinalIgnoreCase));
        AddTextSection(sections, "run details", metadata);

        var stdoutIndex = FindLabel(lines, exitIndex + 1, "stdout:");
        var stderrIndex = FindLabel(lines, exitIndex + 1, "stderr:");
        if (stdoutIndex >= 0)
            AddTextSection(sections, "stdout", Slice(lines, stdoutIndex + 1, stderrIndex >= 0 ? stderrIndex : lines.Length));
        if (stderrIndex >= 0)
            AddTextSection(sections, "stderr", Slice(lines, stderrIndex + 1, lines.Length), ToolDetailTone.Error);
        if (stdoutIndex < 0 && stderrIndex < 0)
            AddTextSection(sections, "output", Slice(lines, exitIndex + 1, lines.Length));
        return sections;
    }

    private static IReadOnlyList<ToolDetailSection> FormatAcpProcess(
        string[] lines,
        int exitIndex,
        int exitCode)
    {
        var sections = new List<ToolDetailSection> { StatusSection(exitCode, timedOut: false) };
        AddTextSection(sections, "run details", Slice(lines, 0, exitIndex));
        var outputIndex = FindLabel(lines, exitIndex + 1, "Final output:");
        AddTextSection(
            sections,
            "output",
            Slice(lines, outputIndex >= 0 ? outputIndex + 1 : exitIndex + 1, lines.Length),
            exitCode == 0 ? ToolDetailTone.Neutral : ToolDetailTone.Error);
        return sections;
    }

    private static ToolDetailSection StatusSection(int exitCode, bool timedOut)
        => new(
            "exit code",
            timedOut ? $"{exitCode} (timed out)" : exitCode.ToString(CultureInfo.InvariantCulture),
            "plaintext",
            timedOut ? ToolDetailTone.Warning : exitCode == 0 ? ToolDetailTone.Success : ToolDetailTone.Error);

    private static int FindLabel(IReadOnlyList<string> lines, int start, string label)
    {
        for (var i = Math.Max(0, start); i < lines.Count; i++)
            if (string.Equals(lines[i].Trim(), label, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string Slice(
        IReadOnlyList<string> lines,
        int start,
        int end,
        Func<string, bool>? include = null)
    {
        start = Math.Clamp(start, 0, lines.Count);
        end = Math.Clamp(end, start, lines.Count);
        var selected = lines.Skip(start).Take(end - start);
        if (include is not null) selected = selected.Where(include);
        return string.Join("\n", selected).Trim();
    }

    private static void AddTextSection(
        List<ToolDetailSection> sections,
        string label,
        string text,
        ToolDetailTone tone = ToolDetailTone.Neutral)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (TryUnwrapFence(text, out var fenced, out var fenceLanguage))
        {
            sections.Add(new ToolDetailSection(label, fenced, fenceLanguage, tone));
            return;
        }
        if (TryPrettyJson(text, out var pretty))
        {
            sections.Add(new ToolDetailSection(label, pretty, "json", tone));
            return;
        }
        sections.Add(new ToolDetailSection(label, text, DetectLanguage("", ToolDetailDirection.Output, text), tone));
    }

    private static bool TryParseJsonObject(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return true;
            document.Dispose();
        }
        catch (JsonException) { }
        document = null!;
        return false;
    }

    private static bool TryPrettyJson(string value, out string pretty)
    {
        pretty = "";
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return false;
            pretty = JsonSerializer.Serialize(document.RootElement, PrettyJson);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryUnwrapFence(string value, out string content, out string language)
    {
        content = "";
        language = "plaintext";
        var normalized = NormalizeNewlines(value).Trim();
        if (!normalized.StartsWith("```", StringComparison.Ordinal)) return false;
        var firstBreak = normalized.IndexOf('\n');
        var closing = normalized.LastIndexOf("\n```", StringComparison.Ordinal);
        if (firstBreak < 0 || closing <= firstBreak) return false;
        var hint = normalized[3..firstBreak].Trim();
        content = normalized[(firstBreak + 1)..closing];
        language = MapLanguage(hint);
        if (language == "plaintext")
            language = DetectLanguage("", ToolDetailDirection.Output, content);
        return true;
    }

    private static string DetectLanguage(string tool, ToolDetailDirection direction, string value)
    {
        if (direction == ToolDetailDirection.Input)
        {
            var toolLanguage = LanguageForTool(tool);
            if (toolLanguage != "plaintext" && !LooksLikeJson(value)) return toolLanguage;
        }
        if (LooksLikeJson(value)) return "json";
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal)
            && trimmed.Contains('>'))
            return "xml";
        if (SqlStart.IsMatch(trimmed)) return "sql";
        return "plaintext";
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.TrimStart();
        return (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            && TryPrettyJson(value, out _);
    }

    private static string LanguageForTool(string tool) => tool switch
    {
        "run_powershell" => "powershell",
        "run_cmd" => "dos",
        "run_python" => "python",
        "run_csharp_script" => "csharp",
        _ => "plaintext"
    };

    private static string LanguageFromPath(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".ps1" or ".psm1" or ".psd1" => "powershell",
        ".cmd" or ".bat" => "dos",
        ".sh" or ".bash" => "bash",
        ".py" => "python",
        ".cs" => "csharp",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".html" or ".htm" or ".xml" or ".xaml" or ".razor" => "xml",
        ".css" or ".scss" => "css",
        ".sql" => "sql",
        ".md" or ".markdown" => "markdown",
        ".json" => "json",
        _ => "plaintext"
    };

    private static string MapLanguage(string hint) => hint.Trim().ToLowerInvariant() switch
    {
        "ps1" or "pwsh" or "powershell" => "powershell",
        "cmd" or "bat" or "batch" or "dos" => "dos",
        "sh" or "shell" or "bash" => "bash",
        "py" or "python" => "python",
        "cs" or "c#" or "csharp" or "dotnet" => "csharp",
        "js" or "javascript" or "node" => "javascript",
        "ts" or "typescript" => "typescript",
        "html" or "htm" or "xml" or "xaml" or "razor" => "xml",
        "css" or "scss" => "css",
        "sql" => "sql",
        "md" or "markdown" => "markdown",
        "json" or "jsonc" => "json",
        "text" or "txt" or "plaintext" or "" => "plaintext",
        _ => "plaintext"
    };

    private static string NormalizeNewlines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
