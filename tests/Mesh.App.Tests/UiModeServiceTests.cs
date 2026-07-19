using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Services;

namespace Mesh.App.Tests;

/// <summary>
/// Unit tests for UiModeParser.ResolveFromWidth and UiModeService. Covers breakpoint
/// resolution, forced-mode immutability under resize, and switching back to Auto.
/// </summary>
[TestClass]
public class UiModeServiceTests
{
    // -----------------------------------------------------------------------
    // ResolveFromWidth - boundary tests
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Resolve_WidthZero_NonMobile_ReturnsDesktop()
        => Assert.AreEqual(UiMode.Desktop, UiModeParser.ResolveFromWidth(0, isMobilePlatform: false));

    [TestMethod]
    public void Resolve_WidthZero_Mobile_ReturnsPhone()
        => Assert.AreEqual(UiMode.Phone, UiModeParser.ResolveFromWidth(0, isMobilePlatform: true));

    [TestMethod]
    public void Resolve_Width1100_ReturnsPhone()
        => Assert.AreEqual(UiMode.Phone, UiModeParser.ResolveFromWidth(1100, isMobilePlatform: false));

    [TestMethod]
    public void Resolve_Width1101_ReturnsDesktop()
        => Assert.AreEqual(UiMode.Desktop, UiModeParser.ResolveFromWidth(1101, isMobilePlatform: false));

    [TestMethod]
    public void Resolve_Width1099_ReturnsPhone()
        => Assert.AreEqual(UiMode.Phone, UiModeParser.ResolveFromWidth(1099, isMobilePlatform: false));

    [TestMethod]
    public void Resolve_LargeWidth_ReturnsDesktop()
        => Assert.AreEqual(UiMode.Desktop, UiModeParser.ResolveFromWidth(1920, isMobilePlatform: false));

    [TestMethod]
    public void Resolve_Width300_ReturnsPhone()
        => Assert.AreEqual(UiMode.Phone, UiModeParser.ResolveFromWidth(300, isMobilePlatform: false));

    // -----------------------------------------------------------------------
    // UiModeService - initial state
    // -----------------------------------------------------------------------

    private static UiModeService CreateService(UiModeParseResult options)
        => new(NullLogger<UiModeService>.Instance, options);

    [TestMethod]
    public void Service_NoFlag_InitializesAutoDefault()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        Assert.AreEqual(UiMode.Auto, svc.RequestedMode);
        Assert.AreEqual(UiModeSource.Default, svc.Source);
        Assert.IsFalse(svc.IsForced);
    }

    [TestMethod]
    public void Service_ForcedDesktop_InitializesDesktopCommandLine()
    {
        var svc = CreateService(new(UiMode.Desktop, UiModeSource.CommandLine, false));
        Assert.AreEqual(UiMode.Desktop, svc.RequestedMode);
        Assert.AreEqual(UiMode.Desktop, svc.EffectiveMode);
        Assert.AreEqual(UiModeSource.CommandLine, svc.Source);
        Assert.IsTrue(svc.IsForced);
    }

    // -----------------------------------------------------------------------
    // UiModeService - forced modes are unaffected by UpdateWindowSize
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ForcedDesktop_UpdateWindowSize_EffectiveModeUnchanged()
    {
        var svc = CreateService(new(UiMode.Desktop, UiModeSource.CommandLine, false));
        svc.UpdateWindowSize(320, 600); // phone-sized window
        Assert.AreEqual(UiMode.Desktop, svc.EffectiveMode);
        Assert.AreEqual(320, svc.CurrentWindowWidth);
    }

    [TestMethod]
    public void ForcedPhone_UpdateWindowSize_EffectiveModeUnchanged()
    {
        var svc = CreateService(new(UiMode.Phone, UiModeSource.CommandLine, false));
        svc.UpdateWindowSize(2560, 1440); // large desktop window
        Assert.AreEqual(UiMode.Phone, svc.EffectiveMode);
    }

    [TestMethod]
    public void ForcedMode_StoresDimensionsEvenThoughModeIsUnchanged()
    {
        var svc = CreateService(new(UiMode.Desktop, UiModeSource.CommandLine, false));
        svc.UpdateWindowSize(360, 800);
        Assert.AreEqual(360, svc.CurrentWindowWidth);
        Assert.AreEqual(800, svc.CurrentWindowHeight);
    }

    // -----------------------------------------------------------------------
    // UiModeService - Auto mode resolves from width
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Auto_UpdateWindowSize_EffectiveModeResolvesFromWidth()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        svc.UpdateWindowSize(1200, 800);
        Assert.AreEqual(UiMode.Desktop, svc.EffectiveMode);
    }

    [TestMethod]
    public void Auto_UpdateWindowSize_PhoneWidth_EffectiveModePhone()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        svc.UpdateWindowSize(375, 812);
        Assert.AreEqual(UiMode.Phone, svc.EffectiveMode);
    }

    [TestMethod]
    public void Auto_UpdateWindowSize_TabletWidth_EffectiveModePhone()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        svc.UpdateWindowSize(768, 1024);
        Assert.AreEqual(UiMode.Phone, svc.EffectiveMode);
    }

    // -----------------------------------------------------------------------
    // UiModeService - Changed event fires only on actual state change
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Auto_ChangedFires_WhenEffectiveModeChanges()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        svc.UpdateWindowSize(400, 700); // sets to Phone
        int count = 0;
        svc.Changed += (_, _) => count++;
        svc.UpdateWindowSize(1200, 800); // changes to Desktop
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void Auto_ChangedDoesNotFire_WhenEffectiveModeUnchanged()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        svc.UpdateWindowSize(1200, 800); // Desktop
        int count = 0;
        svc.Changed += (_, _) => count++;
        svc.UpdateWindowSize(1300, 900); // still Desktop
        Assert.AreEqual(0, count);
    }

    // -----------------------------------------------------------------------
    // UiModeService - switching from forced back to Auto uses current width
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SwitchFromForcedDesktop_ToAuto_UsesCurrentWidth_Phone()
    {
        var svc = CreateService(new(UiMode.Desktop, UiModeSource.CommandLine, false));
        svc.UpdateWindowSize(320, 568); // phone-sized, but forced so mode stays Desktop
        Assert.AreEqual(UiMode.Desktop, svc.EffectiveMode);

        // Now switch to Auto: should resolve from the stored 320 width -> Phone
        svc.ApplyRequestedMode(UiMode.Auto, UiModeSource.CommandLine);
        Assert.AreEqual(UiMode.Auto, svc.RequestedMode);
        Assert.AreEqual(UiMode.Phone, svc.EffectiveMode);
    }

    [TestMethod]
    public void SwitchFromForcedPhone_ToAuto_UsesCurrentWidth_Desktop()
    {
        var svc = CreateService(new(UiMode.Phone, UiModeSource.CommandLine, false));
        svc.UpdateWindowSize(1400, 900); // desktop-sized, but forced so mode stays Phone
        Assert.AreEqual(UiMode.Phone, svc.EffectiveMode);

        svc.ApplyRequestedMode(UiMode.Auto, UiModeSource.CommandLine);
        Assert.AreEqual(UiMode.Auto, svc.RequestedMode);
        Assert.AreEqual(UiMode.Desktop, svc.EffectiveMode);
    }

    // -----------------------------------------------------------------------
    // UiModeService - ApplyCommandLine
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ApplyCommandLine_ValidFlag_ChangesMode()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        svc.UpdateWindowSize(1200, 800);
        svc.ApplyCommandLine(["--ui-mode", "phone"]);
        Assert.AreEqual(UiMode.Phone, svc.RequestedMode);
        Assert.AreEqual(UiMode.Phone, svc.EffectiveMode);
        Assert.AreEqual(UiModeSource.CommandLine, svc.Source);
    }

    [TestMethod]
    public void ApplyCommandLine_NoFlag_DoesNotChangeMode()
    {
        var svc = CreateService(new(UiMode.Desktop, UiModeSource.CommandLine, false));
        svc.ApplyCommandLine(["mesh://somepath"]);
        Assert.AreEqual(UiMode.Desktop, svc.RequestedMode); // unchanged
    }

    [TestMethod]
    public void ApplyCommandLine_InvalidFlag_FallsBackToAuto()
    {
        var svc = CreateService(new(UiMode.Desktop, UiModeSource.CommandLine, false));
        svc.UpdateWindowSize(1200, 800);
        svc.ApplyCommandLine(["--ui-mode", "invalid-value"]);
        Assert.AreEqual(UiMode.Auto, svc.RequestedMode);
        Assert.AreEqual(UiMode.Desktop, svc.EffectiveMode); // Auto resolved from 1200 width
    }

    [TestMethod]
    public void ApplyCommandLine_SameMode_StillNotifiesForActivation()
    {
        var svc = CreateService(new(UiMode.Phone, UiModeSource.CommandLine, false));
        int count = 0;
        svc.Changed += (_, _) => count++;

        svc.ApplyCommandLine(["--ui-mode", "phone"]);

        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void ApplyRequestedMode_SourceChange_Notifies()
    {
        var svc = CreateService(new(UiMode.Auto, UiModeSource.Default, false));
        int count = 0;
        svc.Changed += (_, _) => count++;

        svc.ApplyRequestedMode(UiMode.Auto, UiModeSource.CommandLine);

        Assert.AreEqual(UiModeSource.CommandLine, svc.Source);
        Assert.AreEqual(1, count);
    }
}
