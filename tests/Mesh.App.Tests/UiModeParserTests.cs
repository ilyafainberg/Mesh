using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Services;

namespace Mesh.App.Tests;

/// <summary>
/// Unit tests for UiModeParser.ParseArgs - covers every acceptance criterion for the
/// command-line parser (no flag, valid values, case-insensitivity, invalid/missing value).
/// </summary>
[TestClass]
public class UiModeParserTests
{
    // -----------------------------------------------------------------------
    // No flag -> Auto, Default source
    // -----------------------------------------------------------------------

    [TestMethod]
    public void NoFlag_ReturnsAutoDefault()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.Default, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void EmptyArgs_ReturnsAutoDefault()
    {
        var result = UiModeParser.ParseArgs([]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.Default, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void UnrelatedFlags_ReturnsAutoDefault()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--some-other-flag", "value"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.Default, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    // -----------------------------------------------------------------------
    // Valid values (lowercase)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void FlagAuto_ReturnsAutoCommandLine()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "auto"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void FlagDesktop_ReturnsDesktopCommandLine()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "desktop"]);
        Assert.AreEqual(UiMode.Desktop, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void FlagPhone_ReturnsPhoneCommandLine()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "phone"]);
        Assert.AreEqual(UiMode.Phone, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    // -----------------------------------------------------------------------
    // Case-insensitivity
    // -----------------------------------------------------------------------

    [TestMethod]
    public void FlagPhone_UpperCase_ReturnsPhone()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "PHONE"]);
        Assert.AreEqual(UiMode.Phone, result.Mode);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void FlagDesktop_MixedCase_ReturnsDesktop()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "Desktop"]);
        Assert.AreEqual(UiMode.Desktop, result.Mode);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void FlagAuto_UpperCase_ReturnsAuto()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "AUTO"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void FlagName_CaseInsensitive()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--UI-MODE", "phone"]);
        Assert.AreEqual(UiMode.Phone, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
    }

    // -----------------------------------------------------------------------
    // Equals-sign form: --ui-mode=value
    // -----------------------------------------------------------------------

    [TestMethod]
    public void EqualSignForm_Desktop_ReturnsDesktop()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode=desktop"]);
        Assert.AreEqual(UiMode.Desktop, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsFalse(result.HadInvalidInput);
    }

    [TestMethod]
    public void EqualSignForm_Phone_ReturnsPhone()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode=PHONE"]);
        Assert.AreEqual(UiMode.Phone, result.Mode);
        Assert.IsFalse(result.HadInvalidInput);
    }

    // -----------------------------------------------------------------------
    // Invalid and missing value -> Auto, CommandLine source, HadInvalidInput=true
    // -----------------------------------------------------------------------

    [TestMethod]
    public void InvalidValue_ReturnsAutoCommandLineWithInvalidFlag()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "widescreen"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsTrue(result.HadInvalidInput);
    }

    [TestMethod]
    public void TabletValue_NowUnsupported_ReturnsAutoCommandLineWithInvalidFlag()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", "tablet"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsTrue(result.HadInvalidInput);
    }

    [TestMethod]
    public void EmptyValue_ReturnsAutoCommandLineWithInvalidFlag()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode", ""]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsTrue(result.HadInvalidInput);
    }

    [TestMethod]
    public void MissingValue_FlagAtEnd_ReturnsAutoCommandLineWithInvalidFlag()
    {
        var result = UiModeParser.ParseArgs(["Mesh.exe", "--ui-mode"]);
        Assert.AreEqual(UiMode.Auto, result.Mode);
        Assert.AreEqual(UiModeSource.CommandLine, result.Source);
        Assert.IsTrue(result.HadInvalidInput);
    }

    // -----------------------------------------------------------------------
    // SplitWindowsArgs helper
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SplitWindowsArgs_BasicSpaceSeparated()
    {
        var tokens = UiModeParser.SplitWindowsArgs("--ui-mode phone");
        CollectionAssert.AreEqual(new[] { "--ui-mode", "phone" }, tokens);
    }

    [TestMethod]
    public void SplitWindowsArgs_QuotedPath()
    {
        var tokens = UiModeParser.SplitWindowsArgs("\"C:\\path to\\Mesh.exe\" --ui-mode phone");
        CollectionAssert.AreEqual(new[] { "C:\\path to\\Mesh.exe", "--ui-mode", "phone" }, tokens);
    }

    [TestMethod]
    public void SplitWindowsArgs_MeshUri()
    {
        var tokens = UiModeParser.SplitWindowsArgs("mesh://handle/something --ui-mode phone");
        Assert.AreEqual("mesh://handle/something", tokens[0]);
        Assert.AreEqual("--ui-mode", tokens[1]);
        Assert.AreEqual("phone", tokens[2]);
    }
}
