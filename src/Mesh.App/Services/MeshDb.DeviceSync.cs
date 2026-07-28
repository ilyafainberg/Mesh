using System.Text.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshDb
{
    public bool SaveDeviceSyncSnapshotManifest(DeviceSyncSnapshotManifest manifest)
    {
        DeviceSyncSnapshotProtocol.ValidateManifest(manifest);
        using var transaction = conn.BeginTransaction();
        using (var removeChunks = conn.CreateCommand())
        {
            removeChunks.Transaction = transaction;
            removeChunks.CommandText = """
                DELETE FROM device_sync_snapshot_chunks
                WHERE snapshot_id IN (
                    SELECT snapshot_id
                    FROM device_sync_snapshot_manifests
                    WHERE source_device_id = $source AND snapshot_id <> $snapshot);
                """;
            removeChunks.Parameters.AddWithValue("$source", manifest.SourceDeviceId);
            removeChunks.Parameters.AddWithValue("$snapshot", manifest.SnapshotId);
            removeChunks.ExecuteNonQuery();
        }
        using (var removeManifests = conn.CreateCommand())
        {
            removeManifests.Transaction = transaction;
            removeManifests.CommandText = """
                DELETE FROM device_sync_snapshot_manifests
                WHERE source_device_id = $source AND snapshot_id <> $snapshot;
                """;
            removeManifests.Parameters.AddWithValue("$source", manifest.SourceDeviceId);
            removeManifests.Parameters.AddWithValue("$snapshot", manifest.SnapshotId);
            removeManifests.ExecuteNonQuery();
        }
        using (var save = conn.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO device_sync_snapshot_manifests(
                    snapshot_id, source_device_id, manifest_json, created_at)
                VALUES($snapshot, $source, $manifest, $created)
                ON CONFLICT(snapshot_id) DO UPDATE SET
                    source_device_id = excluded.source_device_id,
                    manifest_json = excluded.manifest_json;
                """;
            save.Parameters.AddWithValue("$snapshot", manifest.SnapshotId);
            save.Parameters.AddWithValue("$source", manifest.SourceDeviceId);
            save.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(manifest, JsonOpts));
            save.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            save.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public bool SaveDeviceSyncSnapshotChunk(DeviceSyncSnapshotChunk chunk)
    {
        DeviceSyncSnapshotProtocol.ValidateChunk(chunk);
        using var manifestCommand = conn.CreateCommand();
        manifestCommand.CommandText = """
            SELECT manifest_json
            FROM device_sync_snapshot_manifests
            WHERE snapshot_id = $snapshot AND source_device_id = $source;
            """;
        manifestCommand.Parameters.AddWithValue("$snapshot", chunk.SnapshotId);
        manifestCommand.Parameters.AddWithValue("$source", chunk.SourceDeviceId);
        var manifestJson = manifestCommand.ExecuteScalar() as string;
        if (manifestJson is null) return false;
        var manifest = JsonSerializer.Deserialize<DeviceSyncSnapshotManifest>(manifestJson, JsonOpts);
        if (manifest is null || chunk.Index >= manifest.ChunkCount) return false;

        using var save = conn.CreateCommand();
        save.CommandText = """
            INSERT INTO device_sync_snapshot_chunks(snapshot_id, chunk_index, chunk_json)
            VALUES($snapshot, $index, $chunk)
            ON CONFLICT(snapshot_id, chunk_index) DO UPDATE SET chunk_json = excluded.chunk_json;
            """;
        save.Parameters.AddWithValue("$snapshot", chunk.SnapshotId);
        save.Parameters.AddWithValue("$index", chunk.Index);
        save.Parameters.AddWithValue("$chunk", JsonSerializer.Serialize(chunk, JsonOpts));
        save.ExecuteNonQuery();
        return true;
    }

    public DeviceSyncSnapshotTransferState? GetDeviceSyncSnapshotTransfer(string snapshotId)
    {
        using var manifestCommand = conn.CreateCommand();
        manifestCommand.CommandText = "SELECT manifest_json FROM device_sync_snapshot_manifests WHERE snapshot_id = $snapshot;";
        manifestCommand.Parameters.AddWithValue("$snapshot", snapshotId);
        var manifestJson = manifestCommand.ExecuteScalar() as string;
        if (manifestJson is null) return null;
        var manifest = JsonSerializer.Deserialize<DeviceSyncSnapshotManifest>(manifestJson, JsonOpts);
        if (manifest is null) return null;

        using var chunksCommand = conn.CreateCommand();
        chunksCommand.CommandText = """
            SELECT chunk_json
            FROM device_sync_snapshot_chunks
            WHERE snapshot_id = $snapshot
            ORDER BY chunk_index;
            """;
        chunksCommand.Parameters.AddWithValue("$snapshot", snapshotId);
        using var reader = chunksCommand.ExecuteReader();
        var chunks = new List<DeviceSyncSnapshotChunk>();
        while (reader.Read())
        {
            var chunk = JsonSerializer.Deserialize<DeviceSyncSnapshotChunk>(reader.GetString(0), JsonOpts);
            if (chunk is not null) chunks.Add(chunk);
        }
        return new DeviceSyncSnapshotTransferState(manifest, chunks);
    }

    public void DeleteDeviceSyncSnapshotTransfer(string snapshotId)
    {
        using var transaction = conn.BeginTransaction();
        using (var deleteChunks = conn.CreateCommand())
        {
            deleteChunks.Transaction = transaction;
            deleteChunks.CommandText = "DELETE FROM device_sync_snapshot_chunks WHERE snapshot_id = $id";
            deleteChunks.Parameters.AddWithValue("$id", snapshotId);
            deleteChunks.ExecuteNonQuery();
        }
        using (var deleteManifest = conn.CreateCommand())
        {
            deleteManifest.Transaction = transaction;
            deleteManifest.CommandText = "DELETE FROM device_sync_snapshot_manifests WHERE snapshot_id = $id";
            deleteManifest.Parameters.AddWithValue("$id", snapshotId);
            deleteManifest.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public DeviceSyncSnapshotResumeState GetDeviceSyncSnapshotResumeState(
        string sourceDeviceId,
        string targetDeviceId)
    {
        using var manifestCommand = conn.CreateCommand();
        manifestCommand.CommandText = """
            SELECT manifest_json
            FROM device_sync_snapshot_manifests
            WHERE source_device_id = $source
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        manifestCommand.Parameters.AddWithValue("$source", sourceDeviceId);
        var manifestJson = manifestCommand.ExecuteScalar() as string;
        if (manifestJson is not null)
        {
            var manifest = JsonSerializer.Deserialize<DeviceSyncSnapshotManifest>(manifestJson, JsonOpts);
            if (manifest is not null)
            {
                using var chunksCommand = conn.CreateCommand();
                chunksCommand.CommandText = """
                    SELECT chunk_index
                    FROM device_sync_snapshot_chunks
                    WHERE snapshot_id = $snapshot;
                    """;
                chunksCommand.Parameters.AddWithValue("$snapshot", manifest.SnapshotId);
                using var reader = chunksCommand.ExecuteReader();
                var present = new HashSet<int>();
                while (reader.Read()) present.Add(reader.GetInt32(0));
                var missing = Enumerable.Range(0, manifest.ChunkCount)
                    .Where(index => !present.Contains(index))
                    .ToArray();
                return new DeviceSyncSnapshotResumeState(manifest.SnapshotId, missing);
            }
        }

        using var receiptCommand = conn.CreateCommand();
        receiptCommand.CommandText = """
            SELECT snapshot_id
            FROM device_sync_snapshot_receipts
            WHERE source_device_id = $source AND target_device_id = $target
            ORDER BY completed_at DESC
            LIMIT 1;
            """;
        receiptCommand.Parameters.AddWithValue("$source", sourceDeviceId);
        receiptCommand.Parameters.AddWithValue("$target", targetDeviceId);
        return new DeviceSyncSnapshotResumeState(receiptCommand.ExecuteScalar() as string, []);
    }

    public bool HasDeviceSyncSnapshotCompletion(
        string snapshotId,
        string sourceDeviceId,
        string targetDeviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM device_sync_snapshot_receipts
                WHERE snapshot_id = $snapshot
                  AND source_device_id = $source
                  AND target_device_id = $target);
            """;
        cmd.Parameters.AddWithValue("$snapshot", snapshotId);
        cmd.Parameters.AddWithValue("$source", sourceDeviceId);
        cmd.Parameters.AddWithValue("$target", targetDeviceId);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }
    public void RecordDeviceSyncSnapshotCompletion(DeviceSyncSnapshotComplete completion)
    {
        using var transaction = conn.BeginTransaction();
        using (var save = conn.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO device_sync_snapshot_receipts(
                    snapshot_id, source_device_id, target_device_id, completed_at)
                VALUES($snapshot, $source, $target, $completed)
                ON CONFLICT(snapshot_id, target_device_id) DO UPDATE SET
                    source_device_id = excluded.source_device_id,
                    completed_at = excluded.completed_at;
                """;
            save.Parameters.AddWithValue("$snapshot", completion.SnapshotId);
            save.Parameters.AddWithValue("$source", completion.SourceDeviceId);
            save.Parameters.AddWithValue("$target", completion.TargetDeviceId);
            save.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
            save.ExecuteNonQuery();
        }
        using (var pruneReceipts = conn.CreateCommand())
        {
            pruneReceipts.Transaction = transaction;
            pruneReceipts.CommandText = """
                DELETE FROM device_sync_snapshot_receipts
                WHERE source_device_id = $source
                  AND target_device_id = $target
                  AND snapshot_id <> $snapshot;
                """;
            pruneReceipts.Parameters.AddWithValue("$source", completion.SourceDeviceId);
            pruneReceipts.Parameters.AddWithValue("$target", completion.TargetDeviceId);
            pruneReceipts.Parameters.AddWithValue("$snapshot", completion.SnapshotId);
            pruneReceipts.ExecuteNonQuery();
        }
        using (var removeChunks = conn.CreateCommand())
        {
            removeChunks.Transaction = transaction;
            removeChunks.CommandText = "DELETE FROM device_sync_snapshot_chunks WHERE snapshot_id = $snapshot;";
            removeChunks.Parameters.AddWithValue("$snapshot", completion.SnapshotId);
            removeChunks.ExecuteNonQuery();
        }
        using (var removeManifest = conn.CreateCommand())
        {
            removeManifest.Transaction = transaction;
            removeManifest.CommandText = "DELETE FROM device_sync_snapshot_manifests WHERE snapshot_id = $snapshot;";
            removeManifest.Parameters.AddWithValue("$snapshot", completion.SnapshotId);
            removeManifest.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
