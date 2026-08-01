using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Mesh.App.Tests;

[TestClass]
public sealed class Protocol9RemoteAgentTests
{
    [TestMethod]
    public void Request_RoundTripsCorrelationAndPrompt()
    {
        var body = RemoteAgentProtocol.RequestBody("request-1", "thread-1", "Check the server.");

        Assert.IsTrue(RemoteAgentProtocol.TryParseRequest(body, out var request));
        Assert.AreEqual("request-1", request.RequestId);
        Assert.AreEqual("thread-1", request.ThreadId);
        Assert.AreEqual("Check the server.", request.Prompt);
    }

    [TestMethod]
    public void Response_RoundTripsCorrelationAndText()
    {
        var body = RemoteAgentProtocol.ResponseBody("request-1", "thread-1", "The server is healthy.");

        Assert.IsTrue(RemoteAgentProtocol.TryParseResponse(body, out var response));
        Assert.AreEqual("request-1", response.RequestId);
        Assert.AreEqual("thread-1", response.ThreadId);
        Assert.AreEqual("The server is healthy.", response.Text);
    }

    [TestMethod]
    public void P9Envelope_RoundTripsSourceAndTargetDevices()
    {
        var envelope = MeshEnvelope.Create(
            "owner",
            "owner",
            MeshKinds.RemoteAgentRequest,
            RemoteAgentProtocol.RequestBody("request-1", "thread-1", "Check the server."),
            fromDevice: "mobile-device",
            toDevice: "desktop-device");

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTrip = JsonSerializer.Deserialize<MeshEnvelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual("mobile-device", roundTrip.FromDevice);
        Assert.AreEqual("desktop-device", roundTrip.ToDevice);
    }
}
