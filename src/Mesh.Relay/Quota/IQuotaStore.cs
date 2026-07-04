namespace Mesh.Relay.Quota;

/// <summary>
/// Durable, per-handle daily usage counter for the hosted free model. Backed by Redis in
/// production so the quota is exact and shared across all relay replicas (and survives
/// restarts); an in-memory implementation is the single-instance default.
///
/// Callers reserve a unit before doing paid work and refund it if the work is rejected or
/// fails for a server-side reason, so only successful completions consume a user's quota.
/// </summary>
public interface IQuotaStore
{
    /// <summary>
    /// Atomically increments today's counter for the handle and returns the new value.
    /// Counters expire automatically a couple of days after the day they belong to.
    /// </summary>
    Task<long> ReserveDailyAsync(string handle, CancellationToken ct = default);

    /// <summary>Gives back a previously reserved unit (floored at zero) when work was rejected or failed.</summary>
    Task RefundDailyAsync(string handle, CancellationToken ct = default);
}

/// <summary>
/// In-memory <see cref="IQuotaStore"/> for single-instance/local use. Not shared across
/// replicas and reset on restart, which is why the Redis implementation exists for production.
/// </summary>
public sealed class InMemoryQuotaStore : IQuotaStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string day, long count)> counts =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<long> ReserveDailyAsync(string handle, CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var updated = counts.AddOrUpdate(handle,
            _ => (today, 1),
            (_, cur) => cur.day == today ? (today, cur.count + 1) : (today, 1));
        return Task.FromResult(updated.count);
    }

    public Task RefundDailyAsync(string handle, CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        counts.AddOrUpdate(handle,
            _ => (today, 0),
            (_, cur) => cur.day == today ? (today, Math.Max(0, cur.count - 1)) : (today, 0));
        return Task.CompletedTask;
    }
}
