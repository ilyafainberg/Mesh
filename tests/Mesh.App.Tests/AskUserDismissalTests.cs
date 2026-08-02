using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AskUserDismissalTests
{
    private static readonly string Source = ReadSource(
        "src", "Mesh.App", "Services", "AppState.AskUser.cs");

    [TestMethod]
    public void TranscriptProjection_RendersPendingPromptsOnly()
    {
        var start = Source.IndexOf(
            "public IReadOnlyList<AskUserBubbleView> AskUserPromptsFor",
            StringComparison.Ordinal);
        var end = Source.IndexOf(
            "public void FocusAskUserPrompt",
            start,
            StringComparison.Ordinal);
        var method = Source[start..end];

        StringAssert.Contains(method, ".Where(view => view.IsInteractive)");
    }

    [TestMethod]
    public void LocalChoice_DismissesBeforeDurableResolutionAwait()
    {
        var start = Source.IndexOf(
            "public async Task<bool> ResolveAskUserPromptAsync",
            StringComparison.Ordinal);
        var end = Source.IndexOf(
            "public async Task<bool> CancelAskUserPromptAsync",
            start,
            StringComparison.Ordinal);
        var method = Source[start..end];
        var dismiss = method.IndexOf("DismissAskUserView(promptId)", StringComparison.Ordinal);
        var resolve = method.IndexOf("await EmitAskUserResolutionAsync(", StringComparison.Ordinal);

        Assert.IsTrue(dismiss >= 0);
        Assert.IsTrue(resolve > dismiss);
    }

    [TestMethod]
    public void TerminalPrompt_IsEvictedButRemainsDurable()
    {
        StringAssert.Contains(Source, "if (prompt.State == AskUserState.Pending)");
        StringAssert.Contains(Source, "DismissAskUserView(prompt.PromptId)");
        StringAssert.Contains(Source, "await store.GetAsync(promptId, ct)");
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
