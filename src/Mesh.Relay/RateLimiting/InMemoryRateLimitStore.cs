using System.Collections.Concurrent;

namespace Mesh.Relay.RateLimiting;

/// <summary>Process-local token-bucket state used when Redis is unavailable or not configured.</summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private sealed class BucketState
    {
        public required double Tokens { get; set; }
        public required long LastRefillTicks { get; set; }
    }

    private readonly ConcurrentDictionary<string, BucketState> buckets =
        new(StringComparer.Ordinal);

    public Task<RateLimitDecision> TryAcquireAsync(
        string handle,
        MessageRateBucket bucket,
        int ratePerMinute,
        int burstCapacity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ratePerMinute = Math.Max(1, ratePerMinute);
        burstCapacity = Math.Max(1, burstCapacity);

        var initialTicks = DateTimeOffset.UtcNow.UtcTicks;
        var key = $"{bucket}:{NormalizeHandle(handle)}";
        var state = buckets.GetOrAdd(
            key,
            _ => new BucketState { Tokens = burstCapacity, LastRefillTicks = initialTicks });

        RateLimitDecision decision;
        lock (state)
        {
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            var elapsedTicks = Math.Max(0, nowTicks - state.LastRefillTicks);
            var refillPerSecond = ratePerMinute / 60d;
            state.Tokens = Math.Min(
                burstCapacity,
                state.Tokens + elapsedTicks / (double)TimeSpan.TicksPerSecond * refillPerSecond);
            if (nowTicks > state.LastRefillTicks)
                state.LastRefillTicks = nowTicks;

            if (state.Tokens >= 1d)
            {
                state.Tokens -= 1d;
                decision = new RateLimitDecision(true, 0, state.Tokens);
            }
            else
            {
                var retryMs = Math.Ceiling((1d - state.Tokens) / refillPerSecond * 1000d);
                decision = new RateLimitDecision(
                    false,
                    (int)Math.Clamp(retryMs, 1d, int.MaxValue),
                    state.Tokens);
            }
        }

        return Task.FromResult(decision);
    }

    private static string NormalizeHandle(string handle) =>
        (handle ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
}
