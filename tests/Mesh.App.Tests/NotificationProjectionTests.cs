using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;

namespace Mesh.App.Tests;

[TestClass]
public sealed class NotificationProjectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void PeerMessageProjection_RewritesNotificationToSenderConversation()
    {
        var intent = NotificationIntents.Message("line-1", "bob", "Alice", "secret body");
        Assert.AreEqual("message:line-1", intent.StableId);
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob", "bob", "v1",
            JsonSerializer.Serialize(new ChatLine
            {
                Id = "line-1", Role = "assistant", Text = "secret body", Status = "sent"
            }, Json),
            intent);

        var projected = ReplicationInboundProjection.ForLocalAccount(
            Event("Alice", "bob"), envelope, "Bob");

        Assert.AreEqual("alice", projected.EntityId);
        Assert.AreEqual("alice", projected.ConversationId);
        Assert.IsNotNull(projected.NotificationIntent);
        Assert.AreEqual("alice", projected.NotificationIntent.EntityId);
        Assert.AreEqual("alice", projected.NotificationIntent.ConversationId);
        Assert.AreEqual(NotificationRoutes.Messages("alice"), projected.NotificationIntent.Route);
        Assert.AreEqual(intent.StableId, projected.NotificationIntent.StableId);
        var line = JsonSerializer.Deserialize<ChatLine>(projected.BodyJson, Json);
        Assert.IsNotNull(line);
        Assert.AreEqual("user", line.Role);
        Assert.AreEqual("", line.Status);
        Assert.AreEqual("alice", line.SenderHandle);
    }

    [TestMethod]
    public void OwnerSiblingProjection_PreservesNotificationPerspective()
    {
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message,
            ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob", "bob", "v1",
            JsonSerializer.Serialize(new ChatLine { Id = "line-1", Text = "body" }, Json),
            NotificationIntents.Message("line-1", "bob", "Alice", "body"));

        var projected = ReplicationInboundProjection.ForLocalAccount(
            Event("Alice", "bob"), envelope, "@ALICE");

        Assert.AreSame(envelope, projected);
    }

    [TestMethod]
    public void DomainEnvelope_RoundTripsNotificationIntent()
    {
        var intent = NotificationIntents.Ask("prompt/1", "topic 1", "Choose one");
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.AskUser,
            ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            "prompt/1", "topic 1", "v1", "{}", intent);

        var decoded = ReplicationPayloadCodec.DecodeEnvelope(
            ReplicationPayloadCodec.EncodeEnvelope(envelope));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(intent, decoded.NotificationIntent);
    }

    [DataTestMethod]
    [DataRow("mesh://messages/alice%20smith", NotificationRouteKind.Messages, "alice smith", null)]
    [DataRow("mesh://me/topic%2Fone", NotificationRouteKind.Topic, "topic/one", null)]
    [DataRow("mesh://me/topic%201/ask/prompt%2F1", NotificationRouteKind.Ask, "topic 1", "prompt/1")]
    [DataRow("mesh://requests", NotificationRouteKind.Requests, null, null)]
    [DataRow("mesh://approvals", NotificationRouteKind.Approvals, null, null)]
    public void Routes_ParseExactTargets(
        string raw, NotificationRouteKind kind, string? entityId, string? promptId)
    {
        Assert.IsTrue(NotificationRouteParser.TryParse(raw, out var route));
        Assert.AreEqual(kind, route.Kind);
        Assert.AreEqual(entityId, route.EntityId);
        Assert.AreEqual(promptId, route.PromptId);
    }

    [DataTestMethod]
    [DataRow("https://mesh/messages/alice")]
    [DataRow("mesh://messages")]
    [DataRow("mesh://messages/alice/extra")]
    [DataRow("mesh://me/topic/ask")]
    [DataRow("mesh://requests/extra")]
    public void Routes_RejectNonExactTargets(string raw)
        => Assert.IsFalse(NotificationRouteParser.TryParse(raw, out _));

    private static ReplicationEvent Event(string originAccount, string entityId)
        => new(
            "event-1", entityId, originAccount, "device-1", "epoch-1", 1, 1,
            ReplicationOpKinds.Message, entityId, "v1",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "ciphertext", "hash", "signature");
}
