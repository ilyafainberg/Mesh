using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MobileOverlayStateTests
{
    [TestMethod]
    public void SetActive_TracksIndependentOwnersAndOnlyRaisesStateTransitions()
    {
        var state = new MobileOverlayState();
        var first = new object();
        var second = new object();
        var changes = 0;
        state.Changed += () => changes++;

        state.SetActive(first, true);
        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, changes);

        state.SetActive(first, true);
        state.SetActive(second, true);
        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, changes);

        state.SetActive(first, false);
        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, changes);

        state.SetActive(second, false);
        Assert.IsFalse(state.IsOpen);
        Assert.AreEqual(2, changes);

        state.SetActive(second, false);
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void SetActive_RejectsNullOwner()
    {
        var state = new MobileOverlayState();
        Assert.ThrowsExactly<ArgumentNullException>(() => state.SetActive(null!, true));
    }

    [TestMethod]
    public void FlyUpSurfaces_UseSharedMobileOverlayContract()
    {
        var shell = ReadRepoFile("src", "Mesh.App", "Components", "Mobile", "MobileShell.razor");
        var shellCss = ReadRepoFile("src", "Mesh.App", "Components", "Mobile", "MobileShell.razor.css");
        var mobileMe = ReadRepoFile("src", "Mesh.App", "Components", "Mobile", "MobileMe.razor");
        var audiencePicker = ReadRepoFile("src", "Mesh.App", "Components", "AudiencePicker.razor");

        StringAssert.Contains(shell, "MobileOverlays.IsOpen ? \"mobile-overlay-open\"");
        StringAssert.Contains(shellCss, ".m-shell.mobile-overlay-open .m-tabbar");
        StringAssert.Contains(shellCss, "display: none;");
        StringAssert.Contains(shellCss, ".m-shell.mobile-overlay-open .m-body");
        StringAssert.Contains(shellCss, "-webkit-overflow-scrolling: auto;");
        StringAssert.Contains(mobileMe,
            "<MobileOverlayScope Active=\"@(newDeviceMenuOpen || moveMenuOpen || pendingMove is not null)\" />");
        StringAssert.Contains(audiencePicker, "<MobileOverlayScope Active=\"@open\" />");
    }

    [TestMethod]
    public void MobileShell_HasNoQuitActionWhileDesktopNavigationRetainsQuit()
    {
        var mobileShell = ReadRepoFile("src", "Mesh.App", "Components", "Mobile", "MobileShell.razor");
        var desktopNav = ReadRepoFile("src", "Mesh.App", "Components", "Layout", "NavMenu.razor");

        Assert.IsFalse(mobileShell.Contains("Quit", StringComparison.Ordinal));
        Assert.IsFalse(mobileShell.Contains("IAppControl", StringComparison.Ordinal));
        StringAssert.Contains(desktopNav, "@onclick=\"Quit\"");
        StringAssert.Contains(desktopNav, "AppControl.Quit();");
    }

    static string ReadRepoFile(params string[] parts)
    {
        var relativePath = Path.Combine(parts);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file {relativePath}.");
    }
}
