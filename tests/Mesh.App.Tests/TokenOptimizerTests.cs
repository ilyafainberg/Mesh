using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class TokenOptimizerTests
{
    [TestMethod]
    public void ModelConfig_DefaultsToBalanced_ForNewAndOlderProfiles()
    {
        Assert.AreEqual(TokenOptimizationLevel.Balanced, new ModelConfig().TokenOptimization);

        var restored = JsonSerializer.Deserialize<ModelConfig>("{}");

        Assert.IsNotNull(restored);
        Assert.AreEqual(TokenOptimizationLevel.Balanced, restored.TokenOptimization);
    }

    [TestMethod]
    public void Disabled_PreservesTheOriginalRequest()
    {
        var history = new[] { new ChatLine { Role = "user", Text = "Keep this exact." } };

        var optimized = TokenOptimizer.OptimizeRequest(
            "system", history, TokenOptimizationLevel.Disabled);

        Assert.AreSame(history, optimized.History);
        Assert.AreEqual(0, optimized.SavedCharacters);
    }

    [TestMethod]
    public void MaxAccuracy_PreservesOrdinaryConversationText()
    {
        var history = Enumerable.Range(0, 14)
            .Select(index => new ChatLine
            {
                Role = index % 2 == 0 ? "user" : "assistant",
                Text = $"Ordinary message {index} with deliberate wording."
            })
            .ToList();

        var optimized = TokenOptimizer.OptimizeRequest(
            "system", history, TokenOptimizationLevel.MaxAccuracy);

        CollectionAssert.AreEqual(
            history.Select(line => line.Text).ToList(),
            optimized.History.Select(line => line.Text).ToList());
    }

    [TestMethod]
    public void Balanced_PreservesLatestUserTurnAndAttachments()
    {
        var attachment = new ChatAttachment("diagram.png", "image/png", [1, 2, 3]);
        var history = Enumerable.Range(0, 12)
            .Select(index => new ChatLine
            {
                Role = index % 2 == 0 ? "user" : "assistant",
                Text = index == 0
                    ? string.Join('\n', Enumerable.Repeat("progress 50%", 600))
                    : $"message {index}"
            })
            .ToList();
        history[^1].Role = "user";
        history[^1].Text = "Use every character in this current request exactly.";
        history[^1].Attachments.Add(attachment);

        var optimized = TokenOptimizer.OptimizeRequest(
            "security instructions", history, TokenOptimizationLevel.Balanced);

        var latest = optimized.History[^1];
        Assert.AreEqual(history[^1].Text, latest.Text);
        Assert.AreSame(attachment, latest.Attachments[0]);
        Assert.IsTrue(optimized.SavedCharacters > 0);
    }

    [TestMethod]
    public void MaxSavings_RetainsRelevantOlderConstraint()
    {
        var history = new List<ChatLine>
        {
            new() { Role = "user", Text = "The deployment port must stay 8080." },
            new() { Role = "assistant", Text = "Understood. Port 8080 is a hard constraint." }
        };
        for (var index = 0; index < 10; index++)
        {
            history.Add(new ChatLine { Role = "user", Text = "thanks" });
            history.Add(new ChatLine { Role = "assistant", Text = "ok" });
        }
        history.Add(new ChatLine { Role = "user", Text = "What deployment port constraint did I set?" });

        var optimized = TokenOptimizer.OptimizeRequest(
            "system", history, TokenOptimizationLevel.MaxSavings);

        StringAssert.Contains(string.Join('\n', optimized.History.Select(line => line.Text)), "8080");
        Assert.AreEqual(history[^1].Text, optimized.History[^1].Text);
        Assert.IsTrue(optimized.History.Count < history.Count);
    }

    [TestMethod]
    public void BalancedToolJson_CompactsLargeArraysButPreservesTheLastItem()
    {
        var input = JsonSerializer.Serialize(new
        {
            results = Enumerable.Range(1, 40).Select(index => new
            {
                id = index,
                value = index == 40 ? "final-error-detail" : $"value-{index}"
            })
        });

        var output = TokenOptimizer.OptimizeToolResult(
            "web_search", input, TokenOptimizationLevel.Balanced);

        Assert.IsTrue(output.Length < input.Length);
        StringAssert.Contains(output, "final-error-detail");
        StringAssert.Contains(output, "items omitted by Mesh token optimization");
        using var parsed = JsonDocument.Parse(output);
        Assert.IsTrue(parsed.RootElement.GetProperty("results").GetArrayLength() <= 12);
    }

    [TestMethod]
    public void BalancedToolOutput_RemovesRepeatedProgressButKeepsErrors()
    {
        var input = string.Join('\n',
            Enumerable.Repeat("Downloading 50%", 100)
                .Append("ERROR: package restore failed")
                .Append("Exit code 1"));

        var output = TokenOptimizer.OptimizeToolResult(
            "run_powershell", input, TokenOptimizationLevel.Balanced);

        Assert.IsTrue(output.Length < input.Length);
        Assert.AreEqual(1, output.Split("Downloading 50%", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(output, "ERROR: package restore failed");
        StringAssert.Contains(output, "Exit code 1");
    }

    [TestMethod]
    public void BalancedKnowledgeSelection_PrefersTheCurrentTopic()
    {
        var items = Enumerable.Range(0, 12)
            .Select(index => new KnowledgeItem
            {
                Id = index.ToString(),
                Title = index == 2 ? "OmniRoute deployment" : $"Unrelated {index}",
                Content = index == 2 ? "Redis is optional for one replica." : "general notes"
            })
            .ToList();

        var selected = TokenOptimizer.SelectKnowledge(
            items, "Does OmniRoute need Redis?", TokenOptimizationLevel.Balanced);

        Assert.HasCount(8, selected.Included);
        Assert.AreEqual("2", selected.Included[0].Id);
        Assert.HasCount(4, selected.Omitted);
    }
}
