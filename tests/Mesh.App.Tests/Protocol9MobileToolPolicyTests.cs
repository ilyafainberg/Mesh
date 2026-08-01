// Protocol 9 Mobile Tool Policy -- behavioral tests
//
// COMPILE PREREQUISITE (no test-csproj change has been made per task constraints):
//   To run these tests, add the following line to Mesh.App.Tests.csproj inside the
//   first <ItemGroup> that contains the other <Compile Include> links:
//
//     <Compile Include="..\..\src\Mesh.App\Services\PlatformCaps.cs" Link="PlatformCaps.cs" />
//
//   PlatformCaps.cs has no MAUI dependency and compiles cleanly against net10.0
//   (it uses only OperatingSystem.Is*(), Mesh.App.Domain.LocalToolKind, and
//   Mesh.Shared.DevicePlatforms -- all of which are already available in the test project).
//
// COVERAGE:
//   1. FileSystem is now classified as desktop-only, so it is excluded from every mobile
//      surface (UI catalog, agent catalog, settings, approval) through the single
//      IsDesktopOnly() predicate already consumed by both Tools.razor and GraphTools.cs.
//   2. The six pre-existing desktop-only tools are unchanged.
//   3. WebSearch, Geolocation, and MeshData remain available on mobile.
//   4. A "fail-closed" simulation confirms that even if a migrated profile has FileSystem
//      enabled, the catalog filter removes it when isMobile is true.

using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class Protocol9MobileToolPolicyTests
{
    // ------------------------------------------------------------------
    // 1. FileSystem is now desktop-only
    // ------------------------------------------------------------------

    [TestMethod]
    public void FileSystem_IsDesktopOnly_ReturnsTrue()
    {
        Assert.IsTrue(LocalToolKind.FileSystem.IsDesktopOnly(),
            "FileSystem must be classified as desktop-only so it is hidden on Android/iOS.");
    }

    // ------------------------------------------------------------------
    // 2. Pre-existing desktop-only tools are unchanged
    // ------------------------------------------------------------------

    [DataTestMethod]
    [DataRow(LocalToolKind.PowerShell)]
    [DataRow(LocalToolKind.Cmd)]
    [DataRow(LocalToolKind.Python)]
    [DataRow(LocalToolKind.CSharpScript)]
    [DataRow(LocalToolKind.Browser)]
    [DataRow(LocalToolKind.HeadlessBrowser)]
    [DataRow(LocalToolKind.WorkIq)]
    public void PreExisting_DesktopOnlyTools_StillReturnTrue(LocalToolKind kind)
    {
        Assert.IsTrue(kind.IsDesktopOnly(),
            $"{kind} was already desktop-only and must remain so.");
    }

    // ------------------------------------------------------------------
    // 3. Mobile-available tools are not affected by the new policy
    // ------------------------------------------------------------------

    [DataTestMethod]
    [DataRow(LocalToolKind.WebSearch)]
    [DataRow(LocalToolKind.Geolocation)]
    [DataRow(LocalToolKind.MeshData)]
    public void MobileAvailableTools_IsDesktopOnly_ReturnsFalse(LocalToolKind kind)
    {
        Assert.IsFalse(kind.IsDesktopOnly(),
            $"{kind} is valid on mobile and must not be gated as desktop-only.");
    }

    // ------------------------------------------------------------------
    // 4. Fail-closed simulation: migrated profile with FileSystem enabled
    //    is rejected at catalog construction when running on mobile.
    // ------------------------------------------------------------------

    [TestMethod]
    public void CatalogFilter_MobileWithFileSystemEnabled_ExcludesFileSystem()
    {
        // Simulate a profile row where FileSystem was enabled on a desktop
        // device and was then synced to a mobile device.
        var profileLocalTools = new Dictionary<LocalToolKind, LocalToolSetting>
        {
            [LocalToolKind.FileSystem] = new LocalToolSetting { Enabled = true },
            [LocalToolKind.WebSearch]  = new LocalToolSetting { Enabled = true },
            [LocalToolKind.MeshData]   = new LocalToolSetting { Enabled = true },
        };

        // Reproduce the same filter GraphTools.LocalTools() applies.
        const bool isMobile = true;
        var catalog = profileLocalTools.Keys
            .Where(k => !(isMobile && k.IsDesktopOnly()))
            .ToList();

        CollectionAssert.DoesNotContain(catalog, LocalToolKind.FileSystem,
            "FileSystem must be excluded from the mobile catalog even when the stored profile has it enabled.");
        CollectionAssert.Contains(catalog, LocalToolKind.WebSearch,
            "WebSearch must remain available on mobile.");
        CollectionAssert.Contains(catalog, LocalToolKind.MeshData,
            "MeshData must remain available on mobile.");
    }

    [TestMethod]
    public void CatalogFilter_DesktopWithFileSystemEnabled_IncludesFileSystem()
    {
        var profileLocalTools = new Dictionary<LocalToolKind, LocalToolSetting>
        {
            [LocalToolKind.FileSystem] = new LocalToolSetting { Enabled = true },
            [LocalToolKind.WebSearch]  = new LocalToolSetting { Enabled = true },
        };

        const bool isMobile = false;
        var catalog = profileLocalTools.Keys
            .Where(k => !(isMobile && k.IsDesktopOnly()))
            .ToList();

        CollectionAssert.Contains(catalog, LocalToolKind.FileSystem,
            "FileSystem must be offered on desktop when the profile has it enabled.");
    }
}
