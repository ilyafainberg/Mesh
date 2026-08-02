using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Converts a peer account's local conversation perspective into the receiving account's
/// perspective. Same-account sibling replication is unchanged.
/// </summary>
public static class ReplicationInboundProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ReplicationPayloadCodec.DomainEnvelope ForLocalAccount(
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        string localAccount)
    {
        var origin = Normalize(evt.OriginAccount);
        if (origin.Length == 0 || string.Equals(origin, Normalize(localAccount), StringComparison.Ordinal))
            return envelope;
        if (envelope.Kind != ReplicationOpKinds.Message)
            return envelope;

        var projected = envelope with
        {
            EntityId = origin,
            ConversationId = origin
        };
        if (envelope.Action is not (
                ReplicationPayloadCodec.DomainAction.AppendLine
                or ReplicationPayloadCodec.DomainAction.Upsert))
            return projected;

        var line = JsonSerializer.Deserialize<ChatLine>(envelope.BodyJson, Json)
            ?? throw new ReplicationProjectionException("Replicated peer message body was invalid.");
        line.Role = "user";
        line.Status = "";
        line.SenderHandle = origin;
        return projected with { BodyJson = JsonSerializer.Serialize(line, Json) };
    }

    private static string Normalize(string value)
        => value.Trim().TrimStart('@').ToLowerInvariant();
}
