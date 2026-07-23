using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MessageLimitsTests
{
    [TestMethod]
    public void TransportLimit_AllowsStructuredRejectionOfLegacyFrames()
    {
        Assert.IsTrue(MessageLimits.MaxTransportMessageBytes > MessageLimits.MaxEnvelopeBodyBytes);
        Assert.IsTrue(MessageLimits.MaxTransportMessageBytes >= 6 * 1024 * 1024);
        Assert.IsTrue(MessageLimits.MaxEnvelopeBodyBytes < 2 * 1024 * 1024);
    }
}
