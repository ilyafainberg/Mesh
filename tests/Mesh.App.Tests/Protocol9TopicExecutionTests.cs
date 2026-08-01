using System.Text.Json;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class Protocol9TopicExecutionTests
{
    [TestMethod]
    public void Request_RoundTripsWithStableStringEnums()
    {
        var request = new TopicRunRequestPayload(
            "run-1",
            "thread-1",
            "line-1",
            "owner",
            "Do the work",
            new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
            "device-1",
            TopicTurnMode.Autonomous,
            Attachments:
            [
                new TopicRunAttachment("attachment-1", "notes.txt", "text/plain", 42)
            ]);

        var body = TopicRunProtocol.RequestBody(request);

        StringAssert.Contains(body, "\"turnMode\":\"autonomous\"");
        Assert.IsTrue(TopicRunProtocol.TryParseRequest(body, out var parsed));
        Assert.AreEqual(request.RunId, parsed.RunId);
        Assert.AreEqual("line-1", parsed.TriggerLineId);
        Assert.AreEqual(request.TurnMode, parsed.TurnMode);
        Assert.AreEqual("attachment-1", parsed.Attachments![0].Id);
        Assert.IsFalse(TopicRunProtocol.TryParseRequest(
            body.Replace("\"autonomous\"", "1", StringComparison.Ordinal),
            out _));
    }

    [TestMethod]
    public void Update_RoundTripsStreamingDeltaAndRejectsInvalid()
    {
        var update = new TopicRunUpdatePayload(
            "run-1",
            "thread-1",
            TopicRunPhase.Executing,
            Timestamp: new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
            DeltaSeq: 3,
            DeltaKind: TopicRunDeltaKind.Answer,
            Delta: "hello",
            TriggerLineId: "line-1");

        var body = TopicRunProtocol.UpdateBody(update);

        StringAssert.Contains(body, "\"deltaKind\":\"answer\"");
        Assert.IsTrue(TopicRunProtocol.TryParseUpdate(body, out var parsed));
        Assert.AreEqual(3, parsed.DeltaSeq);
        Assert.AreEqual(TopicRunDeltaKind.Answer, parsed.DeltaKind);
        Assert.AreEqual("hello", parsed.Delta);
        Assert.AreEqual("line-1", parsed.TriggerLineId);

        // A fragment with no stream kind is rejected.
        Assert.IsFalse(TopicRunProtocol.TryParseUpdate(
            body.Replace("\"deltaKind\":\"answer\"", "\"deltaKind\":null", StringComparison.Ordinal),
            out _));
        // Integer enum values are rejected (stable string enums only).
        Assert.IsFalse(TopicRunProtocol.TryParseUpdate(
            body.Replace("\"answer\"", "1", StringComparison.Ordinal),
            out _));
        // A fragment must carry a positive per-run sequence number.
        Assert.IsFalse(TopicRunProtocol.TryParseUpdate(
            TopicRunProtocol.UpdateBody(update with { DeltaSeq = 0 }),
            out _));
        Assert.IsFalse(TopicRunProtocol.TryParseUpdate(
            TopicRunProtocol.UpdateBody(update with { TriggerLineId = "" }),
            out _));
    }

    [TestMethod]
    public void ChunkParser_EnforcesChunkBoundsAndMetadata()
    {
        var valid = new AttachmentChunkPayload(
            "run-1", "attachment-1", 0, 1,
            new byte[AttachmentChunkProtocol.MaxChunkBytes],
            "notes.txt", "text/plain");

        Assert.IsTrue(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with
            {
                Data = new byte[AttachmentChunkProtocol.MaxChunkBytes + 1]
            }), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with
            {
                Count = AttachmentChunkProtocol.MaxChunks + 1
            }), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with { MimeType = "" }), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with { RunId = "" }), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with { AttachmentId = "" }), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with { Index = 1 }), out _));
        Assert.IsFalse(TopicRunProtocol.TryParseChunk(
            TopicRunProtocol.ChunkBody(valid with { Data = [] }), out _));
    }

    [TestMethod]
    public void AgentReadyAliases_AreNotSerialized()
    {
        var device = new DeviceInfo(
            "device-1", "Phone", true, DevicePlatforms.Android, RemoteAgentEnabled: true);

        var json = JsonSerializer.Serialize(device);

        Assert.IsTrue(device.IsAgentReady);
        Assert.IsTrue(device.AgentReady);
        Assert.IsFalse(json.Contains("agentReady", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(json, "RemoteAgentEnabled");
    }

    [TestMethod]
    public void Protocol_RequiresLineIdentityTimestampAndStableItemStates()
    {
        Assert.AreEqual("topic.attachment.chunk", MeshKinds.TopicAttachmentChunk);
        Assert.AreEqual("attachment.chunk", MeshKinds.AttachmentChunk);

        var request = new TopicRunRequestPayload(
            "run", "thread", "line", "owner", "prompt",
            new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
            "device", TopicTurnMode.Single,
            WidgetContext: """{"selection":"bounded context"}""");
        var requestBody = TopicRunProtocol.RequestBody(request);
        Assert.IsTrue(TopicRunProtocol.TryParseRequest(requestBody, out _));
        Assert.IsFalse(TopicRunProtocol.TryParseRequest(
            requestBody.Replace("\"triggerLineId\":\"line\",", "", StringComparison.Ordinal),
            out _));
        Assert.IsFalse(TopicRunProtocol.TryParseRequest(
            requestBody.Replace("\"triggerText\":\"prompt\"", "\"triggerText\":\"\"",
                StringComparison.Ordinal),
            out _));

        var update = new TopicRunUpdatePayload(
            "run", "thread", TopicRunPhase.Executing,
            Subtasks: [new TopicRunSubtask("item", "Work", TopicRunItemState.Running)],
            Timestamp: new DateTimeOffset(2026, 7, 21, 10, 1, 0, TimeSpan.Zero));
        var updateBody = TopicRunProtocol.UpdateBody(update);
        StringAssert.Contains(updateBody, "\"state\":\"running\"");
        Assert.IsTrue(TopicRunProtocol.TryParseUpdate(updateBody, out _));
        Assert.IsFalse(TopicRunProtocol.TryParseUpdate(
            """{"runId":"run","threadId":"thread","phase":"executing"}""",
            out _));
        Assert.IsFalse(TopicRunProtocol.TryParseUpdate(
            updateBody.Replace("\"running\"", "1", StringComparison.Ordinal),
            out _));
    }

    [TestMethod]
    public void CanHostRemoteTurn_RequiresDesktopAndAgentReady()
    {
        // Directory honesty: only a desktop that advertised the capability may host a remote turn.
        Assert.IsTrue(DevicePlatforms.CanHostRemoteAgent(true, DevicePlatforms.Windows));
        Assert.IsTrue(DevicePlatforms.CanHostRemoteAgent(true, DevicePlatforms.MacOS));
        Assert.IsFalse(DevicePlatforms.CanHostRemoteAgent(true, DevicePlatforms.Android));
        Assert.IsFalse(DevicePlatforms.CanHostRemoteAgent(true, DevicePlatforms.IOS));
        Assert.IsFalse(DevicePlatforms.CanHostRemoteAgent(true, null));
        Assert.IsFalse(DevicePlatforms.CanHostRemoteAgent(false, DevicePlatforms.Windows));

        var phone = new DeviceInfo("d1", "Phone", true, DevicePlatforms.Android, RemoteAgentEnabled: true);
        var desktop = new DeviceInfo("d2", "PC", true, DevicePlatforms.Windows, RemoteAgentEnabled: true);
        var desktopNoModel = new DeviceInfo("d3", "PC2", true, DevicePlatforms.Windows, RemoteAgentEnabled: false);
        Assert.IsFalse(phone.CanHostRemoteTurn, "a mobile device is never an eligible remote host");
        Assert.IsTrue(desktop.CanHostRemoteTurn);
        Assert.IsFalse(desktopNoModel.CanHostRemoteTurn);
    }
}
