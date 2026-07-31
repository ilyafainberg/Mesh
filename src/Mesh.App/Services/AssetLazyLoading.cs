namespace Mesh.App.Services;

/// <summary>
/// A thread-safe, bounded LRU cache of asset content bytes. Entries are held under a combined
/// maximum entry count and maximum total byte budget; the least-recently-used entries are evicted
/// once either budget is exceeded. Eviction only drops the cached copy - the durable body always
/// remains in the asset store, so a subsequent load simply repopulates the cache.
///
/// Byte arrays cross the cache boundary by defensive copy in both directions: <see cref="Set"/>
/// stores a clone of the supplied body and <see cref="TryGet"/> hands back a clone, so a caller can
/// never mutate a cached body (and a later mutation of the source array can never corrupt the
/// cache). Cached bodies are therefore safe to treat as immutable snapshots.
/// </summary>
public sealed class AssetContentCache
{
    private readonly int maxEntries;
    private readonly long maxBytes;
    private readonly object gate = new();
    private readonly LinkedList<Entry> lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> map = new(StringComparer.Ordinal);
    private long currentBytes;

    private sealed record Entry(string Key, byte[] Content);

    public AssetContentCache(int maxEntries, long maxBytes)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.maxEntries = maxEntries;
        this.maxBytes = maxBytes;
    }

    /// <summary>The number of bodies currently cached.</summary>
    public int Count { get { lock (gate) return map.Count; } }

    /// <summary>The total number of content bytes currently cached.</summary>
    public long CurrentBytes { get { lock (gate) return currentBytes; } }

    /// <summary>
    /// Returns a private copy of a cached body and marks it most-recently-used, or false when
    /// absent. The returned array is a defensive clone: mutating it never affects the cache.
    /// </summary>
    public bool TryGet(string key, out byte[] content)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                lru.Remove(node);
                lru.AddFirst(node);
                content = (byte[])node.Value.Content.Clone();
                return true;
            }
        }
        content = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// Inserts or replaces a body (storing a defensive clone), then evicts LRU entries until within
    /// budget. A single body larger than the whole byte budget is never cached: caching it would
    /// force eviction of every other entry and still leave the cache above budget, so instead any
    /// stale prior copy for the key is dropped and nothing is inserted.
    /// </summary>
    public void Set(string key, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (gate)
        {
            if (map.TryGetValue(key, out var existing))
            {
                currentBytes -= existing.Value.Content.LongLength;
                lru.Remove(existing);
                map.Remove(key);
            }

            // Oversized single body: refuse to cache it rather than evicting everything and
            // remaining over budget. The durable body stays in the store for the next load.
            if (content.LongLength > maxBytes) return;

            var stored = (byte[])content.Clone();
            var node = lru.AddFirst(new Entry(key, stored));
            map[key] = node;
            currentBytes += stored.LongLength;

            // Evict from the tail while over either budget, but never evict the entry just inserted.
            while ((map.Count > maxEntries || currentBytes > maxBytes) && lru.Last is { } tail
                   && !ReferenceEquals(tail, node))
            {
                lru.RemoveLast();
                map.Remove(tail.Value.Key);
                currentBytes -= tail.Value.Content.LongLength;
            }
        }
    }

    /// <summary>Drops one cached body if present.</summary>
    public void Remove(string key)
    {
        lock (gate)
        {
            if (!map.TryGetValue(key, out var node)) return;
            lru.Remove(node);
            map.Remove(key);
            currentBytes -= node.Value.Content.LongLength;
        }
    }

    /// <summary>Drops every cached body.</summary>
    public void Clear()
    {
        lock (gate)
        {
            lru.Clear();
            map.Clear();
            currentBytes = 0;
        }
    }
}

/// <summary>
/// A bound on how much content a single bounded batch load may materialise. Both limits are hard:
/// a batch stops before it would exceed either the entry count or the byte budget, so an on-demand
/// load can never accidentally hydrate the whole corpus.
/// </summary>
public readonly record struct AssetLoadBudget(int MaxCount, long MaxBytes)
{
    /// <summary>A conservative default: at most 64 bodies and 4 MiB per call.</summary>
    public static AssetLoadBudget Default => new(64, 4L * 1024 * 1024);

    /// <summary>Throws when either budget is non-positive; both must be strictly greater than zero.</summary>
    public void Validate()
    {
        if (MaxCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCount), MaxCount, "MaxCount must be greater than zero.");
        if (MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "MaxBytes must be greater than zero.");
    }
}

/// <summary>
/// The pure decision engine behind every bounded batch asset load. It deduplicates ids, caps the
/// number of bodies at <see cref="AssetLoadBudget.MaxCount"/>, and refuses any body that would push
/// the accumulated size past <see cref="AssetLoadBudget.MaxBytes"/> - so a batch always stops before
/// exceeding its byte budget and an over-budget body is never returned, even as the sole item.
///
/// Usage is streaming so the corpus is never fully materialised: for each candidate id call
/// <see cref="ShouldLoad"/> (skip when false), load exactly that one body, then call
/// <see cref="TryAccept"/> with its size; a false result means the byte budget is reached and the
/// caller stops iterating.
/// </summary>
public sealed class BoundedAssetAccumulator
{
    private readonly int maxCount;
    private readonly long maxBytes;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    public BoundedAssetAccumulator(AssetLoadBudget budget)
    {
        budget.Validate();
        maxCount = budget.MaxCount;
        maxBytes = budget.MaxBytes;
    }

    /// <summary>The number of bodies accepted so far.</summary>
    public int Count { get; private set; }

    /// <summary>The total size in bytes of the bodies accepted so far.</summary>
    public long Bytes { get; private set; }

    /// <summary>True once the count budget is reached; callers stop iterating.</summary>
    public bool IsFull => Count >= maxCount;

    /// <summary>
    /// Decides whether an id is worth loading and reserves it against duplicates. Returns false -
    /// so the caller skips it without a body read - for a blank id, an id already offered
    /// (deduplication), or when the count budget is already reached.
    /// </summary>
    public bool ShouldLoad(string id)
    {
        if (string.IsNullOrEmpty(id) || IsFull) return false;
        return seen.Add(id);
    }

    /// <summary>
    /// Accepts a loaded body of the given size when it fits the remaining byte budget. Returns false
    /// when the count budget is reached or when adding the body would exceed
    /// <see cref="AssetLoadBudget.MaxBytes"/>; in that case the caller stops - the over-budget body
    /// is not returned even if it is the first one considered.
    /// </summary>
    public bool TryAccept(long size)
    {
        if (IsFull) return false;
        if (Bytes + size > maxBytes) return false;
        Count++;
        Bytes += size;
        return true;
    }
}
