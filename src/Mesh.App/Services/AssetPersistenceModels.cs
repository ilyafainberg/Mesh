using System.Text;
using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Pure, side-effect-free mapping between the in-memory capability domain types
/// (<see cref="Skill"/>, <see cref="KnowledgeItem"/>, <see cref="Widget"/>) and the durable
/// <see cref="AssetRecord"/> + content-byte representation used by the Mesh 1.17 asset store.
///
/// The mapping is lossless in both directions: every domain field is preserved either as the
/// asset's identity/content columns or inside its metadata JSON, so an asset can be migrated to
/// the asset tables and hydrated back into an identical domain object. The metadata also keeps the
/// original (possibly blank) display name so hydration restores it exactly, while the asset row's
/// <see cref="AssetRecord.Name"/> always carries a non-blank value (falling back to the id) to
/// satisfy <see cref="AssetRecord.EnsureValidForUpsert"/>.
/// </summary>
public static class AssetPersistenceModels
{
    public const string SkillMime = "text/plain";
    public const string KnowledgeMime = "text/plain";
    public const string WidgetMime = "text/html";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record SkillMeta(
        string Name,
        string? Description,
        string Visibility,
        bool Enabled,
        string? SourceMarketplaceId,
        string? SourceSkillId,
        string? Version,
        // ---- Mesh 1.17 skill-package envelope (all optional; legacy rows decode with nulls) ----
        SkillCompatibility? Compatibility = null,
        string? PackageHash = null,
        string? PackageVersion = null);

    private sealed record KnowledgeMeta(
        string Title,
        string Visibility,
        KnowledgeSource Source,
        string? SourceRef,
        DateTimeOffset UpdatedAt);

    // The write-side widget metadata deliberately omits the (potentially large) previous HTML: the
    // current and previous HTML now live together in a versioned content envelope so no large payload
    // ever lands in the summary metadata JSON (see <see cref="WidgetEnvelopeMarker"/>). PreviousPrompt
    // is a short string and stays in metadata.
    private sealed record WidgetMeta(
        string Name,
        string Prompt,
        string Visibility,
        DateTimeOffset CreatedAt,
        DateTimeOffset ModifiedAt,
        string? PreviousPrompt);

    // The read-side record is a superset that can still decode legacy rows written before the
    // envelope existed, where the previous HTML was embedded in the metadata JSON.
    private sealed record WidgetMetaLegacy(
        string Name,
        string Prompt,
        string Visibility,
        DateTimeOffset CreatedAt,
        DateTimeOffset ModifiedAt,
        string? PreviousHtml,
        string? PreviousPrompt);

    // Sentinel prefix (record-separator control chars around a version tag) marking a widget content
    // envelope. It cannot occur at the start of a real HTML document, so its presence unambiguously
    // distinguishes a new envelope payload from a legacy raw-HTML payload.
    private const string WidgetEnvelopeMarker = "\u001eMWGTv1\u001e";

    private sealed record WidgetEnvelope(string Html, string? PreviousHtml);

    private static byte[] EncodeWidgetContent(string? html, string? previousHtml)
        => Encoding.UTF8.GetBytes(
            WidgetEnvelopeMarker
            + JsonSerializer.Serialize(new WidgetEnvelope(html ?? "", previousHtml), Json));

    private static (string Html, string? PreviousHtml) DecodeWidgetContent(
        byte[] content, string? legacyPreviousHtml)
    {
        var text = Text(content);
        if (text.StartsWith(WidgetEnvelopeMarker, StringComparison.Ordinal))
        {
            var env = Deserialize<WidgetEnvelope>(text[WidgetEnvelopeMarker.Length..]);
            return (env?.Html ?? "", env?.PreviousHtml);
        }
        // Legacy row: the whole payload is the current HTML and the previous HTML (if any) came from
        // the old metadata JSON.
        return (text, legacyPreviousHtml);
    }

    // ---- Skill -------------------------------------------------------------

    public static (AssetRecord Record, byte[] Content) ToRecord(
        Skill skill, string sourceDeviceId, bool localOnly, int version)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var meta = new SkillMeta(
            skill.Name,
            skill.Description,
            skill.Visibility,
            skill.Enabled,
            skill.SourceMarketplaceId,
            skill.SourceSkillId,
            skill.Version,
            skill.Compatibility,
            skill.PackageHash,
            skill.PackageVersion);
        var content = Encoding.UTF8.GetBytes(skill.Instructions ?? "");
        return (Record(AssetKind.Skill, skill.Id, skill.Name, meta, SkillMime,
            content, sourceDeviceId, localOnly, version), content);
    }

    public static Skill ToSkill(AssetRecord record, byte[] content)
    {
        var meta = Deserialize<SkillMeta>(record.MetadataJson);
        return new Skill
        {
            Id = record.Id,
            Name = meta?.Name ?? record.Name,
            Description = meta?.Description ?? "",
            Instructions = Text(content),
            Visibility = meta?.Visibility ?? "private",
            Enabled = meta?.Enabled ?? true,
            SourceMarketplaceId = meta?.SourceMarketplaceId,
            SourceSkillId = meta?.SourceSkillId,
            Version = meta?.Version,
            Compatibility = meta?.Compatibility,
            PackageHash = meta?.PackageHash,
            PackageVersion = meta?.PackageVersion
        };
    }

    // ---- Knowledge ---------------------------------------------------------

    public static (AssetRecord Record, byte[] Content) ToRecord(
        KnowledgeItem item, string sourceDeviceId, bool localOnly, int version)
    {
        ArgumentNullException.ThrowIfNull(item);
        var meta = new KnowledgeMeta(
            item.Title,
            item.Visibility,
            item.Source,
            item.SourceRef,
            item.UpdatedAt);
        var content = Encoding.UTF8.GetBytes(item.Content ?? "");
        return (Record(AssetKind.Knowledge, item.Id, item.Title, meta, KnowledgeMime,
            content, sourceDeviceId, localOnly, version), content);
    }

    public static KnowledgeItem ToKnowledge(AssetRecord record, byte[] content)
    {
        var meta = Deserialize<KnowledgeMeta>(record.MetadataJson);
        return new KnowledgeItem
        {
            Id = record.Id,
            Title = meta?.Title ?? record.Name,
            Content = Text(content),
            Visibility = meta?.Visibility ?? "private",
            Source = meta?.Source ?? KnowledgeSource.Manual,
            SourceRef = meta?.SourceRef,
            UpdatedAt = meta?.UpdatedAt ?? DateTimeOffset.UtcNow
        };
    }

    // ---- Widget ------------------------------------------------------------

    public static (AssetRecord Record, byte[] Content) ToRecord(
        Widget widget, string sourceDeviceId, bool localOnly, int version)
    {
        ArgumentNullException.ThrowIfNull(widget);
        var meta = new WidgetMeta(
            widget.Name,
            widget.Prompt,
            widget.Visibility,
            widget.CreatedAt,
            widget.ModifiedAt,
            widget.PreviousPrompt);
        var content = EncodeWidgetContent(widget.Html, widget.PreviousHtml);
        return (Record(AssetKind.Widget, widget.Id, widget.Name, meta, WidgetMime,
            content, sourceDeviceId, localOnly, version), content);
    }

    public static Widget ToWidget(AssetRecord record, byte[] content)
    {
        var meta = Deserialize<WidgetMetaLegacy>(record.MetadataJson);
        var (html, previousHtml) = DecodeWidgetContent(content, meta?.PreviousHtml);
        return new Widget
        {
            Id = record.Id,
            Name = meta?.Name ?? record.Name,
            Prompt = meta?.Prompt ?? "",
            Html = html,
            Visibility = meta?.Visibility ?? "private",
            CreatedAt = meta?.CreatedAt ?? DateTimeOffset.UtcNow,
            ModifiedAt = meta?.ModifiedAt ?? DateTimeOffset.UtcNow,
            PreviousHtml = previousHtml,
            PreviousPrompt = meta?.PreviousPrompt
        };
    }

    // ---- summary-only mappers (metadata only, no content bytes) -------------
    //
    // Startup hydration builds these from paged summaries so the in-memory compatibility collections
    // carry every asset's metadata but none of its payload bytes. Content fields are intentionally
    // blank; consumers load a single body on demand through the AppState lazy asset APIs.

    public static Skill ToSkillSummary(AssetRecord record)
    {
        var skill = ToSkill(record, Array.Empty<byte>());
        skill.Instructions = "";
        return skill;
    }

    public static KnowledgeItem ToKnowledgeSummary(AssetRecord record)
    {
        var item = ToKnowledge(record, Array.Empty<byte>());
        item.Content = "";
        return item;
    }

    public static Widget ToWidgetSummary(AssetRecord record)
    {
        // Decode against empty content: a new-format row yields blank HTML (payload lives in the
        // content table), a legacy row still surfaces its old previous HTML from metadata but no
        // current HTML is materialised here.
        var widget = ToWidget(record, Array.Empty<byte>());
        widget.Html = "";
        widget.PreviousHtml = null;
        return widget;
    }

    // ---- shared ------------------------------------------------------------

    private static AssetRecord Record<TMeta>(
        AssetKind kind,
        string id,
        string displayName,
        TMeta meta,
        string mime,
        byte[] content,
        string sourceDeviceId,
        bool localOnly,
        int version)
        => new(
            Kind: kind,
            Id: id,
            Name: string.IsNullOrWhiteSpace(displayName) ? id : displayName,
            MetadataJson: JsonSerializer.Serialize(meta, Json),
            ContentMime: mime,
            ContentHash: null,
            ContentByteCount: content.LongLength,
            Version: version,
            SourceDeviceId: sourceDeviceId,
            UpdatedAt: DateTimeOffset.UtcNow,
            IsDeleted: false,
            LocalOnly: localOnly);

    private static T? Deserialize<T>(string? json) where T : class
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Json);

    private static string Text(byte[] content) => content is { Length: > 0 } ? Encoding.UTF8.GetString(content) : "";
}
