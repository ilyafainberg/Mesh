using System.Text.Json;
using Mesh.App.Domain;

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

    private sealed class AccountIndex
    {
        public string? ActiveId { get; set; }
        public List<AccountRef> Accounts { get; set; } = new();
    }

    private readonly ISecretStore secrets;
    private readonly string dir;
    private readonly string indexPath;
    private string? activeId;
    private List<AccountRef> accounts = new();
    private MeshDb? activeDb;

    public MeshProfile Profile { get; private set; } = new();

    public event Action? Changed;

    // Handles with unread inbound person-messages (in-memory, cleared when the conversation is viewed).
    private readonly HashSet<string> unread = new(StringComparer.OrdinalIgnoreCase);

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
                activeDb.EnsureConversation(conv.Handle);
                foreach (var line in conv.Lines) activeDb.AppendChatLine(Norm(conv.Handle), line);
            }
            foreach (var thread in Profile.OwnThreads)
            {
                activeDb.EnsureOwnThread(thread.Id, thread.Title, thread.CreatedAt);
                foreach (var line in thread.Lines) activeDb.AppendOwnChat(thread.Id, line);
            }
        }

        if (activeId is not null)
        {
            var acc = accounts.FirstOrDefault(a => a.Id == activeId);
            if (acc is null) { acc = new AccountRef { Id = activeId }; accounts.Add(acc); }
            acc.Handle = Profile.Handle;
            acc.DisplayName = Profile.DisplayName;
            activeDb?.SaveProfile(Profile);
        }
        WriteIndex();
    }

    public void Mutate(Action<MeshProfile> change)
    {
        change(Profile);
        Save();
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
        activeDb?.AppendChatLine(Norm(handle), line);
        NotifyChanged();
    }

    /// <summary>Appends a line to a "Me" topic thread as a single row.</summary>
    public void AddOwnChatLine(string threadId, ChatLine line)
    {
        var thread = GetOrCreateOwnThread(threadId);
        thread.Lines.Add(line);
        activeDb?.AppendOwnChat(thread.Id, line);
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
        NotifyChanged();
        return thread;
    }

    /// <summary>Renames a "Me" thread.</summary>
    public void RenameOwnThread(string threadId, string title)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        thread.Title = string.IsNullOrWhiteSpace(title) ? thread.Title : title.Trim();
        activeDb?.RenameOwnThread(thread.Id, thread.Title);
        NotifyChanged();
    }

    /// <summary>Clears a "Me" thread's messages but keeps the thread.</summary>
    public void ClearOwnThread(string threadId)
    {
        var thread = Profile.OwnThreads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return;
        thread.Lines.Clear();
        activeDb?.ClearOwnThread(thread.Id);
        NotifyChanged();
    }

    /// <summary>Deletes a "Me" thread and all its messages.</summary>
    public void DeleteOwnThread(string threadId)
    {
        Profile.OwnThreads.RemoveAll(t => t.Id == threadId);
        activeDb?.DeleteOwnThread(threadId);
        NotifyChanged();
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
        foreach (var conv in Profile.Conversations)
        {
            var line = conv.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is not null) { line.Status = status; break; }
        }
        activeDb?.UpdateLineStatus(lineId, status);
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
        var h = Norm(handle);
        var contact = FindContact(h);
        if (contact is null)
        {
            contact = new Domain.Contact { Handle = h, Allowed = false };
            Profile.Contacts.Add(contact);
        }
        contact.TokensSpent += total;
        Save();
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
            db.EnsureConversation(conv.Handle);
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

    /// <summary>Finds a conversation by its (already-known) key, or null.</summary>
    public Conversation? FindConversation(string handle)
    {
        var h = Norm(handle);
        return Profile.Conversations.FirstOrDefault(c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
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
        if (conv is null)
        {
            conv = new Conversation { Handle = key, ServiceId = serviceId, ServiceName = name, ProviderHandle = provider };
            Profile.Conversations.Add(conv);
            activeDb?.SetConversationService(key, serviceId, name, provider);
        }
        else if (!string.IsNullOrWhiteSpace(serviceName) && conv.ServiceName != name)
        {
            conv.ServiceId = serviceId;
            conv.ServiceName = name;
            conv.ProviderHandle = provider;
            activeDb?.SetConversationService(key, serviceId, name, provider);
        }
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
        }
        return conv;
    }

    /// <summary>Clears all message history for a conversation but keeps it in the list.</summary>
    public void ClearConversation(string handle)
    {
        var h = Norm(handle);
        var conv = Profile.Conversations.FirstOrDefault(c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
        if (conv is null) return;
        conv.Lines.Clear();
        activeDb?.ClearConversation(h);
        NotifyChanged();
    }

    /// <summary>Deletes a conversation and its history entirely (the contact itself is kept).</summary>
    public void DeleteConversation(string handle)
    {
        var h = Norm(handle);
        Profile.Conversations.RemoveAll(c => c.Handle.Equals(h, StringComparison.OrdinalIgnoreCase));
        unread.Remove(h);
        if (Profile.UnreadFrom.Remove(h)) activeDb?.SaveProfile(Profile);
        activeDb?.DeleteConversation(h);
        NotifyChanged();
    }

    public static string Norm(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Friendly display name for a handle (service name for a service thread, contact's name, else the handle).</summary>
    public string DisplayNameFor(string handle)
    {
        var conv = FindConversation(handle);
        if (conv?.IsService == true) return string.IsNullOrWhiteSpace(conv.ServiceName) ? Norm(handle) : conv.ServiceName!;
        var c = FindContact(handle);
        if (c is not null && !string.IsNullOrWhiteSpace(c.DisplayName)) return c.DisplayName!;
        return Norm(handle);
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
