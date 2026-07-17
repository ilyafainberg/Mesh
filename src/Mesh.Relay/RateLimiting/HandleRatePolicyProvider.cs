using System.Collections.Concurrent;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.RateLimiting;

/// <summary>Loads per-handle policy overrides and caches their normalized effective values.</summary>
public sealed class HandleRatePolicyProvider : IHandleRatePolicyProvider
{
    private sealed record CacheEntry(HandleRatePolicy Policy, long ExpiresAtTicks, long Generation);

    private readonly IRelayStore store;
    private readonly HandleRatePolicy defaultPolicy;
    private readonly long cacheTtlTicks;
    private readonly ConcurrentDictionary<string, CacheEntry> cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> loadLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> generations =
        new(StringComparer.OrdinalIgnoreCase);

    public HandleRatePolicyProvider(
        IRelayStore store,
        HandleRatePolicy defaultPolicy,
        TimeSpan? cacheTtl = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.defaultPolicy = NormalizePolicy(
            defaultPolicy ?? throw new ArgumentNullException(nameof(defaultPolicy)));

        var ttl = cacheTtl ?? TimeSpan.FromSeconds(60);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheTtl), "Cache TTL must be positive.");
        cacheTtlTicks = ttl.Ticks;
    }

    public async Task<HandleRatePolicy> GetPolicyAsync(
        string handle,
        CancellationToken ct = default)
    {
        var normalizedHandle = NormalizeHandle(handle);
        var gate = loadLocks.GetOrAdd(normalizedHandle, _ => new SemaphoreSlim(1, 1));
        while (true)
        {
            var generation = generations.GetOrAdd(normalizedHandle, 0);
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            if (TryGetLiveEntry(normalizedHandle, generation, nowTicks, out var policy))
                return policy;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                generation = generations.GetOrAdd(normalizedHandle, 0);
                nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                if (TryGetLiveEntry(normalizedHandle, generation, nowTicks, out policy))
                    return policy;

                var configured = await store
                    .GetHandleRatePolicyAsync(normalizedHandle, ct)
                    .ConfigureAwait(false);
                policy = NormalizePolicy(configured ?? defaultPolicy);
                if (generations.GetOrAdd(normalizedHandle, 0) != generation)
                    continue;

                cache[normalizedHandle] = new CacheEntry(
                    policy,
                    AddSaturating(DateTimeOffset.UtcNow.UtcTicks, cacheTtlTicks),
                    generation);
                return policy;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public void Invalidate(string handle)
    {
        var normalizedHandle = NormalizeHandle(handle);
        generations.AddOrUpdate(normalizedHandle, 1, (_, generation) => generation + 1);
        cache.TryRemove(normalizedHandle, out _);
    }

    private bool TryGetLiveEntry(
        string handle,
        long generation,
        long nowTicks,
        out HandleRatePolicy policy)
    {
        if (cache.TryGetValue(handle, out var entry)
            && entry.Generation == generation
            && entry.ExpiresAtTicks > nowTicks)
        {
            policy = entry.Policy;
            return true;
        }

        cache.TryRemove(handle, out _);
        policy = null!;
        return false;
    }

    private static HandleRatePolicy NormalizePolicy(HandleRatePolicy policy) =>
        new(
            Math.Max(1, policy.MessagesPerMinute),
            Math.Max(1, policy.BurstCapacity),
            Math.Max(1, policy.GroupMessagesPerMinute),
            Math.Max(1, policy.GroupBurstCapacity),
            Math.Clamp(policy.MaxFanoutRecipients, 1, FanoutProtocol.MaxRecipients),
            policy.Enabled);

    private static string NormalizeHandle(string handle) =>
        (handle ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    private static long AddSaturating(long value, long increment) =>
        increment > long.MaxValue - value ? long.MaxValue : value + increment;
}
