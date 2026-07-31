using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AboutLayoutTests
{
    [TestMethod]
    public void OpenSourceActions_HaveResponsiveAccessibleLayoutContract()
    {
        var markup = ReadRepoFile("src", "Mesh.App", "Components", "Pages", "About.razor");
        var styles = ReadRepoFile("src", "Mesh.App", "Components", "Pages", "About.razor.css");

        StringAssert.Contains(markup, "class=\"about-actions\"");
        Assert.AreEqual(2, Count(markup, "ghost about-action"));
        Assert.AreEqual(2, Count(markup, "class=\"about-action-label\""));
        Assert.AreEqual(2, Count(markup, "aria-hidden=\"true\""));

        StringAssert.Contains(styles, "flex-wrap: wrap;");
        StringAssert.Contains(styles, "flex: 1 1 14rem;");
        StringAssert.Contains(styles, "min-width: 0;");
        StringAssert.Contains(styles, "max-width: 100%;");
        StringAssert.Contains(styles, "min-height: 44px;");
        StringAssert.Contains(styles, "white-space: normal;");
        StringAssert.Contains(styles, "overflow-wrap: anywhere;");
        StringAssert.Contains(styles, ".about-action:focus-visible");
        StringAssert.Contains(styles, "@media (max-width: 430px)");
        StringAssert.Contains(styles, "flex-basis: 100%;");
    }

    static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    static string ReadRepoFile(params string[] parts)
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
