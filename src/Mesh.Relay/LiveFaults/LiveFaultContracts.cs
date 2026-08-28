using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mesh.Relay.LiveFaults;

[JsonConverter(typeof(JsonStringEnumConverter<LiveFaultMode>))]
public enum LiveFaultMode
{
    RejectBeforeForwarding,
    DropBeforeForwarding,
    SuccessDropBeforeDestination
}

[JsonConverter(typeof(JsonStringEnumConverter<LiveFaultDirection>))]
public enum LiveFaultDirection
{
    Outbound,
    Inbound
}

public sealed record LiveFaultActivationRequest(
    string RuleId,
    LiveFaultMode Mode,
    LiveFaultDirection Direction,
    string SourceAccount,
    string TargetDevice,
    int TtlSeconds,
    int MaxUses = 1,
    int Ordinal = 1,
    string? SourceDevice = null,
    string? TargetAccount = null,
    string? Kind = null,
    string? StableIdHash = null);

public sealed record LiveFaultRuleStatus(
    string RuleId,
    LiveFaultMode Mode,
    LiveFaultDirection Direction,
    string SourceAccount,
    string? SourceDevice,
    string? TargetAccount,
    string TargetDevice,
    string? Kind,
    string? StableIdHash,
    int Ordinal,
    int MaxUses,
    int ObservedMatches,
    int UseCount,
    DateTimeOffset ActivatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DeactivatedAt,
    bool Active);

public sealed record LiveFaultAuditEntry(
    long Sequence,
    DateTimeOffset At,
    string Event,
    string RuleId,
    LiveFaultMode Mode,
    LiveFaultDirection Direction,
    string SourceAccount,
    string? SourceDevice,
    string? TargetAccount,
    string TargetDevice,
    string? Kind,
    string? StableIdHash,
    int Ordinal,
    int MaxUses,
    int UseCount,
    DateTimeOffset ExpiresAt);

public sealed record LiveFaultDecision(string RuleId, LiveFaultMode Mode);

public sealed record LiveFaultAuthorityRotationRequest(
    string Handle,
    string PreviousDeviceId,
    string NewDevicePublicKey,
    string NewCustodyHead);

public sealed record LiveFaultAuthorityRotationResult(
    string Handle,
    string PreviousDeviceId,
    string NewDeviceId,
    long AuthGeneration,
    string CustodyHead,
    string PublicKeyFingerprint);

public sealed record LiveFaultAuthorityLookup(
    long Sequence,
    DateTimeOffset At,
    string Handle,
    long AuthGeneration,
    string CustodyHead,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> PublicKeyFingerprints);

public sealed class LiveFaultOptions
{
    public bool Enabled { get; init; }
    public int MaxTtlSeconds { get; init; } = 3600;
    public int MaxUses { get; init; } = 1000;
}

public static class LiveFaultIds
{
    public static string Hash(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableId))).ToLowerInvariant();
    }
}
