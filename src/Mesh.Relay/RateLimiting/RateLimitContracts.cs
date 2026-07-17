namespace Mesh.Relay.RateLimiting;

/// <summary>The independently configurable logical-message buckets for one handle.</summary>
public enum MessageRateBucket
{
    Direct,
    Group
}

/// <summary>
/// Effective relay policy for one handle. A group fan-out consumes one group token regardless of
/// recipient count; <see cref="MaxFanoutRecipients"/> separately bounds amplification.
/// </summary>
public sealed record HandleRatePolicy(
    int MessagesPerMinute,
    int BurstCapacity,
    int GroupMessagesPerMinute,
    int GroupBurstCapacity,
    int MaxFanoutRecipients,
    bool Enabled = true);

/// <summary>Atomic token-bucket result returned to the hub and ultimately to the client.</summary>
public sealed record RateLimitDecision(bool Allowed, int RetryAfterMs, double RemainingTokens);

/// <summary>Stores live token-bucket state, either in memory or Redis.</summary>
public interface IRateLimitStore
{
    Task<RateLimitDecision> TryAcquireAsync(
        string handle,
        MessageRateBucket bucket,
        int ratePerMinute,
        int burstCapacity,
        CancellationToken ct = default);
}

/// <summary>Resolves the cached effective policy for a handle.</summary>
public interface IHandleRatePolicyProvider
{
    Task<HandleRatePolicy> GetPolicyAsync(string handle, CancellationToken ct = default);
    void Invalidate(string handle);
}

/// <summary>Applies per-handle policy and consumes one logical-message token atomically.</summary>
public interface IMessageRateLimiter
{
    Task<(RateLimitDecision Decision, HandleRatePolicy Policy)> TryAcquireAsync(
        string handle,
        MessageRateBucket bucket,
        CancellationToken ct = default);
}
