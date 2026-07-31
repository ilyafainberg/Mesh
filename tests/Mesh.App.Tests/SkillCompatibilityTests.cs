using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Device-compatibility matrix tests for the Mesh 1.17 <see cref="SkillCompatibilityChecker"/>. All
/// checks are pure and use an injected CLI probe, so they never touch the filesystem or a shell.
/// </summary>
[TestClass]
public sealed class SkillCompatibilityTests
{
    private sealed class FixedProbe : ICliToolProbe
    {
        private readonly HashSet<string> _available;
        public FixedProbe(params string[] available)
            => _available = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable(string tool) => _available.Contains(tool);
    }

    private static SkillCompatibility Compat(
        SkillOperatingSystems os, SkillDeviceClass cls, params string[] cli)
        => new()
        {
            OperatingSystems = os,
            DeviceClass = cls,
            RequiredCliTools = cli.ToList()
        };

    // ---- null / universal --------------------------------------------------

    [TestMethod]
    public void NullCompatibility_IsAlwaysCompatible_OnDesktopAndMobile()
    {
        var desktop = new SkillCompatibilityChecker(SkillOperatingSystems.Windows, isMobile: false);
        var mobile = new SkillCompatibilityChecker(SkillOperatingSystems.IOS, isMobile: true);

        Assert.IsTrue(desktop.Check(null).IsCompatible);
        Assert.IsTrue(mobile.Check(null).IsCompatible);
    }

    // ---- operating-system matrix -------------------------------------------

    [TestMethod]
    public void Windows_Supported_WhenDeclared()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.Windows, isMobile: false);
        var result = checker.Check(Compat(SkillOperatingSystems.AllDesktop, SkillDeviceClass.Desktop));
        Assert.AreEqual(SkillCompatibilityLevel.Compatible, result.Level);
    }

    [TestMethod]
    public void Windows_Incompatible_WhenOnlyLinuxDeclared()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.Windows, isMobile: false);
        var result = checker.Check(Compat(SkillOperatingSystems.Linux, SkillDeviceClass.Desktop));
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Level);
        Assert.IsFalse(result.CanInstall);
        Assert.IsTrue(result.Reasons.Count > 0);
    }

    [TestMethod]
    public void MacOs_Supported_WhenDeclared()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.MacOS, isMobile: false);
        var result = checker.Check(Compat(SkillOperatingSystems.MacOS, SkillDeviceClass.Desktop));
        Assert.IsTrue(result.IsCompatible);
    }

    // ---- device-class matrix -----------------------------------------------

    [TestMethod]
    public void Mobile_RejectsDesktopDeviceClass()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.Android, isMobile: true);
        var result = checker.Check(Compat(SkillOperatingSystems.All, SkillDeviceClass.Desktop));
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Level);
    }

    [TestMethod]
    public void Mobile_RejectsAnyRequiredCli()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.IOS, isMobile: true);
        var result = checker.Check(Compat(SkillOperatingSystems.All, SkillDeviceClass.Universal, "git"));
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Level);
    }

    [TestMethod]
    public void Mobile_AcceptsUniversalSkillMdOnly()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.IOS, isMobile: true);
        var result = checker.Check(Compat(SkillOperatingSystems.All, SkillDeviceClass.Universal));
        Assert.IsTrue(result.IsCompatible);
    }

    [TestMethod]
    public void Desktop_RejectsMobileOnlyDeviceClass()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.Windows, isMobile: false);
        var result = checker.Check(Compat(SkillOperatingSystems.All, SkillDeviceClass.Mobile));
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Level);
    }

    // ---- CLI probe ---------------------------------------------------------

    [TestMethod]
    public void Desktop_MissingCli_Warns_InstallableButNotRunnable()
    {
        var checker = new SkillCompatibilityChecker(
            SkillOperatingSystems.Windows, isMobile: false, new FixedProbe(/* nothing */));
        var result = checker.Check(Compat(SkillOperatingSystems.Windows, SkillDeviceClass.Desktop, "git", "node"));

        Assert.AreEqual(SkillCompatibilityLevel.Warning, result.Level);
        Assert.IsTrue(result.CanInstall);
        Assert.IsFalse(result.CanRun);
        CollectionAssert.AreEquivalent(new[] { "git", "node" }, result.MissingCliTools.ToArray());
    }

    [TestMethod]
    public void Desktop_PresentCli_IsCompatible()
    {
        var checker = new SkillCompatibilityChecker(
            SkillOperatingSystems.Windows, isMobile: false, new FixedProbe("git", "node"));
        var result = checker.Check(Compat(SkillOperatingSystems.Windows, SkillDeviceClass.Desktop, "git", "node"));

        Assert.AreEqual(SkillCompatibilityLevel.Compatible, result.Level);
        Assert.AreEqual(0, result.MissingCliTools.Count);
    }

    [TestMethod]
    public void Desktop_PartialCli_ReportsOnlyMissing()
    {
        var checker = new SkillCompatibilityChecker(
            SkillOperatingSystems.Linux, isMobile: false, new FixedProbe("git"));
        var result = checker.Check(Compat(SkillOperatingSystems.Linux, SkillDeviceClass.Desktop, "git", "docker"));

        Assert.AreEqual(SkillCompatibilityLevel.Warning, result.Level);
        CollectionAssert.AreEqual(new[] { "docker" }, result.MissingCliTools.ToArray());
    }

    // ---- package-level checks ----------------------------------------------

    [TestMethod]
    public void CheckPackage_Mobile_RejectsSupportingFiles()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.Android, isMobile: true);
        var manifest = new SkillPackageManifest
        {
            Compatibility = Compat(SkillOperatingSystems.All, SkillDeviceClass.Universal),
            Files = new List<SkillFileManifest>
            {
                new() { Path = "Skill.md", Role = SkillFileRole.SkillMarkdown, Sha256 = "a", Size = 1 },
                new() { Path = "run.sh", Role = SkillFileRole.Script, Sha256 = "b", Size = 1 }
            }
        };

        var result = checker.CheckPackage(manifest);
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Level);
    }

    [TestMethod]
    public void CheckPackage_Mobile_AcceptsSkillMdOnly()
    {
        var checker = new SkillCompatibilityChecker(SkillOperatingSystems.IOS, isMobile: true);
        var manifest = new SkillPackageManifest
        {
            Compatibility = Compat(SkillOperatingSystems.All, SkillDeviceClass.Universal),
            Files = new List<SkillFileManifest>
            {
                new() { Path = "Skill.md", Role = SkillFileRole.SkillMarkdown, Sha256 = "a", Size = 1 }
            }
        };

        Assert.IsTrue(checker.CheckPackage(manifest).IsCompatible);
    }

    [TestMethod]
    public void CheckPackage_Desktop_AllowsScriptsWithPresentCli()
    {
        var checker = new SkillCompatibilityChecker(
            SkillOperatingSystems.Windows, isMobile: false, new FixedProbe("python"));
        var manifest = new SkillPackageManifest
        {
            Compatibility = Compat(SkillOperatingSystems.Windows, SkillDeviceClass.Desktop, "python"),
            Files = new List<SkillFileManifest>
            {
                new() { Path = "Skill.md", Role = SkillFileRole.SkillMarkdown, Sha256 = "a", Size = 1 },
                new() { Path = "run.py", Role = SkillFileRole.Script, Sha256 = "b", Size = 1 }
            }
        };

        Assert.AreEqual(SkillCompatibilityLevel.Compatible, checker.CheckPackage(manifest).Level);
    }
}
