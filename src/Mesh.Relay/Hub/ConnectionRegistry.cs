using System.Collections.Concurrent;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// Per-node registry of live hub connections for the online-only switchboard. Maps each SignalR
/// connection id to the handle/device it authenticated as, and each handle to its set of local
/// connection ids, so the router can forward an opaque frame to every online device of a recipient
/// that is connected to THIS instance.
///
/// This is intentionally per-instance and ephemeral (connections are never persisted). Cross-instance
/// delivery is handled by the directed backplane using ephemeral presence, not by this registry.
/// </summary>
public sealed class ConnectionRegistry
{
    /// <summary>The bounded number of in-flight opaque frames a single connection may buffer.</summary>
    public const int MaxOutboundInFlight = 256;

    /// <summary>State tracked for a single connection while it is open.</summary>
    public sealed class ConnState
    {
        public string? Handle { get; set; }
        public string? PublicKey { get; set; }

        /// <summary>
        /// Stable short device id derived from this connection's authenticated device public key
        /// (see <see cref="DeviceProtocol.DeviceId"/>). Set at authentication so the router can target
        /// one specific device of a handle and so presence can report which devices are online.
        /// </summary>
        public string? DeviceId { get; set; }
        public string Nonce { get; set; } = "";
        public bool Authenticated { get; set; }

        /// <summary>The Protocol 9 protocol version asserted on the connect query (always 9 once accepted).</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>The authentication generation the client asserted at connect, bound into the challenge.</summary>
        public long AuthGeneration { get; set; }

        /// <summary>The custody head the client asserted at connect, bound into the challenge.</summary>
        public string CustodyHead { get; set; } = "";

        private int outboundInFlight;

        /// <summary>Reserves one outbound slot; returns false when the bounded buffer is already full.</summary>
        public bool TryReserveOutbound()
        {
            while (true)
            {
                var current = Volatile.Read(ref outboundInFlight);
                if (current >= MaxOutboundInFlight) return false;
                if (Interlocked.CompareExchange(ref outboundInFlight, current + 1, current) == current)
                    return true;
            }
        }

        /// <summary>Releases one previously reserved outbound slot.</summary>
        public void ReleaseOutbound()
        {
            var updated = Interlocked.Decrement(ref outboundInFlight);
            if (updated < 0) Interlocked.Exchange(ref outboundInFlight, 0);
        }
    }

    private readonly ConcurrentDictionary<string, ConnState> byConnection = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> byHandle =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a freshly connected (not yet authenticated) connection with its challenge state.</summary>
    public void Add(
        string connectionId,
        string handle,
        string nonce,
        int protocolVersion,
        long authGeneration,
        string custodyHead)
        => byConnection[connectionId] = new ConnState
        {
            Handle = handle,
            Nonce = nonce,
            ProtocolVersion = protocolVersion,
            AuthGeneration = authGeneration,
            CustodyHead = custodyHead
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
    /// this was its last authenticated connection on this instance.
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
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Where(id => byConnection.TryGetValue(id, out var state) && state.Authenticated)
            .ToArray();
    }

    /// <summary>
    /// The local connection ids for a handle whose authenticated device id matches
    /// <paramref name="deviceId"/>. Used to forward a frame to ONE specific device of a handle.
    /// </summary>
    public IReadOnlyCollection<string> ConnectionsForDevice(string handle, string deviceId)
    {
        if (!byHandle.TryGetValue(handle, out var set)) return Array.Empty<string>();
        return set.Keys
            .Where(c => byConnection.TryGetValue(c, out var s)
                && s.Authenticated
                && s.DeviceId == deviceId)
            .ToArray();
    }

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
