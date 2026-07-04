using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Owner-only tool that lets the agent read a local file the user attached in their private "Me"
/// chat, the same way Scout reads files by path. There is no size cap here (unlike peer messages,
/// which embed bytes over the wire): the file stays on disk and only the extracted text is fed to
/// the model, on demand.
///
/// For safety the tool will only open paths the user explicitly attached this session (an allow
/// list held by <see cref="LocalFileRegistry"/>), so a hostile prompt cannot make the agent read
/// arbitrary files off the disk.
/// </summary>
public sealed class ReadLocalFileTool(LocalFileRegistry registry, DocumentExtractor extractor) : IAgentTool
{
    public string Name => "read_local_file";

    public string Description =>
        "Read a local file the user attached in this chat, by its path, and return its text content. " +
        "Use this to open documents the user shared (PDF, Word, Excel, PowerPoint, text, CSV, JSON, code). " +
        "Only files the user attached in this chat can be read.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Full path of the attached file to read." },
            max_chars = new { type = "integer", description = "Optional cap on characters to return (default 40000)." }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path)) return "ERROR: no path given.";
        if (!registry.IsAllowed(path)) return $"ERROR: '{path}' was not attached in this chat, so it can't be read.";
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";
        var maxChars = ToolArgs.GetInt(args, "max_chars", 40000);
        if (!DocumentExtractor.IsSupported(path))
            return $"'{Path.GetFileName(path)}' is not a text-extractable type ({Path.GetExtension(path)}). Supported: documents, spreadsheets, slides and plain-text formats.";
        try
        {
            var text = await extractor.ExtractFileAsync(path, maxChars, ct);
            return string.IsNullOrWhiteSpace(text)
                ? $"'{Path.GetFileName(path)}' contained no extractable text."
                : $"Content of \"{Path.GetFileName(path)}\":\n{text}";
        }
        catch (Exception ex) { return $"ERROR reading '{Path.GetFileName(path)}': {ex.Message}"; }
    }
}

/// <summary>
/// Session allow list of local file paths the owner attached in their private chat. Only paths in
/// this set may be opened by <see cref="ReadLocalFileTool"/>, so the agent cannot be talked into
/// reading arbitrary files. Singleton, lives for the app session (not persisted).
/// </summary>
public sealed class LocalFileRegistry
{
    private readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public void Allow(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (gate) allowed.Add(Normalize(path));
    }

    public bool IsAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (gate) return allowed.Contains(Normalize(path));
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}
