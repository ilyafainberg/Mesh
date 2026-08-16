using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReplicationBootstrapPersistenceTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] databaseKey = null!;
    private KeyPair localKeys = null!;
    private KeyPair peerKeys = null!;
    private FabricRoster roster = null!;
    private ReplicationIdentity identity = null!;
    private ReplicationBootstrapTarget target = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "replication-bootstrap",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        databaseKey = Enumerable.Range(11, 32).Select(value => (byte)value).ToArray();
        localKeys = KeyPair.New();
        peerKeys = KeyPair.New();
        var localDevice = DeviceProtocol.DeviceId(localKeys.PublicB64);
        var peerDevice = DeviceProtocol.DeviceId(peerKeys.PublicB64);
        identity = new ReplicationIdentity(
            "alice",
            localDevice,
            localKeys.PublicB64,
            localKeys.PrivateB64,
            "epoch-1",
            0,
            OnlineReplicationProtocol.ZeroHash);
        var peer = new ReplicationDevice("alice", peerDevice, peerKeys.PublicB64, 0, false);
        roster = new FabricRoster();
        roster.Add("alice", new ReplicationDevice("alice", localDevice, localKeys.PublicB64, 0, false));
        roster.Add("alice", peer);
        target = ReplicationBootstrapTarget.Create(peer, identity);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void BootstrapProgress_ResumesFromAtomicChunkAfterReopen()
    {
        var envelopes = CreateEnvelopes(3);
        var snapshot = JsonSerializer.Serialize(envelopes);
        var bootstrapId = Guid.NewGuid().ToString("n");

        using (var db = OpenDb())
        {
            var journal = OpenJournal(db);
            db.CreateOrResumePeerBootstrap(target, bootstrapId, Hash(snapshot), snapshot, envelopes.Count);
            var firstChunk = envelopes.Take(2).ToList();
            journal.EmitLocalBatch(
                firstChunk,
                new[] { "alice" },
                domainWork: static (_, _, _) => { },
                eventWork: (_, tx, evt, index) =>
                {
                    if (index == firstChunk.Count - 1)
                        db.UpdatePeerBootstrapProgress(
                            target, bootstrapId, firstChunk.Count, envelopes.Count, 1, evt.Seq, tx);
                });

            var marker = db.GetPeerBootstrap(target)!;
            Assert.AreEqual(MeshDb.BootstrapStatePending, marker.State);
            Assert.AreEqual(2, marker.EmittedItems);
            Assert.AreEqual(1UL, marker.BootstrapFromSeq);
            Assert.AreEqual(2UL, marker.BootstrapThroughSeq);
        }

        SqliteConnection.ClearAllPools();
        using (var db = OpenDb())
        {
            var marker = db.GetPeerBootstrap(target)!;
            Assert.AreEqual(bootstrapId, marker.BootstrapId);
            Assert.AreEqual(2, marker.EmittedItems);
            Assert.AreEqual(snapshot, marker.SnapshotJson);

            var journal = OpenJournal(db);
            var finalChunk = envelopes.Skip(marker.EmittedItems).ToList();
            journal.EmitLocalBatch(
                finalChunk,
                new[] { "alice" },
                domainWork: static (_, _, _) => { },
                eventWork: (_, tx, evt, index) =>
                {
                    if (index == finalChunk.Count - 1)
                        db.UpdatePeerBootstrapProgress(
                            target, bootstrapId, envelopes.Count, envelopes.Count, marker.BootstrapFromSeq, evt.Seq, tx);
                });

            marker = db.GetPeerBootstrap(target)!;
            Assert.AreEqual(MeshDb.BootstrapStateEmitted, marker.State);
            Assert.AreEqual(3, marker.EmittedItems);
            Assert.AreEqual(1UL, marker.BootstrapFromSeq);
            Assert.AreEqual(3UL, marker.BootstrapThroughSeq);
            Assert.AreEqual(3, db.QueryEvents(identity.DeviceId, identity.LogEpoch, 1, 10).Count);
        }
    }

    [TestMethod]
    public void BootstrapReceipt_TransitionsToPersistedAcrossReopen()
    {
        var envelopes = CreateEnvelopes(1);
        var snapshot = JsonSerializer.Serialize(envelopes);
        var bootstrapId = Guid.NewGuid().ToString("n");
        ulong through;

        using (var db = OpenDb())
        {
            var journal = OpenJournal(db);
            db.CreateOrResumePeerBootstrap(target, bootstrapId, Hash(snapshot), snapshot, envelopes.Count);
            journal.EmitLocalBatch(
                envelopes,
                new[] { "alice" },
                domainWork: static (_, _, _) => { },
                eventWork: (_, tx, evt, _) =>
                    db.UpdatePeerBootstrapProgress(target, bootstrapId, 1, 1, evt.Seq, evt.Seq, tx));
            through = db.GetPeerBootstrap(target)!.BootstrapThroughSeq;
            Assert.IsNull(db.GetLastSuccessfulReplication());
        }

        SqliteConnection.ClearAllPools();
        using (var db = OpenDb())
        {
            Assert.AreEqual(MeshDb.BootstrapStateEmitted, db.GetPeerBootstrap(target)!.State);
            var receipt = OnlineReplicationProtocol.CreateReceipt(
                target.PeerDeviceId,
                identity.DeviceId,
                identity.LogEpoch,
                through,
                OnlineReplicationProtocol.HashText("cursor"),
                OnlineReplicationProtocol.HashText("batch"),
                peerKeys.PrivateB64);

            Assert.AreEqual(1, db.MarkOutboxPersistedFromReceipt(receipt, peerKeys.PublicB64, "alice"));
            var marker = db.GetPeerBootstrap(target)!;
            Assert.AreEqual(MeshDb.BootstrapStatePersisted, marker.State);
            Assert.IsNotNull(marker.CompletedAt);
            var checkpoint = db.GetLastSuccessfulReplication()!;
            Assert.AreEqual(target.PeerDeviceId, checkpoint.PeerDeviceId);
            Thread.Sleep(20);
            Assert.AreEqual(0, db.MarkOutboxPersistedFromReceipt(receipt, peerKeys.PublicB64, "alice"));
            Assert.AreEqual(checkpoint.At, db.GetLastSuccessfulReplication()!.At);
        }

        SqliteConnection.ClearAllPools();
        using (var db = OpenDb())
        {
            Assert.AreEqual(MeshDb.BootstrapStatePersisted, db.GetPeerBootstrap(target)!.State);
            Assert.AreEqual(target.PeerDeviceId, db.GetLastSuccessfulReplication()!.PeerDeviceId);
        }
    }

    [TestMethod]
    public void BootstrapReceipt_StalledAt25PersistsAfterReceiptReachesBoundary()
    {
        var envelopes = CreateEnvelopes(30);
        var snapshot = JsonSerializer.Serialize(envelopes);
        const string bootstrapId = "receipt-25-recovery";

        using var db = OpenDb();
        var journal = OpenJournal(db);
        db.CreateOrResumePeerBootstrap(target, bootstrapId, Hash(snapshot), snapshot, envelopes.Count);
        journal.EmitLocalBatch(
            envelopes,
            new[] { "alice" },
            domainWork: static (_, _, _) => { },
            eventWork: (_, tx, evt, index) =>
            {
                if (index == envelopes.Count - 1)
                    db.UpdatePeerBootstrapProgress(
                        target, bootstrapId, envelopes.Count, envelopes.Count, 1, evt.Seq, tx);
            });

        var marker = db.GetPeerBootstrap(target)!;
        Assert.AreEqual(30UL, marker.BootstrapThroughSeq);
        Assert.AreEqual(MeshDb.BootstrapStateEmitted, marker.State);

        var stalledReceipt = OnlineReplicationProtocol.CreateReceipt(
            target.PeerDeviceId,
            identity.DeviceId,
            identity.LogEpoch,
            25,
            Hash("cursor-25"),
            Hash("batch-25"),
            peerKeys.PrivateB64);
        Assert.AreEqual(25, db.MarkOutboxPersistedFromReceipt(stalledReceipt, peerKeys.PublicB64, "alice"));
        Assert.AreEqual(MeshDb.BootstrapStateEmitted, db.GetPeerBootstrap(target)!.State);

        var completedReceipt = OnlineReplicationProtocol.CreateReceipt(
            target.PeerDeviceId,
            identity.DeviceId,
            identity.LogEpoch,
            marker.BootstrapThroughSeq,
            Hash("cursor-30"),
            Hash("batch-30"),
            peerKeys.PrivateB64);
        Assert.AreEqual(5, db.MarkOutboxPersistedFromReceipt(completedReceipt, peerKeys.PublicB64, "alice"));
        marker = db.GetPeerBootstrap(target)!;
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, marker.State);
        Assert.IsNotNull(marker.CompletedAt);
    }

    [TestMethod]
    public void BootstrapIdentityChanges_CreateIndependentOrResetMarkers()
    {
        const string snapshot = "[]";
        using var db = OpenDb();
        var initial = db.CreateOrResumePeerBootstrap(
            target, "initial", Hash(snapshot), snapshot, totalItems: 1);

        var authorityTarget = target with { AuthGeneration = target.AuthGeneration + 1 };
        Assert.IsNull(db.GetPeerBootstrap(authorityTarget));
        var authority = db.CreateOrResumePeerBootstrap(
            authorityTarget, "authority", Hash(snapshot), snapshot, totalItems: 1);
        Assert.AreNotEqual(initial.BootstrapId, authority.BootstrapId);

        var epochTarget = target with { LocalLogEpoch = "epoch-2" };
        var epoch = db.CreateOrResumePeerBootstrap(
            epochTarget, "epoch", Hash(snapshot), snapshot, totalItems: 1);
        Assert.AreEqual("epoch-2", epoch.LocalLogEpoch);
        Assert.AreEqual("epoch", epoch.BootstrapId);
        Assert.AreEqual(MeshDb.BootstrapStatePending, epoch.State);

        var replacementKeys = KeyPair.New();
        var replacementPeer = new ReplicationDevice(
            "alice",
            DeviceProtocol.DeviceId(replacementKeys.PublicB64),
            replacementKeys.PublicB64,
            0,
            false);
        var replacementTarget = ReplicationBootstrapTarget.Create(replacementPeer, identity);
        Assert.IsNull(db.GetPeerBootstrap(replacementTarget));
    }

    [TestMethod]
    public void EmptyBootstrap_UsesSignedZeroLengthReceipt()
    {
        const string snapshot = "[]";
        using (var db = OpenDb())
        {
            var pending = db.CreateOrResumePeerBootstrap(
                target, "empty", Hash(snapshot), snapshot, totalItems: 0);
            var marker = db.CompleteEmptyPeerBootstrap(target, pending.BootstrapId, baselineFrom: 1);

            Assert.AreEqual(MeshDb.BootstrapStateEmitted, marker.State);
            Assert.AreEqual(0, marker.EmittedItems);
            Assert.AreEqual(1UL, marker.BootstrapFromSeq);
            Assert.AreEqual(0UL, marker.BootstrapThroughSeq);
            Assert.IsNull(marker.CompletedAt);
            Assert.IsNull(db.GetLastSuccessfulReplication());

            var cursor = new ReplicationCursorEntry(
                identity.LogEpoch,
                0,
                new byte[OnlineReplicationLimits.AheadBitsBytes]);
            var receipt = OnlineReplicationProtocol.CreateReceipt(
                target.PeerDeviceId,
                identity.DeviceId,
                identity.LogEpoch,
                0,
                OnlineReplicationProtocol.ComputeCursorHash(cursor),
                Hash("empty-range"),
                peerKeys.PrivateB64);
            db.MarkOutboxPersistedFromReceipt(receipt, peerKeys.PublicB64, target.PeerHandle);
            marker = db.GetPeerBootstrap(target)!;
            Assert.AreEqual(MeshDb.BootstrapStatePersisted, marker.State);
            Assert.IsNotNull(marker.CompletedAt);
            Assert.AreEqual(target.PeerDeviceId, db.GetLastSuccessfulReplication()!.PeerDeviceId);
        }

        SqliteConnection.ClearAllPools();
        using var reopened = OpenDb();
        Assert.AreEqual(MeshDb.BootstrapStatePersisted, reopened.GetPeerBootstrap(target)!.State);
    }

    [TestMethod]
    public void LegacyEmptyBootstrap_IsRepairedAfterRestart()
    {
        const string snapshot = "[]";
        using (var db = OpenDb())
        {
            db.CreateOrResumePeerBootstrap(
                target, "legacy-empty", Hash(snapshot), snapshot, totalItems: 1);
            Assert.IsNull(db.GetLastSuccessfulReplication());
        }

        SqliteConnection.ClearAllPools();
        using (var raw = OpenRawConnection())
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE replication_peer_bootstrap
                SET state = 'pending', emitted_items = 0, total_items = 0,
                    bootstrap_through_seq = 0, snapshot_json = '[]', completed_at = NULL
                WHERE bootstrap_id = 'legacy-empty';
                """;
            Assert.AreEqual(1, cmd.ExecuteNonQuery());
        }

        SqliteConnection.ClearAllPools();
        using var reopened = OpenDb();
        var repaired = reopened.CompleteEmptyPeerBootstrap(target, "legacy-empty", baselineFrom: 1);
        Assert.AreEqual(MeshDb.BootstrapStateEmitted, repaired.State);
        Assert.AreEqual(1UL, repaired.BootstrapFromSeq);
        Assert.AreEqual(0UL, repaired.BootstrapThroughSeq);
        Assert.IsNull(repaired.CompletedAt);
        Assert.IsNull(reopened.GetLastSuccessfulReplication());
    }

    [TestMethod]
    public void LegacyEmittedBootstrapWithoutBaseline_IsRebuiltBeforeReuse()
    {
        var envelopes = CreateEnvelopes(1);
        const string oldId = "legacy-without-baseline";
        using (var db = OpenDb())
        {
            var journal = OpenJournal(db);
            db.CreateOrResumePeerBootstrap(
                target, oldId, Hash("old"), "old", envelopes.Count);
            journal.EmitLocalBatch(
                envelopes,
                new[] { "alice" },
                domainWork: static (_, _, _) => { },
                eventWork: (_, tx, evt, _) =>
                    db.UpdatePeerBootstrapProgress(target, oldId, 1, 1, evt.Seq, evt.Seq, tx));

            using var cmd = db.RawConnectionForTest.CreateCommand();
            cmd.CommandText = "UPDATE replication_peer_bootstrap SET bootstrap_from_seq = 0 WHERE bootstrap_id = $id;";
            cmd.Parameters.AddWithValue("$id", oldId);
            Assert.AreEqual(1, cmd.ExecuteNonQuery());
        }

        SqliteConnection.ClearAllPools();
        using var reopened = OpenDb();
        var replacement = reopened.CreateOrResumePeerBootstrap(
            target, "replacement", Hash("new"), "new", totalItems: 1);

        Assert.AreEqual("replacement", replacement.BootstrapId);
        Assert.AreEqual(MeshDb.BootstrapStatePending, replacement.State);
        Assert.AreEqual(0, replacement.EmittedItems);
        Assert.AreEqual(0UL, replacement.BootstrapFromSeq);
        Assert.AreEqual(0UL, replacement.BootstrapThroughSeq);
    }

    [TestMethod]
    public async Task BootstrapJournalLock_AllocatesSnapshotBeforeConcurrentLocalWrite()
    {
        using var db = OpenDb();
        var journal = OpenJournal(db);
        var snapshotEnvelope = CreateEnvelopes(1);
        const string bootstrapId = "atomic-first-sequence";
        db.CreateOrResumePeerBootstrap(
            target, bootstrapId, Hash("snapshot"), "snapshot", totalItems: 1);

        using var started = new ManualResetEventSlim();
        Task<string> concurrent;
        IReadOnlyList<string> snapshotIds;
        using (db.EnterLocalOriginJournalLock())
        {
            concurrent = Task.Run(() =>
            {
                started.Set();
                return journal.EmitLocal(
                    CreateEnvelopes(1)[0] with { EntityId = "concurrent" },
                    new[] { "alice" });
            });
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(25);
            Assert.IsFalse(concurrent.IsCompleted, "the concurrent writer must wait behind the bootstrap boundary");

            snapshotIds = journal.EmitLocalBatch(
                snapshotEnvelope,
                new[] { "alice" },
                domainWork: static (_, _, _) => { },
                eventWork: (_, tx, evt, _) =>
                    db.UpdatePeerBootstrapProgress(
                        target, bootstrapId, 1, 1, evt.Seq, evt.Seq, tx));
        }

        var concurrentId = await concurrent;
        var snapshotEvent = db.GetEvent(snapshotIds[0])!;
        var concurrentEvent = db.GetEvent(concurrentId)!;
        Assert.AreEqual(1UL, snapshotEvent.Seq);
        Assert.AreEqual(2UL, concurrentEvent.Seq);
        Assert.AreEqual(1UL, db.GetPeerBootstrap(target)!.BootstrapFromSeq);
    }

    [TestMethod]
    public void SnapshotBaseline_OnlyAdvancesAnEmptyOrigin()
    {
        using var db = OpenDb();
        var manifest = OnlineReplicationProtocol.CreateSnapshotManifest(
            "snapshot",
            target.PeerDeviceId,
            identity.DeviceId,
            identity.LogEpoch,
            25,
            30,
            Hash("state"),
            identity.AuthGeneration,
            localKeys.PrivateB64);

        Assert.IsTrue(db.TryInstallSnapshotBaseline(manifest));
        Assert.AreEqual(24UL, db.GetCursor(identity.DeviceId)!.Contiguous);

        var evt = OnlineReplicationProtocol.CreateEvent(
            identity.DeviceId,
            identity.LogEpoch,
            25,
            identity.Handle,
            identity.AuthGeneration,
            ReplicationOpKinds.Topic,
            "topic",
            "topic",
            "v1",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplicationPayloadCodec.Encrypt(
                ReplicationPayloadCodec.EncodeEnvelope(CreateEnvelopes(1)[0]),
                new[] { localKeys.PublicB64 }),
            localKeys.PrivateB64);
        db.AppendEvent(evt);

        var later = manifest with { BaselineFrom = 40, SnapshotThrough = 45 };
        Assert.IsFalse(db.TryInstallSnapshotBaseline(later));
        Assert.AreEqual(24UL, db.GetCursor(identity.DeviceId)!.Contiguous);
    }

    [TestMethod]
    public void SnapshotCoverage_AdvancesOnlyOriginsWithoutStoredEvents()
    {
        using var db = OpenDb();
        var existing = OnlineReplicationProtocol.CreateEvent(
            "with-history",
            "epoch-1",
            1,
            identity.Handle,
            identity.AuthGeneration,
            ReplicationOpKinds.Topic,
            "existing",
            "existing",
            "v1",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplicationPayloadCodec.Encrypt(
                ReplicationPayloadCodec.EncodeEnvelope(CreateEnvelopes(1)[0]),
                new[] { localKeys.PublicB64 }),
            localKeys.PrivateB64);
        db.AppendEvent(existing);

        var manifest = OnlineReplicationProtocol.CreateSnapshotManifest(
            "coverage",
            target.PeerDeviceId,
            identity.DeviceId,
            identity.LogEpoch,
            1,
            1,
            Hash("state"),
            identity.AuthGeneration,
            localKeys.PrivateB64,
            new[]
            {
                new ReplicationSnapshotCoverage("empty-origin", "epoch-1", 10),
                new ReplicationSnapshotCoverage("with-history", "epoch-1", 10)
            });

        Assert.AreEqual(1, db.TryInstallSnapshotCoverage(manifest));
        Assert.AreEqual(10UL, db.GetCursor("empty-origin")!.Contiguous);
        Assert.IsNull(db.GetCursor("with-history"));
    }

    [TestMethod]
    public void BootstrapCoverage_SurvivesReopen()
    {
        var coverageJson = ReplicationPayloadCodec.SerializeControl(new List<ReplicationSnapshotCoverage>
        {
            new("third", "epoch-1", 42)
        });
        using (var db = OpenDb())
        {
            db.CreateOrResumePeerBootstrap(
                target,
                "coverage-restart",
                Hash("snapshot"),
                "snapshot",
                totalItems: 1,
                coverageJson: coverageJson);
        }

        SqliteConnection.ClearAllPools();
        using var reopened = OpenDb();
        Assert.AreEqual(coverageJson, reopened.GetPeerBootstrap(target)!.CoverageJson);
    }

    [TestMethod]
    public void LastSuccessfulReplication_CanBeScopedToOwnerHandle()
    {
        using var db = OpenDb();
        db.RecordPeerSync("alice", target.PeerDeviceId);
        Thread.Sleep(20);
        db.RecordPeerSync("bob", "bob-device");

        Assert.AreEqual("bob-device", db.GetLastSuccessfulReplication()!.PeerDeviceId);
        Assert.AreEqual(
            target.PeerDeviceId,
            db.GetLastSuccessfulReplication("alice")!.PeerDeviceId);
        Assert.IsNull(db.GetLastSuccessfulReplication("carol"));
    }

    private SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var key = connection.CreateCommand();
        key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(databaseKey)}'\";";
        key.ExecuteNonQuery();
        return connection;
    }

    private MeshDb OpenDb() => MeshDb.Open(databasePath, databaseKey);

    private ReplicationJournal OpenJournal(MeshDb db)
    {
        var journal = new ReplicationJournal(db, identity, roster, deviceIsDesktop: true);
        journal.EnsureLocalOrigin();
        return journal;
    }

    private static List<ReplicationPayloadCodec.DomainEnvelope> CreateEnvelopes(int count)
        => Enumerable.Range(1, count)
            .Select(index => new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "topic-" + index,
                "topic-" + index,
                "v" + index,
                "{}"))
            .ToList();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
