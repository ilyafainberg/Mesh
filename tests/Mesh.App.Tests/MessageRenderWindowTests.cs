using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MessageRenderWindowTests
{
    [TestMethod]
    public void LatestReturnsOnlyTheNewestRequestedItems()
    {
        var items = Enumerable.Range(1, 200).ToArray();

        var visible = MessageRenderWindow.Latest(items, MessageRenderWindow.InitialCount);

        Assert.AreEqual(80, visible.Count);
        Assert.AreEqual(121, visible[0]);
        Assert.AreEqual(200, visible[^1]);
    }

    [TestMethod]
    public void LatestReusesShortLists()
    {
        IReadOnlyList<int> items = new[] { 1, 2, 3 };

        var visible = MessageRenderWindow.Latest(items, MessageRenderWindow.InitialCount);

        Assert.AreSame(items, visible);
        Assert.AreEqual(0, MessageRenderWindow.HiddenCount(items.Count, MessageRenderWindow.InitialCount));
    }

    [TestMethod]
    public void NextCountExpandsByOneBoundedPage()
    {
        Assert.AreEqual(160, MessageRenderWindow.NextCount(300, 80));
        Assert.AreEqual(300, MessageRenderWindow.NextCount(300, 260));
        Assert.AreEqual(40, MessageRenderWindow.NextPageSize(300, 260));
    }
}
