using System.Threading;

namespace Mesh.Relay.Observability;

/// <summary>
/// Process-wide, thread-safe aggregate counters for the online-only relay. Values are simple
/// monotonic totals (plus a live gauge for connected sockets) meant for ops scraping via GET /metrics.
///
/// Every counter reflects the online switchboard only: online deliveries, offline NACKs, cross-instance
/// backplane forwards and contentless push wakes. There are deliberately NO queue-depth, lease, mailbox
/// or delivery-latency metrics, because the relay never queues or persists a payload.
///
/// Privacy: these are aggregate counts ONLY. No handles, IPs, ciphertext, frame ids or crypto material
/// are ever recorded here, so the snapshot can be exposed unauthenticated without leaking PII.
/// </summary>
public sealed class RelayMetrics
{
    private long handlesRegistered;
    private long onlineDelivered;
    private long offlineNacks;
    private long backplaneForwards;
    private long pushWakes;
    private long hostedModelCalls;
    private long rateLimitRejections;
    private long framesRejectedTooLarge;
    private long connected;

    /// <summary>A new handle was claimed via REST registration.</summary>
    public void HandleRegistered() => Interlocked.Increment(ref handlesRegistered);

    /// <summary>One or more relay frames were delivered to at least one live online socket.</summary>
    public void OnlineDelivered(int count = 1)
    {
        if (count > 0) Interlocked.Add(ref onlineDelivered, count);
    }

    /// <summary>A relay frame found no online target and was answered not_online.</summary>
    public void OfflineNack(int count = 1)
    {
        if (count > 0) Interlocked.Add(ref offlineNacks, count);
    }

    /// <summary>A frame was forwarded to another instance over the transient backplane.</summary>
    public void BackplaneForwarded() => Interlocked.Increment(ref backplaneForwards);

    /// <summary>A contentless push wake was emitted for an offline target.</summary>
    public void PushWake() => Interlocked.Increment(ref pushWakes);

    /// <summary>A hosted free-model completion was served.</summary>
    public void HostedModelCall() => Interlocked.Increment(ref hostedModelCalls);

    /// <summary>A per-handle relay rate limit dropped a frame.</summary>
    public void RateLimitRejected() => Interlocked.Increment(ref rateLimitRejections);

    /// <summary>A frame exceeded the opaque ciphertext ceiling and was rejected.</summary>
    public void FrameRejectedTooLarge() => Interlocked.Increment(ref framesRejectedTooLarge);

    /// <summary>A hub connection opened; bumps the live connected gauge.</summary>
    public void ConnectionOpened() => Interlocked.Increment(ref connected);

    /// <summary>A hub connection closed; drops the live connected gauge.</summary>
    public void ConnectionClosed() => Interlocked.Decrement(ref connected);

    /// <summary>An immutable, consistent read of the current counters for the /metrics endpoint.</summary>
    public RelayMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref handlesRegistered),
        Interlocked.Read(ref onlineDelivered),
        Interlocked.Read(ref offlineNacks),
        Interlocked.Read(ref backplaneForwards),
        Interlocked.Read(ref pushWakes),
        Interlocked.Read(ref hostedModelCalls),
        Interlocked.Read(ref rateLimitRejections),
        Interlocked.Read(ref framesRejectedTooLarge),
        Interlocked.Read(ref connected));
}

/// <summary>Aggregate-only view of the online relay counters. Contains no handles or PII.</summary>
public readonly record struct RelayMetricsSnapshot(
    long HandlesRegistered,
    long OnlineDelivered,
    long OfflineNacks,
    long BackplaneForwards,
    long PushWakes,
    long HostedModelCalls,
    long RateLimitRejections,
    long FramesRejectedTooLarge,
    long Connected);
