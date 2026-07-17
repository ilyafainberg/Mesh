namespace Mesh.Relay.RateLimiting;

/// <summary>Applies the effective per-handle policy to one logical message.</summary>
public sealed class PerHandleRateLimiter : IMessageRateLimiter
{
    private readonly IHandleRatePolicyProvider policies;
    private readonly IRateLimitStore store;

    public PerHandleRateLimiter(IHandleRatePolicyProvider policies, IRateLimitStore store)
    {
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<(RateLimitDecision Decision, HandleRatePolicy Policy)> TryAcquireAsync(
        string handle,
        MessageRateBucket bucket,
        CancellationToken ct = default)
    {
        var policy = await policies.GetPolicyAsync(handle, ct).ConfigureAwait(false);
        if (!policy.Enabled)
            return (new RateLimitDecision(false, 0, 0), policy);

        var (rate, burst) = bucket switch
        {
            MessageRateBucket.Direct => (policy.MessagesPerMinute, policy.BurstCapacity),
            MessageRateBucket.Group => (policy.GroupMessagesPerMinute, policy.GroupBurstCapacity),
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };

        var decision = await store
            .TryAcquireAsync(handle, bucket, rate, burst, ct)
            .ConfigureAwait(false);
        return (decision, policy);
    }
}
