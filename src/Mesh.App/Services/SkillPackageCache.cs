using System.Security.Cryptography;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Desktop-only, lazy, immutable materialization of a validated skill package to disk. A package is
/// only ever written to the filesystem when something needs the real folder (for example a desktop
/// tool that must execute a script); the encrypted DB rows remain the source of truth. Materialization
/// is:
/// <list type="bullet">
///   <item><b>Lazy and content-addressed</b> - each package is written once under
///   <c>{Root}\Cache\Skills\{skillId}\{packageHash}</c> and reused thereafter.</item>
///   <item><b>Hash-validated</b> - every file's SHA-256 is checked against its manifest before it is
///   written and again after the folder is finalized; any mismatch aborts and cleans up.</item>
///   <item><b>Atomic</b> - files are staged in a sibling temp directory and moved into place with a
///   single directory rename, so a partially written cache is never observable.</item>
///   <item><b>Immutable</b> - the finalized folder and its files are marked read-only and no executable
///   bit is ever set.</item>
/// </list>
/// On mobile the type throws <see cref="PlatformNotSupportedException"/> before touching the
/// filesystem: mobile devices keep only the Skill.md body and never materialize folders.
/// </summary>
public sealed class SkillPackageCache
{
    private readonly string _cacheRoot;
    private readonly bool _isMobile;

    /// <param name="cacheRoot">
    /// Root directory for materialized packages (typically <c>{StoragePaths.Root}\Cache\Skills</c>,
    /// supplied by the caller so this type carries no platform-path dependency).
    /// </param>
    /// <param name="isMobile">Overrides platform detection (for tests). Defaults to real platform.</param>
    public SkillPackageCache(string cacheRoot, bool? isMobile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _isMobile = isMobile ?? PlatformCaps.IsMobile;
        _cacheRoot = cacheRoot;
    }

    /// <summary>Absolute directory a package would occupy once materialized.</summary>
    public string PathFor(string skillId, string packageHash)
        => Path.Combine(_cacheRoot, Sanitize(skillId), Sanitize(packageHash));

    /// <summary>True if the package is already materialized on disk.</summary>
    public bool IsMaterialized(string skillId, string packageHash)
    {
        if (_isMobile) return false;
        var dir = PathFor(skillId, packageHash);
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    /// <summary>
    /// Lazily materialize <paramref name="content"/> for <paramref name="skillId"/> and return the
    /// absolute folder path. If already materialized, returns the existing path without rewriting.
    /// Desktop only.
    /// </summary>
    public string Materialize(string skillId, SkillPackageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfMobile();

        var hash = content.Manifest.PackageHash;
        var finalDir = PathFor(skillId, hash);
        if (IsMaterialized(skillId, hash))
            return finalDir;

        // Validate the manifest against the supplied bytes BEFORE writing anything.
        foreach (var file in content.Manifest.Files)
        {
            if (!content.Files.TryGetValue(file.Path, out var bytes))
                throw new SkillPackageValidationException(
                    $"Package content is missing file '{file.Path}' declared in the manifest.");
            var actual = Sha256Hex(bytes);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SkillPackageValidationException(
                    $"File '{file.Path}' failed hash validation before materialization.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
        var tempDir = finalDir + ".tmp-" + Guid.NewGuid().ToString("n");

        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (var file in content.Manifest.Files)
            {
                var bytes = content.Files[file.Path];
                var target = Path.Combine(tempDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            // Marker records the package hash and makes IsMaterialized cheap and unambiguous.
            File.WriteAllText(Path.Combine(tempDir, MarkerFileName), hash);

            // Validate every file again from disk AFTER writing, before we expose the folder.
            foreach (var file in content.Manifest.Files)
            {
                var target = Path.Combine(tempDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var actual = Sha256Hex(File.ReadAllBytes(target));
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SkillPackageValidationException(
                        $"File '{file.Path}' failed hash validation after materialization.");
            }

            // Atomic publish. If a concurrent writer beat us to it, keep the existing folder.
            try
            {
                Directory.Move(tempDir, finalDir);
            }
            catch (IOException) when (Directory.Exists(finalDir))
            {
                TryDelete(tempDir);
                return finalDir;
            }
        }
        catch
        {
            TryDelete(tempDir);
            throw;
        }

        MarkReadOnly(finalDir);
        return finalDir;
    }

    /// <summary>Remove a single materialized package (used on delete/update). Safe if absent.</summary>
    public void Remove(string skillId, string packageHash)
    {
        if (_isMobile) return;
        RemoveDir(PathFor(skillId, packageHash));
    }

    /// <summary>Remove every materialized package for a skill (used on skill delete). Safe if absent.</summary>
    public void RemoveAll(string skillId)
    {
        if (_isMobile) return;
        RemoveDir(Path.Combine(_cacheRoot, Sanitize(skillId)));
    }

    // ---- internals ---------------------------------------------------------

    private const string MarkerFileName = ".mesh-package";

    private void ThrowIfMobile()
    {
        if (_isMobile)
            throw new PlatformNotSupportedException(
                "Skill packages are never materialized on mobile devices; only the Skill.md body is stored.");
    }

    private static void MarkReadOnly(string dir)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(path);
                File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // Read-only marking is best-effort; the DB remains the source of truth.
        }
    }

    private static void RemoveDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            // Clear read-only attributes so the delete can proceed.
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort - a stale folder never affects correctness because the marker + hash gate reuse.
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string Sanitize(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[segment.Length];
        for (var i = 0; i < segment.Length; i++)
            buffer[i] = Array.IndexOf(invalid, segment[i]) >= 0 ? '_' : segment[i];
        return new string(buffer);
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
