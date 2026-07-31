using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Pure, in-memory helpers for protocol-9 online replication: cursor tracking across a
/// bounded set of origin logs, missing-range planning, batch construction under flow and
/// size bounds, and persistence-receipt construction and verification.
///
/// This type consumes only shared contracts and has no storage or MAUI dependency, so the
/// replication decision logic can be exercised deterministically in isolation from the
/// on-device database and the relay transport.
/// </summary>
public sealed class OnlineReplicationState
{
    private readonly Dictionary<string, ReplicationCursorEntry> cursors = new(StringComparer.Ordinal);

    /// <summary>Number of origin logs currently tracked.</summary>
    public int TrackedOriginCount => cursors.Count;

    /// <summary>Returns the tracked cursor for an origin, or a fresh empty cursor when untracked.</summary>
    public ReplicationCursorEntry GetCursor(string originDeviceId)
        => cursors.TryGetValue(originDeviceId, out var cursor)
            ? cursor
            : OnlineReplicationProtocol.EmptyCursor();

    /// <summary>Replaces the tracked cursor for an origin, enforcing the tracked-origin cap.</summary>
    public void SetCursor(string originDeviceId, ReplicationCursorEntry cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (!cursors.ContainsKey(originDeviceId)
            && cursors.Count >= OnlineReplicationLimits.MaxTrackedOrigins)
            throw new InvalidOperationException("The tracked-origin limit has been reached.");
        cursors[originDeviceId] = cursor;
    }

    /// <summary>
    /// Applies a single sequence to the tracked cursor for an origin and stores the result.
    /// New origins are admitted only while under the tracked-origin cap.
    /// </summary>
    public CursorApplyResult Apply(string originDeviceId, string logEpoch, ulong seq)
    {
        if (!cursors.TryGetValue(originDeviceId, out var cursor))
        {
            if (cursors.Count >= OnlineReplicationLimits.MaxTrackedOrigins)
                throw new InvalidOperationException("The tracked-origin limit has been reached.");
            cursor = OnlineReplicationProtocol.EmptyCursor();
        }
        var result = OnlineReplicationProtocol.ApplyToCursor(cursor, logEpoch, seq, out var updated);
        if (result is CursorApplyResult.AppliedContiguous or CursorApplyResult.AppliedAhead)
            cursors[originDeviceId] = updated;
        return result;
    }

    /// <summary>Applies a validated event by its log position.</summary>
    public CursorApplyResult Apply(ReplicationEvent e)
        => Apply(e.OriginDeviceId, e.LogEpoch, e.Seq);

    /// <summary>Computes the bounded missing ranges for a tracked origin against an offered upper bound.</summary>
    public IReadOnlyList<ReplicationRange> MissingRanges(string originDeviceId, ulong offeredThrough)
        => OnlineReplicationProtocol.ComputeMissingRanges(GetCursor(originDeviceId), offeredThrough);

    /// <summary>Plans replication for a tracked origin against a peer offer.</summary>
    public ReplicationRangePlan Plan(ReplicationOffer offer)
        => OnlineReplicationProtocol.PlanReplication(GetCursor(offer.OriginDeviceId), offer);

    // -----------------------------------------------------------------------
    // Batch construction.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Splits an ordered event list into transmissible batches, each within the op-count,
    /// byte-size and advertised flow bounds. Events must share the origin and epoch.
    /// </summary>
    public static IReadOnlyList<ReplicationBatch> BuildBatches(
        string originDeviceId,
        string logEpoch,
        IReadOnlyList<ReplicationEvent> events,
        ReplicationFlow flow)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(flow);
        if (!OnlineReplicationProtocol.ValidateFlow(flow, out var flowError))
            throw new ArgumentException(flowError, nameof(flow));

        var maxOps = Math.Min(flow.MaxBatchOps, OnlineReplicationLimits.MaxBatchOps);
        var maxBytes = Math.Min(flow.MaxBatchBytes, OnlineReplicationLimits.MaxBatchBytes);
        var batches = new List<ReplicationBatch>();
        var current = new List<ReplicationEvent>();
        long currentBytes = 0;
        var creditsLeft = flow.Credits;

        foreach (var e in events)
        {
            if (!string.Equals(e.OriginDeviceId, originDeviceId, StringComparison.Ordinal)
                || !string.Equals(e.LogEpoch, logEpoch, StringComparison.Ordinal))
                throw new ArgumentException("Events must share the batch origin and epoch.", nameof(events));

            var eventBytes = System.Text.Encoding.UTF8.GetByteCount(e.Ciphertext) + 256;
            var wouldOverflow = current.Count >= maxOps || currentBytes + eventBytes > maxBytes;
            if (current.Count > 0 && wouldOverflow)
            {
                if (creditsLeft == 0) break;
                batches.Add(new ReplicationBatch(originDeviceId, logEpoch, current));
                creditsLeft--;
                current = new List<ReplicationEvent>();
                currentBytes = 0;
            }
            current.Add(e);
            currentBytes += eventBytes;
        }

        if (current.Count > 0 && creditsLeft != 0)
            batches.Add(new ReplicationBatch(originDeviceId, logEpoch, current));
        return batches;
    }

    /// <summary>Selects the events matching a request's ranges from an ordered source, bounded by op count.</summary>
    public static IReadOnlyList<ReplicationEvent> SelectForRequest(
        IReadOnlyList<ReplicationEvent> orderedEvents,
        ReplicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(orderedEvents);
        if (!OnlineReplicationProtocol.ValidateRequest(request, out var error))
            throw new ArgumentException(error, nameof(request));
        var selected = new List<ReplicationEvent>();
        foreach (var e in orderedEvents)
        {
            if (!string.Equals(e.OriginDeviceId, request.OriginDeviceId, StringComparison.Ordinal)
                || !string.Equals(e.LogEpoch, request.LogEpoch, StringComparison.Ordinal))
                continue;
            foreach (var range in request.Ranges)
            {
                if (e.Seq >= range.FromSeq && e.Seq <= range.ToSeq)
                {
                    selected.Add(e);
                    break;
                }
            }
            if (selected.Count >= OnlineReplicationLimits.MaxBatchOps) break;
        }
        return selected;
    }

    // -----------------------------------------------------------------------
    // Persistence receipts.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a signed persistence receipt covering an applied batch, binding the receiver's
    /// resulting cursor and the batch contents.
    /// </summary>
    public PersistenceReceipt BuildReceipt(
        string receiverDeviceId,
        ReplicationBatch batch,
        string receiverPrivateKeyB64)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!OnlineReplicationProtocol.ValidateBatch(batch, out var error))
            throw new ArgumentException(error, nameof(batch));
        var cursor = GetCursor(batch.OriginDeviceId);
        var throughSeq = cursor.Contiguous;
        var cursorHash = OnlineReplicationProtocol.ComputeCursorHash(cursor);
        var batchHash = OnlineReplicationProtocol.ComputeBatchHash(batch);
        return OnlineReplicationProtocol.CreateReceipt(
            receiverDeviceId, batch.OriginDeviceId, batch.LogEpoch, throughSeq,
            cursorHash, batchHash, receiverPrivateKeyB64);
    }

    /// <summary>Verifies a persistence receipt against a receiver public key.</summary>
    public static bool VerifyReceipt(PersistenceReceipt receipt, string receiverPublicKeyB64)
        => OnlineReplicationProtocol.VerifyReceipt(receipt, receiverPublicKeyB64);
}
