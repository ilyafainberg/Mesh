using Markdig;

namespace Mesh.App.Services;

/// <summary>A parsed piece of a message: markdown text, an interactive HTML app, or a file attachment.</summary>
public sealed record MessageSegment(bool IsApp, string Content, bool IsFile = false, string? FileName = null, string? FileMime = null);

/// <summary>Renders chat/message markdown to HTML for display in bubbles.</summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .UsePipeTables()
        .UseGridTables()
        .DisableHtml() // strip raw HTML so agent output can't inject markup
        .Build();

    public static string ToHtml(string? text)
        => string.IsNullOrEmpty(text) ? "" : Markdig.Markdown.ToHtml(text, Pipeline);

    /// <summary>Heuristic: does this text contain markdown worth previewing?</summary>
    public static bool LooksLikeMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("**") || text.Contains("__") || text.Contains("`")
            || text.Contains("* ") || text.Contains("- ") || text.Contains("# ")
            || text.Contains("] (") || text.Contains("](") || text.Contains("\n1. ") || text.StartsWith("1. ")
            || text.Contains("> ") || text.Contains("~~")
            || (text.Contains("|") && text.Contains("\n"));
    }

    /// <summary>
    /// Splits a message into markdown segments and fenced ```html-app blocks.
    /// The html-app blocks are the agent's interactive mini-apps.
    /// </summary>
    public static IReadOnlyList<MessageSegment> Parse(string? text)
    {
        var result = new List<MessageSegment>();
        if (string.IsNullOrEmpty(text)) return result;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var buffer = new System.Text.StringBuilder();
        var app = new System.Text.StringBuilder();
        var file = new System.Text.StringBuilder();
        var inApp = false;
        var inFile = false;

        void FlushText()
        {
            if (buffer.Length > 0) { result.Add(new MessageSegment(false, buffer.ToString().Trim('\n'))); buffer.Clear(); }
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!inApp && !inFile && trimmed.StartsWith("```"))
            {
                if (IsAppFence(trimmed)) { FlushText(); inApp = true; app.Clear(); continue; }
                if (IsFileFence(trimmed)) { FlushText(); inFile = true; file.Clear(); continue; }
            }
            if (inApp && trimmed.StartsWith("```"))
            {
                inApp = false;
                result.Add(new MessageSegment(true, app.ToString()));
                continue;
            }
            if (inFile && trimmed.StartsWith("```"))
            {
                inFile = false;
                AddFileSegment(result, file.ToString());
                continue;
            }
            if (inApp) app.AppendLine(line);
            else if (inFile) file.AppendLine(line);
            else buffer.AppendLine(line);
        }
        if (inApp && app.Length > 0) result.Add(new MessageSegment(true, app.ToString())); // unterminated
        if (inFile && file.Length > 0) AddFileSegment(result, file.ToString());
        FlushText();
        return result;
    }

    /// <summary>A fenced block tagged <c>mesh-file</c> carries a JSON attachment descriptor.</summary>
    private static bool IsFileFence(string fenceLine)
        => fenceLine.TrimStart('`').Trim().ToLowerInvariant() is "mesh-file" or "meshfile";

    private static void AddFileSegment(List<MessageSegment> result, string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var mime = root.TryGetProperty("mime", out var m) ? m.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (string.IsNullOrEmpty(data)) return;
            result.Add(new MessageSegment(false, data, IsFile: true,
                FileName: string.IsNullOrWhiteSpace(name) ? "file" : name,
                FileMime: string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime));
        }
        catch { /* malformed attachment, drop it silently */ }
    }

    /// <summary>Builds a <c>mesh-file</c> fenced block that <see cref="Parse"/> renders as an attachment.</summary>
    public static string FileBlock(string name, string mime, string base64)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { name, mime, data = base64 });
        return $"```mesh-file\n{json}\n```";
    }

    /// <summary>
    /// A fenced block is treated as an app if tagged html-app/app, OR tagged html
    /// AND it contains a full HTML document (so plain ```html full pages also run).
    /// </summary>
    private static bool IsAppFence(string fenceLine)
    {
        var tag = fenceLine.TrimStart('`').Trim().ToLowerInvariant();
        return tag is "html-app" or "htmlapp" or "app";
    }
}

