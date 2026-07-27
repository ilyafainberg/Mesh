using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.Relay.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ApnsPushSenderTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Version? Version { get; private set; }
        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Version = request.Version;
            foreach (var header in request.Headers)
                Headers[header.Key] = header.Value.ToArray();
            return responder(request);
        }
    }

    [TestMethod]
    public async Task BackgroundPushUsesContentAvailableAndRequiredHeaders()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var sender = CreateSender(handler);

        var result = await sender.SendAsync(
            "device-token",
            new PushAlert("", "", "sync", PushDeliveryMode.Background));

        Assert.AreEqual(PushSendStatus.Sent, result.Status);
        Assert.AreEqual(HttpVersion.Version20, handler.Version);
        Assert.AreEqual("background", SingleHeader(handler, "apns-push-type"));
        Assert.AreEqual("5", SingleHeader(handler, "apns-priority"));
        Assert.AreEqual("mesh-sync", SingleHeader(handler, "apns-collapse-id"));
        using var payload = JsonDocument.Parse(handler.Body!);
        var aps = payload.RootElement.GetProperty("aps");
        Assert.AreEqual(1, aps.GetProperty("content-available").GetInt32());
        Assert.IsFalse(aps.TryGetProperty("alert", out _));
        Assert.AreEqual("sync", payload.RootElement.GetProperty("mesh").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task AlertAndBackgroundPushKeepsVisibleAlertAndWakesApp()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var sender = CreateSender(handler);

        await sender.SendAsync(
            "device-token",
            new PushAlert("Mesh", "Message from @alice", "message", PushDeliveryMode.AlertAndBackground));

        Assert.AreEqual("alert", SingleHeader(handler, "apns-push-type"));
        Assert.AreEqual("10", SingleHeader(handler, "apns-priority"));
        Assert.IsFalse(handler.Headers.ContainsKey("apns-collapse-id"));
        using var payload = JsonDocument.Parse(handler.Body!);
        var aps = payload.RootElement.GetProperty("aps");
        Assert.AreEqual(1, aps.GetProperty("content-available").GetInt32());
        Assert.AreEqual("Mesh", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.AreEqual("Message from @alice", aps.GetProperty("alert").GetProperty("body").GetString());
    }

    [TestMethod]
    public async Task AlertPushOmitsBackgroundWakeMetadata()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var sender = CreateSender(handler);

        await sender.SendAsync(
            "device-token",
            new PushAlert("Mesh", "Message from @alice", "message", PushDeliveryMode.Alert));

        Assert.AreEqual("alert", SingleHeader(handler, "apns-push-type"));
        Assert.AreEqual("10", SingleHeader(handler, "apns-priority"));
        using var payload = JsonDocument.Parse(handler.Body!);
        var aps = payload.RootElement.GetProperty("aps");
        Assert.IsFalse(aps.TryGetProperty("content-available", out _));
        Assert.IsFalse(payload.RootElement.TryGetProperty("mesh", out _));
    }

    [TestMethod]
    public async Task UnregisteredResponseMarksTokenInvalid()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent("{\"reason\":\"Unregistered\"}", Encoding.UTF8, "application/json")
        });
        using var sender = CreateSender(handler);

        var result = await sender.SendAsync(
            "device-token",
            new PushAlert("Mesh", "Message", "message"));

        Assert.AreEqual(PushSendStatus.InvalidToken, result.Status);
        Assert.AreEqual(410, result.StatusCode);
        Assert.AreEqual("Unregistered", result.Reason);
    }

    private static ApnsPushSender CreateSender(HttpMessageHandler handler)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new ApnsPushSender(
            "KEYID12345",
            "TEAMID1234",
            "net.meshrelay.mesh",
            key.ExportPkcs8PrivateKeyPem(),
            production: false,
            NullLogger.Instance,
            handler);
    }

    private static string SingleHeader(RecordingHandler handler, string name)
    {
        Assert.IsTrue(handler.Headers.TryGetValue(name, out var values));
        Assert.HasCount(1, values);
        return values[0];
    }
}
