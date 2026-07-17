using System.Globalization;
using StackExchange.Redis;

namespace Mesh.Relay.RateLimiting;

/// <summary>Redis-backed token buckets shared exactly across relay replicas.</summary>
public sealed class RedisRateLimitStore : IRateLimitStore
{
    private const string AcquireScript =
        """
        local key = KEYS[1]
        local rate = math.max(1, tonumber(ARGV[1]))
        local capacity = math.max(1, tonumber(ARGV[2]))
        local ttl_ms = tonumber(ARGV[3])

        local redis_time = redis.call('TIME')
        local now_us = tonumber(redis_time[1]) * 1000000 + tonumber(redis_time[2])
        local tokens = tonumber(redis.call('HGET', key, 'tokens'))
        local last_us = tonumber(redis.call('HGET', key, 'last'))

        if tokens == nil or last_us == nil then
            tokens = capacity
            last_us = now_us
        else
            local elapsed_us = math.max(0, now_us - last_us)
            tokens = math.min(capacity, tokens + elapsed_us * rate / 60000000)
        end

        local allowed = 0
        local retry_ms = 0
        if tokens >= 1 then
            allowed = 1
            tokens = tokens - 1
        else
            retry_ms = math.ceil((1 - tokens) * 60000 / rate)
        end

        redis.call('HSET', key, 'tokens', string.format('%.17g', tokens), 'last', now_us)
        redis.call('PEXPIRE', key, ttl_ms)
        return { allowed, retry_ms, string.format('%.17g', tokens) }
        """;

    private readonly string connectionString;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private volatile ConnectionMultiplexer? mux;

    public RedisRateLimitStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A Redis connection string is required.", nameof(connectionString));
        this.connectionString = connectionString;
    }

    public async Task<RateLimitDecision> TryAcquireAsync(
        string handle,
        MessageRateBucket bucket,
        int ratePerMinute,
        int burstCapacity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ratePerMinute = Math.Max(1, ratePerMinute);
        burstCapacity = Math.Max(1, burstCapacity);
        var bucketName = bucket switch
        {
            MessageRateBucket.Direct => "direct",
            MessageRateBucket.Group => "group",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };

        var refillToFullMs = Math.Ceiling(burstCapacity * 60_000d / ratePerMinute);
        var ttlMs = (long)Math.Min(long.MaxValue, refillToFullMs + 60_000d);
        var key = (RedisKey)$"mesh:msg-rate:{bucketName}:{NormalizeHandle(handle)}";

        var db = await DbAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = await db.ScriptEvaluateAsync(
                AcquireScript,
                new[] { key },
                new RedisValue[] { ratePerMinute, burstCapacity, ttlMs })
            .ConfigureAwait(false);

        var values = (RedisResult[])result!;
        var allowed = (long)values[0] == 1;
        var retryAfterMs = checked((int)(long)values[1]);
        var remaining = double.Parse(values[2].ToString(), CultureInfo.InvariantCulture);
        return new RateLimitDecision(allowed, retryAfterMs, remaining);
    }

    private async Task<IDatabase> DbAsync(CancellationToken ct)
    {
        if (mux is { IsConnected: true })
            return mux.GetDatabase();

        await connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (mux is null || !mux.IsConnected)
            {
                ct.ThrowIfCancellationRequested();
                var replacement = await ConnectionMultiplexer
                    .ConnectAsync(connectionString)
                    .ConfigureAwait(false);
                var previous = mux;
                mux = replacement;
                previous?.Dispose();
            }
        }
        finally
        {
            connectLock.Release();
        }

        return mux.GetDatabase();
    }

    private static string NormalizeHandle(string handle) =>
        (handle ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
}
