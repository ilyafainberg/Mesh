namespace Mesh.Relay.Backplane;

/// <summary>The certainty of one directed cross-instance delivery attempt.</summary>
public enum BackplaneDeliveryOutcome
{
    NotDelivered,
    Delivered,
    Uncertain
}

/// <summary>Outcome of one directed cross-instance forward.</summary>
public readonly record struct BackplaneDeliveryReceipt(BackplaneDeliveryOutcome Outcome)
{
    public static BackplaneDeliveryReceipt NotDelivered =>
        new(BackplaneDeliveryOutcome.NotDelivered);

    public static BackplaneDeliveryReceipt Delivered =>
        new(BackplaneDeliveryOutcome.Delivered);
}

/// <summary>
/// Cross-instance routing seam for the online-only relay. When the relay runs as more than one
/// replica, the WebSocket for a given handle/device lives on exactly one instance. The backplane
/// tracks which instance holds each authenticated connection (ephemeral presence with TTL) and
/// forwards an in-flight opaque frame to the instance that can deliver it to the live socket.
///
/// This is transient pub/sub ONLY. The backplane never persists a frame, a delivery, a receipt or
/// any payload; a forward that finds no live socket simply reports NotDelivered. The default
/// in-memory implementation is a no-op single-instance seam; the Redis implementation lights up
/// multi-replica routing via presence keys with TTL and per-instance channels.
/// </summary>
public interface IBackplane
{
    /// <summary>This relay instance's stable id for the lifetime of the process.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Starts listening for frames addressed to sockets on THIS instance. The handler is invoked
    /// with (toHandle, deliveryJson) and should deliver the opaque frame to the local socket(s).
    /// </summary>
    Task StartAsync(
        Func<string, string, Task<BackplaneDeliveryReceipt>> deliverLocal,
        CancellationToken ct = default);

    /// <summary>Records that <paramref name="handle"/> is connected on this instance (renew before TTL).</summary>
    Task SetPresenceAsync(string handle, CancellationToken ct = default);

    /// <summary>Records that one specific device is connected on this instance.</summary>
    Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>Clears presence for a handle when its last socket closes on this instance.</summary>
    Task ClearPresenceAsync(string handle, CancellationToken ct = default);

    /// <summary>Clears one device's presence when its last socket closes on this instance.</summary>
    Task ClearDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>Returns the instance id currently holding the handle's socket, or null if none.</summary>
    Task<string?> GetInstanceForAsync(string handle, CancellationToken ct = default);

    /// <summary>Returns the instance id currently holding one device's socket, or null if offline.</summary>
    Task<string?> GetInstanceForDeviceAsync(string handle, string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Publishes an in-flight opaque frame to the instance that owns the handle so it can deliver it
    /// to the live socket. Returns the confirmed outcome. The payload is transient and never stored.
    /// </summary>
    Task<BackplaneDeliveryReceipt> PublishToOwnerAsync(
        string instanceId, string toHandle, string deliveryJson, CancellationToken ct = default);
}
