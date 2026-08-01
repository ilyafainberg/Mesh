
namespace Mesh.App.Domain;

/// <summary>The lifecycle state of an ask-user prompt.</summary>
public enum AskUserState { Pending, Resolved, Expired, Cancelled }

/// <summary>A single selectable option inside an <see cref="AskUserPrompt"/>.</summary>
public sealed record AskUserOption(string Id, string Title, string? Description);

/// <summary>
/// A question posed to the user during a suspended agent run.
/// Options must contain 2-5 entries; once created only the state transitions
/// (pending -> resolved | expired | cancelled) and resolution fields are written.
/// </summary>
public sealed record AskUserPrompt(
    string PromptId,
    string ThreadId,
    string RunId,
    string Question,
    IReadOnlyList<AskUserOption> Options,
    int? RecommendedIndex,
    AskUserState State,
    string? Selection,
    string? OriginDeviceId,
    string? ResolutionDeviceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ResolvedAt,
    int Revision,
    int Version = 1)
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the options list is outside 2-5
    /// or <see cref="ArgumentOutOfRangeException"/> when recommendedIndex is out of range.
    /// Also enforces per-option invariants: non-blank ids/titles and unique ids.
    /// </summary>
    public static void Validate(IReadOnlyList<AskUserOption> options, int? recommendedIndex)
    {
        if (options is null || options.Count < 2 || options.Count > 5)
            throw new ArgumentException("Options must contain between 2 and 5 entries.", nameof(options));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            if (o is null || string.IsNullOrWhiteSpace(o.Id))
                throw new ArgumentException($"Option {i} has a blank id.", nameof(options));
            if (string.IsNullOrWhiteSpace(o.Title))
                throw new ArgumentException($"Option '{o.Id}' has a blank title.", nameof(options));
            if (!seen.Add(o.Id))
                throw new ArgumentException($"Duplicate option id '{o.Id}'.", nameof(options));
        }

        if (recommendedIndex.HasValue
            && (recommendedIndex.Value < 0 || recommendedIndex.Value >= options.Count))
            throw new ArgumentOutOfRangeException(nameof(recommendedIndex),
                $"RecommendedIndex {recommendedIndex.Value} is out of range for {options.Count} options.");
    }

    /// <summary>
    /// Full create-time invariant check: non-blank identity/question fields, valid
    /// options (see <see cref="Validate"/>), and sane version/revision counters.
    /// Throws <see cref="ArgumentException"/> on any violation.
    /// </summary>
    public void EnsureValidForCreate()
    {
        RequireNonBlank(PromptId, nameof(PromptId));
        RequireNonBlank(ThreadId, nameof(ThreadId));
        RequireNonBlank(RunId, nameof(RunId));
        RequireNonBlank(Question, nameof(Question));
        Validate(Options, RecommendedIndex);
        if (Version < 1)
            throw new ArgumentException("Version must be >= 1.", nameof(Version));
        if (Revision < 1)
            throw new ArgumentException("Revision must be >= 1.", nameof(Revision));
    }

    private static void RequireNonBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must be non-blank.", name);
    }
}

/// <summary>
/// Opaque suspended agent context stored verbatim across a user-interaction pause.
/// The ContextJson payload is neither interpreted nor transformed here.
/// </summary>
public sealed record SuspendedAgentContext(
    string ContextId,
    string PromptId,
    string ThreadId,
    string RunId,
    string ContextJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ResumedAt)
{
    /// <summary>
    /// Validates the identity and payload fields are non-blank.
    /// Throws <see cref="ArgumentException"/> on any violation.
    /// </summary>
    public void EnsureValid()
    {
        Require(ContextId, nameof(ContextId));
        Require(PromptId, nameof(PromptId));
        Require(ThreadId, nameof(ThreadId));
        Require(RunId, nameof(RunId));
        Require(ContextJson, nameof(ContextJson));
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must be non-blank.", name);
    }
}

/// <summary>The kind of persisted capability asset.</summary>
public enum AssetKind { Skill, Knowledge, Widget }

/// <summary>
/// Summary row for a Skill, Knowledge, or Widget asset. Does not carry the content bytes;
/// those live in asset_content and are fetched only when the full asset is required.
/// </summary>
public sealed record AssetRecord(
    AssetKind Kind,
    string Id,
    string Name,
    string? MetadataJson,
    string? ContentMime,
    string? ContentHash,
    long ContentByteCount,
    int Version,
    string? SourceDeviceId,
    DateTimeOffset UpdatedAt,
    bool IsDeleted,
    bool LocalOnly)
{
    /// <summary>
    /// Validates identity and provenance fields for a caller-initiated upsert:
    /// non-blank id/name/source and a version >= 1.
    /// Throws <see cref="ArgumentException"/> on any violation.
    /// </summary>
    public void EnsureValidForUpsert()
    {
        if (!Enum.IsDefined(Kind))
            throw new ArgumentException($"Unknown asset kind '{Kind}'.", nameof(Kind));
        Require(Id, nameof(Id));
        Require(Name, nameof(Name));
        Require(SourceDeviceId, nameof(SourceDeviceId));
        if (Version < 1)
            throw new ArgumentException("Version must be >= 1.", nameof(Version));
    }

    /// <summary>
    /// Validates identity and provenance fields for a remote-supplied tombstone:
    /// non-blank id/source, deleted flag set, and a version >= 1.
    /// Throws <see cref="ArgumentException"/> on any violation.
    /// </summary>
    public void EnsureValidTombstone()
    {
        if (!Enum.IsDefined(Kind))
            throw new ArgumentException($"Unknown asset kind '{Kind}'.", nameof(Kind));
        Require(Id, nameof(Id));
        Require(SourceDeviceId, nameof(SourceDeviceId));
        if (!IsDeleted)
            throw new ArgumentException("Tombstone must have IsDeleted set.", nameof(IsDeleted));
        if (Version < 1)
            throw new ArgumentException("Version must be >= 1.", nameof(Version));
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must be non-blank.", name);
    }
}

/// <summary>
/// The single deterministic conflict rule used for both remote asset upserts and
/// deletes. It decides whether an incoming remote record supersedes the stored one.
///
/// Ordering (highest precedence first):
///   1. Higher <see cref="AssetRecord.Version"/> wins.
///   2. On equal version, the greater ordinal <see cref="AssetRecord.SourceDeviceId"/> wins.
///   3. On equal version and source, a tombstone beats a live record.
///   4. An exact duplicate (same version/source/deleted-state) loses (idempotent reject).
/// A stored <see cref="AssetRecord.LocalOnly"/> row rejects any remote mutation.
/// A missing stored record is always superseded by the incoming record.
/// </summary>
public static class AssetConflict
{
    /// <summary>Returns true when <paramref name="incoming"/> should replace <paramref name="existing"/>.</summary>
    public static bool RemoteWins(AssetRecord? existing, AssetRecord incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (existing is null) return true;
        if (existing.LocalOnly) return false;

        int byVersion = incoming.Version.CompareTo(existing.Version);
        if (byVersion != 0) return byVersion > 0;

        int bySource = string.CompareOrdinal(
            incoming.SourceDeviceId ?? string.Empty, existing.SourceDeviceId ?? string.Empty);
        if (bySource != 0) return bySource > 0;

        // Same version and source: a tombstone supersedes a live row; anything else is a
        // duplicate and loses.
        if (incoming.IsDeleted != existing.IsDeleted)
            return incoming.IsDeleted && !existing.IsDeleted;

        return false;
    }
}
