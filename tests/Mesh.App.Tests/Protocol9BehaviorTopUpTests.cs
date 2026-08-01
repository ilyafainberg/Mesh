using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class Protocol9BehaviorTopUpTests
{
    [DataTestMethod]
    [DataRow(ReplicationOpKinds.Message, nameof(ReplicationPayloadCodec.DomainAction.Upsert))]
    [DataRow(ReplicationOpKinds.Message, nameof(ReplicationPayloadCodec.DomainAction.Delete))]
    [DataRow(ReplicationOpKinds.Message, nameof(ReplicationPayloadCodec.DomainAction.AppendLine))]
    [DataRow(ReplicationOpKinds.Conversation, nameof(ReplicationPayloadCodec.DomainAction.Upsert))]
    [DataRow(ReplicationOpKinds.Conversation, nameof(ReplicationPayloadCodec.DomainAction.Delete))]
    [DataRow(ReplicationOpKinds.Topic, nameof(ReplicationPayloadCodec.DomainAction.Upsert))]
    [DataRow(ReplicationOpKinds.Topic, nameof(ReplicationPayloadCodec.DomainAction.Delete))]
    [DataRow(ReplicationOpKinds.Topic, nameof(ReplicationPayloadCodec.DomainAction.AppendLine))]
    [DataRow(ReplicationOpKinds.Contact, nameof(ReplicationPayloadCodec.DomainAction.Upsert))]
    [DataRow(ReplicationOpKinds.Contact, nameof(ReplicationPayloadCodec.DomainAction.Delete))]
    [DataRow(ReplicationOpKinds.Circle, nameof(ReplicationPayloadCodec.DomainAction.Upsert))]
    [DataRow(ReplicationOpKinds.Circle, nameof(ReplicationPayloadCodec.DomainAction.Delete))]
    [DataRow(ReplicationOpKinds.Memory, nameof(ReplicationPayloadCodec.DomainAction.Upsert))]
    [DataRow(ReplicationOpKinds.Memory, nameof(ReplicationPayloadCodec.DomainAction.Delete))]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.AssetUpsert))]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.AssetDelete))]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.PackageTransfer))]
    [DataRow(ReplicationOpKinds.AskUser, nameof(ReplicationPayloadCodec.DomainAction.AskUserPrompt))]
    [DataRow(ReplicationOpKinds.AskUser, nameof(ReplicationPayloadCodec.DomainAction.AskUserResolve))]
    [DataRow(ReplicationOpKinds.ReadWatermark, nameof(ReplicationPayloadCodec.DomainAction.ReadWatermark))]
    public void OperationMap_AllowsExpectedAction(string kind, string actionName)
    {
        var action = Enum.Parse<ReplicationPayloadCodec.DomainAction>(actionName);
        Assert.IsTrue(ReplicationPayloadCodec.IsMappedAction(kind, action));
    }

    [DataTestMethod]
    [DataRow(ReplicationOpKinds.Contact, nameof(ReplicationPayloadCodec.DomainAction.Upsert), "alice", "{}")]
    [DataRow(ReplicationOpKinds.Contact, nameof(ReplicationPayloadCodec.DomainAction.Delete), "alice", "{}")]
    [DataRow(ReplicationOpKinds.Circle, nameof(ReplicationPayloadCodec.DomainAction.Upsert), "friends", "{\"name\":\"Friends\"}")]
    [DataRow(ReplicationOpKinds.Memory, nameof(ReplicationPayloadCodec.DomainAction.Upsert), "memory-1", "{\"title\":\"T\"}")]
    [DataRow(ReplicationOpKinds.Topic, nameof(ReplicationPayloadCodec.DomainAction.AppendLine), "topic-1", "{\"line\":1}")]
    [DataRow(ReplicationOpKinds.Message, nameof(ReplicationPayloadCodec.DomainAction.AppendLine), "conversation-1", "{\"line\":1}")]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.AssetDelete), "asset-1", "{}")]
    [DataRow(ReplicationOpKinds.AskUser, nameof(ReplicationPayloadCodec.DomainAction.AskUserPrompt), "prompt-1", "{}")]
    [DataRow(ReplicationOpKinds.ReadWatermark, nameof(ReplicationPayloadCodec.DomainAction.ReadWatermark), "thread-1", "{}")]
    public void EnvelopeCodec_RoundTripsMappedEnvelope(string kind, string actionName, string entityId, string body)
    {
        var action = Enum.Parse<ReplicationPayloadCodec.DomainAction>(actionName);
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            kind, action, entityId, "conversation-1", "cv-1", body);

        var decoded = ReplicationPayloadCodec.DecodeEnvelope(ReplicationPayloadCodec.EncodeEnvelope(envelope));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(kind, decoded.Kind);
        Assert.AreEqual(action, decoded.Action);
        Assert.AreEqual(entityId, decoded.EntityId);
        Assert.AreEqual(body, decoded.BodyJson);
    }

    [DataTestMethod]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.AssetUpsert), true)]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.PackageTransfer), true)]
    [DataRow(ReplicationOpKinds.Asset, nameof(ReplicationPayloadCodec.DomainAction.AssetDelete), false)]
    [DataRow(ReplicationOpKinds.Topic, nameof(ReplicationPayloadCodec.DomainAction.Upsert), false)]
    [DataRow(ReplicationOpKinds.Memory, nameof(ReplicationPayloadCodec.DomainAction.Upsert), false)]
    public void DesktopRequirement_IsLimitedToLocalByteTransfers(string kind, string actionName, bool expected)
    {
        var action = Enum.Parse<ReplicationPayloadCodec.DomainAction>(actionName);
        Assert.AreEqual(expected, ReplicationPayloadCodec.RequiresDesktop(kind, action));
    }

    [DataTestMethod]
    [DataRow("accepted", true)]
    [DataRow(TopicExecutionStatus.Delivered, true)]
    [DataRow(TopicExecutionStatus.PendingLocal, false)]
    [DataRow(TopicExecutionStatus.LocalQueued, false)]
    [DataRow("rejected", false)]
    public void TopicDispatchResult_PreservesAcceptedFlagAndCode(string code, bool accepted)
    {
        var result = accepted ? TopicDispatchResult.Ok("run-1", code) : TopicDispatchResult.Reject(code, "run-1");

        Assert.AreEqual(accepted, result.Accepted);
        Assert.AreEqual("run-1", result.RunId);
        Assert.AreEqual(code, result.Code);
    }

    [DataTestMethod]
    [DataRow("2026-08-01T00:00:00Z", "2026-08-01T00:00:01Z", false)]
    [DataRow("2026-08-01T00:00:01Z", "2026-08-01T00:00:00Z", true)]
    [DataRow("2026-08-01T00:00:00Z", "2026-08-01T00:00:00Z", false)]
    [DataRow("2026-08-01T00:00:00Z", null, true)]
    public void ProjectionVersion_OrdersStableDomainVersions(string candidateAt, string? currentAt, bool expected)
    {
        var candidate = ProjectionVersion.Create(DateTimeOffset.Parse(candidateAt), "device", candidateAt);
        var current = currentAt is null
            ? null
            : ProjectionVersion.Create(DateTimeOffset.Parse(currentAt), "device", currentAt);

        Assert.AreEqual(expected, ProjectionVersion.IsNewer(candidate, current));
    }
}
