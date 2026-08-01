using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceTopicTransportTests
{
    [TestMethod]
    public void TopicEnvelope_PreservesExactDeviceRoutingAndCiphertext()
    {
        var envelope = MeshEnvelope.Create(
            "owner",
            "owner",
            MeshKinds.TopicRunRequest,
            "enc:v1:ciphertext",
            "signature",
            fromDevice: "source-device",
            toDevice: "target-device");

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = JsonSerializer.Deserialize<MeshEnvelope>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(parsed);
        Assert.AreEqual("source-device", parsed.FromDevice);
        Assert.AreEqual("target-device", parsed.ToDevice);
        Assert.AreEqual("enc:v1:ciphertext", parsed.Body);
        Assert.AreEqual(MeshKinds.TopicRunRequest, parsed.Kind);
    }

    [TestMethod]
    public void TopicResponseEnvelope_PreservesMetadataOnlyPushHint()
    {
        var envelope = MeshEnvelope.Create(
            "owner",
            "owner",
            MeshKinds.TopicRunUpdate,
            "enc:v1:ciphertext",
            "signature",
            fromDevice: "host-device",
            toDevice: "source-device",
            pushHint: PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Completed));

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = JsonSerializer.Deserialize<MeshEnvelope>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(PushHintProtocol.TopicResponse, parsed.PushHint);
        Assert.IsTrue(PushHintProtocol.IsTopicResponse(parsed));
        Assert.IsNull(PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Executing));
        Assert.IsNull(PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Failed));
        Assert.IsNull(PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Cancelled));
    }

    [TestMethod]
    public void AttachmentManifest_MapsIdsToTransientAttachmentsInOrder()
    {
        IReadOnlyList<ChatAttachment> attachments =
        [
            new("one.txt", "text/plain", [1, 2]),
            new("two.png", "image/png", [3])
        ];
        var ids = new[] { "attachment-one", "attachment-two" };
        var manifest = attachments.Select((item, index) =>
            new TopicRunAttachment(ids[index], item.Name, item.MimeType, item.Data.LongLength)).ToArray();
        var request = new TopicRunRequestPayload(
            "run", "thread", "line", "owner", "prompt", DateTimeOffset.UtcNow,
            "target-device", TopicTurnMode.Single, Attachments: manifest, AttachmentIds: ids);

        Assert.IsTrue(TopicRunProtocol.TryParseRequest(TopicRunProtocol.RequestBody(request), out var parsed));
        CollectionAssert.AreEqual(ids, parsed.AttachmentIds!.ToArray());
        Assert.AreEqual(attachments[0].Data.LongLength, parsed.Attachments![0].Length);
        Assert.AreEqual(attachments[1].MimeType, parsed.Attachments[1].MimeType);
    }

    [TestMethod]
    public void TopicKinds_AreDistinctFromLegacyHomeAgentKinds()
    {
        Assert.AreNotEqual(MeshKinds.RemoteAgentRequest, MeshKinds.TopicRunRequest);
        Assert.AreNotEqual(MeshKinds.RemoteAgentResponse, MeshKinds.TopicRunUpdate);
        Assert.AreEqual("attachment.chunk", MeshKinds.AttachmentChunk);
    }

    [TestMethod]
    public void Request_WithDuplicateAttachmentIds_IsRejected()
    {
        var attachments = new[]
        {
            new TopicRunAttachment("same", "one.txt", "text/plain", 1),
            new TopicRunAttachment("same", "two.txt", "text/plain", 1)
        };
        var request = new TopicRunRequestPayload(
            "run", "thread", "line", "owner", "prompt", DateTimeOffset.UtcNow,
            "target-device", TopicTurnMode.Single, Attachments: attachments);

        Assert.IsFalse(TopicRunProtocol.TryParseRequest(TopicRunProtocol.RequestBody(request), out _));
    }

    [TestMethod]
    public void Chunk_OverProtocolLimit_IsRejected()
    {
        var chunk = new AttachmentChunkPayload(
            "run",
            "attachment",
            0,
            1,
            new byte[AttachmentChunkProtocol.MaxChunkBytes + 1],
            "file.bin",
            "application/octet-stream");

        Assert.IsFalse(TopicRunProtocol.TryParseChunk(TopicRunProtocol.ChunkBody(chunk), out _));
    }
}
