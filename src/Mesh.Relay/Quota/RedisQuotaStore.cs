using StackExchange.Redis;

namespace Mesh.Relay.Quota;

/// <summary>
/// Redis-backed <see cref="IQuotaStore"/>. The daily counter is a Redis key
/// <c>mesh:quota:{handle}:{yyyyMMdd}</c> incremented with INCR and given a 2-day TTL, so the
/// per-user free-model limit is exact, shared across all relay replicas, and cleans itself up.
/// </summary>
public sealed class RedisQuotaStore : IQuotaStore
{
    // Refund without underflow: only decrement when the counter is above zero.
    private const string RefundScript =
        "local v = tonumber(redis.call('get', KEYS[1]) or '0'); if v > 0 then return redis.call('decr', KEYS[1]) else return 0 end";

    private static readonly TimeSpan Ttl = TimeSpan.FromDays(2);

    private readonly string connectionString;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private volatile ConnectionMultiplexer? mux;

    public RedisQuotaStore(string connectionString) => this.connectionString = connectionString;

    private static string Key(string handle) => $"mesh:quota:{handle}:{DateTimeOffset.UtcNow:yyyyMMdd}";

    private async Task<IDatabase> DbAsync()
    {
        if (mux is { IsConnected: true }) return mux.GetDatabase();
        await connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mux is null || !mux.IsConnected)
                mux = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
        }
        finally { connectLock.Release(); }
        return mux.GetDatabase();
    }

    public async Task<long> ReserveDailyAsync(string handle, CancellationToken ct = default)
    {
        var db = await DbAsync().ConfigureAwait(false);
        var key = Key(handle);
        var value = await db.StringIncrementAsync(key).ConfigureAwait(false);
        // Set the expiry once, when the counter is first created for the day.
        if (value == 1) await db.KeyExpireAsync(key, Ttl).ConfigureAwait(false);
        return value;
    }

    public async Task RefundDailyAsync(string handle, CancellationToken ct = default)
    {
        var db = await DbAsync().ConfigureAwait(false);
        await db.ScriptEvaluateAsync(RefundScript, new RedisKey[] { Key(handle) }).ConfigureAwait(false);
    }
}
