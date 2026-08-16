using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshDb
{
    // -----------------------------------------------------------------------
    // Protocol-9 online replication read helpers (serve-side origin discovery and
    // outbox introspection). These read only the foundation replication tables and
    // never touch the retired device-sync snapshot/queue state above.
    // -----------------------------------------------------------------------

    /// <summary>A replicable origin log this device can currently serve to a peer.</summary>
    public sealed record ServeableOrigin(
        string OriginDeviceId,
        string LogEpoch,
        ulong AvailableFrom,
        ulong AvailableThrough);

    /// <summary>
    /// Enumerates every origin log this device holds events for (its own local origin plus any
    /// gossiped sibling origins), with the retained available span, so it can offer them.
    /// </summary>
    public IReadOnlyList<ServeableOrigin> GetServeableOrigins()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT origin_device_id, log_epoch, MIN(seq), MAX(seq)
            FROM replication_events
            GROUP BY origin_device_id, log_epoch
            ORDER BY origin_device_id ASC;
            """;
        var result = new List<ServeableOrigin>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new ServeableOrigin(
                r.GetString(0), r.GetString(1), (ulong)r.GetInt64(2), (ulong)r.GetInt64(3)));
        return result;
    }

    /// <summary>Returns the next sequence reserved for this device's local origin.</summary>
    internal ulong GetLocalOriginNextSeq(string originDeviceId, string logEpoch)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT log_epoch, next_seq FROM replication_local_origins WHERE origin_device_id = $origin;";
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The local origin log has not been registered.");
        if (!string.Equals(reader.GetString(0), logEpoch, StringComparison.Ordinal))
            throw new InvalidOperationException("The local origin epoch changed unexpectedly.");
        var next = reader.GetInt64(1);
        if (next <= 0) throw new InvalidOperationException("The local origin sequence is invalid.");
        return (ulong)next;
    }

    /// <summary>Captures fully contiguous positions for non-local origins represented by a compact state snapshot.</summary>
    internal IReadOnlyList<ReplicationSnapshotCoverage> GetSnapshotCoverage(string localOriginDeviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT origin_device_id, log_epoch, contiguous
            FROM replication_cursors
            WHERE origin_device_id != $local AND contiguous > 0
            ORDER BY origin_device_id ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$local", localOriginDeviceId);
        cmd.Parameters.AddWithValue("$limit", OnlineReplicationLimits.MaxSnapshotCoverageOrigins);
        var result = new List<ReplicationSnapshotCoverage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new ReplicationSnapshotCoverage(
                reader.GetString(0), reader.GetString(1), (ulong)reader.GetInt64(2)));
        return result;
    }

    /// <summary>The highest sequence held for an origin log (0 when none is held).</summary>
    public ulong GetLocalOriginThrough(string originDeviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM replication_events WHERE origin_device_id = $origin;";
        cmd.Parameters.AddWithValue("$origin", originDeviceId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : (ulong)Convert.ToInt64(value);
    }

    /// <summary>The current outbox state of one event toward one target account, or null when absent.</summary>
    public string? GetOutboxState(string eventId, string targetAccount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM replication_outbox WHERE event_id = $eid AND target_account = $account;";
        cmd.Parameters.AddWithValue("$eid", eventId);
        cmd.Parameters.AddWithValue("$account", targetAccount);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }
}
