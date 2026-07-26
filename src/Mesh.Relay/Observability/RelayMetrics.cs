using System.Threading;

namespace Mesh.Relay.Observability;

/// <summary>
/// Process-wide, thread-safe aggregate counters for the relay. Values are simple monotonic
/// totals (plus a live gauge for connected sockets) meant for ops scraping via GET /metrics.
///
/// Privacy: these are aggregate counts ONLY. No handles, IPs, message bodies, or crypto material
/// are ever recorded here, so the snapshot can be exposed unauthenticated without leaking PII.
/// </summary>
public sealed class RelayMetrics
{
    private long handlesRegistered;
    private long messagesRouted;
    private long hostedModelCalls;
    private long rateLimitRejections;
    private long connected;
    private long queueEnqueued;
    private long deliveryAcknowledged;
    private long deliveryAckLatencyMsTotal;
    private long deliveryAckLatencyMsMax;
    private long deliveryRedelivered;
    private long queueCancelled;

    /// <summary>A new handle was claimed via REST registration.</summary>
    public void HandleRegistered() => Interlocked.Increment(ref handlesRegistered);

    /// <summary>Authenticated recipient envelopes routed by the hub.</summary>
    public void MessageRouted(int count = 1)
    {
        if (count > 0) Interlocked.Add(ref messagesRouted, count);
    }

    /// <summary>A hosted free-model completion was served.</summary>
    public void HostedModelCall() => Interlocked.Increment(ref hostedModelCalls);

    /// <summary>A per-handle message rate limit dropped a message.</summary>
    public void RateLimitRejected() => Interlocked.Increment(ref rateLimitRejections);

    /// <summary>A hub connection opened; bumps the live connected gauge.</summary>
    public void ConnectionOpened() => Interlocked.Increment(ref connected);

    /// <summary>A hub connection closed; drops the live connected gauge.</summary>
    public void ConnectionClosed() => Interlocked.Decrement(ref connected);

    public void QueueEnqueued(int count = 1)
    {
        if (count > 0) Interlocked.Add(ref queueEnqueued, count);
    }

    public void DeliveryAcknowledged(TimeSpan latency)
    {
        Interlocked.Increment(ref deliveryAcknowledged);
        var milliseconds = Math.Max(0L, (long)latency.TotalMilliseconds);
        Interlocked.Add(ref deliveryAckLatencyMsTotal, milliseconds);
        var observed = Interlocked.Read(ref deliveryAckLatencyMsMax);
        while (milliseconds > observed)
        {
            var previous = Interlocked.CompareExchange(
                ref deliveryAckLatencyMsMax, milliseconds, observed);
            if (previous == observed) break;
            observed = previous;
        }
    }
    public void DeliveryRedelivered() => Interlocked.Increment(ref deliveryRedelivered);
    public void QueueCancelled() => Interlocked.Increment(ref queueCancelled);

    /// <summary>An immutable, consistent read of the current counters for the /metrics endpoint.</summary>
    public RelayMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref handlesRegistered),
        Interlocked.Read(ref messagesRouted),
        Interlocked.Read(ref hostedModelCalls),
        Interlocked.Read(ref rateLimitRejections),
        Interlocked.Read(ref connected),
        Interlocked.Read(ref queueEnqueued),
        Interlocked.Read(ref deliveryAcknowledged),
        Interlocked.Read(ref deliveryAckLatencyMsTotal),
        Interlocked.Read(ref deliveryAckLatencyMsMax),
        Interlocked.Read(ref deliveryRedelivered),
        Interlocked.Read(ref queueCancelled));
}

/// <summary>Aggregate-only view of the relay counters. Contains no handles or PII.</summary>
public readonly record struct RelayMetricsSnapshot(
    long HandlesRegistered,
    long MessagesRouted,
    long HostedModelCalls,
    long RateLimitRejections,
    long Connected,
    long QueueEnqueued,
    long DeliveryAcknowledged,
    long DeliveryAckLatencyMsTotal,
    long DeliveryAckLatencyMsMax,
    long DeliveryRedelivered,
    long QueueCancelled);
