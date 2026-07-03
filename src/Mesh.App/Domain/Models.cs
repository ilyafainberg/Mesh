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
}

public sealed class ChatLine
{
    public string Role { get; set; } = "user"; // user | assistant | system
    public string Text { get; set; } = "";
    /// <summary>
    /// Who this line was addressed to / came from: "agent" (routed through an agent)
    /// or "person" (a direct human-to-human message). Used to tag bubbles so the owner
    /// can tell an agent exchange apart from a direct message.
    /// </summary>
    public string Via { get; set; } = "agent";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Conversation
{
    public string Handle { get; set; } = "";
    public List<ChatLine> Lines { get; set; } = new();
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
    public string PrivateKey { get; set; } = ""; // base64 PKCS#8
    public string PublicKey { get; set; } = "";  // base64 SubjectPublicKeyInfo
    public string RelayUrl { get; set; } = "https://mesh-relay.whiteground-796c60f9.northeurope.azurecontainerapps.io";
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
    public string HostedModelName { get; set; } = "gpt-4o-mini";

    /// <summary>Allow interactive HTML mini-apps from agents to be run in message bubbles.</summary>
    public bool AllowInteractiveApps { get; set; } = true;

    /// <summary>The agent's own private chat (owner context).</summary>
    public List<ChatLine> OwnChat { get; set; } = new();

    [JsonIgnore] public bool IsOnboarded => !string.IsNullOrWhiteSpace(Handle) && !string.IsNullOrWhiteSpace(PrivateKey);
}
