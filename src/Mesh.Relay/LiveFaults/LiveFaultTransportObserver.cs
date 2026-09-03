using System.Collections.Concurrent;

namespace Mesh.Relay.LiveFaults;

public sealed class LiveFaultTransportObserver
{
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LiveFaultTransportAttempt> events = new();
    private long sequence;

    public int AttemptsFor(string envelopeId) => attempts.GetValueOrDefault(envelopeId);

    public void RecordAttempt(string envelopeId)
    {
        var attempt = attempts.AddOrUpdate(envelopeId, 1, static (_, count) => count + 1);
        events.Enqueue(new LiveFaultTransportAttempt(
            Interlocked.Increment(ref sequence),
            LiveFaultIds.Hash(envelopeId),
            attempt));
    }

    public IReadOnlyList<LiveFaultTransportAttempt> Snapshot() => events.ToArray();
}

public sealed record LiveFaultTransportAttempt(
    long Sequence,
    string StableIdHash,
    int Attempt);
