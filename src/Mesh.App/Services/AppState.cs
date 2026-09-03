using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

internal sealed record GeneratedReplicationEvent(
    string Kind,
    ReplicationPayloadCodec.DomainAction Action,
    string EntityId,
    string BodyJson);

internal interface IReplicationEventTestFaultScheduler
{
    bool Schedule(GeneratedReplicationEvent generated, Action persist);
}

/// <summary>A saved identity on this device (one Mesh handle + its own encrypted database).</summary>
public sealed class AccountRef
{
    public string Id { get; set; } = "";
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Handle : DisplayName;
}
/// <summary>
/// Central in-memory + on-disk store of the user's profile. Singleton.
/// Raises <see cref="Changed"/> whenever state mutates so UI can refresh.
///
/// Each identity owns a single encrypted SQLCipher database (<c>identity-{id}.meshdb</c>) holding
/// everything tied to that user: keys, config, contacts, and the full chat history (as scalable
/// append-only rows). A small device-level index (<c>accounts.json</c>) tracks which identities
/// live on this device and which one is active. Signing out just clears the active pointer; the
/// databases are kept so the user can switch back. No data leaves the device except through an
/// explicit passphrase-encrypted export (see <see cref="MeshExport"/>).
/// </summary>
public sealed partial class AppState :
    IMemoryState,
    ITopicDurabilityStore,
    ITopicRequestOutboxStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ReplicationJson = new(JsonSerializerDefaults.Web);

    private sealed class AccountIndex
    {
        public string? ActiveId { get; set; }
        public List<AccountRef> Accounts { get; set; } = new();
    }

    private readonly ISecretStore secrets;
    private readonly TimeProvider timeProvider;
    private readonly StoragePathSet storagePaths;
    private readonly string dir;
    private readonly string indexPath;
    private readonly object profileSyncGate = new();
    private string? activeId;
    private List<AccountRef> accounts = new();
    private MeshDb? activeDb;
    private sealed record ActiveDatabaseIdentity(
        MeshDb Database,
        string AccountId,
        string Identity,
        long Generation);
    private ActiveDatabaseIdentity? activeDatabaseIdentity;
    private long activeDatabaseGeneration;
    private readonly Dictionary<string, TopicSendRetryAuthorization>
        issuedTopicSendAuthorizations = new(StringComparer.Ordinal);
    private ComposerDraftPersistenceCoordinator? draftPersistence;
    private DesktopSelectionPersistenceCoordinator? desktopSelectionPersistence;
    private bool applyingReplicationProjection;
    internal IReplicationEventTestFaultScheduler? ReplicationEventTestFaultScheduler { get; set; }

    public MeshProfile Profile { get; private set; } = new();
    /// <summary>OwnThreads sorted by pin (pinned first), then activity (newest), then created (newest), then stable id.</summary>
    public IReadOnlyList<OwnThread> OrderedOwnThreads
        => OwnThreadOrdering.ByActivity(Profile.OwnThreads).ToList();

    /// <summary>Conversations sorted by pin (pinned first), then activity (newest), then created (newest), then stable handle.</summary>
    public IReadOnlyList<Conversation> OrderedConversations
        => ConversationOrdering.ByActivity(Profile.Conversations).ToList();

    public event Action? Changed;

    // Handles with unread inbound person-messages (in-memory, cleared when the conversation is viewed).
    private readonly HashSet<string> unread = new(StringComparer.OrdinalIgnoreCase);
    public DeepLink.Parsed? PendingPairingLink { get; private set; }
    public long PairingLinkGeneration { get; private set; }

    public void SetPendingPairingLink(DeepLink.Parsed link)
    {
        PendingPairingLink = link.Kind == DeepLink.Kind.Pairing ? link : null;
        PairingLinkGeneration++;
        NotifyChanged();
    }

    public DeepLink.Parsed? ConsumePendingPairingLink()
    {
        var link = PendingPairingLink;
        PendingPairingLink = null;
        return link;
    }

    public AppState(
        ISecretStore secrets,
        TimeProvider? timeProvider = null,
        StoragePathSet? storagePaths = null)
    {
        this.secrets = secrets;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.storagePaths = storagePaths
                            ?? new StoragePathSet(StoragePaths.Root);
        // Directory is owned by StoragePaths, the single source of truth shared with SecretStore.
        // It resolves to a stable, app-identity-independent root on Windows (%LOCALAPPDATA%\Mesh\Data),
        // still honoring the MESH_PROFILE_DIR override used for isolated test instances.
        dir = this.storagePaths.DataDir;
        Directory.CreateDirectory(dir);
        indexPath = Path.Combine(dir, "accounts.json");
        StorageProtection.TryEnsureBackgroundReadable(indexPath);
        Load();
    }

    public bool IsOnboarded => activeId is not null && Profile.IsOnboarded;

    /// <summary>All identities saved on this device.</summary>
    public IReadOnlyList<AccountRef> Accounts => accounts;
    public string? ActiveAccountId => activeId;
    internal string StorageRoot => storagePaths.Root;
    internal string? ActiveDatabasePath => activeId is null ? null : DbPath(activeId);
    public bool HasSavedAccounts => accounts.Count > 0;

    private string DbPath(string id) => Path.Combine(dir, $"identity-{id}.meshdb");

    private MeshDb OpenDb(string id)
    {
        var path = DbPath(id);
        var key = secrets.GetDbKey(id);
        if (key is null)
        {
            if (File.Exists(path))
                throw new InvalidOperationException("The database key is unavailable for an existing identity.");
            key = secrets.GetOrCreateDbKey(id);
        }
        return MeshDb.Open(path, key, timeProvider);
    }

    private ActiveDatabaseIdentity NewActiveDatabaseIdentity(MeshDb database, string accountId)
    {
        issuedTopicSendAuthorizations.Clear();
        return new(
            database,
            accountId,
            TopicSendSnapshot.StableId(
                "account-database",
                Path.GetFullPath(DbPath(accountId)).ToUpperInvariant()),
            Interlocked.Increment(ref activeDatabaseGeneration));
    }

    internal bool TryConsumeTopicSendAuthorization(
        TopicSendAuthorizationScope expected,
        Func<bool> consume)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(consume);
        lock (profileSyncGate)
        {
            var current = Volatile.Read(ref activeDatabaseIdentity);
            return current is not null
                   && string.Equals(current.AccountId, expected.AccountId, StringComparison.Ordinal)
                   && string.Equals(current.Identity, expected.DatabaseIdentity, StringComparison.Ordinal)
                   && current.Generation == expected.DatabaseGeneration
                   && consume();
        }
    }

    internal bool IsCurrentTopicSendAuthorization(TopicSendAuthorizationScope expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (profileSyncGate)
        {
            var current = Volatile.Read(ref activeDatabaseIdentity);
            return current is not null
                   && string.Equals(current.AccountId, expected.AccountId, StringComparison.Ordinal)
                   && string.Equals(current.Identity, expected.DatabaseIdentity, StringComparison.Ordinal)
                   && current.Generation == expected.DatabaseGeneration;
        }
    }

    public void Load()
    {
        var started = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(indexPath))
            {
                Profile = new MeshProfile();
                RecordStartupTiming("empty", started.Elapsed);
                return;
            }

            var idx = JsonSerializer.Deserialize<AccountIndex>(File.ReadAllText(indexPath), JsonOpts) ?? new AccountIndex();
            accounts = idx.Accounts ?? new();
            activeId = MeshProcessContext.PreferredAccountId ?? idx.ActiveId;

            if (activeId is not null)
            {
                var phase = Stopwatch.StartNew();
                var db = OpenDb(activeId);
                RecordStartupTiming("database-open", phase.Elapsed);
                phase.Restart();
                var loaded = db.LoadProfile();
                RecordStartupTiming("profile-load", phase.Elapsed);
                if (loaded is not null)
                {
                    activeDb = db;
                    Volatile.Write(
                        ref activeDatabaseIdentity,
                        NewActiveDatabaseIdentity(db, activeId));
                    Profile = loaded;
                    phase.Restart();
                    ReconcileDeletedCircles();
                    RehydrateUnread();
                    RehydrateTopicExecutionState();
                    MigrateAndHydrateAssets(activeDb);
                    RecordStartupTiming("rehydrate", phase.Elapsed);
                    RecordStartupTiming("complete", started.Elapsed);
                    return;
                }
                db.Dispose();
                activeId = null; // active database missing/empty, land on the picker
            }
            Profile = new MeshProfile();
            RecordStartupTiming("complete", started.Elapsed);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("account-load", ex);
            Profile = new MeshProfile();
            activeId = null;
            activeDb = null;
            Volatile.Write(ref activeDatabaseIdentity, null);
            RecordStartupTiming("failed", started.Elapsed);
        }
    }

    private static void RecordStartupTiming(string phase, TimeSpan elapsed)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "startup",
            $"app-state.{phase};duration_ms={elapsed.TotalMilliseconds:0};thread={Environment.CurrentManagedThreadId}");

    // Restore the in-memory unread set from the persisted profile (survives restarts).
    private void RehydrateUnread()
    {
        unread.Clear();
        foreach (var h in Profile.UnreadFrom) unread.Add(Norm(h));
    }

    private void ReconcileDeletedCircles()
    {
        var active = Profile.Circles
            .Where(circle => !string.IsNullOrWhiteSpace(circle.Name))
            .GroupBy(circle => CircleEntityId(circle.Name), StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        if (active.Count == Profile.Circles.Count) return;
        Profile.Circles = active;
        activeDb?.ExecuteDurableWrite(
            () => activeDb.SaveProfileJson(MeshDb.SerializeProfileForStorage(Profile)));
    }

    private void WriteIndex()
    {
        try
        {
            WriteIndexCore(activeId, accounts);
        }
        catch { /* best-effort */ }
    }

    private void WriteIndexCore(string? id, IReadOnlyList<AccountRef> accts)
    {
        MeshAccountIndexWriter.WriteAtomic(
            indexPath,
            JsonSerializer.Serialize(new AccountIndex { ActiveId = id, Accounts = accts.ToList() }, JsonOpts));
        StorageProtection.TryEnsureBackgroundReadable(indexPath);
    }

    private static string NewId() => Guid.NewGuid().ToString("n");

    public void Save()
    {
        PrepareProfileStorage();
        ScheduleSave(Array.Empty<AssetWork>());
    }

    /// <summary>Gets the local unsent text for a conversation.</summary>
    public string GetConversationDraft(string handle)
        => GetConversationDraftState(handle)?.Text ?? "";

    public MeshDb.ComposerDraft? GetConversationDraftState(string handle)
    {
        var normalized = Norm(handle);
        var db = activeDb;
        if (normalized.Length == 0 || db is null) return null;
        return draftPersistence?.TryGetLatestState(
            db,
            ComposerDraftKind.Conversation,
            normalized,
            out MeshDb.ComposerDraft? latest) == true
            ? latest
            : db.GetConversationDraftState(normalized);
    }

    /// <summary>Persists local unsent text for a conversation without syncing it to other devices.</summary>
    public void SetConversationDraft(string handle, string text)
        => _ = SetConversationDraftRevision(handle, text);

    public long SetConversationDraftRevision(string handle, string text)
    {
        var normalized = Norm(handle);
        var db = activeDb;
        if (normalized.Length == 0 || db is null) return 0;
        var revision = ComposerDraftRevision.New();
        EnsureDraftPersistence().Schedule(
            db,
            ComposerDraftKind.Conversation,
            normalized,
            text,
            revision);
        return revision;
    }

    public string? GetConversationDraftError(string handle)
    {
        var normalized = Norm(handle);
        var db = activeDb;
        return normalized.Length == 0 || db is null
            ? null
            : draftPersistence?.GetFailure(
                db,
                ComposerDraftKind.Conversation,
                normalized)?.Message;
    }

    public void RetryConversationDraft(string handle)
    {
        var normalized = Norm(handle);
        var db = activeDb;
        if (normalized.Length == 0 || db is null) return;
        draftPersistence?.Retry(db, ComposerDraftKind.Conversation, normalized);
    }

    /// <summary>Gets the local unsent text for a topic.</summary>
    public string GetTopicDraft(string threadId)
        => GetTopicDraftState(threadId)?.Text ?? "";

    public MeshDb.ComposerDraft? GetTopicDraftState(string threadId)
    {
        var db = activeDb;
        if (string.IsNullOrWhiteSpace(threadId) || db is null) return null;
        return draftPersistence?.TryGetLatestState(
            db,
            ComposerDraftKind.Topic,
            threadId,
            out MeshDb.ComposerDraft? latest) == true
            ? latest
            : db.GetTopicDraftState(threadId);
    }

    /// <summary>Persists local unsent text for a topic without syncing it to other devices.</summary>
    public void SetTopicDraft(string threadId, string text)
        => _ = SetTopicDraftRevision(threadId, text);

    public long SetTopicDraftRevision(string threadId, string text)
        => SetTopicDraftSnapshotRevision(
            threadId,
            MeshDb.TopicComposerSnapshot.TextOnly(text));

    public long SetTopicDraftSnapshotRevision(
        string threadId,
        MeshDb.TopicComposerSnapshot snapshot)
    {
        var db = activeDb;
        if (string.IsNullOrWhiteSpace(threadId) || db is null) return 0;
        ArgumentNullException.ThrowIfNull(snapshot);
        var revision = ComposerDraftRevision.New();
        EnsureDraftPersistence().ScheduleTopicSnapshot(
            db,
            threadId,
            snapshot,
            revision);
        return revision;
    }

    public async Task<MeshDb.ComposerDraft> PersistTopicDraftSnapshotAsync(
        string threadId,
        MeshDb.TopicComposerSnapshot snapshot,
        long revision,
        CancellationToken cancellationToken = default)
    {
        var db = activeDb ?? throw new InvalidOperationException("No active profile database.");
        if (string.IsNullOrWhiteSpace(threadId))
            throw new ArgumentException("A thread id is required.", nameof(threadId));
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = snapshot with { Attachments = snapshot.Attachments.ToArray() };
        var current = GetTopicDraftState(threadId);
        if (revision <= 0
            || current is not null
            && current.Revision == revision
            && (current.TopicSnapshot is null
                || !MeshDb.TopicComposerSnapshotsEqual(
                    current.TopicSnapshot,
                    snapshot)))
            revision = ComposerDraftRevision.New();

        var persistence = EnsureDraftPersistence();
        ComposerDraftMutationResult result;
        try
        {
            result = await ComposerDraftPersistenceCoordinator.ScheduleAndAwaitAsync(
                    ComposerDraftKind.Topic,
                    threadId,
                    snapshot.Text,
                    () => persistence.ScheduleTopicSnapshot(db, threadId, snapshot, revision),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            throw new InvalidOperationException(
                $"Draft revision {RevisionHash(revision)} could not be durably committed.",
                exception);
        }
        if (result == ComposerDraftMutationResult.Superseded)
            throw new InvalidOperationException(
                $"Draft revision {RevisionHash(revision)} was superseded before it could be committed.");
        var stored = db.GetTopicDraftState(threadId);
        if (stored is null
            || stored.IsMalformed
            || stored.Revision != revision
            || stored.TopicSnapshot is null
            || !MeshDb.TopicComposerSnapshotsEqual(stored.TopicSnapshot, snapshot))
            throw new InvalidOperationException(
                $"Draft revision {RevisionHash(revision)} was not durably committed.");
        return stored;
    }

    public async Task FlushTopicDraftAsync(
        string threadId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var draft = GetTopicDraftState(threadId)
                    ?? throw new InvalidOperationException("The topic draft is missing.");
        if (draft.IsMalformed
            || draft.Revision != expectedRevision
            || draft.TopicSnapshot is null)
            throw new InvalidOperationException("The draft has no valid complete snapshot.");
        _ = await PersistTopicDraftSnapshotAsync(
                threadId,
                draft.TopicSnapshot,
                expectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MeshDb.ComposerDraftClearResult> CompareAndClearTopicDraftAsync(
        string threadId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var db = activeDb ?? throw new InvalidOperationException("No active profile database.");
        if (string.IsNullOrWhiteSpace(threadId))
            throw new ArgumentException("A thread id is required.", nameof(threadId));
        if (expectedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        return draftPersistence is null
            ? await db.ResolveTopicDraftCleanupAsync(
                    threadId,
                    expectedRevision,
                    null,
                    cancellationToken)
                .ConfigureAwait(false)
            : await draftPersistence.ResolveTopicCleanupAsync(
                    db,
                    threadId,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private static string RevisionHash(long revision)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                revision.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            [..12];

    public string? GetTopicDraftError(string threadId)
    {
        var db = activeDb;
        return string.IsNullOrWhiteSpace(threadId) || db is null
            ? null
            : draftPersistence?.GetFailure(
                db,
                ComposerDraftKind.Topic,
                threadId)?.Message;
    }

    public void RetryTopicDraft(string threadId)
    {
        var db = activeDb;
        if (string.IsNullOrWhiteSpace(threadId) || db is null) return;
        draftPersistence?.Retry(db, ComposerDraftKind.Topic, threadId);
    }

    private ComposerDraftPersistenceCoordinator EnsureDraftPersistence()
        => draftPersistence ??= new ComposerDraftPersistenceCoordinator(failure =>
        {
            if (failure is not null)
                RuntimeDiagnostics.Current?.RecordException(
                    "composer-draft-persistence",
                    failure.Exception);
            NotifyChanged();
        });

    /// <summary>Gets the last Me topic opened in the desktop UI on this device.</summary>
    public string? GetLastDesktopTopicId()
        => activeDb?.GetLastDesktopTopicId();

    /// <summary>Stages the last Me topic immediately and persists it off the UI dispatcher.</summary>
    public void SetLastDesktopTopicId(string? threadId)
    {
        var db = activeDb;
        if (db is null) return;
        EnsureDesktopSelectionPersistence().SetTopic(db, threadId);
    }

    /// <summary>Gets the last Messages conversation opened in the desktop UI on this device.</summary>
    public string? GetLastDesktopConversationKey()
        => activeDb?.GetLastDesktopConversationKey();

    /// <summary>Stages the last Messages conversation immediately and persists it off the UI dispatcher.</summary>
    public void SetLastDesktopConversationKey(string? conversationKey)
    {
        var db = activeDb;
        if (db is null) return;
        EnsureDesktopSelectionPersistence().SetConversation(db, conversationKey);
    }

    private DesktopSelectionPersistenceCoordinator EnsureDesktopSelectionPersistence()
        => desktopSelectionPersistence ??= new DesktopSelectionPersistenceCoordinator();

    public MemorySnapshot SnapshotMemories()
    {
        lock (profileSyncGate)
            return new MemorySnapshot(
                activeId,
                Profile.Memories.Select(MemoryPolicy.Clone).ToList());
    }

    /// <summary>Persists and replicates an owner-only memory.</summary>
    public bool UpsertMemory(
        string? accountId,
        MemoryItem memory,
        MemoryItem? expected,
        out MemoryItem? previous)
    {
        var normalized = MemoryPolicy.Normalize(memory);
        previous = null;
        var changed = false;
        lock (profileSyncGate)
        {
            if (activeDb is null
                || !string.Equals(activeId, accountId, StringComparison.Ordinal))
                return false;
            var existing = Profile.Memories.FirstOrDefault(item =>
                string.Equals(item.Id, normalized.Id, StringComparison.Ordinal));
            previous = existing is null ? null : MemoryPolicy.Clone(existing);
            if (expected is null
                ? existing is not null
                : existing is null || !MemoryPolicy.SharedEquals(existing, expected))
                return false;
            if (existing is not null && MemoryPolicy.SharedEquals(existing, normalized)) return false;

            activeDb.ExecuteDurableWrite(() => activeDb.UpsertMemory(normalized));
            if (existing is null)
                Profile.Memories.Add(normalized);
            else
                MemoryPolicy.CopyShared(normalized, existing);
            changed = true;
        }
        if (changed)
            EmitReplicatedChange(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert,
                normalized.Id, null, JsonSerializer.Serialize(MemoryPolicy.ToSync(normalized), ReplicationJson),
                TargetsForOwnerState());
        NotifyChanged();
        return true;
    }

    /// <summary>Deletes and replicates an owner-only memory.</summary>
    public bool DeleteMemory(
        string? accountId,
        string id,
        MemoryItem expected,
        out MemoryItem? previous)
    {
        previous = null;
        var deleted = false;
        lock (profileSyncGate)
        {
            if (activeDb is null
                || !string.Equals(activeId, accountId, StringComparison.Ordinal)
                || !TopicRunProtocol.IsValidIdentifier(id))
                return false;
            var existing = Profile.Memories.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal));
            previous = existing is null ? null : MemoryPolicy.Clone(existing);
            if (existing is null || !MemoryPolicy.SharedEquals(existing, expected)) return false;

            activeDb.ExecuteDurableWrite(() => activeDb.DeleteMemory(id));
            Profile.Memories.Remove(existing);
            deleted = true;
        }
        if (deleted)
            EmitReplicatedChange(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Delete,
                id, null, string.Empty, TargetsForOwnerState());
        NotifyChanged();
        return true;
    }

    /// <summary>Records local retrieval use without creating cross-device sync traffic.</summary>
    public void TouchMemories(
        string? accountId,
        IEnumerable<string> ids,
        DateTimeOffset? recalledAt = null)
    {
        var distinct = ids
            .Where(TopicRunProtocol.IsValidIdentifier)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0) return;
        var at = recalledAt ?? DateTimeOffset.UtcNow;
        lock (profileSyncGate)
        {
            if (activeDb is null
                || !string.Equals(activeId, accountId, StringComparison.Ordinal))
                return;
            activeDb.ExecuteDurableWrite(() => activeDb.TouchMemories(distinct, at));
            foreach (var memory in Profile.Memories.Where(memory => distinct.Contains(memory.Id, StringComparer.Ordinal)))
            {
                memory.RecallCount = Math.Min(1_000_000, memory.RecallCount + 1);
                memory.LastRecalledAt = at;
            }
        }
    }

    private void PrepareProfileStorage()
    {
        // Adopt: onboarding/link just filled a fresh profile with no active id yet.
        if (activeId is null && Profile.IsOnboarded)
        {
            EnsureRecoveryKeys();
            activeId = NewId();
            activeDb = OpenDb(activeId);
            Volatile.Write(
                ref activeDatabaseIdentity,
                NewActiveDatabaseIdentity(activeDb, activeId));
            accounts.Add(new AccountRef { Id = activeId, Handle = Profile.Handle, DisplayName = Profile.DisplayName });
            // Persist any history the fresh profile already carries (normally none at onboarding).
            foreach (var conv in Profile.Conversations)
            {
                conv.Handle = PrepareConversationForPersistence(conv);
                DeriveActivityMetadata(conv);
                activeDb.ExecuteDurableWrite(
                    () => activeDb.EnsureConversation(conv.Handle, conv.CreatedAt));
                PersistConversationMetadata(activeDb, conv);
                if (conv.LastActivityAt.HasValue)
                    activeDb.ExecuteDurableWrite(
                        () => activeDb.SetConversationActivity(conv.Handle, conv.LastActivityAt.Value));
                if (conv.IsPinned)
                    activeDb.ExecuteDurableWrite(() => activeDb.SetConversationPin(conv.Handle, true));
                foreach (var line in conv.Lines)
                    activeDb.ExecuteDurableWrite(
                        () => activeDb.AppendChatLine(Norm(conv.Handle), line));
            }
            foreach (var thread in Profile.OwnThreads)
            {
                thread.LastActivityAt ??= thread.Lines.Count == 0
                    ? thread.CreatedAt
                    : thread.Lines.Max(line => line.At);
                activeDb.ExecuteDurableWrite(
                    () => activeDb.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt));
                if (thread.LastActivityAt.HasValue)
                    activeDb.ExecuteDurableWrite(
                        () => activeDb.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value));
                if (thread.IsPinned)
                    activeDb.ExecuteDurableWrite(() => activeDb.SetOwnThreadPin(thread.Id, true));
                if (thread.ExecutionDeviceId is not null
                    || thread.ExecutionDeviceName is not null
                    || thread.ExecutionDevicePlatform is not null
                    || thread.ExecutionAt.HasValue
                    || thread.ExecutionRunId is not null)
                    activeDb.ExecuteDurableWrite(() => activeDb.SetOwnThreadExecution(
                        thread.Id,
                        thread.ExecutionDeviceId,
                        thread.ExecutionAt,
                        thread.ExecutionRunId,
                        thread.ExecutionDeviceName,
                        thread.ExecutionDevicePlatform));
                foreach (var line in thread.Lines)
                    activeDb.ExecuteDurableWrite(() => activeDb.AppendOwnChat(thread.Id, line));
            }
            for (var i = 0; i < Profile.Memories.Count; i++)
            {
                var memory = MemoryPolicy.Normalize(Profile.Memories[i]);
                Profile.Memories[i] = memory;
                activeDb.ExecuteDurableWrite(() => activeDb.UpsertMemory(memory));
            }
            NotificationCoordinatorBridge.ResetForAccount();
        }
    }

    private void UpdateActiveAccount()
    {
        if (activeId is null) return;
        var acc = accounts.FirstOrDefault(a => a.Id == activeId);
        if (acc is null) { acc = new AccountRef { Id = activeId }; accounts.Add(acc); }
        acc.Handle = Profile.Handle;
        acc.DisplayName = Profile.DisplayName;
    }

    public void Mutate(Action<MeshProfile> change, string? renamedCircleFrom = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (profileSyncGate)
            MutateCore(change, renamedCircleFrom, AssetPlanKind.None, hints: null);
        NotifyChanged();
    }

    /// <summary>
    /// Mutates database-backed Skills, Knowledge, or Widgets via explicit per-asset hints so the
    /// caller thread never scans the asset corpus. Each hint declares whether the change carries
    /// full content, is metadata-only (the stored body is preserved), or is a delete.
    /// </summary>
    private void MutateAssetsHinted(Action<MeshProfile> change, IReadOnlyList<AssetHint> hints)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (profileSyncGate)
            MutateCore(change, renamedCircleFrom: null, AssetPlanKind.Hints, hints);
        NotifyChanged();
    }

    /// <summary>Adds or replaces one asset whose full content is materialised on the in-memory object.</summary>
    public void SaveAssetContent(AssetKind kind, string id, Action<MeshProfile> change)
        => MutateAssetsHinted(change, new[] { new AssetHint(kind, id, AssetChange.Content) });

    /// <summary>Changes only one asset's metadata; the stored body is preserved off-thread.</summary>
    public void SaveAssetMetadata(AssetKind kind, string id, Action<MeshProfile> change)
        => MutateAssetsHinted(change, new[] { new AssetHint(kind, id, AssetChange.Metadata) });

    /// <summary>Removes one asset.</summary>
    public void RemoveAsset(AssetKind kind, string id, Action<MeshProfile> change)
        => MutateAssetsHinted(change, new[] { new AssetHint(kind, id, AssetChange.Delete) });

    /// <summary>
    /// Adds or replaces several assets whose full content is materialised on the in-memory objects
    /// (bounded bulk import). The batch is applied as one mutation.
    /// </summary>
    public void SaveAssetsContent(
        IReadOnlyList<(AssetKind Kind, string Id)> assets, Action<MeshProfile> change)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var hints = assets.Select(a => new AssetHint(a.Kind, a.Id, AssetChange.Content)).ToList();
        MutateAssetsHinted(change, hints);
    }

    public bool RenameCircle(string oldName, string newName)
    {
        var oldEntityId = CircleEntityId(oldName);
        var replacement = newName.Trim();
        lock (profileSyncGate)
        {
            var circle = Profile.Circles.FirstOrDefault(item =>
                CircleEntityId(item.Name) == oldEntityId);
            if (circle is null
                || replacement.Length == 0
                || Profile.Circles.Any(item =>
                    !ReferenceEquals(item, circle)
                    && CircleEntityId(item.Name) == CircleEntityId(replacement)))
                return false;
            var previousName = circle.Name;
            MutateCore(profile =>
            {
                circle.Name = replacement;
                ProfileProjection.RenameCircleReferences(profile, previousName, replacement);
            }, previousName, AssetPlanKind.MetadataSweep, hints: null);
        }
        NotifyChanged();
        return true;
    }

    public bool DeleteCircle(string name)
    {
        var entityId = CircleEntityId(name);
        lock (profileSyncGate)
        {
            var circle = Profile.Circles.FirstOrDefault(item =>
                CircleEntityId(item.Name) == entityId);
            if (circle is null) return false;
            var currentName = circle.Name;
            MutateCore(profile =>
            {
                profile.Circles.Remove(circle);
                ProfileProjection.DeleteCircleReferences(profile, currentName);
            }, null, AssetPlanKind.MetadataSweep, hints: null);
        }
        NotifyChanged();
        return true;
    }

    private void MutateCore(
        Action<MeshProfile> change,
        string? renamedCircleFrom,
        AssetPlanKind assetPlan = AssetPlanKind.None,
        IReadOnlyList<AssetHint>? hints = null)
    {
        var previousProfile = CloneProfile(Profile);
        var previousActiveId = activeId;
        var previousActiveDb = activeDb;
        var previousAccounts = accounts
            .Select(account => new AccountRef
            {
                Id = account.Id,
                Handle = account.Handle,
                DisplayName = account.DisplayName
            })
            .ToList();
        var before = ProfileProjection.Snapshot(Profile);
        // A metadata sweep (circle rename/delete) may touch many asset rows, so capture a bounded
        // metadata-only signature snapshot before the change and diff it after. Hinted mutations
        // build their work list directly from the hints and need no pre-change snapshot.
        var beforeMeta = assetPlan == AssetPlanKind.MetadataSweep ? SnapshotAssetMetadata() : null;
        try
        {
            change(Profile);
            var after = ProfileProjection.Snapshot(Profile);
            var profileChanged = HasProfileSyncChanges(before, after);
            PrepareProfileStorage();
            var deviceId = LocalDeviceId();
            var assetWorks = BuildAssetWorks(assetPlan, hints, beforeMeta, deviceId, PlatformCaps.IsMobile);
            if (!applyingReplicationProjection && profileChanged && activeDb is not null && deviceId is not null)
            {
                UpdateActiveAccount();
                Enqueue(new ProfileWork(
                    activeDb,
                    MeshDb.SerializeProfileForStorage(Profile),
                    WriteAccountIndex: true,
                    activeId,
                    SnapshotAccounts(),
                    assetWorks));
                EmitProfileProjectionChanges(before, after, renamedCircleFrom);
            }
            else
            {
                ScheduleSave(assetWorks);
            }
        }
        catch
        {
            if (activeId != previousActiveId)
            {
                var failedId = activeId;
                if (!ReferenceEquals(activeDb, previousActiveDb)) activeDb?.Dispose();
                if (failedId is not null)
                {
                    try
                    {
                        var path = DbPath(failedId);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch
                    {
                    }
                    secrets.DeleteDbKey(failedId);
                }
                activeId = previousActiveId;
                Volatile.Write(ref activeDatabaseIdentity, null);
                activeDb = previousActiveDb;
                if (previousActiveDb is not null && previousActiveId is not null)
                    Volatile.Write(
                        ref activeDatabaseIdentity,
                        NewActiveDatabaseIdentity(previousActiveDb, previousActiveId));
                accounts = previousAccounts;
            }
            Profile = previousProfile;
            throw;
        }
    }

    public void NotifyChanged()
    {
        var batch = replicationNotificationBatch.Value;
        if (batch is not null)
        {
            batch.Pending = true;
            return;
        }
        Changed?.Invoke();
    }

    // ---- chat history (append-only rows) ----------------------------------

    /// <summary>
    /// Appends a line to a conversation, persisting it as a single row (not a full re-serialize)
    /// so history stays scalable. Updates the in-memory conversation and notifies the UI.
    /// </summary>
    public void AddChatLine(string handle, ChatLine line, NotificationIntent? notificationIntent = null)
    {
        var conv = GetOrCreateConversation(handle);
        conv.Lines.Add(line);
        conv.LastActivityAt = ActivityTimestamp.Advance(conv.LastActivityAt, line.At);
        // The row itself is written by the persistence worker inside the same transaction as the
        // signed replication event, so history and its event can never diverge.
        EmitLineUpsert("conversation.line", conv.Handle, line, notificationIntent);
        EmitConversationUpsert(conv);
        NotifyChanged();
    }

    /// <summary>Appends a line to a "Me" topic thread as a single row.</summary>
    public void AddOwnChatLine(string threadId, ChatLine line, NotificationIntent? notificationIntent = null)
    {
        if (LegacyUncorrelatedTopicAnswerTestMode
            && string.Equals(line.Role, "assistant", StringComparison.Ordinal))
            line.ReplyToLineId = null;
        lock (profileSyncGate)
        {
            if (IsTopicLineDeleted(threadId, line.Id)
                || IsTopicLineDeleted(threadId, line.ReplyToLineId))
                return;
            var thread = GetOrCreateOwnThread(threadId);
            thread.Lines.Add(line);
            thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, line.At);
            EmitLineUpsert("topic.line", thread.Id, line, notificationIntent);
            EmitTopicUpsert(thread);
        }

        NotifyChanged();
    }

    internal bool LegacyUncorrelatedTopicAnswerTestMode { get; set; }

    /// <summary>Returns the thread with this id, or the first thread, creating one if none exist.</summary>
    public OwnThread GetOrCreateOwnThread(string? threadId = null)
    {
        if (threadId is not null)
        {
            var found = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
            if (found is not null) return found;
        }
        if (Profile.OwnThreads.Count > 0) return Profile.OwnThreads[0];
        return NewOwnThread();
    }

    /// <summary>Creates a new empty "Me" thread and returns it.</summary>
    public OwnThread NewOwnThread(
        string title = "New chat",
        string? targetDeviceId = null,
        DateTimeOffset? executionAt = null,
        string? executionRunId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastActivityAt = null,
        bool isPinned = false,
        string? targetDeviceName = null,
        string? targetDevicePlatform = null)
    {
        if (targetDeviceId is null
            && (targetDeviceName is not null || targetDevicePlatform is not null))
            throw new ArgumentException("Execution device metadata requires a device ID.");
        if (targetDeviceId is not null)
            ValidateExecutionDevice(new ExecutionDevice(
                targetDeviceId,
                targetDeviceName,
                targetDevicePlatform ?? DevicePlatforms.Unknown));
        var thread = new OwnThread
        {
            Title = title,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastActivityAt = lastActivityAt,
            IsPinned = isPinned,
            ExecutionDeviceId = targetDeviceId,
            ExecutionDeviceName = targetDeviceName,
            ExecutionDevicePlatform = targetDevicePlatform,
            ExecutionAt = executionAt,
            ExecutionRunId = executionRunId
        };
        Profile.OwnThreads.Add(thread);
        activeDb?.ExecuteDurableWrite(() => activeDb.UpsertOwnThread(
            thread.Id, thread.Title, thread.CreatedAt, Profile.OwnThreads.Count - 1,
            thread.LastActivityAt, thread.IsPinned, thread.ExecutionDeviceId,
            thread.ExecutionAt, thread.ExecutionRunId, replaceExecutionMetadata: true,
            executionDeviceName: thread.ExecutionDeviceName,
            executionDevicePlatform: thread.ExecutionDevicePlatform));
        EmitTopicUpsert(thread);
        NotifyChanged();
        return thread;
    }

    public OwnThread NewOwnThread(
        string title,
        ExecutionDevice? executionDevice,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastActivityAt = null,
        bool isPinned = false)
        => NewOwnThread(
            title,
            executionDevice?.DeviceId,
            createdAt: createdAt,
            lastActivityAt: lastActivityAt,
            isPinned: isPinned,
            targetDeviceName: executionDevice?.DeviceName,
            targetDevicePlatform: executionDevice?.Platform);

    public async Task<OwnThread> NewOwnThreadAsync(
        string title,
        ExecutionDevice? executionDevice,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastActivityAt = null,
        bool isPinned = false,
        CancellationToken cancellationToken = default)
    {
        if (executionDevice is not null) ValidateExecutionDevice(executionDevice);
        var thread = new OwnThread
        {
            Title = title,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastActivityAt = lastActivityAt,
            IsPinned = isPinned,
            ExecutionDeviceId = executionDevice?.DeviceId,
            ExecutionDeviceName = executionDevice?.DeviceName,
            ExecutionDevicePlatform = executionDevice?.Platform
        };

        MeshDb? db;
        int sortOrder;
        lock (profileSyncGate)
        {
            db = activeDb;
            sortOrder = Profile.OwnThreads.Count;
        }
        if (db is not null)
        {
            await db.ExecuteDurableWriteAsync(
                () => db.UpsertOwnThread(
                    thread.Id, thread.Title, thread.CreatedAt, sortOrder,
                    thread.LastActivityAt, thread.IsPinned, thread.ExecutionDeviceId,
                    thread.ExecutionAt, thread.ExecutionRunId, replaceExecutionMetadata: true,
                    executionDeviceName: thread.ExecutionDeviceName,
                    executionDevicePlatform: thread.ExecutionDevicePlatform),
                cancellationToken);
        }

        lock (profileSyncGate)
        {
            if (db is not null && !ReferenceEquals(activeDb, db))
                throw new InvalidOperationException("The active identity changed while the topic was being created.");
            Profile.OwnThreads.Add(thread);
        }
        EmitTopicUpsert(thread);
        NotifyChanged();
        return thread;
    }

    /// <summary>Renames a "Me" thread.</summary>
    /// <summary>Moves one private topic to the requested list position and persists the order.</summary>
    public void ReorderOwnThread(string threadId, int newIndex)
    {
        OwnThread? thread;
        lock (profileSyncGate)
        {
            var oldIndex = Profile.OwnThreads.FindIndex(t => t.Id == threadId);
            if (oldIndex < 0 || Profile.OwnThreads.Count < 2) return;
            newIndex = Math.Clamp(newIndex, 0, Profile.OwnThreads.Count - 1);
            if (oldIndex == newIndex) return;
            thread = Profile.OwnThreads[oldIndex];
            var ordered = Profile.OwnThreads.ToList();
            ordered.RemoveAt(oldIndex);
            ordered.Insert(newIndex, thread);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.ReorderOwnThreads(ordered.Select(t => t.Id).ToList()));
            Profile.OwnThreads.RemoveAt(oldIndex);
            Profile.OwnThreads.Insert(newIndex, thread);
        }
        NotifyChanged();
    }

    /// <summary>Moves a private topic to the requested list position. Alias for ReorderOwnThread.</summary>
    public void MoveOwnThread(string threadId, int newIndex) => ReorderOwnThread(threadId, newIndex);

    /// <summary>Pins a private topic so it sorts first. Bumps activity.</summary>
    public void PinOwnThread(string threadId)
        => SetOwnThreadPinned(threadId, true);

    public void SetOwnThreadPinned(string threadId, bool pinned)
    {
        OwnThread? thread;
        lock (profileSyncGate)
        {
            thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
            if (thread is null || thread.IsPinned == pinned) return;
            var at = DateTimeOffset.UtcNow;
            var activityAt = ActivityTimestamp.Advance(thread.LastActivityAt, at);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.SetOwnThreadPinAndActivity(thread.Id, pinned, activityAt));
            thread.IsPinned = pinned;
            thread.LastActivityAt = activityAt;
        }
        EmitTopicUpsert(thread);
        NotifyChanged();
    }

    /// <summary>Unpins a private topic. Bumps activity.</summary>
    public void UnpinOwnThread(string threadId)
        => SetOwnThreadPinned(threadId, false);

    public void BindOwnThreadForSend(string threadId, ExecutionDevice target)
    {
        ValidateThreadId(threadId);
        ValidateExecutionDevice(target);
        OwnThread? thread;
        lock (profileSyncGate)
        {
            thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
            if (thread is null)
                throw new KeyNotFoundException($"Topic '{threadId}' does not exist.");
            if (!string.IsNullOrWhiteSpace(thread.ExecutionDeviceId))
                throw new InvalidOperationException("The topic is already bound to an execution device.");
            if (activeDb is not null
                && !activeDb.ExecuteDurableWrite(() => activeDb.TryBindOwnThreadDevice(
                    thread.Id, target.DeviceId, target.DeviceName, target.Platform)))
                throw new InvalidOperationException("The topic could not be bound atomically.");
            thread.ExecutionDeviceId = target.DeviceId;
            thread.ExecutionDeviceName = target.DeviceName;
            thread.ExecutionDevicePlatform = target.Platform;
        }
        EmitTopicUpsert(thread);
        NotifyChanged();
    }

    public void MoveOwnThreadToDevice(
        string threadId,
        ExecutionDevice target,
        DateTimeOffset? movedAt = null)
    {
        ValidateThreadId(threadId);
        ValidateExecutionDevice(target);
        var at = movedAt ?? DateTimeOffset.UtcNow;
        if (at == default) throw new ArgumentException("A move timestamp is required.", nameof(movedAt));
        OwnThread thread;
        lock (profileSyncGate)
        {
            thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId)
                     ?? throw new KeyNotFoundException($"Topic '{threadId}' does not exist.");
            var activityAt = ActivityTimestamp.Advance(thread.LastActivityAt, at);
            if (activeDb is not null
                && !activeDb.ExecuteDurableWrite(() => activeDb.MoveOwnThreadToDevice(
                    thread.Id, target.DeviceId, target.DeviceName, target.Platform, activityAt)))
                throw new InvalidOperationException("The topic could not be moved atomically.");
            thread.ExecutionDeviceId = target.DeviceId;
            thread.ExecutionDeviceName = target.DeviceName;
            thread.ExecutionDevicePlatform = target.Platform;
            thread.ExecutionAt = null;
            thread.ExecutionRunId = null;
            thread.LastActivityAt = activityAt;
            remoteRuns.TryRemove(thread.Id, out _);
        }
        EmitTopicUpsert(thread);
        NotifyChanged();
    }

    public OwnThread EnsureOwnThreadForDeviceRun(
        string threadId,
        ExecutionDevice target,
        DateTimeOffset createdAt)
    {
        ValidateThreadId(threadId);
        ValidateExecutionDevice(target);
        if (createdAt == default)
            throw new ArgumentException("A topic timestamp is required.", nameof(createdAt));

        OwnThread thread;
        lock (profileSyncGate)
        {
            thread = Profile.OwnThreads.FirstOrDefault(item => item.Id == threadId)
                     ?? new OwnThread
                     {
                         Id = threadId,
                         Title = "New chat",
                         CreatedAt = createdAt,
                         LastActivityAt = createdAt
                     };
            if (!Profile.OwnThreads.Contains(thread))
                Profile.OwnThreads.Add(thread);
            if (thread.ExecutionDeviceId is not null
                && !string.Equals(thread.ExecutionDeviceId, target.DeviceId, StringComparison.Ordinal))
                throw new InvalidOperationException("The topic is bound to another execution device.");

            var activityAt = ActivityTimestamp.Advance(thread.LastActivityAt, createdAt);
            activeDb?.ExecuteDurableWrite(() => activeDb.UpsertOwnThread(
                thread.Id,
                thread.Title,
                thread.CreatedAt,
                Math.Max(0, Profile.OwnThreads.IndexOf(thread)),
                activityAt,
                thread.IsPinned,
                target.DeviceId,
                thread.ExecutionAt,
                thread.ExecutionRunId,
                replaceExecutionMetadata: true,
                executionDeviceName: target.DeviceName,
                executionDevicePlatform: target.Platform));
            thread.ExecutionDeviceId = target.DeviceId;
            thread.ExecutionDeviceName = target.DeviceName;
            thread.ExecutionDevicePlatform = target.Platform;
            thread.LastActivityAt = activityAt;
        }
        EmitTopicUpsert(thread);
        NotifyChanged();
        return thread;
    }

    /// <summary>Compatibility alias. Prefer <see cref="BindOwnThreadForSend"/>.</summary>
    public bool BindThreadDevice(string threadId, string deviceId)
    {
        try
        {
            BindOwnThreadForSend(threadId, new ExecutionDevice(deviceId, null, DevicePlatforms.Unknown));
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }

    public void RenameOwnThread(string threadId, string title)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        thread.Title = string.IsNullOrWhiteSpace(title) ? thread.Title : title.Trim();
        var at = DateTimeOffset.UtcNow;
        thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, at);
        activeDb?.ExecuteDurableWrite(() =>
        {
            activeDb.RenameOwnThread(thread.Id, thread.Title);
            activeDb.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value);
        });
        EmitTopicUpsert(thread);
        NotifyChanged();
    }

    /// <summary>Clears a "Me" thread's messages but keeps the thread.</summary>
    public void ClearOwnThread(string threadId)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        thread.Lines.Clear();
        EmitTombstone("topic.clear", thread.Id);
        NotifyChanged();
    }

    /// <summary>Deletes a "Me" thread and all its messages.</summary>
    public void DeleteOwnThread(string threadId)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        if (activeDb is { } db)
            draftPersistence?.Forget(db, ComposerDraftKind.Topic, threadId);
        Profile.OwnThreads.Remove(thread);
        completedThreads.Remove(threadId);
        EmitTombstone("topic.delete", threadId);
        NotifyChanged();
    }

    // ---- online replication emission ---------------------------------------

    private void EmitTopicUpsert(
        OwnThread thread,
        NotificationIntent? notificationIntent = null,
        TopicRunUpdatePayload? terminalUpdate = null)
    {
        var sortOrder = Profile.OwnThreads.IndexOf(thread);
        var executionTriggerLineId = ExecutionTriggerLineId(thread);
        var body = JsonSerializer.Serialize(new
        {
            thread.Id,
            thread.Title,
            thread.CreatedAt,
            SortOrder = Math.Max(0, sortOrder),
            thread.ExecutionDeviceId,
            thread.ExecutionDeviceName,
            thread.ExecutionDevicePlatform,
            thread.LastActivityAt,
            thread.IsPinned,
            thread.ExecutionAt,
            thread.ExecutionRunId,
            ExecutionTriggerLineId = executionTriggerLineId,
            TerminalUpdate = terminalUpdate
        }, ReplicationJson);
        EmitReplicatedChange(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert,
            thread.Id, thread.Id, body, TargetsForOwnerState(), notificationIntent);
    }

    private string? ExecutionTriggerLineId(OwnThread thread)
    {
        if (!TopicRunProtocol.IsValidIdentifier(thread.ExecutionRunId)) return null;
        var runId = thread.ExecutionRunId!;
        var durable = activeDb?.GetTopicRunCorrelation(runId)?.TriggerLineId
                      ?? activeDb?.GetLocalTopicRun(runId)?.TriggerLineId;
        if (TopicRunProtocol.IsValidIdentifier(durable)) return durable;
        return thread.ExecutionAt is { } executionAt
            ? thread.Lines.LastOrDefault(line =>
                string.Equals(line.Role, "user", StringComparison.Ordinal)
                && line.At == executionAt)?.Id
            : null;
    }

    private void EmitConversationUpsert(Conversation conversation)
    {
        var handle = Norm(conversation.Handle);
        var body = JsonSerializer.Serialize(conversation, ReplicationJson);
        EmitReplicatedChange(ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Upsert,
            handle, handle, body, TargetsForOwnerState());
    }

    private void EmitLineUpsert(string kind, string parentId, ChatLine line, NotificationIntent? notificationIntent = null)
    {
        var mappedKind = kind.Contains("Topic", StringComparison.OrdinalIgnoreCase)
            ? ReplicationOpKinds.Topic
            : ReplicationOpKinds.Message;
        var targets = mappedKind == ReplicationOpKinds.Topic ? TargetsForOwnerState() : TargetsForConversation(parentId);
        EmitReplicatedChange(mappedKind, ReplicationPayloadCodec.DomainAction.AppendLine,
            parentId, parentId, JsonSerializer.Serialize(line, ReplicationJson), targets, notificationIntent);
    }

    private void EmitTombstone(string kind, string entityId, IEnumerable<string?>? additionalVersions = null)
    {
        var mappedKind = kind.Contains("Conversation", StringComparison.OrdinalIgnoreCase)
            ? ReplicationOpKinds.Conversation
            : ReplicationOpKinds.Topic;
        var targets = TargetsForOwnerState();
        // A "clear" empties the history but keeps the entity; a "delete" removes it. Both are
        // Delete actions on the wire, so the body carries the discriminator and replicas apply the
        // matching destructive operation deterministically instead of guessing.
        var body = kind.Contains("clear", StringComparison.OrdinalIgnoreCase)
            ? "{\"clear\":true}"
            : "{\"clear\":false}";
        EmitReplicatedChange(mappedKind, ReplicationPayloadCodec.DomainAction.Delete,
            entityId, entityId, body, targets);
    }


    /// <summary>
    /// Replicates the owner's read position for a conversation to their other devices so an already
    /// read thread does not light up as unread elsewhere. The watermark carries no message content,
    /// only the conversation, the position and the device that moved it.
    /// </summary>
    private void EmitReadWatermark(string handle)
    {
        var conversationId = Norm(handle);
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        var account = Norm(Profile.Handle);
        if (string.IsNullOrWhiteSpace(account)) return;
        var deviceId = LocalDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        var conversation = Profile.Conversations
            .FirstOrDefault(c => string.Equals(Norm(c.Handle), conversationId, StringComparison.Ordinal));
        var through = conversation?.Lines.LastOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(through)) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = JsonSerializer.Serialize(
            new ReadWatermarkPayload(conversationId, account, through!, deviceId!, now, now),
            ReplicationJson);
        EmitReplicatedChange(ReplicationOpKinds.ReadWatermark,
            ReplicationPayloadCodec.DomainAction.ReadWatermark,
            conversationId, conversationId, body, TargetsForOwnerState());
    }

    private bool IsTopicLineDeleted(string? threadId, string? lineId)
        => !string.IsNullOrWhiteSpace(threadId)
           && !string.IsNullOrWhiteSpace(lineId)
           && activeDb?.GetSyncTombstoneVersion("topic.line.delete", DomainProjectionEntityIds.TopicLine(threadId, lineId)) is not null;

    private static string CircleEntityId(string? name) => ProfileProjection.CircleEntityId(name);

    private static bool HasProfileSyncChanges(ProfileProjectionState before, ProfileProjectionState after)
        => before.Circles.Count != after.Circles.Count
           || before.Contacts.Count != after.Contacts.Count
           || before.Circles.Any(item =>
               !after.Circles.TryGetValue(item.Key, out var circle) || item.Value != circle)
           || before.Contacts.Any(item =>
               !after.Contacts.TryGetValue(item.Key, out var contact)
               || !ProfileProjection.ContactEquals(item.Value, contact));

    private void EmitProfileProjectionChanges(
        ProfileProjectionState before,
        ProfileProjectionState after,
        string? renamedCircleFrom)
    {
        foreach (var entityId in before.Circles.Keys.Except(after.Circles.Keys, StringComparer.Ordinal))
            EmitReplicatedChange(ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Delete,
                entityId, null, string.Empty, TargetsForOwnerState());
        foreach (var (entityId, circle) in after.Circles)
        {
            if (!before.Circles.TryGetValue(entityId, out var previous) || previous != circle)
            {
                var payload = circle;
                var previousEntityId = CircleEntityId(renamedCircleFrom);
                if (previousEntityId.Length > 0
                    && previousEntityId != entityId
                    && before.Circles.TryGetValue(previousEntityId, out var renamedCircle)
                    && !after.Circles.ContainsKey(previousEntityId)
                    && !before.Circles.ContainsKey(entityId))
                {
                    payload = circle with
                    {
                        Renames = new[] { new CircleRenameProjection(renamedCircle.Name, NewReplicationVersion()) }
                    };
                }
                EmitReplicatedChange(ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
                    entityId, null, JsonSerializer.Serialize(payload, ReplicationJson), TargetsForOwnerState());
            }
        }
        foreach (var entityId in before.Contacts.Keys.Except(after.Contacts.Keys, StringComparer.Ordinal))
            EmitReplicatedChange(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Delete,
                entityId, null, string.Empty, TargetsForOwnerState());
        foreach (var (entityId, contact) in after.Contacts)
            if (!before.Contacts.TryGetValue(entityId, out var previous)
                || !ProfileProjection.ContactEquals(previous, contact))
                EmitReplicatedChange(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert,
                    entityId, null, JsonSerializer.Serialize(contact, ReplicationJson), TargetsForOwnerState());
    }

    /// <summary>
    /// Records a local domain change for durable, atomic replication. The in-memory mutation has
    /// already happened (so the UI stays responsive); this hands an immutable descriptor to the
    /// serialized background persistence worker, which performs the ACTUAL domain table write, the
    /// signed local event allocation and the outbox references inside ONE SQLite transaction.
    ///
    /// There is no fire-and-forget task and no swallowed exception: a failure requeues the work,
    /// surfaces on <see cref="LastPersistenceError"/> and makes
    /// <see cref="FlushPersistenceAsync"/> throw, so a local mutation can never silently claim
    /// replication success.
    /// </summary>
    private void EmitReplicatedChange(
        string kind,
        ReplicationPayloadCodec.DomainAction action,
        string entityId,
        string? conversationId,
        string bodyJson,
        IReadOnlyCollection<string> targets,
        NotificationIntent? notificationIntent = null)
    {
        if (applyingReplicationProjection) return;
        var db = activeDb;
        if (db is null) return;
        void Persist() => Enqueue(new ProfileWork(
                Db: null,
                BlobJson: null,
                WriteAccountIndex: false,
                IndexActiveId: null,
                IndexAccounts: null,
                Assets: Array.Empty<AssetWork>(),
                Replications: new[]
                {
                    new ReplicationWork(db, kind, action, entityId, conversationId,
                        NewReplicationVersion(), bodyJson ?? string.Empty, targets.ToArray(),
                        notificationIntent)
                }));
        if (ReplicationEventTestFaultScheduler?.Schedule(
                new GeneratedReplicationEvent(kind, action, entityId, bodyJson ?? string.Empty),
                Persist) != true)
            Persist();
    }

    private string NewReplicationVersion()
    {
        var source = LocalDeviceId() ?? "local";
        return ProjectionVersion.Create(DateTimeOffset.UtcNow, source, NewId());
    }

    private IReadOnlyCollection<string> TargetsForOwnerState()
    {
        var handle = Norm(Profile.Handle);
        return handle.Length == 0 ? Array.Empty<string>() : new[] { handle };
    }

    private IReadOnlyCollection<string> TargetsForConversation(string handle)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var own = Norm(Profile.Handle);
        if (own.Length > 0) targets.Add(own);
        var peer = Norm(handle);
        if (peer.Length > 0 && !peer.StartsWith("group:", StringComparison.Ordinal) && !peer.StartsWith("svc:", StringComparison.Ordinal))
            targets.Add(peer);
        return targets.ToList();
    }

    private string? LocalDeviceId()
        => string.IsNullOrWhiteSpace(Profile.PublicKey)
            ? null
            : DeviceProtocol.DeviceId(Profile.PublicKey);

    private static void CopyContact(Domain.Contact source, Domain.Contact destination)
    {
        destination.Handle = source.Handle;
        destination.DisplayName = source.DisplayName;
        destination.Circles = source.Circles.ToList();
        destination.Allowed = source.Allowed;
        destination.SigningKeys = source.SigningKeys.ToList();
        destination.KeyChanged = source.KeyChanged;
        destination.TokensSpent = source.TokensSpent;
        destination.Muted = source.Muted;
        destination.Blocked = source.Blocked;
    }

    private static MeshProfile CloneProfile(MeshProfile profile)
    {
        var clone = JsonSerializer.Deserialize<MeshProfile>(
                        JsonSerializer.Serialize(profile, JsonOpts),
                        JsonOpts)
                    ?? throw new InvalidOperationException("The profile could not be cloned for rollback.");
        CopyAttachments(profile.Conversations, clone.Conversations);
        CopyAttachments(profile.OwnThreads, clone.OwnThreads);
        return clone;
    }

    private static void CopyAttachments(
        IEnumerable<Conversation> source,
        IEnumerable<Conversation> destination)
    {
        var sourceByHandle = source.ToDictionary(
            conversation => Norm(conversation.Handle),
            StringComparer.Ordinal);
        foreach (var conversation in destination)
            if (sourceByHandle.TryGetValue(Norm(conversation.Handle), out var original))
                CopyAttachments(original.Lines, conversation.Lines);
    }

    private static void CopyAttachments(
        IEnumerable<OwnThread> source,
        IEnumerable<OwnThread> destination)
    {
        var sourceById = source.ToDictionary(thread => thread.Id, StringComparer.Ordinal);
        foreach (var thread in destination)
            if (sourceById.TryGetValue(thread.Id, out var original))
                CopyAttachments(original.Lines, thread.Lines);
    }

    private static void CopyAttachments(
        IEnumerable<ChatLine> source,
        IEnumerable<ChatLine> destination)
    {
        var sourceById = source.ToDictionary(line => line.Id, StringComparer.Ordinal);
        foreach (var line in destination)
            if (sourceById.TryGetValue(line.Id, out var original))
                line.Attachments = original.Attachments.ToList();
    }

    // ---- token counter ----------------------------------------------------

    /// <summary>Stable key ("Provider/model") for the active model; the token counter resets when it changes.</summary>
    public string CurrentModelKey()
    {
        var m = Profile.Model;
        // The hosted free model's actual id is chosen server-side (currently Groq llama-3.3), so the
        // client does not claim a specific upstream name, it labels it generically.
        if (m.Provider == ModelProvider.MeshHosted)
            return "Mesh free model";
        return $"{m.Provider}/{m.Model}";
    }

    /// <summary>
    /// Folds token usage into the running total for the current model, resetting first when the
    /// model changed since the last record (the counter is only meaningful per model).
    /// </summary>
    public void AddTokens(string modelKey, long promptTokens, long completionTokens)
    {
        var t = Profile.Tokens;
        if (t.ModelKey != modelKey)
        {
            t.ModelKey = modelKey;
            t.PromptTokens = 0;
            t.CompletionTokens = 0;
        }
        t.PromptTokens += promptTokens;
        t.CompletionTokens += completionTokens;
        Save();
        NotifyChanged();
    }

    /// <summary>Resets the live token counter, e.g. when the user switches models in settings.</summary>
    public void ResetTokenCounter()
    {
        Profile.Tokens = new TokenUsage { ModelKey = CurrentModelKey() };
        Save();
        NotifyChanged();
    }

    // ---- unread message tracking -----------------------------------------

    /// <summary>Handles with at least one unread inbound person-message.</summary>
    public IReadOnlyCollection<string> UnreadHandles => unread;

    /// <summary>Total number of things needing the owner's attention: unread chats + requests + approvals.</summary>
    public int AttentionCount => unread.Count + Profile.Requests.Count + Profile.Approvals.Count;

    /// <summary>Marks a conversation as having an unread inbound message.</summary>
    public void MarkUnread(string handle)
    {
        var h = Norm(handle);
        if (unread.Add(h))
        {
            if (!Profile.UnreadFrom.Contains(h)) { Profile.UnreadFrom.Add(h); ScheduleProfileSave(); }
            NotifyChanged();
        }
    }

    /// <summary>True when the given conversation has an unread inbound message.</summary>
    public bool IsUnread(string handle) => unread.Contains(Norm(handle));

    /// <summary>
    /// A conversation key a deep link asked to open. The Messages screen consumes this on navigation
    /// and selects that conversation. Set by the deep-link router after it ensures the conversation
    /// exists; cleared once opened.
    /// </summary>
    public string? PendingOpenConversation { get; private set; }

    /// <summary>Requests that the Messages screen open the given conversation key (from a deep link).</summary>
    public void RequestOpenConversation(string key)
    {
        PendingOpenConversation = key;
        NotifyChanged();
    }

    /// <summary>Returns and clears the pending deep-link conversation, or null when there is none.</summary>
    public string? ConsumePendingOpen()
    {
        var k = PendingOpenConversation;
        PendingOpenConversation = null;
        return k;
    }

    // Conversations (keyed by their exact conversation key) that are waiting for a reply, e.g. a
    // service request whose response arrives asynchronously. Used to show a "thinking" indicator.
    // Each entry carries a timestamp so a lost/never-arriving reply cannot pin the indicator forever.
    private readonly Dictionary<string, DateTimeOffset> awaiting = new(StringComparer.Ordinal);
    private static readonly TimeSpan AwaitTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Marks a conversation as waiting for a reply (shows a processing indicator).</summary>
    public void SetAwaiting(string key)
    {
        awaiting[key] = DateTimeOffset.UtcNow;
        NotifyChanged();
    }

    /// <summary>Clears the waiting-for-reply state for a conversation.</summary>
    public void ClearAwaiting(string key)
    {
        if (awaiting.Remove(key)) NotifyChanged();
    }

    /// <summary>True while a conversation is waiting for a reply (and the wait has not timed out).</summary>
    public bool IsAwaiting(string key)
    {
        if (awaiting.TryGetValue(key, out var t))
        {
            if (DateTimeOffset.UtcNow - t < AwaitTimeout) return true;
            awaiting.Remove(key);
        }
        return false;
    }

    // Live agent step trace, keyed by conversation/thread id so independent threads each show only
    // their own steps. The agent reports a step as each tool call starts and finishes; the Me chat
    // renders the steps for the thread being viewed. Cleared per thread at the start and end of its turn.
    private readonly LiveAgentRenderState liveAgentRenderState = new();

    /// <summary>The steps taken so far in the given thread's current turn (most recent last).</summary>
    public IReadOnlyList<AgentStep> AgentStepsFor(string key)
        => liveAgentRenderState.StepsFor(key);

    /// <summary>Clears one thread's step trace at the start of a new turn.</summary>
    public void BeginAgentSteps(string key)
    {
        if (liveAgentRenderState.BeginSteps(key)) NotifyChanged();
    }

    /// <summary>
    /// Records a step for a thread. A Started step is appended; a Done/Failed step updates the matching
    /// pending step in place (so a tool shows as running then completed rather than twice).
    /// </summary>
    public void ReportAgentStep(string key, AgentStep step)
    {
        liveAgentRenderState.ReportStep(key, step);
        NotifyChanged();
    }

    /// <summary>Clears one thread's step trace when its turn ends.</summary>
    public void EndAgentSteps(string key)
    {
        if (liveAgentRenderState.EndSteps(key)) NotifyChanged();
    }

    // Transient streamed assistant draft (reasoning + answer) for a thread's in-flight turn. Mirrors
    // the step trace: never persisted or sent to peers, and exposed to Razor only as immutable snapshots.
    private readonly AssistantDraftRefreshGate assistantDraftRefreshGate = new();

    /// <summary>A thread's live streamed reply: reasoning and answer accumulated as chunks arrive.</summary>
    public sealed record AssistantDraft(string Reasoning, string Answer)
    {
        public bool HasReasoning => Reasoning.Length > 0;
        public bool HasAnswer => Answer.Length > 0;
    }

    /// <summary>The live streamed draft for the given thread's turn, or null when none is streaming.</summary>
    public AssistantDraft? AssistantDraftFor(string key)
    {
        if (liveAgentRenderState.DraftFor(key) is not { } draft) return null;
        return new AssistantDraft(draft.Reasoning, draft.Answer);
    }

    /// <summary>Starts a fresh streamed draft for a thread at the start of a turn.</summary>
    public void BeginAssistantDraft(string key)
    {
        assistantDraftRefreshGate.Reset(key);
        liveAgentRenderState.BeginDraft(key);
        NotifyChanged();
    }

    /// <summary>Appends one streamed reasoning/answer fragment to a thread's live draft.</summary>
    public void AppendAssistantDelta(string key, AgentDelta delta)
    {
        if (liveAgentRenderState.AppendDraft(key, delta)
            && assistantDraftRefreshGate.ShouldPublish(key, delta.Kind, Environment.TickCount64))
            NotifyChanged();
    }

    /// <summary>Clears a thread's streamed draft once its turn ends and the final line is committed.</summary>
    public void EndAssistantDraft(string key)
    {
        var removed = liveAgentRenderState.EndDraft(key);
        assistantDraftRefreshGate.Reset(key);
        if (removed) NotifyChanged();
    }

    // Per-thread owner-turn run state, held here (in the singleton app state) rather than in the Me
    // page component so it SURVIVES NAVIGATION: a turn keeps running when the user leaves the Me
    // section, and the busy/thinking indicator, the widget-building label, and the steerable-input
    // queue must all still be correct when they return (and a fresh page instance must not start a
    // second concurrent turn for a thread that is already running). Keyed by own-thread id.
    private readonly HashSet<string> busyThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentRunState> agentRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> buildingThreads = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedThreads = new(StringComparer.Ordinal);
    private readonly QueuedTopicRunState queuedTopicRuns = new();
    private readonly ConcurrentDictionary<string, RemoteRunProjection> remoteRuns =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> terminalRemoteRuns = new(StringComparer.Ordinal);
    // Last applied streamed-delta sequence per remote run (key: threadId \0 runId), so a viewing device
    // applies each forwarded reply fragment once and in order and ignores duplicates or reordered ones.
    private readonly Dictionary<string, int> remoteDeltaSeq = new(StringComparer.Ordinal);
    // Cancellation source per running thread, so the user can STOP an in-progress turn. The token is
    // passed into the agent call and flows down through the provider tool loop (real cancellation of
    // the HTTP request, not just a UI change). Threads that were cancelled (rather than finishing on
    // their own) are tracked so the caller can distinguish cancellation from failure.
    private readonly Dictionary<string, CancellationTokenSource> threadCts = new(StringComparer.Ordinal);
    private readonly HashSet<string> cancelledThreads = new(StringComparer.Ordinal);

    /// <summary>True while the given own-thread is running an agent turn.</summary>
    public bool IsThreadBusy(string threadId) => busyThreads.Contains(threadId);

    public AgentRunState? AgentRunFor(string threadId)
        => agentRuns.TryGetValue(threadId, out var run) ? run : null;

    public void SetAgentRun(AgentRunState run)
    {
        agentRuns[run.ThreadId] = run;
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == run.ThreadId);
        if (thread is not null)
        {
            thread.ExecutionRunId = run.RunId;
            thread.ExecutionAt = run.StartedAt;
            thread.LastActivityAt = ActivityTimestamp.Advance(
                thread.LastActivityAt, run.StartedAt);
            activeDb?.ExecuteDurableWrite(() => activeDb.SetOwnThreadExecutionAndActivity(
                thread.Id,
                thread.ExecutionDeviceId,
                thread.ExecutionDeviceName,
                thread.ExecutionDevicePlatform,
                thread.ExecutionAt,
                thread.ExecutionRunId,
                thread.LastActivityAt!.Value));
            EmitTopicUpsert(thread);
        }
        NotifyChanged();
    }

    public void ClearAgentRun(string threadId)
    {
        if (!agentRuns.Remove(threadId)) return;
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is not null)
        {
            var at = DateTimeOffset.UtcNow;
            thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, at);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value));
            EmitTopicUpsert(thread);
        }
        NotifyChanged();
    }

    /// <summary>Gets the current remote run projection for a thread, or null.</summary>
    public RemoteRunProjection? GetRemoteRunProjection(string threadId)
        => remoteRuns.TryGetValue(threadId, out var projection)
            ? CloneRemoteRunProjection(projection)
            : null;

    public void RegisterExpectedRemoteRun(
        string threadId,
        string runId,
        ExecutionDevice target,
        DateTimeOffset startedAt)
    {
        ValidateThreadId(threadId);
        if (!TopicRunProtocol.IsValidIdentifier(runId))
            throw new ArgumentException("A run ID is required.", nameof(runId));
        ValidateExecutionDevice(target);
        if (startedAt == default)
            throw new ArgumentException("A run timestamp is required.", nameof(startedAt));

        OwnThread thread;
        lock (profileSyncGate)
        {
            thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId)
                     ?? throw new KeyNotFoundException($"Topic '{threadId}' does not exist.");
            if (thread.ExecutionRunId is not null
                && !string.Equals(thread.ExecutionRunId, runId, StringComparison.Ordinal))
                throw new InvalidOperationException("The topic already has a different active run.");
            if (thread.ExecutionDeviceId is not null
                && !string.Equals(thread.ExecutionDeviceId, target.DeviceId, StringComparison.Ordinal))
                throw new InvalidOperationException("The run target does not match the bound execution device.");
            var activityAt = ActivityTimestamp.Advance(thread.LastActivityAt, startedAt);
            if (activeDb is not null
                && !activeDb.ExecuteDurableWrite(() => activeDb.SetOwnThreadExecutionAndActivity(
                    thread.Id,
                    target.DeviceId,
                    target.DeviceName,
                    target.Platform,
                    startedAt,
                    runId,
                    activityAt)))
                throw new InvalidOperationException("The expected remote run could not be persisted.");
            terminalRemoteRuns.Remove(threadId + "\0" + runId);
            thread.ExecutionDeviceId = target.DeviceId;
            thread.ExecutionDeviceName = target.DeviceName;
            thread.ExecutionDevicePlatform = target.Platform;
            thread.ExecutionRunId = runId;
            thread.ExecutionAt = startedAt;
            thread.LastActivityAt = activityAt;
        }
        EmitTopicUpsert(thread);
        NotifyChanged();
    }

    public void ApplyRemoteRunUpdate(TopicRunUpdatePayload update)
        => _ = TryApplyRemoteRunUpdate(update);

    public bool TryApplyRemoteRunUpdate(TopicRunUpdatePayload update)
    {
        var result = ApplyRemoteRunUpdateCore(update, null, null);
        return result is RemoteTopicUpdatePersistenceResult.Applied
            or RemoteTopicUpdatePersistenceResult.Ignored
            or RemoteTopicUpdatePersistenceResult.Duplicate;
    }

    public RemoteTopicUpdatePersistenceResult TryApplyReceivedTopicControl(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        MeshDb.ReceivedTopicControlItem control)
        => ApplyRemoteRunUpdateCore(update, sourceDeviceId, control);

    public RemoteTopicUpdatePersistenceResult ApplyRemoteTopicUpdate(
        TopicRunUpdatePayload update,
        string sourceDeviceId)
        => ApplyRemoteRunUpdateCore(update, sourceDeviceId, null);

    private RemoteTopicUpdatePersistenceResult ApplyRemoteRunUpdateCore(
        TopicRunUpdatePayload update,
        string? sourceDeviceId,
        MeshDb.ReceivedTopicControlItem? control)
    {
        ArgumentNullException.ThrowIfNull(update);
        var correlationKey = update.ThreadId + "\0" + update.RunId;
        OwnThread? thread;
        var terminal = TopicControlProtocol.IsTerminal(update);
        lock (profileSyncGate)
        {
            var alreadyTerminal = terminalRemoteRuns.Contains(correlationKey);
            if (alreadyTerminal && control is null)
                return RemoteTopicUpdatePersistenceResult.Duplicate;
            thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, update.ThreadId, StringComparison.Ordinal));
            if (activeDb is null)
                return RemoteTopicUpdatePersistenceResult.PersistenceFailed;
            var expected = sourceDeviceId is null
                ? RemoteRunCorrelation.IsExpected(thread, update.ThreadId, update.RunId)
                : IsExpectedTopicRunCorrelation(
                    update, sourceDeviceId, allowRetained: control is not null);
            if (!expected)
                return activeDb is null
                    ? RemoteTopicUpdatePersistenceResult.PersistenceFailed
                    : RemoteTopicUpdatePersistenceResult.NotCorrelated;
            sourceDeviceId ??= thread?.ExecutionDeviceId;
            if (!TopicRunProtocol.IsValidIdentifier(sourceDeviceId))
                return RemoteTopicUpdatePersistenceResult.NotCorrelated;

            var persistence = activeDb.ExecuteDurableWrite(() =>
                activeDb.ApplyRemoteTopicUpdate(update, sourceDeviceId!, control));
            if (persistence != RemoteTopicUpdatePersistenceResult.Applied)
                return persistence;

            var activityAt = ActivityTimestamp.Advance(
                thread!.LastActivityAt, update.Timestamp);
            thread.LastActivityAt = activityAt;
            thread.ExecutionAt ??= update.Timestamp;
            thread.ExecutionRunId = terminal ? null : update.RunId;
            if (terminal)
            {
                if (update.Result is { } terminalResult
                    && !thread.Lines.Any(line => string.Equals(
                        line.Id, terminalResult.LineId, StringComparison.Ordinal)))
                {
                    thread.Lines.Add(new ChatLine
                    {
                        Id = terminalResult.LineId,
                        Role = "assistant",
                        Text = terminalResult.Text,
                        ReplyToLineId = update.TriggerLineId,
                        At = terminalResult.At,
                        ModelId = terminalResult.ModelId,
                        Reasoning = terminalResult.Reasoning
                    });
                }
                remoteRuns.TryRemove(update.ThreadId, out _);
                terminalRemoteRuns.Add(correlationKey);
                remoteDeltaSeq.Remove(correlationKey);
                liveAgentRenderState.EndDraft(update.ThreadId);
                assistantDraftRefreshGate.Reset(update.ThreadId);
            }
            else if (update.Delta is not { Length: > 0 })
            {
                remoteRuns[update.ThreadId] = new RemoteRunProjection
                {
                    RunId = update.RunId,
                    ThreadId = update.ThreadId,
                    Phase = update.Phase,
                    Status = update.Status,
                    Plan = update.Plan,
                    Subtasks = update.Subtasks,
                    Steps = update.Steps,
                    Queued = update.Queued,
                    Error = update.Error,
                    FailureCode = update.FailureCode,
                    Timestamp = update.Timestamp
                };
            }
        }

        ApplyQueuedTopicRunUpdate(update);
        if (!terminal && update.Delta is { Length: > 0 })
            ApplyRemoteAssistantDelta(update);
        EmitTopicUpsert(thread!, terminalUpdate: terminal ? update : null);
        NotifyChanged();
        return RemoteTopicUpdatePersistenceResult.Applied;
    }

    // Applies one reply fragment forwarded by the executing device into this device's live draft so the
    // reasoning and answer build up incrementally on a viewer instead of arriving as one block when the
    // committed line finally syncs. The executing device shows its own locally streamed draft, so this
    // no-ops there (guarded by busyThreads) to avoid double counting the fragments echoed back through the
    // local projected-progress sink. Fragments are presentation updates that may be delayed or deduplicated;
    // the committed line remains authoritative, while the per-run sequence keeps arriving fragments ordered
    // and applied exactly once.
    private void ApplyRemoteAssistantDelta(TopicRunUpdatePayload update)
    {
        if (update.Delta is not { Length: > 0 } || update.DeltaKind is null)
            return;
        var threadId = update.ThreadId;
        var correlationKey = threadId + "\0" + update.RunId;
        var appended = false;
        lock (profileSyncGate)
        {
            if (busyThreads.Contains(threadId)
                || terminalRemoteRuns.Contains(correlationKey))
                return;
            var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
            if (!RemoteRunCorrelation.IsExpected(thread, threadId, update.RunId))
                return;
            if (remoteDeltaSeq.TryGetValue(correlationKey, out var lastSeq)
                && update.DeltaSeq <= lastSeq)
                return;
            remoteDeltaSeq[correlationKey] = update.DeltaSeq;
            var kind = update.DeltaKind == TopicRunDeltaKind.Reasoning
                ? AgentDeltaKind.Reasoning
                : AgentDeltaKind.Answer;
            appended = liveAgentRenderState.AppendDraft(
                threadId, new AgentDelta(kind, update.Delta));
        }
        if (appended
            && assistantDraftRefreshGate.ShouldPublish(
                threadId,
                update.DeltaKind == TopicRunDeltaKind.Reasoning ? AgentDeltaKind.Reasoning : AgentDeltaKind.Answer,
                Environment.TickCount64)) NotifyChanged();
    }

    /// <summary>Applies a remote run update projection for a thread and refreshes the UI.</summary>
    public void ApplyRemoteRunProjection(string threadId, RemoteRunProjection projection)
    {
        if (!string.Equals(threadId, projection.ThreadId, StringComparison.Ordinal))
            return;
        _ = TryApplyRemoteRunUpdate(new TopicRunUpdatePayload(
            projection.RunId,
            projection.ThreadId,
            projection.Phase,
            projection.Status,
            projection.Plan,
            projection.Subtasks,
            projection.Steps,
            projection.Queued,
            projection.Error,
            projection.FailureCode,
            projection.Timestamp));
    }

    /// <summary>Clears the remote run projection for a thread (run completed or cancelled).</summary>
    public void ClearRemoteRunProjection(
        string threadId,
        string? runId = null,
        DateTimeOffset? clearedAt = null,
        TopicRunUpdatePayload? terminalUpdate = null)
    {
        OwnThread? thread;
        lock (profileSyncGate)
        {
            thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
            remoteRuns.TryGetValue(threadId, out var projection);
            var correlatedRunId = runId ?? projection?.RunId;
            if (!TopicRunProtocol.IsValidIdentifier(correlatedRunId)
                || !RemoteRunCorrelation.IsExpected(thread, threadId, correlatedRunId!)
                || projection is not null
                   && !string.Equals(projection.RunId, correlatedRunId, StringComparison.Ordinal))
                return;

            var at = clearedAt ?? DateTimeOffset.UtcNow;
            var activityAt = ActivityTimestamp.Advance(thread!.LastActivityAt, at);
            if (activeDb is not null
                && !activeDb.ExecuteDurableWrite(() => activeDb.SetOwnThreadExecutionAndActivity(
                    thread.Id,
                    thread.ExecutionDeviceId,
                    thread.ExecutionDeviceName,
                    thread.ExecutionDevicePlatform,
                    thread.ExecutionAt,
                    null,
                    activityAt)))
                return;
            remoteRuns.TryRemove(threadId, out _);
            terminalRemoteRuns.Add(threadId + "\0" + correlatedRunId);
            thread.ExecutionRunId = null;
            thread.LastActivityAt = activityAt;
            liveAgentRenderState.EndDraft(threadId);
            assistantDraftRefreshGate.Reset(threadId);
        }
        EmitTopicUpsert(thread!, terminalUpdate: terminalUpdate);
        NotifyChanged();
    }

    /// <summary>
    /// Finalizes durable queue state and any matching live remote-run projection when the executing
    /// device's committed assistant answer arrives via device sync. The answer and terminal update travel
    /// independently, so the answer is terminal truth for its exact trigger line even after an app restart.
    /// Runs while applying a device-sync batch (under profileSyncGate, which is reentrant), so it mutates
    /// local state only and returns true so the caller refreshes the UI.
    /// </summary>
    private bool ReconcileTopicRunWithAnswer(
        OwnThread thread,
        string? replyToLineId,
        DateTimeOffset answerAt)
    {
        lock (profileSyncGate)
        {
            remoteRuns.TryGetValue(thread.Id, out var projection);
            var hasReplyIdentity = TopicRunProtocol.IsValidIdentifier(replyToLineId);
            var queuedRun = hasReplyIdentity
                ? queuedTopicRuns.FindByLine(thread.Id, replyToLineId!)
                : null;
            MeshDb.TopicRunCorrelationItem? durableCorrelation = null;
            if (hasReplyIdentity && activeDb is not null)
            {
                durableCorrelation = activeDb.ExecuteDurableWrite(
                    () => activeDb.FindTopicRunCorrelation(thread.Id, replyToLineId!));
            }
            var runId = durableCorrelation?.RunId
                        ?? RemoteRunReconciliation.RunIdForAnswer(
                            thread.Id, replyToLineId, queuedRun, projection, answerAt);
            if (!hasReplyIdentity && activeDb is not null)
            {
                if (activeDb.ExecuteDurableWrite(
                        () => activeDb.HasTopicRunCorrelationForThread(thread.Id)))
                    return false;
                // Pre-correlation profiles persisted only the run and start time on the topic.
                // This is the sole upgrade fallback: an uncorrelated answer may finish that legacy
                // run only when no durable run identity exists for the topic.
                if (runId is null
                    && TopicRunProtocol.IsValidIdentifier(thread.ExecutionRunId)
                    && thread.ExecutionAt is { } executionAt
                    && answerAt >= executionAt)
                    runId = thread.ExecutionRunId;
            }
            if (runId is null)
                return false;

            var correlationKey = thread.Id + "\0" + runId;
            var projectionMatches = projection is not null
                                    && string.Equals(
                                        projection.RunId, runId, StringComparison.Ordinal);
            if (thread.ExecutionRunId is null
                && queuedRun is null
                && !projectionMatches
                && terminalRemoteRuns.Contains(correlationKey))
                return false;
            if (thread.ExecutionRunId is not null
                && !string.Equals(thread.ExecutionRunId, runId, StringComparison.Ordinal))
                return false;

            var activityAt = ActivityTimestamp.Advance(thread.LastActivityAt, answerAt);
            if (activeDb is not null
                && !activeDb.ExecuteDurableWrite(() => activeDb.CompleteOwnThreadRunAndDeleteTopicOutbox(
                    thread.Id,
                    runId,
                    replyToLineId,
                    thread.ExecutionDeviceId,
                    thread.ExecutionDeviceName,
                    thread.ExecutionDevicePlatform,
                    thread.ExecutionAt ?? answerAt,
                    activityAt)))
                return false;

            thread.LastActivityAt = activityAt;
            thread.ExecutionRunId = null;
            queuedTopicRuns.Complete(thread.Id, runId);
            terminalRemoteRuns.Add(correlationKey);
            remoteDeltaSeq.Remove(correlationKey);
            if (projectionMatches)
            {
                remoteRuns.TryRemove(thread.Id, out _);
                liveAgentRenderState.EndDraft(thread.Id);
                assistantDraftRefreshGate.Reset(thread.Id);
            }
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-terminal-cleanup",
                $"thread={StableDiagnosticId(thread.Id)}"
                + $";run={StableDiagnosticId(runId)}"
                + $";projection_cleared={projectionMatches.ToString().ToLowerInvariant()}"
                + ";result=converged");
            return true;
        }
    }

    private static RemoteRunProjection CloneRemoteRunProjection(RemoteRunProjection projection)
        => new()
        {
            RunId = projection.RunId,
            ThreadId = projection.ThreadId,
            Phase = projection.Phase,
            Status = projection.Status,
            Plan = projection.Plan,
            Subtasks = projection.Subtasks?.ToArray(),
            Steps = projection.Steps?.ToArray(),
            Queued = projection.Queued,
            Error = projection.Error,
            FailureCode = projection.FailureCode,
            Timestamp = projection.Timestamp
        };

    public void UpdateAgentRun(
        string threadId,
        AgentRunPhase phase,
        IReadOnlyList<AgentSubtaskState>? subtasks = null,
        DateTimeOffset? updatedAt = null)
    {
        if (!agentRuns.TryGetValue(threadId, out var run)) return;
        agentRuns[threadId] = run with { Phase = phase, Subtasks = subtasks ?? run.Subtasks };
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is not null)
        {
            var notificationIntent = phase switch
            {
                AgentRunPhase.Completed => NotificationIntents.Topic(
                    run.RunId,
                    thread.Id,
                    thread.Title,
                    NotificationKind.TopicCompleted,
                    LatestAssistantResponse(thread)),
                AgentRunPhase.Failed => NotificationIntents.Topic(run.RunId, thread.Id, thread.Title, NotificationKind.TopicFailed),
                AgentRunPhase.Cancelled => NotificationIntents.Topic(run.RunId, thread.Id, thread.Title, NotificationKind.TopicCancelled),
                _ => null
            };

            var at = updatedAt ?? DateTimeOffset.UtcNow;
            thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, at);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.SetOwnThreadActivity(thread.Id, thread.LastActivityAt!.Value));
            EmitTopicUpsert(thread, notificationIntent);
        }
        NotifyChanged();
    }

    private static string? LatestAssistantResponse(OwnThread thread)
        => thread.Lines
            .LastOrDefault(line =>
                string.Equals(line.Role, "assistant", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(line.Text))
            ?.Text;

    /// <summary>True while the given own-thread is specifically building a widget (for the label text).</summary>
    public bool IsThreadBuilding(string threadId) => buildingThreads.Contains(threadId);

    /// <summary>True when an own-thread's agent finished while that topic was not being viewed.</summary>
    public bool IsThreadCompleted(string threadId) => completedThreads.Contains(threadId);

    /// <summary>Marks an own-thread as needing attention because its agent run finished.</summary>
    public void MarkThreadCompleted(string threadId)
    {
        if (completedThreads.Add(threadId)) NotifyChanged();
    }

    /// <summary>Clears a topic's completion indicator when the owner opens it.</summary>
    public void MarkThreadSeen(string threadId)
    {
        if (completedThreads.Remove(threadId)) NotifyChanged();
        NotificationCoordinatorBridge.MarkEntityRead(threadId);
    }

    /// <summary>
    /// Marks a thread's turn as started (optionally a widget build) and returns a CancellationToken the
    /// caller must pass into the agent call, so the user can stop the turn. Replaces any prior source
    /// for the thread.
    /// </summary>
    public CancellationToken BeginThreadTurn(string threadId, bool building)
    {
        if (threadCts.Remove(threadId, out var old)) old.Dispose();
        cancelledThreads.Remove(threadId);
        var cts = new CancellationTokenSource();
        threadCts[threadId] = cts;
        busyThreads.Add(threadId);
        if (building) buildingThreads.Add(threadId);
        NotifyChanged();
        return cts.Token;
    }

    /// <summary>Clears the widget-building flag (e.g. once the build step is done) while a turn continues.</summary>
    public void ClearThreadBuilding(string threadId)
    {
        if (buildingThreads.Remove(threadId)) NotifyChanged();
    }

    /// <summary>
    /// Requests cancellation of a thread's in-progress turn. Returns true if a turn was actually
    /// running. The turn's task observes the token, stops, and the caller records it as cancelled.
    /// </summary>
    public bool CancelThreadTurn(string threadId)
    {
        if (!threadCts.TryGetValue(threadId, out var cts)) return false;
        cancelledThreads.Add(threadId);
        try { cts.Cancel(); } catch { }
        NotifyChanged();
        return true;
    }

    /// <summary>True when the thread's current/just-finished turn was cancelled by the user.</summary>
    public bool WasThreadCancelled(string threadId) => cancelledThreads.Contains(threadId);

    /// <summary>Marks a thread's turn as finished (clears busy + building + its cancellation source).</summary>
    public void EndThreadTurn(string threadId)
    {
        var a = busyThreads.Remove(threadId);
        var b = buildingThreads.Remove(threadId);
        if (threadCts.Remove(threadId, out var cts)) cts.Dispose();
        if (agentRuns.TryGetValue(threadId, out var run) &&
            run.Phase is not (AgentRunPhase.Completed or AgentRunPhase.Failed or AgentRunPhase.Cancelled))
            agentRuns[threadId] = run with { Phase = AgentRunPhase.Completed };
        if (a || b) NotifyChanged();
    }

    /// <summary>Marks a submitted topic run as waiting behind the active turn.</summary>
    public void TrackQueuedTopicRun(
        string threadId,
        string runId,
        string lineId,
        TopicQueueStage stage = TopicQueueStage.Sending)
    {
        ValidateThreadId(threadId);
        if (!TopicRunProtocol.IsValidIdentifier(runId))
            throw new ArgumentException("A run ID is required.", nameof(runId));
        if (!TopicRunProtocol.IsValidIdentifier(lineId))
            throw new ArgumentException("A trigger line ID is required.", nameof(lineId));
        bool changed;
        lock (profileSyncGate)
        {
            if (!Profile.OwnThreads.Any(thread =>
                    string.Equals(thread.Id, threadId, StringComparison.Ordinal)))
                return;
            changed = queuedTopicRuns.MarkWaiting(threadId, runId, lineId, stage);
        }
        if (changed) NotifyChanged();
    }

    public void SetQueuedTopicRunStage(string threadId, string runId, TopicQueueStage stage)
    {
        bool changed;
        lock (profileSyncGate)
            changed = queuedTopicRuns.SetStage(threadId, runId, stage);
        if (changed) NotifyChanged();
    }

    /// <summary>Hides the queued subtitle once a waiting run begins while retaining its correlation.</summary>
    public void StartQueuedTopicRun(string threadId, string runId)
    {
        bool changed;
        lock (profileSyncGate)
            changed = queuedTopicRuns.MarkStarted(threadId, runId);
        if (changed) NotifyChanged();
    }

    /// <summary>Forgets a queued run after completion, cancellation, or dispatch failure.</summary>
    public void CompleteQueuedTopicRun(string threadId, string runId)
    {
        bool changed;
        lock (profileSyncGate)
            changed = queuedTopicRuns.Complete(threadId, runId);
        if (changed) NotifyChanged();
    }

    /// <summary>
    /// True when an update belongs to a queued run already tracked on this device, or is the first
    /// queue update for a user line already present in the topic.
    /// </summary>
    public bool IsExpectedQueuedRunUpdate(TopicRunUpdatePayload update)
    {
        lock (profileSyncGate)
        {
            if (terminalRemoteRuns.Contains(update.ThreadId + "\0" + update.RunId))
                return false;
            if (queuedTopicRuns.Matches(
                    update.ThreadId, update.RunId, update.TriggerLineId)) return true;
            if (update.Phase != TopicRunPhase.Queued
                || !TopicRunProtocol.IsValidIdentifier(update.TriggerLineId))
                return false;
            var thread = Profile.OwnThreads.FirstOrDefault(item =>
                string.Equals(item.Id, update.ThreadId, StringComparison.Ordinal));
            return thread is not null
                   && thread.Lines.Any(line =>
                       string.Equals(line.Id, update.TriggerLineId, StringComparison.Ordinal)
                       && string.Equals(line.Role, "user", StringComparison.Ordinal))
                   && !RemoteRunReconciliation.HasCommittedAnswer(
                       thread.Lines, update.TriggerLineId);
        }
    }

    public bool IsExpectedTopicRunCorrelation(
        TopicRunUpdatePayload update,
        string sourceDeviceId,
        bool allowRetained)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        lock (profileSyncGate)
        {
            var thread = Profile.OwnThreads.FirstOrDefault(item =>
                string.Equals(item.Id, update.ThreadId, StringComparison.Ordinal));
            var current = RemoteRunCorrelation.IsExpected(
                thread, update.ThreadId, update.RunId);
            var queued = IsExpectedQueuedRunUpdate(update);
            var correlation = activeDb?.GetTopicRunCorrelation(update.RunId);
            if (correlation is not null
                && correlation.TriggerLineId is null
                && (current || queued)
                && TopicRunProtocol.IsValidIdentifier(update.TriggerLineId)
                && activeDb!.ExecuteDurableWrite(() =>
                    activeDb.TryBindLegacyTopicRunCorrelation(
                        update.RunId,
                        update.ThreadId,
                        sourceDeviceId,
                        update.TriggerLineId!)))
            {
                correlation = activeDb.GetTopicRunCorrelation(update.RunId);
            }
            var durableIdentity = correlation is not null
                                  && string.Equals(
                                      correlation.ThreadId, update.ThreadId, StringComparison.Ordinal)
                                  && string.Equals(
                                      correlation.TargetDeviceId, sourceDeviceId, StringComparison.Ordinal)
                                  && string.Equals(
                                      correlation.TriggerLineId,
                                      update.TriggerLineId,
                                      StringComparison.Ordinal);
            if (correlation is null || !durableIdentity)
                return false;
            return (current || queued)
                   || allowRetained && durableIdentity && correlation!.TerminalAt is not null;
        }
    }

    private void ApplyQueuedTopicRunUpdate(TopicRunUpdatePayload update)
    {
        if (update.Phase == TopicRunPhase.Queued)
        {
            if (TopicRunProtocol.IsValidIdentifier(update.TriggerLineId))
                TrackQueuedTopicRun(
                    update.ThreadId, update.RunId, update.TriggerLineId!, TopicQueueStage.Device);
            return;
        }
        if (update.Phase is TopicRunPhase.Completed or TopicRunPhase.Failed or TopicRunPhase.Cancelled)
        {
            CompleteQueuedTopicRun(update.ThreadId, update.RunId);
            return;
        }
        StartQueuedTopicRun(update.ThreadId, update.RunId);
    }

    /// <summary>True when a specific line is still waiting in some thread's queue (drives the "queued" tag).</summary>
    public bool IsLineQueued(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return queuedTopicRuns.IsLineWaiting(line.Id);
    }

    public QueuedTopicRunInfo? QueuedTopicRunForLine(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return queuedTopicRuns.FindByLine(line.Id);
    }

    public bool IsQueuedTopicRunLine(string threadId, string runId, string lineId)
    {
        var queued = queuedTopicRuns.FindByLine(threadId, lineId);
        return queued is { Waiting: true }
               && string.Equals(queued.ThreadId, threadId, StringComparison.Ordinal)
               && string.Equals(queued.RunId, runId, StringComparison.Ordinal);
    }

    public bool RemoveCancelledQueuedTopicLine(string threadId, string runId, string lineId)
    {
        if (!TopicRunProtocol.IsValidIdentifier(threadId)
            || !TopicRunProtocol.IsValidIdentifier(runId)
            || !TopicRunProtocol.IsValidIdentifier(lineId))
            return false;

        var visibleChanged = false;
        var deleted = false;
        lock (profileSyncGate)
        {
            var queued = queuedTopicRuns.FindByLine(threadId, lineId);
            if (queued is not null
                && (!string.Equals(queued.ThreadId, threadId, StringComparison.Ordinal)
                    || !string.Equals(queued.RunId, runId, StringComparison.Ordinal)))
                return false;

            var thread = Profile.OwnThreads.FirstOrDefault(item =>
                string.Equals(item.Id, threadId, StringComparison.Ordinal));
            if (thread is null) return false;

            var trigger = thread.Lines.FirstOrDefault(line =>
                string.Equals(line.Id, lineId, StringComparison.Ordinal));
            if (trigger is null)
            {
                visibleChanged = queuedTopicRuns.Complete(threadId, runId);
                deleted = true;
            }
            else
            {
                if (!string.Equals(trigger.Role, "user", StringComparison.Ordinal))
                    return false;
                var removed = thread.Lines.Where(line =>
                        string.Equals(line.Id, lineId, StringComparison.Ordinal)
                        || string.Equals(line.ReplyToLineId, lineId, StringComparison.Ordinal))
                    .ToList();
                thread.Lines.RemoveAll(line =>
                    string.Equals(line.Id, lineId, StringComparison.Ordinal)
                    || string.Equals(line.ReplyToLineId, lineId, StringComparison.Ordinal));
                activeDb?.ExecuteDurableWrite(
                    () => activeDb.DeleteOwnChatLine(threadId, lineId));
                queuedTopicRuns.Complete(threadId, runId);
                EmitTombstone("topic.line.delete", DomainProjectionEntityIds.TopicLine(threadId, lineId));
                visibleChanged = true;
                deleted = true;
            }
        }
        if (visibleChanged) NotifyChanged();
        return deleted;
    }

    public bool IsKnownQueuedTopicRun(string threadId, string runId)
        => queuedTopicRuns.IsKnownRun(threadId, runId);

    /// <summary>Number of lines currently queued for a thread.</summary>
    public int QueuedCountForThread(string threadId)
        => queuedTopicRuns.WaitingCount(threadId);

    /// <summary>Clears transient queue presentation state for a topic.</summary>
    public void ClearThreadQueue(string threadId)
    {
        bool changed;
        lock (profileSyncGate)
            changed = queuedTopicRuns.ClearThread(threadId);
        if (changed) NotifyChanged();
    }

    /// <summary>Clears the unread flag for a conversation (called when the owner opens it).</summary>
    public void MarkRead(string handle)
    {
        var h = Norm(handle);
        NotificationCoordinatorBridge.MarkEntityRead(h);
        var changed = unread.Remove(h);
        if (Profile.UnreadFrom.Remove(h)) { ScheduleProfileSave(); changed = true; }
        if (changed)
        {
            EmitReadWatermark(h);
            NotifyChanged();
        }
    }

    /// <summary>Updates an outgoing line's delivery status (persisted) and refreshes the UI.</summary>
    public void SetLineStatus(string lineId, string status)
    {
        Conversation? owner = null;
        ChatLine? updated = null;
        foreach (var conv in Profile.Conversations)
        {
            var line = conv.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is not null)
            {
                line.Status = status;
                owner = conv;
                updated = line;
                break;
            }
        }
        activeDb?.UpdateLineStatus(lineId, status);
        if (owner is not null && updated is not null)
            EmitLineUpsert("conversation.line", owner.Handle, updated);
        NotifyChanged();
    }

    /// <summary>Updates an outgoing line after widget/file content is finalized and persists it.</summary>
    public void SetLineText(string lineId, string text)
    {
        Conversation? owner = null;
        ChatLine? updated = null;
        foreach (var conv in Profile.Conversations)
        {
            var line = conv.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is not null)
            {
                line.Text = text;
                owner = conv;
                updated = line;
                break;
            }
        }
        activeDb?.UpdateLineText(lineId, text);
        if (owner is not null && updated is not null)
            EmitLineUpsert("conversation.line", owner.Handle, updated);
        NotifyChanged();
    }

    /// <summary>Searches all chat history for a query string. Empty when no active database.</summary>
    public IReadOnlyList<MeshDb.SearchHit> SearchHistory(string query)
        => activeDb is not null ? activeDb.Search(query) : new List<MeshDb.SearchHit>();

    /// <summary>
    /// Attributes tokens spent answering a contact's request to that contact's lifetime tally, so
    /// the owner can see who is costing them tokens. Creates a lightweight contact record if needed.
    /// </summary>
    public void AddContactTokens(string handle, long promptTokens, long completionTokens)
    {
        var total = Math.Max(0, promptTokens) + Math.Max(0, completionTokens);
        if (total <= 0) return;
        lock (profileSyncGate)
        {
            var h = Norm(handle);
            var contact = FindContact(h);
            if (contact is null)
            {
                contact = new Domain.Contact { Handle = h, Allowed = false };
                Profile.Contacts.Add(contact);
            }
            contact.TokensSpent += total;
            Save();
        }
        NotifyChanged();
    }

    // ---- handle recovery keys --------------------------------------------

    /// <summary>
    /// Ensures the handle recovery keypair exists (generated once at onboarding). The public half
    /// is registered with the relay; the private half travels only inside a passphrase-encrypted
    /// export so a new device can re-authorize under the same handle when no device is available.
    /// </summary>
    public void EnsureRecoveryKeys()
    {
        if (!string.IsNullOrWhiteSpace(Profile.RecoveryPrivateKey)
            && !string.IsNullOrWhiteSpace(Profile.RecoveryPublicKey)) return;
        var (priv, pub) = IdentityService.GenerateKeyPair();
        Profile.RecoveryPrivateKey = priv;
        Profile.RecoveryPublicKey = pub;
    }

    // ---- export / import --------------------------------------------------

    /// <summary>Produces a portable, passphrase-encrypted export of the active identity.</summary>
    public byte[] ExportActiveProfile(string passphrase)
    {
        FlushBlocking();
        return MeshExport.Create(BuildExportBundle(), passphrase);
    }

    public string ImportProfile(MeshExportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var id = ImportProfile(bundle.Profile);
        ImportSkillPackages(bundle.SkillPackages, id);
        return id;
    }

    /// <summary>
    /// Imports a profile bundle as a NEW identity on this device: mints a fresh device keypair,
    /// keeps the recovery keys and all data from the bundle, writes them to a new encrypted
    /// database, and makes it the active identity. Returns the new local account id. The caller is
    /// responsible for authorizing the new device key under the handle (link or recovery).
    /// </summary>
    public string ImportProfile(MeshProfile imported)
    {
        var (priv, pub) = IdentityService.GenerateKeyPair();
        imported.PrivateKey = priv;
        imported.PublicKey = pub;

        if (activeId is not null && activeDb is not null)
        {
            FlushBlocking();
            RaiseActiveAccountChanging();
        }

        var id = NewId();
        var db = OpenDb(id);
        foreach (var conv in imported.Conversations)
        {
            conv.Handle = PrepareConversationForPersistence(conv);
            DeriveActivityMetadata(conv);
            db.ExecuteDurableWrite(() => db.EnsureConversation(conv.Handle, conv.CreatedAt));
            PersistConversationMetadata(db, conv);
            if (conv.LastActivityAt.HasValue)
                db.ExecuteDurableWrite(
                    () => db.SetConversationActivity(conv.Handle, conv.LastActivityAt.Value));
            if (conv.IsPinned)
                db.ExecuteDurableWrite(() => db.SetConversationPin(conv.Handle, true));
            foreach (var line in conv.Lines)
                db.ExecuteDurableWrite(() => db.AppendChatLine(Norm(conv.Handle), line));
        }
        // Migrate a legacy single OwnChat (older exports) into a thread so nothing is lost.
        if (imported.OwnChat.Count > 0)
        {
            var lines = imported.OwnChat.ToList();
            var legacy = new OwnThread
            {
                Title = "General",
                Lines = lines,
                CreatedAt = lines.Count == 0 ? DateTimeOffset.UnixEpoch : lines.Min(line => line.At),
                LastActivityAt = lines.Count == 0 ? DateTimeOffset.UnixEpoch : lines.Max(line => line.At)
            };
            imported.OwnThreads.Insert(0, legacy);
            imported.OwnChat = new List<ChatLine>();
        }
        foreach (var thread in imported.OwnThreads)
        {
            thread.LastActivityAt ??= thread.Lines.Count == 0
                ? thread.CreatedAt
                : thread.Lines.Max(line => line.At);
            db.ExecuteDurableWrite(
                () => db.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt));
            if (thread.LastActivityAt.HasValue)
                db.ExecuteDurableWrite(
                    () => db.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value));
            if (thread.IsPinned)
                db.ExecuteDurableWrite(() => db.SetOwnThreadPin(thread.Id, true));
            if (thread.ExecutionDeviceId is not null || thread.ExecutionAt.HasValue || thread.ExecutionRunId is not null)
                db.ExecuteDurableWrite(() => db.SetOwnThreadExecution(
                    thread.Id,
                    thread.ExecutionDeviceId,
                    thread.ExecutionAt,
                    thread.ExecutionRunId,
                    thread.ExecutionDeviceName,
                    thread.ExecutionDevicePlatform));
            foreach (var line in thread.Lines)
                db.ExecuteDurableWrite(() => db.AppendOwnChat(thread.Id, line));
        }
        for (var i = 0; i < imported.Memories.Count; i++)
        {
            var memory = MemoryPolicy.Normalize(imported.Memories[i]);
            imported.Memories[i] = memory;
            db.ExecuteDurableWrite(() => db.UpsertMemory(memory));
        }
        db.ExecuteDurableWrite(() => db.SaveProfile(imported));

        lock (profileSyncGate)
        {
            Volatile.Write(ref activeDatabaseIdentity, null);
            activeDb?.Dispose();
            activeDb = db;
            activeId = id;
            Volatile.Write(
                ref activeDatabaseIdentity,
                NewActiveDatabaseIdentity(db, id));
            Profile = imported;
            MigrateAndHydrateAssets(db);
            RehydrateUnread();
            RehydrateTopicExecutionState();
            NotificationCoordinatorBridge.ResetForAccount();
            accounts.Add(new AccountRef { Id = id, Handle = imported.Handle, DisplayName = imported.DisplayName });
            WriteIndex();
        }
        NotifyChanged();
        return id;
    }

    // ---- multi-account -----------------------------------------------------

    /// <summary>
    /// Sign out of the active identity WITHOUT deleting it. The database stays on disk so it can
    /// be switched back to; the app returns to onboarding / the account picker.
    /// </summary>
    public void SignOut()
    {
        if (activeId is not null)
        {
            FlushBlocking();
            RaiseActiveAccountChanging();
        }
        lock (profileSyncGate)
        {
            Volatile.Write(ref activeDatabaseIdentity, null);
            activeDb?.Dispose();
            activeDb = null;
            activeId = null;
            Profile = new MeshProfile();
            ResetAssetState();
            queuedTopicRuns.Clear();
            NotificationCoordinatorBridge.ResetForAccount();
            WriteIndex();
        }
        NotifyChanged();
    }

    /// <summary>Switch the active identity to a previously saved account.</summary>
    public bool SwitchAccount(string id)
    {
        lock (profileSyncGate)
        {
            if (id == activeId) return true;
        }
        MeshDb? db = null;
        try
        {
            db = OpenDb(id);
            var loaded = db.LoadProfile();
            if (loaded is null) { db.Dispose(); return false; }
            if (activeId is not null) FlushBlocking();
            RaiseActiveAccountChanging();

            lock (profileSyncGate)
            {
                if (id == activeId)
                {
                    db.Dispose();
                    return true;
                }
                Volatile.Write(ref activeDatabaseIdentity, null);
                activeDb?.Dispose();
                activeDb = db;
                activeId = id;
                Volatile.Write(
                    ref activeDatabaseIdentity,
                    NewActiveDatabaseIdentity(db, id));
                Profile = loaded;
                MigrateAndHydrateAssets(db);
                RehydrateUnread();
                RehydrateTopicExecutionState();
                NotificationCoordinatorBridge.ResetForAccount();
                WriteIndex();
            }
            NotifyChanged();
            return true;
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("account-switch", ex);
            db?.Dispose();
            return false;
        }
    }

    /// <summary>Permanently remove a saved identity: its database file and its master key.</summary>
    public void DeleteAccount(string id)
    {
        if (string.Equals(id, activeId, StringComparison.Ordinal))
        {
            FlushBlocking();
            RaiseActiveAccountChanging();
        }
        lock (profileSyncGate)
        {
            accounts.RemoveAll(a => a.Id == id);
            if (id == activeId)
            {
                Volatile.Write(ref activeDatabaseIdentity, null);
                activeDb?.Dispose();
                activeDb = null;
                activeId = null;
                Profile = new MeshProfile();
                ResetAssetState();
                queuedTopicRuns.Clear();
                NotificationCoordinatorBridge.ResetForAccount();
            }
            WriteIndex();
        }
        try { var p = DbPath(id); if (File.Exists(p)) File.Delete(p); } catch { }
        secrets.DeleteDbKey(id);
        NotifyChanged();
    }

    /// <summary>True if any saved identity on this device already uses the given handle.</summary>
    public bool HasLocalHandle(string handle)
    {
        var h = Norm(handle);
        return accounts.Any(a => Norm(a.Handle ?? "") == h);
    }

    /// <summary>
    /// Reads a saved identity's handle and keypair without switching to it, by opening its encrypted
    /// database read-only. Used so deleting a non-active identity can still authenticate the relay
    /// handle release. Returns null if the identity can't be opened. The active identity is read from
    /// the in-memory profile directly.
    /// </summary>
    public (string handle, string privateKey, string publicKey)? PeekIdentityKeys(string id)
    {
        if (id == activeId)
            return (Profile.Handle, Profile.PrivateKey, Profile.PublicKey);
        MeshDb? db = null;
        try
        {
            db = OpenDb(id);
            var p = db.LoadProfile();
            if (p is null || string.IsNullOrWhiteSpace(p.PublicKey)) return null;
            return (p.Handle, p.PrivateKey, p.PublicKey);
        }
        catch { return null; }
        finally { db?.Dispose(); }
    }

    // ---- helpers ----------------------------------------------------------
    public Domain.Contact? FindContact(string handle)
        => Profile.Contacts.FirstOrDefault(c => c.Handle.Equals(Norm(handle), StringComparison.OrdinalIgnoreCase));

    /// <summary>Synthetic conversation key for a service thread: <c>svc:{provider}:{serviceId}</c>.</summary>
    public static string ServiceKey(string providerHandle, string serviceId)
        => "svc:" + Norm(providerHandle) + ":" + serviceId;

    /// <summary>Synthetic conversation key for a group thread: <c>grp:{normalizedGroupId}</c>.</summary>
    public static string GroupKey(string groupId)
    {
        var normalized = NormalizeGroupId(groupId);
        return "grp:" + normalized;
    }

    /// <summary>Finds a conversation by its (already-known) key, or null.</summary>
    public Conversation? FindConversation(string handle)
    {
        var h = Norm(handle);
        return Profile.Conversations.FirstOrDefault(c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds a group conversation by its group identifier, or null.</summary>
    public Conversation? FindGroupConversation(string groupId) => FindConversation(GroupKey(groupId));

    /// <summary>
    /// Creates a group conversation from a complete snapshot or applies the snapshot to the existing
    /// group thread. Metadata is normalized, validated, persisted, and never sent to the relay here.
    /// </summary>
    public Conversation GetOrCreateGroupConversation(GroupSnapshotPayload snapshot)
        => ApplyGroupSnapshot(snapshot);

    /// <summary>Convenience overload for locally creating a complete group snapshot.</summary>
    public Conversation GetOrCreateGroupConversation(
        string groupId,
        string name,
        string ownerHandle,
        IEnumerable<string> memberHandles,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(memberHandles);
        return ApplyGroupSnapshot(new GroupSnapshotPayload(
            groupId, name, ownerHandle, memberHandles.ToList(), version));
    }

    /// <summary>Validates and applies a complete group metadata snapshot.</summary>
    public Conversation ApplyGroupSnapshot(GroupSnapshotPayload snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = NormalizeGroupSnapshot(snapshot);
        var key = GroupKey(normalized.GroupId);
        var conv = FindConversation(key);

        if (conv is not null && !conv.IsGroup)
            throw new InvalidOperationException($"Conversation key '{key}' is not a group thread.");
        if (conv is not null && normalized.Version < conv.GroupVersion)
            throw new InvalidOperationException("A group snapshot cannot roll membership back to an older version.");
        if (conv is not null && normalized.Version == conv.GroupVersion)
        {
            if (!string.Equals(conv.GroupName, normalized.Name, StringComparison.Ordinal)
                || !string.Equals(conv.GroupOwnerHandle, normalized.OwnerHandle, StringComparison.OrdinalIgnoreCase)
                || !conv.GroupMembers.SequenceEqual(normalized.MemberHandles, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Conflicting group metadata has the same membership version.");
            return conv;
        }

        if (conv is null)
        {
            conv = new Conversation { Handle = key };
            Profile.Conversations.Add(conv);
        }

        conv.GroupId = normalized.GroupId;
        conv.GroupName = normalized.Name;
        conv.GroupOwnerHandle = normalized.OwnerHandle;
        conv.GroupMembers = normalized.MemberHandles.ToList();
        conv.GroupVersion = normalized.Version;
        activeDb?.ExecuteDurableWrite(() => activeDb.SetConversationGroup(
            key, conv.GroupId, conv.GroupName, conv.GroupOwnerHandle, conv.GroupMembers, conv.GroupVersion));
        EmitConversationUpsert(conv);
        NotifyChanged();
        return conv;
    }

    /// <summary>
    /// Gets or creates the service thread for a (provider, service) pair, keyed distinctly so it never
    /// collides with a person DM or a sibling service, and carrying the real provider handle to route
    /// follow-up ServiceRequests to. Persists the service metadata so the thread survives a restart.
    /// </summary>
    public Conversation GetOrCreateServiceConversation(string providerHandle, string serviceId, string? serviceName)
    {
        var key = ServiceKey(providerHandle, serviceId);
        var provider = Norm(providerHandle);
        var name = string.IsNullOrWhiteSpace(serviceName) ? serviceId : serviceName!.Trim();
        var conv = FindConversation(key);
        var changed = false;
        if (conv is null)
        {
            conv = new Conversation { Handle = key, ServiceId = serviceId, ServiceName = name, ProviderHandle = provider };
            Profile.Conversations.Add(conv);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.SetConversationService(key, serviceId, name, provider));
            changed = true;
        }
        else if (conv.ServiceId != serviceId
                 || conv.ServiceName != name
                 || conv.ProviderHandle != provider)
        {
            conv.ServiceId = serviceId;
            conv.ServiceName = name;
            conv.ProviderHandle = provider;
            activeDb?.ExecuteDurableWrite(
                () => activeDb.SetConversationService(key, serviceId, name, provider));
            changed = true;
        }
        if (changed) EmitConversationUpsert(conv);
        NotifyChanged();
        return conv;
    }

    public Conversation GetOrCreateConversation(string handle)
    {
        handle = Norm(handle);
        var conv = Profile.Conversations.FirstOrDefault(c => c.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (conv is null)
        {
            conv = new Conversation { Handle = handle, CreatedAt = DateTimeOffset.UtcNow };
            Profile.Conversations.Add(conv);
            activeDb?.ExecuteDurableWrite(() => activeDb.EnsureConversation(handle));
            EmitConversationUpsert(conv);
        }
        return conv;
    }

    /// <summary>Moves one message conversation to the requested list position and persists the order.</summary>
    public void ReorderConversation(string handle, int newIndex)
    {
        Conversation conversation;
        lock (profileSyncGate)
        {
            var normalized = Norm(handle);
            var oldIndex = Profile.Conversations.FindIndex(
                c => c.Handle.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (oldIndex < 0 || Profile.Conversations.Count < 2) return;
            newIndex = Math.Clamp(newIndex, 0, Profile.Conversations.Count - 1);
            if (oldIndex == newIndex) return;
            conversation = Profile.Conversations[oldIndex];
            var ordered = Profile.Conversations.ToList();
            ordered.RemoveAt(oldIndex);
            ordered.Insert(newIndex, conversation);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.ReorderConversations(ordered.Select(c => c.Handle).ToList()));
            Profile.Conversations.RemoveAt(oldIndex);
            Profile.Conversations.Insert(newIndex, conversation);
        }
        NotifyChanged();
    }

    /// <summary>Moves a conversation to the requested list position. Alias for ReorderConversation.</summary>
    public void MoveConversation(string handle, int newIndex) => ReorderConversation(handle, newIndex);

    /// <summary>Pins a conversation so it sorts first. Bumps activity.</summary>
    public void PinConversation(string handle)
        => SetConversationPinned(handle, true);

    public void SetConversationPinned(string handle, bool pinned)
    {
        Conversation? conv;
        lock (profileSyncGate)
        {
            var h = Norm(handle);
            conv = Profile.Conversations.FirstOrDefault(
                c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
            if (conv is null || conv.IsPinned == pinned) return;
            var at = DateTimeOffset.UtcNow;
            var activityAt = ActivityTimestamp.Advance(conv.LastActivityAt, at);
            activeDb?.ExecuteDurableWrite(
                () => activeDb.SetConversationPinAndActivity(h, pinned, activityAt));
            conv.IsPinned = pinned;
            conv.LastActivityAt = activityAt;
        }
        EmitConversationUpsert(conv);
        NotifyChanged();
    }

    /// <summary>Unpins a conversation. Bumps activity.</summary>
    public void UnpinConversation(string handle)
        => SetConversationPinned(handle, false);

    /// <summary>Clears all message history for a conversation but keeps it in the list.</summary>
    public void ClearConversation(string handle)
    {
        var h = Norm(handle);
        var conv = Profile.Conversations.FirstOrDefault(c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
        if (conv is null) return;
        conv.Lines.Clear();
        EmitTombstone("conversation.clear", h);
        NotifyChanged();
    }

    /// <summary>Deletes a conversation and its history entirely (the contact itself is kept).</summary>
    public void DeleteConversation(string handle)
    {
        var h = Norm(handle);
        var conversation = Profile.Conversations.FirstOrDefault(
            c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
        if (conversation is null) return;
        if (activeDb is { } db)
            draftPersistence?.Forget(db, ComposerDraftKind.Conversation, h);
        Profile.Conversations.Remove(conversation);
        unread.Remove(h);
        if (Profile.UnreadFrom.Remove(h)) ScheduleProfileSave();
        EmitTombstone("conversation.delete", h);
        NotifyChanged();
    }

    public static string Norm(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();

    private static void ValidateThreadId(string threadId)
    {
        if (!TopicRunProtocol.IsValidIdentifier(threadId))
            throw new ArgumentException("A valid topic ID is required.", nameof(threadId));
    }

    private static void ValidateExecutionDevice(ExecutionDevice target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!TopicRunProtocol.IsValidIdentifier(target.DeviceId))
            throw new ArgumentException("A valid execution device ID is required.", nameof(target));
        if (target.DeviceName is not null
            && (string.IsNullOrWhiteSpace(target.DeviceName)
                || target.DeviceName.Length > TopicRunProtocol.MaxIdChars
                || !string.Equals(target.DeviceName, target.DeviceName.Trim(), StringComparison.Ordinal)
                || target.DeviceName.Any(char.IsControl)))
            throw new ArgumentException("The execution device name is invalid.", nameof(target));
        if (!TopicRunProtocol.IsValidIdentifier(target.Platform))
            throw new ArgumentException("A valid execution device platform is required.", nameof(target));
    }

    /// <summary>Friendly display name for a group/service thread, contact, or handle.</summary>
    public string DisplayNameFor(string handle)
    {
        var conv = FindConversation(handle);
        if (conv?.IsGroup == true) return string.IsNullOrWhiteSpace(conv.GroupName) ? Norm(handle) : conv.GroupName!;
        if (conv?.IsService == true) return string.IsNullOrWhiteSpace(conv.ServiceName) ? Norm(handle) : conv.ServiceName!;
        var c = FindContact(handle);
        if (c is not null && !string.IsNullOrWhiteSpace(c.DisplayName)) return c.DisplayName!;
        return Norm(handle);
    }

    private static string NormalizeGroupId(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("Group ID is required.", nameof(groupId));
        return groupId.Trim().ToLowerInvariant();
    }

    private static GroupSnapshotPayload NormalizeGroupSnapshot(GroupSnapshotPayload snapshot)
    {
        var groupId = NormalizeGroupId(snapshot.GroupId);
        if (string.IsNullOrWhiteSpace(snapshot.Name))
            throw new ArgumentException("Group name is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.OwnerHandle))
            throw new ArgumentException("Group owner handle is required.", nameof(snapshot));
        if (snapshot.MemberHandles is null)
            throw new ArgumentException("Group members are required.", nameof(snapshot));
        if (snapshot.Version < 1)
            throw new ArgumentException("Group version must be at least 1.", nameof(snapshot));

        var owner = Norm(snapshot.OwnerHandle);
        if (owner.Length == 0)
            throw new ArgumentException("Group owner handle is invalid after normalization.", nameof(snapshot));
        var members = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in snapshot.MemberHandles)
        {
            if (string.IsNullOrWhiteSpace(member))
                throw new ArgumentException("Group member handles cannot be empty.", nameof(snapshot));
            var normalized = Norm(member);
            if (normalized.Length == 0)
                throw new ArgumentException("Group member handles must be valid after normalization.", nameof(snapshot));
            if (seen.Add(normalized)) members.Add(normalized);
        }

        if (members.Count < 2)
            throw new ArgumentException("A group requires at least two distinct members.", nameof(snapshot));
        if (!seen.Contains(owner))
            throw new ArgumentException("The group owner must be included in the member list.", nameof(snapshot));

        return new GroupSnapshotPayload(groupId, snapshot.Name.Trim(), owner, members, snapshot.Version);
    }

    private static string PrepareConversationForPersistence(Conversation conversation)
    {
        if (!conversation.IsGroup) return conversation.Handle;

        var normalized = NormalizeGroupSnapshot(new GroupSnapshotPayload(
            conversation.GroupId!,
            conversation.GroupName ?? "",
            conversation.GroupOwnerHandle ?? "",
            conversation.GroupMembers,
            conversation.GroupVersion));
        conversation.GroupId = normalized.GroupId;
        conversation.GroupName = normalized.Name;
        conversation.GroupOwnerHandle = normalized.OwnerHandle;
        conversation.GroupMembers = normalized.MemberHandles.ToList();
        conversation.GroupVersion = normalized.Version;
        return GroupKey(normalized.GroupId);
    }

    private static void PersistConversationMetadata(MeshDb db, Conversation conversation)
    {
        if (conversation.IsGroup)
            db.ExecuteDurableWrite(() => db.SetConversationGroup(
                conversation.Handle,
                conversation.GroupId!,
                conversation.GroupName
                    ?? throw new InvalidOperationException($"Group conversation '{conversation.Handle}' has no name."),
                conversation.GroupOwnerHandle
                    ?? throw new InvalidOperationException($"Group conversation '{conversation.Handle}' has no owner."),
                conversation.GroupMembers,
                conversation.GroupVersion));
    }

    private static void DeriveActivityMetadata(Conversation conversation)
    {
        if (conversation.CreatedAt is null)
            conversation.CreatedAt = conversation.Lines.Count == 0
                ? DateTimeOffset.UnixEpoch
                : conversation.Lines.Min(line => line.At);
        conversation.LastActivityAt ??= conversation.Lines.Count == 0
            ? conversation.CreatedAt
            : conversation.Lines.Max(line => line.At);
    }

    // ---- circles ----------------------------------------------------------
    public IEnumerable<string> CircleNames => Profile.Circles.Select(c => c.Name);

    public Circle? FindCircle(string name)
        => Profile.Circles.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Decide whether a reply to this contact must be approved by the owner first.</summary>
    public bool RequiresApproval(string handle)
    {
        switch (Profile.ApprovalMode)
        {
            case ApprovalMode.Off: return false;
            case ApprovalMode.All: return true;
            default:
                var contact = FindContact(handle);
                if (contact is null) return true; // unknown -> be safe
                return contact.Circles.Any(cn => FindCircle(cn)?.RequireApproval == true);
        }
    }

    // ---- cost control -----------------------------------------------------

    /// <summary>Remaining automatic agent replies allowed today (int.MaxValue when unlimited).</summary>
    public int AgentRepliesRemaining()
    {
        var budget = Profile.AgentDailyReplyBudget;
        if (budget <= 0) return int.MaxValue; // 0 = unlimited
        RollBudgetDay();
        return Math.Max(0, budget - Profile.AgentRepliesUsedToday);
    }

    /// <summary>
    /// Tries to consume one automatic-agent-reply from today's budget. Returns false when the
    /// daily cap is reached, in which case the caller should not invoke the paid model.
    /// </summary>
    public bool TryConsumeAgentReply()
    {
        if (Profile.AgentDailyReplyBudget <= 0) return true; // unlimited
        RollBudgetDay();
        if (Profile.AgentRepliesUsedToday >= Profile.AgentDailyReplyBudget) return false;
        Mutate(p => p.AgentRepliesUsedToday++);
        return true;
    }

    /// <summary>
    /// Gives back a unit consumed by <see cref="TryConsumeAgentReply"/> when the reply could not
    /// actually be produced (for example the model was unavailable), so a failure does not burn
    /// the user's daily agent budget.
    /// </summary>
    public void RefundAgentReply()
    {
        if (Profile.AgentDailyReplyBudget <= 0) return;
        if (Profile.AgentRepliesUsedToday > 0)
            Mutate(p => p.AgentRepliesUsedToday--);
    }

    private void RollBudgetDay()
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        if (Profile.AgentBudgetDate != today)
            Mutate(p => { p.AgentBudgetDate = today; p.AgentRepliesUsedToday = 0; });
    }

    // ---- contact key pinning (trust on first use) -------------------------

    /// <summary>
    /// Records the signing keys seen for a contact the first time we hear from them, and keeps
    /// them stable afterward. Returns the pinned key set to verify against. If the contact is
    /// unknown, a lightweight (not-yet-allowed) contact record is created to hold the pin.
    /// </summary>
    public IReadOnlyList<string> PinAndGetKeys(string handle, IReadOnlyList<string> observedKeys)
    {
        var h = Norm(handle);
        var contact = FindContact(h);
        if (contact is null)
        {
            contact = new Domain.Contact { Handle = h, Allowed = false, SigningKeys = observedKeys.ToList() };
            Mutate(p => p.Contacts.Add(contact));
            return contact.SigningKeys;
        }
        if (contact.SigningKeys.Count == 0 && observedKeys.Count > 0)
            Mutate(_ => contact.SigningKeys = observedKeys.ToList());
        return contact.SigningKeys;
    }

    /// <summary>
    /// Marks a contact as having presented keys that do not match what we pinned (possible identity
    /// change or impostor). Surfaced in the UI so the user can re-verify before trusting new keys.
    /// </summary>
    public void FlagContactKeyChanged(string handle)
    {
        var contact = FindContact(Norm(handle));
        if (contact is not null && !contact.KeyChanged)
            Mutate(_ => contact.KeyChanged = true);
    }

    /// <summary>
    /// Re-verifies a contact after an identity change: replaces the pinned signing keys with the
    /// handle's current device keys from the relay and clears the key-changed flag. This is an
    /// explicit user action (trust on re-verify), so it is never done automatically.
    /// </summary>
    public void ReverifyContact(string handle, IReadOnlyList<string> currentKeys)
    {
        var contact = FindContact(Norm(handle));
        if (contact is null) return;
        Mutate(_ =>
        {
            contact.SigningKeys = currentKeys.ToList();
            contact.KeyChanged = false;
        });
    }
}
