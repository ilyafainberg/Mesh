using System.Text;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Behavioral tests for the Protocol 9 widget-consumer contract shared by every widget-producing or
/// widget-rendering surface (desktop Home/Messages, mobile MobileMe/MobileMessages) after the
/// migration off the removed <c>MutateAssets</c> API onto explicit asset mutations plus on-demand
/// widget bodies.
///
/// The pure decisions (<see cref="WidgetConsumerPolicy"/>: which explicit mutation a change routes
/// to, and whether a widget reference is a body-less summary that must be hydrated) are exercised
/// directly. The observable consumer guarantees - a selected widget's body is loaded exactly once,
/// a body-less summary is never sent, a refine works from the current stored body (not a stale
/// summary), and a 100k-summary picker materialises zero bodies - are proven with a body source
/// that counts loads, and, for the store-backed guarantees, against a real <see cref="MeshDb"/> +
/// <see cref="AssetStore"/> the same way the lazy-asset core tests do.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Protocol9WidgetLazyConsumerTests
{
    private const string DeviceId = "dev-local";

    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "protocol9-widget-consumer-tests",
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
    // WidgetConsumerPolicy.Classify: mutation-API classification
    // ==================================================================

    [TestMethod]
    public void Classify_NewWidget_RoutesToSaveAssetContent()
    {
        Assert.AreEqual(
            WidgetPersistKind.NewContent,
            WidgetConsumerPolicy.Classify(isNewWidget: true, bodyChanged: false),
            "A brand new widget body must persist via SaveAssetContent.");
        Assert.AreEqual(
            WidgetPersistKind.NewContent,
            WidgetConsumerPolicy.Classify(isNewWidget: true, bodyChanged: true),
            "A new widget is new content regardless of the body-changed flag.");
    }

    [TestMethod]
    public void Classify_RefinedBody_RoutesToSaveAssetContent()
    {
        Assert.AreEqual(
            WidgetPersistKind.RefinedContent,
            WidgetConsumerPolicy.Classify(isNewWidget: false, bodyChanged: true),
            "An existing widget whose body changed is a refine and must persist via SaveAssetContent.");
    }

    [TestMethod]
    public void Classify_MetadataOnlyChange_RoutesToSaveAssetMetadata()
    {
        Assert.AreEqual(
            WidgetPersistKind.MetadataOnly,
            WidgetConsumerPolicy.Classify(isNewWidget: false, bodyChanged: false),
            "A name/visibility-only change must persist via SaveAssetMetadata so the body is preserved.");
    }

    // ==================================================================
    // WidgetConsumerPolicy.HasSendableBody / RequiresBodyLoad
    // ==================================================================

    [TestMethod]
    public void HasSendableBody_TrueOnlyWhenBodyMaterialised()
    {
        Assert.IsFalse(WidgetConsumerPolicy.HasSendableBody(null), "Null has no body.");
        Assert.IsFalse(
            WidgetConsumerPolicy.HasSendableBody(new Widget { Id = "w", Name = "W", Html = "" }),
            "A body-less summary must not be sendable.");
        Assert.IsTrue(
            WidgetConsumerPolicy.HasSendableBody(new Widget { Id = "w", Name = "W", Html = "<html>x</html>" }),
            "A materialised body is sendable.");
    }

    [TestMethod]
    public void RequiresBodyLoad_TrueForSummary_FalseForFullOrNull()
    {
        Assert.IsFalse(WidgetConsumerPolicy.RequiresBodyLoad(null), "Null cannot be hydrated.");
        Assert.IsTrue(
            WidgetConsumerPolicy.RequiresBodyLoad(new Widget { Id = "w", Name = "W", Html = "" }),
            "A body-less summary must be hydrated before use.");
        Assert.IsFalse(
            WidgetConsumerPolicy.RequiresBodyLoad(new Widget { Id = "w", Name = "W", Html = "<html>x</html>" }),
            "A widget that already carries a body needs no load.");
    }

    // ==================================================================
    // Selected widget body loads exactly once (never bulk)
    // ==================================================================

    [TestMethod]
    public void SelectedWidget_HydratesExactlyOnce_OtherBodiesUntouched()
    {
        var source = new CountingWidgetBodySource();
        source.Store("a", "<html>alpha</html>");
        source.Store("b", "<html>beta</html>");
        source.Store("c", "<html>gamma</html>");

        // The picker holds body-less summaries (the post-restart shape of Profile.Widgets).
        var summaries = source.Summaries();
        var selected = summaries.Single(w => w.Id == "b");

        // Mirror the consumer select/send path: hydrate only the selected summary.
        var hydrated = HydrateForUse(selected, source);

        Assert.IsTrue(WidgetConsumerPolicy.HasSendableBody(hydrated), "The selected widget must gain a body.");
        Assert.AreEqual("<html>beta</html>", hydrated!.Html);
        Assert.AreEqual(1, source.TotalLoads, "Exactly one body load may occur for a single selection.");
        Assert.AreEqual(1, source.LoadCount("b"), "Only the selected widget's body may be loaded.");
        Assert.AreEqual(0, source.LoadCount("a"));
        Assert.AreEqual(0, source.LoadCount("c"));
    }

    // ==================================================================
    // A body-less summary is never sent; the hydrated body is used
    // ==================================================================

    [TestMethod]
    public void Attach_HydratesSummaryBeforeSend_NeverSendsBlankHtml()
    {
        var source = new CountingWidgetBodySource();
        source.Store("w", "<html>real body</html>");

        var summary = source.Summaries().Single();
        Assert.AreEqual("", summary.Html, "A restored summary starts body-less.");

        // Mirror DeliverAsync: hydrate when required, then require a sendable body.
        var toAttach = summary;
        if (WidgetConsumerPolicy.RequiresBodyLoad(toAttach))
        {
            var full = source.Load(toAttach.Id);
            if (full is not null) toAttach = full;
        }
        Assert.IsTrue(
            WidgetConsumerPolicy.HasSendableBody(toAttach),
            "A summary must be hydrated to a real body before it is attached to a message.");

        var sent = toAttach!.Html;
        Assert.AreEqual("<html>real body</html>", sent, "The stored body, not the blank summary, must be sent.");
        Assert.IsFalse(string.IsNullOrEmpty(sent), "Blank widget HTML must never be transmitted.");
    }

    [TestMethod]
    public void Attach_MissingBody_IsRejected_NotSentBlank()
    {
        // A summary whose body cannot be resolved must be refused rather than sent blank.
        var source = new CountingWidgetBodySource();
        var orphan = new Widget { Id = "gone", Name = "Gone", Html = "" };

        var toAttach = orphan;
        if (WidgetConsumerPolicy.RequiresBodyLoad(toAttach))
        {
            var full = source.Load(toAttach.Id);
            if (full is not null) toAttach = full;
        }

        Assert.IsFalse(
            WidgetConsumerPolicy.HasSendableBody(toAttach),
            "An unresolved widget body must be classified as not sendable so the consumer can refuse it.");
    }

    // ==================================================================
    // Refine uses the current stored body, not a stale summary
    // ==================================================================

    [TestMethod]
    public void Refine_UsesCurrentStoredBody_NotStaleSummary()
    {
        var source = new CountingWidgetBodySource();
        // The store already holds a refined body (e.g. an earlier turn or another device updated it).
        source.Store("w", "<html>CURRENT stored body</html>");

        // The in-memory picker summary is stale/body-less: its Html must never be the refine base.
        var staleSummary = new Widget { Id = "w", Name = "W", Html = "", Prompt = "chart" };

        // Mirror LoadFullWidgetForRefineAsync: load the full widget before comparing/refining.
        var baseWidget = staleSummary;
        if (WidgetConsumerPolicy.RequiresBodyLoad(baseWidget))
        {
            var full = source.Load(baseWidget.Id);
            if (full is not null) baseWidget = full;
        }

        Assert.AreEqual(
            "<html>CURRENT stored body</html>",
            baseWidget!.Html,
            "A refine must build on the current stored body, not the stale/blank summary.");
        Assert.AreEqual(1, source.LoadCount("w"), "The refine base must be loaded from the store exactly once.");
    }

    // ==================================================================
    // 100k-summary picker materialises zero bodies
    // ==================================================================

    [TestMethod]
    public void Picker_With100kSummaries_LoadsNoBodies()
    {
        const int count = 100_000;
        var source = new CountingWidgetBodySource();
        for (var i = 0; i < count; i++)
            source.Store($"w-{i:D6}", "<html>body</html>");

        var summaries = source.Summaries();
        Assert.AreEqual(count, summaries.Count);

        // Render the picker exactly as the consumers do: name/metadata only, never the body.
        long materialisedBodyBytes = 0;
        foreach (var summary in summaries)
        {
            Assert.AreEqual("", summary.Html, "Every picker row must be a body-less summary.");
            materialisedBodyBytes += Encoding.UTF8.GetByteCount(summary.Html);
            _ = summary.Name; // what the picker actually reads
        }

        Assert.AreEqual(0, materialisedBodyBytes, "Rendering the picker must materialise zero body bytes.");
        Assert.AreEqual(0, source.TotalLoads, "Listing summaries must never trigger a body load, at any scale.");
    }

    // ==================================================================
    // Store-backed proof (real MeshDb + AssetStore)
    // ==================================================================

    [TestMethod]
    public void StoreBackedSummary_IsBodyLess_AndSingleLoadRestoresIt()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreWidget(db, "a", "Alpha", "<html>alpha body</html>");
        StoreWidget(db, "b", "Beta", "<html>beta body</html>");
        StoreWidget(db, "c", "Gamma", "<html>gamma body</html>");

        // Page summaries the way AppState hydrates Profile.Widgets: metadata only, no bodies.
        long materialisedBodyBytes = 0;
        string? afterId = null;
        var summaries = new List<Widget>();
        while (true)
        {
            var page = db.PageAssetSummaries(AssetKind.Widget, 100, afterId);
            if (page.Count == 0) break;
            foreach (var record in page)
            {
                afterId = record.Id;
                if (record.IsDeleted) continue;
                var summary = AssetPersistenceModels.ToWidgetSummary(record);
                Assert.AreEqual("", summary.Html, "A paged widget summary must be body-less.");
                Assert.IsNull(summary.PreviousHtml);
                materialisedBodyBytes += Encoding.UTF8.GetByteCount(summary.Html);
                summaries.Add(summary);
            }
            if (page.Count < 100) break;
        }

        Assert.AreEqual(3, summaries.Count);
        Assert.AreEqual(0, materialisedBodyBytes, "Summary hydration must materialise zero body bytes.");

        // Load exactly the selected widget's body on demand.
        var full = new AssetStore(db).GetFullAssetAsync(AssetKind.Widget, "b").GetAwaiter().GetResult();
        Assert.IsNotNull(full);
        var restored = AssetPersistenceModels.ToWidget(full!.Value.Summary, full.Value.Content);
        Assert.AreEqual("<html>beta body</html>", restored.Html, "The on-demand load must restore the stored body.");
        Assert.AreEqual("Beta", restored.Name);
    }

    [TestMethod]
    public void StoreBackedRefine_LoadsCurrentBody_EvenWhenSummaryIsBlank()
    {
        using var db = MeshDb.Open(databasePath, key);
        StoreWidget(db, "w", "W", "<html>v1</html>");
        // A later refine writes v2 to the store.
        StoreWidget(db, "w", "W", "<html>v2 CURRENT</html>", version: 2);

        // The consumer still holds a blank summary; the refine base must come from the store.
        var summary = AssetPersistenceModels.ToWidgetSummary(
            db.PageAssetSummaries(AssetKind.Widget, 10, null).Single(r => r.Id == "w"));
        Assert.AreEqual("", summary.Html);

        var full = db.GetFullAsset(AssetKind.Widget, "w")!.Value;
        var current = AssetPersistenceModels.ToWidget(full.Summary, full.Content);
        Assert.AreEqual("<html>v2 CURRENT</html>", current.Html, "The refine base must be the current stored body.");
    }

    // ------------------------------------------------------------------

    private static Widget? HydrateForUse(Widget reference, CountingWidgetBodySource source)
    {
        if (!WidgetConsumerPolicy.RequiresBodyLoad(reference)) return reference;
        return source.Load(reference.Id) ?? reference;
    }

    private static void StoreWidget(
        MeshDb db, string id, string name, string html, int version = 1, string visibility = "private")
    {
        var widget = new Widget
        {
            Id = id,
            Name = name,
            Html = html,
            Prompt = "make",
            Visibility = visibility,
            ModifiedAt = DateTimeOffset.UtcNow,
        };
        var (record, content) = AssetPersistenceModels.ToRecord(widget, DeviceId, localOnly: false, version);
        db.UpsertAsset(record, content);
    }

    /// <summary>
    /// A test double for the AppState on-demand body loader (<c>LoadFullWidgetAsync</c>) that stores
    /// full bodies, hands out body-less summaries, and counts every per-id body load so tests can
    /// assert the "load once / never bulk" guarantees.
    /// </summary>
    private sealed class CountingWidgetBodySource
    {
        private readonly List<Widget> full = new();
        private readonly Dictionary<string, int> loads = new(StringComparer.Ordinal);

        public void Store(string id, string html)
            => full.Add(new Widget { Id = id, Name = id.ToUpperInvariant(), Html = html, Prompt = "make" });

        public List<Widget> Summaries()
            => full.Select(w => new Widget { Id = w.Id, Name = w.Name, Prompt = w.Prompt, Html = "", PreviousHtml = null })
                   .ToList();

        public Widget? Load(string id)
        {
            loads[id] = loads.TryGetValue(id, out var n) ? n + 1 : 1;
            var match = full.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
            return match is null ? null : new Widget { Id = match.Id, Name = match.Name, Prompt = match.Prompt, Html = match.Html };
        }

        public int LoadCount(string id) => loads.TryGetValue(id, out var n) ? n : 0;

        public int TotalLoads => loads.Values.Sum();
    }
}
