using System.Text.Json;
using System.Text.Json.Serialization;
using Mesh.Shared;

namespace Mesh.App.Domain;

public enum ModelProvider { Anthropic, OpenAI, Gemini, FoundryLocal, Grok, Groq, MeshHosted, AzureOpenAI, OpenRouter, Browser, GitHubCopilot }

/// <summary>Provider-native reasoning intensity. Auto omits the control and uses the model default.</summary>
public enum ReasoningEffort { Auto, Low, Medium, High }

/// <summary>Reasoning effort levels accepted by GitHub Copilot CLI.</summary>
public enum CopilotEffort { Auto, None, Minimal, Low, Medium, High, XHigh, Max }

/// <summary>How aggressively Mesh reduces the ephemeral context sent to a model.</summary>
public enum TokenOptimizationLevel { Disabled, MaxAccuracy, Balanced, MaxSavings }

/// <summary>Who may use an audience-scoped capability through an approved contact.</summary>
public enum CapabilityAudienceMode { Private, SelectedCircles, AllAllowedContacts }

/// <summary>
/// Parsed capability audience. Legacy single-circle values stay readable, while multi-circle values
/// use a distinct encoding that older clients fail closed on instead of granting too much access.
/// </summary>
public sealed class CapabilityAudience
{
    public const string PrivateVisibility = "private";
    public const string AllAllowedContactsVisibility = "public";
    public const string SharedPrefix = "shared:";
    public const string MultiCirclePrefix = "shared-multi:";

    private static readonly CapabilityAudience PrivateAudience =
        new(CapabilityAudienceMode.Private, Array.Empty<string>());
    private static readonly CapabilityAudience AllContactsAudience =
        new(CapabilityAudienceMode.AllAllowedContacts, Array.Empty<string>());

    private CapabilityAudience(CapabilityAudienceMode mode, IReadOnlyList<string> circles)
    {
        Mode = mode;
        Circles = circles;
    }

    public CapabilityAudienceMode Mode { get; }
    public IReadOnlyList<string> Circles { get; }

    public static CapabilityAudience Private => PrivateAudience;
    public static CapabilityAudience AllAllowedContacts => AllContactsAudience;

    public static CapabilityAudience Parse(string? visibility)
    {
        var value = visibility?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Equals(PrivateVisibility, StringComparison.OrdinalIgnoreCase))
            return Private;
        if (value.Equals(AllAllowedContactsVisibility, StringComparison.OrdinalIgnoreCase))
            return AllAllowedContacts;

        if (value.StartsWith(MultiCirclePrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var circles = JsonSerializer.Deserialize<List<string>>(value[MultiCirclePrefix.Length..]);
                return ForCircles(circles);
            }
            catch (JsonException)
            {
                return Private;
            }
        }

        if (value.StartsWith(SharedPrefix, StringComparison.OrdinalIgnoreCase))
            return ForCircles([value[SharedPrefix.Length..]]);

        return Private;
    }

    public static CapabilityAudience ForCircles(IEnumerable<string>? circleNames)
    {
        var normalized = (circleNames ?? Enumerable.Empty<string>())
            .Select(name => name?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0
            ? Private
            : new CapabilityAudience(CapabilityAudienceMode.SelectedCircles, normalized.AsReadOnly());
    }

    public bool IncludesCircle(string? circleName)
        => !string.IsNullOrWhiteSpace(circleName)
           && Circles.Contains(circleName.Trim(), StringComparer.OrdinalIgnoreCase);

    public string ToVisibility()
        => Mode switch
        {
            CapabilityAudienceMode.AllAllowedContacts => AllAllowedContactsVisibility,
            CapabilityAudienceMode.SelectedCircles when Circles.Count == 1 => SharedPrefix + Circles[0],
            CapabilityAudienceMode.SelectedCircles when Circles.Count > 1 =>
                MultiCirclePrefix + JsonSerializer.Serialize(Circles),
            _ => PrivateVisibility
        };
}

/// <summary>Central authorization and lifecycle helpers for capability audiences.</summary>
public static class AudiencePolicy
{
    public static bool CanAccess(string? visibility, IEnumerable<string>? guestCircles)
    {
        var audience = CapabilityAudience.Parse(visibility);
        if (audience.Mode == CapabilityAudienceMode.AllAllowedContacts) return true;
        if (audience.Mode != CapabilityAudienceMode.SelectedCircles) return false;
        var allowed = new HashSet<string>(guestCircles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return audience.Circles.Any(allowed.Contains);
    }

    public static bool ReferencesCircle(string? visibility, string? circleName)
        => CapabilityAudience.Parse(visibility).IncludesCircle(circleName);

    public static string RenameCircle(string? visibility, string oldName, string newName)
    {
        var audience = CapabilityAudience.Parse(visibility);
        if (audience.Mode != CapabilityAudienceMode.SelectedCircles || !audience.IncludesCircle(oldName))
            return audience.ToVisibility();
        return CapabilityAudience.ForCircles(audience.Circles.Select(circle =>
            circle.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : circle)).ToVisibility();
    }

    public static string RemoveCircle(string? visibility, string circleName)
    {
        var audience = CapabilityAudience.Parse(visibility);
        if (audience.Mode != CapabilityAudienceMode.SelectedCircles || !audience.IncludesCircle(circleName))
            return audience.ToVisibility();
        return CapabilityAudience.ForCircles(audience.Circles.Where(circle =>
            !circle.Equals(circleName, StringComparison.OrdinalIgnoreCase))).ToVisibility();
    }

    public static string DisplayLabel(string? visibility)
    {
        var audience = CapabilityAudience.Parse(visibility);
        return audience.Mode switch
        {
            CapabilityAudienceMode.AllAllowedContacts => "All allowed contacts",
            CapabilityAudienceMode.SelectedCircles when audience.Circles.Count == 1 => audience.Circles[0],
            CapabilityAudienceMode.SelectedCircles when audience.Circles.Count == 2 => string.Join(", ", audience.Circles),
            CapabilityAudienceMode.SelectedCircles => $"{audience.Circles.Count} circles",
            _ => "Only me"
        };
    }

    public static string DetailedLabel(string? visibility)
    {
        var audience = CapabilityAudience.Parse(visibility);
        return audience.Mode switch
        {
            CapabilityAudienceMode.AllAllowedContacts => "All allowed contacts",
            CapabilityAudienceMode.SelectedCircles => string.Join(", ", audience.Circles),
            _ => "Only me"
        };
    }

    public static string CssClass(string? visibility)
        => CapabilityAudience.Parse(visibility).Mode switch
        {
            CapabilityAudienceMode.AllAllowedContacts => "pub",
            CapabilityAudienceMode.SelectedCircles => "shared",
            _ => "priv"
        };

    public static string IconClass(string? visibility)
        => CapabilityAudience.Parse(visibility).Mode switch
        {
            CapabilityAudienceMode.AllAllowedContacts => "bi-people-fill",
            CapabilityAudienceMode.SelectedCircles => "bi-diagram-3",
            _ => "bi-lock"
        };
}

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
    /// <summary>Who may use these tools: only the owner, selected circles, or all allowed contacts.</summary>
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
    /// <summary>Selected circles or all allowed contacts that may access this folder.</summary>
    public string Visibility { get; set; } = "private";
}

/// <summary>How the agent handles inbound requests from allowed contacts.</summary>
public enum ApprovalMode { Off, All, PerCircle }

/// <summary>
/// A powerful local-machine capability the owner's agent can be granted (run scripts, control the
/// browser or desktop, work with files). These are OFF by default and, when enabled, are owner-only
/// unless the owner explicitly shares them with selected circles or all allowed contacts.
/// </summary>
public enum LocalToolKind
{
    PowerShell,
    Cmd,
    Python,
    CSharpScript,
    Browser,
    FileSystem,
    WorkIq,
    HeadlessBrowser,
    WebSearch,
    Geolocation,
    MeshData
}

/// <summary>How much permission an enabled tool has to execute without asking first.</summary>
public enum ToolApprovalLevel
{
    AlwaysAsk = 1,
    ReadOnlyAuto = 2,
    AutoApproveAll = 3
}

/// <summary>Per-tool grant: whether the local tool is enabled and who (beyond the owner) may use it.</summary>
public sealed class LocalToolSetting
{
    public bool Enabled { get; set; }
    /// <summary>Who may use the tool beyond the owner.</summary>
    public string Visibility { get; set; } = "private";
    public ToolApprovalLevel ApprovalLevel { get; set; } = ToolApprovalLevel.ReadOnlyAuto;
}

/// <summary>How Mesh connects to a custom MCP server.</summary>
public enum McpTransport
{
    /// <summary>Launch a local command as a child process and talk over stdio (desktop only).</summary>
    Stdio,
    /// <summary>Connect to a remote MCP server over HTTP/SSE (works on mobile and desktop).</summary>
    Http
}

/// <summary>
/// A user-added MCP tool server. Two transports: a local <see cref="McpTransport.Stdio"/> command Mesh
/// launches over stdio (desktop only), or a remote <see cref="McpTransport.Http"/> endpoint reached over
/// the network (works on mobile too). Same off-by-default, owner-first, audience-scoped model
/// as the bundled servers.
/// </summary>
public sealed class CustomMcpServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    /// <summary>Which transport to use. Defaults to stdio for backward compatibility with saved servers.</summary>
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    /// <summary>The executable or command to launch for a stdio server (e.g. a full path to an .exe, "npx", "python").</summary>
    public string Command { get; set; } = "";
    /// <summary>Command arguments, one per entry (e.g. ["-y", "@modelcontextprotocol/server-filesystem"]).</summary>
    public List<string> Arguments { get; set; } = new();
    /// <summary>The remote endpoint URL for an <see cref="McpTransport.Http"/> server (e.g. https://host/mcp).</summary>
    public string Url { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Who may use this server beyond the owner.</summary>
    public string Visibility { get; set; } = "private";
    public ToolApprovalLevel ApprovalLevel { get; set; } = ToolApprovalLevel.ReadOnlyAuto;
}

public sealed class ModelConfig
{
    public ModelProvider Provider { get; set; } = ModelProvider.Anthropic;
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
    /// <summary>Requested reasoning intensity. Auto leaves the choice to the provider.</summary>
    public ReasoningEffort ReasoningEffort { get; set; } = ReasoningEffort.Auto;
    /// <summary>Controls local, provider-neutral optimization of the context copy sent for inference.</summary>
    public TokenOptimizationLevel TokenOptimization { get; set; } = TokenOptimizationLevel.Balanced;
    /// <summary>GitHub Copilot CLI command or absolute path. Desktop only.</summary>
    public string CopilotExecutable { get; set; } = "copilot";
    /// <summary>GitHub Copilot CLI reasoning effort. Auto omits the launch option.</summary>
    public CopilotEffort CopilotEffort { get; set; } = CopilotEffort.Auto;
    /// <summary>Optional base URL override for OpenAI-compatible endpoints, or the Azure OpenAI resource URL.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Azure OpenAI REST api-version (Azure only). Falls back to a sane default when unset.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>Browser engine for the Windows scripted provider: Chrome, Edge, or Firefox.</summary>
    public string BrowserEngine { get; set; } = "Firefox";
    /// <summary>Browser prompt context: CurrentTurn or FullPrompt.</summary>
    public string BrowserContextMode { get; set; } = "FullPrompt";
    /// <summary>Windows browser-provider start page.</summary>
    public string? BrowserUrl { get; set; }
    /// <summary>JavaScript async function invoked with the rendered Mesh request.</summary>
    public string? BrowserExecuteScript { get; set; }
    /// <summary>JavaScript async function polled until it reports completion.</summary>
    public string? BrowserPollScript { get; set; }
    /// <summary>JavaScript async function that extracts the completed response.</summary>
    public string? BrowserResultScript { get; set; }
    public int BrowserPollSeconds { get; set; } = 5;
    public int BrowserTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Configured when there is a usable key, an on-device provider, a custom endpoint, or
    /// the relay-hosted free model (which needs no key of the user's own).
    /// </summary>
    public bool IsConfigured =>
        Provider == ModelProvider.MeshHosted
        || (Provider == ModelProvider.Browser && OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(BrowserUrl))
        || (Provider == ModelProvider.GitHubCopilot && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        || Provider == ModelProvider.FoundryLocal
        || !string.IsNullOrWhiteSpace(ApiKey)
        || !string.IsNullOrWhiteSpace(Endpoint);
}

public sealed class KnowledgeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Who may use this item through the owner's agent.</summary>
    public string Visibility { get; set; } = "private";
    public KnowledgeSource Source { get; set; } = KnowledgeSource.Manual;
    /// <summary>File path or connector reference the content was grounded in.</summary>
    public string? SourceRef { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class MemoryCategories
{
    public const string Preference = "preference";
    public const string PersonalFact = "personal_fact";
    public const string Goal = "goal";
    public const string Workflow = "workflow";
    public const string Constraint = "constraint";

    public static readonly IReadOnlyList<string> All =
    [
        Preference,
        PersonalFact,
        Goal,
        Workflow,
        Constraint
    ];
}

public static class MemoryOrigins
{
    public const string Manual = "manual";
    public const string Explicit = "explicit";
    public const string Inferred = "inferred";
}

/// <summary>
/// Owner-only durable memory used exclusively by Me topics. Memories never have visibility or
/// publishing fields, so they cannot be exposed to Messages, contacts, circles, or Community services.
/// </summary>
public sealed class MemoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = MemoryCategories.Preference;
    public string Origin { get; set; } = MemoryOrigins.Inferred;
    public double Importance { get; set; } = 0.65;
    public double Confidence { get; set; } = 0.8;
    public double Stability { get; set; } = 0.75;
    public int ReinforcementCount { get; set; } = 1;
    public string? SourceThreadId { get; set; }
    public string? SourceLineId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastReinforcedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Local-only retrieval history. It is backed up but not synchronized between devices.</summary>
    public int RecallCount { get; set; }
    public DateTimeOffset? LastRecalledAt { get; set; }
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

    /// <summary>Marketplace this skill was imported from. Null means a local, user-authored skill.</summary>
    public string? SourceMarketplaceId { get; set; }
    /// <summary>The skill's id within its source marketplace (used to match on auto-update).</summary>
    public string? SourceSkillId { get; set; }
    /// <summary>Version string reported by the marketplace, if any.</summary>
    public string? Version { get; set; }
}

/// <summary>A remote catalog of importable skills, addressed by a JSON index URL.</summary>
public sealed class SkillMarketplace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    /// <summary>Display name (from the index, else the URL host).</summary>
    public string Name { get; set; } = "";
    /// <summary>URL of the JSON index.</summary>
    public string Url { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; set; }
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
    /// <summary>Who the owner's agent may send this widget to.</summary>
    public string Visibility { get; set; } = "private";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PreviousHtml { get; set; }
    public string? PreviousPrompt { get; set; }
}

public sealed class Circle
{
    public string Name { get; set; } = "";
    /// <summary>When true, the agent drafts but waits for owner approval before replying to this circle.</summary>
    public bool RequireApproval { get; set; }
}

/// <summary>
/// Reserved system circles. The "Public" circle is not a member list: it is the marker that a
/// capability (KnowledgeItem / Skill / Widget) has been published to the Community directory and may
/// be reached by ANY handle through the sandboxed service path (never the normal contact guest path).
/// </summary>
public static class SystemCircles
{
    /// <summary>Reserved circle name used to flag a capability as public-listed.</summary>
    public const string Public = "__public__";

    /// <summary>The visibility string a published (public-listed) capability carries.</summary>
    public const string PublicVisibility = "shared:__public__";

    /// <summary>True when a capability's visibility marks it public-listed (in the Community directory).</summary>
    public static bool IsPublicListed(string? visibility)
        => CapabilityAudience.Parse(visibility).IncludesCircle(Public);
}

/// <summary>
/// A capability bundle the owner has published to the Community directory as a public service anyone
/// can discover and invoke. The service runs a sandboxed, service-scoped agent over the owner's
/// public-listed capabilities (KB/Skills/Widgets only, never private connectors or local tools).
/// </summary>
public sealed class PublishedService
{
    /// <summary>Stable service id (also its key in the relay directory).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = ServiceCategories.Fallback;
    /// <summary>Persona / system guidance the service-scoped agent follows when answering.</summary>
    public string Persona { get; set; } = "";
    /// <summary>Whether this service is currently listed/live in the relay directory.</summary>
    public bool Published { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Ids of the Knowledge items THIS service exposes. Per-service scoping: a service answers using
    /// only the capabilities explicitly attached to it (not a global public pool). The sandboxed
    /// service agent materializes these ids at invocation; they are never uploaded to the relay.
    /// </summary>
    public List<string> KnowledgeIds { get; set; } = new();

    /// <summary>Ids of the Skills this service exposes (still subject to the skill being enabled).</summary>
    public List<string> SkillIds { get; set; } = new();

    /// <summary>Ids of the Widgets this service exposes.</summary>
    public List<string> WidgetIds { get; set; } = new();

    /// <summary>
    /// Total token budget the owner allots to this service across all callers for its LIFETIME.
    /// 0 means unlimited. Enforced provider-side (the relay never sees the E2E-encrypted token spend),
    /// so this is the owner's own hard ceiling on the AI tokens their public service will ever consume.
    /// Defaults to 1.0 MTokens.
    /// </summary>
    public long TotalTokenBudget { get; set; } = 1_000_000;

    /// <summary>Per-caller (per handle) token budget PER DAY (resets on UTC date rollover). 0 means
    /// unlimited. Defaults to 0.1 MTokens per person per day. Enforced provider-side.</summary>
    public long PerHandleTokenBudget { get; set; } = 100_000;

    /// <summary>Total tokens spent answering this service so far (lifetime).</summary>
    public long SpentTokens { get; set; }

    /// <summary>Tokens spent per requesting handle TODAY, used to enforce the daily per-handle cap.</summary>
    public Dictionary<string, long> SpentByHandleToday { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Max number of REQUESTS one handle may make to this service PER DAY (resets on UTC date rollover),
    /// independent of token cost. This is the anti-abuse rate limit: it stops a caller flooding the
    /// service with many cheap requests even when they stay under the token budget. 0 means unlimited.
    /// Defaults to 100 requests per person per day.
    /// </summary>
    public int PerHandleDailyRequestLimit { get; set; } = 100;

    /// <summary>Number of requests per handle TODAY, used to enforce <see cref="PerHandleDailyRequestLimit"/>.</summary>
    public Dictionary<string, int> RequestsByHandleToday { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UTC date (yyyy-MM-dd) the daily per-handle buckets belong to; a new day resets them.</summary>
    public string SpentTodayUtc { get; set; } = "";

    private static string TodayUtc => DateTime.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>Tokens the given handle has spent TODAY (0 when the stored buckets are from a past day).</summary>
    public long SpentTodayFor(string handle)
        => SpentTodayUtc == TodayUtc && SpentByHandleToday.TryGetValue(handle, out var v) ? v : 0;

    /// <summary>Requests the given handle has made TODAY (0 when the stored buckets are from a past day).</summary>
    public int RequestsTodayFor(string handle)
        => SpentTodayUtc == TodayUtc && RequestsByHandleToday.TryGetValue(handle, out var v) ? v : 0;

    /// <summary>
    /// True when this request must be refused: the service's LIFETIME total budget is exhausted, or the
    /// calling handle has hit its DAILY per-handle token cap. A 0 budget (unlimited) never blocks.
    /// </summary>
    public bool IsBudgetExhausted(string handle)
    {
        if (TotalTokenBudget > 0 && SpentTokens >= TotalTokenBudget) return true;
        if (PerHandleTokenBudget > 0 && SpentTodayFor(handle) >= PerHandleTokenBudget) return true;
        return false;
    }

    /// <summary>True when the calling handle has hit its daily request rate limit. A 0 limit never blocks.</summary>
    public bool IsRateLimited(string handle)
        => PerHandleDailyRequestLimit > 0 && RequestsTodayFor(handle) >= PerHandleDailyRequestLimit;

    /// <summary>Resets the daily per-handle buckets (tokens + requests) when the UTC day has rolled over.</summary>
    private void RollDailyBuckets()
    {
        var today = TodayUtc;
        if (SpentTodayUtc == today) return;
        SpentTodayUtc = today;
        SpentByHandleToday.Clear();
        RequestsByHandleToday.Clear();
    }

    /// <summary>Counts one accepted request from a handle against its daily rate limit.</summary>
    public void RecordRequest(string handle)
    {
        RollDailyBuckets();
        RequestsByHandleToday[handle] = (RequestsByHandleToday.TryGetValue(handle, out var v) ? v : 0) + 1;
    }

    /// <summary>Adds a completed request's token cost to the lifetime total and today's per-handle bucket.</summary>
    public void RecordSpend(string handle, long tokens)
    {
        if (tokens <= 0) return;
        SpentTokens += tokens;
        RollDailyBuckets();
        SpentByHandleToday[handle] = (SpentByHandleToday.TryGetValue(handle, out var v) ? v : 0) + tokens;
    }
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

/// <summary>
/// A file supplied directly to a model turn. Data is deliberately transient and is never written to
/// the encrypted chat database: the original bytes live in memory only for the active run.
/// </summary>
public sealed record ChatAttachment(string Name, string MimeType, byte[] Data)
{
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed class ChatLine
{
    /// <summary>Stable id so delivery receipts can update the right outgoing line.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Role { get; set; } = "user"; // user | assistant | system
    public string Text { get; set; } = "";
    /// <summary>The user line answered by this topic reply. Null for uncorrelated and non-topic lines.</summary>
    public string? ReplyToLineId { get; set; }
    /// <summary>Original instructions for a generated widget in this line, used when saving it.</summary>
    public string? WidgetPrompt { get; set; }
    /// <summary>The actual group-message author. Direct messages may leave this null.</summary>
    public string? SenderHandle { get; set; }
    /// <summary>Transient multimodal inputs for this turn. Never persisted or sent to peers.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ChatAttachment> Attachments { get; set; } = new();
    /// <summary>
    /// Who this line was addressed to / came from: "agent" (routed through an agent)
    /// or "person" (a direct human-to-human message). Used to tag bubbles so the owner
    /// can tell an agent exchange apart from a direct message.
    /// </summary>
    public string Via { get; set; } = "agent";
    /// <summary>
    /// Whether the PEER participant on this line is the peer's agent (true) rather than the
    /// peer person (false). This captures addressee/author for the bubble label independently
    /// of <see cref="Via"/> (the routing channel): an agent-authored auto-reply travels on the
    /// agent channel (Via == "agent") yet answers the human, so it is AddressedToAgent == false
    /// and reads "to them". Via still drives history scoping and the channel icon.
    /// </summary>
    public bool AddressedToAgent { get; set; }
    /// <summary>Delivery status: "" | "sent" | "delivered" | "failed" | "agent_queued".</summary>
    public string Status { get; set; } = "";
    /// <summary>
    /// Optional model reasoning ("thinking") extracted from the reply, shown as a collapsible section
    /// separate from the answer. Populated provider-agnostically (inline think tags or a provider
    /// reasoning field); null when the model returned no reasoning.
    /// </summary>
    public string? Reasoning { get; set; }
    /// <summary>The model id reported by the provider for this assistant reply.</summary>
    public string? ModelId { get; set; }
    /// <summary>
    /// Internal transcript line: part of the agent's own execution record (tool calls with their
    /// arguments and results) that the MODEL sees on later turns and resumes so it never loses the
    /// execution context, but which is NOT shown to the user as a chat bubble (the user sees the live
    /// step trace and the final answer instead). Persisted encrypted with the thread, owner-only, and
    /// never sent to peers.
    /// </summary>
    public bool Internal { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Builds a stable topic transcript where correlated replies follow their prompts.</summary>
public static class TopicTranscriptOrdering
{
    public static IReadOnlyList<ChatLine> OrderForDisplay(IEnumerable<ChatLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var source = lines.ToList();
        if (source.Count < 2) return source;

        var promptIds = source
            .Where(line => string.Equals(line.Role, "user", StringComparison.Ordinal)
                           && !string.IsNullOrWhiteSpace(line.Id))
            .Select(line => line.Id)
            .ToHashSet(StringComparer.Ordinal);
        var repliesByPrompt = source
            .Where(line => string.Equals(line.Role, "assistant", StringComparison.Ordinal)
                           && line.ReplyToLineId is not null
                           && promptIds.Contains(line.ReplyToLineId))
            .GroupBy(line => line.ReplyToLineId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        if (repliesByPrompt.Count == 0) return source;

        var correlatedReplyIds = repliesByPrompt.Values
            .SelectMany(replies => replies)
            .Select(reply => reply.Id)
            .ToHashSet(StringComparer.Ordinal);
        var ordered = new List<ChatLine>(source.Count);
        foreach (var line in source)
        {
            if (correlatedReplyIds.Contains(line.Id)) continue;
            ordered.Add(line);
            if (string.Equals(line.Role, "user", StringComparison.Ordinal)
                && repliesByPrompt.TryGetValue(line.Id, out var replies))
                ordered.AddRange(replies);
        }
        return ordered;
    }
}

/// <summary>State of one agent step surfaced to the UI while a turn runs.</summary>
public enum AgentStepState { Started, Done, Failed }

public enum AgentRunPhase
{
    Planning, Executing, Hyperscaling, Integrating, Verifying, Completed, Failed, Cancelled
}

public sealed record AgentSubtaskState(string Id, string Title, AgentStepState State, string? Result = null);

public sealed record AgentRunState(
    string RunId, string ThreadId, AgentRunPhase Phase, string Plan,
    IReadOnlyList<AgentSubtaskState> Subtasks, DateTimeOffset StartedAt);

/// <summary>
/// A single step the agent takes during a turn (a tool call), surfaced live so the user can see what
/// the agent is doing. <see cref="Label"/> is a friendly description (e.g. "Ran Python").
/// <see cref="Arguments"/> is the raw tool input and <see cref="Result"/> the tool output. Both may be
/// truncated for display. <see cref="ToolName"/> preserves the real tool when <see cref="Tool"/> is an
/// opaque call identifier, allowing the details view to choose the right formatter and language.
/// </summary>
public sealed record AgentStep(string Tool, string Label, AgentStepState State,
    string? Arguments = null, string? Result = null, string? ToolName = null);

/// <summary>Which live stream a chunk belongs to: the model's reasoning, or its answer.</summary>
public enum AgentDeltaKind { Reasoning, Answer }

/// <summary>
/// One streamed fragment of a model turn, surfaced live so the UI can show reasoning and the answer
/// building up separately as they arrive, rather than all at once when the turn finishes. Providers
/// that do not stream simply never report deltas; the buffered final answer is unchanged.
/// </summary>
public sealed record AgentDelta(AgentDeltaKind Kind, string Text);

/// <summary>
/// Coalesces the fine-grained <see cref="AgentDelta"/> fragments a model streams (often one per token)
/// into larger chunks before they are forwarded to a viewing device. Forwarding every token as its own
/// end-to-end-encrypted envelope would hammer the seal/sign path and the relay, so fragments are buffered
/// until the buffer reaches a character budget or a time budget elapses, whichever comes first. A change
/// of stream (reasoning to answer or back) always flushes first so the two are never mixed in one chunk.
/// This type is deliberately pure and single-threaded: a turn reports its deltas sequentially.
/// </summary>
public sealed class AgentDeltaCoalescer
{
    private readonly int flushChars;
    private readonly int flushMillis;
    private readonly System.Text.StringBuilder buffer = new();
    private AgentDeltaKind kind;
    private long? lastFlushMillis;

    public AgentDeltaCoalescer(int flushChars = 48, int flushMillis = 100)
    {
        this.flushChars = flushChars > 0 ? flushChars : 1;
        this.flushMillis = flushMillis >= 0 ? flushMillis : 0;
    }

    /// <summary>
    /// Buffers one streamed fragment and returns a coalesced fragment to forward now, or null to keep
    /// buffering. <paramref name="nowMillis"/> is a monotonic millisecond clock (for example
    /// <see cref="Environment.TickCount64"/>) used only for the time budget, so callers can inject it.
    /// </summary>
    public AgentDelta? Accept(AgentDelta delta, long nowMillis)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (string.IsNullOrEmpty(delta.Text)) return null;
        AgentDelta? flushed = null;
        if (buffer.Length > 0 && delta.Kind != kind)
            flushed = Take(nowMillis);
        if (buffer.Length == 0)
        {
            kind = delta.Kind;
            lastFlushMillis ??= nowMillis;
        }
        buffer.Append(delta.Text);
        if (flushed is null
            && (buffer.Length >= flushChars
                || nowMillis - (lastFlushMillis ?? nowMillis) >= flushMillis))
            return Take(nowMillis);
        return flushed;
    }

    /// <summary>Returns any buffered fragment at the end of a turn, or null when nothing is buffered.</summary>
    public AgentDelta? Flush() => buffer.Length > 0 ? Take(null) : null;

    private AgentDelta Take(long? nowMillis)
    {
        var fragment = new AgentDelta(kind, buffer.ToString());
        buffer.Clear();
        if (nowMillis is { } value) lastFlushMillis = value;
        return fragment;
    }
}

public sealed class Conversation
{
    public string Handle { get; set; } = "";
    public List<ChatLine> Lines { get; set; } = new();
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public bool IsPinned { get; set; }

    /// <summary>
    /// Client-only group metadata. Group threads use <c>grp:{normalizedGroupId}</c> as their
    /// synthetic <see cref="Handle"/> and never expose these values to the relay.
    /// </summary>
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? GroupOwnerHandle { get; set; }
    public List<string> GroupMembers { get; set; } = new();
    public int GroupVersion { get; set; }

    /// <summary>True when this conversation is a client-side group thread.</summary>
    public bool IsGroup => GroupId is not null;

    /// <summary>
    /// Service-thread metadata (null for a normal person DM). When set, this conversation is a thread
    /// with a published service: <see cref="Handle"/> is a synthetic key (svc:{provider}:{serviceId})
    /// so it never collides with a person DM or a sibling service, and <see cref="ProviderHandle"/> is
    /// the real handle a follow-up ServiceRequest routes to.
    /// </summary>
    public string? ServiceId { get; set; }
    public string? ServiceName { get; set; }
    public string? ProviderHandle { get; set; }

    /// <summary>True when this conversation is a thread with a published service.</summary>
    public bool IsService => ServiceId is not null;
}

/// <summary>
/// One topic thread in the owner's private "Me" chat. The owner can keep several parallel
/// conversations with their own agent (like separate chats in Messages). Lines are row-stored
/// (see MeshDb.own_chat.thread_id); this metadata is hydrated on load.
/// </summary>
public sealed class OwnThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "New chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastActivityAt { get; set; }
    public bool IsPinned { get; set; }
    public string? ExecutionDeviceId { get; set; }
    public string? ExecutionDeviceName { get; set; }
    public string? ExecutionDevicePlatform { get; set; }
    public DateTimeOffset? ExecutionAt { get; set; }
    public string? ExecutionRunId { get; set; }
    public List<ChatLine> Lines { get; set; } = new();
}

/// <summary>Immutable snapshot of the device that owns a topic execution run.</summary>
public sealed record ExecutionDevice(string DeviceId, string? DeviceName, string Platform);

/// <summary>
/// A live projection of a remote execution run on an OwnThread, separate from the chat history.
/// Applied when a topic.run.update arrives and cleared when the run reaches a terminal phase.
/// </summary>
public sealed class RemoteRunProjection
{
    public string RunId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public Mesh.Shared.TopicRunPhase Phase { get; set; }
    public string? Status { get; set; }
    public string? Plan { get; set; }
    public IReadOnlyList<Mesh.Shared.TopicRunSubtask>? Subtasks { get; set; }
    public IReadOnlyList<Mesh.Shared.TopicRunStep>? Steps { get; set; }
    public int Queued { get; set; }
    public string? Error { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public static class ActivityTimestamp
{
    public static DateTimeOffset Advance(DateTimeOffset? current, DateTimeOffset candidate)
    {
        if (candidate == default)
            throw new ArgumentException("An activity timestamp is required.", nameof(candidate));
        return current.HasValue && candidate <= current.Value ? current.Value : candidate;
    }
}

public static class RemoteRunCorrelation
{
    public static bool IsExpected(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] OwnThread? thread,
        string threadId,
        string runId)
        => thread is not null
           && string.Equals(thread.Id, threadId, StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(runId)
           && string.Equals(thread.ExecutionRunId, runId, StringComparison.Ordinal);
}

/// <summary>
/// Decides whether a topic bound to another device, carrying a persisted <see cref="OwnThread.ExecutionRunId"/>
/// but with no live run projection on this device, should still count as an active (busy) remote run.
/// A persisted run id is normally cleared by a terminal topic.run.update from the executing device. If
/// that signal is delayed, unavailable on a legacy relay, or expires before delivery, the id can linger
/// indefinitely and the topic would otherwise show a permanent "thinking" bubble and a dead Stop button.
/// We therefore honor the persisted-only binding only while the run is still "fresh": a genuinely live
/// remote run keeps emitting updates that advance the timestamp, while a dead or finished run goes stale
/// and the topic falls back to idle.
/// </summary>
public static class RemoteRunActivity
{
    /// <summary>
    /// Freshness budget for a persisted-only remote run. Deliberately generous: far longer than the gap
    /// between a live run's updates, but finite so a stranded run id cannot pin the busy indicator across
    /// sessions (a phantom from a prior run carries an old timestamp and reads as idle immediately).
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when the topic carries a persisted run id whose last sign of life is within
    /// <see cref="StaleAfter"/> of <paramref name="now"/>.
    /// </summary>
    public static bool IsPersistedRunFresh(OwnThread thread, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (string.IsNullOrWhiteSpace(thread.ExecutionRunId)) return false;
        var last = thread.LastActivityAt ?? thread.ExecutionAt;
        return last.HasValue && now - last.Value < StaleAfter;
    }

    /// <summary>
    /// True when a LIVE remote-run projection's last update is within <see cref="StaleAfter"/> of
    /// <paramref name="now"/>. Mirrors <see cref="IsPersistedRunFresh"/> for the in-memory projection:
    /// a genuinely live run keeps advancing <see cref="RemoteRunProjection.Timestamp"/>, so a projection
    /// whose terminal update is delayed or unavailable self-heals to idle once it goes stale instead of
    /// pinning the "thinking" bubble forever.
    /// </summary>
    public static bool IsProjectionFresh(RemoteRunProjection projection, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return projection.Timestamp != default && now - projection.Timestamp < StaleAfter;
    }

    public static bool UsesAuthoritativeState(
        bool relaySupportsAuthoritativeState,
        string? executionDeviceId,
        string currentDeviceId,
        IEnumerable<Mesh.Shared.DeviceInfo> devices)
    {
        if (!relaySupportsAuthoritativeState) return false;
        if (string.IsNullOrWhiteSpace(executionDeviceId)
            || string.Equals(executionDeviceId, currentDeviceId, StringComparison.Ordinal))
            return true;
        return devices.Any(device =>
            string.Equals(device.DeviceId, executionDeviceId, StringComparison.Ordinal)
            && device.ProtocolVersion == MeshProtocol.Version);
    }

    public static bool IsProjectionActive(
        RemoteRunProjection projection,
        DateTimeOffset now,
        bool authoritativeState)
        => authoritativeState
            ? projection.Timestamp != default
            : IsProjectionFresh(projection, now);

    public static bool IsPersistedRunActive(
        OwnThread thread,
        DateTimeOffset now,
        bool authoritativeState)
        => authoritativeState
            ? !string.IsNullOrWhiteSpace(thread.ExecutionRunId)
            : IsPersistedRunFresh(thread, now);

    /// <summary>True while a topic is actively running on this device or a remote execution device.</summary>
    public static bool IsBusy(
        OwnThread thread,
        bool localTurnActive,
        RemoteRunProjection? projection,
        bool remoteBound,
        DateTimeOffset now,
        bool authoritativeState = false)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return localTurnActive
            || projection is not null && IsProjectionActive(projection, now, authoritativeState)
            || remoteBound && IsPersistedRunActive(thread, now, authoritativeState);
    }
}

/// <summary>
/// Resolves the run state shown in the UI when local execution progress is also echoed through the
/// remote-run channel. The local state is richer and must win so the same tools are not rendered twice.
/// </summary>
public static class RemoteRunPresentation
{
    /// <summary>Returns the remote projection only when this device is not executing the turn locally.</summary>
    public static RemoteRunProjection? VisibleProjection(
        RemoteRunProjection? projection,
        bool localTurnActive)
        => localTurnActive ? null : projection;

    /// <summary>
    /// Returns a stable run status. Tool progress reports carry the current tool label as their status,
    /// but that label is already present in the step list and should not be repeated as a heading.
    /// </summary>
    public static string StatusLabel(RemoteRunProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var status = projection.Status?.Trim();
        var repeatsStep = !string.IsNullOrEmpty(status)
            && projection.Steps?.Any(step => string.Equals(
                step.Label?.Trim(), status, StringComparison.OrdinalIgnoreCase)) == true;
        return !string.IsNullOrEmpty(status) && !repeatsStep
            ? status
            : projection.Phase.ToString().ToLowerInvariant();
    }
}

/// <summary>Chooses the single action shown in a topic composer while a turn is running.</summary>
public static class TopicComposerPresentation
{
    public static bool ShowStop(bool topicBusy, bool hasSendableDraft)
        => topicBusy && !hasSendableDraft;
}

/// <summary>
/// Tracks queued topic runs by stable run and line identifiers. A run remains known after it starts so
/// later progress is still accepted, while only runs that are still waiting drive the line subtitle.
/// </summary>
public enum TopicQueueStage { Sending, Relay, Device, Cancelling, Expired, Failed }

public sealed record QueuedTopicRunInfo(
    string ThreadId,
    string RunId,
    string LineId,
    TopicQueueStage Stage,
    bool Waiting);

public sealed class QueuedTopicRunState
{
    private sealed record Entry(string LineId, bool Waiting, TopicQueueStage Stage);

    private readonly Dictionary<string, Dictionary<string, Entry>> runsByThread =
        new(StringComparer.Ordinal);

    public bool MarkWaiting(
        string threadId,
        string runId,
        string lineId,
        TopicQueueStage stage = TopicQueueStage.Sending)
    {
        ValidateId(threadId, nameof(threadId));
        ValidateId(runId, nameof(runId));
        ValidateId(lineId, nameof(lineId));
        if (!runsByThread.TryGetValue(threadId, out var runs))
            runsByThread[threadId] = runs = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (runs.TryGetValue(runId, out var current))
        {
            if (!string.Equals(current.LineId, lineId, StringComparison.Ordinal))
                throw new InvalidOperationException("A queued run cannot change its trigger line.");
            if (current.Waiting && current.Stage == stage) return false;
            runs[runId] = current with { Waiting = true, Stage = stage };
            return true;
        }
        runs[runId] = new Entry(lineId, Waiting: true, stage);
        return true;
    }

    public bool SetStage(string threadId, string runId, TopicQueueStage stage)
    {
        if (!runsByThread.TryGetValue(threadId, out var runs)
            || !runs.TryGetValue(runId, out var current)
            || current.Stage == stage)
            return false;
        runs[runId] = current with { Stage = stage };
        return true;
    }

    public bool MarkStarted(string threadId, string runId)
    {
        if (!runsByThread.TryGetValue(threadId, out var runs)
            || !runs.TryGetValue(runId, out var current)
            || !current.Waiting)
            return false;
        runs[runId] = current with { Waiting = false };
        return true;
    }

    public bool Complete(string threadId, string runId)
    {
        if (!runsByThread.TryGetValue(threadId, out var runs) || !runs.Remove(runId))
            return false;
        if (runs.Count == 0) runsByThread.Remove(threadId);
        return true;
    }

    public bool IsKnownRun(string threadId, string runId)
        => runsByThread.TryGetValue(threadId, out var runs) && runs.ContainsKey(runId);

    public QueuedTopicRunInfo? FindByLine(string threadId, string lineId)
    {
        if (!runsByThread.TryGetValue(threadId, out var runs)) return null;
        foreach (var (runId, entry) in runs)
            if (string.Equals(entry.LineId, lineId, StringComparison.Ordinal))
                return new QueuedTopicRunInfo(
                    threadId, runId, entry.LineId, entry.Stage, entry.Waiting);
        return null;
    }

    public QueuedTopicRunInfo? FindByLine(string lineId)
    {
        foreach (var threadId in runsByThread.Keys)
        {
            var match = FindByLine(threadId, lineId);
            if (match is not null) return match;
        }
        return null;
    }

    public bool IsLineWaiting(string lineId) => FindByLine(lineId)?.Waiting == true;

    public int WaitingCount(string threadId)
        => runsByThread.TryGetValue(threadId, out var runs)
            ? runs.Values.Count(entry => entry.Waiting)
            : 0;

    public bool ClearThread(string threadId) => runsByThread.Remove(threadId);

    public void Clear() => runsByThread.Clear();

    private static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable identifier is required.", parameterName);
    }
}

/// <summary>
/// Decides whether a committed assistant answer that just synced in from the executing device should
/// finalize a lingering LIVE remote-run projection on a viewing device. Terminal updates and committed
/// lines travel independently, so the durable answer can arrive first. The durable answer is the
/// terminal truth.
/// </summary>
public static class RemoteRunReconciliation
{
    /// <summary>
    /// True when <paramref name="projection"/> exists and <paramref name="answerAt"/> is not older than
    /// the projection's last update, so the answer finalizes it. A genuinely newer run carries a
    /// projection timestamp ahead of this answer and is deliberately left running.
    /// </summary>
    public static bool ShouldFinalizeOnAnswer(RemoteRunProjection? projection, DateTimeOffset answerAt)
        => projection is not null
           && answerAt != default
           && answerAt >= projection.Timestamp;

    /// <summary>
    /// Resolves the run completed by a committed assistant answer. An exact reply-to match against a
    /// durable queued run is authoritative even when the live projection was missed or lost on restart.
    /// Legacy uncorrelated answers retain the timestamp-based projection fallback.
    /// </summary>
    public static string? RunIdForAnswer(
        string threadId,
        string? replyToLineId,
        QueuedTopicRunInfo? queuedRun,
        RemoteRunProjection? projection,
        DateTimeOffset answerAt)
    {
        if (TopicRunProtocol.IsValidIdentifier(replyToLineId)
            && queuedRun is not null
            && string.Equals(queuedRun.ThreadId, threadId, StringComparison.Ordinal)
            && string.Equals(queuedRun.LineId, replyToLineId, StringComparison.Ordinal))
            return queuedRun.RunId;

        return projection is not null
               && string.Equals(projection.ThreadId, threadId, StringComparison.Ordinal)
               && ShouldFinalizeOnAnswer(projection, answerAt)
            ? projection.RunId
            : null;
    }

    /// <summary>True once a trigger line has a durable, user-visible assistant answer.</summary>
    public static bool HasCommittedAnswer(IEnumerable<ChatLine> lines, string? triggerLineId)
        => TopicRunProtocol.IsValidIdentifier(triggerLineId)
           && lines.Any(line =>
               !line.Internal
               && string.Equals(line.Role, "assistant", StringComparison.Ordinal)
               && string.Equals(line.ReplyToLineId, triggerLineId, StringComparison.Ordinal));
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
    public string? AgentRequestId { get; set; }
    public string? AgentDispatchToken { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The entire persisted profile for this device/account.</summary>
public sealed class MeshProfile
{
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool DiscoveryTourCompleted { get; set; }
    /// <summary>Generate and show a model-authored action plan before each standard Me topic turn.</summary>
    public bool PlanBeforeActing { get; set; }
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
    public List<MemoryItem> Memories { get; set; } = new();
    public List<Skill> Skills { get; set; } = new();
    public List<SkillMarketplace> SkillMarketplaces { get; set; } = new();
    public List<Widget> Widgets { get; set; } = new();
    /// <summary>Services this user has published to the Community directory (public capability bundles).</summary>
    public List<PublishedService> PublishedServices { get; set; } = new();
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

    /// <summary>Cached relay-owned primary device for answering agent-addressed Messages.</summary>
    public string? AgentPrimaryDeviceId { get; set; }
    /// <summary>Cached optional failover device. Null means no failover.</summary>
    public string? AgentFailoverDeviceId { get; set; }
    public string AgentRoutingVersion { get; set; } = "";
    public bool AgentPrimaryWasSelectedAutomatically { get; set; }
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
    /// A friendly name for THIS device (e.g. "Ilya's Laptop"), shown in linked-device topic pickers.
    /// Defaults from the machine name at onboarding.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Handles this device has an unread inbound person-message from (persisted read-state).</summary>
    public List<string> UnreadFrom { get; set; } = new();

    /// <summary>
    /// Local-machine tool grants (run scripts, control browser, file access), keyed by tool.
    /// All OFF by default. Enabled tools are always available to the owner in their private chat, and
    /// only reach a guest agent when the owner has shared that tool with a matching circle or all allowed contacts.
    /// </summary>
    public Dictionary<LocalToolKind, LocalToolSetting> LocalTools { get; set; } = new();

    /// <summary>
    /// Grants for bundled MCP tool servers (e.g. TotalControl desktop control), keyed by server id.
    /// Same off-by-default, owner-first, audience-scoped model as <see cref="LocalTools"/>.
    /// Each server can expose several tools; the grant governs the whole server.
    /// </summary>
    public Dictionary<string, LocalToolSetting> McpServers { get; set; } = new();

    /// <summary>User-added MCP tool servers (local command launched over stdio). Off by default.</summary>
    public List<CustomMcpServer> CustomMcpServers { get; set; } = new();

    /// <summary>The agent's own private chat (owner context). Legacy single list, kept for migration
    /// into <see cref="OwnThreads"/>; new code uses threads.</summary>
    public List<ChatLine> OwnChat { get; set; } = new();

    /// <summary>The owner's private "Me" chats, split into topic threads (Messages-style).</summary>
    public List<OwnThread> OwnThreads { get; set; } = new();

    [JsonIgnore] public bool IsOnboarded => !string.IsNullOrWhiteSpace(Handle) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>Stable ordering helpers for OwnThread and Conversation lists.</summary>
public static class OwnThreadOrdering
{
    /// <summary>Sort: pinned first, then by activity (newest first), then by creation (newest first), then by id (stable).</summary>
    public static IOrderedEnumerable<OwnThread> ByActivity(IEnumerable<OwnThread> threads)
        => threads
            .OrderByDescending(t => t.IsPinned)
            .ThenByDescending(t => t.LastActivityAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal);
}

/// <summary>Stable ordering helpers for Conversation lists.</summary>
public static class ConversationOrdering
{
    /// <summary>Sort: pinned first, then by activity (newest first), then by creation (newest first), then by handle (stable).</summary>
    public static IOrderedEnumerable<Conversation> ByActivity(IEnumerable<Conversation> conversations)
        => conversations
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastActivityAt ?? c.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(c => c.Handle, StringComparer.OrdinalIgnoreCase);
}
