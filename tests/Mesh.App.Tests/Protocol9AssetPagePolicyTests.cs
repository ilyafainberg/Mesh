using System;
using System.Collections.Generic;
using System.Linq;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Pure-function tests for the Protocol 9 <see cref="AssetPagePolicy"/> that backs the
/// Skills/Knowledge/Widgets/Community management pages and the Skill marketplace service.
///
/// These exercise the four migration guarantees without a MAUI render host or a live
/// <c>AppState</c> (which resolves a process-wide storage root):
///   * bounded paging of summary metadata (never materialise the whole corpus),
///   * correct classification of a management edit into an explicit per-asset mutation,
///   * the lazy-body guard that keeps a blank summary from overwriting a stored body,
///   * bounded bulk batching for marketplace import/update and sync.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Protocol9AssetPagePolicyTests
{
    // ---- Page-size / batch-size contract -------------------------------------------------

    [TestMethod]
    public void PageSize_Is_100_And_Bounds_The_Initial_Render()
    {
        Assert.AreEqual(100, AssetPagePolicy.PageSize, "Initial page and each Load more step must render at most 100 rows.");
    }

    [TestMethod]
    public void BulkBatchSize_Is_50_And_Bounds_Bulk_Writes()
    {
        Assert.AreEqual(50, AssetPagePolicy.BulkBatchSize, "Bulk content writes must be chunked at 50 assets.");
        Assert.IsTrue(AssetPagePolicy.BulkBatchSize <= AssetPagePolicy.PageSize, "A bulk batch must not exceed a page.");
    }

    // ---- Take: bounded slicing + clamping ------------------------------------------------

    [TestMethod]
    public void Take_Returns_Only_The_Visible_Prefix()
    {
        var all = Enumerable.Range(0, 100_000).ToList();

        var page = AssetPagePolicy.Take(all, AssetPagePolicy.PageSize);

        Assert.AreEqual(100, page.Count, "A 100k corpus must never render more than one page of DOM rows.");
        Assert.AreEqual(0, page[0]);
        Assert.AreEqual(99, page[^1]);
    }

    [TestMethod]
    public void Take_Clamps_Above_Total_And_Below_Zero()
    {
        var all = Enumerable.Range(0, 7).ToList();

        Assert.AreEqual(7, AssetPagePolicy.Take(all, 999).Count, "visibleCount above total clamps to total.");
        Assert.AreEqual(0, AssetPagePolicy.Take(all, -5).Count, "negative visibleCount clamps to zero.");
        Assert.AreEqual(0, AssetPagePolicy.Take(new List<int>(), 100).Count, "empty corpus yields empty page.");
    }

    [TestMethod]
    public void Take_Null_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => AssetPagePolicy.Take<int>(null!, 10));
    }

    // ---- HasMore / Remaining -------------------------------------------------------------

    [TestMethod]
    public void HasMore_And_Remaining_Track_The_Unrendered_Tail()
    {
        Assert.IsTrue(AssetPagePolicy.HasMore(250, 100));
        Assert.AreEqual(150, AssetPagePolicy.Remaining(250, 100));

        Assert.IsFalse(AssetPagePolicy.HasMore(100, 100), "Exactly one page shows no Load more.");
        Assert.AreEqual(0, AssetPagePolicy.Remaining(100, 100));

        Assert.IsFalse(AssetPagePolicy.HasMore(100, 999), "Over-shown never reports more.");
        Assert.AreEqual(0, AssetPagePolicy.Remaining(100, 999), "Remaining is never negative.");
    }

    // ---- NextVisible: grows one page, caps at total --------------------------------------

    [TestMethod]
    public void NextVisible_Grows_By_One_Page_Then_Caps_At_Total()
    {
        var total = 250;
        var visible = AssetPagePolicy.PageSize; // 100

        visible = AssetPagePolicy.NextVisible(visible, total);
        Assert.AreEqual(200, visible);

        visible = AssetPagePolicy.NextVisible(visible, total);
        Assert.AreEqual(250, visible, "Growth is capped at the total, never past it.");

        visible = AssetPagePolicy.NextVisible(visible, total);
        Assert.AreEqual(250, visible, "Once fully shown, Load more is a no-op.");
    }

    // ---- Chunk: bounded bulk batching ----------------------------------------------------

    [TestMethod]
    public void Chunk_Splits_Into_Bounded_Batches()
    {
        var items = Enumerable.Range(0, 120).ToList();

        var batches = AssetPagePolicy.Chunk(items).ToList();

        Assert.AreEqual(3, batches.Count, "120 items at batch size 50 -> 50 + 50 + 20.");
        Assert.AreEqual(50, batches[0].Count);
        Assert.AreEqual(50, batches[1].Count);
        Assert.AreEqual(20, batches[2].Count);
        Assert.IsTrue(batches.All(b => b.Count <= AssetPagePolicy.BulkBatchSize), "No batch exceeds the bulk bound.");

        CollectionAssert.AreEqual(items, batches.SelectMany(b => b).ToList(), "Chunking preserves order and loses nothing.");
    }

    [TestMethod]
    public void Chunk_Empty_Yields_No_Batches()
    {
        Assert.AreEqual(0, AssetPagePolicy.Chunk(new List<int>()).Count());
    }

    [TestMethod]
    public void Chunk_Exact_Multiple_Has_No_Trailing_Partial()
    {
        var items = Enumerable.Range(0, 100).ToList();

        var batches = AssetPagePolicy.Chunk(items).ToList();

        Assert.AreEqual(2, batches.Count);
        Assert.IsTrue(batches.All(b => b.Count == 50));
    }

    [TestMethod]
    public void Chunk_Custom_BatchSize_Is_Honoured()
    {
        var items = Enumerable.Range(0, 10).ToList();

        var batches = AssetPagePolicy.Chunk(items, 4).ToList();

        Assert.AreEqual(3, batches.Count, "10 items at batch size 4 -> 4 + 4 + 2.");
        Assert.AreEqual(4, batches[0].Count);
        Assert.AreEqual(2, batches[2].Count);
    }

    [TestMethod]
    public void Chunk_Invalid_BatchSize_Throws()
    {
        var items = Enumerable.Range(0, 3).ToList();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => AssetPagePolicy.Chunk(items, 0).ToList());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => AssetPagePolicy.Chunk(items, -1).ToList());
    }

    [TestMethod]
    public void Chunk_Null_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => AssetPagePolicy.Chunk<int>(null!).ToList());
    }

    // ---- Classify: edit -> explicit mutation ---------------------------------------------

    [TestMethod]
    public void Classify_Content_Edits_Carry_The_Body()
    {
        Assert.AreEqual(AssetMutation.Content, AssetPagePolicy.Classify(AssetEdit.Create));
        Assert.AreEqual(AssetMutation.Content, AssetPagePolicy.Classify(AssetEdit.EditBody));
    }

    [TestMethod]
    public void Classify_Metadata_Edits_Preserve_The_Stored_Body()
    {
        Assert.AreEqual(AssetMutation.Metadata, AssetPagePolicy.Classify(AssetEdit.ToggleEnabled));
        Assert.AreEqual(AssetMutation.Metadata, AssetPagePolicy.Classify(AssetEdit.ChangeVisibility));
        Assert.AreEqual(AssetMutation.Metadata, AssetPagePolicy.Classify(AssetEdit.Rename));
    }

    [TestMethod]
    public void Classify_Delete_Tombstones()
    {
        Assert.AreEqual(AssetMutation.Delete, AssetPagePolicy.Classify(AssetEdit.Delete));
    }

    [TestMethod]
    public void Classify_Covers_Every_Declared_Edit()
    {
        foreach (AssetEdit edit in Enum.GetValues<AssetEdit>())
        {
            var mutation = AssetPagePolicy.Classify(edit);
            Assert.IsTrue(Enum.IsDefined(mutation), $"{edit} classified to an undefined mutation.");
        }
    }

    // ---- CanPersistBody: the lazy-body guard ---------------------------------------------

    [TestMethod]
    public void CanPersistBody_Blocks_Save_Until_The_Full_Body_Loads()
    {
        Assert.IsFalse(AssetPagePolicy.CanPersistBody(bodyLoaded: false),
            "While a summary's body is unloaded, a body write must be blocked so blank content never overwrites a stored body.");
        Assert.IsTrue(AssetPagePolicy.CanPersistBody(bodyLoaded: true),
            "Once the full asset has loaded, a body write is permitted.");
    }
}
