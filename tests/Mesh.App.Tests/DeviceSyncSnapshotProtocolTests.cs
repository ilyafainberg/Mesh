using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceSyncSnapshotProtocolTests
{
    [TestMethod]
    public void SnapshotTransfer_IsDeterministicChunkedAndRoundTrips()
    {
        var operations = LargeOperations();

        var first = DeviceSyncSnapshotProtocol.Create("source-device", operations);
        var second = DeviceSyncSnapshotProtocol.Create("source-device", operations);
        var assembled = DeviceSyncSnapshotProtocol.Assemble(first.Manifest, first.Chunks);

        Assert.AreEqual(first.Manifest.SnapshotId, second.Manifest.SnapshotId);
        Assert.IsTrue(first.Chunks.Count > 1);
        Assert.IsTrue(first.Chunks.All(chunk => chunk.Data.Length <= DeviceSyncSnapshotProtocol.MaxChunkBytes));
        CollectionAssert.AreEqual(
            operations.Select(operation => operation.OperationId).ToArray(),
            assembled.Select(operation => operation.OperationId).ToArray());
        CollectionAssert.AreEqual(
            operations.Select(operation => operation.Payload).ToArray(),
            assembled.Select(operation => operation.Payload).ToArray());
    }

    [TestMethod]
    public void SnapshotTransfer_RejectsCorruptionAndDuplicateIndexes()
    {
        var transfer = DeviceSyncSnapshotProtocol.Create("source-device", LargeOperations());
        Assert.IsTrue(transfer.Chunks.Count > 1);
        var corruptedData = transfer.Chunks[0].Data.ToArray();
        corruptedData[0] ^= 0x5a;
        var corrupted = transfer.Chunks.ToArray();
        corrupted[0] = corrupted[0] with { Data = corruptedData };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            DeviceSyncSnapshotProtocol.Assemble(transfer.Manifest, corrupted));

        var duplicate = transfer.Chunks.ToArray();
        duplicate[1] = duplicate[0];
        Assert.ThrowsExactly<InvalidDataException>(() =>
            DeviceSyncSnapshotProtocol.Assemble(transfer.Manifest, duplicate));
    }

    [TestMethod]
    public void SnapshotTransfer_RejectsOversizedManifestAndChunk()
    {
        var hash = new string('a', 64);
        var oversizedManifest = new DeviceSyncSnapshotManifest(
            hash,
            "source-device",
            1,
            DeviceSyncSnapshotProtocol.MaxChunks + 1,
            DeviceSyncSnapshotProtocol.MaxCompressedBytes,
            hash);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            DeviceSyncSnapshotProtocol.ValidateManifest(oversizedManifest));

        var data = new byte[DeviceSyncSnapshotProtocol.MaxChunkBytes + 1];
        var chunk = new DeviceSyncSnapshotChunk(
            hash,
            "source-device",
            0,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
            data);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            DeviceSyncSnapshotProtocol.ValidateChunk(chunk));
    }
    [TestMethod]
    public void SnapshotTransfer_PersistsPartialChunksAndCompletionAcrossRestart()
    {
        var path = DatabasePath();
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var transfer = DeviceSyncSnapshotProtocol.Create("source-device", LargeOperations());
        Assert.IsTrue(transfer.Chunks.Count > 1);
        try
        {
            using (var db = MeshDb.Open(path, key))
            {
                Assert.IsTrue(db.SaveDeviceSyncSnapshotManifest(transfer.Manifest));
                Assert.IsTrue(db.SaveDeviceSyncSnapshotChunk(transfer.Chunks[0]));
            }

            using (var reopened = MeshDb.Open(path, key))
            {
                var resume = reopened.GetDeviceSyncSnapshotResumeState(
                    transfer.Manifest.SourceDeviceId,
                    "target-device");
                Assert.AreEqual(transfer.Manifest.SnapshotId, resume.SnapshotId);
                Assert.IsFalse(resume.MissingChunkIndexes.Contains(0));
                Assert.AreEqual(transfer.Manifest.ChunkCount - 1, resume.MissingChunkIndexes.Count);

                foreach (var chunk in transfer.Chunks.Skip(1))
                    Assert.IsTrue(reopened.SaveDeviceSyncSnapshotChunk(chunk));
                var persisted = reopened.GetDeviceSyncSnapshotTransfer(transfer.Manifest.SnapshotId);
                Assert.IsNotNull(persisted);
                Assert.AreEqual(
                    transfer.Manifest.OperationCount,
                    DeviceSyncSnapshotProtocol.Assemble(
                        persisted.Manifest, persisted.Chunks).Count);

                reopened.RecordDeviceSyncSnapshotCompletion(new DeviceSyncSnapshotComplete(
                    "superseded-snapshot",
                    transfer.Manifest.SourceDeviceId,
                    "target-device",
                    transfer.Manifest.OperationCount,
                    transfer.Manifest.Sha256));
                reopened.RecordDeviceSyncSnapshotCompletion(new DeviceSyncSnapshotComplete(
                    transfer.Manifest.SnapshotId,
                    transfer.Manifest.SourceDeviceId,
                    "target-device",
                    transfer.Manifest.OperationCount,
                    transfer.Manifest.Sha256));
            }

            using (var completed = MeshDb.Open(path, key))
            {
                Assert.IsNull(completed.GetDeviceSyncSnapshotTransfer(transfer.Manifest.SnapshotId));
                var completedResume = completed.GetDeviceSyncSnapshotResumeState(
                    transfer.Manifest.SourceDeviceId,
                    "target-device");
                Assert.AreEqual(transfer.Manifest.SnapshotId, completedResume.SnapshotId);
                Assert.HasCount(0, completedResume.MissingChunkIndexes);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void SnapshotTransfer_ResetClearsCorruptPartialState()
    {
        var path = DatabasePath();
        var key = Enumerable.Repeat((byte)0x39, 32).ToArray();
        var transfer = DeviceSyncSnapshotProtocol.Create("source-device", LargeOperations());
        try
        {
            using (var db = MeshDb.Open(path, key))
            {
                Assert.IsTrue(db.SaveDeviceSyncSnapshotManifest(transfer.Manifest));
                Assert.IsTrue(db.SaveDeviceSyncSnapshotChunk(transfer.Chunks[0]));
                db.DeleteDeviceSyncSnapshotTransfer(transfer.Manifest.SnapshotId);
            }

            using (var reopened = MeshDb.Open(path, key))
            {
                Assert.IsNull(reopened.GetDeviceSyncSnapshotTransfer(transfer.Manifest.SnapshotId));
                Assert.IsNull(reopened.GetDeviceSyncSnapshotResumeState(
                    transfer.Manifest.SourceDeviceId,
                    "target-device").SnapshotId);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }
    [TestMethod]
    public void InboundTopicQueueSequence_PreservesAcceptanceOrderAcrossRestart()
    {
        var path = DatabasePath();
        var key = Enumerable.Repeat((byte)0x4d, 32).ToArray();
        var acceptedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        try
        {
            using (var db = MeshDb.Open(path, key))
            {
                Assert.IsTrue(db.TryAddInboundTopicRun(Inbound("run-z", acceptedAt)));
                Assert.IsTrue(db.TryAddInboundTopicRun(Inbound("run-a", acceptedAt)));
                CollectionAssert.AreEqual(
                    new[] { "run-z", "run-a" },
                    db.ListInboundTopicRuns(InboundTopicRunStates.Accepted)
                        .Select(item => item.RunId).ToArray());
            }

            using (var reopened = MeshDb.Open(path, key))
            {
                CollectionAssert.AreEqual(
                    new[] { "run-z", "run-a" },
                    reopened.ListInboundTopicRuns(InboundTopicRunStates.Accepted)
                        .Select(item => item.RunId).ToArray());
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static MeshDb.InboundTopicRunItem Inbound(string runId, DateTimeOffset acceptedAt)
        => new(
            runId,
            "source-device",
            new TopicRunRequestPayload(
                runId,
                "thread-1",
                "line-1",
                "owner",
                "Do the work",
                acceptedAt,
                "target-device",
                TopicTurnMode.Single),
            InboundTopicRunStates.Accepted,
            acceptedAt,
            acceptedAt);

    private static IReadOnlyList<DeviceSyncOperation> LargeOperations()
    {
        var random = new Random(1731);
        var payload = new byte[900_000];
        random.NextBytes(payload);
        return
        [
            new DeviceSyncOperation(
                "operation-1",
                "source-device",
                DeviceSyncKinds.TopicUpsert,
                "topic-1",
                "2026-08-01T12:00:00.0000000Z/source-device/1",
                Convert.ToBase64String(payload)),
            new DeviceSyncOperation(
                "operation-2",
                "source-device",
                DeviceSyncKinds.TopicDelete,
                "topic-2",
                "2026-08-01T12:00:01.0000000Z/source-device/2",
                "")
        ];
    }

    private static string DatabasePath()
        => Path.Combine(Path.GetTempPath(), "mesh-snapshot-tests-" + Guid.NewGuid().ToString("n") + ".db");

    private static void DeleteDatabase(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}
