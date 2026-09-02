using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesh.App.Services;

public sealed record TopicSendSnapshot(
    string OperationId,
    string RunId,
    string LineId,
    string ThreadId,
    string TargetDeviceId,
    long SubmissionSequence,
    long ComposerRevision,
    string DraftFingerprint,
    DateTimeOffset SubmittedAt,
    string? AccountId = null)
{
    internal static TopicSendSnapshot Create(
        string threadId,
        string targetDeviceId,
        long submissionSequence,
        long composerRevision,
        string draftFingerprint,
        DateTimeOffset submittedAt,
        string? accountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftFingerprint);
        if (submissionSequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(submissionSequence));
        if (composerRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(composerRevision));
        if (submittedAt == default)
            throw new ArgumentException("A submission timestamp is required.", nameof(submittedAt));

        var identity = string.Join(
            "\0",
            "topic-send-v3",
            ScopeId(threadId, targetDeviceId),
            submissionSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            draftFingerprint);
        return new TopicSendSnapshot(
            $"topic.send:{StableId("operation", identity)}",
            StableId("run", identity),
            StableId("line", identity),
            threadId,
            targetDeviceId,
            submissionSequence,
            composerRevision,
            draftFingerprint,
            submittedAt,
            accountId);
    }

    internal static TopicSendSnapshot Restore(
        TopicSendIdentityRecord identity,
        string threadId,
        string targetDeviceId,
        DateTimeOffset submittedAt)
        => new(
            identity.OperationId,
            identity.RunId,
            identity.LineId,
            threadId,
            targetDeviceId,
            identity.SubmissionSequence,
            identity.ComposerRevision,
            identity.DraftFingerprint,
            submittedAt,
            identity.AccountId);

    internal static string StableId(string kind, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\0{identity}"));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal string ScopeIdentity => ScopeId(ThreadId, TargetDeviceId);
    internal string LogicalIdentity => LogicalId(ScopeIdentity, SubmissionSequence);

    internal static string ScopeId(string threadId, string targetDeviceId)
        => StableId(
            "scope",
            string.Join(
                "\0",
                "topic-send-v3",
                threadId,
                targetDeviceId));

    internal static string LogicalId(string scopeIdentity, long submissionSequence)
        => StableId(
            "submission",
            $"{scopeIdentity}\0{submissionSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}

public sealed record TopicSendHandoff(
    bool Accepted,
    string Code,
    string? Error = null);

public enum TopicSendOutcomeKind
{
    Accepted,
    Rejected,
    Failed,
    RetryableFailed,
    Reconciling
}

public sealed record TopicSendOutcome(
    TopicSendOutcomeKind Kind,
    TopicSendHandoff? Handoff = null,
    Exception? Exception = null,
    bool RequiresDraftClear = false,
    string? AuthoritativeRunId = null,
    string? AuthoritativeLineId = null,
    string? AuthoritativeOutboxId = null);

public sealed record TopicSendRetentionOptions
{
    public int MaximumRunningOperations { get; init; } = 64;
    public int MaximumCompletedIdentities { get; init; } = 512;
    public int MaximumUnsubmittedSnapshots { get; init; } = 256;
    public TimeSpan CompletedIdentityRetention { get; init; } = TimeSpan.FromMinutes(30);
    public int MaximumReconciliationAttempts { get; init; } = 5;
    public TimeSpan ReconciliationInitialBackoff { get; init; } = TimeSpan.FromMilliseconds(75);
    public TimeSpan ReconciliationMaximumBackoff { get; init; } = TimeSpan.FromSeconds(1);
}

public enum TopicSendReconciliationKind
{
    Unknown,
    Accepted,
    Completed,
    Failed,
    NotFound,
    Conflict,
    Corrupt,
    Unavailable,
    QueryFailed,
    Cancelled,
    Interrupted
}

public sealed record TopicSendReconciliationResult(
    TopicSendReconciliationKind Kind,
    string? Detail = null,
    string? AuthoritativeRunId = null,
    string? AuthoritativeLineId = null,
    string? AuthoritativeOutboxId = null,
    string? DiagnosticReason = null,
    string? AccountId = null,
    string? DatabaseIdentity = null,
    long DatabaseGeneration = 0,
    long ObservationVersion = 0,
    DateTimeOffset ObservedAt = default,
    TopicSendRetryAuthorization? Authorization = null);

public sealed record TopicSendAuthorizationScope(
    string AccountId,
    string DatabaseIdentity,
    long DatabaseGeneration);

public sealed record TopicSendRetryAuthorization(
    int Version,
    string Nonce,
    string OperationId,
    string SnapshotIdentity,
    string AccountId,
    string DatabaseIdentity,
    long DatabaseGeneration,
    long ObservationEpoch,
    DateTimeOffset ObservedAt,
    long ComposerRevision)
{
    public const int CurrentVersion = 1;

    public TopicSendAuthorizationScope Scope
        => new(AccountId, DatabaseIdentity, DatabaseGeneration);
}

public interface ITopicSendAuthorizationAuthority
{
    bool TryConsume(TopicSendAuthorizationScope scope, Func<bool> consume);
    // Coordinator locks must never flow through an authority callback. This overload
    // performs the profile-gated phase independently; coordinator state is compared later.
    bool TryConsume(TopicSendAuthorizationScope scope)
        => TryConsume(scope, static () => true);
    bool IsCurrent(TopicSendAuthorizationScope scope);
    TopicSendRetryAuthorization? IssueRetryAuthorization(
        TopicSendSnapshot snapshot,
        TopicSendReconciliationResult observation)
        => null;
    void InvalidateRetryAuthorization(TopicSendRetryAuthorization authorization)
    {
    }
    TopicRunBeginResult AuthorizeAndBeginTopicRun(
        TopicSendSnapshot snapshot,
        TopicSendRetryAuthorization authorization,
        TopicRunBeginCommand command)
        => new(false, false, "reconcile_required");
}

public interface ITopicSendReconciliationQuery
{
    ValueTask<TopicSendReconciliationResult> QueryAsync(
        TopicSendSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface ITopicSendReconciliationAvailability
{
    event Action? AvailabilityChanged;
}

public sealed class DelegateTopicSendReconciliationQuery(
    Func<TopicSendSnapshot, CancellationToken, ValueTask<TopicSendReconciliationResult>> query)
    : ITopicSendReconciliationQuery
{
    public ValueTask<TopicSendReconciliationResult> QueryAsync(
        TopicSendSnapshot snapshot,
        CancellationToken cancellationToken)
        => query(snapshot, cancellationToken);
}

public enum TopicSendSubmissionKind
{
    Started,
    AlreadyRunning,
    AlreadyCompleted,
    ReconciliationRequired,
    CapacityExceeded,
    IdentityConflict,
    PersistenceFailed
}

public sealed record TopicSendSubmissionResult(
    TopicSendSubmissionKind Kind,
    TopicSendSnapshot Snapshot,
    TopicSendOutcome? Outcome = null,
    string? Error = null)
{
    public bool Started => Kind == TopicSendSubmissionKind.Started;
    public bool Retryable => Kind is TopicSendSubmissionKind.CapacityExceeded
        or TopicSendSubmissionKind.PersistenceFailed;
}

public enum TopicSendJournalLifecycle
{
    Unknown = 0,
    PreHandoff = 1,
    AcceptedOrUnknown = 2,
    Terminal = 3
}

public enum TopicSendJournalCleanup
{
    None = 0,
    DraftClearPending = 1,
    DraftClearPersisted = 2,
    DraftClearSuperseded = 3,
    Pending = DraftClearPending,
    Complete = DraftClearPersisted
}

public enum TopicSendJournalCompaction
{
    Active = 0,
    Compacted = 1
}

public enum TopicSendDraftCleanupResult
{
    DraftClearPersisted,
    DraftClearSuperseded
}

public sealed class TopicSendDraftCleanup(
    Func<TopicSendOutcome, Task<TopicSendDraftCleanupResult>> persistAsync)
{
    internal Task<TopicSendDraftCleanupResult> PersistAsync(TopicSendOutcome outcome)
        => persistAsync(outcome);
}

/// <summary>
/// Version 5 stores only stable hashes/IDs and the immutable draft revision. Pre-handoff
/// records retain the stable operation identity until the trigger ledger proves no commit
/// and the operation is explicitly abandoned or safely superseded. Accepted-or-unknown
/// records remain fenced until an authoritative query returns finality. Terminal records
/// retain their canonical outcome and are compacted only after idempotent draft cleanup is
/// durably acknowledged or safely superseded.
/// </summary>
public sealed record TopicSendIdentityRecord(
    string LogicalIdentity,
    string ScopeIdentity,
    long SubmissionSequence,
    long ComposerRevision,
    string OperationId,
    string RunId,
    string LineId,
    string DraftFingerprint,
    TopicSendOutcomeKind? OutcomeKind = null,
    string? FailureMessage = null,
    int Version = 0,
    TopicSendJournalLifecycle Lifecycle = TopicSendJournalLifecycle.Unknown,
    TopicSendJournalCleanup Cleanup = TopicSendJournalCleanup.None,
    string? AccountId = null,
    long StateSequence = 0,
    TopicSendJournalCompaction Compaction = TopicSendJournalCompaction.Active,
    string? PayloadHash = null)
{
    public const int CurrentVersion = 5;

    public TopicSendOutcome? ToOutcome()
        => Version == CurrentVersion
           && TopicSendJournalInvariant.IsValid(this)
           && Lifecycle == TopicSendJournalLifecycle.Terminal
            ? OutcomeKind switch
        {
            TopicSendOutcomeKind.Accepted => new(
                TopicSendOutcomeKind.Accepted,
                new TopicSendHandoff(true, "recovered"),
                RequiresDraftClear: true,
                AuthoritativeRunId: RunId,
                AuthoritativeLineId: LineId),
            TopicSendOutcomeKind.Rejected => new(
                TopicSendOutcomeKind.Rejected,
                new TopicSendHandoff(false, "recovered", FailureMessage)),
            TopicSendOutcomeKind.Failed => new(
                TopicSendOutcomeKind.Failed,
                Exception: new InvalidOperationException(
                    FailureMessage ?? "The previous durable handoff failed.")),
            TopicSendOutcomeKind.RetryableFailed => new(
                TopicSendOutcomeKind.RetryableFailed,
                Exception: new InvalidOperationException(
                    FailureMessage ?? "The submission was not handed off.")),
            _ => null
        }
            : null;
}

internal static class TopicSendJournalInvariant
{
    public static bool IsValid(
        TopicSendIdentityRecord record,
        bool allowLegacyStateSequence = false)
    {
        if (string.IsNullOrWhiteSpace(record.ScopeIdentity)
            || string.IsNullOrWhiteSpace(record.LogicalIdentity)
            || record.SubmissionSequence <= 0
            || record.ComposerRevision <= 0
            || string.IsNullOrWhiteSpace(record.OperationId)
            || string.IsNullOrWhiteSpace(record.RunId)
            || string.IsNullOrWhiteSpace(record.LineId)
            || string.IsNullOrWhiteSpace(record.DraftFingerprint)
            || record.StateSequence <= 0 && !allowLegacyStateSequence)
            return false;

        if (record.Lifecycle is TopicSendJournalLifecycle.PreHandoff
            or TopicSendJournalLifecycle.AcceptedOrUnknown)
            return record.OutcomeKind is null
                   && record.FailureMessage is null
                   && record.Cleanup == TopicSendJournalCleanup.None
                   && record.Compaction == TopicSendJournalCompaction.Active;

        if (record.Lifecycle != TopicSendJournalLifecycle.Terminal
            || !IsTerminalOutcome(record.OutcomeKind)
            || record.Cleanup is not (TopicSendJournalCleanup.DraftClearPending
                or TopicSendJournalCleanup.DraftClearPersisted
                or TopicSendJournalCleanup.DraftClearSuperseded))
            return false;

        if (record.OutcomeKind == TopicSendOutcomeKind.Accepted
            && record.FailureMessage is not null)
            return false;

        return record.Compaction == TopicSendJournalCompaction.Active
               || record.Compaction == TopicSendJournalCompaction.Compacted
               && IsCleanupFinal(record.Cleanup);
    }

    public static bool IsTerminalOutcome(TopicSendOutcomeKind? outcome)
        => outcome is TopicSendOutcomeKind.Accepted
            or TopicSendOutcomeKind.Rejected
            or TopicSendOutcomeKind.Failed
            or TopicSendOutcomeKind.RetryableFailed;

    public static bool IsCleanupFinal(TopicSendJournalCleanup cleanup)
        => cleanup is TopicSendJournalCleanup.DraftClearPersisted
            or TopicSendJournalCleanup.DraftClearSuperseded;

    public static TopicSendIdentityRecord Normalize(TopicSendIdentityRecord record)
    {
        if (IsValid(record, allowLegacyStateSequence: true))
        {
            var normalized = record with
            {
                Version = TopicSendIdentityRecord.CurrentVersion,
                StateSequence = record.StateSequence > 0
                    ? record.StateSequence
                    : record.SubmissionSequence,
                PayloadHash = null
            };
            return TopicSendJournalOrdering.Prepare(normalized);
        }

        var scope = string.IsNullOrWhiteSpace(record.ScopeIdentity)
            ? TopicSendSnapshot.StableId("malformed-scope", record.OperationId ?? "missing")
            : record.ScopeIdentity;
        var sequenceValue = record.SubmissionSequence > 0 ? record.SubmissionSequence : 1;
        var revision = record.ComposerRevision > 0 ? record.ComposerRevision : 1;
        return TopicSendJournalOrdering.Prepare(new TopicSendIdentityRecord(
            TopicSendSnapshot.LogicalId(scope, sequenceValue),
            scope,
            sequenceValue,
            revision,
            string.IsNullOrWhiteSpace(record.OperationId)
                ? $"topic.send:{TopicSendSnapshot.StableId("malformed-operation", scope)}"
                : record.OperationId,
            string.IsNullOrWhiteSpace(record.RunId)
                ? TopicSendSnapshot.StableId("malformed-run", scope)
                : record.RunId,
            string.IsNullOrWhiteSpace(record.LineId)
                ? TopicSendSnapshot.StableId("malformed-line", scope)
                : record.LineId,
            string.IsNullOrWhiteSpace(record.DraftFingerprint)
                ? TopicSendSnapshot.StableId("malformed-draft", scope)
                : record.DraftFingerprint,
            Version: TopicSendIdentityRecord.CurrentVersion,
            Lifecycle: TopicSendJournalLifecycle.AcceptedOrUnknown,
            Cleanup: TopicSendJournalCleanup.None,
            StateSequence: sequenceValue));
    }
}

public enum TopicSendJournalBoundary
{
    BeforeWrite,
    AfterWrite,
    BeforeCompaction,
    AfterCompaction
}

public interface ITopicSendJournalFaultInjector
{
    void Checkpoint(
        string transition,
        TopicSendJournalBoundary boundary,
        TopicSendIdentityRecord record);
}

public sealed class TopicSendJournalCrashException(string message) : IOException(message);

public interface ITopicSendIdentityStore
{
    long NextSequence(string scopeIdentity);
    bool TryGetUnresolved(string scopeIdentity, out TopicSendIdentityRecord? record);
    void Save(TopicSendIdentityRecord record);
    void Remove(string scopeIdentity, string operationId);
    void Compact(TopicSendIdentityRecord record)
        => Remove(record.ScopeIdentity, record.OperationId);
    TopicSendJournalApplyResult Apply(TopicSendIdentityRecord record)
    {
        Save(record);
        return TopicSendJournalApplyResult.Advance;
    }
    TopicSendJournalApplyResult ApplyCompaction(TopicSendIdentityRecord record)
    {
        Compact(record);
        return TopicSendJournalApplyResult.Advance;
    }
}

public sealed class TopicSendJournalConflictException(string message)
    : InvalidOperationException(message);

internal sealed class TopicSendJournalStaleException(string message)
    : InvalidOperationException(message);

public sealed class InMemoryTopicSendIdentityStore : ITopicSendIdentityStore
{
    private readonly object gate = new();
    private long counter;
    private readonly Dictionary<string, TopicSendIdentityRecord> unresolved = new(StringComparer.Ordinal);

    public long NextSequence(string scopeIdentity)
    {
        lock (gate)
        {
            if (unresolved.TryGetValue(scopeIdentity, out var current))
                counter = Math.Max(
                    counter,
                    Math.Max(current.SubmissionSequence, current.StateSequence));
            return checked(++counter);
        }
    }

    public bool TryGetUnresolved(
        string scopeIdentity,
        out TopicSendIdentityRecord? record)
    {
        lock (gate)
        {
            if (!unresolved.TryGetValue(scopeIdentity, out record)
                || record.Compaction == TopicSendJournalCompaction.Compacted)
            {
                record = null;
                return false;
            }
            return true;
        }
    }

    public void Save(TopicSendIdentityRecord record)
    {
        var result = Apply(record);
        if (result == TopicSendJournalApplyResult.Conflict)
            throw TopicSendJournalOrdering.Conflict(record, record);
    }

    public void Compact(TopicSendIdentityRecord record)
    {
        var result = ApplyCompaction(record);
        if (result == TopicSendJournalApplyResult.Conflict)
            throw TopicSendJournalOrdering.Conflict(record, record);
    }

    public TopicSendJournalApplyResult Apply(TopicSendIdentityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record = TopicSendJournalOrdering.Prepare(record);
        lock (gate)
        {
            return ApplyLocked(record);
        }
    }

    public TopicSendJournalApplyResult ApplyCompaction(TopicSendIdentityRecord record)
        => Apply(record with
        {
            Compaction = TopicSendJournalCompaction.Compacted,
            PayloadHash = null
        });

    public void Remove(string scopeIdentity, string operationId)
    {
        lock (gate)
        {
            if (unresolved.TryGetValue(scopeIdentity, out var record)
                && string.Equals(record.OperationId, operationId, StringComparison.Ordinal))
                unresolved.Remove(scopeIdentity);
        }
    }

    private TopicSendJournalApplyResult ApplyLocked(TopicSendIdentityRecord candidate)
    {
        if (!unresolved.TryGetValue(candidate.ScopeIdentity, out var current))
        {
            unresolved[candidate.ScopeIdentity] = candidate;
            return TopicSendJournalApplyResult.Advance;
        }

        var result = TopicSendJournalOrdering.Compare(current, candidate);
        switch (result)
        {
            case TopicSendJournalApplyResult.Advance:
            case TopicSendJournalApplyResult.NewerSubmission:
                unresolved[candidate.ScopeIdentity] = candidate;
                break;
            case TopicSendJournalApplyResult.Replay:
            case TopicSendJournalApplyResult.Stale:
                break;
            case TopicSendJournalApplyResult.Conflict:
                throw TopicSendJournalOrdering.Conflict(current, candidate);
        }
        return result;
    }
}

public enum TopicSendJournalApplyResult
{
    Replay,
    Advance,
    NewerSubmission,
    Stale,
    Conflict
}

internal static class TopicSendJournalOrdering
{
    public static TopicSendIdentityRecord Prepare(TopicSendIdentityRecord record)
        => Prepare(record, allowInvalidLegacy: false);

    private static TopicSendIdentityRecord Prepare(
        TopicSendIdentityRecord record,
        bool allowInvalidLegacy)
    {
        var prepared = record with
        {
            StateSequence = record.StateSequence > 0
                ? record.StateSequence
                : record.SubmissionSequence,
            PayloadHash = null
        };
        if (!TopicSendJournalInvariant.IsValid(prepared)
            && !(allowInvalidLegacy
                 && prepared.Version != TopicSendIdentityRecord.CurrentVersion))
            throw new TopicSendJournalConflictException(
                $"Topic send journal lifecycle invariant is invalid at state sequence {prepared.StateSequence}.");
        var expectedHash = CanonicalPayloadHash(prepared);
        if (!string.IsNullOrWhiteSpace(record.PayloadHash)
            && !string.Equals(record.PayloadHash, expectedHash, StringComparison.Ordinal))
            throw new TopicSendJournalConflictException(
                $"Topic send journal payload hash is invalid at state sequence {prepared.StateSequence}.");
        return prepared with { PayloadHash = expectedHash };
    }

    public static TopicSendJournalApplyResult Compare(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
    {
        current = Prepare(current, allowInvalidLegacy: true);
        candidate = Prepare(candidate);
        if (candidate.SubmissionSequence != current.SubmissionSequence)
            return candidate.SubmissionSequence > current.SubmissionSequence
                ? TopicSendJournalApplyResult.NewerSubmission
                : TopicSendJournalApplyResult.Stale;

        if (candidate.StateSequence == current.StateSequence)
            return candidate == current
                ? TopicSendJournalApplyResult.Replay
                : TopicSendJournalApplyResult.Conflict;
        if (candidate.StateSequence < current.StateSequence)
            return TopicSendJournalApplyResult.Stale;
        if (IsLegacyMigration(current, candidate))
            return TopicSendJournalApplyResult.Advance;
        if (IsRejectedRetry(current, candidate))
            return TopicSendJournalApplyResult.Advance;
        if (IsObsoleteProgressRegression(current, candidate))
            return TopicSendJournalApplyResult.Stale;
        if (!SameStableIdentity(current, candidate))
            return TopicSendJournalApplyResult.Conflict;
        if (IsRegression(current, candidate))
            return TopicSendJournalApplyResult.Stale;
        return IsLegitimateAdvance(current, candidate)
            ? TopicSendJournalApplyResult.Advance
            : TopicSendJournalApplyResult.Conflict;
    }

    private static bool IsLegacyMigration(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
        => current.Version != TopicSendIdentityRecord.CurrentVersion
           && candidate.Version == TopicSendIdentityRecord.CurrentVersion
           && string.Equals(current.ScopeIdentity, candidate.ScopeIdentity, StringComparison.Ordinal)
           && current.SubmissionSequence == candidate.SubmissionSequence
           && (candidate.Lifecycle == TopicSendJournalLifecycle.AcceptedOrUnknown
               && candidate.Cleanup == TopicSendJournalCleanup.None
               && candidate.OutcomeKind is null
               || TopicSendJournalInvariant.IsValid(
                      current,
                      allowLegacyStateSequence: true)
                  && SameStableIdentity(current, candidate)
                  && current.Lifecycle == candidate.Lifecycle
                  && current.Cleanup == candidate.Cleanup
                  && current.OutcomeKind == candidate.OutcomeKind
                  && string.Equals(
                      current.FailureMessage,
                      candidate.FailureMessage,
                      StringComparison.Ordinal)
                  && current.Compaction == candidate.Compaction);

    private static bool IsObsoleteProgressRegression(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
        => string.Equals(current.ScopeIdentity, candidate.ScopeIdentity, StringComparison.Ordinal)
           && string.Equals(current.OperationId, candidate.OperationId, StringComparison.Ordinal)
           && candidate.OutcomeKind is null
           && (candidate.Lifecycle < current.Lifecycle
               || current.Compaction == TopicSendJournalCompaction.Compacted
               && candidate.Compaction == TopicSendJournalCompaction.Active);

    private static bool IsRejectedRetry(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
        => current.Lifecycle == TopicSendJournalLifecycle.Terminal
           && current.OutcomeKind == TopicSendOutcomeKind.Rejected
           && current.Compaction == TopicSendJournalCompaction.Compacted
           && candidate.Lifecycle == TopicSendJournalLifecycle.PreHandoff
           && candidate.OutcomeKind is null
           && candidate.Cleanup == TopicSendJournalCleanup.None
           && candidate.Compaction == TopicSendJournalCompaction.Active
           && SameStableIdentity(current, candidate)
           && string.Equals(current.RunId, candidate.RunId, StringComparison.Ordinal);

    private static bool IsRegression(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
    {
        if (current.Compaction == TopicSendJournalCompaction.Compacted
            && candidate.Compaction == TopicSendJournalCompaction.Active)
            return candidate.Lifecycle <= current.Lifecycle
                   && candidate.Cleanup == current.Cleanup
                   && (candidate.OutcomeKind is null
                       || SameTerminalPayload(current, candidate));
        if (candidate.Lifecycle < current.Lifecycle)
            return candidate.OutcomeKind is null
                   || SameTerminalPayload(current, candidate);
        return current.Lifecycle == TopicSendJournalLifecycle.Terminal
               && candidate.Lifecycle == TopicSendJournalLifecycle.Terminal
               && CleanupProgress(candidate.Cleanup) < CleanupProgress(current.Cleanup)
               && SameTerminalPayload(current, candidate);

        static int CleanupProgress(TopicSendJournalCleanup cleanup)
            => cleanup == TopicSendJournalCleanup.DraftClearPending ? 0 : 1;
    }

    public static TopicSendJournalConflictException Conflict(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
        => new(
            "Conflicting topic send journal transition was fenced "
            + $"(current={current.StateSequence}:{current.Lifecycle}:{current.Cleanup}:{current.OutcomeKind}:"
            + $"{TopicSendSnapshot.StableId("diagnostic", current.OperationId)}:"
            + $"{TopicSendSnapshot.StableId("diagnostic", current.RunId)}, "
            + $"candidate={candidate.StateSequence}:{candidate.Lifecycle}:{candidate.Cleanup}:"
            + $"{candidate.OutcomeKind}:{TopicSendSnapshot.StableId("diagnostic", candidate.OperationId)}:"
            + $"{TopicSendSnapshot.StableId("diagnostic", candidate.RunId)}).");

    private static bool SameStableIdentity(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
        => string.Equals(current.LogicalIdentity, candidate.LogicalIdentity, StringComparison.Ordinal)
           && string.Equals(current.ScopeIdentity, candidate.ScopeIdentity, StringComparison.Ordinal)
           && current.ComposerRevision == candidate.ComposerRevision
           && string.Equals(current.OperationId, candidate.OperationId, StringComparison.Ordinal)
           && string.Equals(current.LineId, candidate.LineId, StringComparison.Ordinal)
           && string.Equals(current.DraftFingerprint, candidate.DraftFingerprint, StringComparison.Ordinal)
           && string.Equals(current.AccountId, candidate.AccountId, StringComparison.Ordinal);

    private static bool IsLegitimateAdvance(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
    {
        if (current.Compaction == TopicSendJournalCompaction.Compacted)
            return false;
        if (candidate.Compaction == TopicSendJournalCompaction.Compacted)
            return current.Lifecycle == candidate.Lifecycle
                   && current.Cleanup == candidate.Cleanup
                   && current.OutcomeKind == candidate.OutcomeKind
                   && string.Equals(current.FailureMessage, candidate.FailureMessage, StringComparison.Ordinal)
                   && string.Equals(current.RunId, candidate.RunId, StringComparison.Ordinal);
        if (current.Compaction != candidate.Compaction)
            return false;

        if (current.Lifecycle == candidate.Lifecycle
            && current.Cleanup == candidate.Cleanup
            && current.OutcomeKind == candidate.OutcomeKind
            && string.Equals(current.FailureMessage, candidate.FailureMessage, StringComparison.Ordinal)
            && string.Equals(current.RunId, candidate.RunId, StringComparison.Ordinal))
            return true;

        return (current.Lifecycle, current.Cleanup, candidate.Lifecycle, candidate.Cleanup) switch
        {
            (TopicSendJournalLifecycle.PreHandoff, TopicSendJournalCleanup.None,
                TopicSendJournalLifecycle.AcceptedOrUnknown, TopicSendJournalCleanup.None) =>
                current.OutcomeKind is null
                && candidate.OutcomeKind is null,
            (TopicSendJournalLifecycle.PreHandoff, TopicSendJournalCleanup.None,
                TopicSendJournalLifecycle.Terminal, _) =>
                current.OutcomeKind is null
                && TopicSendJournalInvariant.IsValid(candidate),
            (TopicSendJournalLifecycle.AcceptedOrUnknown, TopicSendJournalCleanup.None,
                TopicSendJournalLifecycle.Terminal, _) =>
                current.OutcomeKind is null
                && TopicSendJournalInvariant.IsValid(candidate),
            (TopicSendJournalLifecycle.Terminal, TopicSendJournalCleanup.DraftClearPending,
                TopicSendJournalLifecycle.Terminal, TopicSendJournalCleanup.DraftClearPersisted
                    or TopicSendJournalCleanup.DraftClearSuperseded) =>
                SameTerminalPayload(current, candidate),
            _ => false
        };

        static bool SameTerminalPayload(
            TopicSendIdentityRecord current,
            TopicSendIdentityRecord candidate)
            => TopicSendJournalOrdering.SameTerminalPayload(current, candidate);
    }

    private static bool SameTerminalPayload(
        TopicSendIdentityRecord current,
        TopicSendIdentityRecord candidate)
        => current.OutcomeKind == candidate.OutcomeKind
           && string.Equals(current.FailureMessage, candidate.FailureMessage, StringComparison.Ordinal)
           && string.Equals(current.RunId, candidate.RunId, StringComparison.Ordinal);

    private static string CanonicalPayloadHash(TopicSendIdentityRecord record)
    {
        var canonical = string.Join(
            "\0",
            record.LogicalIdentity,
            record.ScopeIdentity,
            record.SubmissionSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.ComposerRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.OperationId,
            record.RunId,
            record.LineId,
            record.DraftFingerprint,
            ((int?)record.OutcomeKind)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            record.FailureMessage ?? "-",
            record.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)record.Lifecycle).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)record.Cleanup).ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.AccountId ?? "-",
            record.StateSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)record.Compaction).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return TopicSendSnapshot.StableId("journal-payload", canonical);
    }
}

public sealed class KeyValueTopicSendIdentityStore(
    Func<string, string?> read,
    Action<string, string> write,
    Action<string>? remove = null) : ITopicSendIdentityStore
{
    private const string Prefix = "mesh.ui.topic-send.v5.";
    private const string PriorPrefix = "mesh.ui.topic-send.v4.";
    private const string LegacyPrefix = "mesh.ui.topic-send.v3.";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly object gate = new();

    public long NextSequence(string scopeIdentity)
    {
        lock (gate)
        {
            var key = Prefix + "counter.global";
            var legacyKey = LegacyPrefix + "counter.global";
            _ = long.TryParse(
                read(key),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var current);
            _ = long.TryParse(
                read(PriorPrefix + "counter.global"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var prior);
            _ = long.TryParse(
                read(legacyKey),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var legacy);
            current = Math.Max(current, Math.Max(prior, legacy));
            TopicSendIdentityRecord? journal = null;
            _ = TryReadLocked(
                    Prefix,
                    $"pending.{scopeIdentity}",
                    scopeIdentity,
                    out journal)
                || TryReadLocked(
                    PriorPrefix,
                    $"pending.{scopeIdentity}",
                    scopeIdentity,
                    out journal)
                || TryReadLocked(
                    LegacyPrefix,
                    $"pending.{scopeIdentity}",
                    scopeIdentity,
                    out journal);
            if (journal is not null)
                current = Math.Max(
                    current,
                    Math.Max(journal.SubmissionSequence, journal.StateSequence));
            var next = checked(current + 1);
            write(key, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return next;
        }
    }

    public bool TryGetUnresolved(
        string scopeIdentity,
        out TopicSendIdentityRecord? record)
    {
        if (TryRead(Prefix, $"pending.{scopeIdentity}", scopeIdentity, out record))
            return ReturnIfActive(ref record);
        if (TryRead(PriorPrefix, $"pending.{scopeIdentity}", scopeIdentity, out record))
            return ReturnIfActive(ref record);
        if (TryRead(LegacyPrefix, $"pending.{scopeIdentity}", scopeIdentity, out record))
            return ReturnIfActive(ref record);
        return false;

        static bool ReturnIfActive(ref TopicSendIdentityRecord? found)
        {
            if (found?.Compaction != TopicSendJournalCompaction.Compacted)
                return true;
            found = null;
            return false;
        }
    }

    public void Save(TopicSendIdentityRecord record)
    {
        var result = Apply(record);
        if (result == TopicSendJournalApplyResult.Conflict)
            throw TopicSendJournalOrdering.Conflict(record, record);
    }

    public TopicSendJournalApplyResult Apply(TopicSendIdentityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record = TopicSendJournalOrdering.Prepare(record);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        lock (gate)
        {
            var result = TopicSendJournalApplyResult.Advance;
            if ((TryReadLocked(
                        Prefix,
                        $"pending.{record.ScopeIdentity}",
                        record.ScopeIdentity,
                        out var current)
                    || TryReadLocked(
                        PriorPrefix,
                        $"pending.{record.ScopeIdentity}",
                        record.ScopeIdentity,
                        out current)
                    || TryReadLocked(
                        LegacyPrefix,
                        $"pending.{record.ScopeIdentity}",
                        record.ScopeIdentity,
                        out current))
                && current is not null)
            {
                result = TopicSendJournalOrdering.Compare(current, record);
                switch (result)
                {
                    case TopicSendJournalApplyResult.Replay:
                        return TopicSendJournalApplyResult.Replay;
                    case TopicSendJournalApplyResult.Stale:
                        return TopicSendJournalApplyResult.Stale;
                    case TopicSendJournalApplyResult.Conflict:
                        throw TopicSendJournalOrdering.Conflict(current, record);
                }
            }
            write(Prefix + $"pending.{record.ScopeIdentity}", json);
            RemoveOrClear(PriorPrefix + $"pending.{record.ScopeIdentity}");
            var legacyKey = LegacyPrefix + $"pending.{record.ScopeIdentity}";
            RemoveOrClear(legacyKey);
            return result;
        }
    }

    public void Compact(TopicSendIdentityRecord record)
    {
        var result = ApplyCompaction(record);
        if (result == TopicSendJournalApplyResult.Conflict)
            throw TopicSendJournalOrdering.Conflict(record, record);
    }

    public TopicSendJournalApplyResult ApplyCompaction(TopicSendIdentityRecord record)
        => Apply(record with
        {
            Compaction = TopicSendJournalCompaction.Compacted,
            PayloadHash = null
        });

    public void Remove(string scopeIdentity, string operationId)
    {
        lock (gate)
        {
            if (!TryReadLocked(Prefix, $"pending.{scopeIdentity}", scopeIdentity, out var record)
                || record is null
                || !string.Equals(record.OperationId, operationId, StringComparison.Ordinal))
                return;
            var key = Prefix + $"pending.{scopeIdentity}";
            if (remove is null) write(key, "");
            else remove(key);
            var legacyKey = LegacyPrefix + $"pending.{scopeIdentity}";
            RemoveOrClear(legacyKey);
            RemoveOrClear(PriorPrefix + $"pending.{scopeIdentity}");
        }
    }

    private void RemoveOrClear(string key)
    {
        if (remove is null) write(key, "");
        else remove(key);
    }

    private bool TryRead(
        string prefix,
        string key,
        string scopeIdentity,
        out TopicSendIdentityRecord? record)
    {
        lock (gate) return TryReadLocked(prefix, key, scopeIdentity, out record);
    }

    private bool TryReadLocked(
        string prefix,
        string key,
        string scopeIdentity,
        out TopicSendIdentityRecord? record)
    {
        var json = read(prefix + key);
        if (string.IsNullOrWhiteSpace(json))
        {
            record = null;
            return false;
        }

        try
        {
            record = JsonSerializer.Deserialize<TopicSendIdentityRecord>(json, JsonOptions);
            return record is not null;
        }
        catch (JsonException)
        {
            var fingerprint = TopicSendSnapshot.StableId("malformed-journal", json);
            record = new TopicSendIdentityRecord(
                TopicSendSnapshot.StableId("malformed-logical", scopeIdentity),
                scopeIdentity,
                1,
                1,
                $"topic.send:{TopicSendSnapshot.StableId("malformed-operation", scopeIdentity)}",
                TopicSendSnapshot.StableId("malformed-run", scopeIdentity),
                TopicSendSnapshot.StableId("malformed-line", scopeIdentity),
                fingerprint,
                Version: -1);
            return true;
        }
    }
}

public interface ITopicSendObserverSubscription : IDisposable, IAsyncDisposable
{
    string OperationId { get; }
    string ObserverId { get; }
    int InFlightCallbackCount { get; }
}

public interface ITopicSendObserverDispatcher
{
    Task InvokeAsync(Func<Task> workItem);
}

internal interface ITopicSendLifecycleTestObserver
{
    void Checkpoint(string name);
    void JournalWrite(string transition, bool disposalCompleted);
    void CallbackQueued(string operationId, bool disposalCompleted);
    void FinalizationCompleted(
        string operationId,
        TopicSendIdentityRecord record,
        bool cached)
    {
    }
}

public sealed class TopicSendHandoffContext(
    Action? enterDurableBoundary = null,
    Func<bool>? authorizeDurableHandoff = null,
    Func<TopicRunBeginCommand, TopicRunBeginResult>? authorizeAndBeginTopicRun = null)
{
    private int durableBoundaryEntered;
    private int authorizationEntered;
    private int atomicBeginEntered;

    public void AuthorizeDurableHandoff()
    {
        if (authorizeDurableHandoff is null) return;
        if (!authorizeDurableHandoff())
            throw new TopicSendAuthorizationException(
                "The account database changed after retry authorization. The send was fenced for a fresh authoritative lookup.");
        Volatile.Write(ref authorizationEntered, 1);
    }

    public void MarkDurableBoundaryEntered()
    {
        if (Interlocked.Exchange(ref durableBoundaryEntered, 1) != 0) return;
        enterDurableBoundary?.Invoke();
    }

    internal bool DurableBoundaryEntered
        => Volatile.Read(ref durableBoundaryEntered) != 0;

    internal bool AuthorizationEntered
        => authorizeDurableHandoff is null
           && authorizeAndBeginTopicRun is null
           || Volatile.Read(ref authorizationEntered) != 0;

    internal TopicRunBeginResult BeginTopicRun(
        TopicRunBeginCommand command,
        Func<TopicRunBeginResult> unscopedBegin)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(unscopedBegin);
        if (authorizeAndBeginTopicRun is null)
            return unscopedBegin();
        if (Interlocked.Exchange(ref atomicBeginEntered, 1) != 0)
            throw new TopicSendAuthorizationException(
                "The retry authorization has already been presented.");

        var result = authorizeAndBeginTopicRun(command);
        Volatile.Write(ref authorizationEntered, 1);
        if (!result.DurableCommitted
            && string.Equals(result.Code, "reconcile_required", StringComparison.Ordinal))
            throw new TopicSendAuthorizationException(
                "The authoritative NotFound observation is stale. A fresh reconciliation is required.");
        return result;
    }
}

public sealed class TopicSendAuthorizationException(string message) : InvalidOperationException(message);

public sealed class TopicSendCoordinator : IDisposable, IAsyncDisposable
{
    private readonly record struct LogicalSendKey(
        string? AccountId,
        string ThreadId,
        string TargetDeviceId,
        long ComposerRevision);

    private sealed record SnapshotEntry(TopicSendSnapshot Snapshot, long Sequence);

    private sealed record CompletedIdentity(
        LogicalSendKey Key,
        TopicSendSnapshot Snapshot,
        TopicSendOutcome Outcome,
        DateTimeOffset ExpiresAt,
        long SubmissionSequence)
    {
        public string OperationId => Snapshot.OperationId;
    }

    private sealed class OperationState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, ObserverSubscription> Observers =
            new(StringComparer.Ordinal);
        public readonly Dictionary<string, TaskCompletionSource> ObserverDetachBarriers =
            new(StringComparer.Ordinal);
        public TopicSendOutcome? TerminalOutcome;
        public TopicSendOutcome? JournalTerminalOutcome;
        public TopicSendOutcome? LastOutcome;
        public TopicSendOutcome? ObservableOutcome;
        public TopicSendJournalLifecycle Lifecycle = TopicSendJournalLifecycle.PreHandoff;
        public TopicSendJournalCleanup Cleanup = TopicSendJournalCleanup.None;
        public TopicSendRetryAuthorization? RetryAuthorization;
        public TopicSendAuthorizationScope? ConsumedAuthorizationScope;
        public string? FencedError;
        public string? PendingFailure;
        public TopicSendDraftCleanup? RecoveryCleanup;
        public TaskCompletionSource? ReconciliationCompletion;
        public int AvailabilityAttempt;
        public long PendingAvailabilityGeneration;
        public long ProcessedAvailabilityGeneration;
        public int Running = 1;
    }

    private sealed class ObserverSubscription : ITopicSendObserverSubscription
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource quiescence =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim dispatchGate = new(1, 1);
        private readonly ITopicSendObserverDispatcher dispatcher;
        private TopicSendCoordinator? owner;
        private OperationState? operation;
        private Func<TopicSendOutcome, Task>? callback;
        private CancellationTokenRegistration cancellationRegistration;
        private bool detached;
        private bool terminalQueued;
        private long generation = 1;
        private int executing;

        public ObserverSubscription(
            TopicSendCoordinator owner,
            OperationState? operation,
            string operationId,
            string observerId,
            ITopicSendObserverDispatcher dispatcher,
            Func<TopicSendOutcome, Task> callback)
        {
            this.owner = owner;
            this.operation = operation;
            this.dispatcher = dispatcher;
            this.callback = callback;
            OperationId = operationId;
            ObserverId = observerId;
        }

        public string OperationId { get; }
        public string ObserverId { get; }
        public int InFlightCallbackCount
        {
            get
            {
                lock (gate) return executing;
            }
        }

        public void AttachCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return;
            var registration = cancellationToken.Register(
                static state => ((ObserverSubscription)state!).Dispose(),
                this);
            lock (gate)
            {
                if (detached)
                    registration.Dispose();
                else
                    cancellationRegistration = registration;
            }
        }

        public Task QueueDispatch(
            TopicSendOutcome outcome,
            bool detachAfter)
        {
            long queuedGeneration;
            lock (gate)
            {
                if (detached || callback is null) return Task.CompletedTask;
                queuedGeneration = generation;
                terminalQueued |= detachAfter;
            }

            Task dispatch;
            try
            {
                dispatch = dispatcher.InvokeAsync(
                    () => InvokeOnDispatcherAsync(outcome, queuedGeneration, detachAfter));
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-dispatch-failed",
                    $"exception={exception.GetType().FullName}");
                dispatch = Task.CompletedTask;
            }

            return CompleteDispatchAsync(dispatch, detachAfter);
        }

        private async Task CompleteDispatchAsync(Task dispatch, bool detachAfter)
        {
            try
            {
                await dispatch.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-dispatch-failed",
                    $"exception={exception.GetType().FullName}");
            }
            finally
            {
                if (detachAfter)
                    await DisposeAsync().ConfigureAwait(false);
            }
        }

        public bool HoldsCallback
        {
            get
            {
                lock (gate) return callback is not null;
            }
        }

        public void Dispose()
            => Detach();

        public ValueTask DisposeAsync()
            => new(Detach());

        private Task Detach()
        {
            TopicSendCoordinator? foundOwner = null;
            OperationState? foundOperation = null;
            CancellationTokenRegistration registration = default;
            var complete = false;
            lock (gate)
            {
                if (!detached)
                {
                    detached = true;
                    generation++;
                    foundOwner = owner;
                    foundOperation = operation;
                    owner = null;
                    operation = null;
                    registration = cancellationRegistration;
                    cancellationRegistration = default;
                    callback = null;
                    if (executing == 0)
                    {
                        complete = true;
                    }
                }
            }
            registration.Dispose();
            foundOwner?.Detach(foundOperation, this, quiescence.Task);
            if (complete)
                quiescence.TrySetResult();
            return quiescence.Task;
        }

        private async Task InvokeOnDispatcherAsync(
            TopicSendOutcome outcome,
            long queuedGeneration,
            bool terminal)
        {
            await dispatchGate.WaitAsync();
            Func<TopicSendOutcome, Task>? found;
            try
            {
                lock (gate)
                {
                    if (detached
                        || generation != queuedGeneration
                        || owner?.IsDisposing != false
                        || callback is null
                        || !terminal && terminalQueued)
                        return;
                    found = callback;
                    executing++;
                }

                try
                {
                    await found(outcome);
                }
                catch (Exception exception)
                {
                    RuntimeDiagnostics.Current?.RecordEvent(
                        "topic-send-callback-failed",
                        $"exception={exception.GetType().FullName}");
                }
                finally
                {
                    var complete = false;
                    lock (gate)
                    {
                        if (executing <= 0)
                            throw new InvalidOperationException(
                                "Observer callback execution was released more than once.");
                        executing--;
                        if (detached && executing == 0)
                            complete = true;
                    }
                    if (complete)
                        quiescence.TrySetResult();
                }
            }
            finally
            {
                dispatchGate.Release();
            }
        }
    }

    private sealed class ImmediateObserverDispatcher : ITopicSendObserverDispatcher
    {
        public static ImmediateObserverDispatcher Instance { get; } = new();

        public Task InvokeAsync(Func<Task> workItem)
            => workItem();
    }

    private sealed class MutationLease(
        TopicSendCoordinator owner,
        long generation) : IDisposable
    {
        private TopicSendCoordinator? owner = owner;

        public long Generation { get; } = generation;

        public void Dispose()
            => Interlocked.Exchange(ref owner, null)?.ReleaseMutationLease();
    }

    private readonly object identityGate = new();
    private readonly object lifetimeGate = new();
    private readonly TopicSendRetentionOptions retention;
    private readonly TimeProvider timeProvider;
    private readonly ITopicSendIdentityStore identityStore;
    private readonly ITopicSendReconciliationQuery? reconciliationQuery;
    private readonly ITopicSendAuthorizationAuthority? authorizationAuthority;
    private readonly ITopicSendReconciliationAvailability? reconciliationAvailability;
    private readonly ITopicSendJournalFaultInjector? journalFaultInjector;
    private readonly ITopicSendLifecycleTestObserver? testObserver;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, OperationState> operations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<LogicalSendKey, SnapshotEntry> snapshots = new();
    private readonly Dictionary<LogicalSendKey, CompletedIdentity> completedByKey = new();
    private readonly Dictionary<string, CompletedIdentity> completedByOperation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TopicSendOutcome Outcome, DateTimeOffset ExpiresAt)>
        retryableOutcomes = new(StringComparer.Ordinal);
    private readonly HashSet<ObserverSubscription> queuedSubscriptions = [];
    private long sequence;
    private long availabilityGeneration;
    private long lifetimeGeneration = 1;
    private int activeMutationLeases;
    private TaskCompletionSource? leaseDrain;
    private Task? disposalTask;
    private int disposed;
    private int disposalCompleted;

    private bool IsDisposing => Volatile.Read(ref disposed) != 0;

    public TopicSendCoordinator(
        TopicSendRetentionOptions? retention = null,
        TimeProvider? timeProvider = null,
        ITopicSendIdentityStore? identityStore = null,
        ITopicSendReconciliationQuery? reconciliationQuery = null,
        ITopicSendJournalFaultInjector? journalFaultInjector = null)
        : this(
            retention,
            timeProvider,
            identityStore,
            reconciliationQuery,
            journalFaultInjector,
            null)
    {
    }

    internal TopicSendCoordinator(
        TopicSendRetentionOptions? retention,
        TimeProvider? timeProvider,
        ITopicSendIdentityStore? identityStore,
        ITopicSendReconciliationQuery? reconciliationQuery,
        ITopicSendJournalFaultInjector? journalFaultInjector,
        ITopicSendLifecycleTestObserver? testObserver)
    {
        this.retention = retention ?? new TopicSendRetentionOptions();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.identityStore = identityStore ?? new InMemoryTopicSendIdentityStore();
        this.reconciliationQuery = reconciliationQuery;
        authorizationAuthority = reconciliationQuery as ITopicSendAuthorizationAuthority;
        this.journalFaultInjector = journalFaultInjector;
        this.testObserver = testObserver;
        if (reconciliationQuery is ITopicSendReconciliationAvailability availability)
        {
            reconciliationAvailability = availability;
            availability.AvailabilityChanged += OnReconciliationAvailabilityChanged;
        }
        if (this.retention.MaximumRunningOperations <= 0
            || this.retention.MaximumCompletedIdentities <= 0
            || this.retention.MaximumUnsubmittedSnapshots <= 0
            || this.retention.CompletedIdentityRetention <= TimeSpan.Zero
            || this.retention.MaximumReconciliationAttempts <= 0
            || this.retention.ReconciliationInitialBackoff < TimeSpan.Zero
            || this.retention.ReconciliationMaximumBackoff
               < this.retention.ReconciliationInitialBackoff)
            throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (lifetimeGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task drain;
        lock (lifetimeGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Increment(ref lifetimeGeneration);
                if (reconciliationAvailability is not null)
                    reconciliationAvailability.AvailabilityChanged -=
                        OnReconciliationAvailabilityChanged;
                lifetime.Cancel();
            }
            drain = activeMutationLeases == 0
                ? Task.CompletedTask
                : (leaseDrain ??= new(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await drain.ConfigureAwait(false);

        List<ObserverSubscription> subscriptions = [];
        lock (identityGate)
        {
            foreach (var state in operations.Values)
            {
                lock (state.Gate)
                {
                    Volatile.Write(ref state.Running, 0);
                    subscriptions.AddRange(state.Observers.Values);
                    state.Observers.Clear();
                    foreach (var barrier in state.ObserverDetachBarriers.Values)
                        barrier.TrySetResult();
                    state.ObserverDetachBarriers.Clear();
                }
            }
        }
        lock (lifetimeGate)
        {
            subscriptions.AddRange(queuedSubscriptions);
            queuedSubscriptions.Clear();
        }

        // Detach clears delegates and increments callback generations synchronously.
        // Do not await renderer execution here: renderer disposal can own that dispatcher.
        foreach (var subscription in subscriptions)
            subscription.Dispose();
        Volatile.Write(ref disposalCompleted, 1);
    }

    private MutationLease? TryAcquireMutationLease(long? expectedGeneration = null)
    {
        lock (lifetimeGate)
        {
            var generation = lifetimeGeneration;
            if (Volatile.Read(ref disposed) != 0
                || expectedGeneration is not null
                && expectedGeneration.Value != generation)
                return null;
            checked { activeMutationLeases++; }
            return new(this, generation);
        }
    }

    private void ReleaseMutationLease()
    {
        TaskCompletionSource? complete = null;
        lock (lifetimeGate)
        {
            if (--activeMutationLeases < 0)
                throw new InvalidOperationException("A coordinator mutation lease was released twice.");
            if (activeMutationLeases == 0 && Volatile.Read(ref disposed) != 0)
                complete = leaseDrain;
        }
        complete?.TrySetResult();
    }

    public int RunningOperationCount
    {
        get
        {
            lock (identityGate)
                return operations.Values.Count(
                    state => Volatile.Read(ref state.Running) != 0);
        }
    }

    public int CompletedIdentityCount
    {
        get
        {
            lock (identityGate)
            {
                PruneCompletedLocked();
                return completedByKey.Count;
            }
        }
    }

    public TopicSendSnapshot CreateSnapshot(
        string threadId,
        string targetDeviceId,
        long composerRevision,
        string draftFingerprint,
        DateTimeOffset submittedAt,
        string? accountId = null)
    {
        using var mutation = TryAcquireMutationLease()
            ?? throw new ObjectDisposedException(nameof(TopicSendCoordinator));
        var key = new LogicalSendKey(accountId, threadId, targetDeviceId, composerRevision);
        var scopeIdentity = TopicSendSnapshot.ScopeId(threadId, targetDeviceId);
        lock (identityGate)
        {
            PruneCompletedLocked();
            if (snapshots.TryGetValue(key, out var existing))
                return existing.Snapshot;
            if (completedByKey.TryGetValue(key, out var completed))
                return completed.Snapshot;
            if (TryRecoverLocked(
                    scopeIdentity,
                    threadId,
                    targetDeviceId,
                    composerRevision,
                    submittedAt,
                    out var recovered)
                && recovered is not null)
            {
                var recoveredKey = new LogicalSendKey(
                    recovered.AccountId,
                    threadId,
                    targetDeviceId,
                    recovered.ComposerRevision);
                snapshots[recoveredKey] = new SnapshotEntry(recovered, ++sequence);
                return recovered;
            }

            var submissionSequence = identityStore.NextSequence(scopeIdentity);
            var snapshot = TopicSendSnapshot.Create(
                threadId,
                targetDeviceId,
                submissionSequence,
                composerRevision,
                draftFingerprint,
                submittedAt,
                accountId);
            snapshots[key] = new SnapshotEntry(snapshot, ++sequence);
            PruneSnapshotsLocked();
            return snapshot;
        }
    }

    public bool IsRunning(string operationId)
    {
        lock (identityGate)
            return operations.TryGetValue(operationId, out var state)
                   && state.Lifecycle == TopicSendJournalLifecycle.PreHandoff
                   && Volatile.Read(ref state.Running) != 0;
    }

    public int ObserverCount(string operationId)
    {
        lock (identityGate)
        {
            if (!operations.TryGetValue(operationId, out var state)) return 0;
            lock (state.Gate) return state.Observers.Count;
        }
    }

    public int ObserverReferenceCount(string operationId)
    {
        lock (identityGate)
        {
            if (!operations.TryGetValue(operationId, out var state)) return 0;
            lock (state.Gate)
                return state.Observers.Values.Count(subscription => subscription.HoldsCallback);
        }
    }

    public int ObserverInFlightCount(string operationId)
    {
        lock (identityGate)
        {
            if (!operations.TryGetValue(operationId, out var state)) return 0;
            lock (state.Gate)
                return state.Observers.Values.Sum(subscription => subscription.InFlightCallbackCount);
        }
    }

    public Task WaitForObserverDetachedAsync(string operationId, string observerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observerId);
        lock (identityGate)
        {
            if (!operations.TryGetValue(operationId, out var state))
                return Task.CompletedTask;
            lock (state.Gate)
            {
                if (!state.ObserverDetachBarriers.TryGetValue(observerId, out var barrier))
                {
                    if (!state.Observers.ContainsKey(observerId))
                        return Task.CompletedTask;
                    barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    state.ObserverDetachBarriers.Add(observerId, barrier);
                }
                return barrier.Task;
            }
        }
    }

    public bool IsRunning(
        string threadId,
        string targetDeviceId,
        long composerRevision)
    {
        lock (identityGate)
        {
            var snapshot = snapshots.Values.FirstOrDefault(entry =>
                string.Equals(entry.Snapshot.ThreadId, threadId, StringComparison.Ordinal)
                && string.Equals(
                    entry.Snapshot.TargetDeviceId,
                    targetDeviceId,
                    StringComparison.Ordinal)
                && entry.Snapshot.ComposerRevision == composerRevision);
            return snapshot is not null
                   && operations.TryGetValue(snapshot.Snapshot.OperationId, out var state)
                   && state.Lifecycle == TopicSendJournalLifecycle.PreHandoff
                   && Volatile.Read(ref state.Running) != 0;
        }
    }

    public bool TryGetSnapshot(
        string threadId,
        string targetDeviceId,
        long composerRevision,
        out TopicSendSnapshot? snapshot,
        string? accountId = null)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null)
        {
            snapshot = null;
            return false;
        }
        var key = new LogicalSendKey(accountId, threadId, targetDeviceId, composerRevision);
        lock (identityGate)
        {
            PruneCompletedLocked();
            if (snapshots.TryGetValue(key, out var found))
            {
                snapshot = found.Snapshot;
                return true;
            }
            if (accountId is null)
            {
                found = snapshots.Values.FirstOrDefault(entry =>
                    string.Equals(entry.Snapshot.ThreadId, threadId, StringComparison.Ordinal)
                    && string.Equals(
                        entry.Snapshot.TargetDeviceId,
                        targetDeviceId,
                        StringComparison.Ordinal)
                    && entry.Snapshot.ComposerRevision == composerRevision);
                if (found is not null)
                {
                    snapshot = found.Snapshot;
                    return true;
                }
            }
            var scopeIdentity = TopicSendSnapshot.ScopeId(threadId, targetDeviceId);
            if (TryRecoverLocked(
                    scopeIdentity,
                    threadId,
                    targetDeviceId,
                    composerRevision,
                    timeProvider.GetUtcNow(),
                    out snapshot)
                && snapshot is not null)
            {
                var recoveredKey = new LogicalSendKey(
                    snapshot.AccountId,
                    threadId,
                    targetDeviceId,
                    snapshot.ComposerRevision);
                snapshots[recoveredKey] = new SnapshotEntry(snapshot, ++sequence);
                return true;
            }
            snapshot = null;
            return false;
        }
    }

    public ITopicSendObserverSubscription? Observe(
        string operationId,
        string observerId,
        Func<TopicSendOutcome, Task> observer,
        CancellationToken callbackCancellation = default)
        => Observe(
            operationId,
            observerId,
            ImmediateObserverDispatcher.Instance,
            observer,
            callbackCancellation);

    public ITopicSendObserverSubscription? Observe(
        string operationId,
        string observerId,
        ITopicSendObserverDispatcher dispatcher,
        Func<TopicSendOutcome, Task> observer,
        CancellationToken callbackCancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observerId);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(observer);
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return null;

        OperationState? state;
        TopicSendOutcome? terminal = null;
        lock (identityGate)
        {
            PruneCompletedLocked();
            if (!operations.TryGetValue(operationId, out state))
            {
                if (completedByOperation.TryGetValue(operationId, out var completed))
                    terminal = completed.Outcome;
                if (terminal is null) return null;
            }
        }

        var subscription = new ObserverSubscription(
            this, state, operationId, observerId, dispatcher, observer);
        if (state is not null)
        {
            lock (state.Gate)
            {
                terminal = state.TerminalOutcome;
                if (terminal is null)
                {
                    if (state.Observers.ContainsKey(observerId))
                    {
                        subscription.Dispose();
                        return null;
                    }
                    state.ObserverDetachBarriers.Remove(observerId);
                    state.Observers.Add(observerId, subscription);
                    terminal = state.LastOutcome;
                }
            }
        }

        subscription.AttachCancellation(callbackCancellation);
        if (terminal is not null)
        {
            if (terminal.Kind == TopicSendOutcomeKind.Reconciling)
                _ = NotifySubscriptionProgressAsync(subscription, terminal);
            else
                _ = NotifySubscriptionAsync(subscription, terminal);
        }
        return subscription;
    }

    public bool TrySubmit(
        TopicSendSnapshot snapshot,
        Func<TopicSendSnapshot, Task<TopicSendHandoff>> durableHandoff,
        Func<TopicSendOutcome, Task>? reconcileOutcome = null,
        TopicSendDraftCleanup? draftCleanup = null)
        => Submit(snapshot, durableHandoff, reconcileOutcome, draftCleanup).Started;

    public TopicSendSubmissionResult Submit(
        TopicSendSnapshot snapshot,
        Func<TopicSendSnapshot, Task<TopicSendHandoff>> durableHandoff,
        Func<TopicSendOutcome, Task>? reconcileOutcome = null,
        TopicSendDraftCleanup? draftCleanup = null)
        => SubmitCore(
            snapshot,
            (candidate, _) => durableHandoff(candidate),
            reconcileOutcome,
            draftCleanup,
            authorizeBeforeCallback: true);

    public TopicSendSubmissionResult Submit(
        TopicSendSnapshot snapshot,
        Func<TopicSendSnapshot, TopicSendHandoffContext, Task<TopicSendHandoff>> durableHandoff,
        Func<TopicSendOutcome, Task>? reconcileOutcome = null,
        TopicSendDraftCleanup? draftCleanup = null)
        => SubmitCore(
            snapshot,
            durableHandoff,
            reconcileOutcome,
            draftCleanup,
            authorizeBeforeCallback: false);

    private TopicSendSubmissionResult SubmitCore(
        TopicSendSnapshot snapshot,
        Func<TopicSendSnapshot, TopicSendHandoffContext, Task<TopicSendHandoff>> durableHandoff,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup,
        bool authorizeBeforeCallback)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(durableHandoff);
        using var mutation = TryAcquireMutationLease();
        if (mutation is null)
            return new(
                TopicSendSubmissionKind.PersistenceFailed,
                snapshot,
                Error: "The send coordinator is disposing.");
        var logicalKey = new LogicalSendKey(
            snapshot.AccountId,
            snapshot.ThreadId,
            snapshot.TargetDeviceId,
            snapshot.ComposerRevision);
        OperationState state;
        lock (identityGate)
        {
            PruneCompletedLocked();
            if (completedByOperation.TryGetValue(
                    snapshot.OperationId,
                    out var completed))
            {
                _ = NotifyOnceAsync(reconcileOutcome, completed.Outcome);
                return new(
                    TopicSendSubmissionKind.AlreadyCompleted,
                    completed.Snapshot,
                    completed.Outcome);
            }
            if (operations.ContainsKey(snapshot.OperationId))
            {
                var existing = operations[snapshot.OperationId];
                var terminal = existing.TerminalOutcome ?? existing.JournalTerminalOutcome;
                if (terminal is not null)
                    return new(
                        TopicSendSubmissionKind.AlreadyCompleted,
                        snapshot,
                        terminal);
                if (existing.Lifecycle == TopicSendJournalLifecycle.PreHandoff
                    && Volatile.Read(ref existing.Running) == 0)
                {
                    if (existing.FencedError is not null)
                        return new(
                            TopicSendSubmissionKind.IdentityConflict,
                            snapshot,
                            Error: existing.FencedError);
                    if (existing.RetryAuthorization is null)
                        return new(
                            TopicSendSubmissionKind.ReconciliationRequired,
                            snapshot,
                            Error: "The stable send identity must be checked against the durable trigger ledger.");
                    if (authorizationAuthority is null
                        || !authorizationAuthority.IsCurrent(
                            existing.RetryAuthorization.Scope))
                    {
                        lock (existing.Gate)
                            InvalidateAuthorizationLocked(
                                snapshot,
                                existing,
                                "submit_scope_mismatch");
                        return new(
                            TopicSendSubmissionKind.ReconciliationRequired,
                            snapshot,
                            Error: "The account database changed after retry authorization. A fresh authoritative lookup is required.");
                    }

                    existing.LastOutcome = null;
                    Volatile.Write(ref existing.Running, 1);
                    _ = ObserveAsync(
                        snapshot,
                        logicalKey,
                        existing,
                        durableHandoff,
                        reconcileOutcome,
                        draftCleanup,
                        retryAuthorizationRequired: true,
                        authorizeBeforeCallback: authorizeBeforeCallback,
                        lifecycleGeneration: mutation.Generation);
                    return new(TopicSendSubmissionKind.Started, snapshot);
                }
                if (existing.Lifecycle != TopicSendJournalLifecycle.PreHandoff
                    || Volatile.Read(ref existing.Running) == 0)
                {
                    return new(
                        TopicSendSubmissionKind.ReconciliationRequired,
                        snapshot,
                        Error: "The durable handoff is unresolved; reconciliation is running.");
                }
                return new(TopicSendSubmissionKind.AlreadyRunning, snapshot);
            }

            if (identityStore.TryGetUnresolved(
                    snapshot.ScopeIdentity,
                    out var persisted)
                && persisted is not null)
            {
                persisted = NormalizeRecord(persisted);
                if (persisted.Lifecycle == TopicSendJournalLifecycle.PreHandoff)
                {
                    if (!string.Equals(
                            persisted.OperationId,
                            snapshot.OperationId,
                            StringComparison.Ordinal))
                        return new(
                            TopicSendSubmissionKind.ReconciliationRequired,
                            snapshot,
                            Error: "A prior stable send identity must be reconciled before this revision can be submitted.");

                    var preHandoffState = StateFromRecord(persisted);
                    operations[snapshot.OperationId] = preHandoffState;
                    return new(
                        TopicSendSubmissionKind.ReconciliationRequired,
                        snapshot,
                        Error: "The stable send identity must be checked against the durable trigger ledger.");
                }

                if (!string.Equals(
                        persisted.OperationId,
                        snapshot.OperationId,
                        StringComparison.Ordinal))
                    return new(
                        TopicSendSubmissionKind.ReconciliationRequired,
                        snapshot,
                        persisted.ToOutcome(),
                        "A prior durable handoff for this topic must be reconciled before another send.");

                    var persistedOutcome = persisted.ToOutcome();
                    var recoveredState = StateFromRecord(persisted);
                    operations[snapshot.OperationId] = recoveredState;
                    if (persistedOutcome is not null
                        && IsDraftCleanupFinal(persisted.Cleanup))
                    {
                        recoveredState.TerminalOutcome = persistedOutcome;
                        CachePersistedLocked(logicalKey, snapshot, persistedOutcome);
                        _ = NotifyOnceAsync(reconcileOutcome, persistedOutcome);
                    }
                    return new(
                        persistedOutcome is null
                            ? TopicSendSubmissionKind.ReconciliationRequired
                            : TopicSendSubmissionKind.AlreadyCompleted,
                        snapshot,
                        persistedOutcome,
                        persistedOutcome is null
                            ? "This submission may have been handed off; its status is being reconciled."
                            : null);
            }

            if (snapshots.TryGetValue(logicalKey, out var canonical)
                && !string.Equals(
                    canonical.Snapshot.OperationId,
                    snapshot.OperationId,
                    StringComparison.Ordinal))
                return new(
                    TopicSendSubmissionKind.IdentityConflict,
                    canonical.Snapshot,
                    Error: "This draft revision already has a different immutable submission identity.");
            snapshots[logicalKey] = new SnapshotEntry(snapshot, ++sequence);

            if (operations.Values.Count(ConsumesCapacity)
                >= retention.MaximumRunningOperations)
                return new(
                    TopicSendSubmissionKind.CapacityExceeded,
                    snapshot,
                    Error: "Send capacity is temporarily full. Retry when another handoff finishes.");

            state = new OperationState();
            try
            {
                WriteJournal(
                    JournalRecord(
                        snapshot,
                        TopicSendJournalLifecycle.PreHandoff,
                        TopicSendJournalCleanup.None),
                    "pre-handoff");
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-identity-persist-failed",
                    $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
                return new(
                    TopicSendSubmissionKind.PersistenceFailed,
                    snapshot,
                    Error: "The send identity could not be saved. Retry after local storage is available.");
            }
            operations.Add(snapshot.OperationId, state);
        }

        _ = ObserveAsync(
            snapshot,
            logicalKey,
            state,
            durableHandoff,
            reconcileOutcome,
            draftCleanup,
            retryAuthorizationRequired: false,
            authorizeBeforeCallback: authorizeBeforeCallback,
            lifecycleGeneration: mutation.Generation);
        return new(TopicSendSubmissionKind.Started, snapshot);
    }

    private async Task ObserveAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        Func<TopicSendSnapshot, TopicSendHandoffContext, Task<TopicSendHandoff>> durableHandoff,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup,
        bool retryAuthorizationRequired,
        bool authorizeBeforeCallback,
        long lifecycleGeneration)
    {
        await Task.Yield();
        testObserver?.Checkpoint("observe-before-handoff-lease");
        using var handoffMutation = TryAcquireMutationLease(lifecycleGeneration);
        if (handoffMutation is null) return;
        var context = new TopicSendHandoffContext(
            () =>
            {
                WriteJournal(
                    JournalRecord(
                        snapshot,
                        TopicSendJournalLifecycle.AcceptedOrUnknown,
                        TopicSendJournalCleanup.None),
                    "accepted-or-unknown");
                lock (state.Gate)
                {
                    state.Lifecycle = TopicSendJournalLifecycle.AcceptedOrUnknown;
                    state.LastOutcome = ReconcilingOutcome(
                        "Durable handoff entered; awaiting an authoritative outcome.");
                }
            },
            retryAuthorizationRequired
                ? () => TryConsumeRetryAuthorization(snapshot, state, handoffMutation)
                : null,
            retryAuthorizationRequired && !authorizeBeforeCallback
                ? command => AuthorizeAndBeginTopicRun(
                    snapshot, state, command, handoffMutation)
                : null);
        TopicSendHandoff? handoff = null;
        try
        {
            handoff = await Task.Run(async () =>
            {
                using var trace = ManagedOperationDiagnostics.Begin("ui.topic.send");
                if (authorizeBeforeCallback)
                    context.AuthorizeDurableHandoff();
                return await durableHandoff(snapshot, context).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (!context.AuthorizationEntered)
                throw new TopicSendAuthorizationException(
                    "The retry authorization was not consumed before durable handoff.");
        }
        catch (TopicSendJournalCrashException)
        {
            Volatile.Write(ref state.Running, 0);
            return;
        }
        catch (TopicSendJournalStaleException)
        {
            return;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-failed",
                $"operation={snapshot.OperationId};boundary={context.DurableBoundaryEntered};exception={exception.GetType().FullName}");
            if (!context.DurableBoundaryEntered)
            {
                handoffMutation.Dispose();
                await FinalizePreHandoffFailureAsync(
                        snapshot,
                        logicalKey,
                        state,
                        reconcileOutcome,
                        draftCleanup,
                        exception)
                    .ConfigureAwait(false);
                return;
            }

            Volatile.Write(ref state.Running, 0);
            handoffMutation.Dispose();
            await PublishProgressAsync(
                    state,
                    ReconcilingOutcome(
                        "Durable handoff outcome is unknown; checking authoritative local state."))
                .ConfigureAwait(false);
            await ReconcileAsync(
                    snapshot,
                    logicalKey,
                    state,
                    reconcileOutcome,
                    draftCleanup,
                    lifecycleGeneration: handoffMutation.Generation)
                .ConfigureAwait(false);
            return;
        }

        var outcome = new TopicSendOutcome(
            handoff.Accepted
                ? TopicSendOutcomeKind.Accepted
                : TopicSendOutcomeKind.Rejected,
            handoff,
            RequiresDraftClear: handoff.Accepted);
        await FinalizeAsync(
                snapshot,
                logicalKey,
                state,
                outcome,
                reconcileOutcome,
                draftCleanup,
                cacheCompleted: handoff.Accepted,
                inheritedMutation: handoffMutation)
            .ConfigureAwait(false);
    }

    public bool TryGetOutcome(string operationId, out TopicSendOutcome? outcome)
    {
        lock (identityGate)
        {
            if (operations.TryGetValue(operationId, out var operation))
            {
                lock (operation.Gate)
                    outcome = operation.TerminalOutcome ?? operation.ObservableOutcome;
                return outcome is not null;
            }
            if (completedByOperation.TryGetValue(operationId, out var completed))
            {
                outcome = completed.Outcome;
                return true;
            }
            if (retryableOutcomes.TryGetValue(operationId, out var retryable))
            {
                if (retryable.ExpiresAt > timeProvider.GetUtcNow())
                {
                    outcome = retryable.Outcome;
                    return true;
                }
                retryableOutcomes.Remove(operationId);
            }
        }
        outcome = null;
        return false;
    }

    public Task RequestReconciliationAsync(
        TopicSendSnapshot snapshot,
        Func<TopicSendOutcome, Task>? reconcileOutcome = null,
        TopicSendDraftCleanup? draftCleanup = null,
        CancellationToken cancellationToken = default)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return Task.CompletedTask;
        OperationState? state;
        LogicalSendKey key;
        var requestedGeneration = Interlocked.Increment(ref availabilityGeneration);
        lock (identityGate)
        {
            key = new LogicalSendKey(
                snapshot.AccountId,
                snapshot.ThreadId,
                snapshot.TargetDeviceId,
                snapshot.ComposerRevision);
            if (!operations.TryGetValue(snapshot.OperationId, out state))
                return Task.CompletedTask;
            if (state.Lifecycle == TopicSendJournalLifecycle.Terminal)
            {
                if (IsDraftCleanupFinal(state.Cleanup)
                    || Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
                    return Task.CompletedTask;
                return CompleteTerminalRecoveryAsync(
                    snapshot, key, state, reconcileOutcome, draftCleanup);
            }
            if (state.Lifecycle == TopicSendJournalLifecycle.PreHandoff)
            {
                if (draftCleanup is not null)
                    state.RecoveryCleanup = draftCleanup;
                lock (state.Gate)
                    state.PendingAvailabilityGeneration = Math.Max(
                        state.PendingAvailabilityGeneration,
                        requestedGeneration);
                if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
                {
                    lock (state.Gate)
                        return state.ReconciliationCompletion?.Task ?? Task.CompletedTask;
                }
                return RecoverPreHandoffAsync(
                    snapshot, key, state, reconcileOutcome, draftCleanup, cancellationToken);
            }
            if (state.Lifecycle != TopicSendJournalLifecycle.AcceptedOrUnknown
                || Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
                return Task.CompletedTask;
        }
        return ReconcileAsync(
            snapshot,
            key,
            state,
            reconcileOutcome,
            draftCleanup,
            cancellationToken,
            mutation.Generation);
    }

    public bool RequiresRecovery(string operationId)
    {
        lock (identityGate)
            return operations.TryGetValue(operationId, out var state)
                   && (state.Lifecycle == TopicSendJournalLifecycle.PreHandoff
                       || state.Lifecycle == TopicSendJournalLifecycle.AcceptedOrUnknown
                       || state.Lifecycle == TopicSendJournalLifecycle.Terminal);
    }

    public bool TryAbandonPreHandoff(string operationId, long supersedingComposerRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return false;
        OperationState? state;
        TopicSendSnapshot? snapshot;
        lock (identityGate)
        {
            if (!operations.TryGetValue(operationId, out state)
                || state.Lifecycle != TopicSendJournalLifecycle.PreHandoff
                || Volatile.Read(ref state.Running) != 0
                || state.RetryAuthorization is null)
                return false;
            snapshot = snapshots.Values
                .Select(entry => entry.Snapshot)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
            if (snapshot is null
                || supersedingComposerRevision <= snapshot.ComposerRevision)
                return false;
        }

        if (!TryCompactJournal(
                JournalRecord(
                    snapshot,
                    TopicSendJournalLifecycle.PreHandoff,
                    TopicSendJournalCleanup.None),
                "safe-draft-supersession"))
            return false;

        lock (identityGate)
        {
            operations.Remove(operationId);
            snapshots.Remove(new LogicalSendKey(
                snapshot.AccountId,
                snapshot.ThreadId,
                snapshot.TargetDeviceId,
                snapshot.ComposerRevision));
        }
        RecordPreHandoffDiagnostic(snapshot, "notfound", "safe_supersession");
        return true;
    }

    private async Task RecoverPreHandoffAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var linkedCancellation = cancellationToken.CanBeCanceled
            && cancellationToken != lifetime.Token
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.Token)
                : null;
        var workCancellation = linkedCancellation?.Token ?? lifetime.Token;
        var reconciliationGeneration = Volatile.Read(ref lifetimeGeneration);
        if (!IsReconciliationCurrent(reconciliationGeneration))
            return;
        if (reconciliationQuery is null)
        {
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                Volatile.Write(ref state.Running, 0);
            }
            await PublishProgressAsync(
                    state,
                    ReconcilingOutcome(
                        "The stable send identity cannot be checked because the authoritative trigger ledger is unavailable."))
                .ConfigureAwait(false);
            return;
        }

        long handledGeneration;
        using (var mutation = TryAcquireMutationLease())
        {
            if (mutation is null) return;
            lock (state.Gate)
            {
            handledGeneration = state.PendingAvailabilityGeneration;
            state.ProcessedAvailabilityGeneration = Math.Max(
                state.ProcessedAvailabilityGeneration,
                handledGeneration);
            }
        }

        TopicSendReconciliationResult result;
    queryAgain:
        if (!IsReconciliationCurrent(reconciliationGeneration))
            return;
        var delay = retention.ReconciliationInitialBackoff;
        for (var attempt = 1;; attempt++)
        {
            testObserver?.Checkpoint("prehandoff-before-query-lease");
            using var queryMutation = TryAcquireMutationLease(reconciliationGeneration);
            if (queryMutation is null) return;
            try
            {
                result = await reconciliationQuery.QueryAsync(snapshot, workCancellation)
                    .ConfigureAwait(false);
                testObserver?.Checkpoint("prehandoff-query-returned");
                if (!IsReconciliationCurrent(reconciliationGeneration))
                    return;
            }
            catch (OperationCanceledException) when (workCancellation.IsCancellationRequested)
            {
                if (!TryContinuePendingReconciliation(state, ref handledGeneration))
                    return;
                workCancellation = lifetime.Token;
                attempt = 0;
                delay = retention.ReconciliationInitialBackoff;
                continue;
            }
            catch (Exception exception)
            {
                if (!IsReconciliationCurrent(reconciliationGeneration))
                    return;
                result = new(
                    TopicSendReconciliationKind.QueryFailed,
                    "The authoritative trigger ledger query failed.",
                    DiagnosticReason: exception.GetType().Name,
                    AccountId: snapshot.AccountId);
            }

            if (result.Kind is not TopicSendReconciliationKind.Unavailable
                and not TopicSendReconciliationKind.QueryFailed)
            {
                if (TryTakePendingReconciliation(state, ref handledGeneration))
                {
                    attempt = 0;
                    delay = retention.ReconciliationInitialBackoff;
                    continue;
                }
                break;
            }

            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                state.AvailabilityAttempt = attempt;
            }
            RecordPreHandoffDiagnostic(
                snapshot,
                result.Kind == TopicSendReconciliationKind.Unavailable
                    ? "unavailable"
                    : "queryfailed",
                "retry_status",
                result.DiagnosticReason,
                result.AccountId,
                attempt);
            if (attempt >= retention.MaximumReconciliationAttempts)
            {
                if (TryContinuePendingReconciliation(state, ref handledGeneration))
                {
                    attempt = 0;
                    delay = retention.ReconciliationInitialBackoff;
                    continue;
                }
                queryMutation.Dispose();
                await PublishProgressAsync(
                        state,
                        ReconcilingOutcome(
                            result.Detail
                            ?? "The authoritative trigger ledger is unavailable. No duplicate was submitted; retry status when the account database is available."))
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                await Task.Delay(delay, workCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workCancellation.IsCancellationRequested)
            {
                if (!TryContinuePendingReconciliation(state, ref handledGeneration))
                    return;
                workCancellation = lifetime.Token;
                attempt = 0;
                delay = retention.ReconciliationInitialBackoff;
                continue;
            }
            delay = TimeSpan.FromTicks(Math.Min(
                retention.ReconciliationMaximumBackoff.Ticks,
                Math.Max(delay.Ticks + 1, delay.Ticks * 2)));
        }

        var queryResult = result.Kind switch
        {
            TopicSendReconciliationKind.Accepted
                or TopicSendReconciliationKind.Completed
                or TopicSendReconciliationKind.Failed
                or TopicSendReconciliationKind.Cancelled
                or TopicSendReconciliationKind.Interrupted =>
                "found",
            TopicSendReconciliationKind.NotFound => "notfound",
            TopicSendReconciliationKind.Conflict => "conflict",
            TopicSendReconciliationKind.Corrupt => "corrupt",
            TopicSendReconciliationKind.Unavailable => "unavailable",
            TopicSendReconciliationKind.QueryFailed => "queryfailed",
            _ => "unknown"
        };
        RecordPreHandoffDiagnostic(snapshot, queryResult, "query");

        if (result.Kind == TopicSendReconciliationKind.NotFound)
        {
            if (!IsReconciliationCurrent(reconciliationGeneration))
                return;
            if (TryTakePendingReconciliation(state, ref handledGeneration))
                goto queryAgain;
            string? pendingFailure;
            TopicSendRetryAuthorization? authorization;
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                if (!IsReconciliationCurrent(reconciliationGeneration))
                    return;
                if (!TryCreateRetryAuthorization(snapshot, result, out authorization)
                    || authorization is null)
                    authorization = null;
            }
            if (authorization is null)
            {
                const string detail =
                    "The authoritative NotFound observation did not match the active account database. The send remains fenced.";
                using (var mutation = TryAcquireMutationLease())
                {
                    if (mutation is null) return;
                    lock (state.Gate)
                    {
                        InvalidateAuthorizationLocked(snapshot, state, "notfound_scope_mismatch");
                        state.LastOutcome = ReconcilingOutcome(detail);
                        Volatile.Write(ref state.Running, 0);
                    }
                }
                await PublishProgressAsync(state, ReconcilingOutcome(detail)).ConfigureAwait(false);
                RecordPreHandoffDiagnostic(
                    snapshot,
                    "notfound",
                    "fenced_scope_mismatch",
                    result.DiagnosticReason,
                    result.AccountId);
                return;
            }
            var requery = false;
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null)
                {
                    InvalidateRetryAuthorization(authorization);
                    return;
                }
                lock (state.Gate)
                {
                if (!IsReconciliationCurrent(reconciliationGeneration))
                    return;
                var latestAvailability = Volatile.Read(ref availabilityGeneration);
                if (state.PendingAvailabilityGeneration > handledGeneration
                    || latestAvailability > handledGeneration)
                {
                    handledGeneration = Math.Max(
                        state.PendingAvailabilityGeneration,
                        latestAvailability);
                    state.ProcessedAvailabilityGeneration = handledGeneration;
                    requery = true;
                    pendingFailure = null;
                }
                else
                {
                    state.RetryAuthorization = authorization;
                    state.ConsumedAuthorizationScope = null;
                    pendingFailure = state.PendingFailure;
                    state.PendingFailure = null;
                    Volatile.Write(ref state.Running, 0);
                }
                }
            }
            if (requery)
            {
                InvalidateRetryAuthorization(authorization);
                goto queryAgain;
            }
            RecordAuthorizationDiagnostic(snapshot, authorization, "issued");
            if (!IsReconciliationCurrent(reconciliationGeneration))
            {
                InvalidateRetryAuthorization(authorization);
                return;
            }
            var retryable = new TopicSendOutcome(
                TopicSendOutcomeKind.RetryableFailed,
                Exception: new InvalidOperationException(
                    pendingFailure
                    ?? result.Detail
                    ?? "No durable trigger exists. Retry will reuse the same stable send identity."));
            await NotifyOnceAsync(reconcileOutcome, retryable).ConfigureAwait(false);
            await PublishProgressAsync(state, retryable).ConfigureAwait(false);
            RecordPreHandoffDiagnostic(snapshot, "notfound", "retry_same_identity");
            return;
        }

        if (result.Kind is TopicSendReconciliationKind.Conflict
            or TopicSendReconciliationKind.Corrupt)
        {
            var detail = result.Detail
                         ?? "The authoritative trigger ledger conflicts with the stable send identity.";
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                Volatile.Write(ref state.Running, 0);
                lock (state.Gate)
                    state.FencedError = detail;
            }
            await PublishProgressAsync(
                    state,
                    new TopicSendOutcome(
                        TopicSendOutcomeKind.Failed,
                        Exception: new InvalidOperationException(detail)))
                .ConfigureAwait(false);
            RecordPreHandoffDiagnostic(snapshot, queryResult, "fenced");
            return;
        }

        if (result.Kind is not TopicSendReconciliationKind.Accepted
            and not TopicSendReconciliationKind.Completed)
        {
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                Volatile.Write(ref state.Running, 0);
            }
            await PublishProgressAsync(
                    state,
                    ReconcilingOutcome(
                        result.Detail
                        ?? "The durable trigger status is still unknown. No duplicate was submitted."))
                .ConfigureAwait(false);
            return;
        }

        var authoritativeRunId = result.AuthoritativeRunId ?? snapshot.RunId;
        if (result.AuthoritativeLineId is not null
            && !string.Equals(
                result.AuthoritativeLineId,
                snapshot.LineId,
                StringComparison.Ordinal)
            || result.AuthoritativeOutboxId is not null
            && !string.Equals(
                result.AuthoritativeOutboxId,
                authoritativeRunId,
                StringComparison.Ordinal))
        {
            const string detail = "The authoritative trigger ledger returned conflicting run artifacts.";
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                Volatile.Write(ref state.Running, 0);
                lock (state.Gate)
                    state.FencedError = detail;
            }
            await PublishProgressAsync(
                    state,
                    new TopicSendOutcome(
                        TopicSendOutcomeKind.Failed,
                        Exception: new InvalidOperationException(detail)))
                .ConfigureAwait(false);
            RecordPreHandoffDiagnostic(snapshot, "conflict", "fenced");
            return;
        }
        var authoritativeSnapshot = string.Equals(
            authoritativeRunId,
            snapshot.RunId,
            StringComparison.Ordinal)
                ? snapshot
                : snapshot with { RunId = authoritativeRunId };

        using var durableFoundMutation = TryAcquireMutationLease();
        if (durableFoundMutation is null) return;
        try
        {
            WriteJournal(
                JournalRecord(
                    authoritativeSnapshot,
                    TopicSendJournalLifecycle.AcceptedOrUnknown,
                    TopicSendJournalCleanup.None),
                "prehandoff-ledger-found");
        }
        catch (TopicSendJournalCrashException)
        {
            Volatile.Write(ref state.Running, 0);
            return;
        }
        catch (TopicSendJournalStaleException)
        {
            return;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref state.Running, 0);
            durableFoundMutation.Dispose();
            await PublishProgressAsync(
                    state,
                    ReconcilingOutcome(
                        $"The durable trigger was found, but recovery state could not be saved: {exception.Message}"))
                .ConfigureAwait(false);
            return;
        }

        lock (identityGate)
        {
            snapshots[logicalKey] = new SnapshotEntry(
                authoritativeSnapshot,
                ++sequence);
        }
        lock (state.Gate)
        {
            state.Lifecycle = TopicSendJournalLifecycle.AcceptedOrUnknown;
            InvalidateAuthorizationLocked(authoritativeSnapshot, state, "durable_found");
            state.LastOutcome = ReconcilingOutcome(
                "The durable trigger was recovered; completing authoritative reconciliation.");
        }
        RecordPreHandoffDiagnostic(
            authoritativeSnapshot,
            "found",
            "durable_committed");
        durableFoundMutation.Dispose();
        await FinalizeAsync(
                authoritativeSnapshot,
                logicalKey,
                state,
                new TopicSendOutcome(
                    TopicSendOutcomeKind.Accepted,
                    new TopicSendHandoff(
                        true,
                        result.Kind == TopicSendReconciliationKind.Completed
                            ? "recovered_completed"
                            : "recovered_committed"),
                    RequiresDraftClear: true,
                    AuthoritativeRunId: authoritativeRunId,
                    AuthoritativeLineId:
                        result.AuthoritativeLineId ?? authoritativeSnapshot.LineId,
                    AuthoritativeOutboxId: result.AuthoritativeOutboxId),
                reconcileOutcome,
                draftCleanup,
                cacheCompleted: true)
            .ConfigureAwait(false);
    }

    private void OnReconciliationAvailabilityChanged()
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return;
        var generation = Interlocked.Increment(ref availabilityGeneration);
        List<(
            TopicSendSnapshot Snapshot,
            OperationState State,
            TopicSendDraftCleanup? Cleanup,
            TaskCompletionSource Completion)> pending = [];
        List<(TopicSendSnapshot Snapshot, OperationState State, TopicSendRetryAuthorization Authorization)>
            authorizationChecks = [];
        lock (identityGate)
        {
            foreach (var entry in snapshots.Values)
            {
                if (!operations.TryGetValue(entry.Snapshot.OperationId, out var state))
                    continue;
                lock (state.Gate)
                {
                    if (state.Lifecycle != TopicSendJournalLifecycle.PreHandoff
                        || state.FencedError is not null
                        || Volatile.Read(ref state.Running) == 0
                           && state.AvailabilityAttempt == 0
                           && state.RetryAuthorization is null
                           && state.ConsumedAuthorizationScope is null)
                        continue;
                    state.PendingAvailabilityGeneration = Math.Max(
                        state.PendingAvailabilityGeneration,
                        generation);
                    if (state.RetryAuthorization is not null
                        && authorizationAuthority is not null)
                        authorizationChecks.Add(
                            (entry.Snapshot, state, state.RetryAuthorization));
                    if (Interlocked.CompareExchange(ref state.Running, 1, 0) == 0)
                    {
                        var completion = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        state.ReconciliationCompletion = completion;
                        pending.Add((
                            entry.Snapshot,
                            state,
                            state.RecoveryCleanup,
                            completion));
                    }
                }
            }
        }

        // Lock order invariant: coordinator state is snapshotted first, then released
        // before entering AppState's profile gate. Reacquisition only compares identity.
        foreach (var check in authorizationChecks)
        {
            var current = authorizationAuthority!.IsCurrent(check.Authorization.Scope);
            if (current) continue;
            lock (check.State.Gate)
            {
                if (ReferenceEquals(check.State.RetryAuthorization, check.Authorization))
                    InvalidateAuthorizationLocked(
                        check.Snapshot,
                        check.State,
                        "availability_scope_changed");
            }
        }

        foreach (var item in pending)
            _ = CompleteTrackedReconciliationAsync(
                item.State,
                item.Completion,
                RecoverPreHandoffAsync(
                    item.Snapshot,
                    new LogicalSendKey(
                        item.Snapshot.AccountId,
                        item.Snapshot.ThreadId,
                        item.Snapshot.TargetDeviceId,
                        item.Snapshot.ComposerRevision),
                    item.State,
                    reconcileOutcome: null,
                    draftCleanup: item.Cleanup,
                    cancellationToken: lifetime.Token));
    }

    private static async Task CompleteTrackedReconciliationAsync(
        OperationState state,
        TaskCompletionSource completion,
        Task reconciliation)
    {
        try
        {
            await reconciliation.ConfigureAwait(false);
        }
        finally
        {
            lock (state.Gate)
            {
                if (ReferenceEquals(state.ReconciliationCompletion, completion))
                    state.ReconciliationCompletion = null;
            }
            completion.TrySetResult();
        }
    }

    private async Task ReconcileAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup,
        CancellationToken cancellationToken = default,
        long lifecycleGeneration = 0)
    {
        await Task.Yield();
        if (lifecycleGeneration == 0)
            lifecycleGeneration = Volatile.Read(ref lifetimeGeneration);
        using var linkedCancellation = cancellationToken.CanBeCanceled
            && cancellationToken != lifetime.Token
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.Token)
                : null;
        var workCancellation = linkedCancellation?.Token ?? lifetime.Token;
        if (reconciliationQuery is null)
        {
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return;
                Volatile.Write(ref state.Running, 0);
            }
            await PublishProgressAsync(
                    state,
                    ReconcilingOutcome(
                        "Authoritative status is unavailable. Use Check status when it becomes available."))
                .ConfigureAwait(false);
            return;
        }

        var delay = retention.ReconciliationInitialBackoff;
        for (var attempt = 0; attempt < retention.MaximumReconciliationAttempts; attempt++)
        {
            if (workCancellation.IsCancellationRequested)
            {
                await PauseReconciliationAsync(state).ConfigureAwait(false);
                return;
            }
            testObserver?.Checkpoint("reconcile-before-query-lease");
            using var queryMutation = TryAcquireMutationLease(lifecycleGeneration);
            if (queryMutation is null) return;
            TopicSendReconciliationResult result;
            try
            {
                result = await reconciliationQuery.QueryAsync(
                        snapshot, workCancellation)
                    .ConfigureAwait(false);
                testObserver?.Checkpoint("reconcile-query-returned");
            }
            catch (OperationCanceledException) when (workCancellation.IsCancellationRequested)
            {
                queryMutation.Dispose();
                await PauseReconciliationAsync(state).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-reconciliation-query-failed",
                    $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
                result = new(TopicSendReconciliationKind.Unknown, exception.Message);
            }

            switch (result.Kind)
            {
                case TopicSendReconciliationKind.Accepted:
                case TopicSendReconciliationKind.Completed:
                    await FinalizeAsync(
                            snapshot,
                            logicalKey,
                            state,
                            new TopicSendOutcome(
                                TopicSendOutcomeKind.Accepted,
                                new TopicSendHandoff(
                                    true,
                                    result.Kind == TopicSendReconciliationKind.Completed
                                        ? "reconciled_completed"
                                        : "reconciled_accepted"),
                                RequiresDraftClear: true),
                            reconcileOutcome,
                            draftCleanup,
                            cacheCompleted: true,
                            inheritedMutation: queryMutation)
                        .ConfigureAwait(false);
                    return;
                case TopicSendReconciliationKind.NotFound:
                    await FinalizeAsync(
                            snapshot,
                            logicalKey,
                            state,
                            new TopicSendOutcome(
                                TopicSendOutcomeKind.RetryableFailed,
                                Exception: new InvalidOperationException(
                                    result.Detail
                                    ?? "No durable handoff was found. The unchanged draft can be retried."),
                                RequiresDraftClear: true),
                            reconcileOutcome,
                            draftCleanup,
                            cacheCompleted: false,
                            inheritedMutation: queryMutation)
                        .ConfigureAwait(false);
                    return;
                case TopicSendReconciliationKind.Failed:
                case TopicSendReconciliationKind.Cancelled:
                case TopicSendReconciliationKind.Interrupted:
                    await FinalizeAsync(
                            snapshot,
                            logicalKey,
                            state,
                            new TopicSendOutcome(
                                TopicSendOutcomeKind.Failed,
                                Exception: new InvalidOperationException(
                                    result.Detail
                                    ?? result.Kind switch
                                    {
                                        TopicSendReconciliationKind.Cancelled =>
                                            "The durable run was cancelled.",
                                        TopicSendReconciliationKind.Interrupted =>
                                            "The durable run was interrupted.",
                                        _ => "The durable run failed."
                                    }),
                                RequiresDraftClear: true),
                            reconcileOutcome,
                            draftCleanup,
                            cacheCompleted: true,
                            inheritedMutation: queryMutation)
                        .ConfigureAwait(false);
                    return;
                case TopicSendReconciliationKind.Conflict:
                case TopicSendReconciliationKind.Corrupt:
                    var detail = result.Detail
                                 ?? "The authoritative trigger ledger conflicts with this send identity.";
                    Volatile.Write(ref state.Running, 0);
                    lock (state.Gate)
                        state.FencedError = detail;
                    queryMutation.Dispose();
                    await PublishProgressAsync(
                            state,
                            new TopicSendOutcome(
                                TopicSendOutcomeKind.Failed,
                                Exception: new InvalidOperationException(detail)))
                        .ConfigureAwait(false);
                    RuntimeDiagnostics.Current?.RecordEvent(
                        "topic-send-reconciliation-fenced",
                        $"operation={DiagnosticId(snapshot.OperationId)};"
                        + $"result={result.Kind.ToString().ToLowerInvariant()}");
                    return;
                case TopicSendReconciliationKind.Unknown:
                case TopicSendReconciliationKind.Unavailable:
                case TopicSendReconciliationKind.QueryFailed:
                    break;
            }

            queryMutation.Dispose();
            if (attempt + 1 < retention.MaximumReconciliationAttempts && delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, workCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (workCancellation.IsCancellationRequested)
                {
                    await PauseReconciliationAsync(state).ConfigureAwait(false);
                    return;
                }
                delay = TimeSpan.FromTicks(Math.Min(
                    retention.ReconciliationMaximumBackoff.Ticks,
                    Math.Max(delay.Ticks + 1, delay.Ticks * 2)));
            }
        }

        using (var mutation = TryAcquireMutationLease())
        {
            if (mutation is null) return;
            Volatile.Write(ref state.Running, 0);
        }
        await PublishProgressAsync(
                state,
                ReconcilingOutcome(
                    "Status is still unknown. No duplicate was submitted; use Check status to query again."))
            .ConfigureAwait(false);
    }

    private async Task PauseReconciliationAsync(OperationState state)
    {
        using (var mutation = TryAcquireMutationLease())
        {
            if (mutation is null) return;
            Volatile.Write(ref state.Running, 0);
        }
        await PublishProgressAsync(
                state,
                ReconcilingOutcome(
                    "Status check paused. No duplicate was submitted; use Check status to resume."))
            .ConfigureAwait(false);
    }

    private async Task FinalizeAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        TopicSendOutcome outcome,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup,
        bool cacheCompleted = true,
        MutationLease? inheritedMutation = null)
    {
        var requiresDraftCleanup = outcome.RequiresDraftClear;
        var terminal = JournalRecord(
            snapshot,
            TopicSendJournalLifecycle.Terminal,
            requiresDraftCleanup
                ? TopicSendJournalCleanup.DraftClearPending
                : TopicSendJournalCleanup.DraftClearPersisted,
            outcome);
        using var terminalMutation = inheritedMutation is not null
            ? null
            : TryAcquireMutationLease();
        if (inheritedMutation is null && terminalMutation is null) return;
        try
        {
            testObserver?.Checkpoint("terminal-before-commit");
            lock (identityGate)
            {
                WriteJournal(terminal, "terminal");
                lock (state.Gate)
                {
                    state.Lifecycle = TopicSendJournalLifecycle.Terminal;
                    state.Cleanup = terminal.Cleanup;
                    state.JournalTerminalOutcome = outcome;
                    state.LastOutcome = terminal.Cleanup == TopicSendJournalCleanup.DraftClearPending
                        ? ReconcilingOutcome("The send is final; completing local draft cleanup.")
                        : outcome;
                }
            }
            testObserver?.Checkpoint("terminal-committed-capacity-released");
        }
        catch (TopicSendJournalStaleException)
        {
            Volatile.Write(ref state.Running, 0);
            return;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-outcome-persist-failed",
                $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
            lock (state.Gate)
            {
                state.Lifecycle = TopicSendJournalLifecycle.AcceptedOrUnknown;
                state.Cleanup = TopicSendJournalCleanup.None;
                state.LastOutcome = ReconcilingOutcome(
                    "The terminal outcome could not be journaled; authoritative reconciliation is required.");
            }
            Volatile.Write(ref state.Running, 0);
            return;
        }

        terminalMutation?.Dispose();
        inheritedMutation?.Dispose();

        if (terminal.Cleanup == TopicSendJournalCleanup.DraftClearPending)
        {
            var cleanup = await ResolveDraftCleanupAsync(
                    snapshot, state, outcome, reconcileOutcome, draftCleanup)
                .ConfigureAwait(false);
            if (cleanup is null)
                return;

            terminal = AdvanceRecord(terminal with { Cleanup = cleanup.Value });
            using var cleanupMutation = TryAcquireMutationLease();
            if (cleanupMutation is null)
            {
                Volatile.Write(ref state.Running, 0);
                return;
            }
            try
            {
                WriteJournal(
                    terminal,
                    cleanup == TopicSendJournalCleanup.DraftClearPersisted
                        ? "draft-clear-persisted"
                        : "draft-clear-superseded");
            }
            catch (TopicSendJournalStaleException)
            {
                Volatile.Write(ref state.Running, 0);
                return;
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-cleanup-persist-failed",
                    $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
                Volatile.Write(ref state.Running, 0);
                return;
            }
            lock (state.Gate)
                state.Cleanup = cleanup.Value;
        }
        var completed = await CompleteFinalizationAsync(
                snapshot,
                logicalKey,
                state,
                outcome,
                terminal.Cleanup,
                cacheCompleted,
                !requiresDraftCleanup ? reconcileOutcome : null)
            .ConfigureAwait(false);
        if (!completed)
            Volatile.Write(ref state.Running, 0);
    }

    private async Task CompleteTerminalRecoveryAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup)
    {
        await Task.Yield();
        var outcome = state.JournalTerminalOutcome;
        if (outcome is null)
        {
            using var mutation = TryAcquireMutationLease();
            if (mutation is not null)
                Volatile.Write(ref state.Running, 0);
            return;
        }
        var cleanup = await ResolveDraftCleanupAsync(
                snapshot, state, outcome, reconcileOutcome, draftCleanup)
            .ConfigureAwait(false);
        if (cleanup is null)
        {
            using var mutation = TryAcquireMutationLease();
            if (mutation is not null)
                Volatile.Write(ref state.Running, 0);
            return;
        }

        var completedRecord = JournalRecord(
            snapshot,
            TopicSendJournalLifecycle.Terminal,
            cleanup.Value,
            outcome);
        using var completionMutation = TryAcquireMutationLease();
        if (completionMutation is null) return;
        try
        {
            WriteJournal(
                completedRecord,
                cleanup == TopicSendJournalCleanup.DraftClearPersisted
                    ? "draft-clear-persisted"
                    : "draft-clear-superseded");
            lock (state.Gate)
                state.Cleanup = cleanup.Value;
            completionMutation.Dispose();
            await CompleteFinalizationAsync(
                    snapshot,
                    logicalKey,
                    state,
                    outcome,
                    cleanup.Value,
                    cacheCompleted: outcome.Kind != TopicSendOutcomeKind.RetryableFailed)
                .ConfigureAwait(false);
        }
        catch (TopicSendJournalStaleException)
        {
            return;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-recovery-cleanup-failed",
                $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
            using var mutation = TryAcquireMutationLease();
            if (mutation is not null)
                Volatile.Write(ref state.Running, 0);
        }
    }

    private async Task<TopicSendJournalCleanup?> ResolveDraftCleanupAsync(
        TopicSendSnapshot snapshot,
        OperationState state,
        TopicSendOutcome outcome,
        Func<TopicSendOutcome, Task>? legacyCleanup,
        TopicSendDraftCleanup? draftCleanup)
    {
        try
        {
            TopicSendJournalCleanup cleanup;
            if (draftCleanup is not null)
            {
                using var callbackLease = TryAcquireMutationLease();
                if (callbackLease is null) return null;
                testObserver?.CallbackQueued(
                    snapshot.OperationId,
                    Volatile.Read(ref disposalCompleted) != 0);
                var persistence = draftCleanup.PersistAsync(outcome);
                cleanup = (await persistence.ConfigureAwait(false)) switch
                {
                    TopicSendDraftCleanupResult.DraftClearPersisted =>
                        TopicSendJournalCleanup.DraftClearPersisted,
                    TopicSendDraftCleanupResult.DraftClearSuperseded =>
                        TopicSendJournalCleanup.DraftClearSuperseded,
                    _ => throw new InvalidOperationException("Unknown topic draft cleanup result.")
                };
                if (legacyCleanup is not null)
                {
                    if (!await TryNotifyOnceAsync(legacyCleanup, outcome).ConfigureAwait(false))
                        throw new InvalidOperationException("The legacy draft cleanup callback failed.");
                }
            }
            else
            {
                if (legacyCleanup is null)
                    throw new InvalidOperationException(
                        "A durable topic draft cleanup acknowledgement is required.");
                if (!await TryNotifyOnceAsync(legacyCleanup, outcome).ConfigureAwait(false))
                    throw new InvalidOperationException("The legacy draft cleanup callback failed.");
                cleanup = TopicSendJournalCleanup.DraftClearPersisted;
            }
            return cleanup;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-draft-clear-failed",
                $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
            using (var mutation = TryAcquireMutationLease())
            {
                if (mutation is null) return null;
                Volatile.Write(ref state.Running, 0);
            }
            await PublishProgressAsync(
                    state,
                    ReconcilingOutcome(
                        $"The send is final, but its local draft cleanup failed: {exception.Message}"))
                .ConfigureAwait(false);
            return null;
        }
    }

    private async Task<bool> CompleteFinalizationAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        TopicSendOutcome outcome,
        TopicSendJournalCleanup cleanup,
        bool cacheCompleted,
        Func<TopicSendOutcome, Task>? finalCallback = null)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return false;
        TopicSendIdentityRecord compacted;
        try
        {
            compacted = CompactJournal(
                JournalRecord(
                    snapshot,
                    TopicSendJournalLifecycle.Terminal,
                    cleanup,
                    outcome),
                "final-compaction");
        }
        catch (TopicSendJournalStaleException)
        {
            return false;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-identity-cleanup-failed",
                $"operation={snapshot.OperationId};exception={exception.GetType().FullName}");
            Volatile.Write(ref state.Running, 0);
            return false;
        }

        List<ObserverSubscription> observers;
        List<TaskCompletionSource> detachBarriers;
        var cached = false;
        lock (identityGate)
        {
            lock (state.Gate)
            {
                state.LastOutcome = outcome;
                state.TerminalOutcome = outcome;
                observers = state.Observers.Values.ToList();
                state.Observers.Clear();
                detachBarriers = state.ObserverDetachBarriers.Values.ToList();
                state.ObserverDetachBarriers.Clear();
            }
            operations.Remove(snapshot.OperationId);
            snapshots.Remove(logicalKey);
            if (cacheCompleted)
            {
                var completed = new CompletedIdentity(
                    logicalKey,
                    snapshot,
                    outcome,
                    timeProvider.GetUtcNow() + retention.CompletedIdentityRetention,
                    snapshot.SubmissionSequence);
                cached = TryCacheCompletedLocked(completed);
            }
        }
        var dispatches = observers
            .Select(observer => QueueSubscription(observer, outcome, detachAfter: true))
            .ToArray();
        foreach (var barrier in detachBarriers)
            barrier.TrySetResult();
        mutation.Dispose();
        await Task.WhenAll(dispatches).ConfigureAwait(false);
        if (finalCallback is not null)
            await NotifyOnceAsync(finalCallback, outcome).ConfigureAwait(false);
        if (!cacheCompleted)
        {
            lock (identityGate)
            {
                if (!operations.ContainsKey(snapshot.OperationId)
                    && !completedByOperation.ContainsKey(snapshot.OperationId))
                    retryableOutcomes[snapshot.OperationId] = (
                        outcome,
                        timeProvider.GetUtcNow() + retention.CompletedIdentityRetention);
            }
        }
        testObserver?.FinalizationCompleted(snapshot.OperationId, compacted, cached);
        return true;
    }

    private async Task FinalizePreHandoffFailureAsync(
        TopicSendSnapshot snapshot,
        LogicalSendKey logicalKey,
        OperationState state,
        Func<TopicSendOutcome, Task>? reconcileOutcome,
        TopicSendDraftCleanup? draftCleanup,
        Exception failure)
    {
        var checking = ReconcilingOutcome(
            "The handoff stopped before UI durability was marked; checking the authoritative trigger ledger.");
        using (var mutation = TryAcquireMutationLease())
        {
            if (mutation is null) return;
            Volatile.Write(ref state.Running, 0);
            lock (state.Gate)
            {
                InvalidateAuthorizationLocked(snapshot, state, "prehandoff_failure");
                state.PendingFailure = failure.Message;
                state.LastOutcome = checking;
            }
        }
        await PublishProgressAsync(state, checking).ConfigureAwait(false);
        using (var mutation = TryAcquireMutationLease())
        {
            if (mutation is null) return;
            Volatile.Write(ref state.Running, 1);
        }
        await RecoverPreHandoffAsync(
                snapshot,
                logicalKey,
                state,
                reconcileOutcome,
                draftCleanup,
                lifetime.Token)
            .ConfigureAwait(false);
    }

    private static TopicSendOutcome ReconcilingOutcome(string detail)
        => new(
            TopicSendOutcomeKind.Reconciling,
            new TopicSendHandoff(false, "reconciling", detail));

    private static bool ConsumesCapacity(OperationState state)
    {
        lock (state.Gate)
            return state.Lifecycle != TopicSendJournalLifecycle.Terminal
                   && Volatile.Read(ref state.Running) != 0;
    }

    private bool TryRecoverLocked(
        string scopeIdentity,
        string threadId,
        string targetDeviceId,
        long composerRevision,
        DateTimeOffset submittedAt,
        out TopicSendSnapshot? snapshot)
    {
        snapshot = null;
        if (!identityStore.TryGetUnresolved(scopeIdentity, out var found)
            || found is null)
            return false;

        var record = NormalizeRecord(
            found.Version == -1
                ? found with { ComposerRevision = composerRevision }
                : found);
        if (record.Version != found.Version
            || record.Lifecycle != found.Lifecycle
            || record.Cleanup != found.Cleanup
            || record.StateSequence != found.StateSequence
            || !string.Equals(record.PayloadHash, found.PayloadHash, StringComparison.Ordinal))
        {
            try
            {
                record = AdvanceRecord(record);
                WriteJournal(record, "legacy-migration");
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-journal-migration-failed",
                    $"scope={scopeIdentity};exception={exception.GetType().FullName}");
            }
        }

        if (record.Lifecycle == TopicSendJournalLifecycle.PreHandoff)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-prehandoff-recovered",
                $"operation={DiagnosticId(record.OperationId)};action=query_ledger");
        }

        var recovered = TopicSendSnapshot.Restore(
            record, threadId, targetDeviceId, submittedAt);
        if (operations.ContainsKey(recovered.OperationId))
        {
            snapshot = recovered;
            return true;
        }
        var outcome = record.ToOutcome();
        var state = StateFromRecord(record);
        operations[recovered.OperationId] = state;
        if (record.Lifecycle == TopicSendJournalLifecycle.Terminal
            && IsDraftCleanupFinal(record.Cleanup)
            && outcome is not null)
        {
            state.TerminalOutcome = outcome;
            var key = new LogicalSendKey(
                recovered.AccountId, threadId, targetDeviceId, recovered.ComposerRevision);
            CachePersistedLocked(key, recovered, outcome);
            TryCompactJournal(record, "recover-completed");
            operations.Remove(recovered.OperationId);
        }
        snapshot = recovered;
        return true;
    }

    private static OperationState StateFromRecord(TopicSendIdentityRecord record)
    {
        var outcome = record.ToOutcome();
        return new OperationState
        {
            Running = 0,
            Lifecycle = record.Lifecycle,
            Cleanup = record.Cleanup,
            JournalTerminalOutcome = outcome,
            LastOutcome = record.Lifecycle == TopicSendJournalLifecycle.Terminal
                          && IsDraftCleanupFinal(record.Cleanup)
                ? outcome
                : ReconcilingOutcome(
                    record.Lifecycle == TopicSendJournalLifecycle.PreHandoff
                        ? "Recovered the stable send identity; checking the authoritative trigger ledger."
                        : record.Lifecycle == TopicSendJournalLifecycle.Terminal
                        ? "The send is final; completing local draft cleanup."
                        : "Recovered a durable handoff that requires authoritative reconciliation.")
        };
    }

    private static TopicSendIdentityRecord NormalizeRecord(TopicSendIdentityRecord record)
        => TopicSendJournalInvariant.Normalize(record);

    private TopicSendIdentityRecord JournalRecord(
        TopicSendSnapshot snapshot,
        TopicSendJournalLifecycle lifecycle,
        TopicSendJournalCleanup cleanup,
        TopicSendOutcome? outcome = null)
        => TopicSendJournalOrdering.Prepare(new(
            snapshot.LogicalIdentity,
            snapshot.ScopeIdentity,
            snapshot.SubmissionSequence,
            snapshot.ComposerRevision,
            snapshot.OperationId,
            snapshot.RunId,
            snapshot.LineId,
            snapshot.DraftFingerprint,
            outcome?.Kind,
            outcome?.Exception?.Message ?? outcome?.Handoff?.Error,
            Version: TopicSendIdentityRecord.CurrentVersion,
            Lifecycle: lifecycle,
            Cleanup: cleanup,
            AccountId: snapshot.AccountId,
            StateSequence: identityStore.NextSequence(snapshot.ScopeIdentity)));

    private TopicSendIdentityRecord AdvanceRecord(TopicSendIdentityRecord record)
        => TopicSendJournalOrdering.Prepare(record with
        {
            Version = TopicSendIdentityRecord.CurrentVersion,
            StateSequence = identityStore.NextSequence(record.ScopeIdentity),
            PayloadHash = null
        });

    private static bool IsDraftCleanupFinal(TopicSendJournalCleanup cleanup)
        => TopicSendJournalInvariant.IsCleanupFinal(cleanup);

    private void WriteJournal(TopicSendIdentityRecord record, string transition)
    {
        testObserver?.Checkpoint($"journal:{transition}:before-write");
        journalFaultInjector?.Checkpoint(
            transition, TopicSendJournalBoundary.BeforeWrite, record);
        try
        {
            var result = identityStore.Apply(record);
            if (result == TopicSendJournalApplyResult.Conflict)
                throw TopicSendJournalOrdering.Conflict(record, record);
            if (result == TopicSendJournalApplyResult.Stale)
                throw new TopicSendJournalStaleException(
                    $"Stale topic send journal transition at state sequence {record.StateSequence} was fenced.");
        }
        catch (TopicSendJournalConflictException exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-journal-conflict-fenced",
                $"transition={transition};operation={DiagnosticId(record.OperationId)};"
                + $"stateSequence={record.StateSequence};exception={exception.GetType().FullName}");
            throw;
        }
        catch (TopicSendJournalStaleException exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-journal-stale-fenced",
                $"transition={transition};operation={DiagnosticId(record.OperationId)};"
                + $"stateSequence={record.StateSequence};exception={exception.GetType().FullName}");
            throw;
        }
        testObserver?.JournalWrite(transition, Volatile.Read(ref disposalCompleted) != 0);
        journalFaultInjector?.Checkpoint(
            transition, TopicSendJournalBoundary.AfterWrite, record);
    }

    private TopicSendIdentityRecord CompactJournal(
        TopicSendIdentityRecord record,
        string transition)
    {
        record = AdvanceRecord(record with
        {
            Compaction = TopicSendJournalCompaction.Compacted
        });
        journalFaultInjector?.Checkpoint(
            transition, TopicSendJournalBoundary.BeforeCompaction, record);
        try
        {
            var result = identityStore.ApplyCompaction(record);
            if (result == TopicSendJournalApplyResult.Conflict)
                throw TopicSendJournalOrdering.Conflict(record, record);
            if (result == TopicSendJournalApplyResult.Stale)
                throw new TopicSendJournalStaleException(
                    $"Stale topic send journal compaction at state sequence {record.StateSequence} was fenced.");
        }
        catch (TopicSendJournalConflictException exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-journal-conflict-fenced",
                $"transition={transition};operation={DiagnosticId(record.OperationId)};"
                + $"stateSequence={record.StateSequence};exception={exception.GetType().FullName}");
            throw;
        }
        catch (TopicSendJournalStaleException exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-journal-stale-fenced",
                $"transition={transition};operation={DiagnosticId(record.OperationId)};"
                + $"stateSequence={record.StateSequence};exception={exception.GetType().FullName}");
            throw;
        }
        journalFaultInjector?.Checkpoint(
            transition, TopicSendJournalBoundary.AfterCompaction, record);
        return record;
    }

    private bool TryCompactJournal(TopicSendIdentityRecord record, string transition)
    {
        try
        {
            CompactJournal(record, transition);
            return true;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-journal-compaction-failed",
                $"operation={record.OperationId};exception={exception.GetType().FullName}");
            return false;
        }
    }

    private bool TryCreateRetryAuthorization(
        TopicSendSnapshot snapshot,
        TopicSendReconciliationResult result,
        out TopicSendRetryAuthorization? authorization)
    {
        authorization = result.Authorization
                        ?? authorizationAuthority?.IssueRetryAuthorization(snapshot, result)
                        ?? (authorizationAuthority is null
                            ? null
                            : new TopicSendRetryAuthorization(
                                TopicSendRetryAuthorization.CurrentVersion,
                                Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                                    .ToLowerInvariant(),
                                snapshot.OperationId,
                                TopicSendSnapshot.StableId(
                                    "retry-snapshot",
                                    string.Join(
                                        "\0",
                                        snapshot.OperationId,
                                        snapshot.AccountId,
                                        snapshot.ThreadId,
                                        snapshot.TargetDeviceId,
                                        snapshot.DraftFingerprint)),
                                result.AccountId ?? "",
                                result.DatabaseIdentity ?? "",
                                result.DatabaseGeneration,
                                result.ObservationVersion,
                                result.ObservedAt,
                                snapshot.ComposerRevision));
        if (authorizationAuthority is null
            || authorization is null
            || string.IsNullOrWhiteSpace(snapshot.AccountId)
            || string.IsNullOrWhiteSpace(result.AccountId)
            || !string.Equals(snapshot.AccountId, result.AccountId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(result.DatabaseIdentity)
            || result.DatabaseGeneration <= 0
            || result.ObservationVersion <= 0
            || result.ObservedAt == default
            || authorization.Version != TopicSendRetryAuthorization.CurrentVersion
            || string.IsNullOrWhiteSpace(authorization.Nonce)
            || !string.Equals(authorization.OperationId, snapshot.OperationId, StringComparison.Ordinal)
            || authorization.ComposerRevision != snapshot.ComposerRevision
            || !string.Equals(authorization.AccountId, result.AccountId, StringComparison.Ordinal)
            || !string.Equals(
                authorization.DatabaseIdentity,
                result.DatabaseIdentity,
                StringComparison.Ordinal)
            || authorization.DatabaseGeneration != result.DatabaseGeneration
            || authorization.ObservationEpoch != result.ObservationVersion)
        {
            authorization = null;
            return false;
        }

        if (!authorizationAuthority.IsCurrent(authorization.Scope))
        {
            authorization = null;
            return false;
        }
        return true;
    }

    private bool TryConsumeRetryAuthorization(
        TopicSendSnapshot snapshot,
        OperationState state,
        MutationLease _)
    {
        TopicSendRetryAuthorization? authorization;
        TopicSendAuthorizationScope? consumed;
        lock (state.Gate)
        {
            authorization = state.RetryAuthorization;
            consumed = state.ConsumedAuthorizationScope;
        }

        if (authorization is null)
            return consumed is not null
                   && authorizationAuthority?.IsCurrent(consumed) == true;

        if (!string.Equals(authorization.OperationId, snapshot.OperationId, StringComparison.Ordinal)
            || authorization.ComposerRevision != snapshot.ComposerRevision
            || !string.Equals(authorization.AccountId, snapshot.AccountId, StringComparison.Ordinal)
            || !string.Equals(
                authorization.SnapshotIdentity,
                TopicSendSnapshot.StableId(
                    "retry-snapshot",
                    string.Join(
                        "\0",
                        snapshot.OperationId,
                        snapshot.AccountId,
                        snapshot.ThreadId,
                        snapshot.TargetDeviceId,
                        snapshot.DraftFingerprint)),
                StringComparison.Ordinal)
            || authorizationAuthority is null)
        {
            testObserver?.Checkpoint("retry-snapshot-mismatch");
            lock (state.Gate)
                InvalidateAuthorizationLocked(snapshot, state, "snapshot_mismatch");
            return false;
        }

        // Never enter AppState's profileSyncGate while holding state.Gate.
        // The token identity is compared after the AppState call before mutation.
        var consumedNow = authorizationAuthority.TryConsume(authorization.Scope);
        if (consumedNow)
        {
            lock (state.Gate)
            {
                if (!ReferenceEquals(state.RetryAuthorization, authorization))
                    consumedNow = false;
                else
                {
                    state.RetryAuthorization = null;
                    state.ConsumedAuthorizationScope = authorization.Scope;
                }
            }
            if (consumedNow)
                RecordAuthorizationDiagnostic(snapshot, authorization, "consumed");
        }
        else
        {
            lock (state.Gate)
            {
                if (ReferenceEquals(state.RetryAuthorization, authorization))
                    InvalidateAuthorizationLocked(snapshot, state, "scope_mismatch");
            }
        }
        return consumedNow;
    }

    private TopicRunBeginResult AuthorizeAndBeginTopicRun(
        TopicSendSnapshot snapshot,
        OperationState state,
        TopicRunBeginCommand command,
        MutationLease _)
    {
        TopicSendRetryAuthorization? authorization;
        lock (state.Gate)
            authorization = state.RetryAuthorization;
        if (authorization is null || authorizationAuthority is null)
            return new TopicRunBeginResult(false, false, "reconcile_required");

        // AppState owns profileSyncGate. No coordinator state lock may be held here.
        var result = authorizationAuthority.AuthorizeAndBeginTopicRun(
            snapshot,
            authorization,
            command);
        lock (state.Gate)
        {
            if (ReferenceEquals(state.RetryAuthorization, authorization))
            {
                state.RetryAuthorization = null;
                state.ConsumedAuthorizationScope = result.DurableCommitted
                    ? authorization.Scope
                    : null;
            }
            else if (result.DurableCommitted)
            {
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-send-lock-order-race",
                    $"operation={DiagnosticId(snapshot.OperationId)};phase=atomic-begin");
            }
        }
        RecordAuthorizationDiagnostic(
            snapshot,
            authorization,
            result.DurableCommitted ? "atomic_committed" : $"atomic_{result.Code}");
        return result;
    }

    private void InvalidateAuthorizationLocked(
        TopicSendSnapshot snapshot,
        OperationState state,
        string reason)
    {
        var authorization = state.RetryAuthorization;
        state.RetryAuthorization = null;
        state.ConsumedAuthorizationScope = null;
        if (authorization is not null)
            RecordAuthorizationDiagnostic(snapshot, authorization, $"invalidated_{reason}");
    }

    private void InvalidateRetryAuthorization(TopicSendRetryAuthorization authorization)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return;
        authorizationAuthority?.InvalidateRetryAuthorization(authorization);
    }

    private bool TryContinuePendingReconciliation(
        OperationState state,
        ref long handledGeneration)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return false;
        long pending;
        lock (state.Gate)
        {
            pending = state.PendingAvailabilityGeneration;
            if (Volatile.Read(ref disposed) != 0 || pending <= handledGeneration)
            {
                Volatile.Write(ref state.Running, 0);
                return false;
            }
            handledGeneration = pending;
            state.ProcessedAvailabilityGeneration = pending;
        }
        RuntimeDiagnostics.Current?.RecordEvent(
            "topic-send-reconciliation-pending",
            $"generation={DiagnosticId(
                pending.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        return true;
    }

    private bool TryTakePendingReconciliation(
        OperationState state,
        ref long handledGeneration)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return false;
        lock (state.Gate)
        {
            if (state.PendingAvailabilityGeneration <= handledGeneration)
                return false;
            handledGeneration = state.PendingAvailabilityGeneration;
            state.ProcessedAvailabilityGeneration = handledGeneration;
            return true;
        }
    }

    private bool IsReconciliationCurrent(long generation)
        => Volatile.Read(ref disposed) == 0
           && Volatile.Read(ref lifetimeGeneration) == generation;

    private static void RecordAuthorizationDiagnostic(
        TopicSendSnapshot snapshot,
        TopicSendRetryAuthorization authorization,
        string action)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "topic-send-retry-authorization",
            $"operation={DiagnosticId(snapshot.OperationId)};"
            + $"account={DiagnosticAccount(authorization.AccountId)};"
            + $"database={DiagnosticId(authorization.DatabaseIdentity)};"
            + $"generation={DiagnosticId(
                authorization.DatabaseGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))};"
            + $"observation={DiagnosticId(
                authorization.ObservationEpoch.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))};"
            + $"revision={DiagnosticId(
                authorization.ComposerRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))};"
            + $"token={DiagnosticId(authorization.Nonce)};"
            + $"action={action}");

    private static void RecordPreHandoffDiagnostic(
        TopicSendSnapshot snapshot,
        string queryResult,
        string action,
        string? reason = null,
        string? accountId = null,
        int? attempt = null)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "topic-send-prehandoff-recovery",
            $"operation={DiagnosticId(snapshot.OperationId)};"
            + $"run={DiagnosticId(snapshot.RunId)};"
            + $"query={queryResult};action={action};"
            + $"reason={DiagnosticReason(reason)};"
            + $"account={DiagnosticAccount(accountId ?? snapshot.AccountId)};"
            + $"attempt={attempt?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}");

    private static string DiagnosticReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "none"
            : new string(reason.Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(48).ToArray());

    private static string DiagnosticAccount(string? accountId)
        => string.IsNullOrWhiteSpace(accountId) ? "none" : DiagnosticId(accountId);

    private static string DiagnosticId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..16];

    private async Task PublishProgressAsync(
        OperationState state,
        TopicSendOutcome outcome)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return;
        List<ObserverSubscription> observers;
        lock (state.Gate)
        {
            state.LastOutcome = outcome;
            observers = state.Observers.Values.ToList();
        }
        var dispatches = observers
            .Select(observer => QueueSubscription(observer, outcome, detachAfter: false))
            .ToArray();
        mutation.Dispose();
        await Task.WhenAll(dispatches).ConfigureAwait(false);
        using var publishedMutation = TryAcquireMutationLease();
        if (publishedMutation is null) return;
        lock (state.Gate)
        {
            if (ReferenceEquals(state.LastOutcome, outcome))
                state.ObservableOutcome = outcome;
        }
    }

    private void Detach(
        OperationState? operation,
        ObserverSubscription subscription,
        Task quiescence)
    {
        if (operation is null) return;
        TaskCompletionSource? barrier;
        lock (operation.Gate)
        {
            if (!operation.Observers.TryGetValue(subscription.ObserverId, out var found)
                || !ReferenceEquals(found, subscription))
                return;
            operation.Observers.Remove(subscription.ObserverId);
            if (!operation.ObserverDetachBarriers.TryGetValue(subscription.ObserverId, out barrier))
            {
                barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                operation.ObserverDetachBarriers.Add(subscription.ObserverId, barrier);
            }
        }
        _ = CompleteDetachBarrierAsync(
            operation, subscription.ObserverId, quiescence, barrier);
    }

    private static async Task CompleteDetachBarrierAsync(
        OperationState operation,
        string observerId,
        Task quiescence,
        TaskCompletionSource barrier)
    {
        await quiescence.ConfigureAwait(false);
        lock (operation.Gate)
        {
            if (operation.ObserverDetachBarriers.TryGetValue(observerId, out var found)
                && ReferenceEquals(found, barrier))
                operation.ObserverDetachBarriers.Remove(observerId);
        }
        barrier.TrySetResult();
    }

    private Task QueueSubscription(
        ObserverSubscription subscription,
        TopicSendOutcome outcome,
        bool detachAfter)
    {
        testObserver?.CallbackQueued(
            subscription.OperationId,
            Volatile.Read(ref disposalCompleted) != 0);
        lock (lifetimeGate)
            queuedSubscriptions.Add(subscription);
        var dispatch = subscription.QueueDispatch(outcome, detachAfter);
        _ = ForgetQueuedSubscriptionAsync(subscription, dispatch);
        return dispatch;
    }

    private async Task ForgetQueuedSubscriptionAsync(
        ObserverSubscription subscription,
        Task dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            using var mutation = TryAcquireMutationLease();
            if (mutation is not null)
            {
                lock (lifetimeGate)
                    queuedSubscriptions.Remove(subscription);
            }
        }
    }

    private async Task NotifySubscriptionAsync(
        ObserverSubscription subscription,
        TopicSendOutcome outcome)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return;
        var dispatch = QueueSubscription(subscription, outcome, detachAfter: true);
        mutation.Dispose();
        await dispatch.ConfigureAwait(false);
    }

    private async Task NotifySubscriptionProgressAsync(
        ObserverSubscription subscription,
        TopicSendOutcome outcome)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return;
        var dispatch = QueueSubscription(subscription, outcome, detachAfter: false);
        mutation.Dispose();
        await dispatch.ConfigureAwait(false);
    }

    private async Task NotifyOnceAsync(
        Func<TopicSendOutcome, Task>? callback,
        TopicSendOutcome outcome)
    {
        if (callback is null) return;
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return;
        Task invocation;
        try
        {
            testObserver?.CallbackQueued("legacy", Volatile.Read(ref disposalCompleted) != 0);
            invocation = callback(outcome);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-callback-failed",
                $"exception={exception.GetType().FullName}");
            return;
        }
        try
        {
            await invocation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-callback-failed",
                $"exception={exception.GetType().FullName}");
        }
    }

    private async Task<bool> TryNotifyOnceAsync(
        Func<TopicSendOutcome, Task> callback,
        TopicSendOutcome outcome)
    {
        using var mutation = TryAcquireMutationLease();
        if (mutation is null) return false;
        Task invocation;
        try
        {
            testObserver?.CallbackQueued(
                "legacy-cleanup",
                Volatile.Read(ref disposalCompleted) != 0);
            invocation = callback(outcome);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-cleanup-callback-failed",
                $"exception={exception.GetType().FullName}");
            return false;
        }
        try
        {
            await invocation.ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-send-cleanup-callback-failed",
                $"exception={exception.GetType().FullName}");
            return false;
        }
    }

    private void PruneCompletedLocked()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var completed in completedByKey.Values
                     .Where(value => value.ExpiresAt <= now)
                     .OrderBy(value => value.SubmissionSequence)
                     .ToList())
            RemoveCompletedLocked(completed);
        foreach (var operationId in retryableOutcomes
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToList())
            retryableOutcomes.Remove(operationId);

        while (completedByKey.Count > retention.MaximumCompletedIdentities)
            RemoveCompletedLocked(
                completedByKey.Values.MinBy(value => value.SubmissionSequence)!);
        while (retryableOutcomes.Count > retention.MaximumCompletedIdentities)
            retryableOutcomes.Remove(retryableOutcomes.Keys.First());
    }

    private void PruneSnapshotsLocked()
    {
        while (snapshots.Count > retention.MaximumUnsubmittedSnapshots)
        {
            var removable = snapshots
                .Where(pair => !operations.ContainsKey(pair.Value.Snapshot.OperationId))
                .MinBy(pair => pair.Value.Sequence);
            if (removable.Equals(default(KeyValuePair<LogicalSendKey, SnapshotEntry>)))
                return;
            snapshots.Remove(removable.Key);
        }
    }

    private void RemoveCompletedLocked(CompletedIdentity completed)
    {
        completedByKey.Remove(completed.Key);
        completedByOperation.Remove(completed.OperationId);
    }

    private void CachePersistedLocked(
        LogicalSendKey key,
        TopicSendSnapshot snapshot,
        TopicSendOutcome outcome)
    {
        var completed = new CompletedIdentity(
            key,
            snapshot,
            outcome,
            timeProvider.GetUtcNow() + retention.CompletedIdentityRetention,
            snapshot.SubmissionSequence);
        _ = TryCacheCompletedLocked(completed);
    }

    private bool TryCacheCompletedLocked(CompletedIdentity candidate)
    {
        if (completedByKey.TryGetValue(candidate.Key, out var current)
            && current.SubmissionSequence > candidate.SubmissionSequence)
            return false;
        if (completedByOperation.TryGetValue(candidate.OperationId, out current)
            && current.SubmissionSequence > candidate.SubmissionSequence)
            return false;

        if (completedByKey.TryGetValue(candidate.Key, out current)
            && !string.Equals(current.OperationId, candidate.OperationId, StringComparison.Ordinal))
            completedByOperation.Remove(current.OperationId);
        completedByKey[candidate.Key] = candidate;
        completedByOperation[candidate.OperationId] = candidate;
        PruneCompletedLocked();
        return completedByOperation.ContainsKey(candidate.OperationId);
    }

}

public sealed record ComposerRevisionSnapshot(
    string EntityId,
    long Revision,
    string Text);

public sealed record ComposerClearToken(
    ComposerRevisionSnapshot Submitted,
    long ClearedRevision);

public sealed class ComposerRevisionGuard
{
    private sealed record RevisionState(long Revision, string Text);

    private readonly object gate = new();
    private readonly Dictionary<string, RevisionState> current = new(StringComparer.Ordinal);
    private long revision;

    public void SetExact(string entityId, long durableRevision, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(text);
        if (durableRevision <= 0) throw new ArgumentOutOfRangeException(nameof(durableRevision));
        lock (gate)
        {
            current[entityId] = new RevisionState(durableRevision, text);
            revision = Math.Max(revision, durableRevision);
        }
    }

    public long Track(string entityId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            var next = ++revision;
            current[entityId] = new RevisionState(next, text);
            return next;
        }
    }

    public ComposerRevisionSnapshot Capture(string entityId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            if (current.TryGetValue(entityId, out var found)
                && string.Equals(found.Text, text, StringComparison.Ordinal))
                return new ComposerRevisionSnapshot(entityId, found.Revision, found.Text);

            var next = ++revision;
            current[entityId] = new RevisionState(next, text);
            return new ComposerRevisionSnapshot(entityId, next, text);
        }
    }

    public ComposerRevisionSnapshot GetOrCreate(string entityId, string initialText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(initialText);
        lock (gate)
        {
            if (current.TryGetValue(entityId, out var found))
                return new ComposerRevisionSnapshot(entityId, found.Revision, found.Text);

            var next = ++revision;
            current[entityId] = new RevisionState(next, initialText);
            return new ComposerRevisionSnapshot(entityId, next, initialText);
        }
    }

    public bool TryApplyRestore(
        string entityId,
        long expectedRevision,
        string restoredText,
        out long restoredRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(restoredText);
        lock (gate)
        {
            if (!current.TryGetValue(entityId, out var found)
                || found.Revision != expectedRevision)
            {
                restoredRevision = 0;
                return false;
            }

            if (string.Equals(found.Text, restoredText, StringComparison.Ordinal))
            {
                restoredRevision = found.Revision;
                return true;
            }

            if (found.Text.Length != 0)
            {
                restoredRevision = 0;
                return false;
            }

            restoredRevision = ++revision;
            current[entityId] = new RevisionState(restoredRevision, restoredText);
            return true;
        }
    }

    public bool TryReplace(
        ComposerRevisionSnapshot expected,
        string replacement,
        out long replacementRevision)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        lock (gate)
        {
            if (!Matches(expected))
            {
                replacementRevision = 0;
                return false;
            }

            replacementRevision = ++revision;
            current[expected.EntityId] = new RevisionState(replacementRevision, replacement);
            return true;
        }
    }

    public bool TryClear(
        ComposerRevisionSnapshot submitted,
        out ComposerClearToken? token)
    {
        if (!TryReplace(submitted, "", out var clearedRevision))
        {
            token = null;
            return false;
        }

        token = new ComposerClearToken(submitted, clearedRevision);
        return true;
    }

    public bool TryRestore(ComposerClearToken token, out long restoredRevision)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (gate)
        {
            if (!current.TryGetValue(token.Submitted.EntityId, out var found)
                || found.Revision != token.ClearedRevision
                || found.Text.Length != 0)
            {
                restoredRevision = 0;
                return false;
            }

            restoredRevision = ++revision;
            current[token.Submitted.EntityId] =
                new RevisionState(restoredRevision, token.Submitted.Text);
            return true;
        }
    }

    private bool Matches(ComposerRevisionSnapshot expected)
        => current.TryGetValue(expected.EntityId, out var found)
           && found.Revision == expected.Revision
           && string.Equals(found.Text, expected.Text, StringComparison.Ordinal);
}
