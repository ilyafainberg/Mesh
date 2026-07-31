using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Thrown when a candidate skill-package archive fails a security or structural validation. The
/// message is user-facing and states the precise reason the package was rejected.
/// </summary>
public sealed class SkillPackageValidationException : Exception
{
    public SkillPackageValidationException(string message) : base(message) { }
}

/// <summary>A fully validated in-memory skill package: its manifest plus the bytes of every file.</summary>
public sealed class SkillPackageContent
{
    public SkillPackageManifest Manifest { get; }

    /// <summary>File bytes keyed by normalized package-relative path (ordinal).</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    public SkillPackageContent(SkillPackageManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        Manifest = manifest;
        Files = files;
    }

    /// <summary>The decoded UTF-8 text of the single Skill.md instruction file.</summary>
    public string SkillMarkdownText
    {
        get
        {
            var path = Manifest.SkillMarkdown.Path;
            return Encoding.UTF8.GetString(Files[path]);
        }
    }
}

/// <summary>
/// Safe, fully in-memory ZIP parser and manifest builder for Mesh 1.17 skill packages. It never
/// touches the filesystem: an archive is parsed from a byte buffer, every entry is validated against
/// the skill-package rules (path safety, size and count limits, single Skill.md, compression-ratio
/// and total-size caps, no symlinks/encrypted/unsupported entries), and the result is a
/// <see cref="SkillPackageContent"/> carrying an immutable <see cref="SkillPackageManifest"/> whose
/// <see cref="SkillPackageManifest.PackageHash"/> is a canonical content hash - SHA-256 over the
/// sorted (path, per-file hash) pairs, independent of the ZIP container's byte layout.
/// </summary>
public static class SkillPackageArchive
{
    // ---- Strict limits (documented in the 1.17 spec) -----------------------
    public const int MaxFiles = 100;
    public const long MaxSkillMarkdownBytes = 100 * 1024;          // 100 KB
    public const long MaxScriptBytes = 1 * 1024 * 1024;           // 1 MB
    public const long MaxResourceBytes = 10 * 1024 * 1024;        // 10 MB
    public const long MaxTotalBytes = 20 * 1024 * 1024;          // 20 MB
    public const long MaxSingleEntryBytes = MaxResourceBytes;    // never expand a single entry past 10 MB
    // A single entry may not expand beyond this ratio versus its compressed size (zip-bomb guard).
    public const long MaxCompressionRatio = 200;

    public const string SkillMarkdownFileName = "skill.md";

    // MS-DOS/Unix external-attribute bits used to flag a symbolic link (S_IFLNK == 0xA000, shifted
    // into the high 16 bits of the external attributes field).
    private const uint UnixSymlinkMask = 0xA000u << 16;
    private const uint UnixModeMask = 0xFFFFu << 16;
    private const uint UnixExecBits = 0x49u << 16; // 0o111 (owner/group/other execute)

    /// <summary>
    /// Parse and validate a skill-package archive held entirely in memory. On success returns the
    /// validated content; on any violation throws <see cref="SkillPackageValidationException"/> with a
    /// user-facing reason. <paramref name="compatibility"/> and <paramref name="source"/>/<paramref
    /// name="trust"/> are attached to the resulting manifest (compatibility is deep-copied).
    /// </summary>
    public static SkillPackageContent Parse(
        byte[] zipBytes,
        SkillCompatibility compatibility,
        string? version = null,
        string? source = null,
        SkillPackageTrust trust = SkillPackageTrust.Untrusted)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);
        ArgumentNullException.ThrowIfNull(compatibility);
        if (zipBytes.Length == 0)
            throw new SkillPackageValidationException("The skill package archive is empty.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seenLower = new Dictionary<string, string>(StringComparer.Ordinal);
        var manifests = new List<SkillFileManifest>();
        long totalBytes = 0;

        using var ms = new MemoryStream(zipBytes, writable: false);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new SkillPackageValidationException("The skill package is not a valid ZIP archive.");
        }

        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                // Directory entries (trailing slash, zero length name segment) carry no content.
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    continue;
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                RejectSymlink(entry);

                var normalized = NormalizePath(entry.FullName);

                // Case-insensitive collision guard (two paths differing only by case would clobber
                // each other on a case-insensitive filesystem during materialization).
                var lower = normalized.ToLowerInvariant();
                if (seenLower.TryGetValue(lower, out var existing))
                {
                    throw new SkillPackageValidationException(
                        $"The package contains duplicate or case-colliding paths: '{existing}' and '{normalized}'.");
                }
                seenLower[lower] = normalized;

                if (files.Count + 1 > MaxFiles)
                    throw new SkillPackageValidationException(
                        $"The package contains more than the maximum of {MaxFiles} files.");

                var role = ClassifyRole(normalized);

                // Declared (uncompressed) length guards before we ever decompress a byte.
                var declared = entry.Length;
                var compressed = entry.CompressedLength;
                if (declared > MaxSingleEntryBytes)
                    throw new SkillPackageValidationException(
                        $"File '{normalized}' is larger than the {MaxSingleEntryBytes / (1024 * 1024)} MB per-file limit.");
                if (compressed > 0 && declared / compressed > MaxCompressionRatio)
                    throw new SkillPackageValidationException(
                        $"File '{normalized}' has a suspicious compression ratio and was rejected as a possible archive bomb.");

                byte[] bytes = ReadEntry(entry, normalized);

                EnforceRoleSize(role, normalized, bytes.LongLength);

                totalBytes += bytes.LongLength;
                if (totalBytes > MaxTotalBytes)
                    throw new SkillPackageValidationException(
                        $"The package exceeds the total uncompressed size limit of {MaxTotalBytes / (1024 * 1024)} MB.");

                if (role == SkillFileRole.SkillMarkdown && !IsValidUtf8(bytes))
                    throw new SkillPackageValidationException(
                        "Skill.md is not valid UTF-8 text.");

                files[normalized] = bytes;
                manifests.Add(new SkillFileManifest
                {
                    Path = normalized,
                    Sha256 = Sha256Hex(bytes),
                    Size = bytes.LongLength,
                    Role = role,
                    Executable = IsExecutable(entry)
                });
            }
        }

        var markdown = manifests.Where(m => m.Role == SkillFileRole.SkillMarkdown).ToList();
        if (markdown.Count == 0)
            throw new SkillPackageValidationException("The package does not contain a Skill.md file.");
        if (markdown.Count > 1)
            throw new SkillPackageValidationException(
                "The package contains more than one Skill.md file; exactly one is required.");

        manifests.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var manifest = new SkillPackageManifest
        {
            PackageHash = ComputeCanonicalHash(manifests),
            Version = version,
            Source = source,
            Trust = trust,
            Compatibility = compatibility.Clone(),
            Files = manifests
        };

        return new SkillPackageContent(manifest, files);
    }

    /// <summary>
    /// Build a manifest for a Skill.md-only "package" (no archive) - used when a mobile device or a
    /// catalog result yields only instruction text. The result contains exactly one file.
    /// </summary>
    public static SkillPackageContent FromSkillMarkdown(
        string skillMarkdown,
        SkillCompatibility compatibility,
        string? version = null,
        string? source = null,
        SkillPackageTrust trust = SkillPackageTrust.Untrusted)
    {
        ArgumentNullException.ThrowIfNull(skillMarkdown);
        ArgumentNullException.ThrowIfNull(compatibility);
        var bytes = Encoding.UTF8.GetBytes(skillMarkdown);
        if (bytes.LongLength > MaxSkillMarkdownBytes)
            throw new SkillPackageValidationException(
                $"Skill.md is larger than the {MaxSkillMarkdownBytes / 1024} KB limit.");

        const string path = "Skill.md";
        var file = new SkillFileManifest
        {
            Path = path,
            Sha256 = Sha256Hex(bytes),
            Size = bytes.LongLength,
            Role = SkillFileRole.SkillMarkdown,
            Executable = false
        };
        var manifest = new SkillPackageManifest
        {
            PackageHash = ComputeCanonicalHash(new[] { file }),
            Version = version,
            Source = source,
            Trust = trust,
            Compatibility = compatibility.Clone(),
            Files = new List<SkillFileManifest> { file }
        };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = bytes };
        return new SkillPackageContent(manifest, files);
    }

    /// <summary>
    /// Canonical package hash: SHA-256 over the newline-joined sorted "path\0sha256" pairs. It is a
    /// pure function of file content and layout, never of the ZIP container, so two archives with the
    /// same files hash identically. Public so callers/tests can recompute and verify.
    /// </summary>
    public static string ComputeCanonicalHash(IEnumerable<SkillFileManifest> files)
    {
        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var f in ordered)
        {
            sb.Append(f.Path);
            sb.Append('\0');
            sb.Append(f.Sha256);
            sb.Append('\n');
        }
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // ---- internals ---------------------------------------------------------

    private static byte[] ReadEntry(ZipArchiveEntry entry, string normalized)
    {
        // Decompress with a hard cap so a lying/streaming entry cannot exhaust memory even if the
        // declared length was understated.
        var cap = MaxSingleEntryBytes;
        using var target = new MemoryStream();
        try
        {
            using var src = entry.Open();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > cap)
                    throw new SkillPackageValidationException(
                        $"File '{normalized}' expands beyond the per-file limit and was rejected as a possible archive bomb.");
                target.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            // Encrypted or otherwise unsupported entries surface here.
            throw new SkillPackageValidationException(
                $"File '{normalized}' is encrypted or uses an unsupported compression method.");
        }
        return target.ToArray();
    }

    private static void RejectSymlink(ZipArchiveEntry entry)
    {
        var mode = (uint)entry.ExternalAttributes & UnixModeMask;
        if ((mode & UnixSymlinkMask) == UnixSymlinkMask)
            throw new SkillPackageValidationException(
                $"The package contains a symbolic link ('{entry.FullName}'), which is not allowed.");
    }

    private static bool IsExecutable(ZipArchiveEntry entry)
        => ((uint)entry.ExternalAttributes & UnixExecBits) != 0;

    /// <summary>
    /// Normalize a raw archive entry name to a safe, package-relative forward-slash path. Rejects
    /// absolute paths, Windows drive/UNC roots, and any traversal outside the package root.
    /// </summary>
    public static string NormalizePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new SkillPackageValidationException("The package contains an entry with an empty path.");

        var unified = raw.Replace('\\', '/').Trim();

        if (unified.StartsWith('/'))
            throw new SkillPackageValidationException($"Absolute paths are not allowed: '{raw}'.");
        if (unified.Length >= 2 && char.IsLetter(unified[0]) && unified[1] == ':')
            throw new SkillPackageValidationException($"Drive-rooted paths are not allowed: '{raw}'.");
        if (unified.StartsWith("//", StringComparison.Ordinal))
            throw new SkillPackageValidationException($"UNC paths are not allowed: '{raw}'.");

        var segments = new List<string>();
        foreach (var seg in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
                throw new SkillPackageValidationException($"Path traversal is not allowed: '{raw}'.");
            segments.Add(seg);
        }
        if (segments.Count == 0)
            throw new SkillPackageValidationException($"The package contains an invalid path: '{raw}'.");

        return string.Join('/', segments);
    }

    private static SkillFileRole ClassifyRole(string normalizedPath)
    {
        var name = normalizedPath.Contains('/')
            ? normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..]
            : normalizedPath;
        var lower = name.ToLowerInvariant();

        if (lower == SkillMarkdownFileName)
            return SkillFileRole.SkillMarkdown;

        var ext = lower.Contains('.') ? lower[(lower.LastIndexOf('.') + 1)..] : "";
        return ext switch
        {
            "sh" or "bash" or "zsh" or "ps1" or "bat" or "cmd" or "py" or "js" or "ts" or "rb" or "pl"
                => SkillFileRole.Script,
            "json" or "yaml" or "yml" or "toml" or "ini" or "cfg" or "config"
                => SkillFileRole.Config,
            _ => SkillFileRole.Resource
        };
    }

    private static void EnforceRoleSize(SkillFileRole role, string path, long size)
    {
        switch (role)
        {
            case SkillFileRole.SkillMarkdown when size > MaxSkillMarkdownBytes:
                throw new SkillPackageValidationException(
                    $"Skill.md is larger than the {MaxSkillMarkdownBytes / 1024} KB limit.");
            case SkillFileRole.Script when size > MaxScriptBytes:
                throw new SkillPackageValidationException(
                    $"Script '{path}' is larger than the {MaxScriptBytes / (1024 * 1024)} MB limit.");
            case SkillFileRole.Config when size > MaxResourceBytes:
            case SkillFileRole.Resource when size > MaxResourceBytes:
                throw new SkillPackageValidationException(
                    $"Resource '{path}' is larger than the {MaxResourceBytes / (1024 * 1024)} MB limit.");
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
