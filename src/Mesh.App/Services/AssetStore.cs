using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Contract for durable asset (Skill/Knowledge/Widget) storage.
/// Summaries and content are stored separately so that paging never materialises payload
/// bytes. Every method runs its SQLite work off the caller thread via an
/// <see cref="IStoreScheduler"/>, validates its arguments, and delegates conflict resolution
/// to the deterministic policy in <see cref="AssetConflict"/>.
/// </summary>
public interface IAssetStore
{
    /// <summary>
    /// Inserts or updates a local asset. The content hash and byte count are recomputed from
    /// <paramref name="content"/>; a caller-supplied mismatch is rejected. Replicated (non
    /// local-only) mutations are written by the replication journal transaction instead, so this
    /// path carries no outbox of its own.
    /// </summary>
    Task UpsertAsync(
        AssetRecord summary, byte[] content, CancellationToken ct = default);

    /// <summary>Returns a bounded page of summaries (no content). <paramref name="pageSize"/> must be 1..500.</summary>
    Task<IReadOnlyList<AssetRecord>> PageSummariesAsync(
        AssetKind kind, int pageSize, string? afterId, CancellationToken ct = default);

    /// <summary>Loads a single asset summary together with its content bytes.</summary>
    Task<(AssetRecord Summary, byte[] Content)?> GetFullAssetAsync(
        AssetKind kind, string id, CancellationToken ct = default);

    /// <summary>
    /// Tombstones a local asset. Returns the generated tombstone record (existing version + 1,
    /// preserved <see cref="AssetRecord.LocalOnly"/>). Content rows are removed.
    /// </summary>
    Task<AssetRecord> DeleteAsync(
        AssetKind kind, string id, string sourceDeviceId, CancellationToken ct = default);

    /// <summary>
    /// Applies a remote upsert. Returns true when the deterministic comparer accepts the
    /// incoming record over the stored one. Local-only rows reject remote mutation.
    /// </summary>
    Task<bool> ApplyRemoteUpsertAsync(
        AssetRecord summary, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// Applies a remote delete described by a full tombstone record. Returns true when the
    /// deterministic comparer accepts the tombstone. Local-only rows reject remote mutation.
    /// </summary>
    Task<bool> ApplyRemoteDeleteAsync(AssetRecord tombstone, CancellationToken ct = default);

}

/// <summary>
/// SQLite-backed implementation of <see cref="IAssetStore"/>.
/// Defers all SQLite work to an <see cref="IStoreScheduler"/> so nothing touches the database
/// on the caller thread, and validates arguments before delegating to <see cref="MeshDb"/>.
/// </summary>
public sealed class AssetStore(MeshDb db, IStoreScheduler? scheduler = null) : IAssetStore
{
    private const int MaxPage = 500;

    private readonly IStoreScheduler _scheduler = scheduler ?? TaskRunStoreScheduler.Shared;

    public Task UpsertAsync(
        AssetRecord summary, byte[] content, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentNullException.ThrowIfNull(content);
            summary.EnsureValidForUpsert();
            db.ExecuteDurableWrite(() => db.UpsertAsset(summary, content), ct);
        }, ct);

    public Task<IReadOnlyList<AssetRecord>> PageSummariesAsync(
        AssetKind kind, int pageSize, string? afterId, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            RequirePageBound(pageSize, nameof(pageSize));
            return db.PageAssetSummaries(kind, pageSize, afterId);
        }, ct);

    public Task<(AssetRecord Summary, byte[] Content)?> GetFullAssetAsync(
        AssetKind kind, string id, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            RequireNonBlank(id, nameof(id));
            return db.GetFullAsset(kind, id);
        }, ct);

    public Task<AssetRecord> DeleteAsync(
        AssetKind kind, string id, string sourceDeviceId, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            RequireNonBlank(id, nameof(id));
            RequireNonBlank(sourceDeviceId, nameof(sourceDeviceId));
            return db.ExecuteDurableWrite(() => db.DeleteAsset(kind, id, sourceDeviceId), ct);
        }, ct);

    public Task<bool> ApplyRemoteUpsertAsync(
        AssetRecord summary, byte[] content, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentNullException.ThrowIfNull(content);
            summary.EnsureValidForUpsert();
            return db.ExecuteDurableWrite(() => db.ApplyRemoteAssetUpsert(summary, content), ct);
        }, ct);

    public Task<bool> ApplyRemoteDeleteAsync(AssetRecord tombstone, CancellationToken ct = default)
        => _scheduler.RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(tombstone);
            tombstone.EnsureValidTombstone();
            return db.ExecuteDurableWrite(() => db.ApplyRemoteAssetDelete(tombstone), ct);
        }, ct);

    private static void RequireNonBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must be non-blank.", name);
    }

    private static void RequirePageBound(int value, string name)
    {
        if (value is < 1 or > MaxPage)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between 1 and {MaxPage}.");
    }
}
