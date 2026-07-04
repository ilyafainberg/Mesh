using StackExchange.Redis;

namespace Mesh.Relay.Quota;

/// <summary>
/// Redis-backed <see cref="IQuotaStore"/>. The daily token counter is a Redis key
/// <c>mesh:quota:{handle}:{yyyyMMdd}</c> incremented with INCRBY and given a 2-day TTL, so the
/// per-user free-model token limit is exact, shared across all relay replicas, and cleans itself
/// up.
/// </summary>
public sealed class RedisQuotaStore : IQuotaStore
{
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

    public async Task<long> GetDailyAsync(string handle, CancellationToken ct = default)
    {
        var db = await DbAsync().ConfigureAwait(false);
        var value = await db.StringGetAsync(Key(handle)).ConfigureAwait(false);
        return value.HasValue && long.TryParse((string?)value, out var tokens) ? tokens : 0;
    }

    public async Task<long> AddDailyAsync(string handle, long tokens, CancellationToken ct = default)
    {
        var db = await DbAsync().ConfigureAwait(false);
        var key = Key(handle);
        var value = await db.StringIncrementAsync(key, tokens).ConfigureAwait(false);
        // Set the expiry once, when the counter is first created for the day (new total equals the
        // tokens just added).
        if (value == tokens) await db.KeyExpireAsync(key, Ttl).ConfigureAwait(false);
        return value;
    }
}
