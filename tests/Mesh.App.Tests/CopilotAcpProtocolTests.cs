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
}
