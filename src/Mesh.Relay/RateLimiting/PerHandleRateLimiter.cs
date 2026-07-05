using System.Collections.Concurrent;

namespace Mesh.Relay.RateLimiting;

/// <summary>
/// In-memory, thread-safe, per-handle message rate limiter (token bucket). Protects the hub from
/// a single authenticated handle flooding the relay. Keyed by handle in a ConcurrentDictionary so
/// it needs no external service and works on the in-memory (no Redis/Cosmos) fallback path.
///
/// The steady rate is <c>ratePerMinute</c> messages per minute; the bucket capacity is
/// <c>burst</c>, which is how many messages a handle may send back-to-back before being throttled
/// to the steady refill rate. Limits are read once at construction via the relay's Config helper.
///
/// Privacy: this stores only per-handle token state (a count and a timestamp), never message
/// bodies or crypto material.
/// </summary>
public sealed class PerHandleRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefillTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly double capacity;
    private readonly double refillPerSecond;

    public PerHandleRateLimiter(int ratePerMinute, int burst)
    {
        if (ratePerMinute < 1) ratePerMinute = 1;
        if (burst < 1) burst = 1;
        capacity = burst;
        refillPerSecond = ratePerMinute / 60.0;
    }

    /// <summary>The configured steady rate, messages per minute per handle.</summary>
    public int RatePerMinute => (int)Math.Round(refillPerSecond * 60.0);

    /// <summary>The configured burst capacity, messages per handle.</summary>
    public int Burst => (int)capacity;

    /// <summary>
    /// Attempts to spend one message token for a handle. Returns true when the message is allowed,
    /// false when the handle is over its rate limit and the message should be dropped.
    /// </summary>
    public bool TryAcquire(string handle)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var bucket = buckets.GetOrAdd(handle, _ => new Bucket { Tokens = capacity, LastRefillTicks = now });

        lock (bucket)
        {
            var elapsedSeconds = (now - bucket.LastRefillTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsedSeconds > 0)
            {
                bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsedSeconds * refillPerSecond);
                bucket.LastRefillTicks = now;
            }

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return true;
            }

            return false;
        }
    }
}
