using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// The single Protocol-9 domain materialiser. Every replicated change - local or inbound - is
/// funnelled through <see cref="Apply"/> inside the transaction that appends the signed event and
/// its outbox references, so the ACTUAL Mesh domain rows the UI reads and the replication record
/// commit together or not at all.
///
/// Two layers are written:
///   1. The generic convergence index (<see cref="ReplicationDomainStore"/>) which owns the
///      deterministic causal last-writer-wins and fork bookkeeping. It decides whether an incoming
///      change wins.
///   2. The real domain tables (<see cref="Protocol9DomainTables"/>) - conversations / chat_lines,
///      own_threads / own_chat, memories, the profile blob that carries contacts and circles,
///      assets / asset_content, ask_user_prompts, replication_read_watermarks and the skill-package
///      blob staging - which are only touched when layer 1 says the write won.
///
/// A malformed or unmodelled payload throws <see cref="ReplicationProjectionException"/>, which
/// rolls the whole apply back: the event, the cursor and the domain rows all revert together.
/// </summary>
public static class ReplicationDomainMaterializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Wire shape of a replicated "Me" topic (thread metadata without its lines).</summary>
    internal sealed record TopicBody(
        string Id,
        string? Title,
        DateTimeOffset CreatedAt,
        int SortOrder,
        string? CommunicationDestinationDeviceId,
        string? CommunicationDestinationDeviceName,
        string? CommunicationDestinationDevicePlatform,
        string? AgentExecutionHostDeviceId,
        string? AgentExecutionHostDeviceName,
        string? AgentExecutionHostDevicePlatform,
        DateTimeOffset? LastActivityAt,
        bool IsPinned,
        DateTimeOffset? ExecutionAt,
        string? ExecutionRunId,
        ConversationKind ConversationKind = ConversationKind.Assistant);

    /// <summary>Wire shape of a replicated asset (summary plus its bytes).</summary>
    internal sealed record AssetBody(
        string Kind,
        string Id,
        string? Name,
        string? MetadataJson,
        string? ContentMime,
        string? ContentB64,
        string? ContentHash,
        int Version,
        string? SourceDeviceId,
        DateTimeOffset UpdatedAt,
        long ContentByteCount = 0,
        bool LocalOnly = false);

    /// <summary>Wire shape of one ask-user option (full option identity travels with the prompt).</summary>
    internal sealed record AskOptionBody(string Id, string Title, string? Description);

    /// <summary>Wire shape of a replicated ask-user prompt.</summary>
    internal sealed record AskPromptBody(
        string PromptId,
        string ThreadId,
        string RunId,
        string Question,
        IReadOnlyList<AskOptionBody>? Options,
        int? RecommendedIndex,
        string? OriginDeviceId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        int Revision = 1,
        int Version = 1,
        bool Resolved = false);

    /// <summary>Wire shape of a replicated ask-user resolution.</summary>
    internal sealed record AskResolveBody(
        string PromptId,
        string State,
        string? Selection,
        string? ResolutionDeviceId,
        DateTimeOffset ResolvedAt,
        AskPromptBody? Prompt = null,
        bool Resolved = true);

    /// <summary>A tombstone body that clears an entity's lines instead of removing the entity.</summary>
    private sealed record TombstoneBody(bool Clear, string? LineId);

    /// <summary>
    /// Materialises one domain envelope. Returns true when the change won causal arbitration and
    /// the actual domain rows were mutated, so the caller can decide whether to notify the UI.
    /// </summary>
    public static bool Apply(
        SqliteConnection conn,
        SqliteTransaction tx,
        ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!ReplicationPayloadCodec.IsMappedAction(envelope.Kind, envelope.Action))
            throw new ReplicationProjectionException(
                $"Unknown replication kind/action '{envelope.Kind}'/{envelope.Action}; refusing to advance the cursor without a domain decision.");
        if (string.IsNullOrWhiteSpace(envelope.EntityId))
            throw new ReplicationProjectionException($"Replication kind '{envelope.Kind}' carried no entity id.");

        // Asset and skill-package materialisation persists device-local bytes and is desktop-only.
        // A mobile device that is handed such a payload cannot materialise it, and silently keeping
        // the event would let the cursor advance past a change this device can never apply, so the
        // projection fails closed and the whole apply rolls back.
        if (!deviceIsDesktop && ReplicationPayloadCodec.RequiresDesktop(envelope.Kind, envelope.Action))
            throw new ReplicationProjectionException(
                $"Replication kind '{envelope.Kind}' action {envelope.Action} is desktop-only and cannot be materialised on this device.");

        var tiebreak = evt.EventId;
        var conv = envelope.ConversationId;
        var causal = envelope.CausalVersion;
        var account = evt.OriginAccount;
        var updated = evt.CreatedAtUnixMs;

        return envelope.Action switch
        {
            ReplicationPayloadCodec.DomainAction.Upsert =>
                ApplyUpsert(conn, tx, envelope, conv, causal, tiebreak, account, updated),
            ReplicationPayloadCodec.DomainAction.Delete =>
                ApplyDelete(conn, tx, envelope, conv, causal, tiebreak, account, updated),
            ReplicationPayloadCodec.DomainAction.AppendLine =>
                ApplyAppendLine(conn, tx, evt, envelope, conv, causal, updated),
            ReplicationPayloadCodec.DomainAction.AskUserPrompt =>
                ApplyAskPrompt(conn, tx, envelope, conv, causal, tiebreak, account, updated),
            ReplicationPayloadCodec.DomainAction.AskUserResolve =>
                ApplyAskResolve(conn, tx, envelope, conv, causal, tiebreak, account, updated),
            ReplicationPayloadCodec.DomainAction.ReadWatermark =>
                ApplyReadWatermark(conn, tx, envelope),
            ReplicationPayloadCodec.DomainAction.AssetUpsert =>
                ApplyAssetUpsert(conn, tx, envelope, conv, causal, tiebreak, account, updated),
            ReplicationPayloadCodec.DomainAction.AssetDelete =>
                ApplyAssetDelete(conn, tx, envelope, conv, causal, tiebreak, account, updated),
            ReplicationPayloadCodec.DomainAction.PackageTransfer =>
                ApplyPackageTransfer(conn, tx, envelope),
            _ => throw new ReplicationProjectionException(
                $"Replication action {envelope.Action} for kind '{envelope.Kind}' has no materialisation.")
        };
    }

    // -----------------------------------------------------------------------
    // Upsert
    // -----------------------------------------------------------------------

    private static bool ApplyUpsert(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope,
        string? conv, string causal, string tiebreak, string? account, long updated)
    {
        var won = ReplicationDomainStore.UpsertEntity(conn, tx, envelope.Kind, envelope.EntityId, conv,
            causal, tiebreak, envelope.BodyJson, account, deleted: false, updated);
        if (!won) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);

        switch (envelope.Kind)
        {
            case ReplicationOpKinds.Conversation:
            {
                var conversation = Deserialize<Conversation>(envelope.BodyJson, "conversation");
                if (string.IsNullOrWhiteSpace(conversation.Handle)) conversation.Handle = envelope.EntityId;
                Protocol9DomainTables.UpsertConversationMetadata(conn, tx, conversation);
                return true;
            }
            case ReplicationOpKinds.Topic:
            {
                var topic = Deserialize<TopicBody>(envelope.BodyJson, "topic");
                var thread = new OwnThread
                {
                    Id = string.IsNullOrWhiteSpace(topic.Id) ? envelope.EntityId : topic.Id,
                    Title = topic.Title ?? "",
                    CreatedAt = topic.CreatedAt == default ? DateTimeOffset.UtcNow : topic.CreatedAt,
                    LastActivityAt = topic.LastActivityAt,
                    IsPinned = topic.IsPinned,
                    ConversationKind = ConversationKind.Assistant,
                    CommunicationDestinationDeviceId = null,
                    CommunicationDestinationDeviceName = null,
                    CommunicationDestinationDevicePlatform = null,
                    AgentExecutionHostDeviceId = topic.AgentExecutionHostDeviceId,
                    AgentExecutionHostDeviceName = topic.AgentExecutionHostDeviceName,
                    AgentExecutionHostDevicePlatform = topic.AgentExecutionHostDevicePlatform,
                    ExecutionAt = topic.ExecutionAt,
                    ExecutionRunId = topic.ExecutionRunId
                };
                Protocol9DomainTables.UpsertOwnThreadMetadata(conn, tx, thread, topic.SortOrder);
                return true;
            }
            case ReplicationOpKinds.Message:
            {
                // A line edit addressed at a conversation: materialise it as an idempotent append.
                var line = Deserialize<ChatLine>(envelope.BodyJson, "message");
                Protocol9DomainTables.AppendChatLine(conn, tx, envelope.EntityId, line);
                return true;
            }
            case ReplicationOpKinds.Contact:
            {
                var contact = Deserialize<ContactProjection>(envelope.BodyJson, "contact");
                Protocol9DomainTables.UpsertProfileContact(conn, tx, envelope.EntityId,
                    ContactNode(contact, envelope.EntityId));
                return true;
            }
            case ReplicationOpKinds.Circle:
            {
                var circle = Deserialize<CircleProjection>(envelope.BodyJson, "circle");
                var rename = circle.Renames?.FirstOrDefault();
                if (rename is not null && !string.IsNullOrWhiteSpace(rename.PreviousName))
                    Protocol9DomainTables.RenameProfileCircle(conn, tx,
                        rename.PreviousName, circle.Name, circle.RequireApproval);
                else
                    Protocol9DomainTables.UpsertProfileCircle(conn, tx, envelope.EntityId,
                        circle.Name, circle.RequireApproval);
                return true;
            }
            case ReplicationOpKinds.Memory:
            {
                var projection = Deserialize<MemoryProjection>(envelope.BodyJson, "memory");
                MemoryItem memory;
                // A memory whose replicated shape the policy rejects cannot be written to the real
                // table, and keeping only the convergence record would hide it forever, so the
                // projection fails closed and the cursor stays put.
                try { memory = MemoryPolicy.FromSync(projection); }
                catch (ArgumentException ex)
                {
                    throw new ReplicationProjectionException(
                        $"Replicated memory '{envelope.EntityId}' was rejected by the memory policy: " + ex.Message);
                }
                Protocol9DomainTables.UpsertMemory(conn, tx, memory);
                return true;
            }
            default:
                throw new ReplicationProjectionException(
                    $"Replication kind '{envelope.Kind}' has no upsert materialisation.");
        }
    }

    // -----------------------------------------------------------------------
    // Delete / clear
    // -----------------------------------------------------------------------

    private static bool ApplyDelete(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope,
        string? conv, string causal, string tiebreak, string? account, long updated)
    {
        var tombstone = ParseTombstone(envelope.BodyJson);
        // A clear keeps the entity alive, so it must not tombstone the convergence row.
        var won = tombstone.Clear
            ? ReplicationDomainStore.UpsertEntity(conn, tx, envelope.Kind, envelope.EntityId, conv,
                causal, tiebreak, envelope.BodyJson, account, deleted: false, updated)
            : ReplicationDomainStore.UpsertEntity(conn, tx, envelope.Kind, envelope.EntityId, conv,
                causal, tiebreak, envelope.BodyJson, account, deleted: true, updated);
        if (!won) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);

        switch (envelope.Kind)
        {
            case ReplicationOpKinds.Conversation:
                if (tombstone.Clear) Protocol9DomainTables.ClearConversation(conn, tx, envelope.EntityId);
                else Protocol9DomainTables.DeleteConversation(conn, tx, envelope.EntityId);
                return true;
            case ReplicationOpKinds.Topic:
                if (tombstone.Clear) Protocol9DomainTables.ClearOwnThread(conn, tx, envelope.EntityId);
                else Protocol9DomainTables.DeleteOwnThread(conn, tx, envelope.EntityId);
                return true;
            case ReplicationOpKinds.Message:
                if (tombstone.Clear) Protocol9DomainTables.ClearConversation(conn, tx, envelope.EntityId);
                else Protocol9DomainTables.DeleteConversation(conn, tx, envelope.EntityId);
                return true;
            case ReplicationOpKinds.Contact:
                Protocol9DomainTables.DeleteProfileContact(conn, tx, envelope.EntityId);
                return true;
            case ReplicationOpKinds.Circle:
                Protocol9DomainTables.DeleteProfileCircle(conn, tx, envelope.EntityId);
                return true;
            case ReplicationOpKinds.Memory:
                Protocol9DomainTables.DeleteMemory(conn, tx, envelope.EntityId);
                return true;
            default:
                throw new ReplicationProjectionException(
                    $"Replication kind '{envelope.Kind}' has no delete materialisation.");
        }
    }

    private static TombstoneBody ParseTombstone(string bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return new TombstoneBody(false, null);
        try
        {
            return JsonSerializer.Deserialize<TombstoneBody>(bodyJson, Json) ?? new TombstoneBody(false, null);
        }
        catch (JsonException ex)
        {
            throw new ReplicationProjectionException("Tombstone body was malformed: " + ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Line append
    // -----------------------------------------------------------------------

    private static bool ApplyAppendLine(
        SqliteConnection conn, SqliteTransaction tx, ReplicationEvent evt,
        ReplicationPayloadCodec.DomainEnvelope envelope, string? conv, string causal, long updated)
    {
        var line = Deserialize<ChatLine>(envelope.BodyJson, "line");
        if (string.IsNullOrWhiteSpace(line.Id))
            throw new ReplicationProjectionException("Replicated line carried no line id.");

        // The convergence log is exact-once per (kind, entity, line): a duplicate event returns
        // false here and never reaches the actual table, so no duplicate row can be produced.
        var appended = ReplicationDomainStore.AppendLine(conn, tx, envelope.Kind, envelope.EntityId,
            line.Id, conv, causal, evt.EventId, envelope.BodyJson, updated);
        if (!appended) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);

        if (string.Equals(envelope.Kind, ReplicationOpKinds.Topic, StringComparison.Ordinal))
            Protocol9DomainTables.AppendOwnChat(conn, tx, envelope.EntityId, line);
        else
            Protocol9DomainTables.AppendChatLine(conn, tx, envelope.EntityId, line);
        return true;
    }

    // -----------------------------------------------------------------------
    // ask-user
    // -----------------------------------------------------------------------

    private static bool ApplyAskPrompt(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope,
        string? conv, string causal, string tiebreak, string? account, long updated)
    {
        var won = ReplicationDomainStore.AskUserPrompt(conn, tx, envelope.EntityId, conv,
            causal, tiebreak, envelope.BodyJson, account, updated);
        if (!won) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);
        var prompt = Deserialize<AskPromptBody>(envelope.BodyJson, "ask-user prompt");
        UpsertPromptRow(conn, tx, envelope.EntityId, prompt);
        return true;
    }

    /// <summary>
    /// Writes the actual prompt row from a full prompt body. The option list keeps its complete
    /// identity (id, title, description) so a device that only ever sees the replicated body can
    /// render exactly the same prompt as the originator.
    /// </summary>
    private static void UpsertPromptRow(
        SqliteConnection conn, SqliteTransaction tx, string entityId, AskPromptBody prompt)
    {
        var options = (prompt.Options ?? [])
            .Select(o => new AskUserOption(o.Id ?? "", o.Title ?? "", o.Description))
            .ToList();
        Protocol9DomainTables.UpsertAskUserPrompt(conn, tx,
            string.IsNullOrWhiteSpace(prompt.PromptId) ? entityId : prompt.PromptId,
            prompt.ThreadId ?? "", prompt.RunId ?? "", prompt.Question ?? "",
            JsonSerializer.Serialize(options, Json), prompt.RecommendedIndex, prompt.OriginDeviceId,
            prompt.CreatedAt == default ? DateTimeOffset.UtcNow : prompt.CreatedAt, prompt.ExpiresAt,
            prompt.Revision, prompt.Version);
    }

    private static bool ApplyAskResolve(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope,
        string? conv, string causal, string tiebreak, string? account, long updated)
    {
        var won = ReplicationDomainStore.AskUserResolve(conn, tx, envelope.EntityId, conv,
            causal, tiebreak, envelope.BodyJson, account, updated);
        if (!won) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);
        var resolution = Deserialize<AskResolveBody>(envelope.BodyJson, "ask-user resolution");
        var promptId = string.IsNullOrWhiteSpace(resolution.PromptId)
            ? envelope.EntityId : resolution.PromptId;
        // A resolution can arrive before the prompt that created it (a device that was offline when
        // the prompt was raised). The resolution carries a snapshot of the prompt, so the row is
        // materialised first and then transitioned; nothing is dropped and nothing is invented.
        if (resolution.Prompt is not null
            && !Protocol9DomainTables.AskUserPromptExists(conn, tx, promptId))
            UpsertPromptRow(conn, tx, promptId, resolution.Prompt);
        Protocol9DomainTables.ResolveAskUserPrompt(conn, tx,
            promptId,
            string.IsNullOrWhiteSpace(resolution.State) ? "resolved" : resolution.State,
            resolution.Selection, resolution.ResolutionDeviceId,
            resolution.ResolvedAt == default ? DateTimeOffset.UtcNow : resolution.ResolvedAt);
        return true;
    }

    // -----------------------------------------------------------------------
    // read watermark (its convergence table IS the durable table unread counts read)
    // -----------------------------------------------------------------------

    private static bool ApplyReadWatermark(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        ReplicationDomainStore.ReadWatermark(conn, tx, envelope.BodyJson);
        return true;
    }

    // -----------------------------------------------------------------------
    // assets
    // -----------------------------------------------------------------------

    private static bool ApplyAssetUpsert(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope,
        string? conv, string causal, string tiebreak, string? account, long updated)
    {
        var won = ReplicationDomainStore.AssetUpsert(conn, tx, envelope.EntityId, conv,
            causal, tiebreak, envelope.BodyJson, account, updated);
        if (!won) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);
        var asset = Deserialize<AssetBody>(envelope.BodyJson, "asset");
        byte[] content;
        try { content = string.IsNullOrEmpty(asset.ContentB64) ? [] : Convert.FromBase64String(asset.ContentB64); }
        catch (FormatException ex)
        {
            throw new ReplicationProjectionException("Asset content was not valid base64: " + ex.Message);
        }
        // An asset whose kind names no known catalog cannot address a real row. Keeping only the
        // convergence record would leave this device permanently missing a supported asset, so the
        // projection fails closed and the cursor stays put.
        if (!TryParseAssetKind(asset.Kind, out var assetKind)
            && !TryParseAssetKind(AssetKindOf(envelope.EntityId), out assetKind))
            throw new ReplicationProjectionException(
                $"Replicated asset '{envelope.EntityId}' declared unknown kind '{asset.Kind}'.");

        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(asset.ContentHash)
            && !string.Equals(actualHash, asset.ContentHash.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ReplicationProjectionException(
                $"Replicated asset '{envelope.EntityId}' content did not match its declared hash.");
        if (asset.ContentByteCount != 0 && asset.ContentByteCount != content.LongLength)
            throw new ReplicationProjectionException(
                $"Replicated asset '{envelope.EntityId}' declared {asset.ContentByteCount} bytes but carried {content.LongLength}.");
        if (asset.LocalOnly)
            throw new ReplicationProjectionException(
                $"Replicated asset '{envelope.EntityId}' was marked local-only and must never have been replicated.");

        Protocol9DomainTables.UpsertAsset(conn, tx, assetKind,
            string.IsNullOrWhiteSpace(asset.Id) ? AssetIdOf(envelope.EntityId) : asset.Id,
            asset.Name ?? "", asset.MetadataJson, asset.ContentMime, content,
            asset.Version <= 0 ? 1 : asset.Version, asset.SourceDeviceId,
            asset.UpdatedAt == default ? DateTimeOffset.UtcNow : asset.UpdatedAt, localOnly: false);
        return true;
    }

    private static bool ApplyAssetDelete(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope,
        string? conv, string causal, string tiebreak, string? account, long updated)
    {
        var won = ReplicationDomainStore.AssetDelete(conn, tx, envelope.EntityId, conv,
            causal, tiebreak, envelope.BodyJson, account, updated);
        if (!won) return false;
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);
        var tombstone = string.IsNullOrWhiteSpace(envelope.BodyJson)
            ? null
            : Deserialize<AssetBody>(envelope.BodyJson, "asset tombstone");
        if (!TryParseAssetKind(AssetKindOf(envelope.EntityId), out var kind)
            && !TryParseAssetKind(tombstone?.Kind, out kind))
            throw new ReplicationProjectionException(
                $"Replicated asset tombstone '{envelope.EntityId}' named an unknown asset kind.");
        var deletedId = string.IsNullOrWhiteSpace(tombstone?.Id) ? AssetIdOf(envelope.EntityId) : tombstone!.Id;
        Protocol9DomainTables.DeleteAsset(conn, tx, kind, deletedId,
            version: 0, sourceDeviceId: null, DateTimeOffset.FromUnixTimeMilliseconds(updated));
        return true;
    }

    /// <summary>The entity id of a replicated asset: "{kind}/{id}".</summary>
    public static string AssetEntityId(AssetKind kind, string id) => kind + "/" + id;

    private static string AssetKindOf(string entityId)
    {
        var slash = entityId.IndexOf('/');
        return slash <= 0 ? entityId : entityId[..slash];
    }

    private static string AssetIdOf(string entityId)
    {
        var slash = entityId.IndexOf('/');
        return slash < 0 || slash + 1 >= entityId.Length ? entityId : entityId[(slash + 1)..];
    }

    private static bool TryParseAssetKind(string? kind, out AssetKind parsed)
        => Enum.TryParse(kind, ignoreCase: true, out parsed);

    // -----------------------------------------------------------------------
    // skill packages
    // -----------------------------------------------------------------------

    private static bool ApplyPackageTransfer(
        SqliteConnection conn, SqliteTransaction tx, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        var completedHash = ReplicationDomainStore.PackageChunk(conn, tx, envelope.EntityId, envelope.BodyJson);
        if (completedHash is null) return false;
        // Every chunk has landed: reassemble, verify, and install in this same transaction, so the
        // transfer becomes visible exactly once and only in its complete, verified form.
        var bytes = ReplicationDomainStore.AssemblePackage(conn, tx, envelope.EntityId);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (completedHash.Length != 64)
            throw new ReplicationProjectionException(
                $"Transfer '{envelope.EntityId}' declared a content hash that is not a SHA-256 digest.");
        if (!string.Equals(actual, completedHash.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ReplicationProjectionException(
                $"Assembled transfer '{envelope.EntityId}' did not match its declared content hash.");
        RequireDomainSchema(conn, tx, envelope.Kind, envelope.Action);

        if (IsAttachmentTransfer(envelope.EntityId))
            return InstallAttachment(conn, tx, envelope.EntityId, bytes, actual);

        // Skill package: stage the content-addressed blob and install the normalized package rows
        // plus the Skill asset body together, so an installed package is never observable without
        // the files it declares.
        Protocol9DomainTables.StagePackageBlob(conn, tx, actual, bytes);
        var (skill, manifest, files) = ParseTransfer(bytes);
        var skillId = skill.Id;
        if (!SkillPackageRows.SchemaPresent(conn, tx))
            throw new ReplicationProjectionException(
                "Skill-package tables are absent; a replicated package transfer cannot be installed.");
        try
        {
            SkillPackageRows.Install(conn, tx, skillId, manifest, files);
        }
        catch (InvalidOperationException ex)
        {
            throw new ReplicationProjectionException(
                $"Replicated skill package '{skillId}' failed installation: " + ex.Message);
        }
        var skillFile = manifest.Files.FirstOrDefault(f => f.Role == SkillFileRole.SkillMarkdown)
            ?? manifest.Files[0];
        skill.Instructions = System.Text.Encoding.UTF8.GetString(files[skillFile.Path]);
        skill.Compatibility = manifest.Compatibility.Clone();
        skill.PackageHash = manifest.PackageHash;
        skill.PackageVersion = manifest.Version;
        var (record, content) = AssetPersistenceModels.ToRecord(
            skill,
            sourceDeviceId: "",
            localOnly: false,
            version: 1);
        Protocol9DomainTables.UpsertAsset(
            conn,
            tx,
            AssetKind.Skill,
            skillId,
            record.Name,
            record.MetadataJson,
            record.ContentMime,
            content,
            record.Version,
            sourceDeviceId: null,
            DateTimeOffset.UtcNow,
            localOnly: false);
        return true;
    }

    /// <summary>Entity-id prefix that routes a transfer to the attachment table instead of a package.</summary>
    private const string AttachmentPrefix = "attachment/";

    /// <summary>The entity id of a replicated attachment transfer.</summary>
    public static string AttachmentEntityId(string attachmentId) => AttachmentPrefix + attachmentId;

    private static bool IsAttachmentTransfer(string entityId)
        => entityId.StartsWith(AttachmentPrefix, StringComparison.Ordinal);

    private static bool InstallAttachment(
        SqliteConnection conn, SqliteTransaction tx, string entityId, byte[] bytes, string sha)
    {
        if (!Protocol9DomainTables.AttachmentSchemaPresent(conn, tx))
            throw new ReplicationProjectionException(
                "Attachment table is absent; a replicated attachment cannot be materialised.");
        var descriptor = ReplicationDomainStore.PackageDescriptor(conn, tx, entityId)
            ?? throw new ReplicationProjectionException(
                $"Attachment transfer '{entityId}' carried no descriptor.");
        Protocol9DomainTables.UpsertReplicatedAttachment(conn, tx,
            entityId[AttachmentPrefix.Length..], descriptor.RunId ?? "", descriptor.Name ?? "",
            descriptor.MimeType ?? "application/octet-stream", sha, bytes, DateTimeOffset.UtcNow);
        return true;
    }

    private static (Skill Skill, SkillPackageManifest Manifest, IReadOnlyDictionary<string, byte[]> Files)
        ParseTransfer(byte[] bytes)
    {
        try
        {
            return SkillPackageTransfer.Deserialize(bytes);
        }
        catch (InvalidOperationException ex)
        {
            throw new ReplicationProjectionException(
                "Replicated skill-package transfer payload was malformed: " + ex.Message);
        }
    }

    /// <summary>
    /// A device that carries the replication tables but not the domain tables cannot materialise
    /// the actual rows this change describes. Advancing the cursor there would strand the change
    /// forever, so the projection fails closed.
    /// </summary>
    private static void RequireDomainSchema(
        SqliteConnection conn, SqliteTransaction tx, string kind,
        ReplicationPayloadCodec.DomainAction action)
    {
        if (!Protocol9DomainTables.DomainSchemaPresent(conn, tx))
            throw new ReplicationProjectionException(
                $"Domain schema is absent; replication kind '{kind}' action {action} cannot be materialised.");
    }

    // -----------------------------------------------------------------------

    private static T Deserialize<T>(string bodyJson, string what)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
            throw new ReplicationProjectionException($"Replicated {what} carried an empty body.");
        try
        {
            return JsonSerializer.Deserialize<T>(bodyJson, Json)
                ?? throw new ReplicationProjectionException($"Replicated {what} body was null.");
        }
        catch (JsonException ex)
        {
            throw new ReplicationProjectionException($"Replicated {what} body was malformed: " + ex.Message);
        }
    }

    private static System.Text.Json.Nodes.JsonObject ContactNode(ContactProjection contact, string entityId)
    {
        var circles = new System.Text.Json.Nodes.JsonArray();
        foreach (var circle in contact.Circles ?? Array.Empty<string>()) circles.Add(circle);
        var keys = new System.Text.Json.Nodes.JsonArray();
        foreach (var key in contact.SigningKeys ?? Array.Empty<string>()) keys.Add(key);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["handle"] = string.IsNullOrWhiteSpace(contact.Handle) ? entityId : contact.Handle,
            ["displayName"] = contact.DisplayName ?? "",
            ["circles"] = circles,
            ["allowed"] = contact.Allowed,
            ["signingKeys"] = keys,
            ["keyChanged"] = contact.KeyChanged,
            ["muted"] = contact.Muted,
            ["blocked"] = contact.Blocked
        };
    }
}
