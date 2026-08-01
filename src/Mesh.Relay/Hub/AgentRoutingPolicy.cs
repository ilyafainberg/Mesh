using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// Pure agent-routing directory logic over a handle's device metadata. The relay stores only which
/// device a handle designates as its primary/failover agent host; it never persists or observes agent
/// request or response payloads. Selection reads device platform and capability flags from the metadata
/// store so clients can discover where to send an opaque agent frame over the online switchboard.
/// </summary>
public static class AgentRoutingPolicy
{
    public static string? EffectivePrimaryDeviceId(StoredHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!string.IsNullOrWhiteSpace(handle.AgentPrimaryDeviceId))
            return handle.AgentPrimaryDeviceId;

        return handle.DevicePublicKeys
            .Select(DeviceProtocol.DeviceId)
            .FirstOrDefault(deviceId => IsSelectableDevice(handle, deviceId));
    }

    public static bool IsSelectableDevice(StoredHandle handle, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var registered = handle.DevicePublicKeys.Any(publicKey =>
            string.Equals(DeviceProtocol.DeviceId(publicKey), deviceId, StringComparison.Ordinal));
        return registered
            && DevicePlatforms.IsDesktop(handle.DevicePlatforms.GetValueOrDefault(deviceId))
            && handle.DeviceAgentHostEnabled.GetValueOrDefault(deviceId);
    }

    public static bool IsExecutionReady(StoredHandle handle, string? deviceId)
        => IsSelectableDevice(handle, deviceId)
           && handle.DeviceRemoteAgentEnabled.GetValueOrDefault(deviceId!);

    public static string? ChooseOnlineDevice(StoredHandle handle, IReadOnlySet<string> onlineDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(onlineDeviceIds);
        var primary = EffectivePrimaryDeviceId(handle);
        if (IsExecutionReady(handle, primary) && onlineDeviceIds.Contains(primary!))
            return primary;

        var failover = handle.AgentFailoverDeviceId;
        return !string.Equals(primary, failover, StringComparison.Ordinal)
               && IsExecutionReady(handle, failover)
               && onlineDeviceIds.Contains(failover!)
            ? failover
            : null;
    }

    public static AgentRoutingInfo ToInfo(StoredHandle handle)
        => new(
            EffectivePrimaryDeviceId(handle),
            handle.AgentFailoverDeviceId,
            handle.AgentRoutingVersion,
            handle.AgentPrimaryWasSelectedAutomatically);
}
