using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Behavioral, bounded-memory tests for the Mesh 1.17 capability-asset migration and the
/// asynchronous, SQLCipher-backed Skill/Knowledge/Widget storage.
///
/// The tests exercise the owned production units directly:
///   * <see cref="AssetPersistenceModels"/> - lossless domain &lt;-&gt; <see cref="AssetRecord"/> mapping,
///   * <see cref="MeshDb.SerializeProfileForStorage"/> - the bounded profile blob (assets stripped),
///   * <see cref="MeshDb"/>/<see cref="AssetStore"/> - durable asset persistence, tombstones, outbox.
///
/// The migration/hydration algorithm mirrored here is exactly the one
/// <c>AppState.MigrateAndHydrateAssets</c> performs (idempotent upsert-if-absent, rewrite the
/// bounded blob only after every asset is durable, then hydrate from the tables), so the tests
/// verify the same observable guarantees without constructing the full MAUI <c>AppState</c> graph
/// (which resolves a process-wide storage root).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Mesh117AssetMigrationTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string DeviceId = "dev-local";

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "mesh-117-asset-migration-tests",
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

    // ------------------------------------------------------------------
    // Lossless content + metadata round trip for every kind
    // ------------------------------------------------------------------

    [TestMethod]
    public void Mapping_Skill_RoundTripsLosslessly()
    {
        var skill = new Skill
        {
            Id = "skill-1",
            Name = "Trip Planner",
            Description = "Plans trips.",
            Instructions = "Follow these steps: \u2705 pack, then go.",
            Visibility = "circle",
            Enabled = false,
            SourceMarketplaceId = "mkt-7",
            SourceSkillId = "remote-42",
            Version = "1.2.3"
        };

        var (record, content) = AssetPersistenceModels.ToRecord(skill, DeviceId, localOnly: false, version: 1);
        var restored = AssetPersistenceModels.ToSkill(record, content);

        Assert.AreEqual(skill.Id, restored.Id);
        Assert.AreEqual(skill.Name, restored.Name);
        Assert.AreEqual(skill.Description, restored.Description);
        Assert.AreEqual(skill.Instructions, restored.Instructions);
        Assert.AreEqual(skill.Visibility, restored.Visibility);
        Assert.AreEqual(skill.Enabled, restored.Enabled);
        Assert.AreEqual(skill.SourceMarketplaceId, restored.SourceMarketplaceId);
        Assert.AreEqual(skill.SourceSkillId, restored.SourceSkillId);
        Assert.AreEqual(skill.Version, restored.Version);
        Assert.AreEqual(AssetKind.Skill, record.Kind);
    }

    [TestMethod]
    public void Mapping_Knowledge_RoundTripsLosslessly()
    {
        var item = new KnowledgeItem
        {
            Id = "kb-1",
            Title = "Onboarding notes",
            Content = "Line one\nLine two, with an accent: caf\u00e9.",
            Visibility = "public",
            Source = KnowledgeSource.File,
            SourceRef = "C:/docs/notes.md",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 8, 30, 0, TimeSpan.Zero)
        };

        var (record, content) = AssetPersistenceModels.ToRecord(item, DeviceId, localOnly: false, version: 1);
        var restored = AssetPersistenceModels.ToKnowledge(record, content);

        Assert.AreEqual(item.Id, restored.Id);
        Assert.AreEqual(item.Title, restored.Title);
        Assert.AreEqual(item.Content, restored.Content);
        Assert.AreEqual(item.Visibility, restored.Visibility);
        Assert.AreEqual(item.Source, restored.Source);
        Assert.AreEqual(item.SourceRef, restored.SourceRef);
        Assert.AreEqual(item.UpdatedAt, restored.UpdatedAt);
        Assert.AreEqual(AssetKind.Knowledge, record.Kind);
    }

    [TestMethod]
    public void Mapping_Widget_RoundTripsLosslessly()
    {
        var widget = new Widget
        {
            Id = "w-1",
            Name = "Countdown",
            Prompt = "A countdown timer to new year.",
            Html = "<html><body><h1>Tick</h1></body></html>",
            Visibility = "circle",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 2, 2, 12, 0, 0, TimeSpan.Zero),
            PreviousHtml = "<html>old</html>",
            PreviousPrompt = "old prompt"
        };

        var (record, content) = AssetPersistenceModels.ToRecord(widget, DeviceId, localOnly: false, version: 1);
        var restored = AssetPersistenceModels.ToWidget(record, content);

        Assert.AreEqual(widget.Id, restored.Id);
        Assert.AreEqual(widget.Name, restored.Name);
        Assert.AreEqual(widget.Prompt, restored.Prompt);
        Assert.AreEqual(widget.Html, restored.Html);
        Assert.AreEqual(widget.Visibility, restored.Visibility);
        Assert.AreEqual(widget.CreatedAt, restored.CreatedAt);
        Assert.AreEqual(widget.ModifiedAt, restored.ModifiedAt);
        Assert.AreEqual(widget.PreviousHtml, restored.PreviousHtml);
        Assert.AreEqual(widget.PreviousPrompt, restored.PreviousPrompt);
        Assert.AreEqual(AssetKind.Widget, record.Kind);
    }

    [TestMethod]
    public void Mapping_BlankName_FallsBackToId_ButRestoresOriginalBlank()
    {
        var skill = new Skill { Id = "skill-blank", Name = "", Instructions = "do it" };
        var (record, content) = AssetPersistenceModels.ToRecord(skill, DeviceId, localOnly: false, version: 1);

        Assert.AreEqual("skill-blank", record.Name, "The record name must never be blank (upsert requires it).");

        var restored = AssetPersistenceModels.ToSkill(record, content);
        Assert.AreEqual("", restored.Name, "Hydration must restore the original (blank) display name.");
    }

    // ------------------------------------------------------------------
    // Bounded profile blob (assets stripped) at scale
    // ------------------------------------------------------------------

    [TestMethod]
    public void Serialize_StripsAssets_BlobIsBounded_At10k()
        => AssertBoundedBlob(10_000);

    [TestMethod]
    public void Serialize_StripsAssets_BlobIsBounded_At100k()
        => AssertBoundedBlob(100_000);

    private static void AssertBoundedBlob(int count)
    {
        var profile = new MeshProfile { Handle = "me", DisplayName = "Me", PublicKey = "pk" };
        for (var i = 0; i < count; i++)
        {
            profile.Skills.Add(new Skill
            {
                Id = $"skill-{i:D6}",
                Name = $"Skill {i}",
                Instructions = new string('x', 512) // large content that must NOT land in the blob
            });
            profile.Knowledge.Add(new KnowledgeItem { Id = $"kb-{i:D6}", Content = new string('y', 512) });
            profile.Widgets.Add(new Widget { Id = $"w-{i:D6}", Html = new string('z', 512) });
        }

        var blob = MeshDb.SerializeProfileForStorage(profile);

        // The blob must be a tiny, fixed-shape document regardless of how many assets exist.
        Assert.IsFalse(blob.Contains("xxxxxxxxxx"), "Skill instructions must not be embedded in the profile blob.");
        Assert.IsFalse(blob.Contains("yyyyyyyyyy"), "Knowledge content must not be embedded in the profile blob.");
        Assert.IsFalse(blob.Contains("zzzzzzzzzz"), "Widget HTML must not be embedded in the profile blob.");
        Assert.IsFalse(blob.Contains("skill-000001"), "Asset ids must not be embedded in the profile blob.");
        Assert.IsTrue(
            blob.Length < 4096,
            $"Profile blob must stay bounded regardless of asset count; was {blob.Length} bytes for {count} assets.");
    }

    // ------------------------------------------------------------------
    // Migration: success, bounded blob, hydration
    // ------------------------------------------------------------------

    [TestMethod]
    public void Migration_MovesLegacyAssetsToTables_AndLeavesBoundedBlob()
    {
        WriteLegacyBlob(SampleProfile());

        using (var db = MeshDb.Open(databasePath, key))
        {
            var loaded = db.LoadProfile()!;
            Assert.AreEqual(2, loaded.Skills.Count, "Legacy blob must still deserialize its embedded skills.");
            Assert.AreEqual(1, loaded.Knowledge.Count);
            Assert.AreEqual(1, loaded.Widgets.Count);

            Migrate(db, loaded, localOnly: false);

            // The rewritten blob is bounded: reloading yields no embedded assets.
            var reloaded = db.LoadProfile()!;
            Assert.AreEqual(0, reloaded.Skills.Count);
            Assert.AreEqual(0, reloaded.Knowledge.Count);
            Assert.AreEqual(0, reloaded.Widgets.Count);

            // Everything is durable in the asset tables and hydrates back losslessly.
            var hydrated = Hydrate(db);
            Assert.AreEqual(2, hydrated.Skills.Count);
            Assert.AreEqual(1, hydrated.Knowledge.Count);
            Assert.AreEqual(1, hydrated.Widgets.Count);
            Assert.AreEqual("Follow the plan.", hydrated.Skills.Single(s => s.Id == "s-1").Instructions);
            Assert.AreEqual("Some grounding text.", hydrated.Knowledge.Single().Content);
            Assert.AreEqual("<html>widget</html>", hydrated.Widgets.Single().Html);
        }
    }

    [TestMethod]
    public void Migration_IsIdempotent_AcrossReopens()
    {
        WriteLegacyBlob(SampleProfile());

        using (var db = MeshDb.Open(databasePath, key))
            Migrate(db, db.LoadProfile()!, localOnly: false);

        // Reopen and run migration again over the now-bounded profile: nothing to migrate,
        // no versions change, and hydration is stable.
        int skillVersionAfterFirst;
        using (var db = MeshDb.Open(databasePath, key))
        {
            skillVersionAfterFirst = db.GetFullAsset(AssetKind.Skill, "s-1")!.Value.Summary.Version;
            Migrate(db, db.LoadProfile()!, localOnly: false);
        }

        using (var db = MeshDb.Open(databasePath, key))
        {
            var hydrated = Hydrate(db);
            Assert.AreEqual(2, hydrated.Skills.Count, "Idempotent migration must not duplicate assets.");
            Assert.AreEqual(1, hydrated.Knowledge.Count);
            Assert.AreEqual(1, hydrated.Widgets.Count);
            Assert.AreEqual(
                skillVersionAfterFirst,
                db.GetFullAsset(AssetKind.Skill, "s-1")!.Value.Summary.Version,
                "Re-running migration must not bump asset versions.");
        }
    }

    [TestMethod]
    public void Migration_RollsBackOnFailure_LeavesLegacyBlob_ThenRecovers()
    {
        WriteLegacyBlob(SampleProfile());

        using (var db = MeshDb.Open(databasePath, key))
        {
            var loaded = db.LoadProfile()!;

            // Simulate a mid-migration failure: the store rejects a corrupt (hash-mismatched) record
            // before the profile blob is rewritten.
            var (badRecord, content) = AssetPersistenceModels.ToRecord(
                loaded.Skills[0], DeviceId, localOnly: false, version: 1);
            var corrupt = badRecord with { ContentHash = "deadbeef" };

            Assert.ThrowsException<InvalidOperationException>(() =>
                db.UpsertAsset(corrupt, content, createOutboxEntry: false));

            // Because the rewrite only happens after every asset is durable, the legacy blob is intact.
            var afterFailure = db.LoadProfile()!;
            Assert.AreEqual(2, afterFailure.Skills.Count, "A failed migration must leave the legacy blob untouched.");
        }

        // A later open retries safely and completes.
        using (var db = MeshDb.Open(databasePath, key))
        {
            Migrate(db, db.LoadProfile()!, localOnly: false);
            var reloaded = db.LoadProfile()!;
            Assert.AreEqual(0, reloaded.Skills.Count, "Retry must produce the bounded blob.");
            Assert.AreEqual(2, Hydrate(db).Skills.Count, "Retry must migrate every asset.");
        }
    }

    // ------------------------------------------------------------------
    // Platform policy: mobile LocalOnly, desktop outbox
    // ------------------------------------------------------------------

    [TestMethod]
    public void Mobile_Migration_MarksAssetsLocalOnly_AndProducesNoOutbox()
    {
        WriteLegacyBlob(SampleProfile());

        using var db = MeshDb.Open(databasePath, key);
        Migrate(db, db.LoadProfile()!, localOnly: true);

        var store = new AssetStore(db);
        Assert.AreEqual(0, store.ListOutboxAsync(100).GetAwaiter().GetResult().Count,
            "Mobile (LocalOnly) assets must never enter the sync outbox.");
        Assert.IsTrue(
            db.GetFullAsset(AssetKind.Skill, "s-1")!.Value.Summary.LocalOnly,
            "Mobile assets must be persisted as LocalOnly.");
    }

    [TestMethod]
    public void Desktop_Migration_CreatesOutboxEntriesPerAsset()
    {
        WriteLegacyBlob(SampleProfile());

        using var db = MeshDb.Open(databasePath, key);
        Migrate(db, db.LoadProfile()!, localOnly: false);

        var store = new AssetStore(db);
        var outbox = store.ListOutboxAsync(100).GetAwaiter().GetResult();
        Assert.AreEqual(4, outbox.Count, "Desktop migration must enqueue one outbox upsert per asset.");
        Assert.IsTrue(outbox.All(o => o.Operation == "upsert"));
        Assert.IsFalse(
            db.GetFullAsset(AssetKind.Skill, "s-1")!.Value.Summary.LocalOnly,
            "Desktop assets must not be LocalOnly.");
    }

    // ------------------------------------------------------------------
    // Rapid mutations: final revision persisted, no lost updates, bounded time
    // ------------------------------------------------------------------

    [TestMethod]
    public void RapidMutations_FinalRevisionIsPersisted_NoLostUpdates()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        const int iterations = 1_000;

        var sw = Stopwatch.StartNew();
        for (var i = 1; i <= iterations; i++)
        {
            var skill = new Skill { Id = "hot", Name = $"rev {i}", Instructions = $"instruction {i}" };
            var (record, content) = AssetPersistenceModels.ToRecord(skill, DeviceId, localOnly: false, version: i);
            store.UpsertAsync(record, content, createOutboxEntry: false).GetAwaiter().GetResult();
        }
        sw.Stop();

        var full = db.GetFullAsset(AssetKind.Skill, "hot")!.Value;
        Assert.AreEqual(iterations, full.Summary.Version, "The final revision must win.");
        Assert.AreEqual($"instruction {iterations}", AssetPersistenceModels.ToSkill(full.Summary, full.Content).Instructions);
        Assert.AreEqual(1, db.PageAssetSummaries(AssetKind.Skill, 500, null).Count,
            "Rapid updates to one id must collapse to a single row, not accumulate.");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"1k persisted mutations should stay well-bounded; took {sw.Elapsed}.");
    }

    [TestMethod]
    public void ProfileBlob_AsyncWritePath_PersistsLatestJson()
    {
        using var db = MeshDb.Open(databasePath, key);
        // Mirrors the coordinator's non-sync branch, which calls SaveProfileJson with the bounded blob.
        for (var i = 0; i < 50; i++)
        {
            var profile = new MeshProfile { Handle = "me", DisplayName = $"Name {i}", PublicKey = "pk" };
            db.SaveProfileJson(MeshDb.SerializeProfileForStorage(profile));
        }

        Assert.AreEqual("Name 49", db.LoadProfile()!.DisplayName, "The last scheduled blob write must be the durable one.");
    }

    // ------------------------------------------------------------------
    // Account isolation
    // ------------------------------------------------------------------

    [TestMethod]
    public void AccountIsolation_AssetsDoNotLeakBetweenIdentities()
    {
        var secondPath = Path.Combine(directory, "profile-b.meshdb");

        using (var a = MeshDb.Open(databasePath, key))
        {
            var pa = new MeshProfile { Handle = "a", PublicKey = "pk-a" };
            pa.Skills.Add(new Skill { Id = "only-in-a", Name = "A skill", Instructions = "a" });
            WriteLegacyBlob(pa, a);
            Migrate(a, a.LoadProfile()!, localOnly: false);
        }

        using (var b = MeshDb.Open(secondPath, key))
        {
            var pb = new MeshProfile { Handle = "b", PublicKey = "pk-b" };
            pb.Skills.Add(new Skill { Id = "only-in-b", Name = "B skill", Instructions = "b" });
            WriteLegacyBlob(pb, b);
            Migrate(b, b.LoadProfile()!, localOnly: false);
        }

        using (var a = MeshDb.Open(databasePath, key))
        {
            Assert.IsNotNull(a.GetFullAsset(AssetKind.Skill, "only-in-a"));
            Assert.IsNull(a.GetFullAsset(AssetKind.Skill, "only-in-b"), "Identity A must not see identity B's assets.");
        }
        using (var b = MeshDb.Open(secondPath, key))
        {
            Assert.IsNotNull(b.GetFullAsset(AssetKind.Skill, "only-in-b"));
            Assert.IsNull(b.GetFullAsset(AssetKind.Skill, "only-in-a"), "Identity B must not see identity A's assets.");
        }
    }

    // ------------------------------------------------------------------
    // Export / import: bundle carries assets, import leaves a bounded blob
    // ------------------------------------------------------------------

    [TestMethod]
    public void ExportImport_CarriesAssets_AndImportLeavesBoundedBlob()
    {
        // A fully hydrated in-memory profile (the shape AppState holds after migration).
        var live = SampleProfile();
        live.PublicKey = "pk";
        live.PrivateKey = "sk";

        // Simulate the export bundle the way MeshExport.Create does: serialize the FULL in-memory
        // profile (assets included) with device keys blanked. This keeps the test free of the
        // Argon2 packaging dependency while still exercising the owned import/migrate/hydrate path.
        var exported = JsonSerializer.Deserialize<MeshProfile>(JsonSerializer.Serialize(live, Web), Web)!;
        exported.PublicKey = "";
        exported.PrivateKey = "";
        var opened = JsonSerializer.Deserialize<MeshProfile>(JsonSerializer.Serialize(exported, Web), Web)!;

        // The export round-trips every asset (they live in the in-memory collections, not the DB blob).
        Assert.AreEqual(2, opened.Skills.Count);
        Assert.AreEqual(1, opened.Knowledge.Count);
        Assert.AreEqual(1, opened.Widgets.Count);
        Assert.AreEqual("", opened.PrivateKey, "Device keys are never carried in an export.");

        // Import path: write the bounded blob, migrate the imported assets into the tables, hydrate.
        using var db = MeshDb.Open(databasePath, key);
        db.SaveProfile(opened);
        Migrate(db, opened, localOnly: false);

        Assert.AreEqual(0, db.LoadProfile()!.Skills.Count, "Import must leave a bounded profile blob.");
        var hydrated = Hydrate(db);
        Assert.AreEqual(2, hydrated.Skills.Count);
        Assert.AreEqual(1, hydrated.Knowledge.Count);
        Assert.AreEqual(1, hydrated.Widgets.Count);
    }

    [TestMethod]
    public void MeshExportBundle_RoundTripsFullBodiesAndSkillPackage()
    {
        var profile = SampleProfile();
        profile.PublicKey = "device-public";
        profile.PrivateKey = "device-private";
        var skill = profile.Skills[0];
        var skillBytes = Encoding.UTF8.GetBytes(skill.Instructions);
        var skillHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(skillBytes)).ToLowerInvariant();
        var packageHash = new string('a', 64);
        skill.PackageHash = packageHash;

        var bundle = new MeshExportBundle
        {
            Profile = profile,
            SkillPackages =
            [
                new MeshExportSkillPackage
                {
                    SkillId = skill.Id,
                    Manifest = new SkillPackageManifest
                    {
                        PackageHash = packageHash,
                        Compatibility = new SkillCompatibility
                        {
                            OperatingSystems = SkillOperatingSystems.Windows,
                            DeviceClass = SkillDeviceClass.Desktop
                        },
                        Files =
                        [
                            new SkillFileManifest
                            {
                                Path = "Skill.md",
                                Sha256 = skillHash,
                                Size = skillBytes.LongLength,
                                Role = SkillFileRole.SkillMarkdown
                            }
                        ]
                    },
                    Files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["Skill.md"] = skillBytes
                    }
                }
            ]
        };

        var encrypted = MeshExport.Create(bundle, "correct horse battery staple");
        var opened = MeshExport.OpenBundle(encrypted, "correct horse battery staple");

        Assert.AreEqual("", opened.Profile.PrivateKey);
        Assert.AreEqual("", opened.Profile.PublicKey);
        Assert.AreEqual(skill.Instructions, opened.Profile.Skills[0].Instructions);
        Assert.AreEqual(
            profile.Knowledge[0].Content,
            opened.Profile.Knowledge[0].Content);
        Assert.AreEqual(profile.Widgets[0].Html, opened.Profile.Widgets[0].Html);
        Assert.AreEqual(1, opened.SkillPackages.Count);
        Assert.AreEqual(skill.Id, opened.SkillPackages[0].SkillId);
        CollectionAssert.AreEqual(
            skillBytes,
            opened.SkillPackages[0].Files["Skill.md"]);
    }

    // ------------------------------------------------------------------
    // Helpers: mirror AppState.MigrateAndHydrateAssets / HydrateFromAssets
    // ------------------------------------------------------------------

    /// <summary>
    /// Faithful mirror of the production migration: idempotently upsert each embedded asset (only when
    /// absent from the tables), then rewrite the now-bounded blob once every asset is durable.
    /// </summary>
    private static void Migrate(MeshDb db, MeshProfile profile, bool localOnly)
    {
        foreach (var (kind, id, record, content) in Enumerate(profile, localOnly))
            if (db.GetFullAsset(kind, id) is null)
                db.UpsertAsset(record, content, createOutboxEntry: !localOnly);
        db.SaveProfile(profile); // bounded blob, only after every asset is durable
    }

    private static MeshProfile Hydrate(MeshDb db)
    {
        var profile = db.LoadProfile()!;
        profile.Skills = Load(db, AssetKind.Skill, AssetPersistenceModels.ToSkill);
        profile.Knowledge = Load(db, AssetKind.Knowledge, AssetPersistenceModels.ToKnowledge);
        profile.Widgets = Load(db, AssetKind.Widget, AssetPersistenceModels.ToWidget);
        return profile;
    }

    private static List<T> Load<T>(MeshDb db, AssetKind kind, Func<AssetRecord, byte[], T> map)
    {
        const int pageSize = 500;
        var list = new List<T>();
        string? afterId = null;
        while (true)
        {
            var page = db.PageAssetSummaries(kind, pageSize, afterId);
            if (page.Count == 0) break;
            foreach (var summary in page)
            {
                afterId = summary.Id;
                if (summary.IsDeleted) continue;
                var full = db.GetFullAsset(kind, summary.Id);
                if (full is null) continue;
                list.Add(map(full.Value.Summary, full.Value.Content));
            }
            if (page.Count < pageSize) break;
        }
        return list;
    }

    private static IEnumerable<(AssetKind Kind, string Id, AssetRecord Record, byte[] Content)>
        Enumerate(MeshProfile profile, bool localOnly)
    {
        foreach (var skill in profile.Skills)
        {
            var (record, content) = AssetPersistenceModels.ToRecord(skill, DeviceId, localOnly, 1);
            yield return (AssetKind.Skill, skill.Id, record, content);
        }
        foreach (var item in profile.Knowledge)
        {
            var (record, content) = AssetPersistenceModels.ToRecord(item, DeviceId, localOnly, 1);
            yield return (AssetKind.Knowledge, item.Id, record, content);
        }
        foreach (var widget in profile.Widgets)
        {
            var (record, content) = AssetPersistenceModels.ToRecord(widget, DeviceId, localOnly, 1);
            yield return (AssetKind.Widget, widget.Id, record, content);
        }
    }

    private static MeshProfile SampleProfile()
    {
        var profile = new MeshProfile { Handle = "me", DisplayName = "Me", PublicKey = "pk" };
        profile.Skills.Add(new Skill { Id = "s-1", Name = "Planner", Instructions = "Follow the plan." });
        profile.Skills.Add(new Skill
        {
            Id = "s-2",
            Name = "Reviewer",
            Instructions = "Review carefully.",
            Enabled = false,
            SourceMarketplaceId = "mkt-1",
            SourceSkillId = "r-9",
            Version = "0.9"
        });
        profile.Knowledge.Add(new KnowledgeItem
        {
            Id = "k-1",
            Title = "Notes",
            Content = "Some grounding text.",
            Source = KnowledgeSource.File,
            SourceRef = "notes.md"
        });
        profile.Widgets.Add(new Widget { Id = "w-1", Name = "Widget", Prompt = "make it", Html = "<html>widget</html>" });
        return profile;
    }

    /// <summary>Writes a pre-1.17 profile blob that still embeds its assets (the migration input).</summary>
    private void WriteLegacyBlob(MeshProfile profile)
    {
        using var db = MeshDb.Open(databasePath, key);
        WriteLegacyBlob(profile, db);
    }

    private static void WriteLegacyBlob(MeshProfile profile, MeshDb db)
        => db.SaveProfileJson(JsonSerializer.Serialize(profile, Web));
}
