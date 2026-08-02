using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class WindowsPlaywrightReleaseTests
{
    [TestMethod]
    public void Project_Prunes_NonWindows_Playwright_Drivers()
    {
        var project = ReadSource("src", "Mesh.App", "Mesh.App.csproj");

        StringAssert.Contains(project, "PruneNonWindowsPlaywrightDrivers");
        StringAssert.Contains(project, @"$(PublishDir).playwright\node\darwin-arm64");
        StringAssert.Contains(project, @"$(PublishDir).playwright\node\darwin-x64");
        StringAssert.Contains(project, @"$(PublishDir).playwright\node\linux-arm64");
        StringAssert.Contains(project, @"$(PublishDir).playwright\node\linux-x64");
        Assert.IsFalse(
            project.Contains(
                @"<NonWindowsPlaywrightDriver Include=""$(PublishDir).playwright\node\win32_x64""",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReleaseScript_Rejects_Foreign_Playwright_Drivers()
    {
        var release = ReadSource("_deploy", "release.ps1");

        StringAssert.Contains(release, @"Where-Object { $_.Name -ne ""win32_x64"" }");
        StringAssert.Contains(release, "Windows publish contains non-Windows Playwright drivers");
        StringAssert.Contains(release, @"win32_x64\node.exe");
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
