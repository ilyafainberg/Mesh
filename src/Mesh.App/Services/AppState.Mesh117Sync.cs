using System.Globalization;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>The result of applying an inbound 1.17 asset operation to the local store.</summary>
public enum Mesh117ApplyOutcome
{
    /// <summary>The operation superseded local state and was persisted.</summary>
    Applied,

    /// <summary>The operation was a duplicate or lost the deterministic conflict; nothing changed.</summary>
    Ignored,

    /// <summary>The store was momentarily unavailable (signed out / DB closed). Retry later.</summary>
    Unavailable
}

/// <summary>
/// Pure eligibility rules for 1.17 device-sync routing. Asset routing wraps the desktop-only
/// <see cref="AssetSyncPolicy"/>; ask-user routing reaches every eligible device (mobile included).
/// Platforms come from authoritative linked <see cref="Mesh.Shared.DeviceInfo"/> records, never from names.
/// </summary>
public static class Mesh117Routing
{
    /// <summary>Target device ids that may receive an asset operation from a source on
    /// <paramref name="localPlatform"/>. Empty when the local device is not a desktop.</summary>
    public static IReadOnlyList<string> EligibleAssetTargets(
        string? localPlatform, IEnumerable<Mesh.Shared.DeviceInfo> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new List<string>();
        foreach (var device in targets)
        {
            if (device is null || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
            if (AssetSyncPolicy.IsAllowed(localPlatform, device.Platform, isAssetOperation: true))
                result.Add(device.DeviceId);
        }
        return result;
    }

    /// <summary>Every non-blank target device id: ask-user operations route to all eligible devices,
    /// mobile included.</summary>
    public static IReadOnlyList<string> EligibleAskUserTargets(IEnumerable<Mesh.Shared.DeviceInfo> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new List<string>();
        foreach (var device in targets)
        {
            if (device is null || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
            result.Add(device.DeviceId);
        }
        return result;
    }

    /// <summary>True when a device on <paramref name="localPlatform"/> may accept an asset operation
    /// sent by a device on <paramref name="sourcePlatform"/> (both must be desktop).</summary>
    public static bool CanReceiveAsset(string? localPlatform, string? sourcePlatform)
        => AssetSyncPolicy.IsAllowed(sourcePlatform, localPlatform, isAssetOperation: true);
}

/// <summary>
/// Pure conversions between domain records and 1.17 wire payloads/operations. Operation ids are
/// deterministic so a duplicated logical operation always yields the same id (idempotent), while a
/// newer revision yields a fresh id.
/// </summary>
public static class Mesh117Operations
{
    public static Asset117Payload AssetToPayload(AssetRecord summary, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(content);
        return new Asset117Payload(
            Kind: summary.Kind.ToString(),
            Id: summary.Id,
            Name: summary.Name,
            MetadataJson: summary.MetadataJson,
            ContentMime: summary.ContentMime,
            ContentHash: summary.ContentHash,
            ContentByteCount: summary.ContentByteCount,
            Version: summary.Version,
            SourceDeviceId: summary.SourceDeviceId ?? string.Empty,
            UpdatedAtUnixMs: summary.UpdatedAt.ToUnixTimeMilliseconds(),
            IsDeleted: summary.IsDeleted,
            LocalOnly: summary.LocalOnly,
            ContentBase64: summary.IsDeleted || content.Length == 0
                ? string.Empty
                : Convert.ToBase64String(content));
    }

    public static AssetRecord PayloadToAssetRecord(Asset117Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!Enum.TryParse<AssetKind>(payload.Kind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
            throw new Mesh117PayloadInvalidException($"Unknown asset kind '{payload.Kind}'.");
        return new AssetRecord(
            Kind: kind,
            Id: payload.Id,
            Name: payload.Name,
            MetadataJson: payload.MetadataJson,
            ContentMime: payload.ContentMime,
            ContentHash: payload.ContentHash,
            ContentByteCount: payload.ContentByteCount,
            Version: payload.Version,
            SourceDeviceId: payload.SourceDeviceId,
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(payload.UpdatedAtUnixMs),
            IsDeleted: payload.IsDeleted,
            LocalOnly: payload.LocalOnly);
    }

    /// <summary>Builds the device-sync operation for an asset summary + content. The operation id is
    /// the deterministic <see cref="AssetOperationId"/>; the wire kind reflects upsert vs delete.</summary>
    public static DeviceSyncOperation BuildAssetOperation(
        AssetRecord summary,
        byte[] content,
        string? transportSourceDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var operation = summary.IsDeleted ? "delete" : "upsert";
        var kind = summary.IsDeleted ? Mesh117SyncKinds.AssetDelete : Mesh117SyncKinds.AssetUpsert;
        var operationId = AssetOperationId.Create(
            summary.Kind, summary.Id, operation, summary.Version, summary.SourceDeviceId);
        var payload = AssetToPayload(summary, content);
        return new DeviceSyncOperation(
            OperationId: operationId,
            SourceDeviceId: transportSourceDeviceId ?? summary.SourceDeviceId ?? string.Empty,
            Kind: kind,
            EntityId: AssetEntityId(summary.Kind, summary.Id),
            Version: summary.Version.ToString(CultureInfo.InvariantCulture),
            Payload: JsonSerializer.Serialize(payload, Mesh117Json.Options));
    }

    public static DeviceSyncOperation BuildPromptOperation(AskUser117PromptPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var operationId = StableOperationId(
            Mesh117SyncKinds.AskUserPrompt,
            payload.PromptId,
            payload.Revision.ToString(CultureInfo.InvariantCulture));
        return new DeviceSyncOperation(
            OperationId: operationId,
            SourceDeviceId: payload.OriginDeviceId ?? string.Empty,
            Kind: Mesh117SyncKinds.AskUserPrompt,
            EntityId: payload.PromptId,
            Version: payload.Revision.ToString(CultureInfo.InvariantCulture),
            Payload: JsonSerializer.Serialize(payload, Mesh117Json.Options));
    }

    public static DeviceSyncOperation BuildResolutionOperation(
        AskUser117ResolutionPayload payload, string sourceDeviceId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Stable per (prompt, resolver, token) so a redelivered resolution is idempotent.
        var operationId = StableOperationId(
            Mesh117SyncKinds.AskUserResolution,
            payload.PromptId,
            payload.ResolutionDeviceId,
            payload.IdempotencyToken);
        return new DeviceSyncOperation(
            OperationId: operationId,
            SourceDeviceId: sourceDeviceId,
            Kind: Mesh117SyncKinds.AskUserResolution,
            EntityId: payload.PromptId,
            Version: payload.IdempotencyToken,
            Payload: JsonSerializer.Serialize(payload, Mesh117Json.Options));
    }

    /// <summary>Converts an inbound prompt payload to the domain record, applying counter floors so a
    /// malformed low counter cannot produce an invalid record.</summary>
    public static AskUserPrompt PayloadToAskUserPrompt(AskUser117PromptPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new AskUserPrompt(
            PromptId: payload.PromptId,
            ThreadId: payload.ThreadId,
            RunId: payload.RunId,
            Question: payload.Question,
            Options: payload.Options
                .Select(o => new AskUserOption(o.Id, o.Title, o.Description))
                .ToList(),
            RecommendedIndex: payload.RecommendedIndex,
            State: AskUserState.Pending,
            Selection: null,
            OriginDeviceId: payload.OriginDeviceId,
            ResolutionDeviceId: null,
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(payload.CreatedAtUnixMs),
            ExpiresAt: payload.ExpiresAtUnixMs is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null,
            ResolvedAt: null,
            Revision: payload.Revision < 1 ? 1 : payload.Revision,
            Version: payload.Version < 1 ? 1 : payload.Version);
    }

    public static string AssetEntityId(AssetKind kind, string id)
        => $"{kind}\u001f{id}";

    /// <summary>Deterministic, collision-resistant id delegating to the shared
    /// <see cref="Mesh117OperationId.Stable"/> so ask-user operation ids are idempotent.</summary>
    public static string StableOperationId(string kind, params string[] parts)
        => Mesh117OperationId.Stable(kind, parts);
}

internal static class Mesh117Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed partial class AppState
{
    /// <summary>True when a profile database is open and 1.17 asset stores can run.</summary>
    public bool Mesh117PersistenceAvailable
    {
        get { lock (profileSyncGate) return activeDb is not null; }
    }

    private IAssetStore? BuildAssetStore()
    {
        lock (profileSyncGate)
            return activeDb is null ? null : new AssetStore(activeDb);
    }

    /// <summary>Builds a wire payload (with deep-link) from a stored prompt for broadcasting.</summary>
    public static AskUser117PromptPayload BuildPromptPayload(AskUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return new AskUser117PromptPayload(
            PromptId: prompt.PromptId,
            ThreadId: prompt.ThreadId,
            RunId: prompt.RunId,
            Question: prompt.Question,
            Options: prompt.Options
                .Select(o => new AskUser117Option(o.Id, o.Title, o.Description))
                .ToList(),
            RecommendedIndex: prompt.RecommendedIndex,
            OriginDeviceId: prompt.OriginDeviceId,
            CreatedAtUnixMs: prompt.CreatedAt.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs: prompt.ExpiresAt?.ToUnixTimeMilliseconds(),
            Revision: prompt.Revision,
            Version: prompt.Version,
            DeepLink: Mesh117DeepLink.ForPrompt(prompt.PromptId));
    }

    /// <summary>
    /// Builds a wire resolution payload from a resolved prompt. The idempotency token mirrors the
    /// deterministic <c>promptId:selection</c> token the local resolve path uses, so a redelivered
    /// resolution is idempotent and every device converges on the same winner.
    /// </summary>
    public static AskUser117ResolutionPayload BuildResolutionPayload(AskUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var selection = prompt.Selection ?? string.Empty;
        return new AskUser117ResolutionPayload(
            PromptId: prompt.PromptId,
            Selection: selection,
            ResolutionDeviceId: prompt.ResolutionDeviceId ?? string.Empty,
            IdempotencyToken: prompt.PromptId + ":" + selection,
            ResolvedAtUnixMs: (prompt.ResolvedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            Prompt: BuildPromptPayload(prompt));
    }

    // ----------------------------------------------------------------------
    // Outbound reads (asset outbox drain + snapshot paging).
    // ----------------------------------------------------------------------

    public async Task<IReadOnlyList<AssetOutboxItem>> DequeueAsset117OutboxAsync(
        int max, CancellationToken ct = default)
    {
        var store = BuildAssetStore();
        if (store is null) return Array.Empty<AssetOutboxItem>();
        return await store.DequeueOutboxAsync(max, ct);
    }

    public async Task<IReadOnlyList<AssetOutboxItem>> ListAsset117OutboxAsync(
        int max, CancellationToken ct = default)
    {
        var store = BuildAssetStore();
        if (store is null) return Array.Empty<AssetOutboxItem>();
        return await store.ListOutboxAsync(max, ct);
    }

    public async Task MarkAsset117OutboxAsync(
        string operationId, bool success, string? error, CancellationToken ct = default)
    {
        var store = BuildAssetStore();
        if (store is null) return;
        await store.MarkOutboxAttemptAsync(operationId, success, error, ct);
    }

    public async Task DeadLetterAsset117OutboxAsync(
        string operationId, string error, CancellationToken ct = default)
    {
        var store = BuildAssetStore();
        if (store is null) return;
        await store.DeadLetterOutboxAsync(operationId, error, ct);
    }

    public async Task<(AssetRecord Summary, byte[] Content)?> GetAsset117FullAsync(
        AssetKind kind, string id, CancellationToken ct = default)
    {
        var store = BuildAssetStore();
        if (store is null) return null;
        return await store.GetFullAssetAsync(kind, id, ct);
    }

    public async Task<IReadOnlyList<AssetRecord>> PageAsset117SummariesAsync(
        AssetKind kind, int pageSize, string? afterId, CancellationToken ct = default)
    {
        var store = BuildAssetStore();
        if (store is null) return Array.Empty<AssetRecord>();
        return await store.PageSummariesAsync(kind, Mesh117SnapshotPlanner.ClampPageSize(pageSize), afterId, ct);
    }

    // ----------------------------------------------------------------------
    // Inbound asset apply (persist before ACK). Validation failures throw
    // Mesh117PayloadInvalidException (permanent); a closed store returns
    // Unavailable so the caller can retry. Ask-user inbound uses the sibling
    // ReceiveRemoteAskUserPromptAsync / ReceiveRemoteAskUserResolutionAsync seams.
    // ----------------------------------------------------------------------

    public async Task<Mesh117ApplyOutcome> ApplyRemoteAsset117Async(
        Asset117Payload payload, CancellationToken ct = default)
    {
        var content = Mesh117PayloadGuard.ValidateAsset(payload);
        var record = Mesh117Operations.PayloadToAssetRecord(payload);
        var store = BuildAssetStore();
        if (store is null) return Mesh117ApplyOutcome.Unavailable;
        bool applied;
        if (record.IsDeleted)
            applied = await store.ApplyRemoteDeleteAsync(record, ct);
        else
            applied = await store.ApplyRemoteUpsertAsync(record, content, ct);
        if (applied)
            await RefreshAssetFromStoreAsync(record.Kind, record.Id, ct).ConfigureAwait(false);
        return applied ? Mesh117ApplyOutcome.Applied : Mesh117ApplyOutcome.Ignored;
    }
}
