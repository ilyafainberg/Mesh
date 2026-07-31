using System.Security.Cryptography;
using System.Text;

namespace Mesh.Shared;

/// <summary>
/// Mesh 1.17 device-sync operation and envelope kinds. These are additive within Protocol 8:
/// the 1.17 operations ride inside a dedicated <see cref="Envelope117Operation"/> envelope that
/// is still an ordinary encrypted, queued, ACKed device-sync envelope. There is no new protocol
/// version and encryption is never bypassed.
///
/// <see cref="Envelope117Operation"/> starts with the <c>device.sync.</c> prefix so the existing
/// transport policy routes it to the durable relay queue exactly like every other device-sync
/// envelope, which is also what triggers the mobile push-registration path for offline targets.
/// </summary>
public static class Mesh117SyncKinds
{
    /// <summary>Envelope kind that carries a batch of 1.17 device-sync operations.</summary>
    public const string Envelope117Operation = "device.sync.117.operation";

    /// <summary>Operation kind: desktop-only asset (Skill/Knowledge/Widget) create or update.</summary>
    public const string AssetUpsert = "asset.upsert";

    /// <summary>Operation kind: desktop-only asset delete carried as a full tombstone.</summary>
    public const string AssetDelete = "asset.delete";

    /// <summary>Operation kind: an ask-user prompt fanned to every eligible device (incl. mobile).</summary>
    public const string AskUserPrompt = "askuser.prompt";

    /// <summary>Operation kind: an ask-user resolution fanned to every eligible device (incl. mobile).</summary>
    public const string AskUserResolution = "askuser.resolution";

    public static bool IsAssetKind(string? kind)
        => kind is AssetUpsert or AssetDelete;

    public static bool IsAskUserKind(string? kind)
        => kind is AskUserPrompt or AskUserResolution;

    public static bool IsOperationKind(string? kind)
        => IsAssetKind(kind) || IsAskUserKind(kind);
}

/// <summary>
/// Serialisable, App-independent view of an asset summary plus its content bytes. Domain types
/// live in the App assembly, so the wire form is expressed with primitives and a base64 blob and
/// converted to/from <c>AssetRecord</c> by an adapter on the App side.
/// </summary>
public sealed record Asset117Payload(
    string Kind,
    string Id,
    string Name,
    string? MetadataJson,
    string? ContentMime,
    string? ContentHash,
    long ContentByteCount,
    int Version,
    string SourceDeviceId,
    long UpdatedAtUnixMs,
    bool IsDeleted,
    bool LocalOnly,
    string ContentBase64);

/// <summary>A single option inside a fanned ask-user prompt.</summary>
public sealed record AskUser117Option(string Id, string Title, string? Description);

/// <summary>Serialisable view of an ask-user prompt being routed to every eligible device.</summary>
public sealed record AskUser117PromptPayload(
    string PromptId,
    string ThreadId,
    string RunId,
    string Question,
    IReadOnlyList<AskUser117Option> Options,
    int? RecommendedIndex,
    string? OriginDeviceId,
    long CreatedAtUnixMs,
    long? ExpiresAtUnixMs,
    int Revision,
    int Version,
    string DeepLink);

/// <summary>Serialisable view of an ask-user resolution being routed to every eligible device.</summary>
public sealed record AskUser117ResolutionPayload(
    string PromptId,
    string Selection,
    string ResolutionDeviceId,
    string IdempotencyToken,
    long ResolvedAtUnixMs,
    AskUser117PromptPayload? Prompt = null);

/// <summary>
/// Deterministic, collision-resistant 1.17 operation ids built from length-prefixed parts. The same
/// logical operation (same kind + parts) always hashes to the same id so a redelivered operation is
/// idempotent, while any changed part (e.g. a new revision or idempotency token) yields a fresh id.
/// </summary>
public static class Mesh117OperationId
{
    /// <summary>Unit separator between the kind and each length-prefixed part.</summary>
    private const char Separator = '\u001f';

    public static string Stable(string kind, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var canonical = new StringBuilder().Append(kind).Append(Separator);
        foreach (var part in parts)
        {
            var value = part ?? string.Empty;
            canonical.Append(value.Length).Append(':').Append(value).Append(Separator);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Canonical deep-link for an ask-user prompt: <c>mesh://ask/&lt;promptId&gt;</c>.</summary>
public static class Mesh117DeepLink
{
    private const string Prefix = "mesh://ask/";

    public static string ForPrompt(string promptId)
    {
        if (string.IsNullOrWhiteSpace(promptId))
            throw new ArgumentException("A prompt id is required.", nameof(promptId));
        return Prefix + promptId;
    }

    public static bool TryParse(string? link, out string promptId)
    {
        promptId = "";
        if (string.IsNullOrWhiteSpace(link)
            || !link.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var id = link[Prefix.Length..];
        if (string.IsNullOrWhiteSpace(id)) return false;
        promptId = id;
        return true;
    }
}

/// <summary>Thrown when a single asset's encoded content cannot fit an encrypted envelope. The
/// caller must fail the operation explicitly rather than truncate the content.</summary>
public sealed class Mesh117PayloadTooLargeException(string message) : Exception(message);

/// <summary>Thrown when an inbound 1.17 payload fails a structural/integrity check before apply.</summary>
public sealed class Mesh117PayloadInvalidException(string message) : Exception(message);

/// <summary>
/// Pure paging/size math for asset snapshot streaming. Kept free of any storage dependency so it
/// can be exhaustively tested: it decides page counts for a summary count, clamps the page size to
/// the store's 1..500 bound, and enforces that a single asset never silently exceeds the envelope.
/// </summary>
public static class Mesh117SnapshotPlanner
{
    /// <summary>The store's hard page bound; summaries are paged, never materialised all at once.</summary>
    public const int MaxPageSize = 500;

    public const int MinPageSize = 1;

    /// <summary>
    /// Ciphertext is base64 (~1.37x) over a JSON payload that itself base64-encodes content
    /// (~1.34x). This budget keeps a single-asset plaintext safely under
    /// <see cref="MessageLimits.MaxEnvelopeBodyBytes"/> once encrypted and framed.
    /// </summary>
    public const int MaxOperationPlaintextBytes = 1_300_000;

    /// <summary>Largest raw asset content that can ride a single envelope. Larger content fails
    /// explicitly (never truncated).</summary>
    public const long MaxAssetContentBytes = 900_000;

    public static int ClampPageSize(int requested)
        => requested < MinPageSize ? MinPageSize : (requested > MaxPageSize ? MaxPageSize : requested);

    /// <summary>Number of pages needed to walk <paramref name="totalCount"/> summaries at
    /// <paramref name="pageSize"/> (clamped). Zero summaries yields zero pages.</summary>
    public static int PageCount(long totalCount, int pageSize)
    {
        if (totalCount <= 0) return 0;
        var size = ClampPageSize(pageSize);
        return checked((int)((totalCount + size - 1) / size));
    }

    /// <summary>Throws when a single asset's raw content exceeds the per-envelope limit.</summary>
    public static void EnsureAssetContentFits(string assetId, long contentByteCount)
    {
        if (contentByteCount > MaxAssetContentBytes)
            throw new Mesh117PayloadTooLargeException(
                $"Asset '{assetId}' content is {contentByteCount} bytes, over the "
                + $"{MaxAssetContentBytes}-byte single-envelope limit. It must be delivered as its own "
                + "operation with bounded chunking rather than truncated.");
    }

    /// <summary>Throws when an encoded operation plaintext exceeds the per-envelope plaintext budget.</summary>
    public static void EnsureOperationFits(string operationId, long plaintextByteCount)
    {
        if (plaintextByteCount > MaxOperationPlaintextBytes)
            throw new Mesh117PayloadTooLargeException(
                $"Operation '{operationId}' plaintext is {plaintextByteCount} bytes, over the "
                + $"{MaxOperationPlaintextBytes}-byte envelope budget.");
    }
}

/// <summary>
/// Structural and integrity validation for inbound 1.17 payloads, applied before any DB mutation.
/// A failure here is permanent (bad data can never become good), so callers reject rather than
/// retry. Duplicate delivery is not an error: idempotency is handled by the deterministic store.
/// </summary>
public static class Mesh117PayloadGuard
{
    /// <summary>
    /// Validates an asset payload: identity present, byte count matches the decoded content, and the
    /// content hash (when supplied) matches a fresh SHA-256 of the decoded bytes. Returns the decoded
    /// content so the caller decodes exactly once.
    /// </summary>
    public static byte[] ValidateAsset(Asset117Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.Id))
            throw new Mesh117PayloadInvalidException("Asset payload id is blank.");
        if (string.IsNullOrWhiteSpace(payload.SourceDeviceId))
            throw new Mesh117PayloadInvalidException("Asset payload source device is blank.");
        if (payload.Version < 1)
            throw new Mesh117PayloadInvalidException("Asset payload version must be >= 1.");

        byte[] content;
        try
        {
            content = string.IsNullOrEmpty(payload.ContentBase64)
                ? []
                : Convert.FromBase64String(payload.ContentBase64);
        }
        catch (FormatException ex)
        {
            throw new Mesh117PayloadInvalidException("Asset content is not valid base64: " + ex.Message);
        }

        if (payload.IsDeleted)
        {
            // Tombstones carry no content; a live payload must not masquerade as one.
            if (content.Length != 0)
                throw new Mesh117PayloadInvalidException("A delete tombstone must not carry content.");
            return content;
        }

        if (payload.ContentByteCount != content.Length)
            throw new Mesh117PayloadInvalidException(
                $"Asset content byte count {payload.ContentByteCount} does not match decoded length "
                + $"{content.Length}.");

        Mesh117SnapshotPlanner.EnsureAssetContentFits(payload.Id, content.Length);

        if (!string.IsNullOrEmpty(payload.ContentHash))
        {
            var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actual, payload.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new Mesh117PayloadInvalidException("Asset content hash did not match its bytes.");
        }

        return content;
    }

    /// <summary>Validates an ask-user prompt payload: identity/question present and 2-5 options with
    /// non-blank unique ids/titles.</summary>
    public static void ValidatePrompt(AskUser117PromptPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireNonBlank(payload.PromptId, "PromptId");
        RequireNonBlank(payload.ThreadId, "ThreadId");
        RequireNonBlank(payload.RunId, "RunId");
        RequireNonBlank(payload.Question, "Question");
        if (payload.Options is null || payload.Options.Count is < 2 or > 5)
            throw new Mesh117PayloadInvalidException("An ask-user prompt must have between 2 and 5 options.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in payload.Options)
        {
            if (option is null || string.IsNullOrWhiteSpace(option.Id))
                throw new Mesh117PayloadInvalidException("An ask-user option has a blank id.");
            if (string.IsNullOrWhiteSpace(option.Title))
                throw new Mesh117PayloadInvalidException($"Ask-user option '{option.Id}' has a blank title.");
            if (!seen.Add(option.Id))
                throw new Mesh117PayloadInvalidException($"Duplicate ask-user option id '{option.Id}'.");
        }
        if (payload.RecommendedIndex is { } idx && (idx < 0 || idx >= payload.Options.Count))
            throw new Mesh117PayloadInvalidException("Ask-user recommended index is out of range.");
    }

    /// <summary>Validates an ask-user resolution payload: all identity/selection fields present.</summary>
    public static void ValidateResolution(AskUser117ResolutionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireNonBlank(payload.PromptId, "PromptId");
        RequireNonBlank(payload.Selection, "Selection");
        RequireNonBlank(payload.ResolutionDeviceId, "ResolutionDeviceId");
        RequireNonBlank(payload.IdempotencyToken, "IdempotencyToken");
        if (payload.Prompt is not null)
        {
            ValidatePrompt(payload.Prompt);
            if (!string.Equals(
                    payload.Prompt.PromptId,
                    payload.PromptId,
                    StringComparison.Ordinal))
            {
                throw new Mesh117PayloadInvalidException(
                    "The embedded prompt id does not match the resolution.");
            }
            if (!payload.Prompt.Options.Any(option =>
                    string.Equals(option.Id, payload.Selection, StringComparison.Ordinal)))
            {
                throw new Mesh117PayloadInvalidException(
                    "The resolution selection is absent from the embedded prompt.");
            }
        }
    }

    private static void RequireNonBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new Mesh117PayloadInvalidException($"{name} must be non-blank.");
    }
}
