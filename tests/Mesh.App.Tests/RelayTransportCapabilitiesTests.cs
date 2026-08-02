using System.Text.Json;
using Mesh.Relay;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class RelayTransportCapabilitiesTests
{
    [TestMethod]
    public void Protocol9_Advertises_Every_Required_Client_Capability()
    {
        var capabilities = RelayTransportCapabilities.Protocol9(metadataStore: true);
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(capabilities, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;

        Assert.AreEqual(MeshProtocol.Version, root.GetProperty("protocolVersion").GetInt32());
        Assert.IsTrue(root.GetProperty("onlineOnly").GetBoolean());
        Assert.IsFalse(root.GetProperty("durablePayloadStorage").GetBoolean());
        Assert.IsTrue(root.GetProperty("metadataStore").GetBoolean());
        Assert.IsTrue(root.GetProperty("sendResults").GetBoolean());
        Assert.IsTrue(root.GetProperty("ephemeralDelivery").GetBoolean());
        Assert.IsTrue(root.GetProperty("presenceResolution").GetBoolean());
        Assert.IsTrue(root.GetProperty("fanout").GetBoolean());
        Assert.IsTrue(root.GetProperty("replication").GetBoolean());
        Assert.IsTrue(root.GetProperty("onlineDelivery").GetBoolean());
        Assert.IsTrue(root.GetProperty("onlineReplication").GetBoolean());
        Assert.IsTrue(root.GetProperty("onlineWake").GetBoolean());
        Assert.IsTrue(root.GetProperty("deviceRevocation").GetBoolean());
        Assert.IsTrue(root.GetProperty("agentHost").GetBoolean());
        Assert.IsTrue(root.GetProperty("contentlessPush").GetBoolean());
    }

    [TestMethod]
    public void Protocol9_Remains_Metadata_Only()
    {
        var capabilities = RelayTransportCapabilities.Protocol9(metadataStore: true);

        Assert.IsTrue(capabilities.OnlineOnly);
        Assert.IsFalse(capabilities.DurablePayloadStorage);
        Assert.IsTrue(capabilities.MetadataStore);
    }
}
