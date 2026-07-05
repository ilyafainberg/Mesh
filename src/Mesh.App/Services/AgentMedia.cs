namespace Mesh.App.Services;

/// <summary>
/// A single image a tool produced during an agent turn, to be shown in the chat.
/// </summary>
public sealed record AgentImage(string Mime, string Base64, string Name);

/// <summary>
/// Ambient collector for images that tools produce during a turn (screenshots, rendered charts, etc.)
/// so the chat can DISPLAY them rather than the model narrating "[image]". A caller opens a scope for
/// the current async flow; any tool that runs inside it can report an image, and the caller drains
/// the collected images afterward and appends them to the chat message as renderable file blocks.
/// The scope flows with <see cref="AsyncLocal{T}"/> so concurrent turns do not mix images.
/// </summary>
public sealed class AgentMedia
{
    private readonly AsyncLocal<List<AgentImage>?> sink = new();

    /// <summary>Begins collecting images for the current async flow. Dispose to stop and detach.</summary>
    public IDisposable BeginScope(out List<AgentImage> collected)
    {
        collected = new List<AgentImage>();
        var previous = sink.Value;
        sink.Value = collected;
        return new Scope(this, previous);
    }

    /// <summary>Reports raw image bytes (base64) for the active scope, if any.</summary>
    public void Report(string mime, string base64, string? name = null)
    {
        if (string.IsNullOrEmpty(base64)) return;
        sink.Value?.Add(new AgentImage(
            string.IsNullOrWhiteSpace(mime) ? "image/png" : mime,
            base64,
            string.IsNullOrWhiteSpace(name) ? "image" : name!));
    }

    /// <summary>Reports an image file on disk (read + base64) for the active scope, if any.</summary>
    public void ReportFile(string path)
    {
        if (sink.Value is null) return;
        try
        {
            if (!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            Report(MimeForExtension(Path.GetExtension(path)), Convert.ToBase64String(bytes), Path.GetFileName(path));
        }
        catch { /* best effort */ }
    }

    /// <summary>True when a collection scope is active on the current flow (so tools can skip work if not).</summary>
    public bool IsCollecting => sink.Value is not null;

    private static string MimeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "image/png"
    };

    private sealed class Scope(AgentMedia media, List<AgentImage>? previous) : IDisposable
    {
        public void Dispose() => media.sink.Value = previous;
    }
}
