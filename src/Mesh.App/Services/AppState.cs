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
public sealed class AppState
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
        var key = secrets.GetOrCreateDbKey(id);
        return MeshDb.Open(DbPath(id), key);
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
                activeDb.EnsureConversation(conv.Handle);
                PersistConversationMetadata(activeDb, conv);
                foreach (var line in conv.Lines) activeDb.AppendChatLine(Norm(conv.Handle), line);
            }
            foreach (var thread in Profile.OwnThreads)
            {
                activeDb.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt);
                foreach (var line in thread.Lines) activeDb.AppendOwnChat(thread.Id, line);
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
        activeDb?.AppendChatLine(conv.Handle, line);
        EmitLineUpsert(DeviceSyncKinds.ConversationLineUpsert, conv.Handle, line);
        NotifyChanged();
    }

    /// <summary>Appends a line to a "Me" topic thread as a single row.</summary>
    public void AddOwnChatLine(string threadId, ChatLine line)
    {
        var thread = GetOrCreateOwnThread(threadId);
        thread.Lines.Add(line);
        activeDb?.AppendOwnChat(thread.Id, line);
        EmitLineUpsert(DeviceSyncKinds.TopicLineUpsert, thread.Id, line);
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
    public OwnThread NewOwnThread(string title = "New chat")
    {
        var thread = new OwnThread { Title = title };
        Profile.OwnThreads.Add(thread);
        activeDb?.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt);
        EmitTopicUpsert(thread);
        NotifyChanged();
        return thread;
    }

    /// <summary>Renames a "Me" thread.</summary>
    /// <summary>Moves one private topic to the requested list position and persists the order.</summary>
    public void ReorderOwnThread(string threadId, int newIndex)
    {
        var oldIndex = Profile.OwnThreads.FindIndex(t => t.Id == threadId);
        if (oldIndex < 0 || Profile.OwnThreads.Count < 2) return;
        newIndex = Math.Clamp(newIndex, 0, Profile.OwnThreads.Count - 1);
        if (oldIndex == newIndex) return;
        var thread = Profile.OwnThreads[oldIndex];
        Profile.OwnThreads.RemoveAt(oldIndex);
        Profile.OwnThreads.Insert(newIndex, thread);
        activeDb?.ReorderOwnThreads(Profile.OwnThreads.Select(t => t.Id).ToList());
        foreach (var orderedThread in Profile.OwnThreads) EmitTopicUpsert(orderedThread);
        NotifyChanged();
    }

    public void RenameOwnThread(string threadId, string title)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        thread.Title = string.IsNullOrWhiteSpace(title) ? thread.Title : title.Trim();
        activeDb?.RenameOwnThread(thread.Id, thread.Title);
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
                new DeviceSyncTopic(thread.Id, thread.Title, thread.CreatedAt, i)));

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
            _ => false
        };
    }

    private bool ApplyTopicUpsert(DeviceSyncOperation operation)
    {
        var dto = DeserializePayload<DeviceSyncTopic>(operation);
        if (string.IsNullOrWhiteSpace(dto.Id)
            || !string.Equals(dto.Id, operation.EntityId, StringComparison.Ordinal)
            || dto.SortOrder < 0
            || IsBlockedByTombstone(DeviceSyncKinds.TopicDelete, dto.Id, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.TopicUpsert))
            return false;

        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == dto.Id);
        var changed = thread is null
            || !string.Equals(thread.Title, dto.Title, StringComparison.Ordinal)
            || thread.CreatedAt != dto.CreatedAt
            || Profile.OwnThreads.IndexOf(thread) != Math.Min(dto.SortOrder, Profile.OwnThreads.Count - 1);
        if (thread is null)
        {
            thread = new OwnThread { Id = dto.Id };
            Profile.OwnThreads.Insert(Math.Min(dto.SortOrder, Profile.OwnThreads.Count), thread);
        }
        else
        {
            Profile.OwnThreads.Remove(thread);
            Profile.OwnThreads.Insert(Math.Min(dto.SortOrder, Profile.OwnThreads.Count), thread);
        }
        thread.Title = dto.Title ?? "";
        thread.CreatedAt = dto.CreatedAt;
        activeDb!.UpsertOwnThread(thread.Id, thread.Title, thread.CreatedAt, Profile.OwnThreads.IndexOf(thread));
        activeDb.TryAdvanceSyncVersion(SyncKey(operation.Kind, operation.EntityId), operation.Version);
        activeDb.ReorderOwnThreads(Profile.OwnThreads.Select(t => t.Id).ToList());
        return changed;
    }

    private bool ApplyTopicLineUpsert(DeviceSyncOperation operation)
    {
        var threadId = operation.EntityId;
        var dto = DeserializePayload<DeviceSyncLine>(operation);
        var lineId = dto.Id;
        if (!IsValidLine(dto, lineId)
            || IsBlockedByTombstone(DeviceSyncKinds.TopicDelete, threadId, operation.Version)
            || IsBlockedByTombstone(DeviceSyncKinds.TopicClear, threadId, operation.Version)
            || !DeviceSyncVersion.IsNewer(
                operation.Version,
                activeDb!.GetSyncVersion(LineSyncKey(
                    DeviceSyncKinds.TopicLineUpsert, threadId, lineId))))
            return false;

        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null)
        {
            thread = new OwnThread { Id = threadId, CreatedAt = dto.At };
            Profile.OwnThreads.Add(thread);
            activeDb!.UpsertOwnThread(thread.Id, thread.Title, thread.CreatedAt, Profile.OwnThreads.Count - 1);
        }
        var line = thread.Lines.FirstOrDefault(item => item.Id == lineId);
        var changed = line is null || !LineEquals(line, dto);
        if (line is null)
        {
            line = new ChatLine { Id = lineId };
            thread.Lines.Add(line);
        }
        MergeLine(line, dto);
        activeDb!.UpsertOwnChat(threadId, line);
        activeDb.TryAdvanceSyncVersion(
            LineSyncKey(operation.Kind, threadId, lineId), operation.Version);
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
            || dto.SortOrder < 0
            || dto.GroupMembers is null
            || IsBlockedByTombstone(DeviceSyncKinds.ConversationDelete, handle, operation.Version)
            || !IsNewer(operation, DeviceSyncKinds.ConversationUpsert))
            return false;

        var normalized = NormalizeSyncConversation(dto, handle);
        var conversation = FindConversation(handle);
        var targetIndex = Math.Min(normalized.SortOrder, Math.Max(0, Profile.Conversations.Count - 1));
        var changed = conversation is null
            || !ConversationEquals(conversation, normalized)
            || Profile.Conversations.IndexOf(conversation) != targetIndex;
        if (conversation is null)
        {
            conversation = new Conversation { Handle = handle };
            Profile.Conversations.Insert(Math.Min(normalized.SortOrder, Profile.Conversations.Count), conversation);
        }
        else
        {
            Profile.Conversations.Remove(conversation);
            Profile.Conversations.Insert(Math.Min(normalized.SortOrder, Profile.Conversations.Count), conversation);
        }
        MergeConversation(conversation, normalized);
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
            conversation.GroupVersion);
        activeDb.TryAdvanceSyncVersion(SyncKey(operation.Kind, operation.EntityId), operation.Version);
        activeDb.ReorderConversations(Profile.Conversations.Select(c => c.Handle).ToList());
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
        if (conversation is null)
        {
            conversation = new Conversation { Handle = handle };
            Profile.Conversations.Add(conversation);
            activeDb!.UpsertConversation(
                handle, Profile.Conversations.Count - 1, null, null, null, null, null, null, Array.Empty<string>(), 0);
        }
        var line = conversation.Lines.FirstOrDefault(item => item.Id == lineId);
        var changed = line is null || !LineEquals(line, dto);
        if (line is null)
        {
            line = new ChatLine { Id = lineId };
            conversation.Lines.Add(line);
        }
        MergeLine(line, dto);
        activeDb!.UpsertChatLine(handle, line);
        activeDb.TryAdvanceSyncVersion(
            LineSyncKey(operation.Kind, handle, lineId), operation.Version);
        return changed;
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
            new DeviceSyncTopic(thread.Id, thread.Title, thread.CreatedAt, Math.Max(0, sortOrder)),
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
                activeDb.GetSyncTombstoneVersion(clearKind, parentId)
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
        if (kind is DeviceSyncKinds.TopicDelete
            or DeviceSyncKinds.TopicClear
            or DeviceSyncKinds.ConversationDelete
            or DeviceSyncKinds.ConversationClear
            or DeviceSyncKinds.ContactDelete
            or DeviceSyncKinds.CircleDelete)
        {
            switch (kind)
            {
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
                or DeviceSyncKinds.ContactDelete
                or DeviceSyncKinds.CircleDelete
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
            or DeviceSyncKinds.TopicClear
            or DeviceSyncKinds.TopicDelete
            or DeviceSyncKinds.ConversationUpsert
            or DeviceSyncKinds.ConversationLineUpsert
            or DeviceSyncKinds.ConversationClear
            or DeviceSyncKinds.ConversationDelete
            or DeviceSyncKinds.ContactUpsert
            or DeviceSyncKinds.ContactDelete
            or DeviceSyncKinds.CircleUpsert
            or DeviceSyncKinds.CircleDelete;
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
            line.Reasoning);

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
            conversation.GroupVersion);

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
           && line.Role is not null
           && line.Text is not null
           && line.Via is not null
           && line.Status is not null;

    private static bool LineEquals(ChatLine line, DeviceSyncLine dto)
        => line.Role == dto.Role
           && line.Text == dto.Text
           && line.Via == dto.Via
           && line.Status == dto.Status
           && line.At == dto.At
           && line.SenderHandle == dto.SenderHandle
           && line.Internal == dto.Internal
           && line.Reasoning == dto.Reasoning;

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
        line.Attachments.Clear();
    }

    private static bool ConversationEquals(Conversation conversation, DeviceSyncConversation dto)
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
    private readonly Dictionary<string, List<AgentStep>> agentSteps = new(StringComparer.Ordinal);
    private static readonly IReadOnlyList<AgentStep> NoSteps = Array.Empty<AgentStep>();

    /// <summary>The steps taken so far in the given thread's current turn (most recent last).</summary>
    public IReadOnlyList<AgentStep> AgentStepsFor(string key)
        => agentSteps.TryGetValue(key, out var l) ? l : NoSteps;

    /// <summary>Clears one thread's step trace at the start of a new turn.</summary>
    public void BeginAgentSteps(string key)
    {
        if (agentSteps.TryGetValue(key, out var l) && l.Count == 0) return;
        agentSteps[key] = new List<AgentStep>();
        NotifyChanged();
    }

    /// <summary>
    /// Records a step for a thread. A Started step is appended; a Done/Failed step updates the matching
    /// pending step in place (so a tool shows as running then completed rather than twice).
    /// </summary>
    public void ReportAgentStep(string key, AgentStep step)
    {
        if (!agentSteps.TryGetValue(key, out var steps))
            agentSteps[key] = steps = new List<AgentStep>();

        if (step.State == AgentStepState.Started)
        {
            steps.Add(step);
        }
        else
        {
            var i = steps.FindLastIndex(s => s.Tool == step.Tool && s.State == AgentStepState.Started);
            if (i >= 0) steps[i] = step;
            else steps.Add(step);
        }
        NotifyChanged();
    }

    /// <summary>Clears one thread's step trace when its turn ends.</summary>
    public void EndAgentSteps(string key)
    {
        if (agentSteps.Remove(key)) NotifyChanged();
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
    private readonly Dictionary<string, List<ChatLine>> queuedByThread = new(StringComparer.Ordinal);
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
        NotifyChanged();
    }

    public void ClearAgentRun(string threadId)
    {
        if (agentRuns.Remove(threadId)) NotifyChanged();
    }

    public void UpdateAgentRun(string threadId, AgentRunPhase phase,
        IReadOnlyList<AgentSubtaskState>? subtasks = null)
    {
        if (!agentRuns.TryGetValue(threadId, out var run)) return;
        agentRuns[threadId] = run with { Phase = phase, Subtasks = subtasks ?? run.Subtasks };
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

    /// <summary>
    /// Queues a user line for a thread whose turn is already running (steerable input). The line is
    /// also added to the thread history by the caller; this tracks that it is pending so the UI can
    /// tag it and the running turn can drain it.
    /// </summary>
    public void EnqueueForThread(string threadId, ChatLine line)
    {
        if (!queuedByThread.TryGetValue(threadId, out var l))
            queuedByThread[threadId] = l = new List<ChatLine>();
        l.Add(line);
        NotifyChanged();
    }

    /// <summary>True when a specific line is still waiting in some thread's queue (drives the "queued" tag).</summary>
    public bool IsLineQueued(ChatLine line)
    {
        foreach (var l in queuedByThread.Values)
            if (l.Contains(line)) return true;
        return false;
    }

    /// <summary>Number of lines currently queued for a thread.</summary>
    public int QueuedCountForThread(string threadId)
        => queuedByThread.TryGetValue(threadId, out var l) ? l.Count : 0;

    /// <summary>Clears a thread's queue (called when the running turn starts answering the queued lines).</summary>
    public void ClearThreadQueue(string threadId)
    {
        if (queuedByThread.TryGetValue(threadId, out var l) && l.Count > 0)
        {
            l.Clear();
            NotifyChanged();
        }
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
            db.EnsureConversation(conv.Handle);
            PersistConversationMetadata(db, conv);
            foreach (var line in conv.Lines) db.AppendChatLine(Norm(conv.Handle), line);
        }
        // Migrate a legacy single OwnChat (older exports) into a thread so nothing is lost.
        if (imported.OwnChat.Count > 0)
        {
            var legacy = new OwnThread { Title = "General", Lines = imported.OwnChat.ToList() };
            imported.OwnThreads.Insert(0, legacy);
            imported.OwnChat = new List<ChatLine>();
        }
        foreach (var thread in imported.OwnThreads)
        {
            db.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt);
            foreach (var line in thread.Lines) db.AppendOwnChat(thread.Id, line);
        }
        db.SaveProfile(imported);

        activeDb?.Dispose();
        activeDb = db;
        activeId = id;
        Profile = imported;
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
            conv = new Conversation { Handle = handle };
            Profile.Conversations.Add(conv);
            activeDb?.EnsureConversation(handle);
            EmitConversationUpsert(conv);
        }
        return conv;
    }

    /// <summary>Moves one message conversation to the requested list position and persists the order.</summary>
    public void ReorderConversation(string handle, int newIndex)
    {
        var normalized = Norm(handle);
        var oldIndex = Profile.Conversations.FindIndex(
            c => c.Handle.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (oldIndex < 0 || Profile.Conversations.Count < 2) return;
        newIndex = Math.Clamp(newIndex, 0, Profile.Conversations.Count - 1);
        if (oldIndex == newIndex) return;
        var conversation = Profile.Conversations[oldIndex];
        Profile.Conversations.RemoveAt(oldIndex);
        Profile.Conversations.Insert(newIndex, conversation);
        activeDb?.ReorderConversations(Profile.Conversations.Select(c => c.Handle).ToList());
        foreach (var orderedConversation in Profile.Conversations)
            EmitConversationUpsert(orderedConversation);
        NotifyChanged();
    }

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
