using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Behavioral tests for the Protocol 9 lazy-asset core: the bounded LRU content cache, the
/// bounded batch-load budget/accumulator, the versioned widget content envelope, and the
/// summary-only hydration + single-body / metadata-preserving persistence contract.
///
/// Cache, budget, accumulator and envelope are exercised directly against the owned production
/// units. The AppState-level hydration/mutation flows (which need the full MAUI graph) are proven
/// against a real <see cref="MeshDb"/> + <see cref="AssetStore"/> by mirroring the exact algorithm
/// AppState runs - the same pattern the migration tests use - so the observable guarantees
/// (zero per-asset body reads on hydration, single-body load, metadata edit preserves the stored
/// body, delete tombstones one row, a circle metadata sweep preserves every body) are all checked.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Protocol9AssetLazyTests
{
    private const string DeviceId = "dev-local";
    private const string WidgetEnvelopeMarker = "\u001eMWGTv1\u001e";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "protocol9-asset-lazy-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    // ==================================================================
    // Bounded LRU content cache
    // ==================================================================

    [TestMethod]
    public void Cache_Constructor_RejectsNonPositiveBudgets()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new AssetContentCache(0, 16));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new AssetContentCache(16, 0));
    }

    [TestMethod]
    public void Cache_TracksCountAndBytes()
    {
        var cache = new AssetContentCache(maxEntries: 8, maxBytes: 1024);
        cache.Set("a", new byte[] { 1, 2, 3 });
        cache.Set("b", new byte[] { 4, 5 });

        Assert.AreEqual(2, cache.Count);
        Assert.AreEqual(5, cache.CurrentBytes);

        cache.Remove("a");
        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(2, cache.CurrentBytes);

        cache.Clear();
        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(0, cache.CurrentBytes);
    }

    [TestMethod]
    public void Cache_EvictsLeastRecentlyUsed_ByCount()
    {
        var cache = new AssetContentCache(maxEntries: 2, maxBytes: 1_000_000);
        cache.Set("a", new byte[] { 1 });
        cache.Set("b", new byte[] { 2 });
        cache.Set("c", new byte[] { 3 }); // "a" is now the LRU and must be evicted

        Assert.IsFalse(cache.TryGet("a", out _), "The least-recently-used entry must be evicted.");
        Assert.IsTrue(cache.TryGet("b", out _));
        Assert.IsTrue(cache.TryGet("c", out _));
        Assert.AreEqual(2, cache.Count);
    }

    [TestMethod]
    public void Cache_TryGet_PromotesEntry_SoItSurvivesEviction()
    {
        var cache = new AssetContentCache(maxEntries: 2, maxBytes: 1_000_000);
        cache.Set("a", new byte[] { 1 });
        cache.Set("b", new byte[] { 2 });

        Assert.IsTrue(cache.TryGet("a", out _)); // touch "a" so "b" becomes the LRU
        cache.Set("c", new byte[] { 3 });

        Assert.IsTrue(cache.TryGet("a", out _), "A touched entry must survive the next eviction.");
        Assert.IsFalse(cache.TryGet("b", out _), "The untouched entry must be evicted.");
        Assert.IsTrue(cache.TryGet("c", out _));
    }

    [TestMethod]
    public void Cache_EvictsUntilWithinByteBudget()
    {
        var cache = new AssetContentCache(maxEntries: 1_000, maxBytes: 10);
        cache.Set("a", new byte[6]);
        cache.Set("b", new byte[6]); // 12 > 10, so "a" must be evicted

        Assert.IsFalse(cache.TryGet("a", out _));
        Assert.IsTrue(cache.TryGet("b", out _));
        Assert.AreEqual(6, cache.CurrentBytes);
        Assert.IsTrue(cache.CurrentBytes <= 10);
    }

    [TestMethod]
    public void Cache_OversizedBody_IsNotCached_AndDoesNotEvictExisting()
    {
        var cache = new AssetContentCache(maxEntries: 100, maxBytes: 10);
        cache.Set("small", new byte[4]);

        cache.Set("huge", new byte[20]); // larger than the whole budget

        Assert.IsTrue(cache.TryGet("small", out _), "An oversized insert must not evict existing entries.");
        Assert.IsFalse(cache.TryGet("huge", out _), "An oversized body must not be cached above budget.");
        Assert.AreEqual(1, cache.Count);
        Assert.IsTrue(cache.CurrentBytes <= 10, $"Cache must stay within budget; was {cache.CurrentBytes}.");
    }

    [TestMethod]
    public void Cache_DefensiveCopy_MutatingSourceAfterSet_DoesNotCorruptCache()
    {
        var cache = new AssetContentCache(maxEntries: 8, maxBytes: 1024);
        var source = new byte[] { 1, 2, 3 };
        cache.Set("k", source);

        source[0] = 99; // the caller mutates its own array after handing it in

        Assert.IsTrue(cache.TryGet("k", out var got));
        Assert.AreEqual(1, got[0], "The cache must store a defensive copy, not the caller's array.");
    }

    [TestMethod]
    public void Cache_DefensiveCopy_MutatingReturnedArray_DoesNotCorruptCache()
    {
        var cache = new AssetContentCache(maxEntries: 8, maxBytes: 1024);
        cache.Set("k", new byte[] { 1, 2, 3 });

        Assert.IsTrue(cache.TryGet("k", out var first));
        first[0] = 99; // the caller mutates what it got back

        Assert.IsTrue(cache.TryGet("k", out var second));
        Assert.AreEqual(1, second[0], "TryGet must hand back a fresh copy each time.");
    }

    // ==================================================================
    // Bounded batch budget + accumulator
    // ==================================================================

    [TestMethod]
    public void Accumulator_RejectsNonPositiveBudget()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new BoundedAssetAccumulator(new AssetLoadBudget(0, 100)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new BoundedAssetAccumulator(new AssetLoadBudget(4, 0)));
    }

    [TestMethod]
    public void Accumulator_DeduplicatesIds()
    {
        var acc = new BoundedAssetAccumulator(new AssetLoadBudget(10, 1_000));
        Assert.IsTrue(acc.ShouldLoad("a"));
        Assert.IsFalse(acc.ShouldLoad("a"), "A repeated id must not be loaded twice.");
        Assert.IsFalse(acc.ShouldLoad(""), "A blank id is never loaded.");
    }

    [TestMethod]
    public void Accumulator_StopsAtCountBudget()
    {
        var acc = new BoundedAssetAccumulator(new AssetLoadBudget(2, 1_000_000));
        Assert.IsTrue(acc.ShouldLoad("a"));
        Assert.IsTrue(acc.TryAccept(10));
        Assert.IsTrue(acc.ShouldLoad("b"));
        Assert.IsTrue(acc.TryAccept(10));

        Assert.IsTrue(acc.IsFull);
        Assert.IsFalse(acc.ShouldLoad("c"), "No further ids are loaded once the count budget is reached.");
        Assert.AreEqual(2, acc.Count);
    }

    [TestMethod]
    public void Accumulator_StopsBeforeExceedingByteBudget()
    {
        var acc = new BoundedAssetAccumulator(new AssetLoadBudget(100, 100));
        Assert.IsTrue(acc.ShouldLoad("a"));
        Assert.IsTrue(acc.TryAccept(60));
        Assert.IsTrue(acc.ShouldLoad("b"));
        Assert.IsFalse(acc.TryAccept(60), "Adding a body that would exceed MaxBytes must be refused.");

        Assert.AreEqual(1, acc.Count);
        Assert.AreEqual(60, acc.Bytes);
    }

    [TestMethod]
    public void Accumulator_OversizedFirstItem_IsRejected_NotReturnedAlone()
    {
        var acc = new BoundedAssetAccumulator(new AssetLoadBudget(100, 50));
        Assert.IsTrue(acc.ShouldLoad("a"));
        Assert.IsFalse(acc.TryAccept(80), "A single over-budget body must not be returned, even alone.");
        Assert.AreEqual(0, acc.Count);
        Assert.AreEqual(0, acc.Bytes);
    }

    // ==================================================================
    // Versioned widget content envelope
    // ==================================================================

    [TestMethod]
    public void Widget_Envelope_KeepsPreviousHtmlOutOfMetadata_AndRoundTrips()
    {
        const string sentinel = "PREVIOUS_SENTINEL_PAYLOAD_0123456789";
        var widget = new Widget
        {
            Id = "w-env",
            Name = "Env",
            Prompt = "make",
            Html = "<html>current</html>",
            PreviousHtml = $"<div>{sentinel}</div>",
            PreviousPrompt = "old prompt"
        };

        var (record, content) = AssetPersistenceModels.ToRecord(widget, DeviceId, localOnly: false, version: 1);

        Assert.IsFalse(
            record.MetadataJson!.Contains(sentinel),
            "The (large) previous HTML must live in the content envelope, never in the summary metadata.");
        Assert.IsTrue(
            Encoding.UTF8.GetString(content).Contains(sentinel),
            "The previous HTML must be carried in the content envelope.");

        var restored = AssetPersistenceModels.ToWidget(record, content);
        Assert.AreEqual(widget.Html, restored.Html);
        Assert.AreEqual(widget.PreviousHtml, restored.PreviousHtml);
        Assert.AreEqual(widget.PreviousPrompt, restored.PreviousPrompt);
    }

    [TestMethod]
    public void Widget_Envelope_DecodesLegacyRow_WithPreviousHtmlInMetadata()
    {
        // A pre-envelope row: the metadata JSON still carries PreviousHtml and the content is raw HTML.
        var legacyMeta = new
        {
            Name = "Legacy",
            Prompt = "make",
            Visibility = "private",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            PreviousHtml = "<old>legacy previous</old>",
            PreviousPrompt = "legacy prompt"
        };
        var record = new AssetRecord(
            Kind: AssetKind.Widget,
            Id: "w-legacy",
            Name: "Legacy",
            MetadataJson: JsonSerializer.Serialize(legacyMeta, Web),
            ContentMime: AssetPersistenceModels.WidgetMime,
            ContentHash: null,
            ContentByteCount: 0,
            Version: 1,
            SourceDeviceId: DeviceId,
            UpdatedAt: DateTimeOffset.UtcNow,
            IsDeleted: false,
            LocalOnly: false);
        var legacyContent = Encoding.UTF8.GetBytes("<html>legacy current</html>");

        var restored = AssetPersistenceModels.ToWidget(record, legacyContent);

        Assert.AreEqual("<html>legacy current</html>", restored.Html, "Legacy content is the current HTML.");
        Assert.AreEqual("<old>legacy previous</old>", restored.PreviousHtml, "Legacy previous HTML comes from metadata.");
        Assert.AreEqual("legacy prompt", restored.PreviousPrompt);
    }

    [TestMethod]
    public void Widget_Envelope_ReSaveMigratesLegacyRowIntoEnvelope()
    {
        // Decode a legacy row, then re-encode it: the previous HTML must move into the envelope and
        // out of the metadata.
        var restored = new Widget
        {
            Id = "w-mig",
            Name = "Mig",
            Html = "<html>current</html>",
            PreviousHtml = "<old>MIGRATE_PAYLOAD_X9</old>"
        };
        var (record, content) = AssetPersistenceModels.ToRecord(restored, DeviceId, localOnly: false, version: 2);

        Assert.IsFalse(record.MetadataJson!.Contains("MIGRATE_PAYLOAD_X9"),
            "Re-saving must migrate the previous HTML out of metadata into the envelope.");
        Assert.IsTrue(Encoding.UTF8.GetString(content).StartsWith(WidgetEnvelopeMarker, StringComparison.Ordinal),
            "A re-saved widget must use the versioned content envelope.");

        var again = AssetPersistenceModels.ToWidget(record, content);
        Assert.AreEqual("<old>MIGRATE_PAYLOAD_X9</old>", again.PreviousHtml);
    }

    [TestMethod]
    public void Widget_Envelope_CorruptPayload_FailsExplicitly()
    {
        var record = new AssetRecord(
            Kind: AssetKind.Widget,
            Id: "w-bad",
            Name: "Bad",
            MetadataJson: "{}",
            ContentMime: AssetPersistenceModels.WidgetMime,
            ContentHash: null,
            ContentByteCount: 0,
            Version: 1,
            SourceDeviceId: DeviceId,
            UpdatedAt: DateTimeOffset.UtcNow,
            IsDeleted: false,
            LocalOnly: false);
        var corrupt = Encoding.UTF8.GetBytes(WidgetEnvelopeMarker + "{not valid json");

        Assert.ThrowsException<JsonException>(
            () => AssetPersistenceModels.ToWidget(record, corrupt),
            "A corrupt envelope must fail explicitly, not decode to silently-empty content.");
    }

    // ==================================================================
    // Summary mappers carry metadata only, never body bytes
    // ==================================================================

    [TestMethod]
    public void SummaryMappers_ExcludeBodyBytes()
    {
        var skill = new Skill { Id = "s", Name = "S", Instructions = "big instructions" };
        var item = new KnowledgeItem { Id = "k", Title = "K", Content = "big content" };
        var widget = new Widget { Id = "w", Name = "W", Html = "<html>big</html>", PreviousHtml = "<old>x</old>" };

        var (skillRec, _) = AssetPersistenceModels.ToRecord(skill, DeviceId, false, 1);
        var (itemRec, _) = AssetPersistenceModels.ToRecord(item, DeviceId, false, 1);
        var (widgetRec, _) = AssetPersistenceModels.ToRecord(widget, DeviceId, false, 1);

        Assert.AreEqual("", AssetPersistenceModels.ToSkillSummary(skillRec).Instructions);
        Assert.AreEqual("S", AssetPersistenceModels.ToSkillSummary(skillRec).Name);
        Assert.AreEqual("", AssetPersistenceModels.ToKnowledgeSummary(itemRec).Content);
        var widgetSummary = AssetPersistenceModels.ToWidgetSummary(widgetRec);
        Assert.AreEqual("", widgetSummary.Html);
        Assert.IsNull(widgetSummary.PreviousHtml);
    }

    // ==================================================================
    // Hydration pages summaries only: zero per-asset body reads at scale
    // ==================================================================

    [TestMethod]
    public void Hydration_PagesSummariesOnly_MaterialisesZeroBodies()
    {
        const int count = 2_000;
        const int bodySize = 1_024;
        using var db = MeshDb.Open(databasePath, key);
        for (var i = 0; i < count; i++)
        {
            var skill = new Skill
            {
                Id = $"s-{i:D6}",
                Name = $"Skill {i}",
                Instructions = new string('x', bodySize)
            };
            var (record, content) = AssetPersistenceModels.ToRecord(skill, DeviceId, localOnly: false, version: 1);
            db.UpsertAsset(record, content);
        }

        // Hydrate exactly the way AppState.LoadAssetSummaries does: page summaries, map each to a
        // body-less summary object, and NEVER call GetFullAsset. Track materialised body bytes.
        const int pageSize = 500;
        var summaries = new List<Skill>();
        long materialisedBodyBytes = 0;
        var pages = 0;
        string? afterId = null;
        while (true)
        {
            var page = db.PageAssetSummaries(AssetKind.Skill, pageSize, afterId);
            if (page.Count == 0) break;
            pages++;
            foreach (var record in page)
            {
                afterId = record.Id;
                if (record.IsDeleted) continue;
                Assert.AreEqual(bodySize, record.ContentByteCount, "Bodies exist in the store...");
                var summary = AssetPersistenceModels.ToSkillSummary(record);
                materialisedBodyBytes += Encoding.UTF8.GetByteCount(summary.Instructions);
                summaries.Add(summary);
            }
            if (page.Count < pageSize) break;
        }

        Assert.AreEqual(count, summaries.Count, "Every summary must hydrate.");
        Assert.AreEqual(0, materialisedBodyBytes,
            "Summary-only hydration must materialise zero body bytes regardless of asset count.");
        Assert.AreEqual((count + pageSize - 1) / pageSize, pages, "Paging must be O(count / pageSize).");
    }

    // ==================================================================
    // Single-body load
    // ==================================================================

    [TestMethod]
    public void SingleBodyLoad_ReturnsOnlyTheRequestedBody()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreSkill(db, "a", "Alpha", "body-a");
        StoreSkill(db, "b", "Beta", "body-b");
        StoreSkill(db, "c", "Gamma", "body-c");

        var full = new AssetStore(db).GetFullAssetAsync(AssetKind.Skill, "b").GetAwaiter().GetResult();
        Assert.IsNotNull(full);
        var restored = AssetPersistenceModels.ToSkill(full!.Value.Summary, full.Value.Content);
        Assert.AreEqual("body-b", restored.Instructions);
        Assert.AreEqual("Beta", restored.Name);
    }

    // ==================================================================
    // Metadata-only edit preserves an unloaded body (mirrors UpsertMetadataOnly)
    // ==================================================================

    [TestMethod]
    public void MetadataEdit_PreservesUnloadedBody()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreSkill(db, "s", "Original", "ORIGINAL BODY");

        // The in-memory object is a summary: metadata is present, the body is NOT loaded (blank).
        var summaryEdit = new Skill { Id = "s", Name = "Renamed", Visibility = "circle", Instructions = "" };
        var (record, _) = AssetPersistenceModels.ToRecord(summaryEdit, DeviceId, localOnly: false, version: 2);
        var metadataOnly = record with { ContentHash = null, ContentByteCount = 0 };

        // Mirror ProcessWorkAsync's UpsertMetadataOnly: fetch the one stored body and re-save it.
        var existing = db.GetFullAsset(AssetKind.Skill, "s")!.Value;
        db.UpsertAsset(metadataOnly, existing.Content);

        var full = db.GetFullAsset(AssetKind.Skill, "s")!.Value;
        var restored = AssetPersistenceModels.ToSkill(full.Summary, full.Content);
        Assert.AreEqual("ORIGINAL BODY", restored.Instructions, "A metadata-only edit must preserve the stored body.");
        Assert.AreEqual("Renamed", restored.Name, "The metadata must be updated.");
        Assert.AreEqual("circle", restored.Visibility);
    }

    // ==================================================================
    // Content update carries one body; delete tombstones one row
    // ==================================================================

    [TestMethod]
    public void ContentUpdate_ReplacesBody_AndBumpsVersion()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreSkill(db, "s", "S", "V1", version: 1);
        StoreSkill(db, "s", "S", "V2", version: 2);

        var full = db.GetFullAsset(AssetKind.Skill, "s")!.Value;
        Assert.AreEqual("V2", AssetPersistenceModels.ToSkill(full.Summary, full.Content).Instructions);
        Assert.AreEqual(2, full.Summary.Version);
        Assert.AreEqual(1, db.PageAssetSummaries(AssetKind.Skill, 500, null).Count,
            "An update must collapse to a single row.");
    }

    [TestMethod]
    public void Delete_TombstonesOneRow_AndDropsContent()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreSkill(db, "s", "S", "body");

        db.DeleteAsset(AssetKind.Skill, "s", DeviceId);

        var full = db.GetFullAsset(AssetKind.Skill, "s")!.Value;
        Assert.IsTrue(full.Summary.IsDeleted, "Delete must tombstone the summary row.");
        Assert.AreEqual(0, full.Content.Length, "Delete must drop the content body.");
    }

    // ==================================================================
    // Circle metadata sweep: bounded, preserves every body
    // ==================================================================

    [TestMethod]
    public void CircleMetadataSweep_PreservesEveryBody()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreSkill(db, "a", "A", "body-a", visibility: "circle-old");
        StoreSkill(db, "b", "B", "body-b", visibility: "circle-old");
        StoreSkill(db, "c", "C", "body-c", visibility: "private");

        // A circle rename sweep rewrites visibility metadata on the affected rows via metadata-only
        // upserts that preserve each stored body (AppState.DiffAssetMetadata + UpsertMetadataOnly).
        var version = 1;
        foreach (var id in new[] { "a", "b" })
        {
            var edit = new Skill { Id = id, Name = id.ToUpperInvariant(), Visibility = "circle-new", Instructions = "" };
            var (record, _) = AssetPersistenceModels.ToRecord(edit, DeviceId, localOnly: false, version: ++version);
            var metadataOnly = record with { ContentHash = null, ContentByteCount = 0 };
            var existing = db.GetFullAsset(AssetKind.Skill, id)!.Value;
            db.UpsertAsset(metadataOnly, existing.Content);
        }

        foreach (var (id, body, expectedVisibility) in new[]
                 {
                     ("a", "body-a", "circle-new"),
                     ("b", "body-b", "circle-new"),
                     ("c", "body-c", "private"),
                 })
        {
            var full = db.GetFullAsset(AssetKind.Skill, id)!.Value;
            var restored = AssetPersistenceModels.ToSkill(full.Summary, full.Content);
            Assert.AreEqual(body, restored.Instructions, $"Sweep must preserve the body of '{id}'.");
            Assert.AreEqual(expectedVisibility, restored.Visibility, $"Visibility of '{id}' must reflect the sweep.");
        }
    }

    // ==================================================================
    // Bounded batch load against the real store (uses the production accumulator)
    // ==================================================================

    [TestMethod]
    public void BoundedBatch_DedupesAndRespectsCountBudget()
    {
        using var db = MeshDb.Open(databasePath, key);
        foreach (var id in new[] { "a", "b", "c", "d" })
            StoreSkill(db, id, id.ToUpperInvariant(), $"body-{id}");

        // Mirror AppState.LoadSkillsAsync exactly, using the production BoundedAssetAccumulator.
        var ids = new[] { "a", "a", "b", "c", "d" }; // duplicate "a"
        var budget = new AssetLoadBudget(MaxCount: 2, MaxBytes: 1_000_000);
        var accumulator = new BoundedAssetAccumulator(budget);
        var loaded = new List<Skill>();
        foreach (var id in ids)
        {
            if (accumulator.IsFull) break;
            if (!accumulator.ShouldLoad(id)) continue;
            var full = db.GetFullAsset(AssetKind.Skill, id);
            if (full is null) continue;
            var skill = AssetPersistenceModels.ToSkill(full.Value.Summary, full.Value.Content);
            if (!accumulator.TryAccept(Encoding.UTF8.GetByteCount(skill.Instructions))) break;
            loaded.Add(skill);
        }

        CollectionAssert.AreEqual(new[] { "a", "b" }, loaded.Select(s => s.Id).ToArray(),
            "The batch must dedupe ids and stop at the count budget.");
    }

    [TestMethod]
    public void BoundedBatch_StopsBeforeExceedingByteBudget()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreSkill(db, "a", "A", new string('x', 60));
        StoreSkill(db, "b", "B", new string('y', 60));

        var budget = new AssetLoadBudget(MaxCount: 100, MaxBytes: 100);
        var accumulator = new BoundedAssetAccumulator(budget);
        var loaded = new List<Skill>();
        foreach (var id in new[] { "a", "b" })
        {
            if (accumulator.IsFull) break;
            if (!accumulator.ShouldLoad(id)) continue;
            var full = db.GetFullAsset(AssetKind.Skill, id);
            if (full is null) continue;
            var skill = AssetPersistenceModels.ToSkill(full.Value.Summary, full.Value.Content);
            if (!accumulator.TryAccept(Encoding.UTF8.GetByteCount(skill.Instructions))) break;
            loaded.Add(skill);
        }

        Assert.AreEqual(1, loaded.Count, "The second body would exceed MaxBytes and must be refused.");
        Assert.IsTrue(accumulator.Bytes <= budget.MaxBytes);
    }

    // ------------------------------------------------------------------

    private static void StoreSkill(
        MeshDb db, string id, string name, string instructions, int version = 1, string visibility = "private")
    {
        var skill = new Skill { Id = id, Name = name, Instructions = instructions, Visibility = visibility };
        var (record, content) = AssetPersistenceModels.ToRecord(skill, DeviceId, localOnly: false, version);
        db.UpsertAsset(record, content);
    }
}
