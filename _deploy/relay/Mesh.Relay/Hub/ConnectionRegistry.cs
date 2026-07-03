using System.Collections.Concurrent;

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

    /// <summary>Every handle with at least one authenticated connection on this instance.</summary>
    public IReadOnlyCollection<string> LocalHandles() => byHandle.Keys.ToArray();

    private bool HandleHasLocalConnections(string handle)
        => byHandle.TryGetValue(handle, out var set) && !set.IsEmpty;
}
