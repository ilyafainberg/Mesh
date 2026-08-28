using System.Text.RegularExpressions;

namespace Mesh.Relay.LiveFaults;

public sealed class LiveFaultStore(LiveFaultOptions options, TimeProvider? timeProvider = null)
{
    public const string OnlineFrameKind = "online-frame";
    public const string RejectedCode = "test_fault_rejected";

    private static readonly Regex RuleIdPattern =
        new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex AccountPattern =
        new("^[a-z0-9][a-z0-9_-]{1,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex KindPattern =
        new("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, MutableRule> rules = new(StringComparer.Ordinal);
    private readonly List<LiveFaultAuditEntry> audit = [];
    private long auditSequence;

    public bool Enabled => options.Enabled;

    public LiveFaultRuleStatus Activate(LiveFaultActivationRequest request)
    {
        if (!Enabled)
            throw new InvalidOperationException("Live fault hooks are disabled.");
        var normalized = NormalizeAndValidate(request);

        lock (gate)
        {
            CleanupExpiredCore(clock.GetUtcNow());
            if (rules.TryGetValue(normalized.RuleId, out var existing))
            {
                if (!existing.Request.Equals(normalized))
                    throw new InvalidOperationException(
                        $"Rule '{normalized.RuleId}' already exists with different metadata.");
                return existing.Status(clock.GetUtcNow());
            }

            var now = clock.GetUtcNow();
            var rule = new MutableRule(normalized, now, now.AddSeconds(normalized.TtlSeconds));
            rules.Add(normalized.RuleId, rule);
            Audit("activated", rule, now);
            return rule.Status(now);
        }
    }

    public IReadOnlyList<LiveFaultRuleStatus> List()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpiredCore(now);
            return rules.Values
                .OrderBy(rule => rule.ActivatedAt)
                .Select(rule => rule.Status(now))
                .ToArray();
        }
    }

    public LiveFaultRuleStatus? Get(string ruleId)
    {
        var normalizedRuleId = NormalizeRuleId(ruleId);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpiredCore(now);
            return rules.TryGetValue(normalizedRuleId, out var rule) ? rule.Status(now) : null;
        }
    }

    public bool Deactivate(string ruleId)
    {
        var normalizedRuleId = NormalizeRuleId(ruleId);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpiredCore(now);
            if (!rules.TryGetValue(normalizedRuleId, out var rule)) return false;
            if (rule.DeactivatedAt is not null) return false;
            rule.DeactivatedAt = now;
            Audit("deactivated", rule, now);
            return true;
        }
    }

    public int CleanupExpired()
    {
        lock (gate) return CleanupExpiredCore(clock.GetUtcNow());
    }

    public IReadOnlyList<LiveFaultAuditEntry> Audit()
    {
        lock (gate) return audit.ToArray();
    }

    public LiveFaultDecision? TryApply(
        LiveFaultDirection direction,
        string sourceAccount,
        string sourceDevice,
        string targetAccount,
        string targetDevice,
        string kind,
        string stableId)
    {
        if (!Enabled) return null;
        if (!Enum.IsDefined(direction)
            || !TryNormalizeAccount(sourceAccount, out var source)
            || !TryNormalizeDevice(sourceDevice, out var sourceDeviceNormalized)
            || !TryNormalizeAccount(targetAccount, out var target)
            || !TryNormalizeDevice(targetDevice, out var targetDeviceNormalized)
            || !TryNormalizeKind(kind, out var kindNormalized)
            || !IsSafeStableId(stableId))
            return null;
        var idHash = LiveFaultIds.Hash(stableId);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpiredCore(now);
            foreach (var rule in rules.Values.OrderBy(candidate => candidate.ActivatedAt))
            {
                if (!rule.IsActive(now) || !Matches(
                        rule.Request, direction, source, sourceDeviceNormalized, target,
                        targetDeviceNormalized, kindNormalized, idHash))
                    continue;

                rule.ObservedMatches++;
                if (rule.ObservedMatches < rule.Request.Ordinal) continue;

                rule.UseCount++;
                Audit("consumed", rule, now);
                if (rule.UseCount >= rule.Request.MaxUses)
                    rule.DeactivatedAt = now;
                return new LiveFaultDecision(rule.Request.RuleId, rule.Request.Mode);
            }
            return null;
        }
    }

    private static bool Matches(
        LiveFaultActivationRequest rule,
        LiveFaultDirection direction,
        string sourceAccount,
        string sourceDevice,
        string targetAccount,
        string targetDevice,
        string kind,
        string stableIdHash)
        => rule.Direction == direction
           && string.Equals(rule.SourceAccount, sourceAccount, StringComparison.Ordinal)
           && Match(rule.SourceDevice, sourceDevice)
           && Match(rule.TargetAccount, targetAccount)
           && string.Equals(rule.TargetDevice, targetDevice, StringComparison.Ordinal)
           && Match(rule.Kind, kind)
           && Match(rule.StableIdHash, stableIdHash);

    private static bool Match(string? expected, string actual)
        => expected is null || string.Equals(expected, actual, StringComparison.Ordinal);

    private LiveFaultActivationRequest NormalizeAndValidate(LiveFaultActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ruleId = NormalizeRuleId(request.RuleId);
        if (!Enum.IsDefined(request.Mode))
            throw new ArgumentException("mode is invalid.");
        if (!Enum.IsDefined(request.Direction))
            throw new ArgumentException("direction is invalid.");
        if (!TryNormalizeAccount(request.SourceAccount, out var sourceAccount))
            throw new ArgumentException("sourceAccount must be a valid ASCII handle.");
        if (!TryNormalizeDevice(request.TargetDevice, out var targetDevice))
            throw new ArgumentException("targetDevice must be an explicit valid device identifier.");
        string? sourceDevice = null;
        if (request.SourceDevice is not null
            && !TryNormalizeDevice(request.SourceDevice, out sourceDevice))
            throw new ArgumentException("sourceDevice is not a valid device identifier.");
        string? targetAccount = null;
        if (request.TargetAccount is not null
            && !TryNormalizeAccount(request.TargetAccount, out targetAccount))
            throw new ArgumentException("targetAccount must be a valid ASCII handle.");
        string? kind = null;
        if (request.Kind is not null
            && !TryNormalizeKind(request.Kind, out kind))
            throw new ArgumentException("kind must be a valid ASCII protocol kind.");
        if (request.TtlSeconds is < 1 || request.TtlSeconds > 3600
            || request.TtlSeconds > options.MaxTtlSeconds)
            throw new ArgumentOutOfRangeException(nameof(request.TtlSeconds),
                $"ttlSeconds must be between 1 and {Math.Min(3600, options.MaxTtlSeconds)}.");
        if (request.MaxUses is < 1 || request.MaxUses > 1000 || request.MaxUses > options.MaxUses)
            throw new ArgumentOutOfRangeException(nameof(request.MaxUses),
                $"maxUses must be between 1 and {Math.Min(1000, options.MaxUses)}.");
        if (request.Ordinal is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(request.Ordinal), "ordinal must be between 1 and 100000.");
        string? stableIdHash = null;
        if (request.StableIdHash is not null
            && !TryNormalizeHash(request.StableIdHash, out stableIdHash))
            throw new ArgumentException("stableIdHash must be a 64-character ASCII SHA-256 hex value.");

        return request with
        {
            RuleId = ruleId,
            SourceAccount = sourceAccount,
            SourceDevice = sourceDevice,
            TargetAccount = targetAccount,
            TargetDevice = targetDevice,
            Kind = kind,
            StableIdHash = stableIdHash
        };
    }

    private int CleanupExpiredCore(DateTimeOffset now)
    {
        var count = 0;
        foreach (var rule in rules.Values)
        {
            if (rule.DeactivatedAt is not null || rule.ExpiresAt > now) continue;
            rule.DeactivatedAt = rule.ExpiresAt;
            Audit("expired", rule, now);
            count++;
        }
        return count;
    }

    private void Audit(string eventName, MutableRule rule, DateTimeOffset at)
    {
        var request = rule.Request;
        audit.Add(new LiveFaultAuditEntry(
            ++auditSequence,
            at,
            eventName,
            request.RuleId,
            request.Mode,
            request.Direction,
            request.SourceAccount,
            request.SourceDevice,
            request.TargetAccount,
            request.TargetDevice,
            request.Kind,
            request.StableIdHash,
            request.Ordinal,
            request.MaxUses,
            rule.UseCount,
            rule.ExpiresAt));
    }

    private static string NormalizeRuleId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = TrimAsciiSpaces(value);
        if (!IsAscii(normalized) || !RuleIdPattern.IsMatch(normalized))
            throw new ArgumentException("ruleId must contain only ASCII letters, digits, '.', '_' or '-'.");
        return normalized;
    }

    private static bool TryNormalizeAccount(string? value, out string normalized)
    {
        normalized = "";
        if (value is null || !IsAscii(value)) return false;
        normalized = TrimAsciiSpaces(value);
        if (normalized.StartsWith('@')) normalized = normalized[1..];
        normalized = normalized.ToLowerInvariant();
        return AccountPattern.IsMatch(normalized);
    }

    private static bool TryNormalizeDevice(string? value, out string normalized)
    {
        normalized = "";
        if (value is null || !IsAscii(value)) return false;
        normalized = TrimAsciiSpaces(value).ToLowerInvariant();
        return Mesh.Shared.DeviceProtocol.IsValidDeviceId(normalized);
    }

    private static bool TryNormalizeKind(string? value, out string normalized)
    {
        normalized = "";
        if (value is null || !IsAscii(value)) return false;
        normalized = TrimAsciiSpaces(value).ToLowerInvariant();
        return KindPattern.IsMatch(normalized);
    }

    private static bool TryNormalizeHash(string? value, out string normalized)
    {
        normalized = "";
        if (value is null || !IsAscii(value)) return false;
        normalized = TrimAsciiSpaces(value).ToLowerInvariant();
        return normalized.Length == 64
               && normalized.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsSafeStableId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 512
           && IsAscii(value)
           && value.All(c => c is >= '!' and <= '~' && c is not '/' and not '\\');

    private static bool IsAscii(string value)
        => value.All(c => c is >= ' ' and <= '~');

    private static string TrimAsciiSpaces(string value) => value.Trim(' ');

    private sealed class MutableRule(
        LiveFaultActivationRequest request,
        DateTimeOffset activatedAt,
        DateTimeOffset expiresAt)
    {
        public LiveFaultActivationRequest Request { get; } = request;
        public DateTimeOffset ActivatedAt { get; } = activatedAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int ObservedMatches { get; set; }
        public int UseCount { get; set; }
        public DateTimeOffset? DeactivatedAt { get; set; }

        public bool IsActive(DateTimeOffset now)
            => DeactivatedAt is null && now < ExpiresAt && UseCount < Request.MaxUses;

        public LiveFaultRuleStatus Status(DateTimeOffset now) => new(
            Request.RuleId,
            Request.Mode,
            Request.Direction,
            Request.SourceAccount,
            Request.SourceDevice,
            Request.TargetAccount,
            Request.TargetDevice,
            Request.Kind,
            Request.StableIdHash,
            Request.Ordinal,
            Request.MaxUses,
            ObservedMatches,
            UseCount,
            ActivatedAt,
            ExpiresAt,
            DeactivatedAt,
            IsActive(now));
    }
}

public static class LiveFaultAdminAuthorization
{
    public static bool IsAuthorized(string? configuredKey, string? suppliedKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrWhiteSpace(suppliedKey))
            return false;
        var expected = System.Text.Encoding.UTF8.GetBytes(configuredKey);
        var supplied = System.Text.Encoding.UTF8.GetBytes(suppliedKey);
        return expected.Length == supplied.Length
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
