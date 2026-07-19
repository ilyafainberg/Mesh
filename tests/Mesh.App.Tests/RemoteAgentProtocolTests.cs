using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
}
