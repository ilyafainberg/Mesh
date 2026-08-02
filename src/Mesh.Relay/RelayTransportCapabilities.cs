using Mesh.Shared;

namespace Mesh.Relay;

public sealed record RelayTransportCapabilities(
    int ProtocolVersion,
    bool OnlineOnly,
    bool DurablePayloadStorage,
    bool MetadataStore,
    bool SendResults,
    bool EphemeralDelivery,
    bool PresenceResolution,
    bool Fanout,
    bool Replication,
    bool OnlineDelivery,
    bool OnlineReplication,
    bool OnlineWake,
    bool DeviceRevocation,
    bool AgentHost,
    bool ContentlessPush,
    int MaxTransportBytes,
    int MaxFanoutRecipients)
{
    public static RelayTransportCapabilities Protocol9(bool metadataStore)
        => new(
            MeshProtocol.Version,
            OnlineOnly: true,
            DurablePayloadStorage: false,
            MetadataStore: metadataStore,
            SendResults: true,
            EphemeralDelivery: true,
            PresenceResolution: true,
            Fanout: true,
            Replication: true,
            OnlineDelivery: true,
            OnlineReplication: true,
            OnlineWake: true,
            DeviceRevocation: true,
            AgentHost: true,
            ContentlessPush: true,
            OnlineReplicationLimits.MaxTransportBytes,
            FanoutProtocol.MaxRecipients);
}
