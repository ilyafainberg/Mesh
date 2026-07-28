using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Mesh.App.Tests;

[TestClass]
public sealed class UpdatePackageCacheTests
{
    [TestMethod]
    public void SanitizeTag_ReplacesUnsafeCharacters()
    {
        Assert.AreEqual("v1.16.0_beta", UpdatePackageCache.SanitizeTag("v1.16.0/beta"));
        Assert.AreEqual("latest", UpdatePackageCache.SanitizeTag("///"));
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsPreparedInstaller()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var release = UpdatePackageCache.GetReleaseDirectory(root, "v1.16.0");
            Directory.CreateDirectory(release);
            var installer = Path.Combine(release, "Mesh-Setup-v1.16.0.exe");
            await File.WriteAllTextAsync(installer, "signed-installer-placeholder");
            var descriptor = new UpdatePackageDescriptor(
                "v1.16.0", "Mesh-Setup-v1.16.0.zip", "https://example.test/update.zip", 1234);

            var saved = await UpdatePackageCache.SaveAsync(release, descriptor, installer, CancellationToken.None);
            var loaded = await UpdatePackageCache.TryLoadAsync(release, descriptor, CancellationToken.None);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(saved.InstallerPath, loaded.InstallerPath);
            Assert.AreEqual(saved.Sha256, loaded.Sha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryLoad_RejectsInstallerChangedAfterPreparation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var release = UpdatePackageCache.GetReleaseDirectory(root, "v1.16.0");
            Directory.CreateDirectory(release);
            var installer = Path.Combine(release, "Mesh-Setup-v1.16.0.exe");
            await File.WriteAllTextAsync(installer, "original");
            var descriptor = new UpdatePackageDescriptor(
                "v1.16.0", "Mesh-Setup-v1.16.0.zip", "https://example.test/update.zip", 1234);
            await UpdatePackageCache.SaveAsync(release, descriptor, installer, CancellationToken.None);

            await File.WriteAllTextAsync(installer, "changed");

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => UpdatePackageCache.TryLoadAsync(release, descriptor, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryLoad_DifferentReleaseDescriptorDoesNotReuseCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var release = UpdatePackageCache.GetReleaseDirectory(root, "v1.16.0");
            Directory.CreateDirectory(release);
            var installer = Path.Combine(release, "Mesh-Setup-v1.16.0.exe");
            await File.WriteAllTextAsync(installer, "original");
            var descriptor = new UpdatePackageDescriptor(
                "v1.16.0", "Mesh-Setup-v1.16.0.zip", "https://example.test/update.zip", 1234);
            await UpdatePackageCache.SaveAsync(release, descriptor, installer, CancellationToken.None);

            var different = descriptor with { DownloadUrl = "https://example.test/changed.zip" };
            Assert.IsNull(await UpdatePackageCache.TryLoadAsync(release, different, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryLoad_RejectsInstallerPathOutsideReleaseDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var release = UpdatePackageCache.GetReleaseDirectory(root, "v1.16.0");
            Directory.CreateDirectory(release);
            var outsideInstaller = Path.Combine(root, "outside.exe");
            await File.WriteAllTextAsync(outsideInstaller, "outside");
            var descriptor = new UpdatePackageDescriptor(
                "v1.16.0", "Mesh-Setup-v1.16.0.zip", "https://example.test/update.zip", 1234);
            var manifest = JsonSerializer.Serialize(new
            {
                descriptor.TagName,
                descriptor.AssetName,
                descriptor.DownloadUrl,
                descriptor.AssetSize,
                InstallerFile = "..\\outside.exe",
                Sha256 = new string('0', 64)
            });
            await File.WriteAllTextAsync(Path.Combine(release, "ready.json"), manifest);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => UpdatePackageCache.TryLoadAsync(release, descriptor, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryLoad_RejectsManifestWithoutInstallerPath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var release = UpdatePackageCache.GetReleaseDirectory(root, "v1.16.0");
            Directory.CreateDirectory(release);
            var descriptor = new UpdatePackageDescriptor(
                "v1.16.0", "Mesh-Setup-v1.16.0.zip", "https://example.test/update.zip", 1234);
            var manifest = JsonSerializer.Serialize(new
            {
                descriptor.TagName,
                descriptor.AssetName,
                descriptor.DownloadUrl,
                descriptor.AssetSize,
                Sha256 = new string('0', 64)
            });
            await File.WriteAllTextAsync(Path.Combine(release, "ready.json"), manifest);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => UpdatePackageCache.TryLoadAsync(release, descriptor, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryLoad_RejectsMalformedInstallerPath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var release = UpdatePackageCache.GetReleaseDirectory(root, "v1.16.0");
            Directory.CreateDirectory(release);
            var descriptor = new UpdatePackageDescriptor(
                "v1.16.0", "Mesh-Setup-v1.16.0.zip", "https://example.test/update.zip", 1234);
            var manifest = JsonSerializer.Serialize(new
            {
                descriptor.TagName,
                descriptor.AssetName,
                descriptor.DownloadUrl,
                descriptor.AssetSize,
                InstallerFile = "bad\0name.exe",
                Sha256 = new string('0', 64)
            });
            await File.WriteAllTextAsync(Path.Combine(release, "ready.json"), manifest);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => UpdatePackageCache.TryLoadAsync(release, descriptor, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MeshUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
