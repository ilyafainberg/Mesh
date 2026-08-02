using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ReplicationInboundProjectionTests
{
    [TestMethod]
    public void PeerMessage_MapsConversationToOrigin_AndFlipsDirection()
    {
        var line = new ChatLine
        {
            Id = "line-1",
            Role = "assistant",
            Via = "person",
            Text = "hello",
            Status = "sent"
        };
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob",
            "bob",
            "v1",
            JsonSerializer.Serialize(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var evt = Event("alice", ReplicationOpKinds.Message, "bob");

        var projected = ReplicationInboundProjection.ForLocalAccount(evt, envelope, "bob");
        var projectedLine = JsonSerializer.Deserialize<ChatLine>(
            projected.BodyJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.AreEqual("alice", projected.EntityId);
        Assert.AreEqual("alice", projected.ConversationId);
        Assert.AreEqual("user", projectedLine.Role);
        Assert.AreEqual("person", projectedLine.Via);
        Assert.AreEqual("alice", projectedLine.SenderHandle);
        Assert.AreEqual("", projectedLine.Status);
    }

    [TestMethod]
    public void SameAccountSiblingMessage_KeepsLocalPerspective()
    {
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob",
            "bob",
            "v1",
            "{}");

        Assert.AreSame(
            envelope,
            ReplicationInboundProjection.ForLocalAccount(
                Event("alice", ReplicationOpKinds.Message, "bob"), envelope, "alice"));
    }

    private static ReplicationEvent Event(string origin, string kind, string entity)
        => new(
            "event", entity, origin, "device", "epoch", 1, 0, kind, entity,
            "v1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "cipher", "hash", "signature");
}
