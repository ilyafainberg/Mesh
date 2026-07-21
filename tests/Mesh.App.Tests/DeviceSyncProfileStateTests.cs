using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DeviceSyncProfileStateTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "mesh-sync-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    [TestMethod]
    public void ContactMerge_PreservesTokensAndRetainedMemberships()
    {
        var existing = new Contact { Handle = "alice", TokensSpent = 123 };
        var incoming = Contact("alice", ["Friends", "Deleted"]);

        var merged = ProfileSyncState.MergeContact(existing, incoming, ["Friends", "Deleted"]);

        Assert.AreEqual(123, merged.TokensSpent);
        CollectionAssert.AreEqual(new[] { "Friends", "Deleted" }, merged.Circles);
    }

    [TestMethod]
    public void Projection_PreservesUnresolvedMembershipForRetrySafety()
    {
        var profile = Profile(
            contacts: [new Contact { Handle = "alice", Circles = ["Friends", "Missing"] }],
            circles: [new Circle { Name = "Friends" }]);

        var projected = ProfileSyncState.Snapshot(profile);

        CollectionAssert.AreEqual(
            new[] { "Friends", "Missing" },
            projected.Contacts["alice"].Circles.ToArray());
    }

    [TestMethod]
    public void GuestAuthorization_IgnoresUnresolvedMembershipUntilCircleExists()
    {
        var contact = new Contact { Handle = "alice", Circles = ["Friends"] };

        Assert.AreEqual(0, ProfileSyncState.ResolveGuestCircles(contact, []).Count);
        CollectionAssert.AreEqual(
            new[] { "Friends" },
            ProfileSyncState.ResolveGuestCircles(
                contact,
                [new Circle { Name = "Friends" }]).ToArray());
    }

    [TestMethod]
    public void RecreatedCircle_CanBeJoinedOnlyAfterNewerUpsertAndLaterContact()
    {
        var delete = Version(20, "delete");
        var staleUpsert = Version(10, "stale");
        var recreatedUpsert = Version(30, "recreated");
        var incoming = Contact("alice", ["Friends"]);

        Assert.IsFalse(ProfileSyncState.IsCircleAvailable(true, staleUpsert, delete));
        var revoked = ProfileSyncState.MergeContact(null, incoming, []);
        Assert.AreEqual(0, revoked.Circles.Count);

        Assert.IsTrue(ProfileSyncState.IsCircleAvailable(true, recreatedUpsert, delete));
        var joined = ProfileSyncState.MergeContact(revoked, incoming, ["Friends"]);
        CollectionAssert.AreEqual(new[] { "Friends" }, joined.Circles);
    }

    [TestMethod]
    public void LegacySnapshot_AppliesAllCircleStateBeforeContactMembership()
    {
        const string source = "legacy-device";
        var circlePayload = """{"name":"Friends","requireApproval":false}""";
        var contactPayload = """{"handle":"alice","displayName":"Alice","circles":["Friends"],"allowed":true,"signingKeys":[],"keyChanged":false,"muted":false,"blocked":false}""";
        var circle = Operation(
            DeviceSyncKinds.CircleUpsert,
            "friends",
            ProfileSyncState.LegacyVersion(source, DeviceSyncKinds.CircleUpsert, "friends", circlePayload),
            circlePayload);
        var deletedCircle = Operation(
            DeviceSyncKinds.CircleDelete,
            "old",
            ProfileSyncState.LegacyVersion(source, DeviceSyncKinds.CircleDelete, "old", ""),
            "");
        var contact = Operation(
            DeviceSyncKinds.ContactUpsert,
            "alice",
            ProfileSyncState.LegacyVersion(source, DeviceSyncKinds.ContactUpsert, "alice", contactPayload),
            contactPayload);

        var ordered = ProfileSyncState.OrderForApplication([contact, deletedCircle, circle]);

        Assert.IsTrue(ordered.Take(2).All(operation =>
            operation.Kind is DeviceSyncKinds.CircleUpsert or DeviceSyncKinds.CircleDelete));
        Assert.AreEqual(DeviceSyncKinds.ContactUpsert, ordered[2].Kind);

        var activeCircles = ordered
            .Take(2)
            .Where(operation => operation.Kind == DeviceSyncKinds.CircleUpsert)
            .Select(_ => "Friends")
            .ToList();
        var merged = ProfileSyncState.MergeContact(null, Contact("alice", ["Friends"]), activeCircles);
        CollectionAssert.AreEqual(new[] { "Friends" }, merged.Circles.ToArray());
    }

    [TestMethod]
    public void RenameUpsert_IsAppliedBeforeItsOlderDeleteToPreserveReferences()
    {
        var deleteVersion = Version(10, "delete-old");
        var renamePayload = System.Text.Json.JsonSerializer.Serialize(
            new DeviceSyncCircle(
                "Close Friends",
                false,
                [new DeviceSyncCircleRename("Friends", deleteVersion)]));
        var delete = Operation(
            DeviceSyncKinds.CircleDelete,
            "friends",
            deleteVersion,
            "");
        var rename = Operation(
            DeviceSyncKinds.CircleUpsert,
            "close friends",
            Version(20, "rename"),
            renamePayload);

        var ordered = ProfileSyncState.OrderForApplication([delete, rename]);

        Assert.AreSame(rename, ordered[0]);
        Assert.AreSame(delete, ordered[1]);
        var profile = Profile(
            contacts: [new Contact { Handle = "alice", Circles = ["Friends"] }],
            circles: [new Circle { Name = "Friends" }]);
        AddVisibilitySurfaces(profile, "shared:Friends");
        ProfileSyncState.RenameCircleReferences(profile, "Friends", "Close Friends");
        AssertVisibilities(profile, "shared:Close Friends");
    }

    [TestMethod]
    public void RenameCircle_UpdatesMembershipAndEveryVisibilitySurface()
    {
        var profile = Profile(
            contacts: [new Contact { Handle = "alice", Circles = ["friends", "FRIENDS"] }],
            circles: [new Circle { Name = "Friends" }]);
        AddVisibilitySurfaces(profile, "shared:friends");
        profile.Widgets.Add(new Widget { Visibility = SystemCircles.PublicVisibility });

        ProfileSyncState.RenameCircleReferences(profile, "Friends", "Close Friends");

        CollectionAssert.AreEqual(
            new[] { "Close Friends" },
            profile.Contacts[0].Circles.ToArray());
        AssertVisibilities(profile, "shared:Close Friends");
        Assert.AreEqual(SystemCircles.PublicVisibility, profile.Widgets[1].Visibility);
    }

    [TestMethod]
    public void DeleteCircle_RemovesMembershipAndMakesEveryVisibilityPrivate()
    {
        var profile = Profile(
            contacts: [new Contact { Handle = "alice", Circles = ["FRIENDS", "Work"] }],
            circles: [new Circle { Name = "Friends" }]);
        AddVisibilitySurfaces(profile, "shared:Friends");

        ProfileSyncState.DeleteCircleReferences(profile, "friends");

        CollectionAssert.AreEqual(new[] { "Work" }, profile.Contacts[0].Circles.ToArray());
        AssertVisibilities(profile, "private");
    }

    [TestMethod]
    public void LegacyBaseline_CannotBeatModernTombstone()
    {
        var first = ProfileSyncState.LegacyVersion("device-a", DeviceSyncKinds.ContactUpsert, "alice", "{}");
        var divergent = ProfileSyncState.LegacyVersion("device-b", DeviceSyncKinds.ContactUpsert, "alice", "{}");
        var tombstone = Version(DateTimeOffset.UtcNow.UtcTicks, "delete");

        Assert.AreNotEqual(first, divergent);
        Assert.IsTrue(DeviceSyncVersion.IsNewer(tombstone, first));
        Assert.IsFalse(DeviceSyncVersion.IsNewer(first, tombstone));
        Assert.IsTrue(first.StartsWith(DateTimeOffset.UnixEpoch.UtcTicks.ToString("D19") + "|", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AtomicWrite_CommitsProfileAndVersionTogether()
    {
        using var db = MeshDb.Open(databasePath, key);
        var profile = Profile(contacts: [new Contact { Handle = "alice" }]);
        var version = Version(10, "upsert");

        db.SaveProfileAndSyncState(
            profile,
            [new MeshDb.SyncVersionWrite("contact.upsert\u001falice", version)],
            []);

        Assert.IsNotNull(db.LoadProfile()!.Contacts.SingleOrDefault(contact => contact.Handle == "alice"));
        Assert.AreEqual(version, db.GetSyncVersion("contact.upsert\u001falice"));
    }

    [TestMethod]
    public void AtomicWrite_RollsBackProfileAndVersionOnFailure()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.SaveProfile(Profile(contacts: [new Contact { Handle = "before" }]));
        var changed = Profile(contacts: [new Contact { Handle = "after" }]);
        AddVisibilitySurfaces(changed, "shared:Friends");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            db.SaveProfileAndSyncState(
                changed,
                [new MeshDb.SyncVersionWrite("contact.upsert\u001fafter", Version(10, "upsert"))],
                [],
                () => throw new InvalidOperationException("fail before commit"),
                [new MeshDb.SyncCircleRenameWrite(
                    "close-friends",
                    [new DeviceSyncCircleRename(
                        "Friends",
                        Version(9, "delete"))])]));

        CollectionAssert.AreEqual(
            new[] { "before" },
            db.LoadProfile()!.Contacts.Select(contact => contact.Handle).ToArray());
        Assert.AreEqual(0, db.LoadProfile()!.Knowledge.Count);
        Assert.IsNull(db.GetSyncVersion("contact.upsert\u001fafter"));
        Assert.AreEqual(0, db.GetSyncCircleRenames("close-friends").Count);
    }

    [TestMethod]
    public void AtomicWrite_RejectsStaleVersionWithoutChangingProfile()
    {
        using var db = MeshDb.Open(databasePath, key);
        var entityKey = "contact.upsert\u001falice";
        var current = Version(20, "current");
        Assert.IsTrue(db.SaveProfileAndSyncState(
            Profile(contacts: [new Contact { Handle = "current" }]),
            [new MeshDb.SyncVersionWrite(entityKey, current)],
            []));

        var accepted = db.SaveProfileAndSyncState(
            Profile(contacts: [new Contact { Handle = "stale" }]),
            [new MeshDb.SyncVersionWrite(entityKey, Version(10, "stale"))],
            []);

        Assert.IsFalse(accepted);
        CollectionAssert.AreEqual(
            new[] { "current" },
            db.LoadProfile()!.Contacts.Select(contact => contact.Handle).ToArray());
        Assert.AreEqual(current, db.GetSyncVersion(entityKey));
    }

    [TestMethod]
    public async Task AtomicWrite_ConcurrentStaleAndNewWritesKeepMatchingNewestPayload()
    {
        var entityKey = "contact.upsert\u001falice";
        var baseline = Version(5, "baseline");
        using (var seed = MeshDb.Open(databasePath, key))
            Assert.IsTrue(seed.SaveProfileAndSyncState(
                Profile(contacts: [new Contact { Handle = "baseline" }]),
                [new MeshDb.SyncVersionWrite(entityKey, baseline)],
                []));

        using var staleDb = MeshDb.Open(databasePath, key);
        using var newestDb = MeshDb.Open(databasePath, key);
        var start = new Barrier(2);
        var stale = Task.Run(() =>
        {
            start.SignalAndWait();
            return staleDb.SaveProfileAndSyncState(
                Profile(contacts: [new Contact { Handle = "stale" }]),
                [new MeshDb.SyncVersionWrite(entityKey, Version(10, "stale"))],
                []);
        });
        var newestVersion = Version(20, "newest");
        var newest = Task.Run(() =>
        {
            start.SignalAndWait();
            return newestDb.SaveProfileAndSyncState(
                Profile(contacts: [new Contact { Handle = "newest" }]),
                [new MeshDb.SyncVersionWrite(entityKey, newestVersion)],
                []);
        });

        await Task.WhenAll(stale, newest);

        using var verify = MeshDb.Open(databasePath, key);
        Assert.IsTrue(newest.Result);
        Assert.AreEqual(newestVersion, verify.GetSyncVersion(entityKey));
        CollectionAssert.AreEqual(
            new[] { "newest" },
            verify.LoadProfile()!.Contacts.Select(contact => contact.Handle).ToArray());
    }

    [TestMethod]
    public void AtomicWrite_RejectsUpsertOlderThanDeleteTombstone()
    {
        using var db = MeshDb.Open(databasePath, key);
        var entityKey = "contact.upsert\u001falice";
        var deleteVersion = Version(20, "delete");
        Assert.IsTrue(db.SaveProfileAndSyncState(
            Profile(),
            [],
            [new MeshDb.SyncTombstoneWrite(
                DeviceSyncKinds.ContactDelete, "alice", deleteVersion)]));

        Assert.IsFalse(db.SaveProfileAndSyncState(
            Profile(contacts: [new Contact { Handle = "stale" }]),
            [new MeshDb.SyncVersionWrite(entityKey, Version(10, "stale"))],
            []));

        Assert.AreEqual(0, db.LoadProfile()!.Contacts.Count);
        Assert.AreEqual(deleteVersion, db.GetSyncTombstoneVersion(
            DeviceSyncKinds.ContactDelete, "alice"));
    }

    [TestMethod]
    public void DeleteAndTombstone_SurviveRestartTogether()
    {
        var version = Version(20, "delete");
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.SaveProfile(Profile(contacts: [new Contact { Handle = "alice" }]));
            db.SaveProfileAndSyncState(
                Profile(contacts: []),
                [],
                [new MeshDb.SyncTombstoneWrite(DeviceSyncKinds.ContactDelete, "alice", version)]);
        }

        using var reopened = MeshDb.Open(databasePath, key);
        Assert.AreEqual(0, reopened.LoadProfile()!.Contacts.Count);
        Assert.AreEqual(
            version,
            reopened.GetSyncTombstoneVersion(DeviceSyncKinds.ContactDelete, "alice"));
    }

    [TestMethod]
    public void CircleRenameLineage_SurvivesRestartForSnapshots()
    {
        var deleteVersion = Version(20, "delete");
        using (var db = MeshDb.Open(databasePath, key))
        {
            Assert.IsTrue(db.SaveProfileAndSyncState(
                Profile(circles: [new Circle { Name = "Close Friends" }]),
                [new MeshDb.SyncVersionWrite(
                    "circle.upsert\u001fclose friends",
                    Version(30, "rename"))],
                [new MeshDb.SyncTombstoneWrite(
                    DeviceSyncKinds.CircleDelete,
                    "friends",
                    deleteVersion)],
                circleRenames:
                [
                    new MeshDb.SyncCircleRenameWrite(
                        "close friends",
                        [
                            new DeviceSyncCircleRename("Friends", deleteVersion),
                            new DeviceSyncCircleRename("Trusted", Version(10, "older-delete"))
                        ])
                ]));
        }

        using var reopened = MeshDb.Open(databasePath, key);
        var renames = reopened.GetSyncCircleRenames("close friends");
        Assert.HasCount(2, renames);
        Assert.AreEqual("Friends", renames[0].PreviousName);
        Assert.AreEqual(deleteVersion, renames[0].DeleteVersion);
        Assert.AreEqual("Trusted", renames[1].PreviousName);
    }

    private static DeviceSyncContact Contact(string handle, IReadOnlyList<string> circles)
        => new(handle, "Alice", circles, true, ["key"], false, false, false);

    private static MeshProfile Profile(
        IReadOnlyList<Contact>? contacts = null,
        IReadOnlyList<Circle>? circles = null)
        => new()
        {
            Contacts = contacts?.ToList() ?? [],
            Circles = circles?.ToList() ?? []
        };

    private static string Version(long ticks, string operation)
        => DeviceSyncVersion.Create(
            new DateTimeOffset(ticks, TimeSpan.Zero),
            "device",
            operation);

    private static DeviceSyncOperation Operation(
        string kind,
        string entityId,
        string version,
        string payload = "{}")
        => new(Guid.NewGuid().ToString("n"), "remote", kind, entityId, version, payload);

    private static void AddVisibilitySurfaces(MeshProfile profile, string visibility)
    {
        profile.Knowledge.Add(new KnowledgeItem { Visibility = visibility });
        profile.Skills.Add(new Skill { Visibility = visibility });
        profile.Widgets.Add(new Widget { Visibility = visibility });
        profile.Sources.Add(new ConnectedSource
        {
            Visibility = visibility,
            Folders = [new FolderGrant { Visibility = visibility }],
            DrivePaths = [new FolderGrant { Visibility = visibility }]
        });
        profile.LocalTools[LocalToolKind.Browser] = new LocalToolSetting { Visibility = visibility };
        profile.McpServers["server"] = new LocalToolSetting { Visibility = visibility };
        profile.CustomMcpServers.Add(new CustomMcpServer { Visibility = visibility });
    }

    private static void AssertVisibilities(MeshProfile profile, string expected)
    {
        Assert.AreEqual(expected, profile.Knowledge[0].Visibility);
        Assert.AreEqual(expected, profile.Skills[0].Visibility);
        Assert.AreEqual(expected, profile.Widgets[0].Visibility);
        Assert.AreEqual(expected, profile.Sources[0].Visibility);
        Assert.AreEqual(expected, profile.Sources[0].Folders[0].Visibility);
        Assert.AreEqual(expected, profile.Sources[0].DrivePaths[0].Visibility);
        Assert.AreEqual(expected, profile.LocalTools[LocalToolKind.Browser].Visibility);
        Assert.AreEqual(expected, profile.McpServers["server"].Visibility);
        Assert.AreEqual(expected, profile.CustomMcpServers[0].Visibility);
    }
}
