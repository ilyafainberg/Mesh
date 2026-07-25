using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Services;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ToolDetailFormatterTests
{
    [TestMethod]
    public void PowerShellInput_SeparatesScriptFromOptions()
    {
        const string raw = "{\"script\":\"Get-ChildItem | Where-Object { $_.Length -gt 0 }\",\"working_directory\":\"C:\\\\work\",\"timeout_seconds\":30}";

        var detail = ToolDetailFormatter.Format(
            "functions.mesh-run_powershell",
            ToolDetailDirection.Input,
            raw);

        Assert.IsTrue(detail.HasFormattedView);
        Assert.AreEqual("json", detail.RawLanguage);
        Assert.AreEqual(2, detail.Sections.Count);
        Assert.AreEqual("script", detail.Sections[0].Label);
        Assert.AreEqual("powershell", detail.Sections[0].Language);
        StringAssert.Contains(detail.Sections[0].Text, "Where-Object");
        Assert.AreEqual("options", detail.Sections[1].Label);
        StringAssert.Contains(detail.Sections[1].Text, "working_directory");
        Assert.IsFalse(detail.Sections[1].Text.Contains("Get-ChildItem", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FileInput_UsesExtensionForLanguage()
    {
        const string raw = "{\"path\":\"sample.ts\",\"content\":\"const answer: number = 42;\",\"overwrite\":true}";

        var detail = ToolDetailFormatter.Format("file_system", ToolDetailDirection.Input, raw);

        Assert.AreEqual("typescript", detail.Sections[0].Language);
        Assert.AreEqual("content", detail.Sections[0].Label);
        Assert.AreEqual("const answer: number = 42;", detail.Sections[0].Text);
    }

    [TestMethod]
    public void JsonOutput_IsPrettyPrinted()
    {
        const string raw = "{\"ok\":true,\"items\":[1,2]}";

        var detail = ToolDetailFormatter.Format("web_search", ToolDetailDirection.Output, raw);

        Assert.IsTrue(detail.HasFormattedView);
        Assert.AreEqual("json", detail.Sections.Single().Language);
        StringAssert.Contains(detail.Sections.Single().Text, "\n");
        StringAssert.Contains(detail.Sections.Single().Text, "  \"items\"");
        Assert.AreEqual(raw, detail.Raw);
    }

    [TestMethod]
    public void AcpEnvelope_UnwrapsAndSplitsProcessResult()
    {
        const string raw = "{\"content\":[{\"content\":{\"type\":\"text\",\"text\":\"Chunk ID: abc123\\nWall time: 0.12 seconds\\nProcess exited with code 0\\nFinal output:\\nhello\\n\"}}]}";

        var detail = ToolDetailFormatter.Format("run_powershell", ToolDetailDirection.Output, raw);

        Assert.IsTrue(detail.HasFormattedView);
        Assert.AreEqual("json", detail.RawLanguage);
        Assert.AreEqual("exit code", detail.Sections[0].Label);
        Assert.AreEqual("0", detail.Sections[0].Text);
        Assert.AreEqual(ToolDetailTone.Success, detail.Sections[0].Tone);
        Assert.AreEqual("run details", detail.Sections[1].Label);
        StringAssert.Contains(detail.Sections[1].Text, "Chunk ID: abc123");
        Assert.AreEqual("output", detail.Sections[2].Label);
        Assert.AreEqual("hello", detail.Sections[2].Text);
    }

    [TestMethod]
    public void AcpEnvelope_WithStringContent_UnwrapsText()
    {
        const string raw = "{\"content\":\"line one\\nline two\",\"isError\":false}";

        var detail = ToolDetailFormatter.Format("tool", ToolDetailDirection.Output, raw);

        Assert.IsTrue(detail.HasFormattedView);
        Assert.AreEqual("line one\nline two", detail.Sections.Single().Text);
        Assert.AreEqual("plaintext", detail.Sections.Single().Language);
        Assert.AreEqual(raw, detail.Raw);
    }

    [TestMethod]
    public void LocalProcessResult_SplitsStdoutAndStderr()
    {
        const string raw = "exit code: 2\nstdout:\npartial output\nstderr:\nbad argument";

        var detail = ToolDetailFormatter.Format("run_cmd", ToolDetailDirection.Output, raw);

        Assert.AreEqual(3, detail.Sections.Count);
        Assert.AreEqual(ToolDetailTone.Error, detail.Sections[0].Tone);
        Assert.AreEqual("stdout", detail.Sections[1].Label);
        Assert.AreEqual("partial output", detail.Sections[1].Text);
        Assert.AreEqual("stderr", detail.Sections[2].Label);
        Assert.AreEqual(ToolDetailTone.Error, detail.Sections[2].Tone);
    }

    [TestMethod]
    public void TimedOutProcess_UsesWarningStatus()
    {
        const string raw = "[timed out]\nexit code: -1\nstdout:\nwaiting";

        var detail = ToolDetailFormatter.Format("run_python", ToolDetailDirection.Output, raw);

        Assert.AreEqual("-1 (timed out)", detail.Sections[0].Text);
        Assert.AreEqual(ToolDetailTone.Warning, detail.Sections[0].Tone);
    }

    [TestMethod]
    public void FencedCode_RemovesFenceAndMapsLanguage()
    {
        const string raw = "```cs\nvar answer = 42;\n```";

        var detail = ToolDetailFormatter.Format("tool", ToolDetailDirection.Output, raw);

        Assert.IsTrue(detail.HasFormattedView);
        Assert.AreEqual("csharp", detail.Sections.Single().Language);
        Assert.AreEqual("var answer = 42;", detail.Sections.Single().Text);
    }

    [TestMethod]
    public void PlainText_RemainsPlainAndHasNoRedundantToggle()
    {
        const string raw = "A normal tool result.";

        var detail = ToolDetailFormatter.Format("tool", ToolDetailDirection.Output, raw);

        Assert.IsFalse(detail.HasFormattedView);
        Assert.AreEqual("plaintext", detail.RawLanguage);
        Assert.AreEqual(raw, detail.Sections.Single().Text);
    }

    [TestMethod]
    public void NormalizeToolName_PreservesUsefulAcpHint()
    {
        Assert.AreEqual("run_powershell", ToolDetailFormatter.NormalizeToolName("functions.mesh-run_powershell"));
        Assert.AreEqual("run_csharp_script", ToolDetailFormatter.NormalizeToolName("mesh-run-csharp-script"));
    }
}
