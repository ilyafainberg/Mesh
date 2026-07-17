using System.Collections.Concurrent;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// Per-node registry of live hub connections. Maps each SignalR connection id to the handle
/// it authenticated as, and each handle to its set of local connection ids, so the router can
/// deliver a message to every device of a recipient that is connected to THIS instance.
///
/// This is intentionally per-instance (connections cannot be persisted). Cross-instance
/// delivery is handled by the directed backplane using presence, not by this registry.
/// </summary>
public sealed class ConnectionRegistry
{
    /// <summary>State tracked for a single connection while it is open.</summary>
    public sealed class ConnState
    {
        public string? Handle { get; set; }
        public string? PublicKey { get; set; }

        /// <summary>
        /// Stable short device id derived from this connection's authenticated device public key
        /// (see <see cref="DeviceProtocol.DeviceId"/>). Set at authentication so the router can
        /// target one specific device of a handle (MeshEnvelope.ToDevice) and so the directory can
        /// report which devices are online.
        /// </summary>
        public string? DeviceId { get; set; }
        public string Nonce { get; set; } = "";
        public bool Authenticated { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConnState> byConnection = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> byHandle =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a freshly connected (not yet authenticated) connection with its nonce.</summary>
    public void Add(string connectionId, string handle, string nonce)
        => byConnection[connectionId] = new ConnState { Handle = handle, Nonce = nonce };

    public ConnState? Get(string connectionId)
        => byConnection.TryGetValue(connectionId, out var s) ? s : null;

    /// <summary>Marks a connection authenticated and indexes it under its handle for delivery.</summary>
    public void MarkAuthenticated(string connectionId, string publicKey)
    {
        if (!byConnection.TryGetValue(connectionId, out var s) || s.Handle is null) return;
        s.PublicKey = publicKey;
        s.DeviceId = DeviceProtocol.DeviceId(publicKey);
        s.Authenticated = true;
        byHandle.GetOrAdd(s.Handle, _ => new()).TryAdd(connectionId, 0);
    }

    /// <summary>
    /// Removes a connection on disconnect. Returns the handle to clear from presence only when
    /// this was its last local connection (so another device on this node keeps it present).
    /// </summary>
    public string? Remove(string connectionId)
    {
        if (!byConnection.TryRemove(connectionId, out var s) || s.Handle is null) return null;
        if (byHandle.TryGetValue(s.Handle, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty) byHandle.TryRemove(s.Handle, out _);
        }
        return s.Authenticated && !HandleHasLocalConnections(s.Handle) ? s.Handle : null;
    }

    /// <summary>All local connection ids currently authenticated for a handle.</summary>
    public IReadOnlyCollection<string> ConnectionsFor(string handle)
        => byHandle.TryGetValue(handle, out var set) ? set.Keys.ToArray() : Array.Empty<string>();

    /// <summary>
    /// The local connection ids for a handle whose authenticated device id matches
    /// <paramref name="deviceId"/>. Used to route an envelope to ONE specific device of a handle.
    /// </summary>
    public IReadOnlyCollection<string> ConnectionsForDevice(string handle, string deviceId)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Where(c => byConnection.TryGetValue(c, out var s) && s.DeviceId == deviceId)
            .ToArray();
    }

    /// <summary>The distinct device ids of a handle's authenticated connections on this instance.</summary>
    public IReadOnlyCollection<string> OnlineDeviceIds(string handle)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Select(c => byConnection.TryGetValue(c, out var s) ? s.DeviceId : null)
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct()
            .ToArray();
    }

    /// <summary>Every handle with at least one authenticated connection on this instance.</summary>
    public IReadOnlyCollection<string> LocalHandles() => byHandle.Keys.ToArray();

    /// <summary>Every distinct authenticated (handle, device) pair connected to this instance.</summary>
    public IReadOnlyCollection<(string Handle, string DeviceId)> LocalDevices()
        => byConnection.Values
            .Where(s => s.Authenticated && s.Handle is not null && s.DeviceId is not null)
            .Select(s => (s.Handle!, s.DeviceId!))
            .Distinct()
            .ToArray();

    private bool HandleHasLocalConnections(string handle)
        => byHandle.TryGetValue(handle, out var set) && !set.IsEmpty;
}
