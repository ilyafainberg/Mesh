namespace Mesh.Relay.Quota;

/// <summary>
/// In-memory <see cref="IQuotaStore"/> for single-instance/local use. Holds a per-handle,
/// per-UTC-day token counter. Not shared across replicas and reset on restart, which is why the
/// Redis implementation exists for production.
/// </summary>
public sealed class InMemoryQuotaStore : IQuotaStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string day, long tokens)> counts =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<long> GetDailyAsync(string handle, CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var current = counts.TryGetValue(handle, out var cur) && cur.day == today ? cur.tokens : 0;
        return Task.FromResult(current);
    }

    public Task<long> AddDailyAsync(string handle, long tokens, CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var updated = counts.AddOrUpdate(handle,
            _ => (today, tokens),
            (_, cur) => cur.day == today ? (today, cur.tokens + tokens) : (today, tokens));
        return Task.FromResult(updated.tokens);
    }
}
