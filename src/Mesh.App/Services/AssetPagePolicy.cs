using System;
using System.Collections.Generic;

namespace Mesh.App.Services;

/// <summary>The explicit per-asset mutation an edit resolves to (Mesh 1.17 asset model).</summary>
public enum AssetMutation { Content, Metadata, Delete }

/// <summary>The user-visible edit a management page performs on one asset, before it is classified.</summary>
public enum AssetEdit
{
    /// <summary>A brand-new asset whose full body is present.</summary>
    Create,
    /// <summary>An edit that changes the body (skill instructions, knowledge content, widget HTML).</summary>
    EditBody,
    /// <summary>Toggling a skill's Enabled flag - metadata only.</summary>
    ToggleEnabled,
    /// <summary>Changing an asset's visibility/audience - metadata only.</summary>
    ChangeVisibility,
    /// <summary>Renaming without touching the body - metadata only.</summary>
    Rename,
    /// <summary>Removing the asset.</summary>
    Delete
}

/// <summary>
/// Shared, pure Mesh 1.17 policy for the Skills/Knowledge/Widgets/Community management pages:
/// bounded paging of summary metadata, per-asset mutation classification, bounded bulk batching,
/// and the lazy-body guard. Every member is a pure function so page behaviour is unit-testable
/// without a MAUI render host or a live AppState. The AppState-coupled routing that applies a
/// classified mutation lives in the marketplace service (see <c>AssetMutations</c>).
/// </summary>
public static class AssetPagePolicy
{
    /// <summary>Rows rendered in the initial page (and added by each "Load more" step). Never render the whole corpus.</summary>
    public const int PageSize = 100;

    /// <summary>Maximum assets whose bodies are written in a single bounded bulk mutation.</summary>
    public const int BulkBatchSize = 50;

    /// <summary>The bounded slice of a summary collection to actually render.</summary>
    public static IReadOnlyList<T> Take<T>(IReadOnlyList<T> all, int visibleCount)
    {
        ArgumentNullException.ThrowIfNull(all);
        var take = Math.Clamp(visibleCount, 0, all.Count);
        var slice = new List<T>(take);
        for (var i = 0; i < take; i++) slice.Add(all[i]);
        return slice;
    }

    /// <summary>True when more summaries exist beyond the currently rendered page.</summary>
    public static bool HasMore(int total, int visibleCount) => total > Math.Max(0, visibleCount);

    /// <summary>How many summaries remain beyond the currently rendered page (never negative).</summary>
    public static int Remaining(int total, int visibleCount) => Math.Max(0, total - Math.Max(0, visibleCount));

    /// <summary>The next visible count after a "Load more" click, grown by one page and capped at the total.</summary>
    public static int NextVisible(int visibleCount, int total)
        => Math.Min(Math.Max(0, total), Math.Max(0, visibleCount) + PageSize);

    /// <summary>Splits items into bounded batches so a bulk write never materialises the whole corpus at once.</summary>
    public static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> items, int batchSize = BulkBatchSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be >= 1.");
        for (var start = 0; start < items.Count; start += batchSize)
        {
            var end = Math.Min(start + batchSize, items.Count);
            var slice = new List<T>(end - start);
            for (var i = start; i < end; i++) slice.Add(items[i]);
            yield return slice;
        }
    }

    /// <summary>
    /// Classifies a management-page edit into the mutation that must be applied. Creating or editing a
    /// body carries full content; toggling enabled, changing visibility, or a name-only rename is
    /// metadata (the stored body is preserved); a delete tombstones.
    /// </summary>
    public static AssetMutation Classify(AssetEdit edit) => edit switch
    {
        AssetEdit.Create or AssetEdit.EditBody => AssetMutation.Content,
        AssetEdit.ToggleEnabled or AssetEdit.ChangeVisibility or AssetEdit.Rename => AssetMutation.Metadata,
        AssetEdit.Delete => AssetMutation.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(edit), edit, "Unknown asset edit.")
    };

    /// <summary>
    /// The lazy-body guard: a summary carries a blank body, so an edit's body may only be persisted
    /// once the full asset has actually loaded. Returns false while the body is still unloaded, which
    /// callers use to keep Save disabled and never overwrite a real stored body with blank content.
    /// </summary>
    public static bool CanPersistBody(bool bodyLoaded) => bodyLoaded;
}
