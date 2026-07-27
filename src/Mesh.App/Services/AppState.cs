using System.Text.Json;
using System.Text;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

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
public sealed partial class AppState : IMemoryState
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions SyncJson = new(JsonSerializerDefaults.Web);

    private sealed class AccountIndex
    {
        public string? ActiveId { get; set; }
        public List<AccountRef> Accounts { get; set; } = new();
    }

    private sealed record PendingProfileOperation(
        DeviceSyncOperation Operation,
        MeshDb.SyncVersionWrite? Version,
        MeshDb.SyncTombstoneWrite? Tombstone,
        MeshDb.SyncCircleRenameWrite? CircleRename);

    private readonly ISecretStore secrets;
    private readonly string dir;
    private readonly string indexPath;
    private readonly object profileSyncGate = new();
    private string? activeId;
    private List<AccountRef> accounts = new();
    private MeshDb? activeDb;
    private bool applyingDeviceSync;

    public MeshProfile Profile { get; private set; } = new();
    /// <summary>OwnThreads sorted by pin (pinned first), then activity (newest), then created (newest), then stable id.</summary>
    public IReadOnlyList<OwnThread> OrderedOwnThreads
        => OwnThreadOrdering.ByActivity(Profile.OwnThreads).ToList();

    /// <summary>Conversations sorted by pin (pinned first), then activity (newest), then created (newest), then stable handle.</summary>
    public IReadOnlyList<Conversation> OrderedConversations
        => ConversationOrdering.ByActivity(Profile.Conversations).ToList();

    public event Action? Changed;
    public event Action<DeviceSyncOperation>? DeviceSyncOperationCreated;

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

    public AppState(ISecretStore secrets)
    {
        this.secrets = secrets;
        // Directory is owned by StoragePaths, the single source of truth shared with SecretStore.
        // It resolves to a stable, app-identity-independent root on Windows (%LOCALAPPDATA%\Mesh\Data),
        // still honoring the MESH_PROFILE_DIR override used for isolated test instances.
        dir = StoragePaths.DataDir;
        Directory.CreateDirectory(dir);
        indexPath = Path.Combine(dir, "accounts.json");
        StorageProtection.TryEnsureBackgroundReadable(indexPath);
        Load();
    }

    public bool IsOnboarded => activeId is not null && Profile.IsOnboarded;

    /// <summary>All identities saved on this device.</summary>
    public IReadOnlyList<AccountRef> Accounts => accounts;
    public string? ActiveAccountId => activeId;
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
        return MeshDb.Open(path, key);
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(indexPath))
            {
                Profile = new MeshProfile();
                return;
            }

            var idx = JsonSerializer.Deserialize<AccountIndex>(File.ReadAllText(indexPath), JsonOpts) ?? new AccountIndex();
            accounts = idx.Accounts ?? new();
            activeId = idx.ActiveId;

            if (activeId is not null)
            {
                var db = OpenDb(activeId);
                var loaded = db.LoadProfile();
                if (loaded is not null)
                {
                    activeDb = db;
                    Profile = loaded;
                    ReconcileDeletedCircles();
                    RehydrateUnread();
                    RehydrateDurableTopicState();
                    return;
                }
                db.Dispose();
                activeId = null; // active database missing/empty, land on the picker
            }
            Profile = new MeshProfile();
        }
        catch { Profile = new MeshProfile(); activeId = null; activeDb = null; }
    }

    // Restore the in-memory unread set from the persisted profile (survives restarts).
    private void RehydrateUnread()
    {
        unread.Clear();
        foreach (var h in Profile.UnreadFrom) unread.Add(Norm(h));
    }

    private void ReconcileDeletedCircles()
    {
        if (activeDb is null) return;
        var revoked = Profile.Circles
            .Select(circle => CircleEntityId(circle.Name))
            .Where(entityId => entityId.Length > 0
                && !ProfileSyncState.IsCircleAvailable(
                    true,
                    activeDb.GetSyncVersion(SyncKey(DeviceSyncKinds.CircleUpsert, entityId)),
                    activeDb.GetSyncTombstoneVersion(DeviceSyncKinds.CircleDelete, entityId)))
            .ToHashSet(StringComparer.Ordinal);
        if (revoked.Count == 0) return;
        foreach (var entityId in revoked)
        {
            Profile.Circles.RemoveAll(circle => CircleEntityId(circle.Name) == entityId);
            ProfileSyncState.DeleteCircleReferences(Profile, entityId);
        }
        activeDb.SaveProfileAndSyncState(Profile, [], []);
    }

    private void WriteIndex()
    {
        try
        {
            File.WriteAllText(indexPath, JsonSerializer.Serialize(
                new AccountIndex { ActiveId = activeId, Accounts = accounts }, JsonOpts));
            StorageProtection.TryEnsureBackgroundReadable(indexPath);
        }
        catch { /* best-effort */ }
    }

    private static string NewId() => Guid.NewGuid().ToString("n");

    public void Save()
    {
        PrepareProfileStorage();
        if (activeId is not null)
        {
            UpdateActiveAccount();
            activeDb?.SaveProfile(Profile);
        }
        WriteIndex();
    }

    /// <summary>Gets the local unsent text for a conversation.</summary>
    public string GetConversationDraft(string handle)
    {
        var normalized = Norm(handle);
        return normalized.Length == 0 ? "" : activeDb?.GetConversationDraft(normalized) ?? "";
    }

    /// <summary>Persists local unsent text for a conversation without syncing it to other devices.</summary>
    public void SetConversationDraft(string handle, string text)
    {
        var normalized = Norm(handle);
        if (normalized.Length == 0) return;
        activeDb?.SetConversationDraft(normalized, text);
    }

    /// <summary>Gets the local unsent text for a topic.</summary>
    public string GetTopicDraft(string threadId)
        => string.IsNullOrWhiteSpace(threadId) ? "" : activeDb?.GetTopicDraft(threadId) ?? "";

    /// <summary>Persists local unsent text for a topic without syncing it to other devices.</summary>
    public void SetTopicDraft(string threadId, string text)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        activeDb?.SetTopicDraft(threadId, text);
    }

    /// <summary>Gets the last Me topic opened in the desktop UI on this device.</summary>
    public string? GetLastDesktopTopicId()
        => activeDb?.GetLastDesktopTopicId();

    /// <summary>Stores the last Me topic opened in the desktop UI without syncing it.</summary>
    public void SetLastDesktopTopicId(string? threadId)
        => activeDb?.SetLastDesktopTopicId(threadId);

    /// <summary>Gets the last Messages conversation opened in the desktop UI on this device.</summary>
    public string? GetLastDesktopConversationKey()
        => activeDb?.GetLastDesktopConversationKey();

    /// <summary>Stores the last Messages conversation opened in the desktop UI without syncing it.</summary>
    public void SetLastDesktopConversationKey(string? conversationKey)
        => activeDb?.SetLastDesktopConversationKey(conversationKey);

    public MemorySnapshot SnapshotMemories()
    {
        lock (profileSyncGate)
            return new MemorySnapshot(
                activeId,
                Profile.Memories.Select(MemoryPolicy.Clone).ToList());
    }

    /// <summary>Persists and synchronizes an owner-only memory.</summary>
    public bool UpsertMemory(
        string? accountId,
        MemoryItem memory,
        MemoryItem? expected,
        out MemoryItem? previous)
    {
        var normalized = MemoryPolicy.Normalize(memory);
        DeviceSyncOperation? operation = null;
        previous = null;
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

            var deviceId = LocalDeviceId();
            if (deviceId is null)
            {
                activeDb.UpsertMemory(normalized);
            }
            else
            {
                var operationId = NewId();
                var versionKey = SyncKey(DeviceSyncKinds.MemoryUpsert, normalized.Id);
                var version = CreateNewerVersion(deviceId, operationId,
                [
                    activeDb.GetSyncVersion(versionKey),
                    activeDb.GetSyncTombstoneVersion(DeviceSyncKinds.MemoryDelete, normalized.Id)
                ]);
                if (!activeDb.TryApplyMemoryUpsert(
                        normalized,
                        versionKey,
                        version,
                        DeviceSyncKinds.MemoryDelete))
                    return false;
                operation = new DeviceSyncOperation(
                    operationId,
                    deviceId,
                    DeviceSyncKinds.MemoryUpsert,
                    normalized.Id,
                    version,
                    JsonSerializer.Serialize(MemoryPolicy.ToSync(normalized), SyncJson));
            }

            if (existing is null)
                Profile.Memories.Add(normalized);
            else
                MemoryPolicy.CopyShared(normalized, existing);
        }
        if (operation is not null) DeviceSyncOperationCreated?.Invoke(operation);
        NotifyChanged();
        return true;
    }

    /// <summary>Deletes and synchronizes an owner-only memory.</summary>
    public bool DeleteMemory(
        string? accountId,
        string id,
        MemoryItem expected,
        out MemoryItem? previous)
    {
        DeviceSyncOperation? operation = null;
        previous = null;
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

            var deviceId = LocalDeviceId();
            if (deviceId is null)
            {
                activeDb.DeleteMemory(id);
            }
            else
            {
                var operationId = NewId();
                var version = CreateNewerVersion(deviceId, operationId,
                [
                    activeDb.GetSyncTombstoneVersion(DeviceSyncKinds.MemoryDelete, id),
                    activeDb.GetSyncVersion(SyncKey(DeviceSyncKinds.MemoryUpsert, id))
                ]);
                if (!activeDb.TryApplyMemoryDelete(
                        id,
                        DeviceSyncKinds.MemoryDelete,
                        version,
                        SyncKey(DeviceSyncKinds.MemoryUpsert, id)))
                    return false;
                operation = new DeviceSyncOperation(
                    operationId,
                    deviceId,
                    DeviceSyncKinds.MemoryDelete,
                    id,
                    version,
                    "");
            }
            Profile.Memories.Remove(existing);
        }
        if (operation is not null) DeviceSyncOperationCreated?.Invoke(operation);
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
            activeDb.TouchMemories(distinct, at);
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
            accounts.Add(new AccountRef { Id = activeId, Handle = Profile.Handle, DisplayName = Profile.DisplayName });
            // Persist any history the fresh profile already carries (normally none at onboarding).
            foreach (var conv in Profile.Conversations)
            {
                conv.Handle = PrepareConversationForPersistence(conv);
                DeriveActivityMetadata(conv);
                activeDb.EnsureConversation(conv.Handle, conv.CreatedAt);
                PersistConversationMetadata(activeDb, conv);
                if (conv.LastActivityAt.HasValue)
                    activeDb.SetConversationActivity(conv.Handle, conv.LastActivityAt.Value);
                if (conv.IsPinned)
                    activeDb.SetConversationPin(conv.Handle, true);
                foreach (var line in conv.Lines) activeDb.AppendChatLine(Norm(conv.Handle), line);
            }
            foreach (var thread in Profile.OwnThreads)
            {
                thread.LastActivityAt ??= thread.Lines.Count == 0
                    ? thread.CreatedAt
                    : thread.Lines.Max(line => line.At);
                activeDb.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt);
                if (thread.LastActivityAt.HasValue)
                    activeDb.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value);
                if (thread.IsPinned)
                    activeDb.SetOwnThreadPin(thread.Id, true);
                if (thread.ExecutionDeviceId is not null
                    || thread.ExecutionDeviceName is not null
                    || thread.ExecutionDevicePlatform is not null
                    || thread.ExecutionAt.HasValue
                    || thread.ExecutionRunId is not null)
                    activeDb.SetOwnThreadExecution(
                        thread.Id,
                        thread.ExecutionDeviceId,
                        thread.ExecutionAt,
                        thread.ExecutionRunId,
                        thread.ExecutionDeviceName,
                        thread.ExecutionDevicePlatform);
                foreach (var line in thread.Lines) activeDb.AppendOwnChat(thread.Id, line);
            }
            for (var i = 0; i < Profile.Memories.Count; i++)
            {
                var memory = MemoryPolicy.Normalize(Profile.Memories[i]);
                Profile.Memories[i] = memory;
                activeDb.UpsertMemory(memory);
            }
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
        IReadOnlyList<PendingProfileOperation> pending;
        lock (profileSyncGate)
            pending = MutateCore(change, renamedCircleFrom);
        PublishProfileMutation(pending);
    }

    public bool RenameCircle(string oldName, string newName)
    {
        var oldEntityId = CircleEntityId(oldName);
        var replacement = newName.Trim();
        IReadOnlyList<PendingProfileOperation> pending;
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
            pending = MutateCore(profile =>
            {
                circle.Name = replacement;
                ProfileSyncState.RenameCircleReferences(profile, previousName, replacement);
            }, previousName);
        }
        PublishProfileMutation(pending);
        return true;
    }

    public bool DeleteCircle(string name)
    {
        var entityId = CircleEntityId(name);
        IReadOnlyList<PendingProfileOperation> pending;
        lock (profileSyncGate)
        {
            var circle = Profile.Circles.FirstOrDefault(item =>
                CircleEntityId(item.Name) == entityId);
            if (circle is null) return false;
            var currentName = circle.Name;
            pending = MutateCore(profile =>
            {
                profile.Circles.Remove(circle);
                ProfileSyncState.DeleteCircleReferences(profile, currentName);
            }, null);
        }
        PublishProfileMutation(pending);
        return true;
    }

    private IReadOnlyList<PendingProfileOperation> MutateCore(
        Action<MeshProfile> change,
        string? renamedCircleFrom)
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
        var before = ProfileSyncState.Snapshot(Profile);
        IReadOnlyList<PendingProfileOperation> pending = Array.Empty<PendingProfileOperation>();
        try
        {
            change(Profile);
            var after = ProfileSyncState.Snapshot(Profile);
            var profileChanged = HasProfileSyncChanges(before, after);
            PrepareProfileStorage();
            var deviceId = LocalDeviceId();
            if (!applyingDeviceSync && profileChanged && activeDb is not null && deviceId is not null)
            {
                pending = PrepareProfileChanges(before, after, deviceId, renamedCircleFrom);
                if (!activeDb.SaveProfileAndSyncState(
                    Profile,
                    pending.Where(item => item.Version is not null).Select(item => item.Version!).ToList(),
                    pending.Where(item => item.Tombstone is not null).Select(item => item.Tombstone!).ToList(),
                    circleRenames: pending
                        .Where(item => item.CircleRename is not null)
                        .Select(item => item.CircleRename!)
                        .ToList()))
                    throw new InvalidOperationException("The profile sync transaction was not accepted.");
                UpdateActiveAccount();
                WriteIndex();
            }
            else
            {
                Save();
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
                activeDb = previousActiveDb;
                accounts = previousAccounts;
            }
            Profile = previousProfile;
            throw;
        }
        return pending;
    }

    private void PublishProfileMutation(IReadOnlyList<PendingProfileOperation> pending)
    {
        foreach (var item in pending)
            DeviceSyncOperationCreated?.Invoke(item.Operation);
        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke();

    // ---- chat history (append-only rows) ----------------------------------

    /// <summary>
    /// Appends a line to a conversation, persisting it as a single row (not a full re-serialize)
    /// so history stays scalable. Updates the in-memory conversation and notifies the UI.
    /// </summary>
    public void AddChatLine(string handle, ChatLine line)
    {
        var conv = GetOrCreateConversation(handle);
        conv.Lines.Add(line);
        conv.LastActivityAt = ActivityTimestamp.Advance(conv.LastActivityAt, line.At);
        activeDb?.AppendChatLine(conv.Handle, line);
        if (conv.LastActivityAt.HasValue)
            activeDb?.SetConversationActivity(conv.Handle, conv.LastActivityAt.Value);
        EmitLineUpsert(DeviceSyncKinds.ConversationLineUpsert, conv.Handle, line);
        EmitConversationUpsert(conv);
        NotifyChanged();
    }

    /// <summary>Appends a line to a "Me" topic thread as a single row.</summary>
    public void AddOwnChatLine(string threadId, ChatLine line)
    {
        lock (profileSyncGate)
        {
            if (IsTopicLineDeleted(threadId, line.Id)
                || IsTopicLineDeleted(threadId, line.ReplyToLineId))
                return;
            var thread = GetOrCreateOwnThread(threadId);
            thread.Lines.Add(line);
            thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, line.At);
            activeDb?.AppendOwnChat(thread.Id, line);
            if (thread.LastActivityAt.HasValue)
                activeDb?.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value);
            EmitLineUpsert(DeviceSyncKinds.TopicLineUpsert, thread.Id, line);
            EmitTopicUpsert(thread);
        }
        NotifyChanged();
    }

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
        activeDb?.UpsertOwnThread(
            thread.Id, thread.Title, thread.CreatedAt, Profile.OwnThreads.Count - 1,
            thread.LastActivityAt, thread.IsPinned, thread.ExecutionDeviceId,
            thread.ExecutionAt, thread.ExecutionRunId, replaceExecutionMetadata: true,
            executionDeviceName: thread.ExecutionDeviceName,
            executionDevicePlatform: thread.ExecutionDevicePlatform);
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
            activeDb?.ReorderOwnThreads(ordered.Select(t => t.Id).ToList());
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
            activeDb?.SetOwnThreadPinAndActivity(thread.Id, pinned, activityAt);
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
                && !activeDb.TryBindOwnThreadDevice(
                    thread.Id, target.DeviceId, target.DeviceName, target.Platform))
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
                && !activeDb.MoveOwnThreadToDevice(
                    thread.Id, target.DeviceId, target.DeviceName, target.Platform, activityAt))
                throw new InvalidOperationException("The topic could not be moved atomically.");
            thread.ExecutionDeviceId = target.DeviceId;
            thread.ExecutionDeviceName = target.DeviceName;
            thread.ExecutionDevicePlatform = target.Platform;
            thread.ExecutionAt = null;
            thread.ExecutionRunId = null;
            thread.LastActivityAt = activityAt;
            remoteRuns.Remove(thread.Id);
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
            activeDb?.UpsertOwnThread(
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
                executionDevicePlatform: target.Platform);
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
        activeDb?.RenameOwnThread(thread.Id, thread.Title);
        activeDb?.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value);
        EmitTopicUpsert(thread);
        NotifyChanged();
    }

    /// <summary>Clears a "Me" thread's messages but keeps the thread.</summary>
    public void ClearOwnThread(string threadId)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        var lineVersions = thread.Lines
            .Select(line => activeDb?.GetSyncVersion(LineSyncKey(
                DeviceSyncKinds.TopicLineUpsert, thread.Id, line.Id)))
            .ToList();
        thread.Lines.Clear();
        EmitTombstone(DeviceSyncKinds.TopicClear, thread.Id, lineVersions);
        NotifyChanged();
    }

    /// <summary>Deletes a "Me" thread and all its messages.</summary>
    public void DeleteOwnThread(string threadId)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        var lineVersions = thread.Lines
            .Select(line => activeDb?.GetSyncVersion(LineSyncKey(
                DeviceSyncKinds.TopicLineUpsert, thread.Id, line.Id)))
            .ToList();
        Profile.OwnThreads.Remove(thread);
        completedThreads.Remove(threadId);
        EmitTombstone(DeviceSyncKinds.TopicDelete, threadId, lineVersions);
        NotifyChanged();
    }

    // ---- device sync -------------------------------------------------------

    public IReadOnlyList<DeviceSyncOperation> CreateDeviceSyncSnapshot()
    {
        lock (profileSyncGate)
            return CreateDeviceSyncSnapshotCore();
    }

    private IReadOnlyList<DeviceSyncOperation> CreateDeviceSyncSnapshotCore()
    {
        var deviceId = LocalDeviceId();
        if (deviceId is null || activeDb is null) return Array.Empty<DeviceSyncOperation>();

        var operations = new List<DeviceSyncOperation>();
        for (var i = 0; i < Profile.OwnThreads.Count; i++)
        {
            var thread = Profile.OwnThreads[i];
            var entityId = thread.Id;
            var version = GetOrCreateSnapshotVersion(
                SyncKey(DeviceSyncKinds.TopicUpsert, entityId),
                thread.CreatedAt,
                DeviceSyncKinds.TopicUpsert,
                entityId);
            operations.Add(SnapshotOperation(
                deviceId,
                DeviceSyncKinds.TopicUpsert,
                entityId,
                version,
                new DeviceSyncTopic(thread.Id, thread.Title, thread.CreatedAt, i,
                    thread.ExecutionDeviceId, thread.ExecutionDeviceName, thread.ExecutionDevicePlatform,
                    thread.LastActivityAt, thread.IsPinned, thread.ExecutionAt, thread.ExecutionRunId,
                    HasExecutionMetadata: true)));

            foreach (var line in thread.Lines)
            {
                version = GetOrCreateSnapshotVersion(
                    LineSyncKey(DeviceSyncKinds.TopicLineUpsert, thread.Id, line.Id),
                    line.At,
                    DeviceSyncKinds.TopicLineUpsert,
                    thread.Id + "\0" + line.Id);
                operations.Add(SnapshotOperation(
                    deviceId,
                    DeviceSyncKinds.TopicLineUpsert,
                    thread.Id,
                    version,
                    ToSyncLine(line)));
            }
        }

        for (var i = 0; i < Profile.Conversations.Count; i++)
        {
            var conversation = Profile.Conversations[i];
            var handle = Norm(conversation.Handle);
            var version = GetOrCreateSnapshotVersion(
                SyncKey(DeviceSyncKinds.ConversationUpsert, handle),
                DateTimeOffset.UnixEpoch,
                DeviceSyncKinds.ConversationUpsert,
                handle);
            operations.Add(SnapshotOperation(
                deviceId,
                DeviceSyncKinds.ConversationUpsert,
                handle,
                version,
                ToSyncConversation(conversation, i)));

            foreach (var line in conversation.Lines)
            {
                version = GetOrCreateSnapshotVersion(
                    LineSyncKey(DeviceSyncKinds.ConversationLineUpsert, handle, line.Id),
                    line.At,
                    DeviceSyncKinds.ConversationLineUpsert,
                    handle + "\0" + line.Id);
                operations.Add(SnapshotOperation(
                    deviceId,
                    DeviceSyncKinds.ConversationLineUpsert,
                    handle,
                    version,
                    ToSyncLine(line)));
            }
        }

        foreach (var memory in Profile.Memories)
        {
            var version = GetOrCreateSnapshotVersion(
                SyncKey(DeviceSyncKinds.MemoryUpsert, memory.Id),
                memory.UpdatedAt,
                DeviceSyncKinds.MemoryUpsert,
                memory.Id);
            operations.Add(SnapshotOperation(
                deviceId,
                DeviceSyncKinds.MemoryUpsert,
                memory.Id,
                version,
                MemoryPolicy.ToSync(memory)));
        }

        var profileState = ProfileSyncState.Snapshot(Profile);
        foreach (var (entityId, projectedCircle) in profileState.Circles)
        {
            var renames = activeDb.GetSyncCircleRenames(entityId);
            var circle = renames.Count == 0
                ? projectedCircle
                : projectedCircle with { Renames = renames };
            var version = GetOrCreateLegacyProfileVersion(
                SyncKey(DeviceSyncKinds.CircleUpsert, entityId),
                deviceId,
                DeviceSyncKinds.CircleUpsert,
                entityId,
                circle);
            operations.Add(SnapshotOperation(
                deviceId,
                DeviceSyncKinds.CircleUpsert,
                entityId,
                version,
                circle));
        }

        foreach (var (entityId, contact) in profileState.Contacts)
        {
            var version = GetOrCreateLegacyProfileVersion(
                SyncKey(DeviceSyncKinds.ContactUpsert, entityId),
                deviceId,
                DeviceSyncKinds.ContactUpsert,
                entityId,
                contact);
            operations.Add(SnapshotOperation(
                deviceId,
                DeviceSyncKinds.ContactUpsert,
                entityId,
                version,
                contact));
        }

        foreach (var tombstone in activeDb.GetSyncTombstones())
            operations.Add(new DeviceSyncOperation(
                SnapshotOperationId(tombstone.Version, tombstone.Kind, tombstone.EntityId),
                deviceId,
                tombstone.Kind,
                tombstone.EntityId,
                tombstone.Version,
                ""));

        return operations;
    }

    public bool ApplyDeviceSyncBatch(DeviceSyncBatch batch)
    {
        (bool accepted, bool visibleChanged) result;
        lock (profileSyncGate)
            result = ApplyDeviceSyncBatchCore(batch);
        if (result.visibleChanged) NotifyChanged();
        return result.accepted;
    }

    private (bool accepted, bool visibleChanged) ApplyDeviceSyncBatchCore(DeviceSyncBatch batch)
    {
        if (batch is null || activeDb is null) return (false, false);
        var deviceId = LocalDeviceId();
        if (deviceId is null
            || string.IsNullOrWhiteSpace(batch.SourceDeviceId)
            || string.Equals(batch.SourceDeviceId, deviceId, StringComparison.Ordinal)
            || batch.Operations is null)
            return (false, false);

        var accepted = false;
        var visibleChanged = false;
        applyingDeviceSync = true;
        try
        {
            foreach (var operation in ProfileSyncState.OrderForApplication(
                         batch.Operations.Where(operation => operation is not null)))
            {
                if (!IsValidOperation(operation, batch.SourceDeviceId, deviceId)) continue;
                try
                {
                    var previousAcceptedVersion = AcceptedVersion(operation);
                    visibleChanged |= ApplyDeviceSyncOperation(operation);
                    accepted |= DeviceSyncVersion.IsNewer(
                                    operation.Version, previousAcceptedVersion)
                                && string.Equals(
                                    AcceptedVersion(operation),
                                    operation.Version,
                                    StringComparison.Ordinal);
                }
                catch (JsonException)
                {
                }
                catch (ArgumentException)
                {
                }
                catch (FormatException)
                {
                }
            }
        }
        finally
        {
            applyingDeviceSync = false;
        }

        return (accepted, visibleChanged);
    }

    private bool ApplyDeviceSyncOperation(DeviceSyncOperation operation)
    {
        return operation.Kind switch
        {
            DeviceSyncKinds.TopicUpsert => ApplyTopicUpsert(operation),
            DeviceSyncKinds.TopicLineUpsert => ApplyTopicLineUpsert(operation),
            DeviceSyncKinds.TopicLineDelete => ApplyTopicLineDelete(operation),
            DeviceSyncKinds.TopicClear => ApplyTopicClear(operation),
            DeviceSyncKinds.TopicDelete => ApplyTopicDelete(operation),
            DeviceSyncKinds.ConversationUpsert => ApplyConversationUpsert(operation),
            DeviceSyncKinds.ConversationLineUpsert => ApplyConversationLineUpsert(operation),
            DeviceSyncKinds.ConversationClear => ApplyConversationClear(operation),
            DeviceSyncKinds.ConversationDelete => ApplyConversationDelete(operation),
            DeviceSyncKinds.ContactUpsert => ApplyContactUpsert(operation),
            DeviceSyncKinds.ContactDelete => ApplyContactDelete(operation),
            DeviceSyncKinds.CircleUpsert => ApplyCircleUpsert(operation),
            DeviceSyncKinds.CircleDelete => ApplyCircleDelete(operation),
            DeviceSyncKinds.MemoryUpsert => ApplyMemoryUpsert(operation),
            DeviceSyncKinds.MemoryDelete => ApplyMemoryDelete(operation),
            _ => false
        };
    }

    private bool ApplyMemoryUpsert(DeviceSyncOperation operation)
    {
        var dto = DeserializePayload<DeviceSyncMemory>(operation);
        if (!MemoryPolicy.IsValid(dto)
            || !string.Equals(dto.Id, operation.EntityId, StringComparison.Ordinal)
            || IsBlockedByTombstone(DeviceSyncKinds.MemoryDelete, dto.Id, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.MemoryUpsert))
            return false;

        var incoming = MemoryPolicy.FromSync(dto);
        var existing = Profile.Memories.FirstOrDefault(memory =>
            string.Equals(memory.Id, incoming.Id, StringComparison.Ordinal));
        var changed = existing is null || !MemoryPolicy.SharedEquals(existing, incoming);
        if (!activeDb!.TryApplyMemoryUpsert(
                incoming,
                SyncKey(DeviceSyncKinds.MemoryUpsert, incoming.Id),
                operation.Version,
                DeviceSyncKinds.MemoryDelete))
            return false;

        if (existing is null)
            Profile.Memories.Add(incoming);
        else
            MemoryPolicy.CopyShared(incoming, existing);
        return changed;
    }

    private bool ApplyMemoryDelete(DeviceSyncOperation operation)
    {
        if (!TopicRunProtocol.IsValidIdentifier(operation.EntityId)
            || !CanApplyProfileDelete(operation, DeviceSyncKinds.MemoryUpsert)
            || !activeDb!.TryApplyMemoryDelete(
                operation.EntityId,
                DeviceSyncKinds.MemoryDelete,
                operation.Version,
                SyncKey(DeviceSyncKinds.MemoryUpsert, operation.EntityId)))
            return false;
        return Profile.Memories.RemoveAll(memory =>
            string.Equals(memory.Id, operation.EntityId, StringComparison.Ordinal)) > 0;
    }

    private bool ApplyTopicUpsert(DeviceSyncOperation operation)
    {
        var dto = DeserializePayload<DeviceSyncTopic>(operation);
        if (string.IsNullOrWhiteSpace(dto.Id)
            || !string.Equals(dto.Id, operation.EntityId, StringComparison.Ordinal)
            || IsBlockedByTombstone(DeviceSyncKinds.TopicDelete, dto.Id, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.TopicUpsert))
            return false;

        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == dto.Id);
        var hasMetadata = dto.HasExecutionMetadata
                          || dto.ExecutionDeviceId is not null
                          || dto.ExecutionDeviceName is not null
                          || dto.ExecutionDevicePlatform is not null
                          || dto.ExecutionAt.HasValue
                          || dto.ExecutionRunId is not null
                          || dto.LastActivityAt.HasValue
                          || dto.IsPinned;
        var changed = thread is null
            || !string.Equals(thread.Title, dto.Title, StringComparison.Ordinal)
            || thread.CreatedAt != dto.CreatedAt
            || hasMetadata
               && (thread.ExecutionDeviceId != dto.ExecutionDeviceId
                   || thread.ExecutionDeviceName != dto.ExecutionDeviceName
                   || thread.ExecutionDevicePlatform != dto.ExecutionDevicePlatform
                   || thread.ExecutionAt != dto.ExecutionAt
                   || thread.ExecutionRunId != dto.ExecutionRunId
                   || dto.LastActivityAt.HasValue
                      && (!thread.LastActivityAt.HasValue
                          || dto.LastActivityAt.Value > thread.LastActivityAt.Value)
                   || thread.IsPinned != dto.IsPinned);
        if (thread is null)
        {
            thread = new OwnThread { Id = dto.Id };
            Profile.OwnThreads.Add(thread);
        }
        thread.Title = dto.Title ?? "";
        thread.CreatedAt = dto.CreatedAt;
        if (hasMetadata)
        {
            thread.ExecutionDeviceId = dto.ExecutionDeviceId;
            thread.ExecutionDeviceName = dto.ExecutionDeviceName;
            thread.ExecutionDevicePlatform = dto.ExecutionDevicePlatform;
            thread.ExecutionAt = dto.ExecutionAt;
            thread.ExecutionRunId = dto.ExecutionRunId;
            if (dto.LastActivityAt.HasValue)
                thread.LastActivityAt = ActivityTimestamp.Advance(
                    thread.LastActivityAt, dto.LastActivityAt.Value);
            thread.IsPinned = dto.IsPinned;
        }
        activeDb!.UpsertOwnThread(
            thread.Id,
            thread.Title,
            thread.CreatedAt,
            Profile.OwnThreads.IndexOf(thread),
            thread.LastActivityAt,
            thread.IsPinned,
            thread.ExecutionDeviceId,
            thread.ExecutionAt,
            thread.ExecutionRunId,
            replaceExecutionMetadata: hasMetadata,
            executionDeviceName: thread.ExecutionDeviceName,
            executionDevicePlatform: thread.ExecutionDevicePlatform);
        activeDb.TryAdvanceSyncVersion(SyncKey(operation.Kind, operation.EntityId), operation.Version);
        return changed;
    }

    private bool ApplyTopicLineUpsert(DeviceSyncOperation operation)
    {
        var threadId = operation.EntityId;
        var dto = DeserializePayload<DeviceSyncLine>(operation);
        var lineId = dto.Id;
        if (!IsValidLine(dto, lineId)
            || IsTopicLineDeleted(threadId, lineId)
            || IsTopicLineDeleted(threadId, dto.ReplyToLineId)
            || IsBlockedByTombstone(DeviceSyncKinds.TopicDelete, threadId, operation.Version)
            || IsBlockedByTombstone(DeviceSyncKinds.TopicClear, threadId, operation.Version)
            || !DeviceSyncVersion.IsNewer(
                operation.Version,
                activeDb!.GetSyncVersion(LineSyncKey(
                    DeviceSyncKinds.TopicLineUpsert, threadId, lineId))))
            return false;

        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        var incoming = ToChatLine(dto);
        if (!activeDb!.TryApplyOwnSyncLine(
                threadId,
                incoming,
                LineSyncKey(operation.Kind, threadId, lineId),
                operation.Version,
                DeviceSyncKinds.TopicLineDelete))
            return false;
        if (thread is null)
        {
            thread = new OwnThread { Id = threadId, CreatedAt = dto.At, LastActivityAt = dto.At };
            Profile.OwnThreads.Add(thread);
        }
        var line = thread.Lines.FirstOrDefault(item => item.Id == lineId);
        var changed = line is null || !LineEquals(line, dto);
        if (line is null)
        {
            line = new ChatLine { Id = lineId };
            thread.Lines.Add(line);
        }
        MergeLine(line, dto);
        thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, dto.At);
        // A committed assistant answer syncing in from the executing device is the terminal truth for a
        // remote run. The terminal update travels independently and can arrive later, so reconcile any
        // lingering projection here to clear the phantom "thinking" bubble on this viewing device.
        var reconciled = string.Equals(dto.Role, "assistant", StringComparison.Ordinal)
                         && !dto.Internal
                         && ReconcileRemoteRunWithAnswer(thread, dto.At);
        return changed || reconciled;
    }

    private bool ApplyTopicLineDelete(DeviceSyncOperation operation)
    {
        if (!DeviceSyncEntityIds.TryParseTopicLine(
                operation.EntityId, out var threadId, out var lineId)
            || !CanApplyTombstone(operation))
            return false;

        var thread = Profile.OwnThreads.FirstOrDefault(item =>
            string.Equals(item.Id, threadId, StringComparison.Ordinal));
        if (thread?.Lines.Any(line =>
                string.Equals(line.Id, lineId, StringComparison.Ordinal)) == true
            && !DeviceSyncVersion.IsNewer(
                operation.Version,
                activeDb!.GetSyncVersion(LineSyncKey(
                    DeviceSyncKinds.TopicLineUpsert, threadId, lineId))))
            return false;

        var changed = thread is not null && thread.Lines.RemoveAll(line =>
            string.Equals(line.Id, lineId, StringComparison.Ordinal)
            || string.Equals(line.ReplyToLineId, lineId, StringComparison.Ordinal)) > 0;
        var queued = queuedTopicRuns.FindByLine(threadId, lineId);
        if (queued is not null)
            changed |= queuedTopicRuns.Complete(queued.ThreadId, queued.RunId);

        activeDb!.ApplyTopicLineDelete(
            threadId, lineId, operation.EntityId, operation.Kind, operation.Version);
        return changed;
    }

    private bool ApplyTopicClear(DeviceSyncOperation operation)
    {
        if (IsBlockedByTombstone(DeviceSyncKinds.TopicDelete, operation.EntityId, operation.Version)
            || !CanApplyClear(
                operation,
                DeviceSyncKinds.TopicLineUpsert,
                Profile.OwnThreads.FirstOrDefault(t => t.Id == operation.EntityId)?.Lines))
            return false;
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == operation.EntityId);
        var changed = thread is not null && thread.Lines.Count > 0;
        thread?.Lines.Clear();
        activeDb!.ApplyTopicClear(
            operation.EntityId, operation.Kind, operation.Version);
        return changed;
    }

    private bool ApplyTopicDelete(DeviceSyncOperation operation)
    {
        if (!CanApplyDelete(
                operation,
                DeviceSyncKinds.TopicUpsert,
                DeviceSyncKinds.TopicClear,
                DeviceSyncKinds.TopicLineUpsert,
                Profile.OwnThreads.FirstOrDefault(t => t.Id == operation.EntityId)?.Lines))
            return false;
        var changed = Profile.OwnThreads.RemoveAll(t => t.Id == operation.EntityId) > 0;
        completedThreads.Remove(operation.EntityId);
        activeDb!.ApplyTopicDelete(
            operation.EntityId, operation.Kind, operation.Version);
        return changed;
    }

    private bool ApplyConversationUpsert(DeviceSyncOperation operation)
    {
        var dto = DeserializePayload<DeviceSyncConversation>(operation);
        var handle = Norm(dto.Handle ?? "");
        if (handle.Length == 0
            || !string.Equals(handle, operation.EntityId, StringComparison.Ordinal)
            || dto.GroupMembers is null
            || IsBlockedByTombstone(DeviceSyncKinds.ConversationDelete, handle, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.ConversationUpsert))
            return false;

        var normalized = NormalizeSyncConversation(dto, handle);
        var conversation = FindConversation(handle);
        var hasActivityMetadata = dto.HasActivityMetadata
                                  || dto.CreatedAt.HasValue
                                  || dto.LastActivityAt.HasValue
                                  || dto.IsPinned;
        var changed = conversation is null
            || !ConversationCoreEquals(conversation, normalized)
            || hasActivityMetadata
               && (dto.CreatedAt.HasValue && conversation.CreatedAt != dto.CreatedAt
                   || dto.LastActivityAt.HasValue
                      && (!conversation.LastActivityAt.HasValue
                          || dto.LastActivityAt.Value > conversation.LastActivityAt.Value)
                   || conversation.IsPinned != dto.IsPinned);
        if (conversation is null)
        {
            conversation = new Conversation { Handle = handle };
            Profile.Conversations.Add(conversation);
        }
        MergeConversation(conversation, normalized);
        if (hasActivityMetadata)
        {
            if (dto.CreatedAt.HasValue)
                conversation.CreatedAt = dto.CreatedAt;
            if (dto.LastActivityAt.HasValue)
                conversation.LastActivityAt = ActivityTimestamp.Advance(
                    conversation.LastActivityAt, dto.LastActivityAt.Value);
            conversation.IsPinned = dto.IsPinned;
        }
        activeDb!.UpsertConversation(
            handle,
            Profile.Conversations.IndexOf(conversation),
            conversation.ServiceId,
            conversation.ServiceName,
            conversation.ProviderHandle,
            conversation.GroupId,
            conversation.GroupName,
            conversation.GroupOwnerHandle,
            conversation.GroupMembers,
            conversation.GroupVersion,
            conversation.CreatedAt,
            conversation.LastActivityAt,
            conversation.IsPinned,
            replaceCreatedAt: dto.CreatedAt.HasValue);
        if (conversation.LastActivityAt.HasValue)
            activeDb.SetConversationActivity(handle, conversation.LastActivityAt.Value);
        activeDb.SetConversationPin(handle, conversation.IsPinned);
        activeDb.TryAdvanceSyncVersion(SyncKey(operation.Kind, operation.EntityId), operation.Version);
        return changed;
    }

    private bool ApplyConversationLineUpsert(DeviceSyncOperation operation)
    {
        var handle = Norm(operation.EntityId);
        var dto = DeserializePayload<DeviceSyncLine>(operation);
        var lineId = dto.Id;
        if (!string.Equals(handle, operation.EntityId, StringComparison.Ordinal)
            || !IsValidLine(dto, lineId)
            || IsBlockedByTombstone(DeviceSyncKinds.ConversationDelete, handle, operation.Version)
            || IsBlockedByTombstone(DeviceSyncKinds.ConversationClear, handle, operation.Version)
            || !DeviceSyncVersion.IsNewer(
                operation.Version,
                activeDb!.GetSyncVersion(LineSyncKey(
                    DeviceSyncKinds.ConversationLineUpsert, handle, lineId))))
            return false;

        var conversation = FindConversation(handle);
        var incoming = ToChatLine(dto);
        if (!activeDb!.TryApplyConversationSyncLine(
                handle,
                incoming,
                LineSyncKey(operation.Kind, handle, lineId),
                operation.Version))
            return false;
        if (conversation is null)
        {
            conversation = new Conversation { Handle = handle, CreatedAt = dto.At };
            Profile.Conversations.Add(conversation);
        }
        var line = conversation.Lines.FirstOrDefault(item => item.Id == lineId);
        var changed = line is null || !LineEquals(line, dto);
        if (line is null)
        {
            line = new ChatLine { Id = lineId };
            conversation.Lines.Add(line);
        }
        MergeLine(line, dto);
        conversation.LastActivityAt = ActivityTimestamp.Advance(
            conversation.LastActivityAt, dto.At);
        var markedUnread = DeviceSyncUnreadPolicy.ShouldMarkConversationUnread(dto.Role)
                           && MarkUnreadFromDeviceSync(handle);
        return changed || markedUnread;
    }

    private bool MarkUnreadFromDeviceSync(string handle)
    {
        var normalized = Norm(handle);
        if (!unread.Add(normalized)) return false;
        if (!Profile.UnreadFrom.Contains(normalized))
        {
            Profile.UnreadFrom.Add(normalized);
            activeDb?.SaveProfile(Profile);
        }
        return true;
    }

    private bool ApplyConversationClear(DeviceSyncOperation operation)
    {
        var handle = Norm(operation.EntityId);
        if (handle.Length == 0
            || !string.Equals(handle, operation.EntityId, StringComparison.Ordinal)
            || IsBlockedByTombstone(DeviceSyncKinds.ConversationDelete, handle, operation.Version)
            || !CanApplyClear(
                operation,
                DeviceSyncKinds.ConversationLineUpsert,
                FindConversation(handle)?.Lines))
            return false;
        var conversation = FindConversation(handle);
        var changed = conversation is not null && conversation.Lines.Count > 0;
        conversation?.Lines.Clear();
        activeDb!.ApplyConversationClear(handle, operation.Kind, operation.Version);
        return changed;
    }

    private bool ApplyConversationDelete(DeviceSyncOperation operation)
    {
        var handle = Norm(operation.EntityId);
        if (handle.Length == 0
            || !string.Equals(handle, operation.EntityId, StringComparison.Ordinal)
            || !CanApplyDelete(
                operation,
                DeviceSyncKinds.ConversationUpsert,
                DeviceSyncKinds.ConversationClear,
                DeviceSyncKinds.ConversationLineUpsert,
                FindConversation(handle)?.Lines))
            return false;
        var changed = Profile.Conversations.RemoveAll(
            c => c.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) > 0;
        unread.Remove(handle);
        if (Profile.UnreadFrom.Remove(handle)) activeDb!.SaveProfile(Profile);
        activeDb!.ApplyConversationDelete(handle, operation.Kind, operation.Version);
        return changed;
    }

    private bool ApplyContactUpsert(DeviceSyncOperation operation)
    {
        var dto = DeserializePayload<DeviceSyncContact>(operation);
        var unfiltered = ProfileSyncState.NormalizeContact(dto, dto.Circles ?? Array.Empty<string>());
        if (unfiltered.Handle.Length == 0
            || !string.Equals(unfiltered.Handle, operation.EntityId, StringComparison.Ordinal)
            || dto.Circles is null
            || dto.SigningKeys is null
            || dto.Circles.Any(string.IsNullOrWhiteSpace)
            || dto.SigningKeys.Any(string.IsNullOrWhiteSpace)
            || unfiltered.Circles.Count != dto.Circles.Count
            || unfiltered.SigningKeys.Count != dto.SigningKeys.Count
            || IsBlockedByTombstone(DeviceSyncKinds.ContactDelete, unfiltered.Handle, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.ContactUpsert))
            return false;

        var existing = Profile.Contacts.FirstOrDefault(
            item => string.Equals(Norm(item.Handle), unfiltered.Handle, StringComparison.Ordinal));
        var previousProfile = CloneProfile(Profile);
        var retainedIncomingCircles = RetainedContactCircleNames(dto.Circles);
        var retainedExistingCircles = RetainedContactCircleNames(
            existing?.Circles ?? new List<string>());
        var merged = ProfileSyncState.MergeContact(existing, dto, retainedIncomingCircles);
        var changed = existing is null
            || !ProfileSyncState.ContactEquals(
                ProfileSyncState.ProjectContact(existing, retainedExistingCircles),
                ProfileSyncState.NormalizeContact(dto, retainedIncomingCircles));
        if (existing is null)
        {
            existing = merged;
            Profile.Contacts.Add(existing);
        }
        else
        {
            CopyContact(merged, existing);
        }
        try
        {
            if (!activeDb!.SaveProfileAndSyncState(
                Profile,
                [new MeshDb.SyncVersionWrite(SyncKey(operation.Kind, operation.EntityId), operation.Version)],
                []))
            {
                Profile = previousProfile;
                return false;
            }
        }
        catch
        {
            Profile = previousProfile;
            throw;
        }
        return changed;
    }

    private bool ApplyContactDelete(DeviceSyncOperation operation)
    {
        var handle = Norm(operation.EntityId);
        if (handle.Length == 0
            || !string.Equals(handle, operation.EntityId, StringComparison.Ordinal)
            || !CanApplyProfileDelete(operation, DeviceSyncKinds.ContactUpsert))
            return false;

        var previousProfile = CloneProfile(Profile);
        var changed = Profile.Contacts.RemoveAll(
            contact => string.Equals(Norm(contact.Handle), handle, StringComparison.Ordinal)) > 0;
        try
        {
            if (!activeDb!.SaveProfileAndSyncState(
                Profile,
                [],
                [new MeshDb.SyncTombstoneWrite(operation.Kind, handle, operation.Version)]))
            {
                Profile = previousProfile;
                return false;
            }
        }
        catch
        {
            Profile = previousProfile;
            throw;
        }
        return changed;
    }

    private bool ApplyCircleUpsert(DeviceSyncOperation operation)
    {
        var dto = DeserializePayload<DeviceSyncCircle>(operation);
        var name = dto.Name?.Trim() ?? "";
        var entityId = CircleEntityId(name);
        var incomingRenames = dto.Renames ?? Array.Empty<DeviceSyncCircleRename>();
        var normalizedIncomingRenames = incomingRenames
            .Where(rename => rename is not null)
            .GroupBy(rename => CircleEntityId(rename.PreviousName), StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        if (name.Length == 0
            || !string.Equals(entityId, operation.EntityId, StringComparison.Ordinal)
            || normalizedIncomingRenames.Count != incomingRenames.Count
            || normalizedIncomingRenames.Any(rename =>
                CircleEntityId(rename.PreviousName).Length == 0
                || CircleEntityId(rename.PreviousName) == entityId
                || !IsVersion(rename.DeleteVersion))
            || IsBlockedByTombstone(DeviceSyncKinds.CircleDelete, entityId, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.CircleUpsert))
            return false;
        foreach (var rename in normalizedIncomingRenames)
        {
            var previousEntityId = CircleEntityId(rename.PreviousName);
            if (!DeviceSyncVersion.IsNewer(
                    rename.DeleteVersion,
                    activeDb!.GetSyncVersion(SyncKey(
                        DeviceSyncKinds.CircleUpsert, previousEntityId)))
                || DeviceSyncVersion.Compare(
                    rename.DeleteVersion,
                    activeDb.GetSyncTombstoneVersion(
                        DeviceSyncKinds.CircleDelete, previousEntityId)) < 0)
                return false;
        }
        var entityTombstone = activeDb!.GetSyncTombstoneVersion(
            DeviceSyncKinds.CircleDelete, entityId);
        var currentEntityUpsert = activeDb.GetSyncVersion(
            SyncKey(DeviceSyncKinds.CircleUpsert, entityId));
        var recreated = entityTombstone is not null
                        && DeviceSyncVersion.Compare(
                            entityTombstone, currentEntityUpsert) >= 0
                        && DeviceSyncVersion.IsNewer(operation.Version, entityTombstone);
        IReadOnlyList<DeviceSyncCircleRename> persistedRenames = recreated
            ? Array.Empty<DeviceSyncCircleRename>()
            : activeDb.GetSyncCircleRenames(entityId)
                .Where(rename =>
                    DeviceSyncVersion.IsNewer(
                        rename.DeleteVersion,
                        activeDb.GetSyncVersion(SyncKey(
                            DeviceSyncKinds.CircleUpsert,
                            CircleEntityId(rename.PreviousName))))
                    && DeviceSyncVersion.Compare(
                        rename.DeleteVersion,
                        activeDb.GetSyncTombstoneVersion(
                            DeviceSyncKinds.CircleDelete,
                            CircleEntityId(rename.PreviousName))) >= 0)
                .ToList();
        var normalizedRenames = persistedRenames
            .Concat(normalizedIncomingRenames)
            .GroupBy(rename => CircleEntityId(rename.PreviousName), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(rename => rename.DeleteVersion, StringComparer.Ordinal)
                .First())
            .ToList();

        var previousProfile = CloneProfile(Profile);
        var referenceChanged = normalizedRenames.Any(rename =>
            ProfileSyncState.HasCircleReferences(Profile, rename.PreviousName));
        var existing = Profile.Circles.FirstOrDefault(
            circle => string.Equals(CircleEntityId(circle.Name), entityId, StringComparison.Ordinal));
        var changed = existing is null
            || !string.Equals(existing.Name, name, StringComparison.Ordinal)
            || existing.RequireApproval != dto.RequireApproval;
        if (existing is null)
        {
            existing = new Circle();
            Profile.Circles.Add(existing);
        }
        else if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
        {
            ProfileSyncState.RenameCircleReferences(Profile, existing.Name, name);
        }
        foreach (var rename in normalizedRenames)
        {
            var previousEntityId = CircleEntityId(rename.PreviousName);
            ProfileSyncState.RenameCircleReferences(Profile, rename.PreviousName, name);
            Profile.Circles.RemoveAll(circle =>
                CircleEntityId(circle.Name) == previousEntityId);
        }
        existing.Name = name;
        existing.RequireApproval = dto.RequireApproval;
        try
        {
            var tombstones = normalizedRenames
                .Where(rename => DeviceSyncVersion.IsNewer(
                    rename.DeleteVersion,
                    activeDb!.GetSyncTombstoneVersion(
                        DeviceSyncKinds.CircleDelete,
                        CircleEntityId(rename.PreviousName))))
                .Select(rename => new MeshDb.SyncTombstoneWrite(
                    DeviceSyncKinds.CircleDelete,
                    CircleEntityId(rename.PreviousName),
                    rename.DeleteVersion))
                .ToList();
            if (!activeDb!.SaveProfileAndSyncState(
                Profile,
                [new MeshDb.SyncVersionWrite(SyncKey(operation.Kind, operation.EntityId), operation.Version)],
                tombstones,
                circleRenames:
                [
                    new MeshDb.SyncCircleRenameWrite(
                        entityId,
                        normalizedRenames)
                ]))
            {
                Profile = previousProfile;
                return false;
            }
        }
        catch
        {
            Profile = previousProfile;
            throw;
        }
        return changed || referenceChanged;
    }

    private bool ApplyCircleDelete(DeviceSyncOperation operation)
    {
        var entityId = CircleEntityId(operation.EntityId);
        if (entityId.Length == 0
            || !string.Equals(entityId, operation.EntityId, StringComparison.Ordinal)
            || !CanApplyProfileDelete(operation, DeviceSyncKinds.CircleUpsert))
            return false;

        var previousProfile = CloneProfile(Profile);
        var referenceChanged = ProfileSyncState.HasCircleReferences(Profile, entityId);
        var changed = Profile.Circles.RemoveAll(
            circle => string.Equals(CircleEntityId(circle.Name), entityId, StringComparison.Ordinal)) > 0;
        ProfileSyncState.DeleteCircleReferences(Profile, entityId);
        try
        {
            if (!activeDb!.SaveProfileAndSyncState(
                Profile,
                [],
                [new MeshDb.SyncTombstoneWrite(operation.Kind, entityId, operation.Version)]))
            {
                Profile = previousProfile;
                return false;
            }
        }
        catch
        {
            Profile = previousProfile;
            throw;
        }
        return changed || referenceChanged;
    }

    private void EmitTopicUpsert(OwnThread thread)
    {
        var sortOrder = Profile.OwnThreads.IndexOf(thread);
        EmitUpsert(
            DeviceSyncKinds.TopicUpsert,
            thread.Id,
            new DeviceSyncTopic(thread.Id, thread.Title, thread.CreatedAt, Math.Max(0, sortOrder),
                thread.ExecutionDeviceId, thread.ExecutionDeviceName, thread.ExecutionDevicePlatform,
                thread.LastActivityAt, thread.IsPinned, thread.ExecutionAt, thread.ExecutionRunId,
                HasExecutionMetadata: true),
            DeviceSyncKinds.TopicDelete);
    }

    private void EmitConversationUpsert(Conversation conversation)
    {
        var handle = Norm(conversation.Handle);
        EmitUpsert(
            DeviceSyncKinds.ConversationUpsert,
            handle,
            ToSyncConversation(conversation, Math.Max(0, Profile.Conversations.IndexOf(conversation))),
            DeviceSyncKinds.ConversationDelete);
    }

    private bool HasProfileSyncChanges(
        ProfileSyncProjection before,
        ProfileSyncProjection after)
        => before.Circles.Count != after.Circles.Count
           || before.Contacts.Count != after.Contacts.Count
           || before.Circles.Any(item =>
               !after.Circles.TryGetValue(item.Key, out var circle) || item.Value != circle)
           || before.Contacts.Any(item =>
               !after.Contacts.TryGetValue(item.Key, out var contact)
               || !ProfileSyncState.ContactEquals(item.Value, contact));

    private IReadOnlyList<PendingProfileOperation> PrepareProfileChanges(
        ProfileSyncProjection before,
        ProfileSyncProjection after,
        string deviceId,
        string? renamedCircleFrom)
    {
        var pending = new List<PendingProfileOperation>();
        var circleDeletes = before.Circles.Keys
            .Except(after.Circles.Keys, StringComparer.Ordinal)
            .Order()
            .Select(entityId => PrepareProfileDelete(
                DeviceSyncKinds.CircleDelete,
                DeviceSyncKinds.CircleUpsert,
                entityId,
                deviceId))
            .ToList();
        foreach (var (entityId, circle) in after.Circles.OrderBy(item => item.Key, StringComparer.Ordinal))
            if (!before.Circles.TryGetValue(entityId, out var previous)
                || previous != circle)
            {
                var existingRenames = before.Circles.ContainsKey(entityId)
                    ? activeDb!.GetSyncCircleRenames(entityId)
                    : Array.Empty<DeviceSyncCircleRename>();
                var payload = circle with
                {
                    Renames = existingRenames.Count == 0 ? null : existingRenames
                };
                var previousEntityId = CircleEntityId(renamedCircleFrom);
                if (previousEntityId.Length > 0
                    && previousEntityId != entityId
                    && before.Circles.TryGetValue(previousEntityId, out var renamedCircle)
                    && !after.Circles.ContainsKey(previousEntityId)
                    && !before.Circles.ContainsKey(entityId))
                {
                    var delete = circleDeletes.Single(item =>
                        item.Tombstone?.EntityId == previousEntityId);
                    var renames = activeDb!.GetSyncCircleRenames(previousEntityId)
                        .Append(new DeviceSyncCircleRename(
                            renamedCircle.Name,
                            delete.Operation.Version))
                        .GroupBy(item => CircleEntityId(item.PreviousName), StringComparer.Ordinal)
                        .Select(group => group.Last())
                        .ToList();
                    payload = circle with
                    {
                        Renames = renames
                    };
                }
                pending.Add(PrepareProfileUpsert(
                    DeviceSyncKinds.CircleUpsert,
                    DeviceSyncKinds.CircleDelete,
                    entityId,
                    payload,
                    deviceId));
            }
        pending.AddRange(circleDeletes);

        foreach (var entityId in before.Contacts.Keys.Except(after.Contacts.Keys, StringComparer.Ordinal).Order())
            pending.Add(PrepareProfileDelete(
                DeviceSyncKinds.ContactDelete,
                DeviceSyncKinds.ContactUpsert,
                entityId,
                deviceId));
        foreach (var (entityId, contact) in after.Contacts.OrderBy(item => item.Key, StringComparer.Ordinal))
            if (!before.Contacts.TryGetValue(entityId, out var previous)
                || !ProfileSyncState.ContactEquals(previous, contact))
                pending.Add(PrepareProfileUpsert(
                    DeviceSyncKinds.ContactUpsert,
                    DeviceSyncKinds.ContactDelete,
                    entityId,
                    contact,
                    deviceId));
        return pending;
    }

    private PendingProfileOperation PrepareProfileUpsert<T>(
        string kind,
        string deleteKind,
        string entityId,
        T payload,
        string deviceId)
    {
        var operationId = NewId();
        var version = CreateNewerVersion(deviceId, operationId, new[]
        {
            activeDb!.GetSyncVersion(SyncKey(kind, entityId)),
            activeDb.GetSyncTombstoneVersion(deleteKind, entityId)
        });
        return new PendingProfileOperation(
            new DeviceSyncOperation(
                operationId,
                deviceId,
                kind,
                entityId,
                version,
                JsonSerializer.Serialize(payload, SyncJson)),
            new MeshDb.SyncVersionWrite(SyncKey(kind, entityId), version),
            null,
            payload is DeviceSyncCircle circle
                ? new MeshDb.SyncCircleRenameWrite(
                    entityId,
                    circle.Renames ?? Array.Empty<DeviceSyncCircleRename>())
                : null);
    }

    private PendingProfileOperation PrepareProfileDelete(
        string kind,
        string upsertKind,
        string entityId,
        string deviceId)
    {
        var operationId = NewId();
        var version = CreateNewerVersion(deviceId, operationId, new[]
        {
            activeDb!.GetSyncTombstoneVersion(kind, entityId),
            activeDb.GetSyncVersion(SyncKey(upsertKind, entityId))
        });
        return new PendingProfileOperation(
            new DeviceSyncOperation(operationId, deviceId, kind, entityId, version, ""),
            null,
            new MeshDb.SyncTombstoneWrite(kind, entityId, version),
            null);
    }

    private void EmitLineUpsert(string kind, string parentId, ChatLine line)
    {
        if (applyingDeviceSync) return;
        if (kind == DeviceSyncKinds.TopicLineUpsert
            && (IsTopicLineDeleted(parentId, line.Id)
                || IsTopicLineDeleted(parentId, line.ReplyToLineId)))
            return;
        var deviceId = LocalDeviceId();
        if (activeDb is null || deviceId is null) return;
        var deleteKind = kind == DeviceSyncKinds.TopicLineUpsert
            ? DeviceSyncKinds.TopicDelete
            : DeviceSyncKinds.ConversationDelete;
        var clearKind = kind == DeviceSyncKinds.TopicLineUpsert
            ? DeviceSyncKinds.TopicClear
            : DeviceSyncKinds.ConversationClear;
        var operationId = NewId();
        var version = CreateNewerVersion(
            deviceId,
            operationId,
            new[]
            {
                activeDb.GetSyncVersion(LineSyncKey(kind, parentId, line.Id)),
                activeDb.GetSyncTombstoneVersion(deleteKind, parentId),
                activeDb.GetSyncTombstoneVersion(clearKind, parentId),
                kind == DeviceSyncKinds.TopicLineUpsert
                    ? activeDb.GetSyncTombstoneVersion(
                        DeviceSyncKinds.TopicLineDelete,
                        DeviceSyncEntityIds.TopicLine(parentId, line.Id))
                    : null
            });
        if (!activeDb.TryAdvanceSyncVersion(
                LineSyncKey(kind, parentId, line.Id), version))
            return;
        DeviceSyncOperationCreated?.Invoke(new DeviceSyncOperation(
            operationId,
            deviceId,
            kind,
            parentId,
            version,
            JsonSerializer.Serialize(ToSyncLine(line), SyncJson)));
    }

    private void EmitUpsert<T>(string kind, string entityId, T payload, string deleteKind)
    {
        if (applyingDeviceSync) return;
        EmitOperation(
            kind,
            entityId,
            payload,
            activeDb?.GetSyncVersion(SyncKey(kind, entityId)),
            activeDb?.GetSyncTombstoneVersion(deleteKind, entityId));
    }

    private void EmitTombstone(
        string kind,
        string entityId,
        IEnumerable<string?>? additionalVersions = null)
    {
        if (applyingDeviceSync) return;
        var versions = new List<string?>
        {
            activeDb?.GetSyncTombstoneVersion(kind, entityId),
            kind is DeviceSyncKinds.TopicDelete
                ? activeDb?.GetSyncVersion(SyncKey(DeviceSyncKinds.TopicUpsert, entityId))
                : kind is DeviceSyncKinds.ConversationDelete
                    ? activeDb?.GetSyncVersion(SyncKey(DeviceSyncKinds.ConversationUpsert, entityId))
                    : kind is DeviceSyncKinds.ContactDelete
                        ? activeDb?.GetSyncVersion(SyncKey(DeviceSyncKinds.ContactUpsert, entityId))
                        : kind is DeviceSyncKinds.CircleDelete
                            ? activeDb?.GetSyncVersion(SyncKey(DeviceSyncKinds.CircleUpsert, entityId))
                            : null
        };
        if (kind == DeviceSyncKinds.TopicDelete)
            versions.Add(activeDb?.GetSyncTombstoneVersion(DeviceSyncKinds.TopicClear, entityId));
        else if (kind == DeviceSyncKinds.ConversationDelete)
            versions.Add(activeDb?.GetSyncTombstoneVersion(DeviceSyncKinds.ConversationClear, entityId));
        if (additionalVersions is not null) versions.AddRange(additionalVersions);
        EmitOperation<object?>(kind, entityId, null, versions.ToArray());
    }

    private void EmitOperation<T>(string kind, string entityId, T payload, params string?[] priorVersions)
    {
        var deviceId = LocalDeviceId();
        if (activeDb is null) return;
        if (deviceId is null)
        {
            switch (kind)
            {
                case DeviceSyncKinds.TopicLineDelete:
                    if (!DeviceSyncEntityIds.TryParseTopicLine(
                            entityId, out var threadId, out var lineId))
                        throw new InvalidOperationException("The topic line tombstone ID was invalid.");
                    activeDb.DeleteOwnChatLine(threadId, lineId);
                    break;
                case DeviceSyncKinds.TopicDelete:
                    activeDb.DeleteOwnThread(entityId);
                    break;
                case DeviceSyncKinds.TopicClear:
                    activeDb.ClearOwnThread(entityId);
                    break;
                case DeviceSyncKinds.ConversationDelete:
                    activeDb.DeleteConversation(entityId);
                    break;
                case DeviceSyncKinds.ConversationClear:
                    activeDb.ClearConversation(entityId);
                    break;
            }
            return;
        }
        var operationId = NewId();
        var version = CreateNewerVersion(deviceId, operationId, priorVersions);
        var serialized = payload is null ? "" : JsonSerializer.Serialize(payload, SyncJson);
        if (kind is DeviceSyncKinds.TopicLineDelete
            or DeviceSyncKinds.TopicDelete
            or DeviceSyncKinds.TopicClear
            or DeviceSyncKinds.ConversationDelete
            or DeviceSyncKinds.ConversationClear
            or DeviceSyncKinds.ContactDelete
            or DeviceSyncKinds.CircleDelete)
        {
            switch (kind)
            {
                case DeviceSyncKinds.TopicLineDelete:
                    if (!DeviceSyncEntityIds.TryParseTopicLine(
                            entityId, out var threadId, out var lineId))
                        throw new InvalidOperationException("The topic line tombstone ID was invalid.");
                    activeDb.ApplyTopicLineDelete(
                        threadId, lineId, entityId, kind, version);
                    break;
                case DeviceSyncKinds.TopicDelete:
                    activeDb.ApplyTopicDelete(entityId, kind, version);
                    break;
                case DeviceSyncKinds.TopicClear:
                    activeDb.ApplyTopicClear(entityId, kind, version);
                    break;
                case DeviceSyncKinds.ConversationDelete:
                    activeDb.ApplyConversationDelete(entityId, kind, version);
                    break;
                case DeviceSyncKinds.ConversationClear:
                    activeDb.ApplyConversationClear(entityId, kind, version);
                    break;
                case DeviceSyncKinds.ContactDelete:
                case DeviceSyncKinds.CircleDelete:
                    if (!activeDb.SetSyncTombstone(kind, entityId, version)) return;
                    break;
            }
        }
        else
        {
            if (!activeDb.TryAdvanceSyncVersion(SyncKey(kind, entityId), version)) return;
        }
        DeviceSyncOperationCreated?.Invoke(
            new DeviceSyncOperation(operationId, deviceId, kind, entityId, version, serialized));
    }

    private string? LocalDeviceId()
        => string.IsNullOrWhiteSpace(Profile.PublicKey)
            ? null
            : DeviceProtocol.DeviceId(Profile.PublicKey);

    private string GetOrCreateSnapshotVersion(
        string entityKey,
        DateTimeOffset at,
        string kind,
        string entityId)
    {
        var version = activeDb!.GetSyncVersion(entityKey);
        if (version is not null) return version;
        var operationId = LegacyOperationId(kind, entityId);
        version = DeviceSyncVersion.Create(at, "legacy", operationId);
        activeDb.TryAdvanceSyncVersion(entityKey, version);
        return version;
    }

    private string GetOrCreateLegacyProfileVersion<T>(
        string entityKey,
        string deviceId,
        string kind,
        string entityId,
        T payload)
    {
        var version = activeDb!.GetSyncVersion(entityKey);
        if (version is not null) return version;
        var serialized = JsonSerializer.Serialize(payload, SyncJson);
        version = ProfileSyncState.LegacyVersion(deviceId, kind, entityId, serialized);
        activeDb.TryAdvanceSyncVersion(entityKey, version);
        return version;
    }

    private static DeviceSyncOperation SnapshotOperation<T>(
        string deviceId,
        string kind,
        string entityId,
        string version,
        T payload)
        => new(
            SnapshotOperationId(version, kind, entityId),
            deviceId,
            kind,
            entityId,
            version,
            JsonSerializer.Serialize(payload, SyncJson));

    private static string SnapshotOperationId(string version, string kind, string entityId)
    {
        var separator = version.LastIndexOf('|');
        return separator >= 0 && separator + 1 < version.Length
            ? version[(separator + 1)..]
            : LegacyOperationId(kind, entityId);
    }

    private static string LegacyOperationId(string kind, string entityId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(kind + "\0" + entityId))).ToLowerInvariant();

    private static string CreateNewerVersion(
        string deviceId,
        string operationId,
        IEnumerable<string?> priorVersions)
    {
        var at = DateTimeOffset.UtcNow;
        var candidate = DeviceSyncVersion.Create(at, deviceId, operationId);
        var newest = priorVersions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Max(StringComparer.Ordinal);
        if (newest is null || DeviceSyncVersion.IsNewer(candidate, newest)) return candidate;
        var separator = newest.IndexOf('|');
        if (separator <= 0
            || !long.TryParse(newest[..separator], out var ticks)
            || ticks >= DateTimeOffset.MaxValue.UtcTicks)
            return candidate;
        return DeviceSyncVersion.Create(
            new DateTimeOffset(ticks + 1, TimeSpan.Zero), deviceId, operationId);
    }

    private bool IsNewer(DeviceSyncOperation operation, string kind)
        => DeviceSyncVersion.IsNewer(
            operation.Version,
            activeDb!.GetSyncVersion(SyncKey(kind, operation.EntityId)));

    private string? AcceptedVersion(DeviceSyncOperation operation)
        => operation.Kind switch
        {
            DeviceSyncKinds.TopicLineUpsert => activeDb!.GetSyncVersion(
                LineSyncKey(operation.Kind, operation.EntityId, SyncLineId(operation))),
            DeviceSyncKinds.ConversationLineUpsert => activeDb!.GetSyncVersion(
                LineSyncKey(operation.Kind, operation.EntityId, SyncLineId(operation))),
            DeviceSyncKinds.TopicClear
                or DeviceSyncKinds.TopicDelete
                or DeviceSyncKinds.ConversationClear
                or DeviceSyncKinds.ConversationDelete
                or DeviceSyncKinds.TopicLineDelete
                or DeviceSyncKinds.ContactDelete
                or DeviceSyncKinds.CircleDelete
                or DeviceSyncKinds.MemoryDelete
                => activeDb!.GetSyncTombstoneVersion(operation.Kind, operation.EntityId),
            _ => activeDb!.GetSyncVersion(SyncKey(operation.Kind, operation.EntityId))
        };

    private static string SyncLineId(DeviceSyncOperation operation)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceSyncLine>(operation.Payload, SyncJson)?.Id ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private bool IsBlockedByTombstone(string kind, string entityId, string version)
        => DeviceSyncVersion.Compare(
            activeDb!.GetSyncTombstoneVersion(kind, entityId),
            version) >= 0;

    private bool IsTopicLineDeleted(string threadId, string? lineId)
        => TopicRunProtocol.IsValidIdentifier(threadId)
           && TopicRunProtocol.IsValidIdentifier(lineId)
           && activeDb?.GetSyncTombstoneVersion(
               DeviceSyncKinds.TopicLineDelete,
               DeviceSyncEntityIds.TopicLine(threadId, lineId!)) is not null;

    private bool CanApplyTombstone(DeviceSyncOperation operation)
        => DeviceSyncVersion.IsNewer(
            operation.Version,
            activeDb!.GetSyncTombstoneVersion(operation.Kind, operation.EntityId));

    private bool CanApplyClear(
        DeviceSyncOperation operation,
        string lineKind,
        IReadOnlyList<ChatLine>? lines)
        => CanApplyTombstone(operation)
           && IsNewerThanLines(operation.Version, lineKind, operation.EntityId, lines);

    private bool CanApplyDelete(
        DeviceSyncOperation operation,
        string upsertKind,
        string clearKind,
        string lineKind,
        IReadOnlyList<ChatLine>? lines)
        => CanApplyTombstone(operation)
           && DeviceSyncVersion.IsNewer(
               operation.Version,
               activeDb!.GetSyncVersion(SyncKey(upsertKind, operation.EntityId)))
           && DeviceSyncVersion.IsNewer(
               operation.Version,
               activeDb.GetSyncTombstoneVersion(clearKind, operation.EntityId))
           && IsNewerThanLines(operation.Version, lineKind, operation.EntityId, lines);

    private bool CanApplyProfileDelete(DeviceSyncOperation operation, string upsertKind)
        => CanApplyTombstone(operation)
           && DeviceSyncVersion.IsNewer(
               operation.Version,
               activeDb!.GetSyncVersion(SyncKey(upsertKind, operation.EntityId)));

    private bool IsNewerThanLines(
        string version,
        string lineKind,
        string parentId,
        IReadOnlyList<ChatLine>? lines)
        => lines is null
           || lines.All(line => DeviceSyncVersion.IsNewer(
               version,
               activeDb!.GetSyncVersion(LineSyncKey(
                   lineKind, parentId, line.Id))));

    private static bool IsValidOperation(
        DeviceSyncOperation operation,
        string batchSource,
        string localDeviceId)
    {
        if (string.IsNullOrWhiteSpace(operation.OperationId)
            || string.IsNullOrWhiteSpace(operation.SourceDeviceId)
            || !string.Equals(operation.SourceDeviceId, batchSource, StringComparison.Ordinal)
            || string.Equals(operation.SourceDeviceId, localDeviceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(operation.EntityId)
            || !IsVersion(operation.Version))
            return false;
        return operation.Kind is DeviceSyncKinds.TopicUpsert
            or DeviceSyncKinds.TopicLineUpsert
            or DeviceSyncKinds.TopicLineDelete
            or DeviceSyncKinds.TopicClear
            or DeviceSyncKinds.TopicDelete
            or DeviceSyncKinds.ConversationUpsert
            or DeviceSyncKinds.ConversationLineUpsert
            or DeviceSyncKinds.ConversationClear
            or DeviceSyncKinds.ConversationDelete
            or DeviceSyncKinds.ContactUpsert
            or DeviceSyncKinds.ContactDelete
            or DeviceSyncKinds.CircleUpsert
            or DeviceSyncKinds.CircleDelete
            or DeviceSyncKinds.MemoryUpsert
            or DeviceSyncKinds.MemoryDelete;
    }

    private static bool IsVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var first = version.IndexOf('|');
        var second = first < 0 ? -1 : version.IndexOf('|', first + 1);
        return first == 19
               && second > first + 1
               && second + 1 < version.Length
               && version.IndexOf('|', second + 1) < 0
               && long.TryParse(version[..first], out var ticks)
               && ticks >= DateTimeOffset.MinValue.UtcTicks
               && ticks <= DateTimeOffset.MaxValue.UtcTicks;
    }

    private static T DeserializePayload<T>(DeviceSyncOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Payload))
            throw new JsonException("A sync upsert payload is required.");
        return JsonSerializer.Deserialize<T>(operation.Payload, SyncJson)
               ?? throw new JsonException("A sync upsert payload was null.");
    }

    private static DeviceSyncLine ToSyncLine(ChatLine line)
        => new(
            line.Id,
            line.Role,
            line.Text,
            line.Via,
            line.Status,
            line.At,
            line.SenderHandle,
            line.Internal,
            line.Reasoning,
            line.ReplyToLineId,
            line.ModelId);

    private static ChatLine ToChatLine(DeviceSyncLine line)
        => new()
        {
            Id = line.Id,
            Role = line.Role,
            Text = line.Text,
            Via = line.Via,
            Status = line.Status,
            At = line.At,
            SenderHandle = line.SenderHandle,
            Internal = line.Internal,
            Reasoning = line.Reasoning,
            ReplyToLineId = line.ReplyToLineId,
            ModelId = line.ModelId
        };

    private static DeviceSyncConversation ToSyncConversation(Conversation conversation, int sortOrder)
        => new(
            Norm(conversation.Handle),
            sortOrder,
            conversation.ServiceId,
            conversation.ServiceName,
            conversation.ProviderHandle,
            conversation.GroupId,
            conversation.GroupName,
            conversation.GroupOwnerHandle,
            conversation.GroupMembers.ToList(),
            conversation.GroupVersion,
            conversation.CreatedAt,
            conversation.LastActivityAt,
            conversation.IsPinned,
            HasActivityMetadata: true);

    private static string CircleEntityId(string? name)
        => ProfileSyncState.CircleEntityId(name);

    private IReadOnlyList<string> ActiveCircleNames()
        => Profile.Circles
            .Where(circle =>
            {
                var entityId = CircleEntityId(circle.Name);
                if (entityId.Length == 0) return false;
                var tombstone = activeDb!.GetSyncTombstoneVersion(
                    DeviceSyncKinds.CircleDelete, entityId);
                return ProfileSyncState.IsCircleAvailable(
                    true,
                    activeDb.GetSyncVersion(SyncKey(DeviceSyncKinds.CircleUpsert, entityId)),
                    tombstone);
            })
            .Select(circle => circle.Name)
            .ToList();

    private IReadOnlyList<string> RetainedContactCircleNames(IEnumerable<string> names)
        => names
            .Where(name =>
            {
                var entityId = CircleEntityId(name);
                if (entityId.Length == 0) return false;
                var tombstone = activeDb!.GetSyncTombstoneVersion(
                    DeviceSyncKinds.CircleDelete, entityId);
                if (tombstone is null) return true;
                var circleExists = Profile.Circles.Any(circle =>
                    CircleEntityId(circle.Name) == entityId);
                return ProfileSyncState.IsCircleAvailable(
                    circleExists,
                    activeDb.GetSyncVersion(SyncKey(DeviceSyncKinds.CircleUpsert, entityId)),
                    tombstone);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static DeviceSyncConversation NormalizeSyncConversation(
        DeviceSyncConversation dto,
        string handle)
    {
        var provider = string.IsNullOrWhiteSpace(dto.ProviderHandle) ? null : Norm(dto.ProviderHandle);
        var owner = string.IsNullOrWhiteSpace(dto.GroupOwnerHandle) ? null : Norm(dto.GroupOwnerHandle);
        var members = dto.GroupMembers
            .Where(member => !string.IsNullOrWhiteSpace(member))
            .Select(Norm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groupId = string.IsNullOrWhiteSpace(dto.GroupId) ? null : NormalizeGroupId(dto.GroupId);
        var serviceId = string.IsNullOrWhiteSpace(dto.ServiceId) ? null : dto.ServiceId.Trim();
        if (groupId is not null && serviceId is not null)
            throw new ArgumentException("A synchronized conversation cannot be both a group and a service.");
        if (groupId is not null
            && (string.IsNullOrWhiteSpace(dto.GroupName)
                || owner is null
                || dto.GroupVersion < 1
                || members.Count < 2
                || !members.Contains(owner, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Synchronized group metadata is invalid.");
        if (groupId is not null && handle != GroupKey(groupId))
            throw new ArgumentException("Synchronized group handle is invalid.");
        if (serviceId is not null && provider is null)
            throw new ArgumentException("Synchronized service metadata is invalid.");
        if (serviceId is not null && handle != ServiceKey(provider!, serviceId))
            throw new ArgumentException("Synchronized service handle is invalid.");
        return dto with
        {
            Handle = handle,
            ServiceId = serviceId,
            ServiceName = serviceId is null ? null : dto.ServiceName,
            ProviderHandle = provider,
            GroupId = groupId,
            GroupName = groupId is null ? null : dto.GroupName!.Trim(),
            GroupOwnerHandle = groupId is null ? null : owner,
            GroupMembers = groupId is null ? Array.Empty<string>() : members,
            GroupVersion = groupId is null ? 0 : dto.GroupVersion
        };
    }

    private static bool IsValidLine(DeviceSyncLine line, string lineId)
        => !string.IsNullOrWhiteSpace(line.Id)
           && string.Equals(line.Id, lineId, StringComparison.Ordinal)
           && line.At != default
           && line.Role is not null
           && line.Text is not null
           && line.Via is not null
           && line.Status is not null
           && (line.ReplyToLineId is null
               || TopicRunProtocol.IsValidIdentifier(line.ReplyToLineId));

    private static bool LineEquals(ChatLine line, DeviceSyncLine dto)
        => line.Role == dto.Role
           && line.Text == dto.Text
           && line.Via == dto.Via
           && line.Status == dto.Status
           && line.At == dto.At
           && line.SenderHandle == dto.SenderHandle
           && line.Internal == dto.Internal
           && line.Reasoning == dto.Reasoning
           && line.ReplyToLineId == dto.ReplyToLineId
           && (dto.ModelId is null || line.ModelId == dto.ModelId);

    private static void MergeLine(ChatLine line, DeviceSyncLine dto)
    {
        line.Role = dto.Role;
        line.Text = dto.Text;
        line.Via = dto.Via;
        line.Status = dto.Status;
        line.At = dto.At;
        line.SenderHandle = dto.SenderHandle;
        line.Internal = dto.Internal;
        line.Reasoning = dto.Reasoning;
        line.ReplyToLineId = dto.ReplyToLineId;
        if (dto.ModelId is not null) line.ModelId = dto.ModelId;
        line.Attachments.Clear();
    }

    private static bool ConversationCoreEquals(Conversation conversation, DeviceSyncConversation dto)
        => conversation.Handle == dto.Handle
           && conversation.ServiceId == dto.ServiceId
           && conversation.ServiceName == dto.ServiceName
           && conversation.ProviderHandle == dto.ProviderHandle
           && conversation.GroupId == dto.GroupId
           && conversation.GroupName == dto.GroupName
           && conversation.GroupOwnerHandle == dto.GroupOwnerHandle
           && conversation.GroupMembers.SequenceEqual(dto.GroupMembers, StringComparer.OrdinalIgnoreCase)
           && conversation.GroupVersion == dto.GroupVersion;

    private static void MergeConversation(Conversation conversation, DeviceSyncConversation dto)
    {
        conversation.Handle = dto.Handle;
        conversation.ServiceId = dto.ServiceId;
        conversation.ServiceName = dto.ServiceName;
        conversation.ProviderHandle = dto.ProviderHandle;
        conversation.GroupId = dto.GroupId;
        conversation.GroupName = dto.GroupName;
        conversation.GroupOwnerHandle = dto.GroupOwnerHandle;
        conversation.GroupMembers = dto.GroupMembers.ToList();
        conversation.GroupVersion = dto.GroupVersion;
    }

    private static string SyncKey(string kind, string entityId) => kind + "\u001f" + entityId;

    private static string LineSyncKey(string kind, string parentId, string lineId)
        => kind + "\u001f" + parentId + "\u001f" + lineId;

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
            if (!Profile.UnreadFrom.Contains(h)) { Profile.UnreadFrom.Add(h); activeDb?.SaveProfile(Profile); }
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
    private readonly Dictionary<string, RemoteRunProjection> remoteRuns = new(StringComparer.Ordinal);
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
            activeDb?.SetOwnThreadExecutionAndActivity(
                thread.Id,
                thread.ExecutionDeviceId,
                thread.ExecutionDeviceName,
                thread.ExecutionDevicePlatform,
                thread.ExecutionAt,
                thread.ExecutionRunId,
                thread.LastActivityAt!.Value);
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
            activeDb?.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value);
            EmitTopicUpsert(thread);
        }
        NotifyChanged();
    }

    /// <summary>Gets the current remote run projection for a thread, or null.</summary>
    public RemoteRunProjection? GetRemoteRunProjection(string threadId)
    {
        lock (profileSyncGate)
            return remoteRuns.TryGetValue(threadId, out var projection)
                ? CloneRemoteRunProjection(projection)
                : null;
    }

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
                && !activeDb.SetOwnThreadExecutionAndActivity(
                    thread.Id,
                    target.DeviceId,
                    target.DeviceName,
                    target.Platform,
                    startedAt,
                    runId,
                    activityAt))
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
    {
        ApplyQueuedTopicRunUpdate(update);
        // A streamed reply fragment rides the same channel as run-state updates but is applied to the
        // live draft, not the run projection, so it never disturbs the phase/steps the viewer is showing.
        if (update.Delta is { Length: > 0 })
        {
            ApplyRemoteAssistantDelta(update);
            return;
        }
        ApplyRemoteRunProjection(update.ThreadId, new RemoteRunProjection
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
        });
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
        var correlationKey = threadId + "\0" + projection.RunId;
        if (!string.Equals(threadId, projection.ThreadId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(projection.RunId)
            || projection.Timestamp == default)
            return;

        OwnThread? thread;
        lock (profileSyncGate)
        {
            if (terminalRemoteRuns.Contains(correlationKey))
                return;
            thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
            if (!RemoteRunCorrelation.IsExpected(thread, threadId, projection.RunId)
                || remoteRuns.TryGetValue(threadId, out var current)
                   && projection.Timestamp < current.Timestamp)
                return;

            var activityAt = ActivityTimestamp.Advance(
                thread!.LastActivityAt, projection.Timestamp);
            var terminal = projection.Phase is TopicRunPhase.Completed
                or TopicRunPhase.Failed
                or TopicRunPhase.Cancelled;
            var nextRunId = terminal ? null : projection.RunId;
            var executionAt = thread.ExecutionAt ?? projection.Timestamp;
            if (activeDb is not null
                && !activeDb.SetOwnThreadExecutionAndActivity(
                    thread.Id,
                    thread.ExecutionDeviceId,
                    thread.ExecutionDeviceName,
                    thread.ExecutionDevicePlatform,
                    executionAt,
                    nextRunId,
                    activityAt))
                return;

            thread.LastActivityAt = activityAt;
            thread.ExecutionAt = executionAt;
            thread.ExecutionRunId = nextRunId;
            if (terminal)
            {
                remoteRuns.Remove(threadId);
                terminalRemoteRuns.Add(correlationKey);
                remoteDeltaSeq.Remove(correlationKey);
                liveAgentRenderState.EndDraft(threadId);
                assistantDraftRefreshGate.Reset(threadId);
            }
            else
            {
                remoteRuns[threadId] = CloneRemoteRunProjection(projection);
            }
        }
        EmitTopicUpsert(thread!);
        NotifyChanged();
    }

    /// <summary>Clears the remote run projection for a thread (run completed or cancelled).</summary>
    public void ClearRemoteRunProjection(
        string threadId,
        string? runId = null,
        DateTimeOffset? clearedAt = null)
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
                && !activeDb.SetOwnThreadExecutionAndActivity(
                    thread.Id,
                    thread.ExecutionDeviceId,
                    thread.ExecutionDeviceName,
                    thread.ExecutionDevicePlatform,
                    thread.ExecutionAt,
                    null,
                    activityAt))
                return;
            remoteRuns.Remove(threadId);
            terminalRemoteRuns.Add(threadId + "\0" + correlatedRunId);
            thread.ExecutionRunId = null;
            thread.LastActivityAt = activityAt;
            liveAgentRenderState.EndDraft(threadId);
            assistantDraftRefreshGate.Reset(threadId);
        }
        EmitTopicUpsert(thread!);
        NotifyChanged();
    }

    /// <summary>
    /// Finalizes a lingering LIVE remote-run projection when the executing device's committed assistant
    /// answer arrives via device sync. The terminal update and committed line travel independently, so the
    /// line can arrive first or the update can be unavailable on a legacy relay. The durable answer is the
    /// terminal truth: drop the projection, null the persisted run id, and
    /// mark the run terminal so a late replay of a non-terminal update for the same run cannot resurrect
    /// it. Runs while applying a device-sync batch (under profileSyncGate, which is reentrant) so it must
    /// not re-broadcast: it mutates local state only and returns true so the caller refreshes the UI.
    /// </summary>
    private bool ReconcileRemoteRunWithAnswer(OwnThread thread, DateTimeOffset answerAt)
    {
        lock (profileSyncGate)
        {
            if (!remoteRuns.TryGetValue(thread.Id, out var projection)
                || !RemoteRunReconciliation.ShouldFinalizeOnAnswer(projection, answerAt))
                return false;

            if (RemoteRunCorrelation.IsExpected(thread, thread.Id, projection.RunId))
            {
                var activityAt = ActivityTimestamp.Advance(thread.LastActivityAt, answerAt);
                if (activeDb is not null
                    && !activeDb.SetOwnThreadExecutionAndActivity(
                        thread.Id,
                        thread.ExecutionDeviceId,
                        thread.ExecutionDeviceName,
                        thread.ExecutionDevicePlatform,
                        thread.ExecutionAt ?? answerAt,
                        null,
                        activityAt))
                    return false;
                thread.LastActivityAt = activityAt;
                thread.ExecutionRunId = null;
            }
            remoteRuns.Remove(thread.Id);
            terminalRemoteRuns.Add(thread.Id + "\0" + projection.RunId);
            remoteDeltaSeq.Remove(thread.Id + "\0" + projection.RunId);
            liveAgentRenderState.EndDraft(thread.Id);
            assistantDraftRefreshGate.Reset(thread.Id);
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
            var at = updatedAt ?? DateTimeOffset.UtcNow;
            thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, at);
            activeDb?.SetOwnThreadActivity(thread.Id, thread.LastActivityAt!.Value);
            EmitTopicUpsert(thread);
        }
        NotifyChanged();
    }

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
            if (queuedTopicRuns.IsKnownRun(update.ThreadId, update.RunId)) return true;
            if (update.Phase != TopicRunPhase.Queued
                || !TopicRunProtocol.IsValidIdentifier(update.TriggerLineId))
                return false;
            var thread = Profile.OwnThreads.FirstOrDefault(item =>
                string.Equals(item.Id, update.ThreadId, StringComparison.Ordinal));
            return thread?.Lines.Any(line =>
                string.Equals(line.Id, update.TriggerLineId, StringComparison.Ordinal)
                && string.Equals(line.Role, "user", StringComparison.Ordinal)) == true;
        }
    }

    private void ApplyQueuedTopicRunUpdate(TopicRunUpdatePayload update)
    {
        if (update.Phase == TopicRunPhase.Queued)
        {
            if (TopicRunProtocol.IsValidIdentifier(update.TriggerLineId))
                TrackQueuedTopicRun(
                    update.ThreadId, update.RunId, update.TriggerLineId!, TopicQueueStage.Device);
            SetTopicOutboxState(update.RunId, TopicOutboxStates.DeviceQueued);
            return;
        }
        if (update.Phase is TopicRunPhase.Completed or TopicRunPhase.Failed or TopicRunPhase.Cancelled)
        {
            CompleteQueuedTopicRun(update.ThreadId, update.RunId);
            DeleteTopicOutbox(update.RunId);
        }
        else
        {
            StartQueuedTopicRun(update.ThreadId, update.RunId);
            SetTopicOutboxState(update.RunId, TopicOutboxStates.Running);
        }
    }

    /// <summary>True when a specific line is still waiting in some thread's queue (drives the "queued" tag).</summary>
    public bool IsLineQueued(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (profileSyncGate)
            return queuedTopicRuns.IsLineWaiting(line.Id);
    }

    public QueuedTopicRunInfo? QueuedTopicRunForLine(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (profileSyncGate)
            return queuedTopicRuns.FindByLine(line.Id);
    }

    public bool IsQueuedTopicRunLine(string threadId, string runId, string lineId)
    {
        lock (profileSyncGate)
        {
            var queued = queuedTopicRuns.FindByLine(threadId, lineId);
            return queued is { Waiting: true }
                   && string.Equals(queued.ThreadId, threadId, StringComparison.Ordinal)
                   && string.Equals(queued.RunId, runId, StringComparison.Ordinal);
        }
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
                var lineVersions = removed.Select(line => activeDb?.GetSyncVersion(LineSyncKey(
                    DeviceSyncKinds.TopicLineUpsert, thread.Id, line.Id))).ToList();
                thread.Lines.RemoveAll(line =>
                    string.Equals(line.Id, lineId, StringComparison.Ordinal)
                    || string.Equals(line.ReplyToLineId, lineId, StringComparison.Ordinal));
                queuedTopicRuns.Complete(threadId, runId);
                EmitTombstone(
                    DeviceSyncKinds.TopicLineDelete,
                    DeviceSyncEntityIds.TopicLine(threadId, lineId),
                    lineVersions);
                visibleChanged = true;
                deleted = true;
            }
        }
        if (visibleChanged) NotifyChanged();
        return deleted;
    }

    public bool IsKnownQueuedTopicRun(string threadId, string runId)
    {
        lock (profileSyncGate)
            return queuedTopicRuns.IsKnownRun(threadId, runId);
    }

    /// <summary>Number of lines currently queued for a thread.</summary>
    public int QueuedCountForThread(string threadId)
    {
        lock (profileSyncGate)
            return queuedTopicRuns.WaitingCount(threadId);
    }

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
        var changed = unread.Remove(h);
        if (Profile.UnreadFrom.Remove(h)) { activeDb?.SaveProfile(Profile); changed = true; }
        if (changed) NotifyChanged();
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
            EmitLineUpsert(DeviceSyncKinds.ConversationLineUpsert, owner.Handle, updated);
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
            EmitLineUpsert(DeviceSyncKinds.ConversationLineUpsert, owner.Handle, updated);
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
    public byte[] ExportActiveProfile(string passphrase) => MeshExport.Create(Profile, passphrase);

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

        if (activeId is not null && activeDb is not null) activeDb.SaveProfile(Profile);

        var id = NewId();
        var db = OpenDb(id);
        foreach (var conv in imported.Conversations)
        {
            conv.Handle = PrepareConversationForPersistence(conv);
            DeriveActivityMetadata(conv);
            db.EnsureConversation(conv.Handle, conv.CreatedAt);
            PersistConversationMetadata(db, conv);
            if (conv.LastActivityAt.HasValue)
                db.SetConversationActivity(conv.Handle, conv.LastActivityAt.Value);
            if (conv.IsPinned)
                db.SetConversationPin(conv.Handle, true);
            foreach (var line in conv.Lines) db.AppendChatLine(Norm(conv.Handle), line);
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
            db.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt);
            if (thread.LastActivityAt.HasValue)
                db.SetOwnThreadActivity(thread.Id, thread.LastActivityAt.Value);
            if (thread.IsPinned)
                db.SetOwnThreadPin(thread.Id, true);
            if (thread.ExecutionDeviceId is not null || thread.ExecutionAt.HasValue || thread.ExecutionRunId is not null)
                db.SetOwnThreadExecution(
                    thread.Id,
                    thread.ExecutionDeviceId,
                    thread.ExecutionAt,
                    thread.ExecutionRunId,
                    thread.ExecutionDeviceName,
                    thread.ExecutionDevicePlatform);
            foreach (var line in thread.Lines) db.AppendOwnChat(thread.Id, line);
        }
        for (var i = 0; i < imported.Memories.Count; i++)
        {
            var memory = MemoryPolicy.Normalize(imported.Memories[i]);
            imported.Memories[i] = memory;
            db.UpsertMemory(memory);
        }
        db.SaveProfile(imported);

        activeDb?.Dispose();
        activeDb = db;
        activeId = id;
        Profile = imported;
        RehydrateUnread();
        RehydrateDurableTopicState();
        accounts.Add(new AccountRef { Id = id, Handle = imported.Handle, DisplayName = imported.DisplayName });
        WriteIndex();
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
        if (activeId is not null) activeDb?.SaveProfile(Profile);
        activeDb?.Dispose();
        activeDb = null;
        activeId = null;
        Profile = new MeshProfile();
        queuedTopicRuns.Clear();
        WriteIndex();
        NotifyChanged();
    }

    /// <summary>Switch the active identity to a previously saved account.</summary>
    public bool SwitchAccount(string id)
    {
        if (id == activeId) return true;
        MeshDb? db = null;
        try
        {
            db = OpenDb(id);
            var loaded = db.LoadProfile();
            if (loaded is null) { db.Dispose(); return false; }

            if (activeId is not null) activeDb?.SaveProfile(Profile); // persist the one we're leaving
            activeDb?.Dispose();
            activeDb = db;
            activeId = id;
            Profile = loaded;
            RehydrateUnread();
            RehydrateDurableTopicState();
            WriteIndex();
            NotifyChanged();
            return true;
        }
        catch { db?.Dispose(); return false; }
    }

    /// <summary>Permanently remove a saved identity: its database file and its master key.</summary>
    public void DeleteAccount(string id)
    {
        accounts.RemoveAll(a => a.Id == id);
        if (id == activeId)
        {
            activeDb?.Dispose();
            activeDb = null;
            activeId = null;
            Profile = new MeshProfile();
        }
        try { var p = DbPath(id); if (File.Exists(p)) File.Delete(p); } catch { }
        secrets.DeleteDbKey(id);
        WriteIndex();
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
        activeDb?.SetConversationGroup(
            key, conv.GroupId, conv.GroupName, conv.GroupOwnerHandle, conv.GroupMembers, conv.GroupVersion);
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
            activeDb?.SetConversationService(key, serviceId, name, provider);
            changed = true;
        }
        else if (conv.ServiceId != serviceId
                 || conv.ServiceName != name
                 || conv.ProviderHandle != provider)
        {
            conv.ServiceId = serviceId;
            conv.ServiceName = name;
            conv.ProviderHandle = provider;
            activeDb?.SetConversationService(key, serviceId, name, provider);
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
            activeDb?.EnsureConversation(handle);
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
            activeDb?.ReorderConversations(ordered.Select(c => c.Handle).ToList());
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
            activeDb?.SetConversationPinAndActivity(h, pinned, activityAt);
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
        var lineVersions = conv.Lines
            .Select(line => activeDb?.GetSyncVersion(LineSyncKey(
                DeviceSyncKinds.ConversationLineUpsert, h, line.Id)))
            .ToList();
        conv.Lines.Clear();
        EmitTombstone(DeviceSyncKinds.ConversationClear, h, lineVersions);
        NotifyChanged();
    }

    /// <summary>Deletes a conversation and its history entirely (the contact itself is kept).</summary>
    public void DeleteConversation(string handle)
    {
        var h = Norm(handle);
        var conversation = Profile.Conversations.FirstOrDefault(
            c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
        if (conversation is null) return;
        var lineVersions = conversation.Lines
            .Select(line => activeDb?.GetSyncVersion(LineSyncKey(
                DeviceSyncKinds.ConversationLineUpsert, h, line.Id)))
            .ToList();
        Profile.Conversations.Remove(conversation);
        unread.Remove(h);
        if (Profile.UnreadFrom.Remove(h)) activeDb?.SaveProfile(Profile);
        EmitTombstone(DeviceSyncKinds.ConversationDelete, h, lineVersions);
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
            db.SetConversationGroup(
                conversation.Handle,
                conversation.GroupId!,
                conversation.GroupName
                    ?? throw new InvalidOperationException($"Group conversation '{conversation.Handle}' has no name."),
                conversation.GroupOwnerHandle
                    ?? throw new InvalidOperationException($"Group conversation '{conversation.Handle}' has no owner."),
                conversation.GroupMembers,
                conversation.GroupVersion);
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
