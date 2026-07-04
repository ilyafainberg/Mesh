using System.Text;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Owner-gated file-system + file-format tool: list directories, read files (including PDF/Word/
/// Excel/PowerPoint as text via <see cref="DocumentExtractor"/>), write and append text files, get
/// file info, make and delete files/directories, and do best-effort format conversion. Runs on the
/// owner's own machine so there is no size cap on disk, but read output is truncated to max_chars for
/// the model's benefit. All failures are returned as descriptive strings; nothing throws out of
/// <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class FileSystemTool(DocumentExtractor extractor) : IAgentTool
{
    private readonly DocumentExtractor extractor = extractor;

    private const int ListCap = 500;

    public string Name => "file_system";

    public string Description =>
        "Work with local files: list a directory, read a file (including PDF/Word/Excel/PowerPoint as " +
        "text), write or append a text file, get file info, make or delete a directory, delete a file, " +
        "and convert documents to plain text or markdown. Use to inspect and produce files on disk.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                description = "list | read | write | append | info | mkdir | delete | convert"
            },
            path = new { type = "string", description = "File or directory path (source path for convert)." },
            content = new { type = "string", description = "Text content for write/append." },
            to = new { type = "string", description = "Target path for convert (extension decides the format)." },
            max_chars = new { type = "integer", description = "Cap on characters returned by read (default 40000)." },
            overwrite = new { type = "boolean", description = "For write: overwrite an existing file (default true)." }
        },
        required = new[] { "action", "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        try
        {
            var action = ToolArgs.GetString(args, "action").Trim().ToLowerInvariant();
            var path = ToolArgs.GetString(args, "path");

            if (string.IsNullOrWhiteSpace(action))
                return "ERROR: 'action' is required. Valid actions: list, read, write, append, info, mkdir, delete, convert.";
            if (string.IsNullOrWhiteSpace(path))
                return "ERROR: 'path' is required.";

            return action switch
            {
                "list" => ListEntries(path),
                "read" => await ReadEntryAsync(args, path, ct),
                "write" => await WriteEntryAsync(args, path, ct),
                "append" => await AppendEntryAsync(args, path, ct),
                "info" => InfoEntry(path),
                "mkdir" => MakeDirectory(path),
                "delete" => DeleteFile(path),
                "convert" => await ConvertEntryAsync(args, path, ct),
                _ => $"ERROR: unknown action '{action}'. Valid actions: list, read, write, append, info, mkdir, delete, convert."
            };
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static string ListEntries(string path)
    {
        if (File.Exists(path))
            return InfoEntry(path);
        if (!Directory.Exists(path))
            return $"ERROR: directory not found: {path}";

        var dir = new DirectoryInfo(path);
        var items = new List<object>();
        var count = 0;
        var truncated = false;

        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (count >= ListCap) { truncated = true; break; }
            count++;
            var isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
            long size = 0;
            if (!isDir && entry is FileInfo fi) size = fi.Length;
            items.Add(new
            {
                name = entry.Name,
                type = isDir ? "dir" : "file",
                size,
                lastModified = entry.LastWriteTimeUtc.ToString("o")
            });
        }

        return Json(new
        {
            path = dir.FullName,
            count,
            truncated,
            entries = items
        });
    }

    private async Task<string> ReadEntryAsync(JsonElement args, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return $"ERROR: file not found: {path}";

        var maxChars = ToolArgs.GetInt(args, "max_chars", 40000);
        if (maxChars <= 0) maxChars = 40000;

        var name = Path.GetFileName(path);

        if (DocumentExtractor.IsSupported(path))
        {
            var text = await extractor.ExtractFileAsync(path, maxChars, ct);
            return string.IsNullOrWhiteSpace(text)
                ? $"'{name}' contained no extractable text."
                : $"Content of \"{name}\":\n{text}";
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (LooksLikeText(bytes))
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length > maxChars) text = text[..maxChars] + "\n…(truncated)";
            return $"Content of \"{name}\":\n{text}";
        }

        return $"'{name}' is a binary file ({Path.GetExtension(path)}, {bytes.Length} bytes); not shown as text.";
    }

    private static async Task<string> WriteEntryAsync(JsonElement args, string path, CancellationToken ct)
    {
        if (!(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("content", out var contentEl)
              && contentEl.ValueKind == JsonValueKind.String))
            return "ERROR: 'content' (a string) is required for write.";

        var content = contentEl.GetString() ?? "";
        var overwrite = GetBool(args, "overwrite", true);

        if (!overwrite && File.Exists(path))
            return $"ERROR: file already exists and overwrite is false: {path}";

        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return Json(new { ok = true, action = "write", path = Path.GetFullPath(path), bytesWritten = bytes.Length });
    }

    private static async Task<string> AppendEntryAsync(JsonElement args, string path, CancellationToken ct)
    {
        if (!(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("content", out var contentEl)
              && contentEl.ValueKind == JsonValueKind.String))
            return "ERROR: 'content' (a string) is required for append.";

        var content = contentEl.GetString() ?? "";
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        await File.AppendAllTextAsync(path, content, Encoding.UTF8, ct);
        var bytes = Encoding.UTF8.GetByteCount(content);
        return Json(new { ok = true, action = "append", path = Path.GetFullPath(path), bytesAppended = bytes });
    }

    private static string InfoEntry(string path)
    {
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            return Json(new
            {
                name = di.Name,
                fullPath = di.FullName,
                isDirectory = true,
                size = (long?)null,
                created = di.CreationTimeUtc.ToString("o"),
                modified = di.LastWriteTimeUtc.ToString("o")
            });
        }
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return Json(new
            {
                name = fi.Name,
                fullPath = fi.FullName,
                isDirectory = false,
                size = fi.Length,
                created = fi.CreationTimeUtc.ToString("o"),
                modified = fi.LastWriteTimeUtc.ToString("o")
            });
        }
        return $"ERROR: path not found: {path}";
    }

    private static string MakeDirectory(string path)
    {
        var created = Directory.CreateDirectory(path);
        return Json(new { ok = true, action = "mkdir", path = created.FullName });
    }

    private static string DeleteFile(string path)
    {
        if (Directory.Exists(path))
            return $"ERROR: '{path}' is a directory; delete only removes files.";
        if (!File.Exists(path))
            return $"ERROR: file not found: {path}";
        File.Delete(path);
        return Json(new { ok = true, action = "delete", path = Path.GetFullPath(path) });
    }

    private async Task<string> ConvertEntryAsync(JsonElement args, string path, CancellationToken ct)
    {
        var to = ToolArgs.GetString(args, "to");
        if (string.IsNullOrWhiteSpace(to))
            return "ERROR: 'to' (target path) is required for convert.";
        if (!File.Exists(path))
            return $"ERROR: source file not found: {path}";

        var srcExt = Path.GetExtension(path).ToLowerInvariant();
        var dstExt = Path.GetExtension(to).ToLowerInvariant();

        if (dstExt is not (".txt" or ".md"))
            return $"conversion {srcExt} to {dstExt} not supported with the referenced packages. Supported targets: .txt, .md.";

        string text;
        if (DocumentExtractor.IsSupported(path))
        {
            text = await extractor.ExtractFileAsync(path, int.MaxValue, ct);
        }
        else if (srcExt == ".csv")
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (!LooksLikeText(bytes))
                return $"conversion {srcExt} to {dstExt} not supported: source is not readable text.";
            text = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (!LooksLikeText(bytes))
                return $"conversion {srcExt} to {dstExt} not supported with the referenced packages.";
            text = Encoding.UTF8.GetString(bytes);
        }

        if (dstExt == ".md")
            text = ToMarkdown(text, Path.GetFileName(path));

        var parent = Path.GetDirectoryName(Path.GetFullPath(to));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var outBytes = Encoding.UTF8.GetBytes(text);
        await File.WriteAllBytesAsync(to, outBytes, ct);
        return Json(new
        {
            ok = true,
            action = "convert",
            from = Path.GetFullPath(path),
            to = Path.GetFullPath(to),
            bytesWritten = outBytes.Length
        });
    }

    private static string ToMarkdown(string text, string sourceName)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Path.GetFileNameWithoutExtension(sourceName));
        sb.AppendLine();
        sb.Append(text);
        return sb.ToString();
    }

    /// <summary>Heuristic: try a UTF-8 decode and reject if too many null/control bytes appear.</summary>
    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0) return true;

        var sample = Math.Min(bytes.Length, 8000);
        var control = 0;
        for (var i = 0; i < sample; i++)
        {
            var b = bytes[i];
            if (b == 0) return false;
            var isAllowed = b is 9 or 10 or 13 || b >= 32;
            if (!isAllowed) control++;
        }
        return control <= sample * 0.05;
    }

    private static bool GetBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }

    private static string Json(object value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
}
