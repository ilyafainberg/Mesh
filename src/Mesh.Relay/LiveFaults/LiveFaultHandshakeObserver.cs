using System.Collections.Concurrent;

namespace Mesh.Relay.LiveFaults;

public sealed record LiveFaultHandshakeEvent(
    string Stage,
    string Handle,
    string DeviceId,
    long AuthGeneration,
    string CustodyHead,
    string? Nonce = null,
    string? Canonical = null,
    string? Signature = null,
    bool? Accepted = null);

public sealed class LiveFaultHandshakeObserver
{
    private readonly ConcurrentQueue<LiveFaultHandshakeEvent> events = new();

    public IReadOnlyList<LiveFaultHandshakeEvent> Events => events.ToArray();

    public void Record(LiveFaultHandshakeEvent entry) => events.Enqueue(entry);
}
