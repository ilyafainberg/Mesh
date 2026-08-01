namespace Mesh.Relay.Hub;

using System.Collections.Concurrent;

/// <summary>
/// A bounded, in-memory, time-limited ring of recently seen frame ids so the relay drops an
/// accidental duplicate submission without re-forwarding it. It holds ONLY frame ids (never bodies,
/// ciphertext or delivery results) for at most <see cref="Ttl"/>, and is capped at
/// <see cref="MaxEntries"/> so it can never grow unbounded. This is transient de-duplication, not a
/// queue or a receipt store: nothing here is persisted and it is safe to lose on restart.
/// </summary>
public sealed class RelayFrameDedup
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private const int MaxEntries = 200_000;

    private sealed class Entry(long seenAtTicks)
    {
        public long SeenAtTicks { get; } = seenAtTicks;
        public int State;
    }

    public enum Admission { Acquired, InFlight, Delivered }

    private readonly ConcurrentDictionary<string, Entry> seen = new(StringComparer.Ordinal);
    private long lastPruneTicks;

    /// <summary>
    /// Reserves a frame id for one forwarding attempt. Delivered duplicates replay success; a
    /// duplicate while the first attempt is still running must retry rather than assume delivery.
    /// </summary>
    public Admission TryBegin(string frameId)
    {
        if (string.IsNullOrEmpty(frameId)) return Admission.Acquired;

        var now = DateTimeOffset.UtcNow;
        MaybePrune(now);

        var nowTicks = now.UtcTicks;
        while (true)
        {
            if (!seen.TryGetValue(frameId, out var existing))
            {
                if (seen.TryAdd(frameId, new Entry(nowTicks)))
                    return Admission.Acquired;
                continue;
            }

            if (nowTicks - existing.SeenAtTicks > Ttl.Ticks)
            {
                if (seen.TryUpdate(frameId, new Entry(nowTicks), existing))
                    return Admission.Acquired;
                continue;
            }

            return Volatile.Read(ref existing.State) == 1
                ? Admission.Delivered
                : Admission.InFlight;
        }
    }

    public void Commit(string frameId)
    {
        if (seen.TryGetValue(frameId, out var entry))
            Interlocked.Exchange(ref entry.State, 1);
    }

    public void Release(string frameId)
        => seen.TryRemove(frameId, out _);

    private void MaybePrune(DateTimeOffset now)
    {
        var nowTicks = now.UtcTicks;
        var last = Interlocked.Read(ref lastPruneTicks);
        var due = now.UtcTicks - last > TimeSpan.FromSeconds(10).Ticks;
        if (!due && seen.Count < MaxEntries) return;
        if (Interlocked.CompareExchange(ref lastPruneTicks, nowTicks, last) != last) return;

        foreach (var kv in seen)
        {
            if (nowTicks - kv.Value.SeenAtTicks > Ttl.Ticks)
                seen.TryRemove(kv.Key, out _);
        }

        // Hard cap: if still over budget, drop oldest entries.
        if (seen.Count > MaxEntries)
        {
            foreach (var kv in seen.OrderBy(k => k.Value.SeenAtTicks).Take(seen.Count - MaxEntries))
                seen.TryRemove(kv.Key, out _);
        }
    }
}
