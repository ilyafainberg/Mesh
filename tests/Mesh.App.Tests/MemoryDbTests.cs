using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MemoryDbTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "mesh-memory-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void Memory_PersistsAcrossRestartOutsideProfileJson()
    {
        var memory = CreateMemory("memory-1", "Original content");
        memory.RecallCount = 4;
        memory.LastRecalledAt = memory.UpdatedAt.AddHours(1);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertMemory(memory);
            db.SaveProfile(new MeshProfile { Memories = [MemoryPolicy.Clone(memory)] });
        }
        SqliteConnection.ClearAllPools();

        using (var raw = OpenRaw())
        using (var command = raw.CreateCommand())
        {
            command.CommandText = "SELECT json FROM profile WHERE id = 1;";
            var profileJson = (string)command.ExecuteScalar()!;
            Assert.IsFalse(profileJson.Contains("Original content", StringComparison.Ordinal));
            Assert.IsFalse(profileJson.Contains("memories", StringComparison.OrdinalIgnoreCase));
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var loaded = reopened.LoadProfile()!.Memories.Single();
        AssertMemoryShared(memory, loaded);
        Assert.AreEqual(4, loaded.RecallCount);
        Assert.AreEqual(memory.LastRecalledAt?.UtcTicks, loaded.LastRecalledAt?.UtcTicks);
    }

    [TestMethod]
    public void TouchMemories_PersistsLocalRecallUsage()
    {
        var recalledAt = new DateTimeOffset(2026, 3, 2, 10, 30, 0, TimeSpan.Zero);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertMemory(CreateMemory("memory-1", "Content"));
            db.SaveProfile(new MeshProfile());
            db.TouchMemories(["memory-1", "memory-1", "missing"], recalledAt);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        var loaded = reopened.LoadProfile()!.Memories.Single();
        Assert.AreEqual(1, loaded.RecallCount);
        Assert.AreEqual(recalledAt.UtcTicks, loaded.LastRecalledAt?.UtcTicks);
    }

    [TestMethod]
    public void RemoteUpsert_PreservesLocalRecallUsage()
    {
        var local = CreateMemory("memory-1", "Local content");
        local.RecallCount = 7;
        local.LastRecalledAt = local.UpdatedAt.AddHours(2);
        using var db = MeshDb.Open(databasePath, key);
        db.UpsertMemory(local);
        db.SaveProfile(new MeshProfile());

        var remote = CreateMemory("memory-1", "Remote content");
        remote.UpdatedAt = local.UpdatedAt.AddDays(1);
        remote.LastReinforcedAt = remote.UpdatedAt;
        remote.RecallCount = 0;
        remote.LastRecalledAt = null;
        Assert.IsTrue(db.TryApplyMemoryUpsert(
            remote,
            SyncKey(remote.Id),
            Version(20, "remote"),
            DomainProjectionKinds.MemoryDelete));

        var loaded = db.LoadProfile()!.Memories.Single();
        Assert.AreEqual("Remote content", loaded.Content);
        Assert.AreEqual(7, loaded.RecallCount);
        Assert.AreEqual(local.LastRecalledAt?.UtcTicks, loaded.LastRecalledAt?.UtcTicks);
    }

    [TestMethod]
    public void AtomicUpsert_RejectsStaleVersionWithoutChangingContent()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.SaveProfile(new MeshProfile());
        var newer = CreateMemory("memory-1", "Newer");
        var stale = CreateMemory("memory-1", "Stale");

        Assert.IsTrue(db.TryApplyMemoryUpsert(
            newer,
            SyncKey(newer.Id),
            Version(20, "newer"),
            DomainProjectionKinds.MemoryDelete));
        Assert.IsFalse(db.TryApplyMemoryUpsert(
            stale,
            SyncKey(stale.Id),
            Version(10, "stale"),
            DomainProjectionKinds.MemoryDelete));

        Assert.AreEqual("Newer", db.LoadProfile()!.Memories.Single().Content);
        Assert.AreEqual(Version(20, "newer"), db.GetSyncVersion(SyncKey(newer.Id)));
    }

    [TestMethod]
    public void DeleteTombstone_BlocksStaleResurrectionAndAllowsNewerRecreation()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.SaveProfile(new MeshProfile());
        var original = CreateMemory("memory-1", "Original");

        Assert.IsTrue(db.TryApplyMemoryUpsert(
            original,
            SyncKey(original.Id),
            Version(10, "original"),
            DomainProjectionKinds.MemoryDelete));
        Assert.IsTrue(db.TryApplyMemoryDelete(
            original.Id,
            DomainProjectionKinds.MemoryDelete,
            Version(20, "delete"),
            SyncKey(original.Id)));
        Assert.IsFalse(db.TryApplyMemoryUpsert(
            CreateMemory(original.Id, "Stale resurrection"),
            SyncKey(original.Id),
            Version(15, "stale"),
            DomainProjectionKinds.MemoryDelete));
        Assert.AreEqual(0, db.LoadProfile()!.Memories.Count);

        Assert.IsTrue(db.TryApplyMemoryUpsert(
            CreateMemory(original.Id, "Recreated"),
            SyncKey(original.Id),
            Version(30, "recreated"),
            DomainProjectionKinds.MemoryDelete));
        Assert.AreEqual("Recreated", db.LoadProfile()!.Memories.Single().Content);
    }

    [TestMethod]
    public void DeleteMemory_RemovesPersistedMemory()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.UpsertMemory(CreateMemory("memory-1", "Content"));
            db.SaveProfile(new MeshProfile());
            db.DeleteMemory("memory-1");
        }
        SqliteConnection.ClearAllPools();

        using var reopened = MeshDb.Open(databasePath, key);
        Assert.AreEqual(0, reopened.LoadProfile()!.Memories.Count);
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        var hex = Convert.ToHexString(key);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA key = \"x'{hex}'\";";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static string SyncKey(string id)
        => DomainProjectionKinds.MemoryUpsert + "\u001f" + id;

    private static string Version(int ticks, string operation)
        => ProjectionVersion.Create(
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks),
            "device-a",
            operation);

    private static MemoryItem CreateMemory(string id, string content)
    {
        var created = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);
        return new MemoryItem
        {
            Id = id,
            Title = "Memory title",
            Content = content,
            Category = MemoryCategories.Preference,
            Origin = MemoryOrigins.Explicit,
            Importance = 0.82,
            Confidence = 0.93,
            Stability = 0.88,
            ReinforcementCount = 3,
            SourceThreadId = "topic-1",
            SourceLineId = "line-1",
            CreatedAt = created,
            UpdatedAt = created.AddDays(3),
            LastReinforcedAt = created.AddDays(3)
        };
    }

    private static void AssertMemoryShared(MemoryItem expected, MemoryItem actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Title, actual.Title);
        Assert.AreEqual(expected.Content, actual.Content);
        Assert.AreEqual(expected.Category, actual.Category);
        Assert.AreEqual(expected.Origin, actual.Origin);
        Assert.AreEqual(expected.Importance, actual.Importance, 0.0001);
        Assert.AreEqual(expected.Confidence, actual.Confidence, 0.0001);
        Assert.AreEqual(expected.Stability, actual.Stability, 0.0001);
        Assert.AreEqual(expected.ReinforcementCount, actual.ReinforcementCount);
        Assert.AreEqual(expected.SourceThreadId, actual.SourceThreadId);
        Assert.AreEqual(expected.SourceLineId, actual.SourceLineId);
        Assert.AreEqual(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
        Assert.AreEqual(expected.UpdatedAt.UtcTicks, actual.UpdatedAt.UtcTicks);
        Assert.AreEqual(expected.LastReinforcedAt.UtcTicks, actual.LastReinforcedAt.UtcTicks);
    }
}
