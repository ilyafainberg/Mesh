using Mesh.Updater;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class UpdateOptionsTests
{
    [TestMethod]
    public void TryParse_ValidCommand_ReturnsAllValues()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var installer = Path.Combine(root, "Mesh-Setup-v1.16.0.exe");
            var meshExe = Path.Combine(root, "Mesh.App.exe");
            File.WriteAllText(installer, "installer");
            File.WriteAllText(meshExe, "mesh");
            var hash = new string('a', 64);

            var ok = UpdateOptions.TryParse(new[]
            {
                "--installer", installer,
                "--mesh-exe", meshExe,
                "--cleanup-dir", root,
                "--sha256", hash,
                "--version", "1.16.0",
                "--quit-event", @"Local\MeshUpdateQuit-0123456789abcdef0123456789abcdef",
                "--mesh-pid", "42",
                "--mesh-start-ticks", "638000000000000000"
            }, out var options, out var error);

            Assert.IsTrue(ok, error);
            Assert.IsNotNull(options);
            Assert.AreEqual(Path.GetFullPath(installer), options.InstallerPath);
            Assert.AreEqual(Path.GetFullPath(meshExe), options.MeshExePath);
            Assert.AreEqual(Path.GetFullPath(root), options.CleanupDirectory);
            Assert.AreEqual(hash, options.ExpectedSha256);
            Assert.AreEqual(42, options.MeshProcessId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TryParse_MissingValue_IsRejected()
    {
        Assert.IsFalse(UpdateOptions.TryParse(new[] { "--installer" }, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void TryParse_InvalidHash_IsRejected()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var installer = Path.Combine(root, "Mesh-Setup.exe");
            var meshExe = Path.Combine(root, "Mesh.App.exe");
            File.WriteAllText(installer, "installer");
            File.WriteAllText(meshExe, "mesh");

            Assert.IsFalse(UpdateOptions.TryParse(new[]
            {
                "--installer", installer,
                "--mesh-exe", meshExe,
                "--cleanup-dir", root,
                "--sha256", "not-a-hash",
                "--version", "1.16.0",
                "--quit-event", @"Local\MeshUpdateQuit-0123456789abcdef0123456789abcdef",
                "--mesh-pid", "42",
                "--mesh-start-ticks", "638000000000000000"
            }, out _, out var error));
            StringAssert.Contains(error, "checksum");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TryParse_CleanupDirectoryOutsideInstaller_IsRejected()
    {
        var root = CreateTemporaryDirectory();
        var other = CreateTemporaryDirectory();
        try
        {
            var installer = Path.Combine(root, "Mesh-Setup.exe");
            var meshExe = Path.Combine(root, "Mesh.App.exe");
            File.WriteAllText(installer, "installer");
            File.WriteAllText(meshExe, "mesh");

            Assert.IsFalse(UpdateOptions.TryParse(new[]
            {
                "--installer", installer,
                "--mesh-exe", meshExe,
                "--cleanup-dir", other,
                "--sha256", new string('a', 64),
                "--version", "1.16.0",
                "--quit-event", @"Local\MeshUpdateQuit-0123456789abcdef0123456789abcdef",
                "--mesh-pid", "42",
                "--mesh-start-ticks", "638000000000000000"
            }, out _, out var error));
            StringAssert.Contains(error, "does not contain");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }
    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MeshUpdaterOptionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
