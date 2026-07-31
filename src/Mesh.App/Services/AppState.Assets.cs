using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Mesh 1.17 asynchronous profile persistence plus SQLCipher-backed capability-asset
/// (Skill/Knowledge/Widget) storage, migration and hydration.
///
/// The in-memory <see cref="MeshProfile.Skills"/>/<see cref="MeshProfile.Knowledge"/>/
/// <see cref="MeshProfile.Widgets"/> collections remain the compatibility surface every page and
/// service already reads, but their content no longer lives in the persisted profile blob (see
/// <see cref="MeshDb.SerializeProfileForStorage"/>). Instead each asset is stored losslessly in the
/// asset tables. On identity load the legacy embedded assets are migrated idempotently, then the
/// in-memory collections are hydrated back from those tables. All subsequent create/update/delete
/// operations flowing through <see cref="Mutate"/> are diffed and persisted through
/// <see cref="IAssetStore"/>, and an <see cref="AssetMutationCreated"/> event is raised for the sync
/// layer. Profile-blob writes are scheduled onto a <see cref="ProfilePersistenceCoordinator{T}"/>
/// worker so the UI thread never performs SQLite work.
/// </summary>
public sealed partial class AppState : IAsyncDisposable
{
    /// <summary>
    /// Raised after a local asset mutation has been made durable. For an upsert the record and its
    /// content bytes are carried; for a delete the tombstone record is carried with null content.
    /// The sync layer uses this to propagate asset changes without re-embedding them in the profile.
    /// </summary>
    public event Action<AssetSyncMutation>? AssetMutationCreated;

    /// <summary>An asset change that is ready to be surfaced to device sync.</summary>
    public sealed record AssetSyncMutation(AssetRecord Record, byte[]? Content);

    /// <summary>How a single asset change must be applied by the persistence worker.</summary>
    private enum AssetWorkOp
    {
        /// <summary>Upsert the summary and the supplied content bytes.</summary>
        Upsert,
        /// <summary>Upsert the summary but preserve the already-stored body (reloaded off-thread).</summary>
        UpsertMetadataOnly,
        /// <summary>Tombstone the asset and drop its content.</summary>
        Delete
    }

    private sealed record AssetWork(
        AssetKind Kind,
        string Id,
        AssetWorkOp Op,
        AssetRecord? Record,
        byte[]? Content,
        bool CreateOutboxEntry,
        string SourceDeviceId);

    /// <summary>The kind of change a caller declares for a single asset, before it is diffed.</summary>
    private enum AssetChange
    {
        /// <summary>The in-memory object carries its full content; upsert body and metadata.</summary>
        Content,
        /// <summary>Only metadata changed; the stored body must be preserved.</summary>
        Metadata,
        /// <summary>The asset was removed.</summary>
        Delete
    }

    private readonly record struct AssetHint(AssetKind Kind, string Id, AssetChange Change);

    /// <summary>How <see cref="MutateCore"/> should derive asset persistence work after a mutation.</summary>
    private enum AssetPlanKind
    {
        /// <summary>No asset work (ordinary profile/setting/contact mutations).</summary>
        None,
        /// <summary>Explicit per-asset hints (single edit, delete, or bounded bulk import).</summary>
        Hints,
        /// <summary>Metadata-only sweep: diff every summary's metadata (circle rename/delete).</summary>
        MetadataSweep
    }

    private sealed record ProfileWork(
        MeshDb? Db,
        string? BlobJson,
        IReadOnlyList<MeshDb.SyncVersionWrite> Versions,
        IReadOnlyList<MeshDb.SyncTombstoneWrite> Tombstones,
        IReadOnlyList<MeshDb.SyncCircleRenameWrite> CircleRenames,
        IReadOnlyList<DeviceSyncOperation> Operations,
        bool WriteAccountIndex,
        string? IndexActiveId,
        IReadOnlyList<AccountRef>? IndexAccounts,
        IReadOnlyList<AssetWork> Assets);

    private ProfilePersistenceCoordinator<long>? persistence;
    private readonly object writeQueueGate = new();
    private readonly LinkedList<ProfileWork> writeQueue = new();
    private readonly object errorGate = new();

    // In-memory monotonic version chaining. Rapid mutations to the same entity must not read a
    // stale committed version (the previous write may still be queued), so issued versions are
    // remembered here and used as an additional floor when creating the next version.
    private readonly Dictionary<string, string> issuedSyncVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<(AssetKind Kind, string Id), int> assetVersions = new();

    // Bounded content cache: on-demand asset bodies are cached here under a combined count + byte
    // budget so repeat reads (agent turns, widget refine, previews) stay warm without ever holding
    // the whole corpus. Eviction only drops the cached copy; the durable body stays in the DB.
    private readonly AssetContentCache assetContentCache = new(maxEntries: 256, maxBytes: 16L * 1024 * 1024);

    private static string CacheKey(AssetKind kind, string id) => (int)kind + "\u001f" + id;

    /// <summary>The message of the last persistence failure, or null once a later write succeeds.</summary>
    public string? LastPersistenceError { get; private set; }

    // ---- coordinator wiring ------------------------------------------------

    private ProfilePersistenceCoordinator<long> EnsurePersistence()
        => persistence ??= new ProfilePersistenceCoordinator<long>(
            DrainWritesAsync, TimeSpan.FromMilliseconds(5));

    private void Enqueue(ProfileWork work)
    {
        var coordinator = EnsurePersistence();
        lock (writeQueueGate) writeQueue.AddLast(work);
        coordinator.Schedule(0);
    }

    /// <summary>
    /// Completes once every persistence write scheduled before the call has been made durable.
    /// Callers await this before switching identity, signing out, importing, exporting or disposing
    /// the database so the background worker never targets a swapped-out connection.
    /// </summary>
    public async Task FlushPersistenceAsync(CancellationToken ct = default)
    {
        var coordinator = persistence;
        if (coordinator is null) return;
        lock (writeQueueGate)
        {
            if (writeQueue.Count > 0)
                coordinator.Schedule(0);
        }
        await coordinator.FlushAsync(ct).ConfigureAwait(false);
    }

    private void FlushBlocking()
    {
        if (persistence is null) return;
        try { FlushPersistenceAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            RecordError(ex);
            throw;
        }
    }

    private async Task DrainWritesAsync(long _, CancellationToken ct)
    {
        while (true)
        {
            ProfileWork work;
            lock (writeQueueGate)
            {
                if (writeQueue.Count == 0) return;
                work = writeQueue.First!.Value;
                writeQueue.RemoveFirst();
            }
            try
            {
                await ProcessWorkAsync(work, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (writeQueueGate) writeQueue.AddFirst(work);
                throw;
            }
            catch (Exception ex)
            {
                lock (writeQueueGate) writeQueue.AddFirst(work);
                RecordError(ex);
                throw;
            }
        }
    }

    private async Task ProcessWorkAsync(ProfileWork work, CancellationToken ct)
    {
        if (work.Db is not null)
        {
            // Assets are made durable first so the (bounded) profile blob is only ever rewritten
            // once every asset it no longer embeds is safely stored.
            if (work.Assets.Count > 0)
            {
                var store = new AssetStore(work.Db);
                foreach (var asset in work.Assets)
                {
                    switch (asset.Op)
                    {
                        case AssetWorkOp.Delete:
                        {
                            var tombstone = await store
                                .DeleteAsync(asset.Kind, asset.Id, asset.SourceDeviceId, asset.CreateOutboxEntry, ct)
                                .ConfigureAwait(false);
                            assetContentCache.Remove(CacheKey(asset.Kind, asset.Id));
                            AssetMutationCreated?.Invoke(new AssetSyncMutation(tombstone, null));
                            break;
                        }
                        case AssetWorkOp.UpsertMetadataOnly:
                        {
                            // Preserve the existing body: fetch the single stored body off the UI
                            // thread and re-upsert it under the new metadata. Never overwrites an
                            // unloaded body with empty content.
                            var existing = await store
                                .GetFullAssetAsync(asset.Kind, asset.Id, ct).ConfigureAwait(false);
                            var body = existing?.Content ?? Array.Empty<byte>();
                            var record = asset.Record! with { ContentHash = null, ContentByteCount = 0 };
                            await store.UpsertAsync(record, body, asset.CreateOutboxEntry, ct)
                                .ConfigureAwait(false);
                            assetContentCache.Set(CacheKey(asset.Kind, asset.Id), body);
                            // The stored row's byte count is recomputed from the preserved body; surface
                            // that same count (not the zeroed placeholder) to the sync layer.
                            AssetMutationCreated?.Invoke(new AssetSyncMutation(
                                record with { ContentByteCount = body.LongLength }, body));
                            break;
                        }
                        default:
                        {
                            await store.UpsertAsync(asset.Record!, asset.Content!, asset.CreateOutboxEntry, ct)
                                .ConfigureAwait(false);
                            assetContentCache.Set(CacheKey(asset.Kind, asset.Id), asset.Content!);
                            AssetMutationCreated?.Invoke(new AssetSyncMutation(asset.Record!, asset.Content));
                            break;
                        }
                    }
                }
            }

            if (work.BlobJson is not null)
            {
                if (work.Versions.Count > 0 || work.Tombstones.Count > 0 || work.CircleRenames.Count > 0)
                {
                    var accepted = work.Db.SaveProfileAndSyncState(
                        work.BlobJson, work.Versions, work.Tombstones, circleRenames: work.CircleRenames);
                    if (!accepted)
                    {
                        throw new InvalidOperationException(
                            "The profile sync transaction was not accepted.");
                    }
                    foreach (var operation in work.Operations)
                        DeviceSyncOperationCreated?.Invoke(operation);
                }
                else
                {
                    work.Db.SaveProfileJson(work.BlobJson);
                }
            }
        }

        if (work.WriteAccountIndex && work.IndexAccounts is not null)
            WriteIndexCore(work.IndexActiveId, work.IndexAccounts);

        ClearError();
    }

    private void RecordError(Exception ex)
    {
        lock (errorGate) LastPersistenceError = ex.Message;
    }

    private void ClearError()
    {
        lock (errorGate) LastPersistenceError = null;
    }

    // ---- profile-blob scheduling ------------------------------------------

    /// <summary>Schedules a bounded profile-blob write for the active identity (no sync state).</summary>
    private void ScheduleProfileSave()
    {
        var db = activeDb;
        if (db is null) return;
        Enqueue(new ProfileWork(
            db,
            MeshDb.SerializeProfileForStorage(Profile),
            Array.Empty<MeshDb.SyncVersionWrite>(),
            Array.Empty<MeshDb.SyncTombstoneWrite>(),
            Array.Empty<MeshDb.SyncCircleRenameWrite>(),
            Array.Empty<DeviceSyncOperation>(),
            WriteAccountIndex: false,
            IndexActiveId: null,
            IndexAccounts: null,
            Array.Empty<AssetWork>()));
    }

    private List<AccountRef> SnapshotAccounts()
        => accounts
            .Select(a => new AccountRef { Id = a.Id, Handle = a.Handle, DisplayName = a.DisplayName })
            .ToList();

    /// <summary>
    /// Schedules the non-sync persistence path (the else branch of a mutation and the public
    /// <c>Save</c>): writes the account index, the bounded profile blob for the active identity, and
    /// any diffed asset changes. Mirrors the previous synchronous <c>Save</c> semantics but off the
    /// UI thread.
    /// </summary>
    private void ScheduleSave(IReadOnlyList<AssetWork> assetWorks)
    {
        string? blobJson = null;
        if (activeId is not null)
        {
            UpdateActiveAccount();
            if (activeDb is not null) blobJson = MeshDb.SerializeProfileForStorage(Profile);
        }
        Enqueue(new ProfileWork(
            activeDb,
            blobJson,
            Array.Empty<MeshDb.SyncVersionWrite>(),
            Array.Empty<MeshDb.SyncTombstoneWrite>(),
            Array.Empty<MeshDb.SyncCircleRenameWrite>(),
            Array.Empty<DeviceSyncOperation>(),
            WriteAccountIndex: true,
            activeId,
            SnapshotAccounts(),
            assetWorks));
    }

    // ---- in-memory version chaining ---------------------------------------

    private string? LatestSyncVersion(string entityKey)
    {
        var committed = activeDb!.GetSyncVersion(entityKey);
        return issuedSyncVersions.TryGetValue("V\u001f" + entityKey, out var issued)
               && string.CompareOrdinal(issued, committed ?? "") > 0
            ? issued
            : committed;
    }

    private string? LatestTombstoneVersion(string kind, string entityId)
    {
        var committed = activeDb!.GetSyncTombstoneVersion(kind, entityId);
        return issuedSyncVersions.TryGetValue("T\u001f" + kind + "\u001f" + entityId, out var issued)
               && string.CompareOrdinal(issued, committed ?? "") > 0
            ? issued
            : committed;
    }

    private void RememberIssuedVersion(string entityKey, string version)
        => issuedSyncVersions["V\u001f" + entityKey] = version;

    private void RememberIssuedTombstone(string kind, string entityId, string version)
        => issuedSyncVersions["T\u001f" + kind + "\u001f" + entityId] = version;

    // ---- asset diffing -----------------------------------------------------
    //
    // Enumerates every in-memory asset as (record, content). Used only by the one-time legacy
    // migration, which legitimately reads embedded content once. The steady-state write path never
    // scans content: it works from explicit per-asset hints or a metadata-only sweep.

    private static IEnumerable<(AssetKind Kind, string Id, AssetRecord Record, byte[] Content)>
        EnumerateAssets(MeshProfile profile, string deviceId, bool localOnly)
    {
        foreach (var skill in profile.Skills)
        {
            var (record, content) = AssetPersistenceModels.ToRecord(skill, deviceId, localOnly, 1);
            yield return (AssetKind.Skill, skill.Id, record, content);
        }
        foreach (var item in profile.Knowledge)
        {
            var (record, content) = AssetPersistenceModels.ToRecord(item, deviceId, localOnly, 1);
            yield return (AssetKind.Knowledge, item.Id, record, content);
        }
        foreach (var widget in profile.Widgets)
        {
            var (record, content) = AssetPersistenceModels.ToRecord(widget, deviceId, localOnly, 1);
            yield return (AssetKind.Widget, widget.Id, record, content);
        }
    }

    /// <summary>Builds the (record, content) pair for a single in-memory asset by id.</summary>
    private (AssetRecord Record, byte[] Content)? BuildAssetRecord(
        AssetKind kind, string id, string deviceId, bool localOnly)
    {
        switch (kind)
        {
            case AssetKind.Skill:
                var skill = Profile.Skills.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
                return skill is null ? null : AssetPersistenceModels.ToRecord(skill, deviceId, localOnly, 1);
            case AssetKind.Knowledge:
                var item = Profile.Knowledge.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));
                return item is null ? null : AssetPersistenceModels.ToRecord(item, deviceId, localOnly, 1);
            case AssetKind.Widget:
                var widget = Profile.Widgets.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
                return widget is null ? null : AssetPersistenceModels.ToRecord(widget, deviceId, localOnly, 1);
            default:
                return null;
        }
    }

    /// <summary>Turns explicit per-asset change hints into persistence work without scanning content.</summary>
    private IReadOnlyList<AssetWork> BuildHintedAssetWorks(
        IReadOnlyList<AssetHint> hints, string deviceId, bool localOnly)
    {
        var works = new List<AssetWork>(hints.Count);
        foreach (var hint in hints)
        {
            if (hint.Change == AssetChange.Delete)
            {
                BumpAssetVersion((hint.Kind, hint.Id));
                works.Add(new AssetWork(
                    hint.Kind, hint.Id, AssetWorkOp.Delete, null, null,
                    CreateOutboxEntry: !localOnly, deviceId));
                continue;
            }

            var built = BuildAssetRecord(hint.Kind, hint.Id, deviceId, localOnly);
            if (built is null) continue;
            var version = NextAssetVersion(hint.Kind, hint.Id);
            var op = hint.Change == AssetChange.Metadata
                ? AssetWorkOp.UpsertMetadataOnly
                : AssetWorkOp.Upsert;
            works.Add(new AssetWork(
                hint.Kind, hint.Id, op, built.Value.Record with { Version = version },
                built.Value.Content, CreateOutboxEntry: !localOnly, deviceId));
        }
        return works;
    }

    // ---- metadata-only sweep (circle rename/delete) ------------------------
    //
    // A metadata signature covers only the display name and the metadata JSON, never the body, so a
    // mass visibility rewrite can be diffed in bounded memory. Changed rows become metadata-only
    // upserts (bodies preserved off-thread) and removed rows become deletes.

    private static string AssetMetadataSignature(AssetRecord record)
        => record.Name + "\u001f" + (record.MetadataJson ?? "");

    private Dictionary<(AssetKind, string), string> SnapshotAssetMetadata()
    {
        var map = new Dictionary<(AssetKind, string), string>();
        foreach (var (kind, id, record, _) in EnumerateAssets(Profile, "snapshot", false))
            map[(kind, id)] = AssetMetadataSignature(record);
        return map;
    }

    private IReadOnlyList<AssetWork> DiffAssetMetadata(
        Dictionary<(AssetKind, string), string> before, string deviceId, bool localOnly)
    {
        var works = new List<AssetWork>();
        var afterKeys = new HashSet<(AssetKind, string)>();
        foreach (var (kind, id, record, content) in EnumerateAssets(Profile, deviceId, localOnly))
        {
            afterKeys.Add((kind, id));
            var signature = AssetMetadataSignature(record);
            var known = before.TryGetValue((kind, id), out var previous);
            if (known && previous == signature) continue;
            var version = NextAssetVersion(kind, id);
            // Existing rows keep their stored body (metadata-only); a genuinely new row upserts its
            // materialised content.
            var op = known ? AssetWorkOp.UpsertMetadataOnly : AssetWorkOp.Upsert;
            works.Add(new AssetWork(
                kind, id, op, record with { Version = version }, content,
                CreateOutboxEntry: !localOnly, deviceId));
        }
        foreach (var key in before.Keys)
        {
            if (afterKeys.Contains(key)) continue;
            BumpAssetVersion(key);
            works.Add(new AssetWork(
                key.Item1, key.Item2, AssetWorkOp.Delete, null, null,
                CreateOutboxEntry: !localOnly, deviceId));
        }
        return works;
    }

    /// <summary>
    /// Dispatches to the correct asset-work builder for the requested plan. Returns no work when the
    /// device is unknown (identity not ready) or no asset tracking was requested.
    /// </summary>
    private IReadOnlyList<AssetWork> BuildAssetWorks(
        AssetPlanKind plan,
        IReadOnlyList<AssetHint>? hints,
        Dictionary<(AssetKind, string), string>? beforeMeta,
        string? deviceId,
        bool localOnly)
    {
        if (deviceId is null) return Array.Empty<AssetWork>();
        return plan switch
        {
            AssetPlanKind.Hints when hints is { Count: > 0 }
                => BuildHintedAssetWorks(hints, deviceId, localOnly),
            AssetPlanKind.MetadataSweep when beforeMeta is not null
                => DiffAssetMetadata(beforeMeta, deviceId, localOnly),
            _ => Array.Empty<AssetWork>(),
        };
    }

    private int NextAssetVersion(AssetKind kind, string id)
    {
        var key = (kind, id);
        var next = (assetVersions.TryGetValue(key, out var current) ? current : 0) + 1;
        assetVersions[key] = next;
        return next;
    }

    private void BumpAssetVersion((AssetKind, string) key)
        => assetVersions[key] = (assetVersions.TryGetValue(key, out var current) ? current : 0) + 1;

    // ---- migration + hydration --------------------------------------------

    private void ResetAssetState()
    {
        assetVersions.Clear();
        issuedSyncVersions.Clear();
        assetContentCache.Clear();
    }

    /// <summary>
    /// Idempotently migrates any legacy embedded assets on the just-loaded profile into the asset
    /// tables, rewrites the now-bounded profile blob only after every asset is durable, then
    /// hydrates the in-memory collections from the tables. A failure mid-migration leaves the legacy
    /// blob intact so a later open retries safely, and the in-memory collections keep working.
    /// </summary>
    private void MigrateAndHydrateAssets(MeshDb db)
    {
        ResetAssetState();
        var deviceId = LocalDeviceId();
        if (deviceId is null) return;
        var localOnly = PlatformCaps.IsMobile;
        var hadLegacy = Profile.Skills.Count > 0 || Profile.Knowledge.Count > 0 || Profile.Widgets.Count > 0;
        try
        {
            foreach (var (kind, id, record, content) in EnumerateAssets(Profile, deviceId, localOnly))
                if (db.GetFullAsset(kind, id) is null)
                    db.UpsertAsset(record, content, createOutboxEntry: !localOnly);
            if (hadLegacy) db.SaveProfile(Profile);
            HydrateFromAssets(db);
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private void HydrateFromAssets(MeshDb db)
    {
        Profile.Skills = LoadAssetSummaries(db, AssetKind.Skill, AssetPersistenceModels.ToSkillSummary);
        Profile.Knowledge = LoadAssetSummaries(db, AssetKind.Knowledge, AssetPersistenceModels.ToKnowledgeSummary);
        Profile.Widgets = LoadAssetSummaries(db, AssetKind.Widget, AssetPersistenceModels.ToWidgetSummary);
    }

    /// <summary>
    /// Refreshes exactly one in-memory summary (and its cached body) after a remote asset mutation,
    /// leaving the rest of the catalog untouched. The compatibility collection holds metadata only;
    /// the body is placed in the bounded content cache so a subsequent on-demand read is warm.
    /// </summary>
    public async Task RefreshAssetFromStoreAsync(
        AssetKind kind, string id, CancellationToken ct = default)
    {
        MeshDb? db;
        lock (profileSyncGate) db = activeDb;
        if (db is null) return;

        var full = await new AssetStore(db).GetFullAssetAsync(kind, id, ct).ConfigureAwait(false);
        lock (profileSyncGate)
        {
            var alive = full is { Summary.IsDeleted: false };
            switch (kind)
            {
                case AssetKind.Skill:
                    Profile.Skills.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                    if (alive)
                        Profile.Skills.Add(AssetPersistenceModels.ToSkillSummary(full!.Value.Summary));
                    break;
                case AssetKind.Knowledge:
                    Profile.Knowledge.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                    if (alive)
                        Profile.Knowledge.Add(AssetPersistenceModels.ToKnowledgeSummary(full!.Value.Summary));
                    break;
                case AssetKind.Widget:
                    Profile.Widgets.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                    if (alive)
                        Profile.Widgets.Add(AssetPersistenceModels.ToWidgetSummary(full!.Value.Summary));
                    break;
            }

            if (alive)
                assetContentCache.Set(CacheKey(kind, id), full!.Value.Content);
            else
                assetContentCache.Remove(CacheKey(kind, id));

            if (full is { } current)
                assetVersions[(kind, id)] = current.Summary.Version;
        }
        NotifyChanged();
    }

    private List<T> LoadAssetSummaries<T>(MeshDb db, AssetKind kind, Func<AssetRecord, T> map)
    {
        const int pageSize = 500;
        var list = new List<T>();
        string? afterId = null;
        while (true)
        {
            // Summaries only: paging never materialises payload bytes, so startup memory stays
            // O(1) in total content size regardless of how many assets exist.
            var page = db.PageAssetSummaries(kind, pageSize, afterId);
            if (page.Count == 0) break;
            foreach (var summary in page)
            {
                afterId = summary.Id;
                assetVersions[(kind, summary.Id)] = summary.Version;
                if (summary.IsDeleted) continue;
                list.Add(map(summary));
            }
            if (page.Count < pageSize) break;
        }
        return list;
    }

    // ---- on-demand body loading -------------------------------------------

    /// <summary>
    /// Loads a single asset body on demand, cache-first. Returns null when the asset is absent or
    /// deleted. Only ever materialises one body; the result is added to the bounded content cache.
    /// </summary>
    public async Task<byte[]?> LoadAssetContentAsync(
        AssetKind kind, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var key = CacheKey(kind, id);
        if (assetContentCache.TryGet(key, out var cached)) return cached;

        MeshDb? db;
        lock (profileSyncGate) db = activeDb;
        if (db is null) return null;

        var full = await new AssetStore(db).GetFullAssetAsync(kind, id, ct).ConfigureAwait(false);
        if (full is not { Summary.IsDeleted: false } alive) return null;
        assetContentCache.Set(key, alive.Content);
        return alive.Content;
    }

    private async Task<(AssetRecord Summary, byte[] Content)?> LoadFullAssetAsync(
        AssetKind kind, string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return null;
        MeshDb? db;
        lock (profileSyncGate) db = activeDb;
        if (db is null) return null;

        var full = await new AssetStore(db).GetFullAssetAsync(kind, id, ct).ConfigureAwait(false);
        if (full is not { Summary.IsDeleted: false } alive) return null;
        assetContentCache.Set(CacheKey(kind, id), alive.Content);
        return alive;
    }

    /// <summary>Loads one fully-hydrated skill (metadata + instructions) on demand.</summary>
    public async Task<Skill?> LoadFullSkillAsync(string id, CancellationToken ct = default)
    {
        var full = await LoadFullAssetAsync(AssetKind.Skill, id, ct).ConfigureAwait(false);
        return full is null ? null : AssetPersistenceModels.ToSkill(full.Value.Summary, full.Value.Content);
    }

    /// <summary>Loads one fully-hydrated knowledge item (metadata + content) on demand.</summary>
    public async Task<KnowledgeItem?> LoadFullKnowledgeAsync(string id, CancellationToken ct = default)
    {
        var full = await LoadFullAssetAsync(AssetKind.Knowledge, id, ct).ConfigureAwait(false);
        return full is null ? null : AssetPersistenceModels.ToKnowledge(full.Value.Summary, full.Value.Content);
    }

    /// <summary>Loads one fully-hydrated widget (metadata + current/previous HTML) on demand.</summary>
    public async Task<Widget?> LoadFullWidgetAsync(string id, CancellationToken ct = default)
    {
        var full = await LoadFullAssetAsync(AssetKind.Widget, id, ct).ConfigureAwait(false);
        return full is null ? null : AssetPersistenceModels.ToWidget(full.Value.Summary, full.Value.Content);
    }

    private static long Utf8Size(string? text)
        => string.IsNullOrEmpty(text) ? 0 : System.Text.Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// Loads a bounded set of fully-hydrated skills for the given ids. Deduplicates ids, stops
    /// before the count or byte budget would be exceeded, and never loads the whole corpus. An
    /// item that would push the batch past the byte budget is not returned - not even alone.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for a non-positive budget.
    /// </summary>
    public async Task<IReadOnlyList<Skill>> LoadSkillsAsync(
        IEnumerable<string> ids, AssetLoadBudget budget, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var accumulator = new BoundedAssetAccumulator(budget);
        var result = new List<Skill>();
        foreach (var id in ids)
        {
            if (accumulator.IsFull) break;
            if (!accumulator.ShouldLoad(id)) continue;
            var skill = await LoadFullSkillAsync(id, ct).ConfigureAwait(false);
            if (skill is null) continue;
            if (!accumulator.TryAccept(Utf8Size(skill.Instructions))) break;
            result.Add(skill);
        }
        return result;
    }

    /// <summary>Loads a bounded set of fully-hydrated knowledge items for the given ids (see <see cref="LoadSkillsAsync"/>).</summary>
    public async Task<IReadOnlyList<KnowledgeItem>> LoadKnowledgeAsync(
        IEnumerable<string> ids, AssetLoadBudget budget, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var accumulator = new BoundedAssetAccumulator(budget);
        var result = new List<KnowledgeItem>();
        foreach (var id in ids)
        {
            if (accumulator.IsFull) break;
            if (!accumulator.ShouldLoad(id)) continue;
            var item = await LoadFullKnowledgeAsync(id, ct).ConfigureAwait(false);
            if (item is null) continue;
            if (!accumulator.TryAccept(Utf8Size(item.Content))) break;
            result.Add(item);
        }
        return result;
    }

    /// <summary>Loads a bounded set of fully-hydrated widgets for the given ids (see <see cref="LoadSkillsAsync"/>).</summary>
    public async Task<IReadOnlyList<Widget>> LoadWidgetsAsync(
        IEnumerable<string> ids, AssetLoadBudget budget, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var accumulator = new BoundedAssetAccumulator(budget);
        var result = new List<Widget>();
        foreach (var id in ids)
        {
            if (accumulator.IsFull) break;
            if (!accumulator.ShouldLoad(id)) continue;
            var widget = await LoadFullWidgetAsync(id, ct).ConfigureAwait(false);
            if (widget is null) continue;
            if (!accumulator.TryAccept(Utf8Size(widget.Html))) break;
            result.Add(widget);
        }
        return result;
    }

    /// <summary>
    /// Rebuilds a detached profile with complete asset bodies for portable export. Runtime state
    /// remains summary-only; this explicit backup path may perform the full paged read.
    /// </summary>
    private MeshProfile BuildFullProfileForExport(MeshDb db)
    {
        var export = CloneProfile(Profile);
        export.Skills = LoadFullAssetList(db, AssetKind.Skill, AssetPersistenceModels.ToSkill);
        export.Knowledge = LoadFullAssetList(
            db, AssetKind.Knowledge, AssetPersistenceModels.ToKnowledge);
        export.Widgets = LoadFullAssetList(db, AssetKind.Widget, AssetPersistenceModels.ToWidget);
        return export;
    }

    private static List<T> LoadFullAssetList<T>(
        MeshDb db,
        AssetKind kind,
        Func<AssetRecord, byte[], T> map)
    {
        const int pageSize = 500;
        var result = new List<T>();
        string? afterId = null;
        while (true)
        {
            var page = db.PageAssetSummaries(kind, pageSize, afterId);
            if (page.Count == 0) break;
            foreach (var summary in page)
            {
                afterId = summary.Id;
                if (summary.IsDeleted) continue;
                var full = db.GetFullAsset(kind, summary.Id);
                if (full is { } asset)
                    result.Add(map(asset.Summary, asset.Content));
            }
            if (page.Count < pageSize) break;
        }
        return result;
    }

    /// <summary>Flushes pending writes and disposes the persistence coordinator.</summary>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try
        {
            await FlushPersistenceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordError(ex);
            failure = ex;
        }
        finally
        {
            if (persistence is not null)
            {
                try { await persistence.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    RecordError(ex);
                    failure ??= ex;
                }
            }
            activeDb?.Dispose();
        }

        if (failure is not null)
            throw new InvalidOperationException("App state persistence failed during disposal.", failure);
    }
}
