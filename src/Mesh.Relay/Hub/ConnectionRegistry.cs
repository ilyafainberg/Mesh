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
        public bool SupportsDurableDelivery { get; init; }
        public bool IsBackgroundSync { get; init; }
        public SemaphoreSlim DeliveryGate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, ConnState> byConnection = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> byHandle =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a freshly connected (not yet authenticated) connection with its nonce.</summary>
    public void Add(
        string connectionId, string handle, string nonce,
        bool supportsDurableDelivery = false, bool isBackgroundSync = false)
        => byConnection[connectionId] = new ConnState
        {
            Handle = handle,
            Nonce = nonce,
            SupportsDurableDelivery = supportsDurableDelivery,
            IsBackgroundSync = isBackgroundSync
        };

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

    public IReadOnlyList<string> RevokeDevice(string handle, string deviceId)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return [];
        var revoked = new List<string>();
        foreach (var connectionId in set.Keys)
        {
            if (!byConnection.TryGetValue(connectionId, out var state)
                || !string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal))
                continue;
            state.Authenticated = false;
            set.TryRemove(connectionId, out _);
            revoked.Add(connectionId);
        }
        if (set.IsEmpty) byHandle.TryRemove(handle, out _);
        return revoked;
    }

    public IReadOnlyList<string> RevokeUnauthorizedDevices(
        string handle,
        IReadOnlySet<string> authorizedPublicKeys)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return [];
        var revokedDeviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connectionId in set.Keys)
        {
            if (!byConnection.TryGetValue(connectionId, out var state))
            {
                set.TryRemove(connectionId, out _);
                continue;
            }
            if (state.Authenticated
                && state.PublicKey is not null
                && authorizedPublicKeys.Contains(state.PublicKey))
                continue;

            state.Authenticated = false;
            set.TryRemove(connectionId, out _);
            if (!string.IsNullOrWhiteSpace(state.DeviceId))
                revokedDeviceIds.Add(state.DeviceId);
        }
        if (set.IsEmpty) byHandle.TryRemove(handle, out _);
        return revokedDeviceIds.ToArray();
    }
    /// <summary>
    /// Removes a connection on disconnect. Returns the handle to clear from presence only when
    /// this was its last foreground connection (so background drains never hold presence open).
    /// </summary>
    public string? Remove(string connectionId)
    {
        if (!byConnection.TryRemove(connectionId, out var s) || s.Handle is null) return null;
        if (byHandle.TryGetValue(s.Handle, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty) byHandle.TryRemove(s.Handle, out _);
        }
        return s.Authenticated
            && !s.IsBackgroundSync
            && !HandleHasLocalConnections(s.Handle, includeBackgroundSync: false)
                ? s.Handle
                : null;
    }

    /// <summary>All local connection ids currently authenticated for a handle.</summary>
    public IReadOnlyCollection<string> ConnectionsFor(string handle, bool includeBackgroundSync = true)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Where(id => byConnection.TryGetValue(id, out var state)
                && state.Authenticated
                && (includeBackgroundSync || !state.IsBackgroundSync))
            .ToArray();
    }

    /// <summary>
    /// The local connection ids for a handle whose authenticated device id matches
    /// <paramref name="deviceId"/>. Used to route an envelope to ONE specific device of a handle.
    /// </summary>
    public IReadOnlyCollection<string> ConnectionsForDevice(
        string handle, string deviceId, bool includeBackgroundSync = true)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Where(c => byConnection.TryGetValue(c, out var s)
                && s.Authenticated
                && s.DeviceId == deviceId
                && (includeBackgroundSync || !s.IsBackgroundSync))
            .ToArray();
    }

    public bool SupportsDurableDelivery(string connectionId)
        => byConnection.TryGetValue(connectionId, out var state)
           && state.Authenticated
           && state.SupportsDurableDelivery;

    /// <summary>The distinct device ids of a handle's authenticated connections on this instance.</summary>
    public IReadOnlyCollection<string> OnlineDeviceIds(string handle)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Select(c => byConnection.TryGetValue(c, out var s) && s.Authenticated ? s.DeviceId : null)
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct()
            .ToArray();
    }

    /// <summary>Every handle with at least one authenticated connection on this instance.</summary>
    public IReadOnlyCollection<string> LocalHandles(bool includeBackgroundSync = true)
    {
        if (includeBackgroundSync) return byHandle.Keys.ToArray();
        return byHandle
            .Where(pair => pair.Value.Keys.Any(id =>
                byConnection.TryGetValue(id, out var state) && !state.IsBackgroundSync))
            .Select(pair => pair.Key)
            .ToArray();
    }

    /// <summary>Every distinct authenticated (handle, device) pair connected to this instance.</summary>
    public IReadOnlyCollection<(string Handle, string DeviceId)> LocalDevices(
        bool includeBackgroundSync = true)
        => byConnection.Values
            .Where(s => s.Authenticated
                && s.Handle is not null
                && s.DeviceId is not null
                && (includeBackgroundSync || !s.IsBackgroundSync))
            .Select(s => (s.Handle!, s.DeviceId!))
            .Distinct()
            .ToArray();

    /// <summary>Every distinct device with an authenticated background-sync socket on this instance.</summary>
    public IReadOnlyCollection<(string Handle, string DeviceId)> LocalBackgroundDevices()
        => byConnection.Values
            .Where(s => s.Authenticated
                && s.IsBackgroundSync
                && s.Handle is not null
                && s.DeviceId is not null)
            .Select(s => (s.Handle!, s.DeviceId!))
            .Distinct()
            .ToArray();

    public bool HasBackgroundConnectionForDevice(string handle, string deviceId)
        => byHandle.TryGetValue(handle, out var set)
           && set.Keys.Any(id =>
               byConnection.TryGetValue(id, out var state)
               && state.Authenticated
               && state.IsBackgroundSync
               && string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal));

    private bool HandleHasLocalConnections(string handle, bool includeBackgroundSync = true)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return false;
        return includeBackgroundSync
            ? !set.IsEmpty
            : set.Keys.Any(id =>
                byConnection.TryGetValue(id, out var state) && !state.IsBackgroundSync);
    }
}
