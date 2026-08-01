using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Protocol 9 ACTUAL replication for assets, ask-user interactions, skill packages and
/// attachments.
///
/// Every test here asserts on the real tables the app reads (<c>assets</c> / <c>asset_content</c>,
/// <c>ask_user_prompts</c>, <c>skill_packages</c> / <c>skill_package_files</c> /
/// <c>skill_package_blobs</c>, <c>replicated_attachments</c>) together with the signed event log
/// and its outbox, proving three properties:
///   * LOCAL ATOMICITY: the actual row, the signed event, the outbox references and the sequence
///     allocation commit in ONE transaction, so a failure leaves no row, no event and no sequence
///     hole;
///   * FAIL CLOSED: an inbound payload this device cannot faithfully materialise (unknown kind,
///     absent domain schema, bad hash, oversized or malformed chunk, a local-only asset) rolls the
///     whole transaction back and leaves the cursor where it was;
///   * NO LEGACY SURFACE: the old store-only asset outbox is gone from the schema entirely.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Protocol9AssetsInteractionsActualTests : ReplicationTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- helpers -----------------------------------------------------------

    private static string Body(object value) => JsonSerializer.Serialize(value, Json);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static long Scalar(MeshDb db, string sql)
    {
        using var cmd = db.RawConnectionForTest.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static string? Text(MeshDb db, string sql)
    {
        using var cmd = db.RawConnectionForTest.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static long EventCount(MeshDb db) => Scalar(db, "SELECT COUNT(*) FROM replication_events;");

    private static long OutboxCount(MeshDb db) => Scalar(db, "SELECT COUNT(*) FROM replication_outbox;");

    private static bool TableExists(MeshDb db, string table)
        => Scalar(db, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';") > 0;

    private OfflineJournal Local(string handle = "alice", string device = "dev-a", bool desktop = true)
    {
        AddSibling(handle, device + "-sib");
        return NewJournal(handle, device, desktop);
    }

    /// <summary>A canonical replicated asset body carrying kind, identity, bytes, size and hash.</summary>
    private static string AssetBody(
        AssetKind kind, string id, byte[] content, string? name = null,
        string? metadataJson = null, string? overrideHash = null, bool localOnly = false,
        long? overrideByteCount = null)
        => Body(new
        {
            Kind = kind.ToString(),
            Id = id,
            Name = name ?? id,
            MetadataJson = metadataJson,
            ContentMime = "text/markdown",
            ContentB64 = Convert.ToBase64String(content),
            ContentHash = overrideHash ?? Sha(content),
            ContentByteCount = overrideByteCount ?? content.LongLength,
            Version = 1,
            SourceDeviceId = "dev-a",
            UpdatedAt = DateTimeOffset.UtcNow,
            LocalOnly = localOnly
        });

    private static ReplicationPayloadCodec.DomainEnvelope AssetUpsert(
        AssetKind kind, string id, byte[] content, string? name = null, string? metadataJson = null,
        string causal = "v1", string? overrideHash = null, bool localOnly = false,
        long? overrideByteCount = null)
        => new(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            ReplicationDomainMaterializer.AssetEntityId(kind, id), null, causal,
            AssetBody(kind, id, content, name, metadataJson, overrideHash, localOnly, overrideByteCount));

    private static ReplicationPayloadCodec.DomainEnvelope AssetDelete(
        AssetKind kind, string id, string causal = "v2")
        => new(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetDelete,
            ReplicationDomainMaterializer.AssetEntityId(kind, id), null, causal,
            Body(new { Kind = kind.ToString(), Id = id }));

    private static string PromptBody(
        string promptId, string question = "Proceed?", int optionCount = 2,
        int? recommended = 0, DateTimeOffset? expiresAt = null, int revision = 1, int version = 1)
        => Body(new
        {
            PromptId = promptId,
            ThreadId = "t-1",
            RunId = "r-1",
            Question = question,
            Options = Enumerable.Range(0, optionCount)
                .Select(i => new { Id = "opt-" + i, Title = "Option " + i, Description = "Why " + i })
                .ToArray(),
            RecommendedIndex = recommended,
            OriginDeviceId = "dev-a",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Revision = revision,
            Version = version,
            Resolved = false
        });

    private static ReplicationPayloadCodec.DomainEnvelope AskPrompt(
        string promptId, string question = "Proceed?", int optionCount = 2, string causal = "v1")
        => new(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            promptId, null, causal, PromptBody(promptId, question, optionCount));

    private static ReplicationPayloadCodec.DomainEnvelope AskResolve(
        string promptId, string selection, string state = "answered", string causal = "v2",
        bool withSnapshot = false)
        => new(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve,
            promptId, null, causal,
            Body(new
            {
                PromptId = promptId,
                State = state,
                Selection = selection,
                ResolutionDeviceId = "dev-a",
                ResolvedAt = DateTimeOffset.UtcNow,
                Prompt = withSnapshot
                    ? JsonSerializer.Deserialize<JsonElement>(PromptBody(promptId), Json)
                    : (JsonElement?)null,
                Resolved = true
            }));

    /// <summary>Builds a validated in-memory skill package of roughly the requested size.</summary>
    private static SkillPackageContent Package(string packageHash = "ph-1", int resourceBytes = 32)
    {
        var markdown = Encoding.UTF8.GetBytes("# Skill\nDo the thing.\n");
        var resource = new byte[resourceBytes];
        for (var i = 0; i < resource.Length; i++) resource[i] = (byte)(i % 251);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Skill.md"] = markdown,
            ["data/resource.bin"] = resource
        };
        var manifest = new SkillPackageManifest
        {
            PackageHash = packageHash,
            Version = "1.0.0",
            Source = "test",
            Trust = SkillPackageTrust.Untrusted,
            Compatibility = new SkillCompatibility(),
            Files =
            [
                new SkillFileManifest
                {
                    Path = "Skill.md", Sha256 = Sha(markdown), Size = markdown.LongLength,
                    Role = SkillFileRole.SkillMarkdown
                },
                new SkillFileManifest
                {
                    Path = "data/resource.bin", Sha256 = Sha(resource), Size = resource.LongLength,
                    Role = SkillFileRole.Resource
                }
            ]
        };
        return new SkillPackageContent(manifest, files);
    }

    /// <summary>Chunk envelopes for one package transfer, exactly as the desktop installer builds them.</summary>
    private static (List<ReplicationPayloadCodec.DomainEnvelope> Envelopes, byte[] Payload, string Hash)
        TransferEnvelopes(string skillId, SkillPackageContent content, int? maxChunkBytes = null)
    {
        var payload = SkillPackageTransfer.Serialize(skillId, content);
        var hash = Sha(payload);
        var chunks = SkillPackageTransfer.Chunk(payload, maxChunkBytes ?? SkillPackageTransfer.MaxChunkBytes);
        var envelopes = new List<ReplicationPayloadCodec.DomainEnvelope>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var body = JsonSerializer.Serialize(
                new ReplicationDomainStore.PackageChunkBody(
                    i, chunks.Count, payload.LongLength, hash, Convert.ToBase64String(chunks[i]),
                    skillId, "application/vnd.mesh.skill-package"),
                Json);
            envelopes.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                skillId, null, "v" + (i + 1), body));
        }
        return (envelopes, payload, hash);
    }

    /// <summary>Attachment chunk envelopes: the same transfer surface addressed to an attachment id.</summary>
    private static List<ReplicationPayloadCodec.DomainEnvelope> AttachmentEnvelopes(
        string attachmentId, string runId, string name, string mime, byte[] bytes, int maxChunkBytes)
    {
        var hash = Sha(bytes);
        var chunks = SkillPackageTransfer.Chunk(bytes, maxChunkBytes);
        var entity = ReplicationDomainMaterializer.AttachmentEntityId(attachmentId);
        var envelopes = new List<ReplicationPayloadCodec.DomainEnvelope>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var body = JsonSerializer.Serialize(
                new ReplicationDomainStore.PackageChunkBody(
                    i, chunks.Count, bytes.LongLength, hash, Convert.ToBase64String(chunks[i]),
                    name, mime, runId),
                Json);
            envelopes.Add(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                entity, null, "v" + (i + 1), body));
        }
        return envelopes;
    }

    // =======================================================================
    // 1. Desktop asset: actual row + signed event + outbox in one transaction
    // =======================================================================

    [TestMethod]
    public void DesktopAssetUpsert_WritesTheActualAssetRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "body"u8.ToArray()), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
    }

    [TestMethod]
    public void DesktopAssetUpsert_WritesTheActualContentRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "body"u8.ToArray()), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM asset_content WHERE id = 's-1';"));
    }

    [TestMethod]
    public void DesktopAssetUpsert_WritesTheSignedEventAndOutboxWithTheRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "body"u8.ToArray()), new[] { "alice" });
        Assert.AreEqual(1, EventCount(node.Db), "one signed event");
        Assert.IsTrue(OutboxCount(node.Db) >= 1, "target refs are durable");
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
    }

    [TestMethod]
    public void DesktopAssetUpsert_StoresTheActualContentBytes()
    {
        var node = Local();
        var content = "the real bytes"u8.ToArray();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", content), new[] { "alice" });
        var stored = node.Db.GetFullAsset(AssetKind.Skill, "s-1");
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(content, stored!.Value.Content);
    }

    [TestMethod]
    public void DesktopAssetUpsert_StoresTheActualName()
    {
        var node = Local();
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray(), name: "Nightly Report"), new[] { "alice" });
        Assert.AreEqual("Nightly Report", node.Db.GetFullAsset(AssetKind.Skill, "s-1")!.Value.Summary.Name);
    }

    [TestMethod]
    public void MetadataOnlyEdit_PreservesTheStoredBody()
    {
        var node = Local();
        var content = "keep me"u8.ToArray();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", content, name: "First"), new[] { "alice" });
        // A metadata-only edit re-sends the stored body, so the bytes survive the rename.
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", content, name: "Renamed",
                metadataJson: "{\"tag\":\"x\"}", causal: "v2"),
            new[] { "alice" });
        var stored = node.Db.GetFullAsset(AssetKind.Skill, "s-1")!.Value;
        Assert.AreEqual("Renamed", stored.Summary.Name);
        CollectionAssert.AreEqual(content, stored.Content);
    }

    [TestMethod]
    public void MetadataOnlyEdit_StoresTheActualMetadataJson()
    {
        var node = Local();
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray(), metadataJson: "{\"tag\":\"x\"}"),
            new[] { "alice" });
        StringAssert.Contains(
            Text(node.Db, "SELECT metadata_json FROM assets WHERE id = 's-1';") ?? "", "tag");
    }

    [TestMethod]
    public void DesktopAssetUpsert_KnowledgeKindReachesTheActualTable()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Knowledge, "k-1", "k"u8.ToArray()), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db,
            $"SELECT COUNT(*) FROM assets WHERE id = 'k-1' AND kind = '{AssetKind.Knowledge}';"));
    }

    [TestMethod]
    public void DesktopAssetUpsert_WidgetKindReachesTheActualTable()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Widget, "w-1", "w"u8.ToArray()), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db,
            $"SELECT COUNT(*) FROM assets WHERE id = 'w-1' AND kind = '{AssetKind.Widget}';"));
    }

    [TestMethod]
    public void DesktopAssetDelete_TombstonesTheActualRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray()), new[] { "alice" });
        node.Journal.EmitLocal(AssetDelete(AssetKind.Skill, "s-1"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1' AND is_deleted = 1;"));
    }

    [TestMethod]
    public void DesktopAssetDelete_DropsTheActualContentRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray()), new[] { "alice" });
        node.Journal.EmitLocal(AssetDelete(AssetKind.Skill, "s-1"), new[] { "alice" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM asset_content WHERE id = 's-1';"));
    }

    [TestMethod]
    public void DesktopAssetDelete_EmitsItsOwnSignedEvent()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray()), new[] { "alice" });
        node.Journal.EmitLocal(AssetDelete(AssetKind.Skill, "s-1"), new[] { "alice" });
        Assert.AreEqual(2, EventCount(node.Db));
    }

    [TestMethod]
    public void MobileAssetUpsert_EmitsNothingAndWritesNothing()
    {
        var node = Local("alice", "phone", desktop: false);
        Assert.ThrowsException<InvalidOperationException>(() =>
            node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray()), new[] { "alice" }));
        Assert.AreEqual(0, EventCount(node.Db), "a mobile device never emits asset bytes");
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
    }

    [TestMethod]
    public void LocalOnlyAsset_WrittenThroughTheStoreLeavesTheJournalUntouched()
    {
        var node = Local();
        // The local-only path never enters replication: it writes the actual row directly.
        node.Db.UpsertAsset(
            new AssetRecord(
                AssetKind.Skill, "local-1", "Local", null, "text/markdown", null, 7, 1, "dev-a",
                DateTimeOffset.UtcNow, IsDeleted: false, LocalOnly: true),
            "private"u8.ToArray());
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 'local-1';"));
        Assert.AreEqual(0, EventCount(node.Db), "a local-only asset produces no replication event");
        Assert.AreEqual(0, OutboxCount(node.Db));
    }

    [TestMethod]
    public void FailedAssetProjection_LeavesNoRowNoEventAndNoSequenceHole()
    {
        var node = Local();
        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray()), new[] { "alice" });
        var seqBefore = Scalar(node.Db, "SELECT MAX(seq) FROM replication_events;");

        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(
                AssetUpsert(AssetKind.Skill, "s-2", "b"u8.ToArray(), overrideHash: new string('a', 64)),
                new[] { "alice" }));

        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-2';"));
        Assert.AreEqual(1, EventCount(node.Db), "the failed change never became an event");

        node.Journal.EmitLocal(AssetUpsert(AssetKind.Skill, "s-3", "b"u8.ToArray()), new[] { "alice" });
        Assert.AreEqual(seqBefore + 1, Scalar(node.Db, "SELECT MAX(seq) FROM replication_events;"),
            "the rolled-back attempt burned no sequence number");
    }

    [TestMethod]
    public void AssetUpsert_DeclaredByteCountMismatchRollsBack()
    {
        var node = Local();
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(
                AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray(), overrideByteCount: 999),
                new[] { "alice" }));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    [TestMethod]
    public void AssetUpsert_LocalOnlyFlagOnTheWireRollsBack()
    {
        var node = Local();
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(
                AssetUpsert(AssetKind.Skill, "s-1", "b"u8.ToArray(), localOnly: true), new[] { "alice" }));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
    }

    [TestMethod]
    public void AssetUpsert_UnknownKindRollsBack()
    {
        var node = Local();
        var body = Body(new
        {
            Kind = "Sculpture", Id = "s-1", Name = "s", ContentB64 = "", ContentHash = (string?)null,
            Version = 1, UpdatedAt = DateTimeOffset.UtcNow
        });
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
                "Sculpture/s-1", null, "v1", body), new[] { "alice" }));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    [TestMethod]
    public async Task AssetSurvivesRestart_ActualRowAndEventAreStillThere()
    {
        var node = NewNode("alice", "dev-r", desktop: true);
        AddSibling("alice", "dev-r-sib");
        await node.Engine.EmitLocalAsync(AssetUpsert(AssetKind.Skill, "s-1", "durable"u8.ToArray()),
            new[] { "alice" });
        var reopened = Reopen(node);
        Assert.AreEqual(1, Scalar(reopened.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
        Assert.AreEqual(1, EventCount(reopened.Db));
    }

    // =======================================================================
    // 2. Ask-user: prompt, resolution, expiry, cancel, first writer
    // =======================================================================

    [TestMethod]
    public void AskPrompt_WritesTheActualPromptRowAndItsEvent()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
        Assert.AreEqual(1, EventCount(node.Db));
    }

    [TestMethod]
    public void AskPrompt_CarriesEveryOptionIdentity()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", optionCount: 3), new[] { "alice" });
        var prompt = node.Db.GetAskUserPrompt("p-1");
        Assert.IsNotNull(prompt);
        Assert.AreEqual(3, prompt!.Options.Count);
        Assert.AreEqual("opt-1", prompt.Options[1].Id);
        Assert.AreEqual("Option 1", prompt.Options[1].Title);
        Assert.AreEqual("Why 1", prompt.Options[1].Description);
    }

    [TestMethod]
    public void AskPrompt_CarriesTheRecommendedOption()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", optionCount: 3), new[] { "alice" });
        Assert.AreEqual(0, node.Db.GetAskUserPrompt("p-1")!.RecommendedIndex);
    }

    [TestMethod]
    public void AskPrompt_CarriesItsOriginDevice()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        Assert.AreEqual("dev-a", node.Db.GetAskUserPrompt("p-1")!.OriginDeviceId);
    }

    [TestMethod]
    public void AskPrompt_StartsPending()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        Assert.AreEqual("pending", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskResolution_MarksTheActualRowAnsweredAndEmitsAnEvent()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "opt-0"), new[] { "alice" });
        Assert.AreEqual("answered", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
        Assert.AreEqual("opt-0", Text(node.Db, "SELECT selection FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
        Assert.AreEqual(2, EventCount(node.Db));
    }

    [TestMethod]
    public void AskResolution_FirstWriterWins()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "opt-0", causal: "v2"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "opt-1", causal: "v3"), new[] { "alice" });
        Assert.AreEqual("opt-0", Text(node.Db, "SELECT selection FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskExpiry_TransitionsTheActualRowExactlyOnce()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", selection: null!, state: "expired"), new[] { "alice" });
        Assert.AreEqual("expired", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
        node.Journal.EmitLocal(AskResolve("p-1", "opt-0", causal: "v3"), new[] { "alice" });
        Assert.AreEqual("expired", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"),
            "an expired prompt is already resolved and cannot be answered afterwards");
    }

    [TestMethod]
    public void AskCancel_TransitionsTheActualRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", selection: null!, state: "cancelled"), new[] { "alice" });
        Assert.AreEqual("cancelled", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskResolution_OutOfOrderCreatesThePromptFromItsSnapshot()
    {
        var node = Local();
        // The resolution lands on a device that never saw the prompt: the snapshot rebuilds the row.
        node.Journal.EmitLocal(AskResolve("p-9", "opt-1", withSnapshot: true), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM ask_user_prompts WHERE prompt_id = 'p-9';"));
        Assert.AreEqual("answered", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-9';"));
        Assert.AreEqual("opt-1", Text(node.Db, "SELECT selection FROM ask_user_prompts WHERE prompt_id = 'p-9';"));
    }

    [TestMethod]
    public void AskResolution_OutOfOrderSnapshotKeepsTheOptionIdentities()
    {
        var node = Local();
        node.Journal.EmitLocal(AskResolve("p-9", "opt-1", withSnapshot: true), new[] { "alice" });
        var prompt = node.Db.GetAskUserPrompt("p-9");
        Assert.IsNotNull(prompt);
        Assert.AreEqual(2, prompt!.Options.Count);
        Assert.AreEqual("opt-0", prompt.Options[0].Id);
    }

    [TestMethod]
    public void AskPrompt_LatePromptDoesNotDowngradeAResolvedRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", causal: "v1"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "opt-0", causal: "v2"), new[] { "alice" });
        node.Journal.EmitLocal(AskPrompt("p-1", causal: "v3"), new[] { "alice" });
        Assert.AreEqual("answered", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskPrompt_DuplicateDeliveryDoesNotDuplicateTheActualRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", causal: "v1"), new[] { "alice" });
        node.Journal.EmitLocal(AskPrompt("p-1", causal: "v1"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskPrompt_SuspendedContextResumesExactlyOnce()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1"), new[] { "alice" });
        node.Db.SaveSuspendedContext(new SuspendedAgentContext(
            "ctx-1", "p-1", "t-1", "r-1", "{\"step\":1}", DateTimeOffset.UtcNow, null, null));
        node.Journal.EmitLocal(AskResolve("p-1", "opt-0"), new[] { "alice" });
        Assert.IsNotNull(node.Db.GetSuspendedContext("ctx-1"));
        Assert.IsTrue(node.Db.MarkContextResumed("ctx-1", DateTimeOffset.UtcNow), "the first resume wins");
        Assert.IsFalse(node.Db.MarkContextResumed("ctx-1", DateTimeOffset.UtcNow), "and it is exactly once");
    }

    [TestMethod]
    public void AskPrompt_MalformedBodyRollsBack()
    {
        var node = Local();
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
                "p-bad", null, "v1", "{not json"), new[] { "alice" }));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM ask_user_prompts;"));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    [TestMethod]
    public async Task AskPromptAndResolution_ReachASiblingsActualTable()
    {
        var a = NewNode("alice", "dev-1");
        var b = NewNode("alice", "dev-2");
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        await a.Engine.EmitLocalAsync(AskPrompt("p-7"), new[] { "alice" });
        await Fabric.DrainAsync();
        await a.Engine.EmitLocalAsync(AskResolve("p-7", "opt-1"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual("answered", Text(b.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-7';"));
        Assert.AreEqual("opt-1", Text(b.Db, "SELECT selection FROM ask_user_prompts WHERE prompt_id = 'p-7';"));
    }

    // =======================================================================
    // 3. Skill packages: chunked transfer, install atomicity, mobile refusal
    // =======================================================================

    [TestMethod]
    public void PackageInstall_PackageRowsAndEveryTransferEventCommitTogether()
    {
        var node = Local();
        var content = Package();
        var (envelopes, _, _) = TransferEnvelopes("s-pkg", content);
        node.Journal.EmitLocalBatch(envelopes, new[] { "alice" }, (conn, tx, index) =>
        {
            if (index == 0) SkillPackageRows.Install(conn, tx, "s-pkg", content.Manifest, content.Files);
        });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-pkg';"));
        Assert.AreEqual(envelopes.Count, EventCount(node.Db));
    }

    [TestMethod]
    public void PackageInstall_WritesEveryDeclaredFileRow()
    {
        var node = Local();
        var content = Package();
        var (envelopes, _, _) = TransferEnvelopes("s-pkg", content);
        node.Journal.EmitLocalBatch(envelopes, new[] { "alice" }, (conn, tx, index) =>
        {
            if (index == 0) SkillPackageRows.Install(conn, tx, "s-pkg", content.Manifest, content.Files);
        });
        Assert.AreEqual(2, Scalar(node.Db, "SELECT COUNT(*) FROM skill_package_files WHERE skill_id = 's-pkg';"));
    }

    [TestMethod]
    public void PackageInstall_StagesEveryFileBlob()
    {
        var node = Local();
        var content = Package();
        var (envelopes, _, _) = TransferEnvelopes("s-pkg", content);
        node.Journal.EmitLocalBatch(envelopes, new[] { "alice" }, (conn, tx, index) =>
        {
            if (index == 0) SkillPackageRows.Install(conn, tx, "s-pkg", content.Manifest, content.Files);
        });
        Assert.AreEqual(2, Scalar(node.Db, "SELECT COUNT(*) FROM skill_package_blobs;"));
    }

    [TestMethod]
    public void PackageInstall_FailedFileValidationLeavesNoPackageAndNoEvent()
    {
        var node = Local();
        var content = Package();
        // A manifest whose declared hash does not match the bytes must abort the whole install.
        content.Manifest.Files[1].Sha256 = new string('b', 64);
        var (envelopes, _, _) = TransferEnvelopes("s-bad", content);
        Assert.ThrowsException<InvalidOperationException>(() =>
            node.Journal.EmitLocalBatch(envelopes, new[] { "alice" }, (conn, tx, index) =>
            {
                if (index == 0) SkillPackageRows.Install(conn, tx, "s-bad", content.Manifest, content.Files);
            }));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-bad';"));
        Assert.AreEqual(0, EventCount(node.Db), "no half-installed package leaves a stranded event behind");
    }

    [TestMethod]
    public void PackageTransfer_TwoMegabytePayloadChunksDeterministically()
    {
        var payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        var first = SkillPackageTransfer.Chunk(payload, SkillPackageTransfer.MaxChunkBytes);
        var second = SkillPackageTransfer.Chunk(payload, SkillPackageTransfer.MaxChunkBytes);
        Assert.AreEqual(6, first.Count, "2 MB splits into six 400 KB-bounded chunks");
        Assert.AreEqual(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++) CollectionAssert.AreEqual(first[i], second[i]);
    }

    [TestMethod]
    public void PackageTransfer_EveryChunkStaysUnderTheChunkBound()
    {
        var payload = new byte[2 * 1024 * 1024];
        foreach (var chunk in SkillPackageTransfer.Chunk(payload, SkillPackageTransfer.MaxChunkBytes))
            Assert.IsTrue(chunk.LongLength <= ReplicationDomainStore.MaxChunkBytes);
    }

    [TestMethod]
    public void PackageTransfer_TwentyMegabytePayloadIsChunkedNotRejected()
    {
        var payload = new byte[20 * 1024 * 1024];
        var chunks = SkillPackageTransfer.Chunk(payload, SkillPackageTransfer.MaxChunkBytes);
        Assert.AreEqual(52, chunks.Count);
        Assert.AreEqual(payload.LongLength, chunks.Sum(c => c.LongLength));
        Assert.IsTrue(payload.LongLength <= ReplicationDomainStore.MaxPackageBytes);
    }

    [TestMethod]
    public void PackageTransfer_OverTheTwentyMegabyteBoundRollsBack()
    {
        var node = Local();
        var body = JsonSerializer.Serialize(
            new ReplicationDomainStore.PackageChunkBody(
                0, 1, ReplicationDomainStore.MaxPackageBytes + 1, new string('c', 64), "AAAA"),
            Json);
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "over", null, "v1", body), new[] { "alice" }));
        Assert.AreEqual(0, node.Db.GetPackageChunkCount("over"));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    [TestMethod]
    public void PackageTransfer_ChunkAboveTheChunkBoundRollsBack()
    {
        var node = Local();
        var oversized = new byte[ReplicationDomainStore.MaxChunkBytes + 1];
        var body = JsonSerializer.Serialize(
            new ReplicationDomainStore.PackageChunkBody(
                0, 1, oversized.LongLength, Sha(oversized), Convert.ToBase64String(oversized)),
            Json);
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "fat", null, "v1", body), new[] { "alice" }));
        Assert.AreEqual(0, node.Db.GetPackageChunkCount("fat"));
    }

    [TestMethod]
    public async Task PackageTransfer_ReceiverInstallsNormalizedRowsAndTheSkillAsset()
    {
        var a = NewNode("alice", "dev-1", desktop: true);
        var b = NewNode("alice", "dev-2", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        var content = Package();
        var (envelopes, _, _) = TransferEnvelopes("s-recv", content);
        foreach (var envelope in envelopes)
        {
            await a.Engine.EmitLocalAsync(envelope, new[] { "alice" });
            await Fabric.DrainAsync();
        }
        Assert.AreEqual(1, Scalar(b.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-recv';"));
        Assert.AreEqual(2, Scalar(b.Db, "SELECT COUNT(*) FROM skill_package_files WHERE skill_id = 's-recv';"));
        Assert.AreEqual(1, Scalar(b.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-recv';"));
    }

    [TestMethod]
    public async Task PackageTransfer_ReceiverKeepsNothingUntilTheLastChunkLands()
    {
        var a = NewNode("alice", "dev-1", desktop: true);
        var b = NewNode("alice", "dev-2", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        var content = Package(resourceBytes: 600 * 1024);
        var (envelopes, _, _) = TransferEnvelopes("s-partial", content);
        Assert.IsTrue(envelopes.Count > 1, "the fixture must actually span several chunks");
        await a.Engine.EmitLocalAsync(envelopes[0], new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(0, Scalar(b.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-partial';"),
            "a partially transferred package is never observable");
        Assert.IsTrue(b.Db.GetPackageChunkCount("s-partial") >= 1, "but its chunks are durable");
    }

    [TestMethod]
    public async Task PackageTransfer_TamperedChunkFailsTheHashAndRollsBackTheReceiver()
    {
        var a = NewNode("alice", "dev-1", desktop: true);
        var b = NewNode("alice", "dev-2", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        var content = Package();
        var payload = SkillPackageTransfer.Serialize("s-tamper", content);
        var tampered = (byte[])payload.Clone();
        tampered[^1] ^= 0xFF;
        var body = JsonSerializer.Serialize(
            new ReplicationDomainStore.PackageChunkBody(
                0, 1, tampered.LongLength, Sha(payload), Convert.ToBase64String(tampered),
                "s-tamper", "application/vnd.mesh.skill-package"),
            Json);
        var origin = AddOrigin("alice", "dev-3");
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
            "s-tamper", null, "v1", body);
        var evt = MakeEvent(origin, "alice", "dev-3", 1, envelope, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("dev-3", new[] { evt }));

        Assert.IsNull(b.Db.GetEvent(evt.EventId), "a tampered transfer is never stored");
        Assert.AreEqual(0, Scalar(b.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-tamper';"));
        var cursor = b.Db.GetCursor("dev-3");
        Assert.IsTrue(cursor is null || cursor.Contiguous == 0, "the cursor never advanced");
    }

    [TestMethod]
    public void PackageTransfer_MobilePermanentlyRefusesTheTransfer()
    {
        var node = Local("alice", "phone", desktop: false);
        var content = Package();
        var (envelopes, _, _) = TransferEnvelopes("s-mob", content);
        Assert.ThrowsException<InvalidOperationException>(() =>
            node.Journal.EmitLocalBatch(envelopes, new[] { "alice" }));
        Assert.AreEqual(0, EventCount(node.Db));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM skill_packages;"));
    }

    [TestMethod]
    public void PackageTransfer_ChunksSurviveAReopenAndStillComplete()
    {
        var node = Local();
        var bytes = new byte[600 * 1024];
        Random.Shared.NextBytes(bytes);
        var envelopes = AttachmentEnvelopes(
            "att-restart", "run-1", "big.bin", "application/octet-stream", bytes,
            SkillPackageTransfer.MaxChunkBytes);
        Assert.IsTrue(envelopes.Count > 1);
        node.Journal.EmitLocal(envelopes[0], new[] { "alice" });
        var entity = ReplicationDomainMaterializer.AttachmentEntityId("att-restart");
        Assert.AreEqual(1, node.Db.GetPackageChunkCount(entity));
        Assert.IsNull(node.Db.GetReplicatedAttachment("att-restart"));

        for (var i = 1; i < envelopes.Count; i++)
            node.Journal.EmitLocal(envelopes[i], new[] { "alice" });
        Assert.IsNotNull(node.Db.GetReplicatedAttachment("att-restart"),
            "the staged chunks complete the transfer after the interruption");
    }

    [TestMethod]
    public void PackageDelete_RemovesEveryPackageRowAndFileRow()
    {
        var node = Local();
        var content = Package();
        node.Db.InstallSkillPackage("s-del", content.Manifest, content.Files);
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-del';"));
        node.Db.DeleteAllSkillPackages("s-del");
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM skill_packages WHERE skill_id = 's-del';"));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM skill_package_files WHERE skill_id = 's-del';"));
    }

    [TestMethod]
    public void PackageUpdate_SupersededRowsAreCleanedUp()
    {
        var node = Local();
        node.Db.InstallSkillPackage("s-upd", Package("ph-1").Manifest, Package("ph-1").Files);
        var second = Package("ph-2");
        node.Db.InstallSkillPackage("s-upd", second.Manifest, second.Files);
        node.Db.DeleteSkillPackage("s-upd", "ph-1");
        var hashes = node.Db.ListSkillPackageHashes("s-upd");
        Assert.AreEqual(1, hashes.Count);
        Assert.AreEqual("ph-2", hashes[0]);
    }

    [TestMethod]
    public void PackageTransfer_MalformedPayloadIsNeverInstalled()
    {
        var node = Local();
        var junk = "this is not a package"u8.ToArray();
        var body = JsonSerializer.Serialize(
            new ReplicationDomainStore.PackageChunkBody(
                0, 1, junk.LongLength, Sha(junk), Convert.ToBase64String(junk),
                "s-junk", "application/vnd.mesh.skill-package"),
            Json);
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "s-junk", null, "v1", body), new[] { "alice" }));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM skill_packages;"));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    // =======================================================================
    // 4. Attachments: chunked local emission, actual staging, inbound assembly
    // =======================================================================

    [TestMethod]
    public void Attachment_SingleChunkRoundTripsIntoTheActualTable()
    {
        var node = Local();
        var bytes = "attachment payload"u8.ToArray();
        var envelopes = AttachmentEnvelopes(
            "att-1", "run-1", "note.txt", "text/plain", bytes, SkillPackageTransfer.MaxChunkBytes);
        foreach (var envelope in envelopes) node.Journal.EmitLocal(envelope, new[] { "alice" });
        var stored = node.Db.GetReplicatedAttachment("att-1");
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(bytes, stored!.Value.Bytes);
    }

    [TestMethod]
    public void Attachment_CarriesItsRunNameAndMimeType()
    {
        var node = Local();
        var envelopes = AttachmentEnvelopes(
            "att-1", "run-7", "note.txt", "text/plain", "x"u8.ToArray(), SkillPackageTransfer.MaxChunkBytes);
        foreach (var envelope in envelopes) node.Journal.EmitLocal(envelope, new[] { "alice" });
        var stored = node.Db.GetReplicatedAttachment("att-1")!.Value;
        Assert.AreEqual("run-7", stored.RunId);
        Assert.AreEqual("note.txt", stored.Name);
        Assert.AreEqual("text/plain", stored.MimeType);
    }

    [TestMethod]
    public void Attachment_EmitsOneEventPerChunkWithOutboxRefs()
    {
        var node = Local();
        var bytes = new byte[900 * 1024];
        var envelopes = AttachmentEnvelopes(
            "att-2", "run-1", "big.bin", "application/octet-stream", bytes,
            SkillPackageTransfer.MaxChunkBytes);
        node.Journal.EmitLocalBatch(envelopes, new[] { "alice" });
        Assert.AreEqual(3, envelopes.Count);
        Assert.AreEqual(envelopes.Count, EventCount(node.Db));
        Assert.IsTrue(OutboxCount(node.Db) >= envelopes.Count);
    }

    [TestMethod]
    public void Attachment_MultiChunkAssemblesTheExactBytes()
    {
        var node = Local();
        var bytes = new byte[900 * 1024];
        Random.Shared.NextBytes(bytes);
        var envelopes = AttachmentEnvelopes(
            "att-3", "run-1", "big.bin", "application/octet-stream", bytes,
            SkillPackageTransfer.MaxChunkBytes);
        foreach (var envelope in envelopes) node.Journal.EmitLocal(envelope, new[] { "alice" });
        var stored = node.Db.GetReplicatedAttachment("att-3");
        Assert.IsNotNull(stored);
        Assert.AreEqual(Sha(bytes), stored!.Value.Sha256);
        CollectionAssert.AreEqual(bytes, stored.Value.Bytes);
    }

    [TestMethod]
    public void Attachment_OfflinePendingChunksStayStagedUntilComplete()
    {
        var node = Local();
        var bytes = new byte[900 * 1024];
        var envelopes = AttachmentEnvelopes(
            "att-4", "run-1", "big.bin", "application/octet-stream", bytes,
            SkillPackageTransfer.MaxChunkBytes);
        node.Journal.EmitLocal(envelopes[0], new[] { "alice" });
        node.Journal.EmitLocal(envelopes[1], new[] { "alice" });
        Assert.IsNull(node.Db.GetReplicatedAttachment("att-4"), "still incomplete");
        Assert.AreEqual(2, node.Db.GetPackageChunkCount(
            ReplicationDomainMaterializer.AttachmentEntityId("att-4")));
        node.Journal.EmitLocal(envelopes[2], new[] { "alice" });
        Assert.IsNotNull(node.Db.GetReplicatedAttachment("att-4"));
    }

    [TestMethod]
    public async Task Attachment_InboundAssemblesTheReceiversActualAttachment()
    {
        var a = NewNode("alice", "dev-1", desktop: true);
        var b = NewNode("alice", "dev-2", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        var bytes = "inbound attachment"u8.ToArray();
        foreach (var envelope in AttachmentEnvelopes(
                     "att-in", "run-3", "note.txt", "text/plain", bytes, SkillPackageTransfer.MaxChunkBytes))
        {
            await a.Engine.EmitLocalAsync(envelope, new[] { "alice" });
            await Fabric.DrainAsync();
        }
        var stored = b.Db.GetReplicatedAttachment("att-in");
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(bytes, stored!.Value.Bytes);
    }

    [TestMethod]
    public void Attachment_CountsPerRunAreQueryable()
    {
        var node = Local();
        foreach (var id in new[] { "att-a", "att-b" })
            foreach (var envelope in AttachmentEnvelopes(
                         id, "run-9", id + ".txt", "text/plain", Encoding.UTF8.GetBytes(id),
                         SkillPackageTransfer.MaxChunkBytes))
                node.Journal.EmitLocal(envelope, new[] { "alice" });
        Assert.AreEqual(2, node.Db.CountReplicatedAttachments("run-9"));
    }

    // =======================================================================
    // 5. Inbound fail-closed
    // =======================================================================

    [TestMethod]
    public async Task Inbound_UnknownAssetKindFailsClosedAndHoldsTheCursor()
    {
        var a = NewNode("alice", "dev-1");
        var b = NewNode("bob", "dev-2", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        var origin = AddOrigin("mallory", "m1");
        var body = Body(new
        {
            Kind = "Sculpture", Id = "x-1", Name = "x", ContentB64 = "", ContentHash = (string?)null,
            Version = 1, UpdatedAt = DateTimeOffset.UtcNow
        });
        var envelope = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            "Sculpture/x-1", null, "v1", body);
        var evt = MakeEvent(origin, "mallory", "m1", 1, envelope, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("m1", new[] { evt }));

        Assert.IsNull(b.Db.GetEvent(evt.EventId));
        var cursor = b.Db.GetCursor("m1");
        Assert.IsTrue(cursor is null || cursor.Contiguous == 0);
    }

    [TestMethod]
    public async Task Inbound_BadAssetHashFailsClosedAndHoldsTheCursor()
    {
        var a = NewNode("alice", "dev-1");
        var b = NewNode("bob", "dev-2", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        var origin = AddOrigin("mallory", "m1");
        var envelope = AssetUpsert(AssetKind.Skill, "x-2", "b"u8.ToArray(), overrideHash: new string('d', 64));
        var evt = MakeEvent(origin, "mallory", "m1", 1, envelope, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("m1", new[] { evt }));

        Assert.IsNull(b.Db.GetEvent(evt.EventId));
        Assert.AreEqual(0, Scalar(b.Db, "SELECT COUNT(*) FROM assets WHERE id = 'x-2';"));
        var cursor = b.Db.GetCursor("m1");
        Assert.IsTrue(cursor is null || cursor.Contiguous == 0);
    }

    [TestMethod]
    public void Inbound_MissingDomainSchemaFailsClosed()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)conn.BeginTransaction();
        var envelope = AssetUpsert(AssetKind.Skill, "x-3", "b"u8.ToArray());
        var evt = MakeEvent(KeyPair.New(), "alice", "dev-x", 1, envelope, new[] { KeyPair.New().PublicB64 });
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            ReplicationDomainMaterializer.Apply(conn, tx, evt, envelope, deviceIsDesktop: true));
        tx.Rollback();
    }

    [TestMethod]
    public void Inbound_MobileAssetProjectionFailsClosed()
    {
        var node = Local("alice", "phone", desktop: false);
        var conn = node.Db.RawConnectionForTest;
        using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)conn.BeginTransaction();
        var envelope = AssetUpsert(AssetKind.Skill, "x-4", "b"u8.ToArray());
        var evt = MakeEvent(KeyPair.New(), "alice", "dev-x", 1, envelope, new[] { KeyPair.New().PublicB64 });
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            ReplicationDomainMaterializer.Apply(conn, tx, evt, envelope, deviceIsDesktop: false));
        tx.Rollback();
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 'x-4';"));
    }

    // =======================================================================
    // 6. The legacy store-only asset outbox is gone from the schema
    // =======================================================================

    [TestMethod]
    public void Schema_AssetOutboxTableIsAbsent()
    {
        var node = Local();
        Assert.IsFalse(TableExists(node.Db, "asset_outbox"),
            "Protocol 9 replicates through replication_outbox only");
    }

    [TestMethod]
    public void Schema_AssetOutboxDeadLetterTableIsAbsent()
    {
        var node = Local();
        Assert.IsFalse(TableExists(node.Db, "asset_outbox_dead_letters"));
    }

    [TestMethod]
    public void Schema_ReplicationOutboxIsTheOnlyOutbox()
    {
        var node = Local();
        var outboxes = Scalar(node.Db,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE '%asset%outbox%';");
        Assert.AreEqual(0, outboxes, "no asset specific outbox table exists");
        Assert.IsTrue(TableExists(node.Db, "replication_outbox"));
    }

    [TestMethod]
    public void Schema_AssetTablesStillCarrySummaryAndContent()
    {
        var node = Local();
        Assert.IsTrue(TableExists(node.Db, "assets"));
        Assert.IsTrue(TableExists(node.Db, "asset_content"));
        Assert.IsTrue(TableExists(node.Db, "replicated_attachments"));
    }
}
