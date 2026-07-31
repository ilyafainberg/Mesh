using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Mesh 1.17 skill-package tests: the in-memory archive parser/security rules, the canonical content
/// hash, the normalized SQLCipher package tables (install/delete/dedupe/rollback), the desktop
/// immutable materialization cache, and the SkillMeta package/compat envelope roundtrip.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SkillPackageTests
{
    private string directory = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(AppContext.BaseDirectory, "skill-package-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        key = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            foreach (var f in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    // ---- zip helpers -------------------------------------------------------

    private sealed record Entry(string Path, byte[] Bytes, uint ExternalAttributes = 0);

    private static byte[] BuildZip(params Entry[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                var entry = zip.CreateEntry(e.Path, CompressionLevel.Optimal);
                if (e.ExternalAttributes != 0) entry.ExternalAttributes = (int)e.ExternalAttributes;
                using var s = entry.Open();
                s.Write(e.Bytes, 0, e.Bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static Entry Text(string path, string content, uint attrs = 0)
        => new(path, Encoding.UTF8.GetBytes(content), attrs);

    private static SkillCompatibility DesktopCompat()
        => new()
        {
            OperatingSystems = SkillOperatingSystems.AllDesktop,
            DeviceClass = SkillDeviceClass.Desktop,
            RequiredCliTools = new List<string>()
        };

    private static byte[] ValidPackageZip()
        => BuildZip(
            Text("Skill.md", "# Demo\nDo the thing."),
            Text("scripts/run.sh", "#!/bin/sh\necho hi\n"),
            Text("data/notes.txt", "some resource"));

    // ---- archive: happy path -----------------------------------------------

    [TestMethod]
    public void Parse_FullFolder_RoundtripsAllFilesWithRoles()
    {
        var content = SkillPackageArchive.Parse(ValidPackageZip(), DesktopCompat());

        Assert.AreEqual(3, content.Manifest.Files.Count);
        Assert.AreEqual("# Demo\nDo the thing.", content.SkillMarkdownText);

        var byPath = content.Manifest.Files.ToDictionary(f => f.Path, f => f.Role);
        Assert.AreEqual(SkillFileRole.SkillMarkdown, byPath["Skill.md"]);
        Assert.AreEqual(SkillFileRole.Script, byPath["scripts/run.sh"]);
        Assert.AreEqual(SkillFileRole.Resource, byPath["data/notes.txt"]);

        // Every file's declared hash matches its bytes.
        foreach (var f in content.Manifest.Files)
            Assert.AreEqual(Sha256Hex(content.Files[f.Path]), f.Sha256);
    }

    [TestMethod]
    public void CanonicalHash_IsStableAcrossRecompression()
    {
        var a = SkillPackageArchive.Parse(
            BuildZip(Text("Skill.md", "# X"), Text("a.txt", "hello")), DesktopCompat());
        // Same content, different entry order and (potentially) different compression.
        var b = SkillPackageArchive.Parse(
            BuildZip(Text("a.txt", "hello"), Text("Skill.md", "# X")), DesktopCompat());

        Assert.AreEqual(a.Manifest.PackageHash, b.Manifest.PackageHash);
    }

    [TestMethod]
    public void CanonicalHash_ChangesWhenContentChanges()
    {
        var a = SkillPackageArchive.Parse(BuildZip(Text("Skill.md", "# X")), DesktopCompat());
        var b = SkillPackageArchive.Parse(BuildZip(Text("Skill.md", "# Y")), DesktopCompat());
        Assert.AreNotEqual(a.Manifest.PackageHash, b.Manifest.PackageHash);
    }

    // ---- archive: security rejections --------------------------------------

    [TestMethod]
    public void Parse_Rejects_ZipSlipTraversal()
        => AssertRejected(BuildZip(Text("Skill.md", "# X"), Text("../evil.txt", "x")));

    [TestMethod]
    public void Parse_Rejects_AbsolutePath()
        => AssertRejected(BuildZip(Text("Skill.md", "# X"), Text("/etc/passwd", "x")));

    [TestMethod]
    public void Parse_Rejects_Symlink()
    {
        // S_IFLNK (0xA000) in the high 16 bits of external attributes marks a symlink.
        var attrs = 0xA000u << 16;
        AssertRejected(BuildZip(Text("Skill.md", "# X"), Text("link", "/target", attrs)));
    }

    [TestMethod]
    public void Parse_Rejects_CaseCollidingPaths()
        => AssertRejected(BuildZip(Text("Skill.md", "# X"), Text("A.txt", "1"), Text("a.txt", "2")));

    [TestMethod]
    public void Parse_Rejects_MissingSkillMd()
        => AssertRejected(BuildZip(Text("readme.txt", "no skill here")));

    [TestMethod]
    public void Parse_Rejects_MultipleSkillMd()
        => AssertRejected(BuildZip(Text("Skill.md", "# a"), Text("nested/skill.md", "# b")));

    [TestMethod]
    public void Parse_Rejects_TooManyFiles()
    {
        var entries = new List<Entry> { Text("Skill.md", "# X") };
        for (var i = 0; i < SkillPackageArchive.MaxFiles; i++)
            entries.Add(Text($"f{i}.txt", "x"));
        AssertRejected(BuildZip(entries.ToArray()));
    }

    [TestMethod]
    public void Parse_Rejects_OversizeSkillMd()
    {
        var big = new string('a', (int)SkillPackageArchive.MaxSkillMarkdownBytes + 16);
        AssertRejected(BuildZip(Text("Skill.md", "# " + big)));
    }

    [TestMethod]
    public void Parse_Rejects_CompressionBomb()
    {
        // A highly compressible entry whose declared size exceeds the per-file cap: the ratio guard or
        // the decompress cap must reject it.
        var bomb = new byte[SkillPackageArchive.MaxSingleEntryBytes + 1024];
        AssertRejected(BuildZip(Text("Skill.md", "# X"), new Entry("bomb.bin", bomb)));
    }

    [TestMethod]
    public void Parse_Rejects_InvalidUtf8SkillMd()
    {
        var invalid = new byte[] { 0xFF, 0xFE, 0x00, 0x9C };
        AssertRejected(BuildZip(new Entry("Skill.md", invalid)));
    }

    private static void AssertRejected(byte[] zip)
        => Assert.ThrowsException<SkillPackageValidationException>(
            () => SkillPackageArchive.Parse(zip, DesktopCompat()));

    // ---- FromSkillMarkdown (mobile / Skill.md-only) ------------------------

    [TestMethod]
    public void FromSkillMarkdown_ProducesSingleFilePackage()
    {
        var content = SkillPackageArchive.FromSkillMarkdown("# Only\ninstructions", DesktopCompat());
        Assert.AreEqual(1, content.Manifest.Files.Count);
        Assert.AreEqual(SkillFileRole.SkillMarkdown, content.Manifest.Files[0].Role);
        Assert.AreEqual("# Only\ninstructions", content.SkillMarkdownText);
    }

    // ---- DB: install / load / delete / dedupe ------------------------------

    [TestMethod]
    public void Db_InstallAndLoad_RoundtripsContent()
    {
        var dbPath = Path.Combine(directory, "p.meshdb");
        var content = SkillPackageArchive.Parse(ValidPackageZip(), DesktopCompat());

        using var db = MeshDb.Open(dbPath, key);
        db.InstallSkillPackage("skill-1", content.Manifest, content.Files);

        var loaded = db.LoadSkillPackageContent("skill-1", content.Manifest.PackageHash);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(content.Manifest.Files.Count, loaded!.Manifest.Files.Count);
        foreach (var f in content.Manifest.Files)
            CollectionAssert.AreEqual(content.Files[f.Path], loaded.Files[f.Path]);
    }

    [TestMethod]
    public void Db_GetManifest_RoundtripsCompatibilityAndFiles()
    {
        var dbPath = Path.Combine(directory, "p.meshdb");
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Windows | SkillOperatingSystems.Linux,
            DeviceClass = SkillDeviceClass.Desktop,
            RequiredCliTools = new List<string> { "git", "node" }
        };
        var content = SkillPackageArchive.Parse(ValidPackageZip(), compat, version: "1.2.3",
            source: "https://example/repo", trust: SkillPackageTrust.Community);

        using var db = MeshDb.Open(dbPath, key);
        db.InstallSkillPackage("skill-1", content.Manifest, content.Files);

        var manifest = db.GetSkillPackageManifest("skill-1", content.Manifest.PackageHash);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("1.2.3", manifest!.Version);
        Assert.AreEqual("https://example/repo", manifest.Source);
        Assert.AreEqual(SkillPackageTrust.Community, manifest.Trust);
        Assert.AreEqual(compat.OperatingSystems, manifest.Compatibility.OperatingSystems);
        Assert.AreEqual(SkillDeviceClass.Desktop, manifest.Compatibility.DeviceClass);
        CollectionAssert.AreEquivalent(
            new[] { "git", "node" }, manifest.Compatibility.RequiredCliTools.ToArray());
        Assert.AreEqual(content.Manifest.Files.Count, manifest.Files.Count);
    }

    [TestMethod]
    public void Db_Delete_RemovesRowsAndBlobs()
    {
        var dbPath = Path.Combine(directory, "p.meshdb");
        var content = SkillPackageArchive.Parse(ValidPackageZip(), DesktopCompat());

        using var db = MeshDb.Open(dbPath, key);
        db.InstallSkillPackage("skill-1", content.Manifest, content.Files);
        Assert.AreEqual(1, db.ListSkillPackageHashes("skill-1").Count);
        Assert.IsTrue(BlobCount(dbPath) > 0);

        db.DeleteSkillPackage("skill-1", content.Manifest.PackageHash);
        Assert.AreEqual(0, db.ListSkillPackageHashes("skill-1").Count);
        Assert.AreEqual(0, BlobCount(dbPath));
    }

    [TestMethod]
    public void Db_Dedupe_SharedBlobStoredOnce_AndRefcounted()
    {
        var dbPath = Path.Combine(directory, "p.meshdb");
        // Two skills whose Skill.md bytes are identical share one blob.
        var shared = SkillPackageArchive.FromSkillMarkdown("# Shared body", DesktopCompat());

        using var db = MeshDb.Open(dbPath, key);
        db.InstallSkillPackage("skill-a", shared.Manifest, shared.Files);
        db.InstallSkillPackage("skill-b", shared.Manifest, shared.Files);

        Assert.AreEqual(1, BlobCount(dbPath), "Identical content must be stored once.");

        // Deleting one keeps the shared blob alive for the other.
        db.DeleteSkillPackage("skill-a", shared.Manifest.PackageHash);
        Assert.AreEqual(1, BlobCount(dbPath));
        Assert.IsNotNull(db.LoadSkillPackageContent("skill-b", shared.Manifest.PackageHash));

        // Deleting the last reference removes the blob.
        db.DeleteSkillPackage("skill-b", shared.Manifest.PackageHash);
        Assert.AreEqual(0, BlobCount(dbPath));
    }

    [TestMethod]
    public void Db_Install_RollsBack_WhenContentFailsHashValidation()
    {
        var dbPath = Path.Combine(directory, "p.meshdb");
        var content = SkillPackageArchive.Parse(ValidPackageZip(), DesktopCompat());

        // Corrupt one file's bytes so it no longer matches the manifest hash.
        var tampered = content.Files.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var victim = content.Manifest.Files.First(f => f.Role != SkillFileRole.SkillMarkdown).Path;
        tampered[victim] = Encoding.UTF8.GetBytes("tampered bytes that do not match the hash");

        using var db = MeshDb.Open(dbPath, key);
        Assert.ThrowsException<InvalidOperationException>(
            () => db.InstallSkillPackage("skill-1", content.Manifest, tampered));

        // Nothing was written - the failed install left no rows and no blobs.
        Assert.AreEqual(0, db.ListSkillPackageHashes("skill-1").Count);
        Assert.AreEqual(0, BlobCount(dbPath));
    }

    private long BlobCount(string dbPath)
    {
        using var db = MeshDb.Open(dbPath, key);
        return db.CountSkillPackageBlobsForTest();
    }

    // ---- desktop cache: materialize / immutable / hash-validate ------------

    [TestMethod]
    public void Cache_Desktop_Materializes_ReadOnly_Immutable()
    {
        var content = SkillPackageArchive.Parse(ValidPackageZip(), DesktopCompat());
        var cache = new SkillPackageCache(Path.Combine(directory, "cache"), isMobile: false);

        var dir = cache.Materialize("skill-1", content);
        Assert.IsTrue(cache.IsMaterialized("skill-1", content.Manifest.PackageHash));

        // Every file present with correct bytes.
        foreach (var f in content.Manifest.Files)
        {
            var target = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(target));
            CollectionAssert.AreEqual(content.Files[f.Path], File.ReadAllBytes(target));
            Assert.IsTrue(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly),
                $"{f.Path} should be read-only.");
        }

        // Idempotent: a second materialize returns the same path without rewriting.
        Assert.AreEqual(dir, cache.Materialize("skill-1", content));
    }

    [TestMethod]
    public void Cache_Desktop_RemoveAll_Cleans()
    {
        var content = SkillPackageArchive.Parse(ValidPackageZip(), DesktopCompat());
        var cache = new SkillPackageCache(Path.Combine(directory, "cache"), isMobile: false);

        cache.Materialize("skill-1", content);
        cache.RemoveAll("skill-1");
        Assert.IsFalse(cache.IsMaterialized("skill-1", content.Manifest.PackageHash));
    }

    [TestMethod]
    public void Cache_Mobile_Throws_BeforeAnyWrite()
    {
        var content = SkillPackageArchive.FromSkillMarkdown("# body", DesktopCompat());
        var cacheDir = Path.Combine(directory, "cache");
        var cache = new SkillPackageCache(cacheDir, isMobile: true);

        Assert.ThrowsException<PlatformNotSupportedException>(
            () => cache.Materialize("skill-1", content));
        Assert.IsFalse(Directory.Exists(cacheDir), "Mobile must never touch the filesystem.");
    }

    // ---- SkillMeta envelope roundtrip --------------------------------------

    [TestMethod]
    public void SkillMeta_Roundtrips_CompatAndPackageFields()
    {
        var skill = new Skill
        {
            Id = "s1",
            Name = "Demo",
            Description = "d",
            Instructions = "# body",
            PackageHash = "hash-123",
            PackageVersion = "2.0.0",
            Compatibility = new SkillCompatibility
            {
                OperatingSystems = SkillOperatingSystems.Windows | SkillOperatingSystems.MacOS,
                DeviceClass = SkillDeviceClass.Desktop,
                RequiredCliTools = new List<string> { "git" }
            }
        };

        var (record, _) = AssetPersistenceModels.ToRecord(skill, "device-1", localOnly: false, version: 1);
        var restored = AssetPersistenceModels.ToSkill(record, Encoding.UTF8.GetBytes("# body"));

        Assert.AreEqual("hash-123", restored.PackageHash);
        Assert.AreEqual("2.0.0", restored.PackageVersion);
        Assert.IsNotNull(restored.Compatibility);
        Assert.AreEqual(skill.Compatibility.OperatingSystems, restored.Compatibility!.OperatingSystems);
        Assert.AreEqual(SkillDeviceClass.Desktop, restored.Compatibility.DeviceClass);
        CollectionAssert.AreEqual(new[] { "git" }, restored.Compatibility.RequiredCliTools.ToArray());
    }

    [TestMethod]
    public void SkillMeta_Legacy_NoPackageFields_DeserializesToNulls()
    {
        var skill = new Skill { Id = "s1", Name = "Legacy", Instructions = "# body" };
        var (record, _) = AssetPersistenceModels.ToRecord(skill, "device-1", localOnly: false, version: 1);
        var restored = AssetPersistenceModels.ToSkill(record, Encoding.UTF8.GetBytes("# body"));

        Assert.IsNull(restored.PackageHash);
        Assert.IsNull(restored.PackageVersion);
        Assert.IsNull(restored.Compatibility);
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
