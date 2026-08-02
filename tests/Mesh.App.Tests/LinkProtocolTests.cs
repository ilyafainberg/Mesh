using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class LinkProtocolTests
{
    [TestMethod]
    public void HashCode_Produces_Cosmos_Safe_Base64Url_Id()
    {
        var hash = LinkProtocol.HashCode("ZQkUsEfyKHuiKOSSgScmjmaA");

        Assert.IsFalse(hash.Contains('/'));
        Assert.IsFalse(hash.Contains('+'));
        Assert.IsFalse(hash.Contains('='));
        StringAssert.Matches(hash, new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]+$"));
    }

    [TestMethod]
    public void HashCode_Is_Deterministic_And_Code_Sensitive()
    {
        var first = LinkProtocol.HashCode("invite-one");

        Assert.AreEqual(first, LinkProtocol.HashCode("invite-one"));
        Assert.AreNotEqual(first, LinkProtocol.HashCode("invite-two"));
    }
}
