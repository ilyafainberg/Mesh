namespace Mesh.Relay.Backplane;

/// <summary>
/// Cross-instance routing seam for the relay. When the relay runs as more than one
/// replica, the WebSocket for a given handle lives on exactly one instance. The
/// backplane tracks which instance holds each handle (presence) and forwards a
/// message to the instance that can actually deliver it.
///
/// The default in-memory implementation is a no-op suitable for a single instance;
/// the Redis implementation lights up multi-replica routing via pub/sub + presence
/// keys with TTL.
/// </summary>
public interface IBackplane
{
    /// <summary>This relay instance's stable id for the lifetime of the process.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Starts listening for messages addressed to sockets on THIS instance. The handler
    /// is invoked with (toHandle, envelopeJson) and should deliver to the local socket.
    /// </summary>
    Task StartAsync(Func<string, string, Task<bool>> deliverLocal, CancellationToken ct = default);

    /// <summary>Records that <paramref name="handle"/> is connected on this instance (renew before TTL).</summary>
    Task SetPresenceAsync(string handle, CancellationToken ct = default);

    /// <summary>Records that one specific device is connected on this instance.</summary>
    Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>Clears presence for a handle when its socket closes on this instance.</summary>
    Task ClearPresenceAsync(string handle, CancellationToken ct = default);

    /// <summary>Clears one device's presence when its last socket closes on this instance.</summary>
    Task ClearDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>Returns the instance id currently holding the handle's socket, or null if none.</summary>
    Task<string?> GetInstanceForAsync(string handle, CancellationToken ct = default);

    /// <summary>Returns the instance id currently holding one device's socket, or null if offline.</summary>
    Task<string?> GetInstanceForDeviceAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Publishes a message to the instance that owns the handle so it can deliver it to the
    /// live socket. Returns true only when the owning instance confirms local delivery.
    /// </summary>
    Task<bool> PublishToOwnerAsync(string instanceId, string toHandle, string envelopeJson, CancellationToken ct = default);
}
