using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class PairingLinkTests
{
    [TestMethod]
    public void CustomScheme_RoundTripsPairingPayload()
    {
        var value = DeepLink.Pairing("alice", "single-use-code", "https://relay.example");

        Assert.IsTrue(DeepLink.TryParsePairing(value, out var parsed));
        Assert.AreEqual("alice", parsed.Handle);
        Assert.AreEqual("single-use-code", parsed.PairingCode);
        Assert.AreEqual("https://relay.example", parsed.RelayUrl);
    }

    [TestMethod]
    public void HttpsLink_RoundTripsPairingPayload()
    {
        var value = UniversalLink.ForPairing("alice", "single-use-code", "https://relay.example");

        Assert.IsTrue(DeepLink.TryParsePairing(value, out var parsed));
        Assert.AreEqual(DeepLink.Kind.Pairing, parsed.Kind);
        Assert.AreEqual("alice", parsed.Handle);
        Assert.AreEqual("single-use-code", parsed.PairingCode);
        Assert.AreEqual("https://relay.example", parsed.RelayUrl);
        Assert.AreEqual("", new Uri(value).Query);
        Assert.IsTrue(new Uri(value).Fragment.StartsWith("#handle=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PairingLink_RequiresCode()
        => Assert.IsFalse(DeepLink.TryParsePairing("https://meshrelay.net/link?handle=alice", out _));
}
