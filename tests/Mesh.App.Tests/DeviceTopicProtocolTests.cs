using System.Text.Json;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceTopicProtocolTests
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
    public void SyncRecords_PreserveLegacyDeconstructionAndJsonDefaults()
    {
        var created = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var topic = new DeviceSyncTopic("topic-1", "Topic", created, 3);
        var (id, title, topicCreated, topicOrder) = topic;
        Assert.AreEqual(("topic-1", "Topic", created, 3), (id, title, topicCreated, topicOrder));

        var conversation = new DeviceSyncConversation(
            "alice", 2, null, null, null, null, null, null, [], 0);
        var (handle, order, serviceId, serviceName, provider, groupId, groupName, owner, members, version)
            = conversation;
        Assert.AreEqual("alice", handle);
        Assert.AreEqual(2, order);
        Assert.IsNull(serviceId);
        Assert.IsNull(serviceName);
        Assert.IsNull(provider);
        Assert.IsNull(groupId);
        Assert.IsNull(groupName);
        Assert.IsNull(owner);
        Assert.AreEqual(0, members.Count);
        Assert.AreEqual(0, version);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var oldTopic = JsonSerializer.Deserialize<DeviceSyncTopic>(
            """{"id":"old","title":"Legacy","createdAt":"2026-07-20T08:00:00Z","sortOrder":1}""",
            options)!;
        Assert.IsNull(oldTopic.ExecutionDeviceId);
        Assert.IsNull(oldTopic.ExecutionDeviceName);
        Assert.IsNull(oldTopic.ExecutionDevicePlatform);
        Assert.IsNull(oldTopic.LastActivityAt);
        Assert.IsFalse(oldTopic.IsPinned);

        var oldConversation = JsonSerializer.Deserialize<DeviceSyncConversation>(
            """{"handle":"bob","sortOrder":0,"groupMembers":[],"groupVersion":0}""",
            options)!;
        Assert.IsNull(oldConversation.CreatedAt);
        Assert.IsNull(oldConversation.LastActivityAt);
        Assert.IsFalse(oldConversation.HasActivityMetadata);
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
}
