using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MessageActionsRenderingTests
{
    [TestMethod]
    public void DesktopCopy_IsCompactIconOnlyAndLeavesRightClickNative()
    {
        var markup = ReadRepoFile("src", "Mesh.App", "Components", "MessageActions.razor");
        var styles = ReadRepoFile("src", "Mesh.App", "Components", "MessageActions.razor.css");
        var button = Between(markup, "class=\"message-copy-button\"", "</button>");

        StringAssert.Contains(button, "aria-label=\"Copy message\"");
        StringAssert.Contains(button, "title=\"Copy Markdown source\"");
        StringAssert.Contains(button, "<i class=\"@CopyIconClass\" aria-hidden=\"true\"></i>");
        Assert.IsFalse(button.Contains("@(copied ? \"Copied\" : \"Copy\")", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("@oncontextmenu=\"", StringComparison.Ordinal));
        StringAssert.Contains(markup, "@oncontextmenu:preventDefault=\"IsMobile && CanCopy\"");
        Assert.IsFalse(markup.Contains("OpenContextActions", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("message-context-menu", StringComparison.Ordinal));

        StringAssert.Contains(styles, "width: 30px;");
        StringAssert.Contains(styles, "min-width: 30px;");
        StringAssert.Contains(styles, "height: 30px;");
        StringAssert.Contains(styles, ".message-action-surface.desktop:hover > .message-copy-button");
        StringAssert.Contains(styles, ".message-action-surface.desktop:focus-within > .message-copy-button");
    }

    [TestMethod]
    public void MobileCopy_UsesLongPressOnly()
    {
        var markup = ReadRepoFile("src", "Mesh.App", "Components", "MessageActions.razor");
        var styles = ReadRepoFile("src", "Mesh.App", "Components", "MessageActions.razor.css");

        Assert.IsFalse(markup.Contains("class=\"message-more-button\"", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("OpenMobileActions", StringComparison.Ordinal));
        Assert.IsFalse(styles.Contains(".message-more-button", StringComparison.Ordinal));
        StringAssert.Contains(markup, "@onpointerdown=\"BeginLongPress\"");
        StringAssert.Contains(markup, "@onpointermove=\"TrackLongPress\"");
        StringAssert.Contains(markup, "Math.Abs(args.ClientX - pointerStartX) > LongPressMoveTolerance");
        StringAssert.Contains(markup, "if (!IsMobile || !CanCopy || args.Button != 0");
        StringAssert.Contains(markup, "class=\"message-action-sheet\"");
        StringAssert.Contains(markup, "class=\"message-sheet-copy\"");
        StringAssert.Contains(markup, "Copied to clipboard");
    }

    [TestMethod]
    public void AllConversationSurfaces_UseSharedMessageActions()
    {
        var surfaces = new[]
        {
            new[] { "src", "Mesh.App", "Components", "Pages", "Home.razor" },
            new[] { "src", "Mesh.App", "Components", "Pages", "Messages.razor" },
            new[] { "src", "Mesh.App", "Components", "Mobile", "MobileMe.razor" },
            new[] { "src", "Mesh.App", "Components", "Mobile", "MobileMessages.razor" }
        };

        foreach (var path in surfaces)
            StringAssert.Contains(ReadRepoFile(path), "<MessageActions Markdown=\"@line.Text\"");
    }

    private static string Between(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, $"Could not find {start}.");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(endIndex >= 0, $"Could not find {end} after {start}.");
        return source[startIndex..(endIndex + end.Length)];
    }

    private static string ReadRepoFile(params string[] parts)
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
