using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>A saved identity on this device (one Mesh handle + its own profile file).</summary>
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
/// Supports multiple identities on one device: each account has its own encrypted
/// profile file (<c>profile-{id}.json</c>) and an index (<c>accounts.json</c>) tracks
/// them plus the active one. Signing out just clears the active pointer, profiles
/// are kept so the user can switch back.
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

    private readonly string dir;
    private readonly string indexPath;
    private readonly string legacyPath;
    private string? activeId;
    private List<AccountRef> accounts = new();

    public MeshProfile Profile { get; private set; } = new();

    public event Action? Changed;

    public AppState()
    {
        // Allow an override directory so multiple instances (e.g. for testing two
        // users on one machine) can run with isolated profiles.
        var d = Environment.GetEnvironmentVariable("MESH_PROFILE_DIR");
        if (string.IsNullOrWhiteSpace(d))
            d = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
        Directory.CreateDirectory(d);
        dir = d;
        indexPath = Path.Combine(dir, "accounts.json");
        legacyPath = Path.Combine(dir, "mesh-profile.json");
        Load();
    }

    public bool IsOnboarded => activeId is not null && Profile.IsOnboarded;

    /// <summary>All identities saved on this device.</summary>
    public IReadOnlyList<AccountRef> Accounts => accounts;
    public string? ActiveAccountId => activeId;
    public bool HasSavedAccounts => accounts.Count > 0;

    private string ProfilePath(string id) => Path.Combine(dir, $"profile-{id}.json");

    public void Load()
    {
        try
        {
            if (File.Exists(indexPath))
            {
                var idx = JsonSerializer.Deserialize<AccountIndex>(File.ReadAllText(indexPath), JsonOpts) ?? new AccountIndex();
                accounts = idx.Accounts ?? new();
                activeId = idx.ActiveId;
                if (activeId is not null)
                {
                    var loaded = LoadProfileFile(activeId);
                    if (loaded is not null) { Profile = loaded; return; }
                    activeId = null; // active profile file missing → land on the picker
                }
                Profile = new MeshProfile();
                return;
            }

            // First run after the single-profile version: migrate the legacy file into an account.
            if (File.Exists(legacyPath))
            {
                var stored = File.ReadAllText(legacyPath);
                var json = ProfileProtector.Unprotect(stored);
                Profile = JsonSerializer.Deserialize<MeshProfile>(json, JsonOpts) ?? new MeshProfile();
                if (Profile.IsOnboarded)
                {
                    activeId = NewId();
                    accounts = new() { new AccountRef { Id = activeId, Handle = Profile.Handle, DisplayName = Profile.DisplayName } };
                    WriteProfileFile(activeId, Profile);
                    WriteIndex();
                    try { File.Move(legacyPath, legacyPath + ".migrated", overwrite: true); } catch { }
                    return;
                }
            }

            // Fresh install: no accounts yet.
            Profile = new MeshProfile();
        }
        catch { Profile = new MeshProfile(); activeId = null; }
    }

    private MeshProfile? LoadProfileFile(string id)
    {
        try
        {
            var p = ProfilePath(id);
            if (!File.Exists(p)) return null;
            var json = ProfileProtector.Unprotect(File.ReadAllText(p));
            return JsonSerializer.Deserialize<MeshProfile>(json, JsonOpts);
        }
        catch { return null; }
    }

    private void WriteProfileFile(string id, MeshProfile profile)
    {
        try
        {
            var json = JsonSerializer.Serialize(profile, JsonOpts);
            File.WriteAllText(ProfilePath(id), ProfileProtector.Protect(json));
        }
        catch { /* best-effort on prototype */ }
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
            activeId = NewId();
            accounts.Add(new AccountRef { Id = activeId, Handle = Profile.Handle, DisplayName = Profile.DisplayName });
        }

        if (activeId is not null)
        {
            var acc = accounts.FirstOrDefault(a => a.Id == activeId);
            if (acc is null) { acc = new AccountRef { Id = activeId }; accounts.Add(acc); }
            acc.Handle = Profile.Handle;
            acc.DisplayName = Profile.DisplayName;
            WriteProfileFile(activeId, Profile);
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

    // ---- multi-account -----------------------------------------------------

    /// <summary>
    /// Sign out of the active identity WITHOUT deleting it. The profile stays on
    /// disk so it can be switched back to; the app returns to onboarding / the
    /// account picker where the user can add or link another identity.
    /// </summary>
    public void SignOut()
    {
        if (activeId is not null) WriteProfileFile(activeId, Profile);
        activeId = null;
        Profile = new MeshProfile();
        WriteIndex();
        NotifyChanged();
    }

    /// <summary>Switch the active identity to a previously saved account.</summary>
    public bool SwitchAccount(string id)
    {
        if (id == activeId) return true;
        var loaded = LoadProfileFile(id);
        if (loaded is null) return false;
        if (activeId is not null) WriteProfileFile(activeId, Profile); // persist the one we're leaving
        activeId = id;
        Profile = loaded;
        WriteIndex();
        NotifyChanged();
        return true;
    }

    /// <summary>Permanently remove a saved identity and its profile file from this device.</summary>
    public void DeleteAccount(string id)
    {
        accounts.RemoveAll(a => a.Id == id);
        try { var p = ProfilePath(id); if (File.Exists(p)) File.Delete(p); } catch { }
        if (id == activeId)
        {
            activeId = null;
            Profile = new MeshProfile();
        }
        WriteIndex();
        NotifyChanged();
    }

    // ---- helpers ----------------------------------------------------------
    public Domain.Contact? FindContact(string handle)
        => Profile.Contacts.FirstOrDefault(c => c.Handle.Equals(Norm(handle), StringComparison.OrdinalIgnoreCase));

    public Conversation GetOrCreateConversation(string handle)
    {
        handle = Norm(handle);
        var conv = Profile.Conversations.FirstOrDefault(c => c.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (conv is null)
        {
            conv = new Conversation { Handle = handle };
            Profile.Conversations.Add(conv);
        }
        return conv;
    }

    public static string Norm(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Friendly display name for a handle (contact's name, else the handle itself).</summary>
    public string DisplayNameFor(string handle)
    {
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
}

