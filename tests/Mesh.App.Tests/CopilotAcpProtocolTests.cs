using System.Text.Json;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public class CopilotAcpProtocolTests
{
    [TestMethod]
    public void BuildArguments_Auto_OmitsModelAndEffort()
    {
        var args = CopilotAcpProtocol.BuildServerArguments("auto", "Auto");
        CollectionAssert.AreEqual(new[] { "--acp", "--stdio", "--available-tools=" }, args.ToArray());
    }

    [TestMethod]
    public void BuildArguments_Explicit_AddsModelAndEffort()
    {
        var args = CopilotAcpProtocol.BuildServerArguments("gpt-5.4", "XHigh");
        CollectionAssert.Contains(args.ToArray(), "gpt-5.4");
        CollectionAssert.Contains(args.ToArray(), "xhigh");
    }

    [TestMethod]
    public void BuildArguments_Tools_UsesSingleFilterArgument()
    {
        var args = CopilotAcpProtocol.BuildServerArguments("auto", "auto", "mesh-web_search,mesh-file_system");
        CollectionAssert.Contains(args.ToArray(), "--available-tools=mesh-web_search,mesh-file_system");
    }

    [TestMethod]
    public void BuildArguments_InvalidEffort_Throws()
        => Assert.ThrowsException<ArgumentException>(
            () => CopilotAcpProtocol.BuildServerArguments("auto", "ridiculous"));

    [TestMethod]
    public void ComposePrompt_LabelsHistoryAndInstructions()
    {
        var prompt = CopilotAcpProtocol.ComposePrompt(
            "Be concise.",
            new[] { ("user", "Hello"), ("assistant", "Hi"), ("user", "Help") });
        StringAssert.Contains(prompt, "SYSTEM INSTRUCTIONS:");
        StringAssert.Contains(prompt, "USER: Hello");
        StringAssert.Contains(prompt, "ASSISTANT: Hi");
        StringAssert.Contains(prompt, "Do not use tools or access files.");
    }

    [TestMethod]
    public void ComposePrompt_WithTools_UsesMeshPermissionLanguage()
    {
        var prompt = CopilotAcpProtocol.ComposePrompt(
            "Be concise.",
            new[] { ("user", "Use a tool") },
            toolsAvailable: true);
        StringAssert.Contains(prompt, "Use only tools supplied by Mesh.");
        Assert.IsFalse(prompt.Contains("Do not use tools", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseModels_DedupesAndReadsMetadata()
    {
        using var document = JsonDocument.Parse("""
            {
              "sessionId": "s",
              "models": {
                "availableModels": [
                  { "modelId": "auto", "name": "Auto" },
                  {
                    "modelId": "gpt-5.4",
                    "name": "GPT-5.4",
                    "description": "Model",
                    "_meta": {
                      "copilotUsage": "1x",
                      "copilotPriceCategory": "medium",
                      "copilotEnablement": "enabled"
                    }
                  }
                ]
              }
            }
            """);
        var models = CopilotAcpProtocol.ParseModels(document.RootElement);
        Assert.AreEqual(2, models.Count);
        Assert.AreEqual("gpt-5.4", models[1].Id);
        Assert.AreEqual("1x", models[1].Usage);
        Assert.IsTrue(models[1].Enabled);
    }

    [TestMethod]
    public void NormalizeText_LeavesValidUnicodeAndAsciiUnchanged()
    {
        const string unicode = "Café \u2013 Ångström 😊";
        Assert.AreEqual(unicode, CopilotAcpProtocol.NormalizeText(unicode));
        Assert.AreEqual("Plain ASCII text", CopilotAcpProtocol.NormalizeText("Plain ASCII text"));
    }

    [TestMethod]
    public void NormalizeText_RepairsSmartPunctuationAccentsAndEmoji()
    {
        Assert.AreEqual(
            "It\u2019s \u201cquoted\u201d \u2014 😊 Å",
            CopilotAcpProtocol.NormalizeText("It\u00e2\u20ac\u2122s \u00e2\u20ac\u0153quoted\u00e2\u20ac\u009d \u00e2\u20ac\u201d \u00f0\u0178\u02dc\u0160 \u00c3\u2026"));
        Assert.AreEqual(
            "Café says \u201chello\u201d 😊",
            CopilotAcpProtocol.NormalizeText("Café says \u00e2\u20ac\u0153hello\u00e2\u20ac\u009d 😊"));
    }

    [TestMethod]
    public void NormalizeText_InvalidUtf8RepairIsRejected()
    {
        const string suspicious = "Broken Ã(";
        Assert.AreEqual(suspicious, CopilotAcpProtocol.NormalizeText(suspicious));
    }

    [TestMethod]
    public void ParseUsage_ReadsNestedExplicitInputAndOutputVariants()
    {
        using var document = JsonDocument.Parse("""
            {
              "usage": {
                "input_tokens": 120,
                "completionTokens": "30"
              }
            }
            """);

        Assert.IsTrue(CopilotAcpProtocol.TryParseUsage(document.RootElement, out var usage));
        Assert.AreEqual(120L, usage.PromptTokens);
        Assert.AreEqual(30L, usage.CompletionTokens);
        Assert.IsNull(usage.UsedTokens);
    }

    [TestMethod]
    public void ParseUsage_ReadsUsedAndIgnoresContextCapacitySize()
    {
        using var document = JsonDocument.Parse("""
            {
              "sessionUpdate": "usage_update",
              "context": { "used": 345, "size": 128000 }
            }
            """);

        Assert.IsTrue(CopilotAcpProtocol.TryParseUsage(document.RootElement, out var usage));
        Assert.AreEqual(345L, usage.UsedTokens);
        Assert.IsNull(usage.PromptTokens);
        Assert.IsNull(usage.CompletionTokens);
    }

    [TestMethod]
    public void UsageAccumulator_RecordsOnlyPositiveCumulativeDeltas()
    {
        var accumulator = new CopilotAcpUsageAccumulator();

        Assert.AreEqual(new CopilotAcpUsageDelta(100, 10),
            accumulator.Apply(new(100, 10, null)));
        Assert.AreEqual(default(CopilotAcpUsageDelta),
            accumulator.Apply(new(100, 10, null)));
        Assert.AreEqual(new CopilotAcpUsageDelta(20, 5),
            accumulator.Apply(new(120, 15, null)));
        Assert.AreEqual(default(CopilotAcpUsageDelta),
            accumulator.Apply(new(110, 14, null)));
    }

    [TestMethod]
    public void UsageAccumulator_UsedOnlyCountsContextDeltaAsPrompt()
    {
        var accumulator = new CopilotAcpUsageAccumulator();

        Assert.AreEqual(new CopilotAcpUsageDelta(200, 0),
            accumulator.Apply(new(null, null, 200)));
        Assert.AreEqual(default(CopilotAcpUsageDelta),
            accumulator.Apply(new(null, null, 200)));
        Assert.AreEqual(new CopilotAcpUsageDelta(25, 0),
            accumulator.Apply(new(null, null, 225)));
    }
}
