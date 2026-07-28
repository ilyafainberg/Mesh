using System.Security.Cryptography;
using System.Text.Json;

namespace Mesh.App.Services;

internal sealed record UpdatePackageDescriptor(
    string TagName,
    string AssetName,
    string DownloadUrl,
    long AssetSize);

internal sealed record PreparedUpdate(string InstallerPath, string Sha256);

internal static class UpdatePackageCache
{
    private const string ManifestFileName = "ready.json";

    private sealed record CacheManifest(
        string TagName,
        string AssetName,
        string DownloadUrl,
        long AssetSize,
        string InstallerFile,
        string Sha256);

    public static string DefaultBaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mesh", "Updates");

    public static string GetReleaseDirectory(string baseDirectory, string tagName)
        => Path.Combine(baseDirectory, SanitizeTag(tagName));

    public static async Task<PreparedUpdate?> TryLoadAsync(
        string releaseDirectory,
        UpdatePackageDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(releaseDirectory, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;

        CacheManifest? manifest;
        try
        {
            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<CacheManifest>(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The cached update manifest is invalid.", ex);
        }

        if (manifest is null
            || !string.Equals(manifest.TagName, descriptor.TagName, StringComparison.Ordinal)
            || !string.Equals(manifest.AssetName, descriptor.AssetName, StringComparison.Ordinal)
            || !string.Equals(manifest.DownloadUrl, descriptor.DownloadUrl, StringComparison.Ordinal)
            || manifest.AssetSize != descriptor.AssetSize)
            return null;

        if (string.IsNullOrWhiteSpace(manifest.InstallerFile)
            || string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The cached update manifest is incomplete.");


        var installerPath = ResolveContainedPath(releaseDirectory, manifest.InstallerFile);
        if (!File.Exists(installerPath))
            throw new InvalidDataException("The cached update installer is missing.");

        var actualHash = await ComputeSha256Async(installerPath, cancellationToken);
        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cached update installer checksum is invalid.");

        return new PreparedUpdate(installerPath, actualHash);
    }

    public static async Task<PreparedUpdate> SaveAsync(
        string releaseDirectory,
        UpdatePackageDescriptor descriptor,
        string installerPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(releaseDirectory);
        var relativeInstaller = Path.GetRelativePath(releaseDirectory, installerPath);
        var fullInstallerPath = ResolveContainedPath(releaseDirectory, relativeInstaller);
        if (!File.Exists(fullInstallerPath))
            throw new FileNotFoundException("The prepared update installer is missing.", fullInstallerPath);

        var sha256 = await ComputeSha256Async(fullInstallerPath, cancellationToken);
        var manifest = new CacheManifest(
            descriptor.TagName,
            descriptor.AssetName,
            descriptor.DownloadUrl,
            descriptor.AssetSize,
            relativeInstaller,
            sha256);

        var manifestPath = Path.Combine(releaseDirectory, ManifestFileName);
        var temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, manifestPath, overwrite: true);
        return new PreparedUpdate(fullInstallerPath, sha256);
    }

    public static string SanitizeTag(string tagName)
    {
        var cleaned = new string(tagName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) || cleaned.All(c => c is '.' or '-' or '_') ? "latest" : cleaned;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveContainedPath(string rootDirectory, string relativePath)
    {
        try
        {
            if (Path.IsPathRooted(relativePath))
                throw new InvalidDataException("The cached update contains an absolute path.");

            var root = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The cached update path leaves its release directory.");
            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The cached update path is invalid.", ex);
        }
    }
}
