using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ComposerPreviewTests
{
    [TestMethod]
    public void ComposersDoNotRenderUnsentMarkdownPreview()
    {
        foreach (var path in ComposerPaths)
        {
            var source = Read(path);
            var file = Path.Combine(path);
            Assert.IsFalse(source.Contains("composer-preview", StringComparison.Ordinal), file);
            Assert.IsFalse(source.Contains("ShowPreview", StringComparison.Ordinal), file);
            Assert.IsFalse(source.Contains("Markdown.ToHtml(input)", StringComparison.Ordinal), file);
        }
    }

    [TestMethod]
    public void SentMessagesStillUseMessageContentRenderer()
    {
        var messageContent = Read(
            "src", "Mesh.App", "Components", "MessageContent.razor");
        StringAssert.Contains(messageContent, "Markdown.ToHtml");

        foreach (var path in ComposerPaths)
            StringAssert.Contains(Read(path), "<MessageContent");
    }

    private static readonly string[][] ComposerPaths =
    [
        ["src", "Mesh.App", "Components", "Pages", "Home.razor"],
        ["src", "Mesh.App", "Components", "Pages", "Messages.razor"],
        ["src", "Mesh.App", "Components", "Mobile", "MobileMe.razor"],
        ["src", "Mesh.App", "Components", "Mobile", "MobileMessages.razor"]
    ];

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(parts)}.");
    }
}
