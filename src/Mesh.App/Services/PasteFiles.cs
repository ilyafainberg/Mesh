using System.Text.RegularExpressions;

namespace Mesh.App.Services;

/// <summary>
/// Turns a pasted clipboard file (base64 from paste.js) into a temp file on disk, so the existing
/// path-based staged-file send flow can carry it. Composers call <see cref="SaveTemp"/> from their
/// [JSInvokable] paste callback and add the result to their staged-file list.
/// </summary>
public static class PasteFiles
{
    /// <summary>The result of staging a pasted file: a safe display name, the temp file path, and its size.</summary>
    public readonly record struct Staged(string Name, string Path, long Size);

    /// <summary>
    /// Writes the base64 payload to a uniquely named temp file (preserving the original extension) and
    /// returns its name, path, and size. Returns null on any failure or empty payload.
    /// </summary>
    public static Staged? SaveTemp(string name, string mime, string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0) return null;

            var safe = SafeName(name, mime);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeshPaste");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("n") + "_" + safe);
            System.IO.File.WriteAllBytes(path, bytes);
            return new Staged(safe, path, bytes.Length);
        }
        catch { return null; }
    }

    // A filesystem-safe display name, defaulting the extension from the mime type when the pasted
    // item had no usable name (common for images copied from another app).
    private static string SafeName(string name, string mime)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "pasted" : name.Trim();
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        if (!System.IO.Path.HasExtension(n))
        {
            var ext = ExtFor(mime);
            if (ext is not null) n += ext;
        }
        return n.Length > 120 ? n[^120..] : n;
    }

    private static string? ExtFor(string mime) => (mime ?? "").ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        _ => null
    };
}
