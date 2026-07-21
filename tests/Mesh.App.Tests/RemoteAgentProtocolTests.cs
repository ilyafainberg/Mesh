using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Mesh.App.Tests;

[TestClass]
public sealed class RemoteAgentProtocolTests
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
    public void RequestEnvelope_RoundTripsSourceAndTargetDevices()
    {
        var envelope = MeshEnvelope.Create(
            "owner",
            "owner",
            MeshKinds.RemoteAgentRequest,
            RemoteAgentProtocol.RequestBody("request-1", "thread-1", "Check the server."),
            fromDevice: "mobile-device",
            toDevice: "desktop-device");

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTrip = JsonSerializer.Deserialize<MeshEnvelope>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual("mobile-device", roundTrip.FromDevice);
        Assert.AreEqual("desktop-device", roundTrip.ToDevice);
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
    public void InvalidPayload_IsRejected()
    {
        Assert.IsFalse(RemoteAgentProtocol.TryParseRequest("not-json", out _));
        Assert.IsFalse(RemoteAgentProtocol.TryParseResponse("""{"requestId":"x"}""", out _));
    }

    [TestMethod]
    public void DevicePlatforms_ClassifyOnlyDesktopOperatingSystems()
    {
        Assert.IsTrue(DevicePlatforms.IsDesktop(DevicePlatforms.Windows));
        Assert.IsTrue(DevicePlatforms.IsDesktop(DevicePlatforms.MacOS));
        Assert.IsFalse(DevicePlatforms.IsDesktop(DevicePlatforms.Android));
        Assert.IsFalse(DevicePlatforms.IsDesktop(DevicePlatforms.IOS));
        Assert.IsFalse(DevicePlatforms.IsDesktop(DevicePlatforms.Unknown));
    }

    [TestMethod]
    public void DispatchResult_ProvidesStableFailureCode()
    {
        var result = RemoteAgentDispatchResult.Reject("home_device_offline", "request-1");

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("home_device_offline", result.Code);
        Assert.AreEqual("request-1", result.RequestId);
    }

    [TestMethod]
    public void DeviceSyncVersion_OrdersByTimeThenDeviceAndOperation()
    {
        var earlier = DeviceSyncVersion.Create(
            new DateTimeOffset(2026, 7, 19, 20, 0, 0, TimeSpan.Zero), "device-a", "op-a");
        var later = DeviceSyncVersion.Create(
            new DateTimeOffset(2026, 7, 19, 20, 0, 1, TimeSpan.Zero), "device-a", "op-b");
        var tieBreak = DeviceSyncVersion.Create(
            new DateTimeOffset(2026, 7, 19, 20, 0, 1, TimeSpan.Zero), "device-b", "op-a");

        Assert.IsTrue(DeviceSyncVersion.IsNewer(later, earlier));
        Assert.IsTrue(DeviceSyncVersion.IsNewer(tieBreak, later));
        Assert.IsFalse(DeviceSyncVersion.IsNewer(later, tieBreak));
    }

    [TestMethod]
    public void DeviceSyncEnvelopeKinds_AreRecognized()
    {
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeOperation));
        Assert.IsTrue(DeviceSyncKinds.IsEnvelopeKind(DeviceSyncKinds.EnvelopeSnapshotRequest));
        Assert.IsFalse(DeviceSyncKinds.IsEnvelopeKind(MeshKinds.Chat));
    }
}
