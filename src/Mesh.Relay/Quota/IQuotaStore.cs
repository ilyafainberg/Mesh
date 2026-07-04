namespace Mesh.Relay.Quota;

/// <summary>
/// Durable, per-handle daily token counter for the hosted free model. Backed by Redis in
/// production so the quota is exact and shared across all relay replicas (and survives
/// restarts); an in-memory implementation is the single-instance default.
///
/// Metering is token-based: after a successful completion the relay adds the upstream-reported
/// token usage for the day, and it reads the running daily total before serving a request to
/// enforce the free-tier limit. Only real usage is counted, so a failed upstream call meters
/// nothing.
/// </summary>
public interface IQuotaStore
{
    /// <summary>Returns the current UTC day's total token count for the handle (0 if none).</summary>
    Task<long> GetDailyAsync(string handle, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="tokens"/> to today's token counter for the handle and returns the new
    /// total. Counters expire automatically a couple of days after the day they belong to.
    /// </summary>
    Task<long> AddDailyAsync(string handle, long tokens, CancellationToken ct = default);
}
