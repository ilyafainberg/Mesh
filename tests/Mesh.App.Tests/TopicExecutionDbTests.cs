using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DeviceTopicDbTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "mesh-device-topic-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void TopicReceiptOutbox_DuplicateAcceptedAfterClockAdvanceReusesCanonicalBytes()
    {
        var at = new DateTimeOffset(2026, 8, 25, 20, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(at);
        var request = new TopicRunRequestPayload(
            "run-receipt-replay", "thread-receipt-replay", "line-receipt-replay",
            "owner", "receipt replay", at, "executor", TopicTurnMode.Single);
        var accepted = TopicAcceptancePolicy.Create(request, at.AddSeconds(1));
        using var db = MeshDb.Open(databasePath, key, time);

        var firstReceipt = TopicControlProtocol.CreateReceipt(accepted, time.GetUtcNow());
        var firstItem = ReceiptOutbox(firstReceipt, "executor", time.GetUtcNow());
        var first = db.ExecuteDurableWrite(() => db.GetOrCreateTopicReceiptOutbox(firstItem));
        Assert.AreEqual(TopicReceiptOutboxPersistenceKind.Created, first.Kind);

        time.Advance(TimeSpan.FromHours(12));
        var replayReceipt = TopicControlProtocol.CreateReceipt(accepted, time.GetUtcNow());
        var replay = db.ExecuteDurableWrite(() => db.GetOrCreateTopicReceiptOutbox(
            ReceiptOutbox(replayReceipt, "executor", time.GetUtcNow())));

        Assert.AreEqual(TopicReceiptOutboxPersistenceKind.Reused, replay.Kind);
        Assert.AreEqual(first.Item.Plaintext, replay.Item.Plaintext);
        Assert.AreEqual(first.Item.CreatedAt, replay.Item.CreatedAt);
        Assert.HasCount(1, db.ListDeviceEnvelopeOutbox());
    }

    [TestMethod]
    public void Migration_BackfillsTriggerLineIdentityForRetainedTerminalCorrelation()
    {
        var at = new DateTimeOffset(2026, 8, 25, 20, 15, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            using var insert = db.RawConnectionForTest.CreateCommand();
            insert.CommandText = """
                INSERT INTO received_topic_controls(
                    envelope_id, source_device_id, run_id, thread_id,
                    control_kind, update_json, received_at)
                VALUES(
                    'terminal-envelope', 'executor', 'run-migrated', 'thread-migrated',
                    'topic.terminal', '{"triggerLineId":"line-migrated"}', $at);
                INSERT INTO topic_run_correlations(
                    run_id, thread_id, target_device_id, trigger_line_id,
                    created_at, terminal_at, terminal_event_at)
                VALUES(
                    'run-migrated', 'thread-migrated', 'executor', 'line-migrated',
                    $at, $at, $at);
                """;
            insert.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
            insert.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using (var raw = OpenRawConnection())
        {
            using var downgrade = raw.CreateCommand();
            downgrade.CommandText =
                "ALTER TABLE topic_run_correlations DROP COLUMN trigger_line_id;";
            downgrade.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = MeshDb.Open(databasePath, key);
        var correlation = migrated.GetTopicRunCorrelation("run-migrated");

        Assert.IsNotNull(correlation);
        Assert.AreEqual("line-migrated", correlation.TriggerLineId);
        Assert.IsNotNull(correlation.TerminalAt);
    }

    [TestMethod]
    public void Migration_PreTriggerSchema_BackfillsClassifiesAndBindsActiveNullOnce()
    {
        var at = new DateTimeOffset(2026, 8, 25, 20, 20, 0, TimeSpan.Zero);
        CreatePreTriggerCorrelationFixture(at);

        string diagnostics;
        using (var migrated = MeshDb.Open(databasePath, key, new ManualTimeProvider(at)))
        {
            foreach (var state in new[]
                     {
                         TopicOutboxStates.Pending,
                         TopicOutboxStates.RelayQueued,
                         TopicOutboxStates.DeviceQueued,
                         TopicOutboxStates.Running
                     })
            {
                var correlation = migrated.GetTopicRunCorrelation("run-" + state);
                Assert.AreEqual("line-" + state, correlation?.TriggerLineId);
            }
            Assert.AreEqual(
                "line-inbound",
                migrated.GetTopicRunCorrelation("run-inbound")?.TriggerLineId);
            Assert.AreEqual(
                "line-local",
                migrated.GetTopicRunCorrelation("run-local")?.TriggerLineId);
            Assert.AreEqual(
                "line-retained",
                migrated.GetTopicRunCorrelation("run-retained")?.TriggerLineId);
            Assert.IsNotNull(migrated.GetTopicRunCorrelation("run-retained")?.TerminalAt);

            Assert.IsFalse(migrated.TryBindLegacyTopicRunCorrelation(
                "run-unresolved-active", "thread-unresolved", "wrong-device", "line-bound"));
            Assert.IsFalse(migrated.TryBindLegacyTopicRunCorrelation(
                "run-unresolved-active", "wrong-thread", "executor", "line-bound"));
            Assert.IsTrue(migrated.TryBindLegacyTopicRunCorrelation(
                "run-unresolved-active", "thread-unresolved", "executor", "line-bound"));
            Assert.IsFalse(migrated.TryBindLegacyTopicRunCorrelation(
                "run-unresolved-active", "thread-unresolved", "executor", "line-conflict"));
            Assert.AreEqual(
                "line-bound",
                migrated.GetTopicRunCorrelation("run-unresolved-active")?.TriggerLineId);

            using var read = migrated.RawConnectionForTest.CreateCommand();
            read.CommandText = """
                SELECT
                    (SELECT v FROM meta WHERE k = 'topic_run_trigger_schema_version'),
                    (SELECT v FROM meta WHERE k = 'topic_run_trigger_migration_diagnostics'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-unresolved-tombstone'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-unresolved-terminal'),
                    (SELECT v FROM meta WHERE k = 'topic_run_trigger_legacy_bind_count');
                """;
            using var reader = read.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("3", reader.GetString(0));
            diagnostics = reader.GetString(1);
            Assert.AreEqual("legacy-tombstone", reader.GetString(2));
            Assert.AreEqual("legacy-tombstone", reader.GetString(3));
            Assert.AreEqual("1", reader.GetString(4));
            StringAssert.Contains(diagnostics, "\"legacyActiveNull\":1");
            StringAssert.Contains(diagnostics, "\"legacyTombstone\":2");
            Assert.IsFalse(diagnostics.Contains("run-unresolved", StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        using var restarted = MeshDb.Open(databasePath, key, new ManualTimeProvider(at.AddHours(1)));
        Assert.AreEqual(
            "line-bound",
            restarted.GetTopicRunCorrelation("run-unresolved-active")?.TriggerLineId);
        using var restartRead = restarted.RawConnectionForTest.CreateCommand();
        restartRead.CommandText = """
            SELECT
                (SELECT v FROM meta WHERE k = 'topic_run_trigger_migration_diagnostics'),
                (SELECT v FROM meta WHERE k = 'topic_run_trigger_legacy_bind_count');
            """;
        using var restartReader = restartRead.ExecuteReader();
        Assert.IsTrue(restartReader.Read());
        Assert.AreEqual(diagnostics, restartReader.GetString(0));
        Assert.AreEqual("1", restartReader.GetString(1));
    }

    [TestMethod]
    public void Migration_PreTriggerRetainedCandidates_FilterEmptyDetectConflictAndRestartIdempotently()
    {
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        CreatePreTriggerRetainedCandidateFixture(at);

        string diagnostics;
        using (var migrated = MeshDb.Open(databasePath, key, new ManualTimeProvider(at)))
        {
            Assert.AreEqual(
                "line-older-valid",
                migrated.GetTopicRunCorrelation("run-newer-empty")?.TriggerLineId);
            Assert.AreEqual(
                "line-duplicate",
                migrated.GetTopicRunCorrelation("run-duplicate-valid")?.TriggerLineId);
            Assert.IsNull(
                migrated.GetTopicRunCorrelation("run-whitespace-only")?.TriggerLineId);
            Assert.IsNull(
                migrated.GetTopicRunCorrelation("run-conflicting-valid")?.TriggerLineId);
            Assert.IsNull(
                migrated.GetTopicRunCorrelation("run-cross-tier-conflict")?.TriggerLineId);
            Assert.IsNull(
                migrated.GetTopicRunCorrelation("run-overlong-only")?.TriggerLineId);
            Assert.IsNull(
                migrated.GetTopicRunCorrelation("run-outbox-created-conflict")?.TriggerLineId);

            using var read = migrated.RawConnectionForTest.CreateCommand();
            read.CommandText = """
                SELECT
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-newer-empty'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-whitespace-only'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-conflicting-valid'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-duplicate-valid'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-cross-tier-conflict'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-overlong-only'),
                    (SELECT trigger_identity_state FROM topic_run_correlations
                     WHERE run_id = 'run-outbox-created-conflict'),
                    (SELECT v FROM meta WHERE k = 'topic_run_trigger_migration_diagnostics');
                """;
            using var reader = read.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("strict", reader.GetString(0));
            Assert.AreNotEqual("strict", reader.GetString(1));
            Assert.AreEqual("legacy-conflict", reader.GetString(2));
            Assert.AreEqual("strict", reader.GetString(3));
            Assert.AreEqual("legacy-conflict", reader.GetString(4));
            Assert.AreNotEqual("strict", reader.GetString(5));
            Assert.AreEqual("legacy-conflict", reader.GetString(6));
            diagnostics = reader.GetString(7);
            StringAssert.Contains(diagnostics, "\"version\":3");
            StringAssert.Contains(diagnostics, "\"legacyConflicts\":3");
            StringAssert.Contains(
                diagnostics,
                HashMigrationIdentifierForTest("run-conflicting-valid"));
            StringAssert.Contains(
                diagnostics,
                HashMigrationIdentifierForTest("run-cross-tier-conflict"));
            StringAssert.Contains(
                diagnostics,
                HashMigrationIdentifierForTest("run-outbox-created-conflict"));
            Assert.IsFalse(
                diagnostics.Contains("run-conflicting-valid", StringComparison.Ordinal));

            using var invalidStrict = migrated.RawConnectionForTest.CreateCommand();
            invalidStrict.CommandText = """
                SELECT COUNT(*) FROM topic_run_correlations
                WHERE trigger_identity_state = 'strict'
                  AND trim(COALESCE(trigger_line_id, '')) = '';
                """;
            Assert.AreEqual(0L, Convert.ToInt64(invalidStrict.ExecuteScalar()));
        }

        SqliteConnection.ClearAllPools();
        using var restarted = MeshDb.Open(
            databasePath, key, new ManualTimeProvider(at.AddHours(1)));
        Assert.AreEqual(
            "line-older-valid",
            restarted.GetTopicRunCorrelation("run-newer-empty")?.TriggerLineId);
        Assert.IsNull(
            restarted.GetTopicRunCorrelation("run-conflicting-valid")?.TriggerLineId);
        Assert.IsNull(
            restarted.GetTopicRunCorrelation("run-cross-tier-conflict")?.TriggerLineId);
        Assert.IsNull(
            restarted.GetTopicRunCorrelation("run-outbox-created-conflict")?.TriggerLineId);
        using var restartRead = restarted.RawConnectionForTest.CreateCommand();
        restartRead.CommandText =
            "SELECT v FROM meta WHERE k = 'topic_run_trigger_migration_diagnostics';";
        Assert.AreEqual(diagnostics, restartRead.ExecuteScalar());
    }

    [TestMethod]
    public void TopicReceiptOutbox_StableIdWithConflictingSemanticsIsRejected()
    {
        var at = new DateTimeOffset(2026, 8, 25, 20, 30, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-receipt-conflict", "thread-receipt-a", "line-receipt",
            "owner", "receipt conflict", at, "executor", TopicTurnMode.Single);
        var accepted = TopicAcceptancePolicy.Create(request, at);
        using var db = MeshDb.Open(databasePath, key);
        var original = ReceiptOutbox(
            TopicControlProtocol.CreateReceipt(accepted, at), "executor", at);
        Assert.AreEqual(
            TopicReceiptOutboxPersistenceKind.Created,
            db.ExecuteDurableWrite(() => db.GetOrCreateTopicReceiptOutbox(original)).Kind);

        var conflicting = accepted with { ThreadId = "thread-receipt-b" };
        var conflict = db.ExecuteDurableWrite(() => db.GetOrCreateTopicReceiptOutbox(
            ReceiptOutbox(
                TopicControlProtocol.CreateReceipt(conflicting, at.AddMinutes(1)),
                "executor",
                at.AddMinutes(1))));

        Assert.AreEqual(TopicReceiptOutboxPersistenceKind.IdentityConflict, conflict.Kind);
        Assert.AreEqual(original.Plaintext, db.GetDeviceEnvelopeOutbox(original.EnvelopeId)!.Plaintext);
        Assert.HasCount(1, db.ListDeviceEnvelopeOutbox());
    }

    [TestMethod]
    public void CommittedAnswerCleanup_AllowsNullRunButRejectsDifferentActiveRun()
    {
        var at = new DateTimeOffset(2026, 8, 25, 21, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(at);
        var request = new TopicRunRequestPayload(
            "run-answer-cleanup", "thread-answer-cleanup", "line-answer-cleanup",
            "owner", "answer cleanup", at, "executor", TopicTurnMode.Single);
        using var db = MeshDb.Open(databasePath, key, time);
        db.EnsureOwnThread(request.ThreadId, "Answer cleanup", at);
        db.SetOwnThreadExecution(
            request.ThreadId, request.TargetDeviceId, at, request.RunId,
            "Executor", DevicePlatforms.Windows);
        _ = new TopicRequestOutboxHandler(db, time).Queue(request.TargetDeviceId, request, []);
        using (var clear = db.RawConnectionForTest.CreateCommand())
        {
            clear.CommandText =
                "UPDATE own_threads SET execution_run_id = NULL WHERE id = $thread;";
            clear.Parameters.AddWithValue("$thread", request.ThreadId);
            Assert.AreEqual(1, clear.ExecuteNonQuery());
        }

        Assert.IsTrue(db.ExecuteDurableWrite(() =>
            db.CompleteOwnThreadRunAndDeleteTopicOutbox(
                request.ThreadId, request.RunId, request.TriggerLineId, request.TargetDeviceId,
                "Executor", DevicePlatforms.Windows, at, at.AddSeconds(2))));
        Assert.IsNull(db.GetTopicOutbox(request.RunId));
        Assert.IsNotNull(db.GetTopicRunCorrelation(request.RunId)!.TerminalAt);

        var foreign = request with
        {
            RunId = "run-answer-foreign",
            TriggerLineId = "line-answer-foreign"
        };
        db.SetOwnThreadExecution(
            request.ThreadId, request.TargetDeviceId, at.AddSeconds(3),
            "run-newer-active", "Executor", DevicePlatforms.Windows);
        _ = new TopicRequestOutboxHandler(db, time).Queue(
            request.TargetDeviceId, foreign, []);
        Assert.IsFalse(db.ExecuteDurableWrite(() =>
            db.CompleteOwnThreadRunAndDeleteTopicOutbox(
                request.ThreadId, foreign.RunId, foreign.TriggerLineId, request.TargetDeviceId,
                "Executor", DevicePlatforms.Windows, at, at.AddSeconds(4))));
        Assert.IsNotNull(db.GetTopicOutbox(foreign.RunId));
    }

    [TestMethod]
    public void GenericNullTopicUpsert_PreservesActiveRunInDatabaseAndLiveProfile()
    {
        var at = new DateTimeOffset(2026, 8, 25, 21, 30, 0, TimeSpan.Zero);
        using var db = MeshDb.Open(databasePath, key);
        db.EnsureOwnThread("thread-upsert-run", "Before", at);
        db.SetOwnThreadExecution(
            "thread-upsert-run", "executor", at, "run-upsert-active",
            "Executor", DevicePlatforms.Windows);
        using (var tx = db.RawConnectionForTest.BeginTransaction())
        {
            Protocol9DomainTables.UpsertOwnThreadMetadata(
                db.RawConnectionForTest,
                tx,
                new OwnThread
                {
                    Id = "thread-upsert-run",
                    Title = "After",
                    CreatedAt = at,
                    ExecutionRunId = null
                },
                0);
            tx.Commit();
        }
        using (var read = db.RawConnectionForTest.CreateCommand())
        {
            read.CommandText =
                "SELECT execution_run_id FROM own_threads WHERE id = 'thread-upsert-run';";
            Assert.AreEqual("run-upsert-active", read.ExecuteScalar());
        }

        var profile = new MeshProfile();
        profile.OwnThreads.Add(new OwnThread
        {
            Id = "thread-upsert-run",
            Title = "Before",
            ExecutionDeviceId = "executor",
            ExecutionRunId = "run-upsert-active"
        });
        var body = JsonSerializer.Serialize(
            new ReplicationDomainMaterializer.TopicBody(
                Id: "thread-upsert-run",
                Title: "After",
                CreatedAt: at,
                SortOrder: 0,
                CommunicationDestinationDeviceId: null,
                CommunicationDestinationDeviceName: null,
                CommunicationDestinationDevicePlatform: null,
                AgentExecutionHostDeviceId: null,
                AgentExecutionHostDeviceName: null,
                AgentExecutionHostDevicePlatform: null,
                LastActivityAt: at,
                IsPinned: false,
                ExecutionAt: null,
                ExecutionRunId: null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsTrue(ReplicationProfileMaterializer.Apply(
            profile,
            new ReplicationPayloadCodec.DomainEnvelope(
                ReplicationOpKinds.Topic,
                ReplicationPayloadCodec.DomainAction.Upsert,
                "thread-upsert-run",
                null,
                "v2",
                body)));
        Assert.AreEqual(
            "run-upsert-active",
            profile.OwnThreads.Single().ExecutionRunId);
    }

    private static MeshDb.DeviceEnvelopeOutboxItem ReceiptOutbox(
        TopicRunUpdatePayload receipt,
        string targetDeviceId,
        DateTimeOffset createdAt)
        => new(
            TopicControlProtocol.EnvelopeId(
                TopicControlProtocol.ControlPurpose(receipt), receipt.RunId),
            targetDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(receipt),
            null,
            createdAt);

    [TestMethod]
    public void DesktopSelectionState_PersistsAndCanBeCleared()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.SetLastDesktopTopicId("topic-42");
            db.SetLastDesktopConversationKey("group:project");
        }
        SqliteConnection.ClearAllPools();

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual("topic-42", reopened.GetLastDesktopTopicId());
            Assert.AreEqual("group:project", reopened.GetLastDesktopConversationKey());
            reopened.SetLastDesktopTopicId(null);
            reopened.SetLastDesktopConversationKey(null);
        }
        SqliteConnection.ClearAllPools();

        using var cleared = MeshDb.Open(databasePath, key);
        Assert.IsNull(cleared.GetLastDesktopTopicId());
        Assert.IsNull(cleared.GetLastDesktopConversationKey());
    }

    [TestMethod]
    public void DesktopSelectionState_IsIndependentForEachIdentityDatabase()
    {
        var otherPath = Path.Combine(directory, "other.meshdb");
        using (var db = MeshDb.Open(databasePath, key))
            db.SetLastDesktopTopicId("first-topic");
        using (var other = MeshDb.Open(otherPath, key))
            other.SetLastDesktopTopicId("second-topic");

        using var first = MeshDb.Open(databasePath, key);
        using var second = MeshDb.Open(otherPath, key);
        Assert.AreEqual("first-topic", first.GetLastDesktopTopicId());
        Assert.AreEqual("second-topic", second.GetLastDesktopTopicId());
    }

    [TestMethod]
    public void Migration_SetsLastActivityAt_FromNewestChatLine()
    {
        var older = DateTimeOffset.UtcNow.AddHours(-2);
        var newer = DateTimeOffset.UtcNow.AddHours(-1);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("t1", "Test", older);
            db.AppendOwnChat("t1", new ChatLine { Id = "l1", Role = "user", Text = "hello", Via = "agent", At = older });
            db.AppendOwnChat("t1", new ChatLine { Id = "l2", Role = "assistant", Text = "hi", Via = "agent", At = newer });
            SaveProfile(db);
        }

        SqliteConnection.ClearAllPools();
        ClearOwnThreadActivity();
        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var thread = db2.LoadProfile()!.OwnThreads.FirstOrDefault(t => t.Id == "t1");
        Assert.IsNotNull(thread);
        Assert.IsNotNull(thread.LastActivityAt);
        Assert.AreEqual(newer.UtcTicks, thread.LastActivityAt!.Value.UtcTicks);
        Assert.IsTrue(thread.LastActivityAt > older, "Migration should use newest line, not created_at");
    }

    [TestMethod]
    public void Migration_OrdersOffsetTimestampsByInstant()
    {
        var older = new DateTimeOffset(2026, 1, 2, 2, 0, 0, TimeSpan.FromHours(14));
        var newer = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(-12));
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("offsets", "Offsets", older);
            db.AppendOwnChat("offsets", new ChatLine { Id = "old", At = older });
            db.AppendOwnChat("offsets", new ChatLine { Id = "new", At = newer });
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();
        ClearOwnThreadActivity();
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var thread = reopened.LoadProfile()!.OwnThreads.Single(t => t.Id == "offsets");
        Assert.AreEqual(newer.UtcTicks, thread.LastActivityAt!.Value.UtcTicks);
    }

    [TestMethod]
    public void Migration_SetsLastActivityAt_ToCreatedAt_WhenNoLines()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-1);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("t2", "Empty Thread", created);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();
        ClearOwnThreadActivity();
        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var thread = db2.LoadProfile()!.OwnThreads.FirstOrDefault(t => t.Id == "t2");
        Assert.IsNotNull(thread?.LastActivityAt, "Empty thread should get created_at as activity");
        Assert.AreEqual(created.UtcTicks, thread!.LastActivityAt!.Value.UtcTicks);
    }

    [TestMethod]
    public void SetOwnThreadPin_PersistsAndLoads()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("t3", "Pin Test", DateTimeOffset.UtcNow);
            db.SetOwnThreadPin("t3", true);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var thread = db2.LoadProfile()!.OwnThreads.FirstOrDefault(t => t.Id == "t3");
        Assert.IsTrue(thread?.IsPinned, "Pinned flag should persist across reopen");
    }

    [TestMethod]
    public void SetOwnThreadActivity_PersistsAndLoads()
    {
        var at = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("t4", "Activity Test", at.AddDays(-1));
            db.SetOwnThreadActivity("t4", at);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var thread = db2.LoadProfile()!.OwnThreads.FirstOrDefault(t => t.Id == "t4");
        Assert.IsNotNull(thread?.LastActivityAt);
        Assert.AreEqual(at.UtcTicks, thread!.LastActivityAt!.Value.UtcTicks);
    }

    [TestMethod]
    public void TopicReplyCorrelation_PersistsAndLoads()
    {
        var at = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("reply-order", "Reply order", at);
            db.AppendOwnChat("reply-order", new ChatLine
            {
                Id = "answer-1",
                Role = "assistant",
                Text = "done",
                ReplyToLineId = "prompt-1",
                At = at
            });
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var answer = reopened.LoadProfile()!.OwnThreads
            .Single(thread => thread.Id == "reply-order")
            .Lines.Single();
        Assert.AreEqual("prompt-1", answer.ReplyToLineId);
    }

    [TestMethod]
    public void ModelAttribution_PersistsAcrossChatStorageAndUpserts()
    {
        var at = new DateTimeOffset(2026, 7, 25, 11, 0, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("model-topic", "Model", at);
            var topicLine = new ChatLine
            {
                Id = "topic-model-line",
                Role = "assistant",
                Text = "answer",
                At = at,
                ModelId = "deepseek/deepseek-chat"
            };
            db.AppendOwnChat("model-topic", topicLine);
            topicLine.ModelId = "moonshotai/kimi-k2";
            db.UpsertOwnChat("model-topic", topicLine);

            db.EnsureConversation("model-conversation");
            var conversationLine = new ChatLine
            {
                Id = "conversation-model-line",
                Role = "assistant",
                Text = "answer",
                At = at,
                ModelId = "deepseek/deepseek-r1"
            };
            db.AppendChatLine("model-conversation", conversationLine);
            conversationLine.ModelId = "moonshotai/kimi-k2";
            db.UpsertChatLine("model-conversation", conversationLine);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var profile = reopened.LoadProfile()!;
        Assert.AreEqual(
            "moonshotai/kimi-k2",
            profile.OwnThreads.Single(thread => thread.Id == "model-topic").Lines.Single().ModelId);
        Assert.AreEqual(
            "moonshotai/kimi-k2",
            profile.Conversations.Single(conversation => conversation.Handle == "model-conversation").Lines.Single().ModelId);
    }

    [TestMethod]
    public void SetOwnThreadExecution_PersistsAndLoads()
    {
        var execAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread("t5", "Exec Test", DateTimeOffset.UtcNow);
            db.SetOwnThreadExecution("t5", "device123", execAt, "run-abc");
            SaveProfile(db);
        }

        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var thread = db2.LoadProfile()!.OwnThreads.FirstOrDefault(t => t.Id == "t5");
        Assert.AreEqual("device123", thread?.ExecutionDeviceId);
        Assert.AreEqual(execAt.UtcTicks, thread?.ExecutionAt?.UtcTicks);
        Assert.AreEqual("run-abc", thread?.ExecutionRunId);
    }

    [TestMethod]
    public void UpsertOwnThread_CanAuthoritativelyClearExecution()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread(
                "clear-execution", "Run", created, 0, created, false,
                "device123", created, "run-abc", replaceExecutionMetadata: true);
            db.UpsertOwnThread(
                "clear-execution", "Run", created, 0, created, false,
                null, null, null, replaceExecutionMetadata: true);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var thread = reopened.LoadProfile()!.OwnThreads.Single(t => t.Id == "clear-execution");
        Assert.IsNull(thread.ExecutionDeviceId);
        Assert.IsNull(thread.ExecutionAt);
        Assert.IsNull(thread.ExecutionRunId);
    }

    [TestMethod]
    public void Migration_ConversationActivity_FromNewestLine()
    {
        var older = DateTimeOffset.UtcNow.AddHours(-3);
        var newer = DateTimeOffset.UtcNow.AddHours(-1);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureConversation("alice");
            db.AppendChatLine("alice", new ChatLine { Id = "c1", Role = "user", Text = "hi", Via = "agent", At = older });
            db.AppendChatLine("alice", new ChatLine { Id = "c2", Role = "assistant", Text = "hey", Via = "agent", At = newer });
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();
        ClearConversationActivity();
        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var conv = db2.LoadProfile()!.Conversations.FirstOrDefault(c => c.Handle == "alice");
        Assert.IsNotNull(conv?.LastActivityAt);
        Assert.AreEqual(newer.UtcTicks, conv!.LastActivityAt!.Value.UtcTicks);
        Assert.IsTrue(conv.LastActivityAt > older, "Should use newest line timestamp");
    }

    [TestMethod]
    public void SetConversationPin_PersistsAndLoads()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureConversation("bob");
            db.SetConversationPin("bob", true);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var db2 = MeshDb.Open(databasePath, key);
        var conv = db2.LoadProfile()!.Conversations.FirstOrDefault(c => c.Handle == "bob");
        Assert.IsTrue(conv?.IsPinned, "Pin should persist");
    }

    [TestMethod]
    public void FirstLines_InitializeAndPersistNullableActivity()
    {
        var created = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var topicLineAt = created.AddHours(1);
        var conversationLineAt = created.AddHours(2);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread("first-topic", "First", created, 0);
            db.UpsertConversation(
                "first-conversation", 0, null, null, null, null, null, null, [], 0);
            db.AppendOwnChat("first-topic", new ChatLine { Id = "topic-line", At = topicLineAt });
            db.AppendChatLine(
                "first-conversation",
                new ChatLine { Id = "conversation-line", At = conversationLineAt });
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var profile = reopened.LoadProfile()!;
        Assert.AreEqual(
            topicLineAt.UtcTicks,
            profile.OwnThreads.Single(t => t.Id == "first-topic").LastActivityAt?.UtcTicks);
        Assert.AreEqual(
            conversationLineAt.UtcTicks,
            profile.Conversations.Single(c => c.Handle == "first-conversation")
                .LastActivityAt?.UtcTicks);
    }

    [TestMethod]
    public void ExecutionDeviceMetadata_BindMoveAndRunPersist()
    {
        var created = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var moved = created.AddHours(1);
        var runAt = moved.AddMinutes(5);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread("targeted", "Targeted", created, 0);
            Assert.IsTrue(db.TryAssignOwnThreadAgentExecutionHost(
                "targeted", "phone-id", "Phone", "android"));
            Assert.IsFalse(db.TryAssignOwnThreadAgentExecutionHost(
                "targeted", "other-id", "Other", "windows"));
            Assert.IsTrue(db.MoveOwnThreadAgentExecutionHost(
                "targeted", "desktop-id", "Desktop", "windows", moved));
            Assert.IsTrue(db.SetOwnThreadExecutionAndActivity(
                "targeted", "desktop-id", "Desktop", "windows",
                runAt, "run-1", runAt));
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var thread = reopened.LoadProfile()!.OwnThreads.Single(t => t.Id == "targeted");
        Assert.AreEqual("desktop-id", thread.ExecutionDeviceId);
        Assert.AreEqual("Desktop", thread.ExecutionDeviceName);
        Assert.AreEqual("windows", thread.ExecutionDevicePlatform);
        Assert.AreEqual("run-1", thread.ExecutionRunId);
        Assert.AreEqual(runAt.UtcTicks, thread.ExecutionAt?.UtcTicks);
        Assert.AreEqual(runAt.UtcTicks, thread.LastActivityAt?.UtcTicks);
    }

    [TestMethod]
    public void AssistantAiRequest_ReopenReassignAndRetryRetainExactIdentity()
    {
        var created = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread("durable-topic", "Durable topic", created, 0);
            SaveProfile(db);
            var first = db.CreateAssistantAiRequest(
                "stable-run",
                "stable-operation",
                "durable-topic",
                "stable-line",
                "account-a",
                7,
                null,
                created);
            Assert.AreEqual(AssistantAiRequestState.AwaitingHost, first.State);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var restored = reopened.GetPendingAssistantAiRequest("durable-topic");
        Assert.IsNotNull(restored);
        Assert.AreEqual("stable-run", restored.RunId);
        Assert.AreEqual("stable-operation", restored.OperationId);
        Assert.AreEqual("stable-line", restored.TriggerLineId);
        var reassigned = reopened.ReassignAssistantAiRequest(
            restored.RunId,
            new AgentExecutionHost("desktop-b", "Desktop B", DevicePlatforms.Windows),
            created.AddMinutes(1));
        Assert.AreEqual("stable-run", reassigned.RunId);
        Assert.AreEqual("stable-line", reassigned.TriggerLineId);
        var retry = reopened.SetAssistantAiRequestState(
            reassigned.RunId,
            AssistantAiRequestState.RetryPending,
            created.AddMinutes(2),
            "offline",
            incrementAttempt: true);
        Assert.AreEqual(1, retry.DispatchAttempts);

        var duplicate = reopened.CreateAssistantAiRequest(
            "stable-run",
            "stable-operation",
            "durable-topic",
            "stable-line",
            "account-a",
            7,
            new AgentExecutionHost("desktop-b", "Desktop B", DevicePlatforms.Windows),
            created.AddMinutes(3));
        Assert.AreEqual("stable-run", duplicate.RunId);
        Assert.AreEqual(1, duplicate.DispatchAttempts);
        Assert.IsTrue(reopened.TryCompleteAssistantAiRequest(
            duplicate.RunId,
            created.AddMinutes(4)));
        Assert.IsNull(reopened.GetPendingAssistantAiRequest("durable-topic"));
    }

    [TestMethod]
    public void MeTopic_IgnoresCommunicationMoveAndPersistsAiHost()
    {
        var created = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread("two-plane", "Two plane", created, 0);
            db.SetOwnThreadCommunicationDestination(
                "two-plane", "phone-a", "Phone A", DevicePlatforms.Android, created);
            Assert.IsTrue(db.TryAssignOwnThreadAgentExecutionHost(
                "two-plane", "desktop-a", "Desktop A", DevicePlatforms.Windows));
            db.SetOwnThreadCommunicationDestination(
                "two-plane", "phone-b", "Phone B", DevicePlatforms.IOS, created.AddMinutes(1));
            Assert.IsTrue(db.MoveOwnThreadAgentExecutionHost(
                "two-plane", "desktop-b", "Desktop B", DevicePlatforms.Windows, created.AddMinutes(2)));
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var thread = reopened.LoadProfile()!.OwnThreads.Single(item => item.Id == "two-plane");
        Assert.AreEqual(ConversationKind.Assistant, thread.ConversationKind);
        Assert.IsNull(thread.CommunicationDestinationDeviceId);
        Assert.IsNull(thread.CommunicationDestinationDevicePlatform);
        Assert.AreEqual("desktop-b", thread.AgentExecutionHostDeviceId);
        Assert.AreEqual(DevicePlatforms.Windows, thread.AgentExecutionHostDevicePlatform);
    }

    [TestMethod]
    public void UpsertConversation_NewerMetadataReplacesCreatedAt_LegacyDoesNot()
    {
        var original = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var accepted = original.AddDays(-10);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertConversation(
                "created", 0, null, null, null, null, null, null, [], 0,
                original, original, replaceCreatedAt: true);
            db.UpsertConversation(
                "created", 0, null, null, null, null, null, null, [], 0,
                accepted, original, replaceCreatedAt: true);
            db.UpsertConversation(
                "created", 0, null, null, null, null, null, null, [], 0);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var conversation = reopened.LoadProfile()!.Conversations.Single(c => c.Handle == "created");
        Assert.AreEqual(accepted.UtcTicks, conversation.CreatedAt?.UtcTicks);
    }

    [TestMethod]
    public void MetadataUpserts_CannotRegressActivityAcrossOffsets()
    {
        var newest = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(-12));
        var stale = new DateTimeOffset(2026, 1, 2, 2, 0, 0, TimeSpan.FromHours(14));
        Assert.IsTrue(newest > stale);

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread("monotonic-topic", "Topic", stale, 0, newest);
            db.UpsertOwnThread("monotonic-topic", "Topic", stale, 0, stale);
            db.UpsertConversation(
                "monotonic-conversation", 0, null, null, null, null, null, null, [], 0,
                stale, newest);
            db.UpsertConversation(
                "monotonic-conversation", 0, null, null, null, null, null, null, [], 0,
                stale, stale);
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var profile = reopened.LoadProfile()!;
        Assert.AreEqual(
            newest.UtcTicks,
            profile.OwnThreads.Single(t => t.Id == "monotonic-topic").LastActivityAt?.UtcTicks);
        Assert.AreEqual(
            newest.UtcTicks,
            profile.Conversations.Single(c => c.Handle == "monotonic-conversation")
                .LastActivityAt?.UtcTicks);
    }

    [TestMethod]
    public void InvalidSyncLine_HasZeroSideEffects_AndLaterOlderVersionApplies()
    {
        const string threadId = "sync-parent";
        const string versionKey = "topic.line.upsert\u001fsync-parent\u001fline";
        var parentAt = DateTimeOffset.UtcNow.AddDays(2);
        var invalidVersion = ProjectionVersion.Create(
            DateTimeOffset.UtcNow.AddMinutes(2), "remote", "invalid");
        var validVersion = ProjectionVersion.Create(
            DateTimeOffset.UtcNow.AddMinutes(1), "remote", "valid");

        using var db = MeshDb.Open(databasePath, key);
        db.UpsertOwnThread(threadId, "Parent", parentAt.AddDays(-1), 0, parentAt, true);
        SaveProfile(db);
        var invalid = new ChatLine
        {
            Id = "line",
            Role = "user",
            Text = "invalid",
            Via = "device",
            Status = "sent",
            At = default
        };

        Assert.IsFalse(db.TryApplyOwnSyncLine(
            threadId, invalid, versionKey, invalidVersion));
        var unchanged = db.LoadProfile()!.OwnThreads.Single(t => t.Id == threadId);
        Assert.AreEqual("Parent", unchanged.Title);
        Assert.AreEqual(parentAt.UtcTicks, unchanged.LastActivityAt?.UtcTicks);
        Assert.IsTrue(unchanged.IsPinned);
        Assert.AreEqual(0, unchanged.Lines.Count);
        Assert.IsNull(db.GetSyncVersion(versionKey));

        var valid = new ChatLine
        {
            Id = "line",
            Role = "user",
            Text = "valid",
            Via = "device",
            Status = "sent",
            At = parentAt.AddDays(-1),
            ModelId = "deepseek/deepseek-chat"
        };
        Assert.IsTrue(db.TryApplyOwnSyncLine(
            threadId, valid, versionKey, validVersion));
        var applied = db.LoadProfile()!.OwnThreads.Single(t => t.Id == threadId);
        Assert.HasCount(1, applied.Lines);
        Assert.AreEqual("valid", applied.Lines[0].Text);
        Assert.AreEqual("deepseek/deepseek-chat", applied.Lines[0].ModelId);
        Assert.AreEqual(parentAt.UtcTicks, applied.LastActivityAt?.UtcTicks);
        Assert.AreEqual(validVersion, db.GetSyncVersion(versionKey));

        var newerVersion = ProjectionVersion.Create(
            DateTimeOffset.UtcNow.AddMinutes(3), "remote", "newer");
        valid.Text = "updated by older client";
        valid.ModelId = null;
        Assert.IsTrue(db.TryApplyOwnSyncLine(
            threadId, valid, versionKey, newerVersion));
        var updated = db.LoadProfile()!.OwnThreads.Single(t => t.Id == threadId).Lines.Single();
        Assert.AreEqual("updated by older client", updated.Text);
        Assert.AreEqual("deepseek/deepseek-chat", updated.ModelId);
    }

    [TestMethod]
    public void TopicLineDelete_RemovesPromptAndRepliesAndBlocksResurrection()
    {
        const string threadId = "topic-delete-parent";
        const string otherThreadId = "topic-delete-other";
        const string lineId = "cancelled-line";
        const string versionKey = "topic.line.upsert\u001ftopic-delete-parent\u001fcancelled-line";
        var at = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var upsertVersion = ProjectionVersion.Create(at, "mobile", "upsert");
        var deleteVersion = ProjectionVersion.Create(at.AddMinutes(1), "mobile", "delete");
        var deleteEntityId = DomainProjectionEntityIds.TopicLine(threadId, lineId);

        using var db = MeshDb.Open(databasePath, key);
        SaveProfile(db);
        db.UpsertOwnThread(threadId, "Topic", at.AddDays(-1), 0, at, false);
        db.UpsertOwnThread(otherThreadId, "Other", at.AddDays(-1), 1, at, false);
        var prompt = new ChatLine
        {
            Id = lineId,
            Role = "user",
            Text = "Do not run this",
            At = at
        };
        Assert.IsTrue(db.TryApplyOwnSyncLine(
            threadId, prompt, versionKey, upsertVersion, DomainProjectionKinds.TopicLineDelete));
        db.AppendOwnChat(threadId, new ChatLine
        {
            Id = "reply",
            Role = "assistant",
            Text = "late answer",
            ReplyToLineId = lineId,
            At = at.AddSeconds(1)
        });
        db.AppendOwnChat(threadId, new ChatLine
        {
            Id = "keep",
            Role = "user",
            Text = "keep me",
            At = at.AddSeconds(2)
        });
        db.AppendOwnChat(otherThreadId, new ChatLine
        {
            Id = lineId,
            Role = "user",
            Text = "same id, other topic",
            At = at.AddSeconds(3)
        });

        db.ApplyTopicLineDelete(
            threadId, lineId, deleteEntityId, DomainProjectionKinds.TopicLineDelete, deleteVersion);

        var profile = db.LoadProfile()!;
        var remaining = profile.OwnThreads.Single(thread => thread.Id == threadId).Lines;
        Assert.HasCount(1, remaining);
        Assert.AreEqual("keep", remaining[0].Id);
        Assert.HasCount(
            1,
            profile.OwnThreads.Single(thread => thread.Id == otherThreadId).Lines);
        Assert.AreEqual(
            deleteVersion,
            db.GetSyncTombstoneVersion(DomainProjectionKinds.TopicLineDelete, deleteEntityId));

        prompt.Text = "resurrected";
        Assert.IsFalse(db.TryApplyOwnSyncLine(
            threadId,
            prompt,
            versionKey,
            ProjectionVersion.Create(at.AddMinutes(2), "desktop", "resurrect"),
            DomainProjectionKinds.TopicLineDelete));
    }

    [TestMethod]
    public void ComposerDrafts_PersistIndependentlyAndClear()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.SetConversationDraft("alice", "message draft");
            db.SetConversationDraft("bob", "other message draft");
            db.SetTopicDraft("alice", "topic draft");
        }
        SqliteConnection.ClearAllPools();

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual("message draft", reopened.GetConversationDraft("alice"));
            Assert.AreEqual("other message draft", reopened.GetConversationDraft("bob"));
            Assert.AreEqual("topic draft", reopened.GetTopicDraft("alice"));
            Assert.AreEqual("", reopened.GetTopicDraft("missing"));
            reopened.SetConversationDraft("alice", "");
        }
        SqliteConnection.ClearAllPools();

        using var cleared = MeshDb.Open(databasePath, key);
        Assert.AreEqual("", cleared.GetConversationDraft("alice"));
        Assert.AreEqual("other message draft", cleared.GetConversationDraft("bob"));
        Assert.AreEqual("topic draft", cleared.GetTopicDraft("alice"));
    }

    [TestMethod]
    public void DeletingConversationOrTopic_RemovesOnlyItsComposerDraft()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.EnsureConversation("shared");
        db.EnsureOwnThread("shared", "Topic", DateTimeOffset.UtcNow);
        db.SetConversationDraft("shared", "message draft");
        db.SetTopicDraft("shared", "topic draft");

        db.DeleteConversation("shared");

        Assert.AreEqual("", db.GetConversationDraft("shared"));
        Assert.AreEqual("topic draft", db.GetTopicDraft("shared"));

        db.DeleteOwnThread("shared");

        Assert.AreEqual("", db.GetTopicDraft("shared"));
    }

    [TestMethod]
    public void BeginRemoteTopicRun_AtomicallyCreatesFirstRunAndIsRestartIdempotent()
    {
        var at = new DateTimeOffset(2026, 8, 24, 21, 1, 2, TimeSpan.Zero);
        var command = CreateBeginCommand(
            "run-first-remote",
            "thread-first-remote",
            "line-first-remote",
            "remote-device",
            at,
            TopicRunBeginMode.Remote);

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread(command.Draft.ThreadId, "First remote", at.AddMinutes(-1));
            SaveProfile(db);
            var result = db.BeginTopicRun(command);

            Assert.IsTrue(result.Committed);
            Assert.IsTrue(result.Created);
            Assert.IsNotNull(result.Outbox);
            Assert.AreEqual(command.Draft.RunId, result.Outbox.EnvelopeId);
            Assert.IsNotNull(db.GetTopicRunCorrelation(command.Draft.RunId));
            var profile = db.LoadProfile()!;
            var thread = profile.OwnThreads.Single(item => item.Id == command.Draft.ThreadId);
            Assert.AreEqual(command.Draft.RunId, thread.ExecutionRunId);
            Assert.AreEqual(command.Target.DeviceId, thread.ExecutionDeviceId);
            Assert.AreEqual(
                1,
                thread.Lines.Count(line => line.Id == command.Draft.TriggerLineId));
        }
        SqliteConnection.ClearAllPools();

        using var restarted = MeshDb.Open(databasePath, key);
        var retry = restarted.BeginTopicRun(command);
        Assert.IsTrue(retry.Committed);
        Assert.IsFalse(retry.Created);
        Assert.AreEqual("already_started", retry.Code);
        Assert.HasCount(
            1,
            restarted.ListTopicOutbox()
                .Where(item => item.RunId == command.Draft.RunId)
                .ToList());
        Assert.AreEqual(
            1,
            restarted.LoadProfile()!.OwnThreads
                .Single(item => item.Id == command.Draft.ThreadId)
                .Lines.Count(line => line.Id == command.Draft.TriggerLineId));
    }

    [TestMethod]
    public void BeginRemoteTopicRun_SameTriggerWithNewRunIdReusesDurableAuthority()
        {
            var at = new DateTimeOffset(2026, 8, 24, 21, 1, 10, TimeSpan.Zero);
            var originalBase = CreateBeginCommand(
                "authoritative-run",
                "stable-trigger-thread",
                "stable-trigger-line",
                "remote-device",
                at,
                TopicRunBeginMode.Remote);
            var original = originalBase with
            {
                Draft = originalBase.Draft with
                {
                    TriggerOperationId = CreateJournaledOperationId(
                        originalBase.Draft.ThreadId,
                        originalBase.Draft.TargetDeviceId!,
                        at)
                }
            };
            var proposed = RebindRun(original, "newly-proposed-run");
            using (var db = MeshDb.Open(databasePath, key))
            {
                db.EnsureOwnThread(original.Draft.ThreadId, "Stable trigger", at.AddMinutes(-1));
                SaveProfile(db);
                Assert.IsTrue(db.BeginTopicRun(original).Created);
            }
            SqliteConnection.ClearAllPools();

            using var restarted = MeshDb.Open(databasePath, key);
            var retry = restarted.BeginTopicRun(proposed);

            Assert.IsTrue(retry.DurableCommitted);
            Assert.IsFalse(retry.Created);
            Assert.AreEqual(original.Draft.RunId, retry.AuthoritativeRunId);
            Assert.AreEqual(original.Draft.RunId, retry.AuthoritativeDraft!.RunId);
            Assert.AreEqual(original.Draft.RunId, retry.Outbox!.RunId);
            Assert.IsNull(restarted.GetTopicOutbox(proposed.Draft.RunId));
            Assert.HasCount(1, restarted.ListTopicOutbox());
            Assert.AreEqual(
                original.Draft.RunId,
                restarted.GetTopicRunTrigger(
                    TopicRunTriggerIdentity.For(
                        original.Draft.ThreadId,
                        original.Draft.TriggerLineId,
                        original.Draft.TriggerOperationId))!.RunId);
            var firstAttempt = restarted.BeginTopicTransportAttempt(original.Draft.RunId);
            var secondAttempt = restarted.BeginTopicTransportAttempt(original.Draft.RunId);
            Assert.AreEqual(1, firstAttempt!.Ordinal);
            Assert.AreEqual(2, secondAttempt!.Ordinal);
            Assert.AreEqual(firstAttempt.TriggerId, secondAttempt.TriggerId);
            Assert.AreEqual(
                2,
                restarted.GetTopicOutbox(original.Draft.RunId)!.TransportAttemptOrdinal);
        }

    [TestMethod]
    public void BeginRemoteTopicRun_ConflictingStableTriggerIsExplicitlyRejected()
        {
            var at = new DateTimeOffset(2026, 8, 24, 21, 1, 20, TimeSpan.Zero);
            var originalBase = CreateBeginCommand(
                "conflict-authority",
                "conflict-thread",
                "conflict-line",
                "remote-device",
                at,
                TopicRunBeginMode.Remote);
            var original = originalBase with
            {
                Draft = originalBase.Draft with
                {
                    TriggerOperationId = CreateJournaledOperationId(
                        originalBase.Draft.ThreadId,
                        originalBase.Draft.TargetDeviceId!,
                        at)
                }
            };
            using var db = MeshDb.Open(databasePath, key);
            db.EnsureOwnThread(original.Draft.ThreadId, "Conflict", at.AddMinutes(-1));
            SaveProfile(db);
            Assert.IsTrue(db.BeginTopicRun(original).Created);
            var changed = RebindRun(original, "conflict-proposed") with
            {
                Draft = RebindRun(original, "conflict-proposed").Draft with
                {
                    Prompt = "different private prompt"
                }
            };

            var rejected = db.BeginTopicRun(changed);

            Assert.IsFalse(rejected.DurableCommitted);
            Assert.AreEqual("trigger_identity_conflict", rejected.Code);
            Assert.IsNull(db.GetTopicOutbox("conflict-proposed"));
            Assert.HasCount(1, db.ListTopicOutbox());

            var otherTarget = RebindRun(original, "conflict-other-target");
            otherTarget = otherTarget with
            {
                Draft = otherTarget.Draft with { TargetDeviceId = "other-device" },
                Target = otherTarget.Target with { DeviceId = "other-device" },
                Request = otherTarget.Request! with { TargetDeviceId = "other-device" }
            };
            var targetRejected = db.BeginTopicRun(otherTarget);
            Assert.IsFalse(targetRejected.DurableCommitted);
            Assert.AreEqual("trigger_identity_conflict", targetRejected.Code);

            var otherTopic = RebindRun(original, "conflict-other-topic");
            otherTopic = otherTopic with
            {
                Draft = otherTopic.Draft with { ThreadId = "other-topic" },
                InitialProjection = otherTopic.InitialProjection with
                {
                    ThreadId = "other-topic"
                },
                Request = otherTopic.Request! with { ThreadId = "other-topic" }
            };
            db.EnsureOwnThread("other-topic", "Other", at);
            var topicRejected = db.BeginTopicRun(otherTopic);
            Assert.IsFalse(topicRejected.DurableCommitted);
            Assert.AreEqual("trigger_identity_conflict", topicRejected.Code);
        }

    [TestMethod]
    public void TerminalTriggerLedger_SurvivesRestartUntilSupportedJournalLifetime()
        {
            var at = new DateTimeOffset(2026, 8, 24, 21, 1, 25, TimeSpan.Zero);
            var time = new ManualTimeProvider(at);
            var command = CreateBeginCommand(
                "retained-authority",
                "retained-thread",
                "retained-line",
                "remote-device",
                at,
                TopicRunBeginMode.Remote);
            var triggerId = TopicRunTriggerIdentity.For(
                command.Draft.ThreadId, command.Draft.TriggerLineId);
            using (var db = MeshDb.Open(databasePath, key, time))
            {
                db.EnsureOwnThread(command.Draft.ThreadId, "Retained", at.AddMinutes(-1));
                SaveProfile(db);
                Assert.IsTrue(db.BeginTopicRun(command).Created);
                db.CompleteTopicOutbox(command.Draft.RunId, at.AddSeconds(1));
                Assert.IsNotNull(db.GetTopicRunTrigger(triggerId));
            }
            SqliteConnection.ClearAllPools();

            using var restarted = MeshDb.Open(databasePath, key, time);
            time.Advance(TopicTransportPolicy.TriggerLedgerRetention - TimeSpan.FromSeconds(1));
            restarted.PruneTopicRunCorrelations(time.GetUtcNow());
            Assert.IsNotNull(restarted.GetTopicRunTrigger(triggerId));
            var retry = restarted.BeginTopicRun(RebindRun(command, "retained-retry"));
            Assert.IsTrue(retry.DurableCommitted);
            Assert.AreEqual(command.Draft.RunId, retry.AuthoritativeRunId);

            time.Advance(TimeSpan.FromSeconds(2));
            restarted.PruneTopicRunCorrelations(time.GetUtcNow());
            Assert.IsNull(restarted.GetTopicRunTrigger(triggerId));
        }
    [TestMethod]
    public void BeginRemoteTopicRun_QueuesDurablyBehindActiveRunWithoutReplacingIt()
    {
        var at = new DateTimeOffset(2026, 8, 24, 21, 1, 30, TimeSpan.Zero);
        var active = CreateBeginCommand(
            "run-active",
            "thread-queued",
            "line-active",
            "remote-device",
            at,
            TopicRunBeginMode.Remote);
        var queued = CreateBeginCommand(
            "run-queued",
            active.Draft.ThreadId,
            "line-queued",
            active.Target.DeviceId,
            at.AddSeconds(1),
            TopicRunBeginMode.Remote);
        using var db = MeshDb.Open(databasePath, key);
        db.EnsureOwnThread(active.Draft.ThreadId, "Queued", at.AddMinutes(-1));
        SaveProfile(db);

        Assert.IsTrue(db.BeginTopicRun(active).Committed);
        var result = db.BeginTopicRun(queued);

        Assert.IsTrue(result.Committed);
        Assert.IsTrue(result.Created);
        Assert.AreEqual(
            active.Draft.RunId,
            db.LoadProfile()!.OwnThreads.Single().ExecutionRunId);
        Assert.IsNotNull(db.GetTopicRunCorrelation(queued.Draft.RunId));
        Assert.IsNotNull(db.GetTopicOutbox(queued.Draft.RunId));
        Assert.HasCount(2, db.ListTopicOutbox());
    }

    [TestMethod]
    public void BeginLocalTopicRun_PersistsWithoutFakeRemoteCorrelation()
    {
        var at = new DateTimeOffset(2026, 8, 24, 21, 2, 2, TimeSpan.Zero);
        var command = CreateBeginCommand(
            "run-first-local",
            "thread-first-local",
            "line-first-local",
            "local-device",
            at,
            TopicRunBeginMode.Local);

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.EnsureOwnThread(command.Draft.ThreadId, "First local", at.AddMinutes(-1));
            SaveProfile(db);
            var result = db.BeginTopicRun(command);
            Assert.IsTrue(result.Committed);
            Assert.IsTrue(result.Created);
            Assert.IsNull(result.Outbox);
            Assert.IsNotNull(db.GetLocalTopicRun(command.Draft.RunId));
            Assert.IsNull(db.GetTopicOutbox(command.Draft.RunId));
            Assert.IsNull(db.GetTopicRunCorrelation(command.Draft.RunId));
            db.CompleteLocalTopicRun(command.Draft.RunId, at.AddMinutes(1));
        }
        SqliteConnection.ClearAllPools();

        using var restarted = MeshDb.Open(databasePath, key);
        var retry = restarted.BeginTopicRun(command);
        Assert.IsTrue(retry.Committed);
        Assert.IsFalse(retry.Created);
        Assert.AreEqual("already_completed", retry.Code);
        Assert.IsNull(restarted.GetTopicRunCorrelation(command.Draft.RunId));
    }

    [TestMethod]
    public void BeginTopicRun_FailureAtEveryTransactionStepRollsBackAndRetainsDraft()
    {
        foreach (var mode in new[] { TopicRunBeginMode.Remote, TopicRunBeginMode.Local })
        {
            var checkpoints = mode == TopicRunBeginMode.Remote
                ? new[]
                {
                    MeshDb.TopicRunBeginCheckpoint.ThreadBound,
                    MeshDb.TopicRunBeginCheckpoint.PromptPersisted,
                    MeshDb.TopicRunBeginCheckpoint.OutboxPersisted,
                    MeshDb.TopicRunBeginCheckpoint.CorrelationPersisted,
                    MeshDb.TopicRunBeginCheckpoint.TriggerPersisted,
                    MeshDb.TopicRunBeginCheckpoint.BeforeCommit
                }
                : new[]
                {
                    MeshDb.TopicRunBeginCheckpoint.ThreadBound,
                    MeshDb.TopicRunBeginCheckpoint.PromptPersisted,
                    MeshDb.TopicRunBeginCheckpoint.LocalRunPersisted,
                    MeshDb.TopicRunBeginCheckpoint.TriggerPersisted,
                    MeshDb.TopicRunBeginCheckpoint.BeforeCommit
                };
            foreach (var failAt in checkpoints)
            {
                var suffix = $"{mode}-{failAt}";
                var path = Path.Combine(directory, suffix + ".meshdb");
                var at = new DateTimeOffset(2026, 8, 24, 21, 3, 2, TimeSpan.Zero);
                var command = CreateBeginCommand(
                    "run-" + suffix,
                    "thread-" + suffix,
                    "line-" + suffix,
                    mode == TopicRunBeginMode.Remote ? "remote-device" : "local-device",
                    at,
                    mode);
                using var db = MeshDb.Open(path, key);
                db.EnsureOwnThread(command.Draft.ThreadId, suffix, at.AddMinutes(-1));
                SaveProfile(db);
                db.SetTopicDraft(command.Draft.ThreadId, "retained retry draft");

                Assert.ThrowsExactly<InjectedBeginFailure>(() =>
                    db.BeginTopicRun(
                        command,
                        checkpoint =>
                        {
                            if (checkpoint == failAt) throw new InjectedBeginFailure();
                        }));

                Assert.IsNull(db.GetTopicOutbox(command.Draft.RunId), suffix);
                Assert.IsNull(db.GetTopicRunCorrelation(command.Draft.RunId), suffix);
                Assert.IsNull(db.GetLocalTopicRun(command.Draft.RunId), suffix);
                var thread = db.LoadProfile()!.OwnThreads.Single(item =>
                    item.Id == command.Draft.ThreadId);
                Assert.IsNull(thread.ExecutionRunId, suffix);
                Assert.IsFalse(
                    thread.Lines.Any(line => line.Id == command.Draft.TriggerLineId),
                    suffix);
                Assert.AreEqual(
                    "retained retry draft",
                    db.GetTopicDraft(command.Draft.ThreadId),
                    suffix);
            }
        }
    }

    [TestMethod]
    public async Task BeginRemoteTopicRun_TransportFailureLeavesOneDurableRetryEnvelope()
    {
        var at = new DateTimeOffset(2026, 8, 24, 21, 4, 2, TimeSpan.Zero);
        var time = new ManualTimeProvider(at);
        var command = CreateBeginCommand(
            "run-transport-retry",
            "thread-transport-retry",
            "line-transport-retry",
            "remote-device",
            at,
            TopicRunBeginMode.Remote);
        var envelopes = new List<string>();
        using var db = MeshDb.Open(databasePath, key, time);
        db.EnsureOwnThread(command.Draft.ThreadId, "Transport retry", at.AddMinutes(-1));
        SaveProfile(db);
        var begin = db.BeginTopicRun(command);
        var handler = new TopicRequestOutboxHandler(db, time);
        var failedDelivery = new TopicRequestOutboxDelivery(
            handler,
            new ControlledTopicTransport((_, _, _, envelope, _, _) =>
            {
                envelopes.Add(envelope);
                return Task.FromResult<MeshSendResult?>(
                    MeshSendResult.Reject("temporarily_unavailable"));
            }),
            time);

        var failed = await failedDelivery.TrySendAsync(
            begin.Outbox!, CancellationToken.None);

        Assert.IsFalse(failed.TransportResult!.Accepted);
        Assert.AreEqual(
            TopicOutboxStates.Pending,
            db.GetTopicOutbox(command.Draft.RunId)!.State);
        Assert.IsNotNull(db.GetTopicRunCorrelation(command.Draft.RunId));
        Assert.HasCount(1, db.ListTopicOutbox());

        var retryDelivery = new TopicRequestOutboxDelivery(
            handler,
            new ControlledTopicTransport((_, _, _, envelope, _, _) =>
            {
                envelopes.Add(envelope);
                return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
            }),
            time);
        var retry = await retryDelivery.TrySendAsync(
            db.GetTopicOutbox(command.Draft.RunId)!,
            CancellationToken.None);

        Assert.IsTrue(retry.TransportResult!.Accepted);
        CollectionAssert.AreEqual(
            new[] { command.Draft.RunId, command.Draft.RunId },
            envelopes);
        Assert.HasCount(1, db.ListTopicOutbox());
        Assert.AreEqual(
            TopicOutboxStates.RelayQueued,
            db.GetTopicOutbox(command.Draft.RunId)!.State);
    }

    [TestMethod]
    public void ReliableTopicState_PersistsAcrossRestartAndDeduplicatesInboundRuns()
    {
        var created = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-reliable",
            "thread-reliable",
            "line-reliable",
            "owner",
            "Do reliable work",
            created,
            "laptop-device",
            TopicTurnMode.Single);
        var attachment = new ChatAttachment("note.txt", "text/plain", [1, 2, 3]);
        var topic = new MeshDb.TopicOutboxItem(
            request.RunId,
            request.ThreadId,
            request.TriggerLineId,
            request.TargetDeviceId,
            request,
            [attachment],
            TopicOutboxStates.Pending,
            created,
            created);
        var inbound = new MeshDb.InboundTopicRunItem(
            request.RunId,
            "phone-device",
            request,
            InboundTopicRunStates.Accepted,
            created,
            created);
        var terminalUpdate = new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Completed,
            Timestamp: created.AddMinutes(1));
        var envelope = new MeshDb.DeviceEnvelopeOutboxItem(
            "terminal-envelope",
            "phone-device",
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(terminalUpdate),
            PushHintProtocol.TopicResponse,
            created.AddMinutes(1));

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertTopicOutbox(topic);
            Assert.IsTrue(db.TryAddInboundTopicRun(inbound));
            Assert.IsFalse(db.TryAddInboundTopicRun(inbound));
            db.UpsertDeviceEnvelopeOutbox(envelope);
        }

        SqliteConnection.ClearAllPools();

        using (var reopened = MeshDb.Open(databasePath, key))
        {
            var restoredTopic = reopened.GetTopicOutbox(request.RunId);
            Assert.IsNotNull(restoredTopic);
            Assert.AreEqual(3, restoredTopic.Attachments.Single().Data.Length);
            Assert.AreEqual(TopicOutboxStates.Pending, restoredTopic.State);

            var restoredInbound = reopened.GetInboundTopicRun(request.RunId);
            Assert.IsNotNull(restoredInbound);
            Assert.AreEqual("phone-device", restoredInbound.SourceDeviceId);
            Assert.IsFalse(reopened.TryAddInboundTopicRun(inbound));

            var restoredEnvelope = reopened.ListDeviceEnvelopeOutbox().Single();
            Assert.AreEqual("terminal-envelope", restoredEnvelope.EnvelopeId);
            Assert.AreEqual(PushHintProtocol.TopicResponse, restoredEnvelope.PushHint);

            reopened.SetTopicOutboxState(request.RunId, TopicOutboxStates.RelayQueued);
            Assert.IsTrue(reopened.SetInboundTopicRunState(request.RunId, InboundTopicRunStates.Running));
            Assert.IsTrue(reopened.SetInboundTopicRunTerminal(
                request.RunId, InboundTopicRunStates.Completed, terminalUpdate));
            Assert.IsTrue(reopened.SetInboundTopicRunTerminal(
                request.RunId,
                InboundTopicRunStates.Failed,
                terminalUpdate with { Phase = TopicRunPhase.Failed, Error = "conflict" }));
            Assert.IsFalse(reopened.SetInboundTopicRunState(
                request.RunId, InboundTopicRunStates.Running));
            reopened.DeleteDeviceEnvelopeOutbox(envelope.EnvelopeId);
        }
        SqliteConnection.ClearAllPools();

        using var final = MeshDb.Open(databasePath, key);
        Assert.AreEqual(TopicOutboxStates.RelayQueued, final.GetTopicOutbox(request.RunId)!.State);
        var finalInbound = final.GetInboundTopicRun(request.RunId)!;
        Assert.AreEqual(InboundTopicRunStates.Completed, finalInbound.State);
        Assert.IsTrue(TopicRunProtocol.TryParseUpdate(
            finalInbound.TerminalUpdateJson, out var persistedTerminal));
        Assert.AreEqual(TopicRunPhase.Completed, persistedTerminal.Phase);
        Assert.HasCount(0, final.ListDeviceEnvelopeOutbox());
    }

    [TestMethod]
    public void InboundAcceptanceAndDurableAck_CommitAtomicallyWithStableIdentity()
    {
        var acceptedAt = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-atomic-accept",
            "thread-atomic-accept",
            "line-atomic-accept",
            "owner",
            "Run exactly once",
            acceptedAt,
            "desktop-device",
            TopicTurnMode.Single);
        var inbound = new MeshDb.InboundTopicRunItem(
            request.RunId,
            "phone-device",
            request,
            InboundTopicRunStates.Accepted,
            acceptedAt,
            acceptedAt);
        var accepted = TopicAcceptancePolicy.Create(request, acceptedAt);
        var acceptance = new MeshDb.DeviceEnvelopeOutboxItem(
            "stable-acceptance-envelope",
            inbound.SourceDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(accepted),
            null,
            acceptedAt);

        using (var db = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(db.TryAddInboundTopicRunAndQueueAcceptance(
                inbound, acceptance));
            Assert.IsFalse(db.TryAddInboundTopicRunAndQueueAcceptance(
                inbound, acceptance));
            Assert.IsFalse(db.TryAddInboundTopicRun(inbound with
            {
                SourceDeviceId = "conflicting-device",
                Request = request with { TriggerText = "different" }
            }));
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var persisted = reopened.GetInboundTopicRun(request.RunId)!;
        Assert.AreEqual(inbound.SourceDeviceId, persisted.SourceDeviceId);
        Assert.AreEqual(
            TopicRunProtocol.RequestBody(request),
            TopicRunProtocol.RequestBody(persisted.Request));
        var queued = reopened.GetDeviceEnvelopeOutbox(acceptance.EnvelopeId)!;
        Assert.AreEqual(acceptance.Plaintext, queued.Plaintext);
        Assert.AreEqual(TopicOutboxStates.Pending, queued.State);
    }

    [TestMethod]
    public void TerminalControl_FailureBeforeAtomicCommit_RetryConvergesExactlyOnce()
    {
        var terminalAt = new DateTimeOffset(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-terminal-drop",
            "thread-terminal-drop",
            "line-terminal-drop",
            "owner",
            "finish durably",
            terminalAt.AddMinutes(-1),
            "desktop-device",
            TopicTurnMode.Single);
        var terminal = new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Failed,
            Status: "Failed",
            Error: "interrupted",
            FailureCode: "remote_execution_interrupted",
            Timestamp: terminalAt,
            TriggerLineId: request.TriggerLineId);
        var body = TopicRunProtocol.UpdateBody(terminal);
        var receipt = new MeshDb.ReceivedTopicControlItem(
            "stable-terminal-envelope",
            request.TargetDeviceId,
            terminal.RunId,
            terminal.ThreadId,
            TopicControlProtocol.ControlPurpose(terminal),
            body,
            terminalAt.AddSeconds(1));

        using (var db = MeshDb.Open(databasePath, key))
        {
            SaveProfile(db);
            db.EnsureOwnThread(request.ThreadId, "Atomic terminal", request.TriggerAt);
            db.SetOwnThreadExecution(
                request.ThreadId,
                request.TargetDeviceId,
                request.TriggerAt,
                request.RunId,
                "Desktop",
                DevicePlatforms.Windows);
            db.UpsertTopicOutbox(new MeshDb.TopicOutboxItem(
                request.RunId,
                request.ThreadId,
                request.TriggerLineId,
                request.TargetDeviceId,
                request,
                [],
                TopicOutboxStates.Running,
                request.TriggerAt,
                request.TriggerAt,
                RemoteStage: "executing",
                RemoteStageOrdinal: TopicRemoteStage.Executing));

            Assert.ThrowsExactly<InjectedCommitFailureException>(() =>
                db.ExecuteDurableWrite(() => db.ApplyRemoteTopicUpdate(
                    terminal,
                    request.TargetDeviceId,
                    receipt,
                    () => throw new InjectedCommitFailureException())));
            Assert.IsNotNull(db.GetTopicOutbox(request.RunId));
            Assert.IsNull(db.GetReceivedTopicControl(receipt.EnvelopeId));
            Assert.AreEqual(
                request.RunId,
                db.LoadProfile()!.OwnThreads.Single(
                    thread => thread.Id == request.ThreadId).ExecutionRunId);
        }
        SqliteConnection.ClearAllPools();

        using (var restarted = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                restarted.ExecuteDurableWrite(() => restarted.ApplyRemoteTopicUpdate(
                    terminal, request.TargetDeviceId, receipt)));
        }
        SqliteConnection.ClearAllPools();

        using var final = MeshDb.Open(databasePath, key);
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.Duplicate,
            final.ExecuteDurableWrite(() => final.ApplyRemoteTopicUpdate(
                terminal, request.TargetDeviceId, receipt)));
        Assert.IsNull(final.GetTopicOutbox(request.RunId));
        Assert.HasCount(1, final.ListReceivedTopicControls());
        Assert.IsNull(final.LoadProfile()!.OwnThreads.Single(
            thread => thread.Id == request.ThreadId).ExecutionRunId);
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.IdentityConflict,
            final.ExecuteDurableWrite(() => final.ApplyRemoteTopicUpdate(
                terminal,
                request.TargetDeviceId,
                receipt with
                {
                    SourceDeviceId = "conflicting-device"
                })));
    }

    [TestMethod]
    public void RestartedRemoteStage_DelayedAcceptanceAndQueueCannotRegressRunningOrTerminal()
    {
        var startedAt = new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-monotonic-restart",
            "thread-monotonic-restart",
            "line-monotonic-restart",
            "owner",
            "continue after restart",
            startedAt,
            "desktop-device",
            TopicTurnMode.Single);
        var running = new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Executing,
            Status: "Running",
            Timestamp: startedAt.AddSeconds(3),
            TriggerLineId: request.TriggerLineId);
        var accepted = TopicAcceptancePolicy.Create(request, startedAt.AddSeconds(1));
        var queued = accepted with
        {
            Status = TopicControlProtocol.ExecutionQueuedStatus,
            Timestamp = startedAt.AddSeconds(2)
        };
        var acceptanceControl = new MeshDb.ReceivedTopicControlItem(
            "stable-acceptance-envelope",
            request.TargetDeviceId,
            request.RunId,
            request.ThreadId,
            TopicControlProtocol.ControlPurpose(accepted),
            TopicRunProtocol.UpdateBody(accepted),
            accepted.Timestamp);
        var terminal = running with
        {
            Phase = TopicRunPhase.Completed,
            Status = "Completed",
            Timestamp = startedAt.AddSeconds(4),
            Result = new TopicRunResultPayload(
                "assistant-result-line",
                "durable terminal answer",
                startedAt.AddSeconds(3),
                "model-id",
                "reasoning")
        };
        var terminalControl = new MeshDb.ReceivedTopicControlItem(
            "stable-terminal-envelope",
            request.TargetDeviceId,
            request.RunId,
            request.ThreadId,
            TopicControlProtocol.ControlPurpose(terminal),
            TopicRunProtocol.UpdateBody(terminal),
            terminal.Timestamp);

        using (var db = MeshDb.Open(databasePath, key))
        {
            SaveProfile(db);
            db.EnsureOwnThread(request.ThreadId, "Monotonic run", startedAt);
            db.SetOwnThreadExecution(
                request.ThreadId,
                request.TargetDeviceId,
                startedAt,
                request.RunId,
                "Desktop",
                DevicePlatforms.Windows);
            db.UpsertTopicOutbox(new MeshDb.TopicOutboxItem(
                request.RunId,
                request.ThreadId,
                request.TriggerLineId,
                request.TargetDeviceId,
                request,
                [],
                TopicOutboxStates.DeviceQueued,
                startedAt,
                startedAt,
                RemoteStage: "queued",
                RemoteStageOrdinal: TopicRemoteStage.ExecutionQueued));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                db.ExecuteDurableWrite(() => db.ApplyRemoteTopicUpdate(
                    running, request.TargetDeviceId)));
        }
        SqliteConnection.ClearAllPools();

        using (var restarted = MeshDb.Open(databasePath, key))
        {
            var persistedRunning = restarted.GetTopicOutbox(request.RunId)!;
            Assert.AreEqual(TopicOutboxStates.Running, persistedRunning.State);
            Assert.AreEqual(TopicRemoteStage.Executing, persistedRunning.RemoteStageOrdinal);

            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Ignored,
                restarted.ExecuteDurableWrite(() => restarted.ApplyRemoteTopicUpdate(
                    accepted, request.TargetDeviceId, acceptanceControl)));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Duplicate,
                restarted.ExecuteDurableWrite(() => restarted.ApplyRemoteTopicUpdate(
                    accepted, request.TargetDeviceId, acceptanceControl)));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Ignored,
                restarted.ExecuteDurableWrite(() => restarted.ApplyRemoteTopicUpdate(
                    queued, request.TargetDeviceId)));
            var stillRunning = restarted.GetTopicOutbox(request.RunId)!;
            Assert.AreEqual(TopicOutboxStates.Running, stillRunning.State);
            Assert.AreEqual(TopicRemoteStage.Executing, stillRunning.RemoteStageOrdinal);
            Assert.HasCount(1, restarted.ListReceivedTopicControls());

            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                restarted.ExecuteDurableWrite(() => restarted.ApplyRemoteTopicUpdate(
                    terminal, request.TargetDeviceId, terminalControl)));
        }
        SqliteConnection.ClearAllPools();

        using var final = MeshDb.Open(databasePath, key);
        Assert.IsNull(final.GetTopicOutbox(request.RunId));
        Assert.IsNull(final.LoadProfile()!.OwnThreads.Single(
            thread => thread.Id == request.ThreadId).ExecutionRunId);
        var resultLine = final.LoadProfile()!.OwnThreads.Single(
            thread => thread.Id == request.ThreadId).Lines.Single();
        Assert.AreEqual(terminal.Result!.LineId, resultLine.Id);
        Assert.AreEqual(terminal.Result.Text, resultLine.Text);
        Assert.AreEqual(request.TriggerLineId, resultLine.ReplyToLineId);
        Assert.AreEqual(terminal.Result.ModelId, resultLine.ModelId);
        Assert.AreEqual(terminal.Result.Reasoning, resultLine.Reasoning);
        var lateProgress = running with
        {
            Status = "Late thinking",
            Timestamp = terminal.Timestamp.AddSeconds(1),
            DeltaSeq = 1,
            DeltaKind = TopicRunDeltaKind.Reasoning,
            Delta = "must not reappear"
        };
        var lateProgressControl = new MeshDb.ReceivedTopicControlItem(
            "stable-late-progress-envelope",
            request.TargetDeviceId,
            request.RunId,
            request.ThreadId,
            TopicControlProtocol.ControlPurpose(lateProgress),
            TopicRunProtocol.UpdateBody(lateProgress),
            lateProgress.Timestamp);
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.NotCorrelated,
            final.ExecuteDurableWrite(() => final.ApplyRemoteTopicUpdate(
                lateProgress, request.TargetDeviceId, lateProgressControl)));
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.NotCorrelated,
            final.ExecuteDurableWrite(() => final.ApplyRemoteTopicUpdate(
                lateProgress, request.TargetDeviceId, lateProgressControl)));
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.Duplicate,
            final.ExecuteDurableWrite(() => final.ApplyRemoteTopicUpdate(
                terminal, request.TargetDeviceId, terminalControl)));
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.NotCorrelated,
            final.ExecuteDurableWrite(() => final.ApplyRemoteTopicUpdate(
                running, request.TargetDeviceId)));
        Assert.HasCount(2, final.ListReceivedTopicControls());
        Assert.IsNull(final.GetTopicOutbox(request.RunId));
        var replayedProfile = final.LoadProfile()!;
        Assert.IsNull(replayedProfile.OwnThreads.Single(
            thread => thread.Id == request.ThreadId).ExecutionRunId);
        Assert.HasCount(1, replayedProfile.OwnThreads.Single(
            thread => thread.Id == request.ThreadId).Lines);
    }

    [TestMethod]
    public async Task TargetedOnlineRecovery_DrainsOnlyMatchingDeviceOutbox()
    {
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var transport = new ControlledTopicTransport((
            targetDeviceId, _, _, _, _, _) =>
        {
            attempts[targetDeviceId] = attempts.GetValueOrDefault(targetDeviceId) + 1;
            return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
        });
        using var db = MeshDb.Open(databasePath, key, time);
        SaveProfile(db);
        var handler = new TopicRequestOutboxHandler(db, time);
        var delivery = new TopicRequestOutboxDelivery(handler, transport, time);

        foreach (var (run, thread, line, device) in new[]
                 {
                     ("run-target", "thread-target", "line-target", "target-device"),
                     ("run-other", "thread-other", "line-other", "other-device")
                 })
        {
            db.EnsureOwnThread(thread, thread, now);
            db.SetOwnThreadExecution(
                thread, device, now, run, "Executor", DevicePlatforms.Windows);
            handler.Queue(device, new(
                run,
                thread,
                line,
                "owner",
                run,
                now,
                device,
                TopicTurnMode.Single), []);
        }

        var scope = new OnlineDeliveryTargetScope(new[] { "target-device" });
        foreach (var item in db.ListTopicOutbox().Where(item => scope.Includes(item.TargetDeviceId)))
            await delivery.TrySendAsync(item, CancellationToken.None);

        Assert.AreEqual(1, attempts.GetValueOrDefault("target-device"));
        Assert.AreEqual(0, attempts.GetValueOrDefault("other-device"));
        Assert.AreEqual(
            TopicOutboxStates.RelayQueued,
            db.GetTopicOutbox("run-target")!.State);
        Assert.AreEqual(
            TopicOutboxStates.Pending,
            db.GetTopicOutbox("run-other")!.State,
            "an unrelated queued device must remain untouched by a targeted wake");
        Console.WriteLine(
            $"TARGETED_OUTBOX targetAttempts={attempts.GetValueOrDefault("target-device")} " +
            $"otherAttempts={attempts.GetValueOrDefault("other-device")} " +
            $"targetState={db.GetTopicOutbox("run-target")!.State} " +
            $"otherState={db.GetTopicOutbox("run-other")!.State}");
    }

    [TestMethod]
    public async Task ProductionTransitions_LateRelayCompletionCannotRegressAcceptedOrRunning()
    {
        var startedAt = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var request = new TopicRunRequestPayload(
            "run-live-race",
            "thread-live-race",
            "line-live-race",
            "owner",
            "race the relay result",
            startedAt,
            "executor-device",
            TopicTurnMode.Single);
        var accepted = TopicAcceptancePolicy.Create(request, startedAt.AddSeconds(1));
        var running = accepted with
        {
            Phase = TopicRunPhase.Executing,
            Status = "Running",
            Timestamp = startedAt.AddSeconds(2)
        };
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<MeshSendResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ControlledTopicTransport(async (
            targetDeviceId, kind, plaintext, envelopeId, pushHint, cancellationToken) =>
        {
            sendEntered.TrySetResult();
            return await releaseSend.Task.WaitAsync(cancellationToken);
        });

        using (var db = MeshDb.Open(databasePath, key, time))
        {
            SaveProfile(db);
            db.EnsureOwnThread(request.ThreadId, "Live race", startedAt);
            db.SetOwnThreadExecution(
                request.ThreadId,
                request.TargetDeviceId,
                startedAt,
                request.RunId,
                "Executor",
                DevicePlatforms.Windows);
            var requestOutbox = new TopicRequestOutboxHandler(db, time);
            var delivery = new TopicRequestOutboxDelivery(
                requestOutbox, transport, time);
            var durability = new TopicDurabilityHandler(db, time);
            var queued = requestOutbox.Queue(
                request.TargetDeviceId, request, []);
            var lateRelayCompletion = delivery.TrySendAsync(
                queued, CancellationToken.None);
            await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                durability.HandleUpdate(
                    accepted,
                    request.TargetDeviceId,
                    TopicControlProtocol.EnvelopeId(
                        TopicControlProtocol.ControlPurpose(accepted), request.RunId)));
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Applied,
                durability.HandleUpdate(
                    running, request.TargetDeviceId, "running-envelope"));

            releaseSend.SetResult(MeshSendResult.Ok());
            Assert.AreEqual(
                TopicSendOutcomePersistenceResult.Ignored,
                (await lateRelayCompletion.WaitAsync(TimeSpan.FromSeconds(2)))
                .PersistenceResult);
            var persisted = db.GetTopicOutbox(request.RunId)!;
            Assert.AreEqual(TopicOutboxStates.Running, persisted.State);
            Assert.AreEqual(TopicRemoteStage.Executing, persisted.RemoteStageOrdinal);
            Assert.IsFalse(TopicOutboxStates.NeedsRemoteAcceptance(persisted.State));
            Assert.IsFalse(TopicTransportPolicy.ShouldAttemptRequestDelivery(
                persisted.State, persisted.UpdatedAt, persisted.UpdatedAt.AddDays(1)));
        }
        SqliteConnection.ClearAllPools();

        using var restarted = MeshDb.Open(databasePath, key, time);
        var restartedDurability = new TopicDurabilityHandler(restarted, time);
        var restartedOutbox = new TopicRequestOutboxHandler(restarted, time);
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.Duplicate,
            restartedDurability.HandleUpdate(
                accepted,
                request.TargetDeviceId,
                TopicControlProtocol.EnvelopeId(
                    TopicControlProtocol.ControlPurpose(accepted), request.RunId)));
        Assert.AreEqual(
            TopicSendOutcomePersistenceResult.Ignored,
            restartedOutbox.ApplySendOutcome(
                request.RunId, TopicOutboxStates.Failed, "late_reject"));
        var afterDuplicate = restarted.GetTopicOutbox(request.RunId)!;
        Assert.AreEqual(TopicOutboxStates.Running, afterDuplicate.State);
        Assert.AreEqual(TopicRemoteStage.Executing, afterDuplicate.RemoteStageOrdinal);
        Assert.HasCount(1, restarted.ListReceivedTopicControls());
    }

    [TestMethod]
    public async Task ProductionHandlers_BoundControlRetryReceiptExpiryAndTombstoneAcrossRestarts()
    {
        var executorPath = Path.Combine(directory, "executor.meshdb");
        var startedAt = new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var request = new TopicRunRequestPayload(
            "run-delayed-acceptance",
            "thread-delayed-acceptance",
            "line-delayed-acceptance",
            "owner",
            "finish before acceptance arrives",
            startedAt,
            "executor-device",
            TopicTurnMode.Single);
        var terminal = TopicAcceptancePolicy.Create(request, startedAt.AddSeconds(1)) with
        {
            Phase = TopicRunPhase.Completed,
            Status = "Completed",
            Timestamp = startedAt.AddYears(-10)
        };

        using var requester = MeshDb.Open(databasePath, key, time);
        SaveProfile(requester);
        requester.EnsureOwnThread(request.ThreadId, "Delayed acceptance", startedAt);
        requester.SetOwnThreadExecution(
            request.ThreadId,
            request.TargetDeviceId,
            startedAt,
            request.RunId,
            "Executor",
            DevicePlatforms.Windows);
        var requesterOutbox = new TopicRequestOutboxHandler(requester, time);
        _ = requesterOutbox.Queue(request.TargetDeviceId, request, []);
        var requesterDurability = new TopicDurabilityHandler(requester, time);

        using var executor = MeshDb.Open(executorPath, key, time);
        var executorDurability = new TopicDurabilityHandler(executor, time);
        var inbound = executorDurability.AcceptRequest(request, "requester-device");
        var accepted = TopicAcceptancePolicy.Create(request, inbound.AcceptedAt);
        var acceptanceEnvelopeId = TopicControlProtocol.EnvelopeId(
            TopicControlProtocol.ControlPurpose(accepted), request.RunId);
        var terminalWinner = executorDurability.CompleteRun(
            request.RunId,
            InboundTopicRunStates.Completed,
            terminal,
            "requester-device");
        Assert.AreEqual(terminal, terminalWinner);
        var terminalEnvelopeId = TopicControlProtocol.EnvelopeId(
            TopicControlProtocol.ControlPurpose(terminal), request.RunId);

        var activeRequesterHandler = requesterDurability;
        var transport = new ControlledTopicTransport((
            targetDeviceId, kind, plaintext, envelopeId, pushHint, cancellationToken) =>
        {
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(plaintext, out var update));
            var persistence = activeRequesterHandler.HandleUpdate(
                update, request.TargetDeviceId, envelopeId);
            Assert.IsTrue(persistence is RemoteTopicUpdatePersistenceResult.Applied
                or RemoteTopicUpdatePersistenceResult.Ignored
                or RemoteTopicUpdatePersistenceResult.Duplicate);
            return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
        });
        var executorDelivery = new TopicControlOutboxDelivery(
            executor, transport, time);

        var terminalOutbox = executor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!;
        Assert.IsTrue((await executorDelivery.TrySendAsync(
            terminalOutbox, CancellationToken.None))!.Accepted);
        var correlation = requester.GetTopicRunCorrelation(request.RunId);
        Assert.IsNotNull(correlation);
        Assert.AreEqual(startedAt, correlation.TerminalAt);
        Assert.AreEqual(terminal.Timestamp, correlation.TerminalEventAt);
        Assert.IsNull(requester.GetTopicOutbox(request.RunId));

        time.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval
                     + TimeSpan.FromSeconds(1));
        var requesterMaintenance = new TopicCorrelationMaintenance(requester, time);
        Assert.AreEqual(0, requesterMaintenance.PruneTerminalCorrelations());
        requester.Dispose();
        SqliteConnection.ClearAllPools();

        using (var requesterRestarted = MeshDb.Open(databasePath, key, time))
        {
            activeRequesterHandler = new TopicDurabilityHandler(
                requesterRestarted, time);
            var acceptanceOutbox =
                executor.GetDeviceEnvelopeOutbox(acceptanceEnvelopeId)!;
            Assert.IsTrue((await executorDelivery.TrySendAsync(
                acceptanceOutbox, CancellationToken.None))!.Accepted);
            Assert.AreEqual(
                RemoteTopicUpdatePersistenceResult.Duplicate,
                activeRequesterHandler.HandleUpdate(
                    accepted, request.TargetDeviceId, acceptanceEnvelopeId));
            var receipt = TopicControlProtocol.CreateReceipt(
                accepted, time.GetUtcNow());
            Assert.AreEqual(
                TopicControlReceiptPersistenceResult.Applied,
                executorDurability.HandleReceipt(receipt, "requester-device"));
            Assert.IsNull(executor.GetDeviceEnvelopeOutbox(acceptanceEnvelopeId));
            Assert.IsNull(requesterRestarted.GetTopicOutbox(request.RunId));
            Assert.IsNotNull(requesterRestarted.GetTopicRunCorrelation(request.RunId));
        }

        time.Advance(TopicTransportPolicy.ControlDeliveryLifetime);
        terminalOutbox = executor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!;
        var expired = await executorDelivery.TrySendAsync(
            terminalOutbox, CancellationToken.None);
        Assert.AreEqual("control_receipt_expired", expired!.Code);
        var deadLetter = executor.GetDeviceEnvelopeOutbox(terminalEnvelopeId);
        Assert.IsNotNull(deadLetter);
        Assert.AreEqual(TopicOutboxStates.DeadLetter, deadLetter.State);
        Assert.AreEqual("control_receipt_expired", deadLetter.LastError);

        executor.Dispose();
        SqliteConnection.ClearAllPools();
        using (var executorRestarted = MeshDb.Open(executorPath, key, time))
        {
            var restartedExecutorHandler = new TopicDurabilityHandler(
                executorRestarted, time);
            var lateTerminalReceipt = TopicControlProtocol.CreateReceipt(
                terminal, time.GetUtcNow());
            Assert.AreEqual(
                TopicControlReceiptPersistenceResult.Applied,
                restartedExecutorHandler.HandleReceipt(
                    lateTerminalReceipt, "requester-device"));
            Assert.IsNull(
                executorRestarted.GetDeviceEnvelopeOutbox(terminalEnvelopeId));
        }
        using var requesterFinal = MeshDb.Open(databasePath, key, time);
        var finalMaintenance = new TopicCorrelationMaintenance(requesterFinal, time);
        Assert.IsTrue(
            TopicTransportPolicy.TerminalCorrelationRetention
            > TopicTransportPolicy.ControlDeliveryLifetime);
        Assert.AreEqual(0, finalMaintenance.PruneTerminalCorrelations());
        time.Advance(
            TopicTransportPolicy.TerminalCorrelationRetention
            - TopicTransportPolicy.ControlDeliveryLifetime
            + TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, finalMaintenance.PruneTerminalCorrelations());
        Assert.IsNull(requesterFinal.GetTopicRunCorrelation(request.RunId));
    }

    [TestMethod]
    public async Task DeadLetteredTerminal_RestartRecoveryResendsStableEnvelopeWithBackoffAndFinalCleanup()
    {
        var startedAt = new DateTimeOffset(2026, 8, 22, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var executorPath = Path.Combine(directory, "executor-recovery.meshdb");
        var request = new TopicRunRequestPayload(
            "run-terminal-recovery",
            "thread-terminal-recovery",
            "line-terminal-recovery",
            "owner",
            "recover terminal",
            startedAt,
            "executor-device",
            TopicTurnMode.Single);
        var terminal = new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Completed,
            Status: "Completed",
            Timestamp: startedAt.AddMinutes(1),
            TriggerLineId: request.TriggerLineId);

        using var requester = MeshDb.Open(databasePath, key, time);
        SaveProfile(requester);
        requester.EnsureOwnThread(request.ThreadId, "Recovery", startedAt);
        requester.SetOwnThreadExecution(
            request.ThreadId,
            request.TargetDeviceId,
            startedAt,
            request.RunId,
            "Executor",
            DevicePlatforms.Windows);
        _ = new TopicRequestOutboxHandler(requester, time)
            .Queue(request.TargetDeviceId, request, []);
        var requesterHandler = new TopicDurabilityHandler(requester, time);

        string terminalEnvelopeId;
        using (var executor = MeshDb.Open(executorPath, key, time))
        {
            SaveProfile(executor);
            var executorHandler = new TopicDurabilityHandler(executor, time);
            _ = executorHandler.AcceptRequest(request, "requester-device");
            _ = executorHandler.CompleteRun(
                request.RunId,
                InboundTopicRunStates.Completed,
                terminal,
                "requester-device");
            terminalEnvelopeId = TopicControlProtocol.EnvelopeId(
                TopicControlProtocol.ControlPurpose(terminal), request.RunId);
            var firstResults = new List<RemoteTopicUpdatePersistenceResult>();
            var transport = new ControlledTopicTransport((
                targetDeviceId, kind, plaintext, envelopeId, pushHint, cancellationToken) =>
            {
                Assert.AreEqual(terminalEnvelopeId, envelopeId);
                Assert.IsTrue(TopicRunProtocol.TryParseUpdate(plaintext, out var update));
                firstResults.Add(requesterHandler.HandleUpdate(
                    update, "executor-device", envelopeId));
                return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
            });
            var delivery = new TopicControlOutboxDelivery(executor, transport, time);
            Assert.IsTrue((await delivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!,
                CancellationToken.None))!.Accepted);
            Assert.AreEqual(RemoteTopicUpdatePersistenceResult.Applied, firstResults.Single());

            time.Advance(TopicTransportPolicy.ControlDeliveryLifetime);
            Assert.AreEqual(
                "control_receipt_expired",
                (await delivery.TrySendAsync(
                    executor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!,
                    CancellationToken.None))!.Code);
            Assert.AreEqual(
                TopicOutboxStates.DeadLetter,
                executor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!.State);
        }

        SqliteConnection.ClearAllPools();
        using var restartedExecutor = MeshDb.Open(executorPath, key, time);
        var persistedTerminal = restartedExecutor.GetInboundTopicRun(request.RunId);
        Assert.IsNotNull(persistedTerminal);
        Assert.AreEqual(
            TopicRunProtocol.UpdateBody(terminal),
            persistedTerminal.TerminalUpdateJson);
        var deadLetter = restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!;
        var recovery = new TopicControlOutboxRecovery(restartedExecutor, time);
        var recovered = recovery.Recover(deadLetter);
        Assert.AreEqual(TopicControlRecoveryKind.Recovered, recovered.Kind);
        var pending = restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!;
        Assert.AreEqual(terminalEnvelopeId, pending.EnvelopeId);
        Assert.AreEqual(TopicOutboxStates.Pending, pending.State);
        Assert.AreEqual(1, pending.RecoveryCount);
        Assert.AreEqual(
            TopicControlRecoveryKind.NotDeadLettered,
            recovery.Recover(pending).Kind);

        var unavailableCalls = 0;
        var unavailable = new TopicControlOutboxDelivery(
            restartedExecutor,
            new ControlledTopicTransport((
                targetDeviceId, kind, plaintext, envelopeId, pushHint, cancellationToken) =>
            {
                Interlocked.Increment(ref unavailableCalls);
                return Task.FromResult<MeshSendResult?>(null);
            }),
            time);
        Assert.IsNull(await unavailable.TrySendAsync(pending, CancellationToken.None));
        var afterUnavailable = restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!;
        Assert.IsNull(await unavailable.TrySendAsync(
            afterUnavailable, CancellationToken.None));
        Assert.AreEqual(1, Volatile.Read(ref unavailableCalls));

        time.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval);
        var replayResults = new List<RemoteTopicUpdatePersistenceResult>();
        var replay = new TopicControlOutboxDelivery(
            restartedExecutor,
            new ControlledTopicTransport((
                targetDeviceId, kind, plaintext, envelopeId, pushHint, cancellationToken) =>
            {
                Assert.AreEqual(terminalEnvelopeId, envelopeId);
                Assert.IsTrue(TopicRunProtocol.TryParseUpdate(plaintext, out var update));
                replayResults.Add(requesterHandler.HandleUpdate(
                    update, "executor-device", envelopeId));
                return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
            }),
            time);
        Assert.IsTrue((await replay.TrySendAsync(
            restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!,
            CancellationToken.None))!.Accepted);
        Assert.AreEqual(RemoteTopicUpdatePersistenceResult.Duplicate, replayResults.Single());

        time.Advance(TopicTransportPolicy.RecoveredControlDeliveryLifetime);
        Assert.AreEqual(
            "control_receipt_expired",
            (await replay.TrySendAsync(
                restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!,
                CancellationToken.None))!.Code);
        var exhausted = restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId)!;
        Assert.AreEqual(TopicOutboxStates.DeadLetter, exhausted.State);
        Assert.AreEqual(
            TopicControlRecoveryKind.RecoveryLimitReached,
            recovery.Recover(exhausted).Kind);

        var restartedHandler = new TopicDurabilityHandler(restartedExecutor, time);
        var receipt = TopicControlProtocol.CreateReceipt(terminal, time.GetUtcNow());
        Assert.AreEqual(
            TopicControlReceiptPersistenceResult.Applied,
            restartedHandler.HandleReceipt(receipt, "requester-device"));
        Assert.AreEqual(
            TopicControlReceiptPersistenceResult.Duplicate,
            restartedHandler.HandleReceipt(receipt, "requester-device"));
        Assert.IsNull(restartedExecutor.GetDeviceEnvelopeOutbox(terminalEnvelopeId));
        Assert.IsNull(requester.GetTopicOutbox(request.RunId));
        Assert.AreEqual(1, requester.ListReceivedTopicControls().Count);
        Assert.IsNotNull(requester.GetTopicRunCorrelation(request.RunId));

        time.Advance(
            startedAt + TopicTransportPolicy.DedupRetention
            - TimeSpan.FromSeconds(1)
            - time.GetUtcNow());
        Assert.AreEqual(
            0,
            requester.PruneReceivedTopicControls(
                time.GetUtcNow() - TopicTransportPolicy.DedupRetention));
        Assert.AreEqual(0, requester.PruneTopicRunCorrelations(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            1,
            requester.PruneReceivedTopicControls(
                time.GetUtcNow() - TopicTransportPolicy.DedupRetention));
        Assert.AreEqual(1, requester.PruneTopicRunCorrelations(time.GetUtcNow()));
    }

    [TestMethod]
    public async Task DeadLetteredAcceptance_RestartRecoveryResendsSameControlExactlyOnce()
    {
        var startedAt = new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var executorPath = Path.Combine(directory, "executor-acceptance-recovery.meshdb");
        var request = new TopicRunRequestPayload(
            "run-acceptance-recovery",
            "thread-acceptance-recovery",
            "line-acceptance-recovery",
            "owner",
            "recover acceptance",
            startedAt,
            "executor-device",
            TopicTurnMode.Single);

        using var requester = MeshDb.Open(databasePath, key, time);
        SaveProfile(requester);
        requester.EnsureOwnThread(request.ThreadId, "Acceptance Recovery", startedAt);
        requester.SetOwnThreadExecution(
            request.ThreadId,
            request.TargetDeviceId,
            startedAt,
            request.RunId,
            "Executor",
            DevicePlatforms.Windows);
        _ = new TopicRequestOutboxHandler(requester, time)
            .Queue(request.TargetDeviceId, request, []);
        var requesterHandler = new TopicDurabilityHandler(requester, time);

        string envelopeId;
        TopicRunUpdatePayload acceptance;
        using (var executor = MeshDb.Open(executorPath, key, time))
        {
            SaveProfile(executor);
            var inbound = new TopicDurabilityHandler(executor, time)
                .AcceptRequest(request, "requester-device");
            acceptance = TopicAcceptancePolicy.Create(request, inbound.AcceptedAt);
            envelopeId = TopicControlProtocol.EnvelopeId(
                TopicControlProtocol.ControlPurpose(acceptance), request.RunId);
            var delivery = new TopicControlOutboxDelivery(
                executor,
                new ControlledTopicTransport((
                    targetDeviceId, kind, plaintext, stableEnvelopeId, pushHint,
                    cancellationToken) =>
                {
                    Assert.AreEqual(envelopeId, stableEnvelopeId);
                    Assert.IsTrue(TopicRunProtocol.TryParseUpdate(plaintext, out var update));
                    Assert.AreEqual(
                        RemoteTopicUpdatePersistenceResult.Applied,
                        requesterHandler.HandleUpdate(
                            update, "executor-device", stableEnvelopeId));
                    return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
                }),
                time);
            Assert.IsTrue((await delivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(envelopeId)!,
                CancellationToken.None))!.Accepted);
            time.Advance(TopicTransportPolicy.ControlDeliveryLifetime);
            Assert.AreEqual(
                "control_receipt_expired",
                (await delivery.TrySendAsync(
                    executor.GetDeviceEnvelopeOutbox(envelopeId)!,
                    CancellationToken.None))!.Code);
        }

        SqliteConnection.ClearAllPools();
        using var restartedExecutor = MeshDb.Open(executorPath, key, time);
        Assert.HasCount(1, restartedExecutor.ListInboundTopicRuns());
        var deadLetter = restartedExecutor.GetDeviceEnvelopeOutbox(envelopeId)!;
        Assert.AreEqual(TopicOutboxStates.DeadLetter, deadLetter.State);
        var recovery = new TopicControlOutboxRecovery(restartedExecutor, time);
        Assert.AreEqual(
            TopicControlRecoveryKind.Recovered,
            recovery.Recover(deadLetter).Kind);
        Assert.AreEqual(
            TopicControlRecoveryKind.NotDeadLettered,
            recovery.Recover(
                restartedExecutor.GetDeviceEnvelopeOutbox(envelopeId)!).Kind);

        var replayCount = 0;
        var replay = new TopicControlOutboxDelivery(
            restartedExecutor,
            new ControlledTopicTransport((
                targetDeviceId, kind, plaintext, stableEnvelopeId, pushHint,
                cancellationToken) =>
            {
                replayCount++;
                Assert.AreEqual(envelopeId, stableEnvelopeId);
                Assert.IsTrue(TopicRunProtocol.TryParseUpdate(plaintext, out var update));
                Assert.AreEqual(
                    RemoteTopicUpdatePersistenceResult.Duplicate,
                    requesterHandler.HandleUpdate(
                        update, "executor-device", stableEnvelopeId));
                return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
            }),
            time);
        Assert.IsTrue((await replay.TrySendAsync(
            restartedExecutor.GetDeviceEnvelopeOutbox(envelopeId)!,
            CancellationToken.None))!.Accepted);
        Assert.AreEqual(1, replayCount);
        Assert.HasCount(1, restartedExecutor.ListInboundTopicRuns());
        Assert.HasCount(1, requester.ListReceivedTopicControls());

        var receipt = TopicControlProtocol.CreateReceipt(acceptance, time.GetUtcNow());
        var restartedHandler = new TopicDurabilityHandler(restartedExecutor, time);
        Assert.AreEqual(
            TopicControlReceiptPersistenceResult.Applied,
            restartedHandler.HandleReceipt(receipt, "requester-device"));
        Assert.AreEqual(
            TopicControlReceiptPersistenceResult.Duplicate,
            restartedHandler.HandleReceipt(receipt, "requester-device"));
        Assert.IsNull(restartedExecutor.GetDeviceEnvelopeOutbox(envelopeId));
    }

    [TestMethod]
    public async Task DeadLetterRecovery_AfterBoundedWindowRemainsObservableAndDoesNotResend()
    {
        var startedAt = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var request = new TopicRunRequestPayload(
            "run-recovery-window",
            "thread-recovery-window",
            "line-recovery-window",
            "owner",
            "bounded recovery",
            startedAt,
            "executor-device",
            TopicTurnMode.Single);
        using var executor = MeshDb.Open(databasePath, key, time);
        SaveProfile(executor);
        var inbound = new TopicDurabilityHandler(executor, time)
            .AcceptRequest(request, "requester-device");
        var acceptance = TopicAcceptancePolicy.Create(request, inbound.AcceptedAt);
        var envelopeId = TopicControlProtocol.EnvelopeId(
            TopicControlProtocol.ControlPurpose(acceptance), request.RunId);
        var sendCalls = 0;
        var delivery = new TopicControlOutboxDelivery(
            executor,
            new ControlledTopicTransport((
                targetDeviceId, kind, plaintext, stableEnvelopeId, pushHint,
                cancellationToken) =>
            {
                sendCalls++;
                return Task.FromResult<MeshSendResult?>(MeshSendResult.Ok());
            }),
            time);

        time.Advance(TopicTransportPolicy.DeadLetterRecoveryWindow);
        Assert.AreEqual(
            "control_receipt_expired",
            (await delivery.TrySendAsync(
                executor.GetDeviceEnvelopeOutbox(envelopeId)!,
                CancellationToken.None))!.Code);
        var deadLetter = executor.GetDeviceEnvelopeOutbox(envelopeId)!;
        Assert.AreEqual(TopicOutboxStates.DeadLetter, deadLetter.State);
        Assert.AreEqual(0, sendCalls);
        Assert.AreEqual(
            TopicControlRecoveryKind.RecoveryWindowExpired,
            new TopicControlOutboxRecovery(executor, time).Recover(deadLetter).Kind);
        Assert.AreEqual(
            TopicOutboxStates.DeadLetter,
            executor.GetDeviceEnvelopeOutbox(envelopeId)!.State);
    }

    private sealed class InjectedCommitFailureException : Exception
    {
    }

    [TestMethod]
    public void ReceiverRestart_InterruptsRunningOnceThenExposesNextAcceptedRun()
    {
        var acceptedAt = new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero);
        var firstRequest = new TopicRunRequestPayload(
            "run-restart-q1",
            "thread-restart",
            "line-restart-q1",
            "owner",
            "q1",
            acceptedAt,
            "desktop-device",
            TopicTurnMode.Single);
        var secondRequest = firstRequest with
        {
            RunId = "run-restart-q2",
            TriggerLineId = "line-restart-q2",
            TriggerText = "q2",
            TriggerAt = acceptedAt.AddSeconds(1)
        };
        var running = new MeshDb.InboundTopicRunItem(
            firstRequest.RunId,
            "phone-device",
            firstRequest,
            InboundTopicRunStates.Running,
            acceptedAt,
            acceptedAt);
        var accepted = new MeshDb.InboundTopicRunItem(
            secondRequest.RunId,
            "phone-device",
            secondRequest,
            InboundTopicRunStates.Accepted,
            acceptedAt.AddSeconds(1),
            acceptedAt.AddSeconds(1));
        var interrupted = new TopicRunUpdatePayload(
            running.RunId,
            running.Request.ThreadId,
            TopicRunPhase.Failed,
            Error: "The remote device restarted before this run completed.",
            FailureCode: "remote_execution_interrupted",
            Timestamp: acceptedAt.AddMinutes(1),
            TriggerLineId: running.Request.TriggerLineId);
        var interruptedOutbox = new MeshDb.DeviceEnvelopeOutboxItem(
            "stable-interrupted-terminal",
            running.SourceDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(interrupted),
            null,
            interrupted.Timestamp);

        using (var db = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(db.TryAddInboundTopicRun(running));
            Assert.IsTrue(db.TryAddInboundTopicRun(accepted));
        }
        SqliteConnection.ClearAllPools();

        using (var recovered = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(recovered.SetInboundTopicRunTerminalAndQueue(
                running.RunId,
                InboundTopicRunStates.Interrupted,
                interrupted,
                interruptedOutbox));
            Assert.IsTrue(recovered.SetInboundTopicRunTerminalAndQueue(
                running.RunId,
                InboundTopicRunStates.Interrupted,
                interrupted,
                interruptedOutbox with { EnvelopeId = "late-duplicate" }));

            var resumable = recovered.ListInboundTopicRuns(
                InboundTopicRunStates.Accepted,
                InboundTopicRunStates.Running);
            Assert.HasCount(1, resumable);
            Assert.AreEqual(accepted.RunId, resumable[0].RunId);
            var terminals = recovered.ListDeviceEnvelopeOutbox();
            Assert.HasCount(1, terminals);
            Assert.AreEqual(interruptedOutbox.EnvelopeId, terminals[0].EnvelopeId);
        }
    }

    [TestMethod]
    public void SenderRestart_PreservesRelayAcceptedAndExecutionQueuedStages()
    {
        var created = new DateTimeOffset(2026, 8, 22, 11, 30, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-sender-restart",
            "thread-sender-restart",
            "line-sender-restart",
            "owner",
            "resume delivery",
            created,
            "desktop-device",
            TopicTurnMode.Single);
        var outbox = new MeshDb.TopicOutboxItem(
            request.RunId,
            request.ThreadId,
            request.TriggerLineId,
            request.TargetDeviceId,
            request,
            [],
            TopicOutboxStates.RelayQueued,
            created,
            created);

        using (var db = MeshDb.Open(databasePath, key))
            db.UpsertTopicOutbox(outbox);
        SqliteConnection.ClearAllPools();

        using (var relayRestart = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual(
                TopicOutboxStates.RelayQueued,
                relayRestart.GetTopicOutbox(request.RunId)!.State);
            relayRestart.SetTopicOutboxState(
                request.RunId, TopicOutboxStates.DeviceAccepted);
        }
        SqliteConnection.ClearAllPools();

        using (var acceptedRestart = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual(
                TopicOutboxStates.DeviceAccepted,
                acceptedRestart.GetTopicOutbox(request.RunId)!.State);
            acceptedRestart.SetTopicOutboxState(
                request.RunId, TopicOutboxStates.DeviceQueued);
        }
        SqliteConnection.ClearAllPools();

        using var queuedRestart = MeshDb.Open(databasePath, key);
        Assert.AreEqual(
            TopicOutboxStates.DeviceQueued,
            queuedRestart.GetTopicOutbox(request.RunId)!.State);
    }

    [TestMethod]
    public void TopicQueuePresentation_DistinguishesProtocolHandoffStages()
    {
        Assert.AreEqual(
            "sending from this device",
            TopicQueuePresentation.Label(TopicQueueStage.Sending, false, "Laptop"));
        Assert.AreEqual(
            "sent · waiting for Laptop",
            TopicQueuePresentation.Label(TopicQueueStage.Relay, false, "Laptop"));
        Assert.AreEqual(
            "accepted by Laptop",
            TopicQueuePresentation.Label(TopicQueueStage.Device, false, "Laptop"));
    }

    [TestMethod]
    public void DeviceEnvelopeOutbox_ReplacementSupersedesOnlyMatchingTargetAndKind()
    {
        var created = new DateTimeOffset(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);
        var oldJob = new MeshDb.DeviceEnvelopeOutboxItem(
            "old-job", "phone", "internal.snapshot", "old", null, created);
        var newJob = oldJob with
        {
            EnvelopeId = "new-job",
            Plaintext = "new",
            CreatedAt = created.AddMinutes(1)
        };
        var otherTarget = oldJob with
        {
            EnvelopeId = "tablet-job",
            TargetDeviceId = "tablet"
        };
        var control = oldJob with
        {
            EnvelopeId = "control",
            Kind = MeshKinds.TopicRunUpdate,
            Plaintext = "control"
        };

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertDeviceEnvelopeOutbox(oldJob);
            db.UpsertDeviceEnvelopeOutbox(otherTarget);
            db.UpsertDeviceEnvelopeOutbox(control);
            db.ReplaceDeviceEnvelopeOutboxForTargetAndKind(newJob);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var queued = reopened.ListDeviceEnvelopeOutbox();
        Assert.HasCount(3, queued);
        Assert.IsFalse(queued.Any(item => item.EnvelopeId == oldJob.EnvelopeId));
        Assert.IsTrue(queued.Any(item => item.EnvelopeId == newJob.EnvelopeId
                                         && item.Plaintext == "new"));
        Assert.IsTrue(queued.Any(item => item.EnvelopeId == otherTarget.EnvelopeId));
        Assert.IsTrue(queued.Any(item => item.EnvelopeId == control.EnvelopeId));
    }

    [TestMethod]
    public void DeviceEnvelopeOutbox_ConditionalReplacementPreservesPreferredJob()
    {
        var created = new DateTimeOffset(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);
        var current = new MeshDb.DeviceEnvelopeOutboxItem(
            "current", "phone", "internal.snapshot", "format-2", null, created);
        var legacy = current with
        {
            EnvelopeId = "legacy",
            Plaintext = "format-1",
            CreatedAt = created.AddMinutes(1)
        };
        var otherTarget = current with
        {
            EnvelopeId = "tablet",
            TargetDeviceId = "tablet"
        };

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertDeviceEnvelopeOutbox(current);
            db.UpsertDeviceEnvelopeOutbox(otherTarget);
            db.ReplaceDeviceEnvelopeOutboxForTargetAndKind(
                legacy,
                existing => !string.Equals(
                    existing.Plaintext, "format-2", StringComparison.Ordinal));
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var queued = reopened.ListDeviceEnvelopeOutbox();
        Assert.HasCount(2, queued);
        Assert.IsTrue(queued.Any(item => item.EnvelopeId == current.EnvelopeId));
        Assert.IsFalse(queued.Any(item => item.EnvelopeId == legacy.EnvelopeId));
        Assert.IsTrue(queued.Any(item => item.EnvelopeId == otherTarget.EnvelopeId));
    }

    [TestMethod]
    public void InboundRejection_PersistsMetadataAndDeduplicatesAcrossRestart()
    {
        var rejectedAt = new DateTimeOffset(2026, 8, 2, 7, 0, 0, TimeSpan.Zero);
        var rejection = new MeshDb.InboundRejectionItem(
            "relay:delivery-1",
            "envelope-1",
            "delivery-1",
            MeshKinds.TopicRunRequest,
            "owner",
            "source-device",
            "topic_request_payload_invalid",
            rejectedAt);

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertInboundRejection(rejection);
            db.UpsertInboundRejection(rejection with
            {
                Reason = "topic_request_identity_conflict",
                RejectedAt = rejectedAt.AddMinutes(1)
            });
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var persisted = reopened.ListInboundRejections();
        Assert.HasCount(1, persisted);
        Assert.AreEqual(rejection.EnvelopeId, persisted[0].EnvelopeId);
        Assert.AreEqual("topic_request_identity_conflict", persisted[0].Reason);
        Assert.AreEqual(1, reopened.PruneInboundRejections(rejectedAt.AddMinutes(2)));
        Assert.HasCount(0, reopened.ListInboundRejections());
    }
    [TestMethod]
    public void InboundTerminalAndEnvelopeOutbox_CommitAsOneWinner()
    {
        var created = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);
        var request = new TopicRunRequestPayload(
            "run-target-atomic",
            "thread-target-atomic",
            "line-target-atomic",
            "owner",
            "Do target work",
            created,
            "desktop-device",
            TopicTurnMode.Single);
        var inbound = new MeshDb.InboundTopicRunItem(
            request.RunId,
            "phone-device",
            request,
            InboundTopicRunStates.Running,
            created,
            created);
        var completed = new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Completed,
            Timestamp: created.AddMinutes(1));
        var completedOutbox = new MeshDb.DeviceEnvelopeOutboxItem(
            "terminal-completed",
            inbound.SourceDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(completed),
            PushHintProtocol.TopicResponse,
            completed.Timestamp);
        var failed = completed with
        {
            Phase = TopicRunPhase.Failed,
            Error = "late failure",
            Timestamp = created.AddMinutes(2)
        };
        var failedOutbox = completedOutbox with
        {
            EnvelopeId = "terminal-failed",
            Plaintext = TopicRunProtocol.UpdateBody(failed),
            CreatedAt = failed.Timestamp
        };

        using (var db = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(db.TryAddInboundTopicRun(inbound));
            Assert.IsTrue(db.SetInboundTopicRunTerminalAndQueue(
                request.RunId,
                InboundTopicRunStates.Completed,
                completed,
                completedOutbox));
            Assert.IsTrue(db.SetInboundTopicRunTerminalAndQueue(
                request.RunId,
                InboundTopicRunStates.Failed,
                failed,
                failedOutbox));
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var persisted = reopened.GetInboundTopicRun(request.RunId)!;
        Assert.AreEqual(InboundTopicRunStates.Completed, persisted.State);
        Assert.IsTrue(TopicRunProtocol.TryParseUpdate(persisted.TerminalUpdateJson, out var winner));
        Assert.AreEqual(TopicRunPhase.Completed, winner.Phase);
        Assert.HasCount(0, reopened.ListInboundTopicRuns(
            InboundTopicRunStates.Accepted,
            InboundTopicRunStates.Running));
        var queued = reopened.ListDeviceEnvelopeOutbox();
        Assert.HasCount(1, queued);
        Assert.AreEqual(completedOutbox.EnvelopeId, queued[0].EnvelopeId);
    }

    [TestMethod]
    public void InboundCancellationTombstone_PersistsFirstIdentityAcrossRestart()
    {
        var created = new DateTimeOffset(2026, 8, 2, 8, 30, 0, TimeSpan.Zero);
        var terminal = new TopicRunUpdatePayload(
            "run-cancel-first",
            "thread-cancel-first",
            TopicRunPhase.Cancelled,
            Status: "Cancelled",
            Timestamp: created);
        var item = new MeshDb.InboundTopicCancellationItem(
            terminal.RunId,
            "phone-device",
            terminal.ThreadId,
            TopicRunProtocol.UpdateBody(terminal),
            created);

        using (var db = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(db.TryAddInboundTopicCancellation(item));
            Assert.IsFalse(db.TryAddInboundTopicCancellation(item with
            {
                SourceDeviceId = "conflicting-device"
            }));
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var persisted = reopened.GetInboundTopicCancellation(item.RunId)!;
        Assert.AreEqual(item.SourceDeviceId, persisted.SourceDeviceId);
        Assert.AreEqual(item.ThreadId, persisted.ThreadId);
        Assert.AreEqual(item.TerminalUpdateJson, persisted.TerminalUpdateJson);
    }
    [TestMethod]
    public void CompleteOwnThreadRunAndDeleteTopicOutbox_CommitsTogether()
    {
        var created = new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        var completed = created.AddMinutes(5);
        var request = new TopicRunRequestPayload(
            "run-atomic",
            "thread-atomic",
            "line-atomic",
            "owner",
            "Do atomic work",
            created,
            "desktop-device",
            TopicTurnMode.Single);
        var outbox = new MeshDb.TopicOutboxItem(
            request.RunId,
            request.ThreadId,
            request.TriggerLineId,
            request.TargetDeviceId,
            request,
            [],
            TopicOutboxStates.RelayQueued,
            created,
            created);

        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertOwnThread(request.ThreadId, "Atomic", created, 0, created);
            Assert.IsTrue(db.SetOwnThreadExecutionAndActivity(
                request.ThreadId,
                request.TargetDeviceId,
                "Desktop",
                DevicePlatforms.Windows,
                created,
                request.RunId,
                created));
            db.UpsertTopicOutbox(outbox);

            Assert.IsFalse(db.CompleteOwnThreadRunAndDeleteTopicOutbox(
                request.ThreadId,
                "different-run",
                request.TriggerLineId,
                request.TargetDeviceId,
                "Desktop",
                DevicePlatforms.Windows,
                created,
                completed));
            Assert.IsNotNull(db.GetTopicOutbox(request.RunId));

            Assert.IsTrue(db.CompleteOwnThreadRunAndDeleteTopicOutbox(
                request.ThreadId,
                request.RunId,
                request.TriggerLineId,
                request.TargetDeviceId,
                "Desktop",
                DevicePlatforms.Windows,
                created,
                completed));
            Assert.IsNull(db.GetTopicOutbox(request.RunId));
            SaveProfile(db);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var thread = reopened.LoadProfile()!.OwnThreads.Single(item => item.Id == request.ThreadId);
        Assert.IsNull(thread.ExecutionRunId);
        Assert.AreEqual(completed.UtcTicks, thread.LastActivityAt?.UtcTicks);
        Assert.IsNull(reopened.GetTopicOutbox(request.RunId));
    }
    [TestMethod]
    public void LegacyReorder_DoesNotChangeActivity()
    {
        var activity = DateTimeOffset.UtcNow.AddDays(1);
        using var db = MeshDb.Open(databasePath, key);
        db.UpsertOwnThread("first", "First", activity.AddDays(-2), 0, activity);
        db.UpsertOwnThread("second", "Second", activity.AddDays(-1), 1, activity.AddHours(-1));
        SaveProfile(db);

        db.ReorderOwnThreads(["second", "first"], "first", DateTimeOffset.UtcNow);

        var reordered = db.LoadProfile()!.OwnThreads;
        Assert.AreEqual("second", reordered[0].Id);
        Assert.AreEqual(activity.UtcTicks, reordered.Single(t => t.Id == "first").LastActivityAt?.UtcTicks);
    }

    [TestMethod]
    public async Task DurableWrites_ConcurrentAcceptanceAndCleanup_AreExactlyOnceAndRestartSafe()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new TopicRunRequestPayload(
            "run-contention",
            "thread-contention",
            "line-contention",
            "owner",
            "Do reliable work",
            now,
            "desktop-device",
            TopicTurnMode.Single);
        var inbound = new MeshDb.InboundTopicRunItem(
            request.RunId,
            "phone-device",
            request,
            InboundTopicRunStates.Accepted,
            now,
            now);
        var staleRequest = request with
        {
            RunId = "run-stale",
            ThreadId = "thread-stale",
            TriggerLineId = "line-stale"
        };
        var stale = new MeshDb.InboundTopicRunItem(
            staleRequest.RunId,
            "phone-device",
            staleRequest,
            InboundTopicRunStates.Accepted,
            now.AddDays(-30),
            now.AddDays(-30));
        var staleTerminal = new TopicRunUpdatePayload(
            stale.RunId,
            stale.Request.ThreadId,
            TopicRunPhase.Completed,
            Timestamp: now.AddDays(-30));

        using (var db = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(db.TryAddInboundTopicRun(stale));
            Assert.IsTrue(db.SetInboundTopicRunTerminal(
                stale.RunId, InboundTopicRunStates.Completed, staleTerminal));
            using (var raw = OpenRawConnection())
            using (var age = raw.CreateCommand())
            {
                age.CommandText = """
                    UPDATE inbound_topic_runs
                    SET updated_at = $updated
                    WHERE run_id = $run;
                    """;
                age.Parameters.AddWithValue("$updated", now.AddDays(-30).ToString("O"));
                age.Parameters.AddWithValue("$run", stale.RunId);
                age.ExecuteNonQuery();
            }

            var acceptances = Enumerable.Range(0, 32)
                .Select(_ => db.ExecuteDurableWriteAsync(
                    () => db.TryAddInboundTopicRun(inbound)))
                .ToArray();
            var cleanup = Enumerable.Range(0, 16)
                .Select(_ => db.ExecuteDurableWriteAsync(
                    () => db.PruneInboundTopicRuns(now.AddDays(-7))))
                .ToArray();

            var results = await Task.WhenAll(acceptances);
            await Task.WhenAll(cleanup);
            Assert.AreEqual(1, results.Count(accepted => accepted));
            Assert.IsNotNull(db.GetInboundTopicRun(inbound.RunId));
            Assert.IsNull(db.GetInboundTopicRun(stale.RunId));
        }

        SqliteConnection.ClearAllPools();
        using var reopened = MeshDb.Open(databasePath, key);
        var recovered = reopened.ListInboundTopicRuns(InboundTopicRunStates.Accepted);
        Assert.HasCount(1, recovered);
        Assert.AreEqual(inbound.RunId, recovered[0].RunId);
    }

    [TestMethod]
    public async Task DurableWriteAsync_DoesNotBlockCallerAndCancellationWinsDuringContention()
    {
        using var db = MeshDb.Open(databasePath, key);
        using var blocker = OpenRawConnection();
        using var transaction = blocker.BeginTransaction(deferred: false);
        using var cancellation = new CancellationTokenSource();
        var terminal = new TopicRunUpdatePayload(
            "run-cancel-contention",
            "thread-cancel-contention",
            TopicRunPhase.Cancelled,
            Status: "Cancelled",
            Timestamp: DateTimeOffset.UtcNow);
        var tombstone = new MeshDb.InboundTopicCancellationItem(
            terminal.RunId,
            "phone-device",
            terminal.ThreadId,
            TopicRunProtocol.UpdateBody(terminal),
            terminal.Timestamp);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var pending = db.ExecuteDurableWriteAsync(
            () => db.TryAddInboundTopicCancellation(tombstone),
            cancellation.Token);
        Assert.IsTrue(started.ElapsedMilliseconds < 100);
        await Task.Delay(100);
        Assert.IsFalse(pending.IsCompleted);

        cancellation.Cancel();
        try
        {
            await pending;
            Assert.Fail("The contended durable write should honor cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.IsTrue(started.ElapsedMilliseconds < 1500);
        Assert.IsNull(db.GetInboundTopicCancellation(tombstone.RunId));
    }

    [TestMethod]
    public async Task DurableWriteAsync_RetriesBoundedBusyAndRecoversAfterLockRelease()
    {
        using var db = MeshDb.Open(databasePath, key);
        var now = DateTimeOffset.UtcNow;
        var request = new TopicRunRequestPayload(
            "run-retry",
            "thread-retry",
            "line-retry",
            "owner",
            "Retry reliably",
            now,
            "desktop-device",
            TopicTurnMode.Single);
        var inbound = new MeshDb.InboundTopicRunItem(
            request.RunId,
            "phone-device",
            request,
            InboundTopicRunStates.Accepted,
            now,
            now);

        using (var blocker = OpenRawConnection())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            var pending = db.ExecuteDurableWriteAsync(
                () => db.TryAddInboundTopicRun(inbound));
            await Task.Delay(400);
            transaction.Commit();
            Assert.IsTrue(await pending);
        }

        var cancelled = new TopicRunUpdatePayload(
            "run-cancel-retry",
            "thread-cancel-retry",
            TopicRunPhase.Cancelled,
            Timestamp: now);
        var cancellation = new MeshDb.InboundTopicCancellationItem(
            cancelled.RunId,
            "phone-device",
            cancelled.ThreadId,
            TopicRunProtocol.UpdateBody(cancelled),
            now);
        using (var blocker = OpenRawConnection())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            var pending = db.ExecuteDurableWriteAsync(
                () => db.TryAddInboundTopicCancellation(cancellation));
            await Task.Delay(400);
            transaction.Commit();
            Assert.IsTrue(await pending);
        }

        using (var blocker = OpenRawConnection())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await db.ExecuteDurableWriteAsync(
                    () => db.TryAddInboundTopicCancellation(
                        new MeshDb.InboundTopicCancellationItem(
                            "run-bounded",
                            "phone-device",
                            "thread-bounded",
                            TopicRunProtocol.UpdateBody(new TopicRunUpdatePayload(
                                "run-bounded",
                                "thread-bounded",
                                TopicRunPhase.Cancelled,
                                Timestamp: now)),
                            now)));
                Assert.Fail("A permanently held writer lock should surface SQLite busy.");
            }
            catch (SqliteException ex)
            {
                Assert.IsTrue(ex.SqliteErrorCode is 5 or 6);
            }
            Assert.IsTrue(elapsed.ElapsedMilliseconds < 4000);
        }

        Assert.IsNotNull(db.GetInboundTopicRun(inbound.RunId));
        using var reopened = MeshDb.Open(databasePath, key);
        Assert.IsNotNull(reopened.GetInboundTopicRun(inbound.RunId));
        Assert.IsNotNull(reopened.GetInboundTopicCancellation(cancellation.RunId));
    }

    private static string CreateJournaledOperationId(
        string threadId,
        string targetDeviceId,
        DateTimeOffset at)
    {
        var journal = new InMemoryTopicSendIdentityStore();
        var sends = new TopicSendCoordinator(identityStore: journal);
        var snapshot = sends.CreateSnapshot(
            threadId,
            targetDeviceId,
            composerRevision: 1,
            draftFingerprint: "database-trigger-test",
            at);
        Assert.AreEqual(
            TopicSendSubmissionKind.Started,
            sends.Submit(
                snapshot,
                (_, _) => throw new TopicSendJournalCrashException(
                    "simulated process termination"))
                .Kind);
        Assert.IsTrue(journal.TryGetUnresolved(
            snapshot.ScopeIdentity,
            out var persisted));
        return persisted!.OperationId;
    }

    private static TopicRunBeginCommand CreateBeginCommand(
        string runId,
        string threadId,
        string lineId,
        string deviceId,
        DateTimeOffset at,
        TopicRunBeginMode mode)
    {
        var draft = new TopicTurnDraft(
            runId,
            threadId,
            lineId,
            "owner",
            "private prompt",
            at,
            TopicTurnMode.Single,
            deviceId);
        var update = new TopicRunUpdatePayload(
            runId,
            threadId,
            TopicRunPhase.Queued,
            "Queued",
            Timestamp: at,
            TriggerLineId: lineId);
        var target = new ExecutionDevice(
            deviceId,
            mode == TopicRunBeginMode.Remote ? "Remote" : "Local",
            DevicePlatforms.Windows);
        var request = mode == TopicRunBeginMode.Remote
            ? new TopicRunRequestPayload(
                runId,
                threadId,
                lineId,
                draft.TriggerHandle,
                draft.Prompt,
                at,
                deviceId,
                TopicTurnMode.Single)
            : null;
        return new TopicRunBeginCommand(
            draft,
            target,
            mode,
            update,
            request,
            mode == TopicRunBeginMode.Remote
                ? [new ChatAttachment("note.txt", "text/plain", [1, 2, 3])]
                : null);
    }

    private static TopicRunBeginCommand RebindRun(
        TopicRunBeginCommand command,
        string runId)
        => command with
        {
            Draft = command.Draft with { RunId = runId },
            InitialProjection = command.InitialProjection with { RunId = runId },
            Request = command.Request is null
                ? null
                : command.Request with { RunId = runId }
        };

    private sealed class InjectedBeginFailure : Exception;

    private void CreatePreTriggerRetainedCandidateFixture(DateTimeOffset at)
    {
        using var raw = OpenRawConnection();
        using var fixture = raw.CreateCommand();
        fixture.CommandText = """
            CREATE TABLE meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);
            INSERT INTO meta(k, v) VALUES('schema_version', '1');
            INSERT INTO meta(k, v) VALUES('topic_run_trigger_schema_version', '1');
            CREATE TABLE topic_run_correlations(
                run_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                terminal_at TEXT);
            CREATE TABLE received_topic_controls(
                envelope_id TEXT PRIMARY KEY,
                source_device_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                control_kind TEXT NOT NULL,
                update_json TEXT NOT NULL,
                received_at TEXT NOT NULL);
            CREATE TABLE inbound_topic_runs(
                run_id TEXT PRIMARY KEY,
                source_device_id TEXT NOT NULL,
                request_json TEXT NOT NULL,
                state TEXT NOT NULL,
                accepted_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                terminal_update_json TEXT);
            CREATE TABLE topic_outbox(
                run_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                trigger_line_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                request_json TEXT NOT NULL,
                attachments_json TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                remote_stage TEXT,
                remote_stage_ordinal INTEGER NOT NULL DEFAULT 0,
                transport_attempt_ordinal INTEGER NOT NULL DEFAULT 0);

            INSERT INTO topic_run_correlations(
                run_id, thread_id, target_device_id, created_at, terminal_at)
            VALUES
                ('run-newer-empty', 'thread-newer-empty', 'executor', $at, NULL),
                ('run-whitespace-only', 'thread-whitespace-only', 'executor', $at, NULL),
                ('run-conflicting-valid', 'thread-conflicting-valid', 'executor', $at, NULL),
                ('run-duplicate-valid', 'thread-duplicate-valid', 'executor', $at, NULL),
                ('run-cross-tier-conflict', 'thread-cross-tier-conflict', 'executor', $at, NULL),
                ('run-overlong-only', 'thread-overlong-only', 'executor', $at, NULL);

            INSERT INTO received_topic_controls(
                envelope_id, source_device_id, run_id, thread_id, control_kind,
                update_json, received_at)
            VALUES
                ('newer-empty-valid', 'executor', 'run-newer-empty', 'thread-newer-empty',
                 'topic.terminal', '{"triggerLineId":"line-older-valid"}', $older),
                ('newer-empty-empty', 'executor', 'run-newer-empty', 'thread-newer-empty',
                 'topic.terminal', '{"triggerLineId":""}', $newer),
                ('whitespace-only', 'executor', 'run-whitespace-only', 'thread-whitespace-only',
                 'topic.terminal', '{"triggerLineId":"   "}', $newer),
                ('conflict-a', 'executor', 'run-conflicting-valid', 'thread-conflicting-valid',
                 'topic.terminal', '{"triggerLineId":"line-conflict-a"}', $older),
                ('conflict-b', 'executor', 'run-conflicting-valid', 'thread-conflicting-valid',
                 'topic.terminal', '{"triggerLineId":"line-conflict-b"}', $newer),
                ('duplicate-a', 'executor', 'run-duplicate-valid', 'thread-duplicate-valid',
                 'topic.terminal', '{"triggerLineId":"line-duplicate"}', $older),
                ('duplicate-b', 'executor', 'run-duplicate-valid', 'thread-duplicate-valid',
                 'topic.terminal', '{"triggerLineId":"line-duplicate"}', $newer),
                ('cross-tier-control', 'executor', 'run-cross-tier-conflict',
                 'thread-cross-tier-conflict', 'topic.terminal',
                 '{"triggerLineId":"line-from-control"}', $newer),
                ('overlong-only', 'executor', 'run-overlong-only', 'thread-overlong-only',
                 'topic.terminal', $overlong, $newer),
                ('outbox-created-control', 'executor', 'run-outbox-created-conflict',
                 'thread-outbox-created-conflict', 'topic.terminal',
                 '{"triggerLineId":"line-from-control"}', $newer);

            INSERT INTO inbound_topic_runs(
                run_id, source_device_id, request_json, state, accepted_at, updated_at,
                terminal_update_json)
            VALUES(
                'run-cross-tier-conflict', 'executor',
                '{"threadId":"thread-cross-tier-conflict","triggerLineId":"line-from-inbound"}',
                'accepted', $older, $older, NULL);

            INSERT INTO topic_outbox(
                run_id, thread_id, trigger_line_id, target_device_id, request_json,
                attachments_json, state, created_at, updated_at)
            VALUES(
                'run-outbox-created-conflict', 'thread-outbox-created-conflict',
                'line-from-outbox', 'executor', '{}', '[]', 'pending', $older, $older);
            """;
        fixture.Parameters.AddWithValue("$at", at.ToString("O"));
        fixture.Parameters.AddWithValue("$older", at.AddMinutes(1).ToString("O"));
        fixture.Parameters.AddWithValue("$newer", at.AddMinutes(2).ToString("O"));
        fixture.Parameters.AddWithValue(
            "$overlong",
            JsonSerializer.Serialize(new { triggerLineId = new string('x', TopicRunProtocol.MaxIdChars + 1) }));
        fixture.ExecuteNonQuery();
    }

    private static string HashMigrationIdentifierForTest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private void CreatePreTriggerCorrelationFixture(DateTimeOffset at)
    {
        using var raw = OpenRawConnection();
        using (var schema = raw.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);
                INSERT INTO meta(k, v) VALUES('schema_version', '1');
                INSERT INTO meta(k, v) VALUES('topic_run_trigger_schema_version', '1');
                CREATE TABLE topic_run_correlations(
                    run_id TEXT PRIMARY KEY,
                    thread_id TEXT NOT NULL,
                    target_device_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    terminal_at TEXT);
                CREATE TABLE topic_outbox(
                    run_id TEXT PRIMARY KEY,
                    thread_id TEXT NOT NULL,
                    trigger_line_id TEXT NOT NULL,
                    target_device_id TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    attachments_json TEXT NOT NULL,
                    state TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_error TEXT,
                    remote_stage TEXT,
                    remote_stage_ordinal INTEGER NOT NULL DEFAULT 0,
                    transport_attempt_ordinal INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE topic_local_runs(
                    run_id TEXT PRIMARY KEY,
                    thread_id TEXT NOT NULL,
                    trigger_line_id TEXT NOT NULL,
                    target_device_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    terminal_at TEXT);
                CREATE TABLE topic_run_triggers(
                    trigger_id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL UNIQUE,
                    mode TEXT NOT NULL,
                    thread_id TEXT NOT NULL,
                    trigger_line_id TEXT NOT NULL,
                    target_device_id TEXT NOT NULL,
                    payload_hash TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    terminal_at TEXT);
                CREATE TABLE inbound_topic_runs(
                    run_id TEXT PRIMARY KEY,
                    source_device_id TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    state TEXT NOT NULL,
                    accepted_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    terminal_update_json TEXT,
                    queue_sequence INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE received_topic_controls(
                    envelope_id TEXT PRIMARY KEY,
                    source_device_id TEXT NOT NULL,
                    run_id TEXT NOT NULL,
                    thread_id TEXT NOT NULL,
                    control_kind TEXT NOT NULL,
                    update_json TEXT NOT NULL,
                    received_at TEXT NOT NULL);
                CREATE TABLE own_threads(
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    execution_device_id TEXT,
                    execution_device_name TEXT,
                    execution_device_platform TEXT,
                    execution_at TEXT,
                    execution_run_id TEXT);
                """;
            schema.ExecuteNonQuery();
        }

        foreach (var state in new[]
                 {
                     TopicOutboxStates.Pending,
                     TopicOutboxStates.RelayQueued,
                     TopicOutboxStates.DeviceQueued,
                     TopicOutboxStates.Running
                 })
        {
            var request = new TopicRunRequestPayload(
                "run-" + state,
                "thread-" + state,
                "line-" + state,
                "owner",
                "old client request",
                at,
                "executor",
                TopicTurnMode.Single);
            using var insert = raw.CreateCommand();
            insert.CommandText = """
                INSERT INTO topic_run_correlations(
                    run_id, thread_id, target_device_id, created_at, terminal_at)
                VALUES($run, $thread, 'executor', $at, NULL);
                INSERT INTO topic_outbox(
                    run_id, thread_id, trigger_line_id, target_device_id, request_json,
                    attachments_json, state, created_at, updated_at)
                VALUES($run, $thread, $line, 'executor', $request, '[]', $state, $at, $at);
                """;
            insert.Parameters.AddWithValue("$run", request.RunId);
            insert.Parameters.AddWithValue("$thread", request.ThreadId);
            insert.Parameters.AddWithValue("$line", request.TriggerLineId);
            insert.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request));
            insert.Parameters.AddWithValue("$state", state);
            insert.Parameters.AddWithValue("$at", at.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var inboundRequest = new TopicRunRequestPayload(
            "run-inbound", "thread-inbound", "line-inbound", "owner",
            "accepted by old client", at, "remote-a", TopicTurnMode.Single);
        var retained = new TopicRunUpdatePayload(
            "run-retained", "thread-retained", TopicRunPhase.Completed, "Completed",
            Timestamp: at.AddMinutes(1), TriggerLineId: "line-retained");
        using var data = raw.CreateCommand();
        data.CommandText = """
            INSERT INTO topic_run_correlations(
                run_id, thread_id, target_device_id, created_at, terminal_at)
            VALUES
                ('run-inbound', 'thread-inbound', 'remote-a', $at, NULL),
                ('run-local', 'thread-local', 'local-device', $at, NULL),
                ('run-retained', 'thread-retained', 'executor', $at, NULL),
                ('run-unresolved-active', 'thread-unresolved', 'executor', $at, NULL),
                ('run-unresolved-tombstone', 'thread-tombstone', 'executor', $at, NULL),
                ('run-unresolved-terminal', 'thread-terminal', 'executor', $at, $terminal);
            INSERT INTO inbound_topic_runs(
                run_id, source_device_id, request_json, state, accepted_at, updated_at,
                terminal_update_json, queue_sequence)
            VALUES('run-inbound', 'remote-a', $inbound, 'running', $at, $at, NULL, 1);
            INSERT INTO topic_local_runs(
                run_id, thread_id, trigger_line_id, target_device_id, created_at, terminal_at)
            VALUES('run-local', 'thread-local', 'line-local', 'local-device', $at, NULL);
            INSERT INTO received_topic_controls(
                envelope_id, source_device_id, run_id, thread_id, control_kind,
                update_json, received_at)
            VALUES(
                'terminal-retained', 'executor', 'run-retained', 'thread-retained',
                'topic.terminal', $retained, $terminal);
            INSERT INTO own_threads(
                id, title, created_at, execution_device_id, execution_device_name,
                execution_device_platform, execution_at, execution_run_id)
            VALUES(
                'thread-unresolved', 'Old in-flight run', $at, 'executor', 'Executor',
                'Windows', $at, 'run-unresolved-active');
            """;
        data.Parameters.AddWithValue("$at", at.ToString("O"));
        data.Parameters.AddWithValue("$terminal", at.AddMinutes(1).ToString("O"));
        data.Parameters.AddWithValue("$inbound", JsonSerializer.Serialize(inboundRequest));
        data.Parameters.AddWithValue("$retained", TopicRunProtocol.UpdateBody(retained));
        data.ExecuteNonQuery();
    }

    private void ClearOwnThreadActivity()
    {
        using var raw = new SqliteConnection($"Data Source={databasePath}");
        raw.Open();
        ApplyKey(raw);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE own_threads SET last_activity_at = NULL;";
        cmd.ExecuteNonQuery();
    }

    private void ClearConversationActivity()
    {
        using var raw = new SqliteConnection($"Data Source={databasePath}");
        raw.Open();
        ApplyKey(raw);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET last_activity_at = NULL;";
        cmd.ExecuteNonQuery();
    }

    private void ApplyKey(SqliteConnection connection)
    {
        var hex = Convert.ToHexString(key);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA key = \"x'{hex}'\";";
        pragma.ExecuteNonQuery();
    }

    private SqliteConnection OpenRawConnection()
    {
        var raw = new SqliteConnection($"Data Source={databasePath}");
        raw.Open();
        ApplyKey(raw);
        using var pragma = raw.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 0; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return raw;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan elapsed)
            => utcNow = utcNow.Add(elapsed);
    }

    private sealed class ControlledTopicTransport(
        Func<
            string,
            string,
            string,
            string,
            string?,
            CancellationToken,
            Task<MeshSendResult?>> send) : ITopicEnvelopeTransport
    {
        public Task<MeshSendResult?> SendAsync(
            string targetDeviceId,
            string kind,
            string plaintext,
            string envelopeId,
            string? pushHint,
            CancellationToken cancellationToken)
            => send(
                targetDeviceId,
                kind,
                plaintext,
                envelopeId,
                pushHint,
                cancellationToken);
    }

    private static void SaveProfile(MeshDb db) => db.SaveProfile(new MeshProfile());
}
