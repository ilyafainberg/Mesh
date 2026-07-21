using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            db.EnsureOwnThread("t4", "Activity Test", DateTimeOffset.UtcNow.AddDays(-1));
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
            Assert.IsTrue(db.TryBindOwnThreadDevice(
                "targeted", "phone-id", "Phone", "android"));
            Assert.IsFalse(db.TryBindOwnThreadDevice(
                "targeted", "other-id", "Other", "windows"));
            Assert.IsTrue(db.MoveOwnThreadToDevice(
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

    private static void SaveProfile(MeshDb db) => db.SaveProfile(new MeshProfile());
}
