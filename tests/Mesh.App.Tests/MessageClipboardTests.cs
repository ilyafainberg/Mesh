using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MessageClipboardTests
{
    [TestMethod]
    public async Task CopyMarkdown_PreservesExactSource()
    {
        string? copied = null;
        var clipboard = new MessageClipboard(value =>
        {
            copied = value;
            return Task.CompletedTask;
        });
        const string source = "# Heading\r\n\r\n**Important:** Run `dotnet test`.\r\n\r\n```csharp\r\nvar x = 1;\r\n```\r\n\r\n```html-app\r\n<p>Hi</p>\r\n```";

        await clipboard.CopyMarkdownAsync(source);

        Assert.AreEqual(source, copied);
    }

    [TestMethod]
    public async Task CopyMarkdown_AllowsEmptySource()
    {
        string? copied = null;
        var clipboard = new MessageClipboard(value =>
        {
            copied = value;
            return Task.CompletedTask;
        });

        await clipboard.CopyMarkdownAsync(string.Empty);

        Assert.AreEqual(string.Empty, copied);
    }

    [TestMethod]
    public async Task CopyMarkdown_RejectsNull()
    {
        var clipboard = new MessageClipboard(_ => Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => clipboard.CopyMarkdownAsync(null!));
    }

    [DataTestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(" \r\n\t", false)]
    [DataRow("**text**", true)]
    [DataRow("```\n\n```", true)]
    public void CopyPolicy_DisablesOnlyVisuallyEmptyText(string? source, bool expected)
        => Assert.AreEqual(expected, MessageCopyPolicy.HasVisibleContent(source));
}
