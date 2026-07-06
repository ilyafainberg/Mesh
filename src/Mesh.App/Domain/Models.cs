using System.Text.Json.Serialization;

namespace Mesh.App.Domain;

public enum ModelProvider { Anthropic, OpenAI, Gemini, FoundryLocal, Grok, Groq, MeshHosted, AzureOpenAI }

/// <summary>Where a knowledge item's content came from.</summary>
public enum KnowledgeSource { Manual, File }

/// <summary>A live external source connected to the agent, exposing tools (not bulk data).</summary>
public enum SourceProvider { MicrosoftGraph, Google, MicrosoftPersonal, Dropbox, Notion, Slack }

/// <summary>
/// A connected account that gives the agent on-demand tools (e.g. search email/Teams).
/// Nothing is copied locally; tools are called live with the user's token when needed.
/// </summary>
public sealed class ConnectedSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public SourceProvider Provider { get; set; }
    public string? ConnectedAs { get; set; }
    /// <summary>Stable account identity for token acquisition (MSAL home account id, or Gmail address).</summary>
    public string? AccountId { get; set; }
    /// <summary>Which tiers may use these tools: "private" | "public" | "shared:&lt;circle&gt;".</summary>
    public string Visibility { get; set; } = "private";
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Optional per-folder grants for email. When any exist, email search for a guest
    /// is restricted to the folders visible to their circle (unless the whole source
    /// is already visible to them). Folders are Outlook mail folders or Gmail labels.
    /// </summary>
    public List<FolderGrant> Folders { get; set; } = new();

    /// <summary>
    /// Optional per-folder grants for cloud files (OneDrive / Google Drive). Same model
    /// as <see cref="Folders"/> but for drive folders: a guest whose circle a path is
    /// shared with can search inside just that folder even when the whole drive is private.
    /// </summary>
    public List<FolderGrant> DrivePaths { get; set; } = new();
}

/// <summary>A specific mail folder / label exposed to a visibility tier.</summary>
public sealed class FolderGrant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"private" | "public" | "shared:&lt;circle&gt;"</summary>
    public string Visibility { get; set; } = "private";
}

/// <summary>How the agent handles inbound requests from allowed contacts.</summary>
public enum ApprovalMode { Off, All, PerCircle }

/// <summary>
/// A powerful local-machine capability the owner's agent can be granted (run scripts, control the
/// browser or desktop, work with files). These are OFF by default and, when enabled, are owner-only
/// unless the owner explicitly shares them with a circle (same visibility model as knowledge/skills).
/// </summary>
public enum LocalToolKind
{
    PowerShell,
    Cmd,
    Python,
    CSharpScript,
    Browser,
    FileSystem,
    WorkIq
}

/// <summary>Per-tool grant: whether the local tool is enabled and who (beyond the owner) may use it.</summary>
public sealed class LocalToolSetting
{
    public bool Enabled { get; set; }
    /// <summary>"private" (owner only) | "public" | "shared:&lt;circle&gt;".</summary>
    public string Visibility { get; set; } = "private";
}

/// <summary>
/// A user-added MCP tool server: a local command Mesh launches over stdio to expose its tools to the
/// agent. Same off-by-default, owner-first, optionally-circle-shared model as the bundled servers.
/// </summary>
public sealed class CustomMcpServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    /// <summary>The executable or command to launch (e.g. a full path to an .exe, "npx", "python").</summary>
    public string Command { get; set; } = "";
    /// <summary>Command arguments, one per entry (e.g. ["-y", "@modelcontextprotocol/server-filesystem"]).</summary>
    public List<string> Arguments { get; set; } = new();
    public bool Enabled { get; set; }
    /// <summary>"private" | "public" | "shared:&lt;circle&gt;".</summary>
    public string Visibility { get; set; } = "private";
}

public sealed class ModelConfig
{
    public ModelProvider Provider { get; set; } = ModelProvider.Anthropic;
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
    /// <summary>Optional base URL override for OpenAI-compatible endpoints, or the Azure OpenAI resource URL.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Azure OpenAI REST api-version (Azure only). Falls back to a sane default when unset.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Configured when there is a usable key, an on-device provider, a custom endpoint, or
    /// the relay-hosted free model (which needs no key of the user's own).
    /// </summary>
    public bool IsConfigured =>
        Provider == ModelProvider.MeshHosted
        || Provider == ModelProvider.FoundryLocal
        || !string.IsNullOrWhiteSpace(ApiKey)
        || !string.IsNullOrWhiteSpace(Endpoint);
}

public sealed class KnowledgeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>"private" | "public" | "shared:&lt;circle&gt;"</summary>
    public string Visibility { get; set; } = "private";
    public KnowledgeSource Source { get; set; } = KnowledgeSource.Manual;
    /// <summary>File path or connector reference the content was grounded in.</summary>
    public string? SourceRef { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A capability the agent can offer, exposed by visibility like knowledge.</summary>
public sealed class Skill
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Guidance the agent follows when performing this skill.</summary>
    public string Instructions { get; set; } = "";
    public string Visibility { get; set; } = "private";
    public bool Enabled { get; set; } = true;
}

/// <summary>A saved interactive mini-app (self-contained HTML) the user can reuse and share.</summary>
public sealed class Widget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    /// <summary>The original description the user asked for (for regeneration/context).</summary>
    public string Prompt { get; set; } = "";
    /// <summary>Self-contained HTML document.</summary>
    public string Html { get; set; } = "";
    /// <summary>"private" | "public" | "shared:&lt;circle&gt;", who your agent may send it to.</summary>
    public string Visibility { get; set; } = "private";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Circle
{
    public string Name { get; set; } = "";
    /// <summary>When true, the agent drafts but waits for owner approval before replying to this circle.</summary>
    public bool RequireApproval { get; set; }
}

public sealed class Contact
{
    public string Handle { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> Circles { get; set; } = new();
    public bool Allowed { get; set; }
    /// <summary>
    /// Signing public keys pinned for this contact on first contact (trust on first use).
    /// Inbound messages are verified against these to defend against a malicious relay.
    /// </summary>
    public List<string> SigningKeys { get; set; } = new();

    /// <summary>
    /// Set when an inbound message from this contact fails verification against the pinned
    /// <see cref="SigningKeys"/>, i.e. the contact's identity keys appear to have changed. Surfaced
    /// in the UI so the user can re-verify out of band before trusting the new keys, rather than the
    /// change being silently dropped. Cleared when the user re-verifies (re-pins) the contact.
    /// </summary>
    public bool KeyChanged { get; set; }

    /// <summary>
    /// Cumulative tokens this contact's requests have cost the owner's model (guest replies your
    /// agent generated for them). Shown per contact so the owner can see who is spending their
    /// tokens. Not reset on model change, this is a lifetime cost-per-contact tally.
    /// </summary>
    public long TokensSpent { get; set; }

    /// <summary>When true, no OS notification fires for this contact's messages (in-app badge still updates).</summary>
    public bool Muted { get; set; }

    /// <summary>True when this contact is blocked: their messages are dropped and their agent gets nothing.</summary>
    public bool Blocked { get; set; }
}

public sealed class ChatLine
{
    /// <summary>Stable id so delivery receipts can update the right outgoing line.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Role { get; set; } = "user"; // user | assistant | system
    public string Text { get; set; } = "";
    /// <summary>
    /// Who this line was addressed to / came from: "agent" (routed through an agent)
    /// or "person" (a direct human-to-human message). Used to tag bubbles so the owner
    /// can tell an agent exchange apart from a direct message.
    /// </summary>
    public string Via { get; set; } = "agent";
    /// <summary>Delivery status for an outgoing line: "" | "sent" | "delivered" | "failed".</summary>
    public string Status { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Conversation
{
    public string Handle { get; set; } = "";
    public List<ChatLine> Lines { get; set; } = new();
}

/// <summary>
/// Cumulative token usage for the currently selected model. Tracks the model the counts belong
/// to so the counter can auto-reset when the model changes.
/// </summary>
public sealed class TokenUsage
{
    /// <summary>Provider+model the counts apply to (e.g. "Groq/llama-3.3-70b-versatile"); reset trigger.</summary>
    public string ModelKey { get; set; } = "";
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>An inbound request from a handle that is not yet allowed.</summary>
public sealed class PendingRequest
{
    public string From { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A drafted reply to an allowed contact, awaiting human approval before sending.</summary>
public sealed class PendingApproval
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string From { get; set; } = "";
    public string RequestBody { get; set; } = "";
    public string DraftReply { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The entire persisted profile for this device/account.</summary>
public sealed class MeshProfile
{
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PrivateKey { get; set; } = ""; // base64 PKCS#8 (device signing key, never exported)
    public string PublicKey { get; set; } = "";  // base64 SubjectPublicKeyInfo (device signing key)

    /// <summary>
    /// Handle recovery keypair (ECDSA P-256). Generated once at onboarding. The PUBLIC half is
    /// registered with the relay; the PRIVATE half is the only key that travels inside an
    /// encrypted export, so a brand-new device can re-authorize itself under the same handle when
    /// no existing device is available to link. Device signing keys are NEVER exported.
    /// </summary>
    public string RecoveryPrivateKey { get; set; } = ""; // base64 PKCS#8
    public string RecoveryPublicKey { get; set; } = "";  // base64 SubjectPublicKeyInfo

    public string RelayUrl { get; set; } = "https://meshrelay.net";
    public ModelConfig Model { get; set; } = new();

    /// <summary>Optional Google OAuth client id (Desktop app) for connecting Gmail.</summary>
    public string GoogleClientId { get; set; } = "";

    /// <summary>Google OAuth client secret (Desktop-app clients require it at token exchange, even with PKCE).</summary>
    public string GoogleClientSecret { get; set; } = "";

    /// <summary>Persisted Google refresh tokens by account email (so Gmail survives app restarts).</summary>
    public Dictionary<string, string> GoogleRefreshTokens { get; set; } = new();

    /// <summary>OAuth app client ids for tier-2 connectors (Dropbox/Notion/Slack), keyed by provider name.</summary>
    public Dictionary<string, string> ConnectorClientIds { get; set; } = new();
    /// <summary>OAuth app client secrets for tier-2 connectors, keyed by provider name.</summary>
    public Dictionary<string, string> ConnectorClientSecrets { get; set; } = new();
    /// <summary>Persisted access/refresh tokens for tier-2 connectors, keyed "provider:account".</summary>
    public Dictionary<string, string> ConnectorTokens { get; set; } = new();

    /// <summary>
    /// Cached copy of the relay's public connector catalog (GET /connectors) as JSON. Non-sensitive
    /// public metadata (authorize/token URLs + public client ids). Persisted so a user who has been
    /// online before can still see and start connector sign-ins while briefly offline.
    /// </summary>
    public string? ConnectorCatalogCache { get; set; }

    public List<KnowledgeItem> Knowledge { get; set; } = new();
    public List<Skill> Skills { get; set; } = new();
    public List<Widget> Widgets { get; set; } = new();
    public List<ConnectedSource> Sources { get; set; } = new();
    public List<Contact> Contacts { get; set; } = new();
    public List<Circle> Circles { get; set; } = new()
    {
        new Circle { Name = "Trusted" },
        new Circle { Name = "Work" },
        new Circle { Name = "Friends" }
    };
    public List<Conversation> Conversations { get; set; } = new();
    public List<PendingRequest> Requests { get; set; } = new();
    public List<PendingApproval> Approvals { get; set; } = new();

    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.PerCircle;

    /// <summary>
    /// Cost control: the maximum number of automatic agent replies (each of which calls the
    /// paid model) allowed per calendar day across all contacts. Guards against an allowed
    /// peer draining the user's model credits. Zero means unlimited.
    /// </summary>
    public int AgentDailyReplyBudget { get; set; } = 100;
    /// <summary>Automatic agent replies used so far on <see cref="AgentBudgetDate"/>.</summary>
    public int AgentRepliesUsedToday { get; set; }
    /// <summary>The calendar day (yyyy-MM-dd, UTC) the used-counter applies to.</summary>
    public string AgentBudgetDate { get; set; } = "";

    /// <summary>Model id used for the relay-hosted free model (first-launch, no key required).</summary>
    public string HostedModelName { get; set; } = "free model";

    /// <summary>
    /// Running token usage for the CURRENTLY selected model, shown as a live counter in the UI.
    /// Reset to zero whenever the user switches models (provider or model id changes), because a
    /// counter is only meaningful per model. Persisted so it survives restarts.
    /// </summary>
    public TokenUsage Tokens { get; set; } = new();

    /// <summary>Allow interactive HTML mini-apps from agents to be run in message bubbles.</summary>
    public bool AllowInteractiveApps { get; set; } = true;

    /// <summary>Global do-not-disturb: suppress all OS notifications (in-app badges still update).</summary>
    public bool DoNotDisturb { get; set; }

    /// <summary>Play a sound with OS notifications.</summary>
    public bool NotificationSound { get; set; } = true;

    /// <summary>
    /// When true, this device (a desktop) will answer remote requests from the owner's OTHER devices
    /// (e.g. a phone) using its full local toolset, so the owner can "talk to my home agent" on the go.
    /// Off by default; only the owner's own linked devices can ever reach it.
    /// </summary>
    public bool ActAsRemoteAgent { get; set; }

    /// <summary>Handles this device has an unread inbound person-message from (persisted read-state).</summary>
    public List<string> UnreadFrom { get; set; } = new();

    /// <summary>
    /// Local-machine tool grants (run scripts, control browser, file access), keyed by tool.
    /// All OFF by default. Enabled tools are always available to the owner in their private chat, and
    /// only reach a guest agent when the owner has shared that tool with one of the guest's circles.
    /// </summary>
    public Dictionary<LocalToolKind, LocalToolSetting> LocalTools { get; set; } = new();

    /// <summary>
    /// Grants for bundled MCP tool servers (e.g. TotalControl desktop control), keyed by server id.
    /// Same off-by-default, owner-first, optionally-circle-shared model as <see cref="LocalTools"/>.
    /// Each server can expose several tools; the grant governs the whole server.
    /// </summary>
    public Dictionary<string, LocalToolSetting> McpServers { get; set; } = new();

    /// <summary>User-added MCP tool servers (local command launched over stdio). Off by default.</summary>
    public List<CustomMcpServer> CustomMcpServers { get; set; } = new();

    /// <summary>The agent's own private chat (owner context).</summary>
    public List<ChatLine> OwnChat { get; set; } = new();

    [JsonIgnore] public bool IsOnboarded => !string.IsNullOrWhiteSpace(Handle) && !string.IsNullOrWhiteSpace(PrivateKey);
}
