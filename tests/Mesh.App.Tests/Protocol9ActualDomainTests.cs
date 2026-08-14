using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;

namespace Mesh.App.Tests;

/// <summary>
/// Protocol 9 ACTUAL domain behaviour.
///
/// These tests do not assert on the generic <c>replication_domain_*</c> convergence index; they
/// assert on the REAL Mesh tables the UI reads (conversations / chat_lines, own_threads / own_chat,
/// the profile blob that carries contacts and circles, memories, assets / asset_content,
/// ask_user_prompts, the read watermark table and the skill-package staging tables), plus the
/// in-memory profile materialisation that runs after the transaction commits.
///
/// Two seams are exercised:
///   * the LOCAL path - <see cref="ReplicationJournal.EmitLocal"/> writes the actual domain rows,
///     the signed event and its outbox references in ONE transaction;
///   * the INBOUND path - a delivered event materialises the same actual rows.
/// Both converge on <see cref="ReplicationDomainMaterializer"/>, so a local change and a replicated
/// change can never disagree about what "applied" means.
/// </summary>
[TestClass]
public sealed class Protocol9ActualDomainTests : ReplicationTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- helpers -----------------------------------------------------------

    private static ChatLine Line(string id, string text = "hello", string role = "user")
        => new() { Id = id, Text = text, Role = role, At = DateTimeOffset.UtcNow, Via = "person" };

    private static string Body(object value) => JsonSerializer.Serialize(value, Json);

    private static ReplicationPayloadCodec.DomainEnvelope LineEnv(
        string conversation, ChatLine line, string causal = "v1",
        string kind = ReplicationOpKinds.Message)
        => new(kind, ReplicationPayloadCodec.DomainAction.AppendLine,
            conversation, conversation, causal, Body(line));

    private static long Scalar(MeshDb db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = db.RawConnectionForTest.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result);
    }

    private static string? Text(MeshDb db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = db.RawConnectionForTest.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is null || result is DBNull ? null : Convert.ToString(result);
    }

    private static long ChatLineCount(MeshDb db, string handle, string lineId)
        => Scalar(db, "SELECT COUNT(*) FROM chat_lines WHERE handle = $h AND line_id = $l;",
            ("$h", handle), ("$l", lineId));

    private static long OwnChatCount(MeshDb db, string threadId, string lineId)
        => Scalar(db, "SELECT COUNT(*) FROM own_chat WHERE thread_id = $t AND line_id = $l;",
            ("$t", threadId), ("$l", lineId));

    private static long PendingOutbox(MeshDb db)
        => Scalar(db, "SELECT COUNT(*) FROM replication_outbox WHERE state = 'pending';");

    private static long EventCount(MeshDb db)
        => Scalar(db, "SELECT COUNT(*) FROM replication_events;");

    private static MeshProfile? ProfileBlob(MeshDb db)
    {
        var json = Text(db, "SELECT json FROM profile WHERE id = 1;");
        return json is null ? null : JsonSerializer.Deserialize<MeshProfile>(json, Json);
    }

    private OfflineJournal Local(string handle = "alice", string device = "dev-a", bool desktop = true)
    {
        AddSibling(handle, device + "-sib");
        return NewJournal(handle, device, desktop);
    }

    // =======================================================================
    // 1. LOCAL: actual rows + event + outbox in one transaction
    // =======================================================================

    [TestMethod]
    public void LocalPersonMessage_WritesActualChatLineRow()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1", "hey bob")), new[] { "alice", "bob" });
        Assert.AreEqual(1, ChatLineCount(node.Db, "bob", "l-1"));
    }

    [TestMethod]
    public void LocalPersonMessage_WritesEventAndOutboxWithTheSameRow()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        Assert.AreEqual(1, EventCount(node.Db));
        Assert.IsTrue(PendingOutbox(node.Db) >= 1);
        Assert.AreEqual(1, ChatLineCount(node.Db, "bob", "l-1"));
    }

    [TestMethod]
    public void LocalPersonMessage_CreatesTheConversationRow()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM conversations WHERE handle = 'bob';"));
    }

    [TestMethod]
    public void LocalPersonMessage_StoresTheActualLineText()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1", "the actual text")), new[] { "alice", "bob" });
        Assert.AreEqual("the actual text",
            Text(node.Db, "SELECT text FROM chat_lines WHERE line_id = 'l-1';"));
    }

    [TestMethod]
    public void LocalMessage_OfflineWithNoEngine_StillCreatesPendingOutbox()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        Assert.IsTrue(PendingOutbox(node.Db) > 0, "an offline local change must leave pending outbox refs");
    }

    [TestMethod]
    public void LocalMessage_JournalIdentityAvailableWithoutAnyEngine()
    {
        var node = Local();
        var eventId = node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        Assert.IsFalse(string.IsNullOrWhiteSpace(eventId), "the local journal must never silently no-op");
    }

    [TestMethod]
    public void LocalEmit_InvalidBody_RollsBackEventAndDomain()
    {
        var node = Local();
        var bad = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob", "bob", "v1", "{ not json");
        Assert.ThrowsExactly<ReplicationProjectionException>(
            () => node.Journal.EmitLocal(bad, new[] { "alice", "bob" }));
        Assert.AreEqual(0, EventCount(node.Db));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM chat_lines;"));
        Assert.AreEqual(0, PendingOutbox(node.Db));
    }

    [TestMethod]
    public void LocalEmit_InvalidBody_LeavesNoSequenceHole()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        var bad = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob", "bob", "v2", "{ not json");
        Assert.ThrowsExactly<ReplicationProjectionException>(
            () => node.Journal.EmitLocal(bad, new[] { "alice", "bob" }));
        node.Journal.EmitLocal(LineEnv("bob", Line("l-2"), "v3"), new[] { "alice", "bob" });
        Assert.AreEqual(2, Scalar(node.Db, "SELECT COUNT(*) FROM replication_events;"));
        Assert.AreEqual(2, Scalar(node.Db, "SELECT MAX(seq) FROM replication_events;"));
    }

    [TestMethod]
    public void LocalEmit_UnknownKind_Throws_AndWritesNothing()
    {
        var node = Local();
        var bad = new ReplicationPayloadCodec.DomainEnvelope(
            "not-a-kind", ReplicationPayloadCodec.DomainAction.Upsert, "x", null, "v1", "{}");
        Assert.ThrowsExactly<ArgumentException>(
            () => node.Journal.EmitLocal(bad, new[] { "alice" }));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    [TestMethod]
    public void LocalEmit_EmptyEntityId_Throws()
    {
        var node = Local();
        var bad = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
            "", null, "v1", "{}");
        Assert.ThrowsExactly<ArgumentException>(
            () => node.Journal.EmitLocal(bad, new[] { "alice" }));
    }

    [TestMethod]
    public void LocalEmit_TwoLines_AppendsBothActualRows()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1"), "v1"), new[] { "alice", "bob" });
        node.Journal.EmitLocal(LineEnv("bob", Line("l-2"), "v2"), new[] { "alice", "bob" });
        Assert.AreEqual(2, Scalar(node.Db, "SELECT COUNT(*) FROM chat_lines WHERE handle = 'bob';"));
    }

    [TestMethod]
    public void LocalEmit_SameLineTwice_DoesNotDuplicateTheActualRow()
    {
        var node = Local();
        var line = Line("l-1");
        node.Journal.EmitLocal(LineEnv("bob", line, "v1"), new[] { "alice", "bob" });
        node.Journal.EmitLocal(LineEnv("bob", line, "v2"), new[] { "alice", "bob" });
        Assert.AreEqual(1, ChatLineCount(node.Db, "bob", "l-1"));
    }

    [TestMethod]
    public void LocalTopicLine_WritesActualOwnChatRow()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("t-1", Line("l-1", "me note"), "v1", ReplicationOpKinds.Topic),
            new[] { "alice" });
        Assert.AreEqual(1, OwnChatCount(node.Db, "t-1", "l-1"));
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM own_threads WHERE id = 't-1';"));
    }

    [TestMethod]
    public void LocalTopicLine_DuplicateDoesNotDuplicateActualRow()
    {
        var node = Local();
        var line = Line("l-1");
        node.Journal.EmitLocal(LineEnv("t-1", line, "v1", ReplicationOpKinds.Topic), new[] { "alice" });
        node.Journal.EmitLocal(LineEnv("t-1", line, "v2", ReplicationOpKinds.Topic), new[] { "alice" });
        Assert.AreEqual(1, OwnChatCount(node.Db, "t-1", "l-1"));
    }

    // =======================================================================
    // 2. LOCAL-ONLY (no audience) still writes the actual rows
    // =======================================================================

    [TestMethod]
    public void LocalOnlyChange_NoTargets_StillWritesActualRow_AndNoEvent()
    {
        var node = NewJournal("solo", "dev-solo");
        var applied = node.Db.ApplyLocalDomainChange(LineEnv("bob", Line("l-1")), deviceIsDesktop: true);
        Assert.IsTrue(applied);
        Assert.AreEqual(1, ChatLineCount(node.Db, "bob", "l-1"));
        Assert.AreEqual(0, EventCount(node.Db));
        Assert.AreEqual(0, PendingOutbox(node.Db));
    }

    [TestMethod]
    public void LocalOnlyChange_InvalidBody_RollsBackActualDomain()
    {
        var node = NewJournal("solo", "dev-solo2");
        var bad = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
            "bob", "bob", "v1", "{ nope");
        Assert.ThrowsExactly<ReplicationProjectionException>(
            () => node.Db.ApplyLocalDomainChange(bad, deviceIsDesktop: true));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM chat_lines;"));
    }

    // =======================================================================
    // 3. Topics, clears and deletes
    // =======================================================================

    private static ReplicationPayloadCodec.DomainEnvelope TopicUpsert(
        string id, string title, string causal = "v1", bool pinned = false)
        => new(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert, id, id, causal,
            Body(new
            {
                Id = id,
                Title = title,
                CreatedAt = DateTimeOffset.UtcNow,
                SortOrder = 0,
                ExecutionDeviceId = (string?)null,
                ExecutionDeviceName = (string?)null,
                ExecutionDevicePlatform = (string?)null,
                LastActivityAt = (DateTimeOffset?)DateTimeOffset.UtcNow,
                IsPinned = pinned,
                ExecutionAt = (DateTimeOffset?)null,
                ExecutionRunId = (string?)null
            }));

    [TestMethod]
    public void TopicUpsert_WritesActualOwnThreadRowWithTitle()
    {
        var node = Local();
        node.Journal.EmitLocal(TopicUpsert("t-9", "Planning"), new[] { "alice" });
        Assert.AreEqual("Planning", Text(node.Db, "SELECT title FROM own_threads WHERE id = 't-9';"));
    }

    [TestMethod]
    public void TopicUpsert_LaterVersionUpdatesTheActualTitle()
    {
        var node = Local();
        node.Journal.EmitLocal(TopicUpsert("t-9", "First", "v1"), new[] { "alice" });
        node.Journal.EmitLocal(TopicUpsert("t-9", "Second", "v2"), new[] { "alice" });
        Assert.AreEqual("Second", Text(node.Db, "SELECT title FROM own_threads WHERE id = 't-9';"));
    }

    [TestMethod]
    public void TopicClear_EmptiesLinesButKeepsTheThread()
    {
        var node = Local();
        node.Journal.EmitLocal(TopicUpsert("t-9", "Keep"), new[] { "alice" });
        node.Journal.EmitLocal(LineEnv("t-9", Line("l-1"), "v2", ReplicationOpKinds.Topic), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Delete, "t-9", "t-9", "v3",
            "{\"clear\":true}"), new[] { "alice" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM own_chat WHERE thread_id = 't-9';"));
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM own_threads WHERE id = 't-9';"));
    }

    [TestMethod]
    public void TopicClear_BlocksStaleLinesButAllowsNewerLines()
    {
        var node = Local();
        node.Journal.EmitLocal(TopicUpsert("t-9", "Keep"), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Delete, "t-9", "t-9", "v3",
            "{\"clear\":true}"), new[] { "alice" });

        node.Journal.EmitLocal(
            LineEnv("t-9", Line("stale"), "v2", ReplicationOpKinds.Topic),
            new[] { "alice" });
        node.Journal.EmitLocal(
            LineEnv("t-9", Line("fresh"), "v4", ReplicationOpKinds.Topic),
            new[] { "alice" });

        Assert.AreEqual(0, OwnChatCount(node.Db, "t-9", "stale"));
        Assert.AreEqual(1, OwnChatCount(node.Db, "t-9", "fresh"));
    }

    [TestMethod]
    public void TopicDelete_RemovesTheActualThreadRow()
    {
        var node = Local();
        node.Journal.EmitLocal(TopicUpsert("t-9", "Gone"), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Delete, "t-9", "t-9", "v3",
            "{\"clear\":false}"), new[] { "alice" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM own_threads WHERE id = 't-9';"));
    }

    [TestMethod]
    public void ConversationClear_EmptiesLinesButKeepsTheConversation()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Delete, "bob", "bob", "v2",
            "{\"clear\":true}"), new[] { "alice", "bob" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM chat_lines WHERE handle = 'bob';"));
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM conversations WHERE handle = 'bob';"));
    }

    [TestMethod]
    public void ConversationClear_BlocksStaleLinesButAllowsNewerLines()
    {
        var node = Local();
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Delete, "bob", "bob", "v3",
            "{\"clear\":true}"), new[] { "alice", "bob" });

        node.Journal.EmitLocal(LineEnv("bob", Line("stale"), "v2"), new[] { "alice", "bob" });
        node.Journal.EmitLocal(LineEnv("bob", Line("fresh"), "v4"), new[] { "alice", "bob" });

        Assert.AreEqual(0, ChatLineCount(node.Db, "bob", "stale"));
        Assert.AreEqual(1, ChatLineCount(node.Db, "bob", "fresh"));
    }

    [TestMethod]
    public void ConversationDelete_RemovesTheConversationAndItsLines()
    {
        var node = Local();
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1")), new[] { "alice", "bob" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Delete, "bob", "bob", "v2",
            "{\"clear\":false}"), new[] { "alice", "bob" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM conversations WHERE handle = 'bob';"));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM chat_lines WHERE handle = 'bob';"));
    }

    // =======================================================================
    // 4. Contacts and circles land in the ACTUAL profile blob
    // =======================================================================

    private static ReplicationPayloadCodec.DomainEnvelope ContactUpsert(
        string handle, string display, string causal = "v1", params string[] circles)
        => new(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert, handle, null, causal,
            Body(new ContactProjection(handle, display, circles.ToList(), true,
                Array.Empty<string>(), false, false, false)));

    [TestMethod]
    public void ContactUpsert_WritesIntoTheActualProfileBlob()
    {
        var node = Local();
        node.Journal.EmitLocal(ContactUpsert("bob", "Bob"), new[] { "alice" });
        var profile = ProfileBlob(node.Db);
        Assert.IsNotNull(profile);
        Assert.IsTrue(profile!.Contacts.Any(c => c.Handle == "bob" && c.DisplayName == "Bob"));
    }

    [TestMethod]
    public void ContactUpsert_LaterVersionUpdatesDisplayName()
    {
        var node = Local();
        node.Journal.EmitLocal(ContactUpsert("bob", "Bob", "v1"), new[] { "alice" });
        node.Journal.EmitLocal(ContactUpsert("bob", "Bobby", "v2"), new[] { "alice" });
        Assert.AreEqual("Bobby", ProfileBlob(node.Db)!.Contacts.Single(c => c.Handle == "bob").DisplayName);
    }

    [TestMethod]
    public void ContactDelete_RemovesFromTheActualProfileBlob()
    {
        var node = Local();
        node.Journal.EmitLocal(ContactUpsert("bob", "Bob"), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Delete, "bob", null, "v2", "{}"),
            new[] { "alice" });
        Assert.IsFalse(ProfileBlob(node.Db)!.Contacts.Any(c => c.Handle == "bob"));
    }

    [TestMethod]
    public void CircleUpsert_WritesIntoTheActualProfileBlob()
    {
        var node = Local();
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            ProfileProjection.CircleEntityId("Work"), null, "v1",
            Body(new CircleProjection("Work", true, null))), new[] { "alice" });
        Assert.IsTrue(ProfileBlob(node.Db)!.Circles.Any(c => c.Name == "Work"));
    }

    [TestMethod]
    public void CircleRename_RetargetsContactMembershipInTheActualBlob()
    {
        var node = Local();
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            ProfileProjection.CircleEntityId("Work"), null, "v1",
            Body(new CircleProjection("Work", false, null))), new[] { "alice" });
        node.Journal.EmitLocal(ContactUpsert("bob", "Bob", "v2", "Work"), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            ProfileProjection.CircleEntityId("Office"), null, "v3",
            Body(new CircleProjection("Office", false,
                new[] { new CircleRenameProjection("Work", "v3") }))), new[] { "alice" });

        var profile = ProfileBlob(node.Db)!;
        Assert.IsTrue(profile.Circles.Any(c => c.Name == "Office"));
        Assert.IsFalse(profile.Circles.Any(c => c.Name == "Work"));
        Assert.IsTrue(profile.Contacts.Single(c => c.Handle == "bob").Circles.Contains("Office"));
    }

    [TestMethod]
    public void CircleDelete_RemovesCircleAndItsMembershipReferences()
    {
        var node = Local();
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            ProfileProjection.CircleEntityId("Work"), null, "v1",
            Body(new CircleProjection("Work", false, null))), new[] { "alice" });
        node.Journal.EmitLocal(ContactUpsert("bob", "Bob", "v2", "Work"), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Delete,
            ProfileProjection.CircleEntityId("Work"), null, "v3", "{}"), new[] { "alice" });

        var profile = ProfileBlob(node.Db)!;
        Assert.IsFalse(profile.Circles.Any(c => c.Name == "Work"));
        Assert.IsFalse(profile.Contacts.Single(c => c.Handle == "bob").Circles.Contains("Work"));
    }

    // =======================================================================
    // 5. Memories
    // =======================================================================

    private static ReplicationPayloadCodec.DomainEnvelope MemoryUpsert(
        string id, string text, string causal = "v1")
    {
        var item = new MemoryItem
        {
            Id = id,
            Title = "note",
            Content = text,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return new(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert, id, null, causal,
            Body(MemoryPolicy.ToSync(item)));
    }

    [TestMethod]
    public void MemoryUpsert_WritesTheActualMemoriesRow()
    {
        var node = Local();
        node.Journal.EmitLocal(MemoryUpsert("m-1", "remember this"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM memories WHERE id = 'm-1';"));
    }

    [TestMethod]
    public void MemoryUpsert_LaterVersionUpdatesActualText()
    {
        var node = Local();
        node.Journal.EmitLocal(MemoryUpsert("m-1", "first", "v1"), new[] { "alice" });
        node.Journal.EmitLocal(MemoryUpsert("m-1", "second", "v2"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM memories WHERE id = 'm-1';"));
    }

    [TestMethod]
    public void MemoryDelete_RemovesTheActualRow()
    {
        var node = Local();
        node.Journal.EmitLocal(MemoryUpsert("m-1", "gone"), new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Delete, "m-1", null, "v2", "{}"),
            new[] { "alice" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM memories WHERE id = 'm-1';"));
    }

    [TestMethod]
    public void MemoryUpsert_UnmaterialisableProjection_FailsClosed()
    {
        var node = Local();
        var bad = new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert, "m-1", null, "v1",
            "{\"id\":\"m-1\"}");
        // The memory policy rejects the shape. Keeping only a convergence record would leave this
        // device permanently missing a supported row, so the whole transaction fails closed.
        Assert.ThrowsException<ReplicationProjectionException>(() => node.Journal.EmitLocal(bad, new[] { "alice" }));
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM memories;"));
        Assert.AreEqual(0, EventCount(node.Db));
        Assert.IsNull(node.Db.GetReplicatedEntity(ReplicationOpKinds.Memory, "m-1"));
    }

    // =======================================================================
    // 6. Assets
    // =======================================================================

    private static ReplicationPayloadCodec.DomainEnvelope AssetUpsert(
        AssetKind kind, string id, string name, byte[] content, string causal = "v1")
        => new(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            ReplicationDomainMaterializer.AssetEntityId(kind, id), null, causal,
            Body(new
            {
                Kind = kind.ToString(),
                Id = id,
                Name = name,
                MetadataJson = "{}",
                ContentMime = "text/plain",
                ContentB64 = Convert.ToBase64String(content),
                ContentHash = (string?)null,
                Version = 1,
                SourceDeviceId = "dev-a",
                UpdatedAt = DateTimeOffset.UtcNow
            }));

    [TestMethod]
    public void AssetUpsert_WritesTheActualAssetsRow()
    {
        var node = Local();
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", "Skill One", Encoding.UTF8.GetBytes("body")),
            new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-1';"));
    }

    [TestMethod]
    public void AssetUpsert_WritesTheActualAssetContentBody()
    {
        var node = Local();
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", "Skill One", Encoding.UTF8.GetBytes("the body")),
            new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM asset_content WHERE id = 's-1';"));
    }

    [TestMethod]
    public void AssetUpsert_StoresTheActualName()
    {
        var node = Local();
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Knowledge, "k-1", "Knowledge One", Encoding.UTF8.GetBytes("x")),
            new[] { "alice" });
        Assert.AreEqual("Knowledge One", Text(node.Db, "SELECT name FROM assets WHERE id = 'k-1';"));
    }

    [TestMethod]
    public void AssetDelete_TombstonesTheActualRowAndDropsTheBody()
    {
        var node = Local();
        node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", "Skill One", Encoding.UTF8.GetBytes("body")),
            new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetDelete,
            ReplicationDomainMaterializer.AssetEntityId(AssetKind.Skill, "s-1"), null, "v2", "{}"),
            new[] { "alice" });
        Assert.AreEqual(0, Scalar(node.Db, "SELECT COUNT(*) FROM asset_content WHERE id = 's-1';"));
        Assert.AreEqual(1, Scalar(node.Db, "SELECT is_deleted FROM assets WHERE id = 's-1';"));
    }

    [TestMethod]
    public void AssetUpsert_OnMobile_IsRefusedRatherThanSilentlyDropped()
    {
        var node = NewJournal("alice", "dev-mob", desktop: false);
        AddSibling("alice", "dev-mob-sib");
        Assert.ThrowsExactly<InvalidOperationException>(() => node.Journal.EmitLocal(
            AssetUpsert(AssetKind.Skill, "s-1", "Skill One", Encoding.UTF8.GetBytes("body")),
            new[] { "alice" }));
        Assert.AreEqual(0, EventCount(node.Db));
    }

    // =======================================================================
    // 7. Ask-user
    // =======================================================================

    private static ReplicationPayloadCodec.DomainEnvelope AskPrompt(string id, string question, string causal = "v1")
        => new(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt, id, null, causal,
            Body(new
            {
                PromptId = id,
                ThreadId = "t-1",
                RunId = "r-1",
                Question = question,
                Options = new[]
                {
                    new { Id = "yes", Title = "Yes", Description = (string?)null },
                    new { Id = "no", Title = "No", Description = (string?)null }
                },
                RecommendedIndex = (int?)0,
                OriginDeviceId = "dev-a",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = (DateTimeOffset?)null,
                Revision = 1,
                Version = 1,
                Resolved = false
            }));

    private static ReplicationPayloadCodec.DomainEnvelope AskResolve(string id, string selection, string causal = "v2")
        => new(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve, id, null, causal,
            Body(new
            {
                PromptId = id,
                State = "answered",
                Selection = selection,
                ResolutionDeviceId = "dev-a",
                ResolvedAt = DateTimeOffset.UtcNow,
                Resolved = true
            }));

    [TestMethod]
    public void AskUserPrompt_WritesTheActualPromptRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", "Proceed?"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskUserPrompt_StoresTheActualQuestion()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", "Proceed?"), new[] { "alice" });
        Assert.AreEqual("Proceed?", Text(node.Db, "SELECT question FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskUserResolve_MarksTheActualPromptResolved()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", "Proceed?"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "yes"), new[] { "alice" });
        Assert.AreEqual("answered", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskUserResolve_SecondResolutionDoesNotOverwriteTheFirst()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", "Proceed?"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "yes", "v2"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "no", "v3"), new[] { "alice" });
        Assert.AreEqual("yes", Text(node.Db, "SELECT selection FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    [TestMethod]
    public void AskUserPrompt_AfterResolution_DoesNotDowngradeTheActualRow()
    {
        var node = Local();
        node.Journal.EmitLocal(AskPrompt("p-1", "Proceed?", "v1"), new[] { "alice" });
        node.Journal.EmitLocal(AskResolve("p-1", "yes", "v2"), new[] { "alice" });
        node.Journal.EmitLocal(AskPrompt("p-1", "Proceed?", "v3"), new[] { "alice" });
        Assert.AreEqual("answered", Text(node.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-1';"));
    }

    // =======================================================================
    // 8. Read watermark
    // =======================================================================

    private static ReplicationPayloadCodec.DomainEnvelope Watermark(string conv, long version, string causal)
        => new(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            conv, conv, causal,
            Body(new ReadWatermarkPayload(conv, "alice", "evt-" + version, "dev-a", version,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

    [TestMethod]
    public void ReadWatermark_WritesTheActualWatermarkRow()
    {
        var node = Local();
        node.Journal.EmitLocal(Watermark("bob", 1, "v1"), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db,
            "SELECT COUNT(*) FROM replication_read_watermarks WHERE conversation_id = 'bob';"));
    }

    [TestMethod]
    public void ReadWatermark_AdvancesMonotonically()
    {
        var node = Local();
        node.Journal.EmitLocal(Watermark("bob", 5, "v1"), new[] { "alice" });
        node.Journal.EmitLocal(Watermark("bob", 1, "v2"), new[] { "alice" });
        Assert.AreEqual(5, Scalar(node.Db,
            "SELECT version FROM replication_read_watermarks WHERE conversation_id = 'bob';"));
    }

    // =======================================================================
    // 9. Skill packages
    // =======================================================================

    [TestMethod]
    public void PackageChunk_StagesTheActualBlobOnceComplete()
    {
        var node = Local();
        var content = Encoding.UTF8.GetBytes("package-bytes");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
            "attachment/pkg-1", null, "v1",
            Body(new
            {
                PackageId = "attachment/pkg-1",
                ChunkIndex = 0,
                ChunkCount = 1,
                TotalBytes = (long)content.Length,
                ChunkB64 = Convert.ToBase64String(content),
                ContentHash = hash,
                Name = "pkg.bin",
                MimeType = "application/octet-stream",
                RunId = "r-1"
            })), new[] { "alice" });
        Assert.AreEqual(1, Scalar(node.Db,
            "SELECT COUNT(*) FROM replication_package_chunks WHERE package_id = 'attachment/pkg-1';"));
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM replicated_attachments WHERE attachment_id = 'pkg-1';"));
    }

    [TestMethod]
    public void PackageChunk_TwoChunksAssembleIntoOneStagedBlob()
    {
        var node = Local();
        var a = Encoding.UTF8.GetBytes("first-");
        var b = Encoding.UTF8.GetBytes("second");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData([.. a, .. b])).ToLowerInvariant();
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer, "attachment/pkg-2", null, "v1",
            Body(new { PackageId = "attachment/pkg-2", ChunkIndex = 0, ChunkCount = 2, TotalBytes = (long)(a.Length + b.Length), ChunkB64 = Convert.ToBase64String(a), ContentHash = hash, Name = "pkg.bin", MimeType = "application/octet-stream", RunId = "r-1" })),
            new[] { "alice" });
        node.Journal.EmitLocal(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer, "attachment/pkg-2", null, "v2",
            Body(new { PackageId = "attachment/pkg-2", ChunkIndex = 1, ChunkCount = 2, TotalBytes = (long)(a.Length + b.Length), ChunkB64 = Convert.ToBase64String(b), ContentHash = hash, Name = "pkg.bin", MimeType = "application/octet-stream", RunId = "r-1" })),
            new[] { "alice" });
        Assert.AreEqual(2, Scalar(node.Db,
            "SELECT COUNT(*) FROM replication_package_chunks WHERE package_id = 'attachment/pkg-2';"));
        Assert.AreEqual(1, Scalar(node.Db, "SELECT COUNT(*) FROM replicated_attachments WHERE attachment_id = 'pkg-2';"));
    }

    // =======================================================================
    // 10. INBOUND: two nodes, real tables on the receiver
    // =======================================================================

    private async Task<(ReplicationNode Sender, ReplicationNode Receiver)> PairAsync()
    {
        var a = NewNode("alice", "dev-a1");
        var b = NewNode("alice", "dev-a2");
        UseProjectingApplier(b);
        await EstablishAsync(a, b);
        return (a, b);
    }

    [TestMethod]
    public async Task Inbound_PersonMessage_WritesTheReceiversActualChatLine()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(LineEnv("bob", Line("l-1", "from a")), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(1, ChatLineCount(b.Db, "bob", "l-1"));
    }

    [TestMethod]
    public async Task Inbound_PersonMessage_IsVisibleWithTheActualText()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(LineEnv("bob", Line("l-1", "visible text")), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual("visible text", Text(b.Db, "SELECT text FROM chat_lines WHERE line_id = 'l-1';"));
    }

    [TestMethod]
    public async Task Inbound_DuplicateEvent_DoesNotDuplicateTheActualLine()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(LineEnv("bob", Line("l-1")), new[] { "alice" });
        await Fabric.DrainAsync();
        await a.Engine.EmitLocalAsync(LineEnv("bob", Line("l-1"), "v2"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(1, ChatLineCount(b.Db, "bob", "l-1"));
    }

    [TestMethod]
    public async Task Inbound_TopicLine_WritesTheReceiversActualOwnChatRow()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(
            LineEnv("t-1", Line("l-1", "me note"), "v1", ReplicationOpKinds.Topic), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(1, OwnChatCount(b.Db, "t-1", "l-1"));
    }

    [TestMethod]
    public async Task Inbound_TopicUpsert_WritesTheReceiversActualThreadTitle()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(TopicUpsert("t-7", "Replicated"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual("Replicated", Text(b.Db, "SELECT title FROM own_threads WHERE id = 't-7';"));
    }

    [TestMethod]
    public async Task Inbound_Contact_WritesTheReceiversActualProfileBlob()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(ContactUpsert("carol", "Carol"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.IsTrue(ProfileBlob(b.Db)!.Contacts.Any(c => c.Handle == "carol"));
    }

    [TestMethod]
    public async Task Inbound_Memory_WritesTheReceiversActualMemoriesRow()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(MemoryUpsert("m-9", "shared"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(1, Scalar(b.Db, "SELECT COUNT(*) FROM memories WHERE id = 'm-9';"));
    }

    [TestMethod]
    public async Task Inbound_Asset_WritesTheReceiversActualAssetAndBody()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(
            AssetUpsert(AssetKind.Skill, "s-9", "Shared Skill", Encoding.UTF8.GetBytes("body")),
            new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(1, Scalar(b.Db, "SELECT COUNT(*) FROM assets WHERE id = 's-9';"));
        Assert.AreEqual(1, Scalar(b.Db, "SELECT COUNT(*) FROM asset_content WHERE id = 's-9';"));
    }

    [TestMethod]
    public async Task Inbound_AskUserPromptAndResolution_ReachTheReceiversActualTable()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(AskPrompt("p-9", "Ship it?"), new[] { "alice" });
        await Fabric.DrainAsync();
        await a.Engine.EmitLocalAsync(AskResolve("p-9", "yes"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual("answered", Text(b.Db, "SELECT state FROM ask_user_prompts WHERE prompt_id = 'p-9';"));
    }

    [TestMethod]
    public async Task Inbound_ReadWatermark_ReachesTheReceiversActualTable()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(Watermark("bob", 42, "v1"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(42, Scalar(b.Db,
            "SELECT version FROM replication_read_watermarks WHERE conversation_id = 'bob';"));
    }

    [TestMethod]
    public async Task Inbound_ConversationDelete_RemovesTheReceiversActualConversation()
    {
        var (a, b) = await PairAsync();
        await a.Engine.EmitLocalAsync(LineEnv("bob", Line("l-1")), new[] { "alice" });
        await Fabric.DrainAsync();
        await a.Engine.EmitLocalAsync(new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Delete, "bob", "bob", "v2",
            "{\"clear\":false}"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(0, Scalar(b.Db, "SELECT COUNT(*) FROM conversations WHERE handle = 'bob';"));
    }

    [TestMethod]
    public async Task Inbound_AfterCommit_IsNotifiedWithTheCommittedEnvelope()
    {
        var a = NewNode("alice", "dev-p1");
        var b = NewNode("alice", "dev-p2");
        var seen = new List<ReplicationPayloadCodec.DomainEnvelope>();
        UseProjectingApplier(b);
        b.Applier.OnAfterCommit = (_, env) => { lock (seen) seen.Add(env); };
        await EstablishAsync(a, b);
        await a.Engine.EmitLocalAsync(LineEnv("bob", Line("l-1", "post commit")), new[] { "alice" });
        await Fabric.DrainAsync();
        lock (seen)
        {
            Assert.AreEqual(1, seen.Count, "post-commit must fire exactly once per committed event");
            Assert.AreEqual("bob", seen[0].EntityId);
        }
    }

    [TestMethod]
    public async Task Inbound_CausalLoser_DoesNotRunPostCommitMutation()
    {
        var a = NewNode("alice", "dev-loser-a");
        var b = NewNode("alice", "dev-loser-b");
        b.Applier.ApplyResult = false;
        var postCommit = 0;
        b.Applier.OnAfterCommit = (_, _) => Interlocked.Increment(ref postCommit);

        await a.Engine.EmitLocalAsync(
            ContactUpsert("contact-loser", "Older"),
            new[] { "alice" });
        await EstablishAsync(a, b);
        await Fabric.DrainAsync();

        Assert.AreEqual(1, b.Applier.Count, "The durable arbitration evaluated the event.");
        Assert.AreEqual(0, postCommit, "A causal loser must not mutate live UI state.");
    }

    [TestMethod]
    public async Task Inbound_UndecodableAuthenticatedPayload_HaltsWithoutCursorAdvance()
    {
        var a = NewNode("alice", "dev-bad-a");
        var b = NewNode("alice", "dev-bad-b");
        var ciphertext = ReplicationPayloadCodec.Encrypt(
            "not a domain envelope",
            new[] { a.Keys.PublicB64, b.Keys.PublicB64 });
        a.Db.AllocateAndAppendLocalEvent(
            a.Device,
            (epoch, seq) => OnlineReplicationProtocol.CreateEvent(
                a.Device,
                epoch,
                seq,
                a.Handle,
                Roster.AuthGeneration(a.Handle),
                ReplicationOpKinds.Message,
                "bad-envelope",
                "conversation",
                "v1",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ciphertext,
                a.Keys.PrivateB64),
            new[] { "alice" },
            domainApply: null);

        await EstablishAsync(a, b);
        await Fabric.DrainAsync();

        Assert.IsTrue(b.Engine.IsHalted(a.Device));
        Assert.IsNull(b.Db.GetCursor(a.Device));
    }

    [TestMethod]
    public async Task Inbound_DuplicateEvent_DoesNotFirePostCommitTwice()
    {
        var a = NewNode("alice", "dev-p3");
        var b = NewNode("alice", "dev-p4");
        var count = 0;
        UseProjectingApplier(b);
        b.Applier.OnAfterCommit = (_, _) => Interlocked.Increment(ref count);
        await EstablishAsync(a, b);
        var line = Line("l-1");
        await a.Engine.EmitLocalAsync(LineEnv("bob", line, "v1"), new[] { "alice" });
        await Fabric.DrainAsync();
        await a.Engine.EmitLocalAsync(LineEnv("bob", line, "v2"), new[] { "alice" });
        await Fabric.DrainAsync();
        Assert.AreEqual(1, ChatLineCount(b.Db, "bob", "l-1"));
    }

    // =======================================================================
    // 11. POST-COMMIT in-memory materialisation
    // =======================================================================

    [TestMethod]
    public void PostCommit_AppendLine_BecomesVisibleOnTheInMemoryProfile()
    {
        var profile = new MeshProfile();
        var changed = ReplicationProfileMaterializer.Apply(profile, LineEnv("bob", Line("l-1", "in memory")));
        Assert.IsTrue(changed);
        Assert.AreEqual("in memory", profile.Conversations.Single().Lines.Single().Text);
    }

    [TestMethod]
    public void PostCommit_DuplicateLine_DoesNotDuplicateInMemory()
    {
        var profile = new MeshProfile();
        var line = Line("l-1");
        ReplicationProfileMaterializer.Apply(profile, LineEnv("bob", line, "v1"));
        var second = ReplicationProfileMaterializer.Apply(profile, LineEnv("bob", line, "v2"));
        Assert.IsFalse(second, "a duplicate must not report a change, so the UI is not notified again");
        Assert.AreEqual(1, profile.Conversations.Single().Lines.Count);
    }

    [TestMethod]
    public void PostCommit_TopicLine_BecomesVisibleOnTheInMemoryProfile()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, LineEnv("t-1", Line("l-1"), "v1", ReplicationOpKinds.Topic));
        Assert.AreEqual("l-1", profile.OwnThreads.Single().Lines.Single().Id);
    }

    [TestMethod]
    public void PostCommit_TopicUpsert_UpdatesTheInMemoryTitle()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, TopicUpsert("t-1", "Renamed"));
        Assert.AreEqual("Renamed", profile.OwnThreads.Single().Title);
    }

    [TestMethod]
    public void PostCommit_TopicClear_EmptiesLinesButKeepsTheThreadInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, LineEnv("t-1", Line("l-1"), "v1", ReplicationOpKinds.Topic));
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Delete, "t-1", "t-1", "v2",
            "{\"clear\":true}"));
        Assert.AreEqual(1, profile.OwnThreads.Count);
        Assert.AreEqual(0, profile.OwnThreads.Single().Lines.Count);
    }

    [TestMethod]
    public void PostCommit_TopicDelete_RemovesTheThreadInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, TopicUpsert("t-1", "Gone"));
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Delete, "t-1", "t-1", "v2",
            "{\"clear\":false}"));
        Assert.AreEqual(0, profile.OwnThreads.Count);
    }

    [TestMethod]
    public void PostCommit_Contact_BecomesVisibleInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, ContactUpsert("bob", "Bob"));
        Assert.AreEqual("Bob", profile.Contacts.Single().DisplayName);
    }

    [TestMethod]
    public void PostCommit_ContactDelete_RemovesItInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, ContactUpsert("bob", "Bob"));
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Delete, "bob", null, "v2", "{}"));
        Assert.AreEqual(0, profile.Contacts.Count);
    }

    [TestMethod]
    public void PostCommit_CircleRename_RetargetsInMemoryMembership()
    {
        var profile = new MeshProfile();
        profile.Circles.Add(new Circle { Name = "Work" });
        profile.Contacts.Add(new Contact { Handle = "bob", DisplayName = "Bob", Circles = new List<string> { "Work" } });
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            ProfileProjection.CircleEntityId("Office"), null, "v1",
            Body(new CircleProjection("Office", false, new[] { new CircleRenameProjection("Work", "v1") }))));
        Assert.IsTrue(profile.Circles.Any(c => c.Name == "Office"));
        Assert.IsFalse(profile.Circles.Any(c => c.Name == "Work"));
        CollectionAssert.Contains(profile.Contacts.Single().Circles, "Office");
    }

    [TestMethod]
    public void PostCommit_CircleDelete_RemovesMembershipInMemory()
    {
        var profile = new MeshProfile();
        profile.Circles.Add(new Circle { Name = "Work" });
        profile.Contacts.Add(new Contact { Handle = "bob", Circles = new List<string> { "Work" } });
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Delete,
            ProfileProjection.CircleEntityId("Work"), null, "v2", "{}"));
        Assert.IsFalse(profile.Circles.Any(c => c.Name == "Work"));
        Assert.AreEqual(0, profile.Contacts.Single().Circles.Count);
    }

    [TestMethod]
    public void PostCommit_Memory_BecomesVisibleInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, MemoryUpsert("m-1", "remember"));
        Assert.AreEqual("m-1", profile.Memories.Single().Id);
    }

    [TestMethod]
    public void PostCommit_MemoryDelete_RemovesItInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, MemoryUpsert("m-1", "remember"));
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Delete, "m-1", null, "v2", "{}"));
        Assert.AreEqual(0, profile.Memories.Count);
    }

    [TestMethod]
    public void PostCommit_ConversationDelete_RemovesItInMemory()
    {
        var profile = new MeshProfile();
        ReplicationProfileMaterializer.Apply(profile, LineEnv("bob", Line("l-1")));
        ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Delete, "bob", "bob", "v2",
            "{\"clear\":false}"));
        Assert.AreEqual(0, profile.Conversations.Count);
    }

    [TestMethod]
    public void PostCommit_AssetEnvelope_ReportsNoInMemoryProfileChange()
    {
        var profile = new MeshProfile();
        var changed = ReplicationProfileMaterializer.Apply(profile,
            AssetUpsert(AssetKind.Skill, "s-1", "Skill", Encoding.UTF8.GetBytes("x")));
        Assert.IsFalse(changed, "asset bodies are read from the database, not the in-memory profile");
    }

    [TestMethod]
    public void PostCommit_MalformedBody_ReportsNoChangeAndDoesNotThrow()
    {
        var profile = new MeshProfile();
        var changed = ReplicationProfileMaterializer.Apply(profile, new ReplicationPayloadCodec.DomainEnvelope(
            ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine, "bob", "bob", "v1",
            "{ not json"));
        Assert.IsFalse(changed);
        Assert.AreEqual(0, profile.Conversations.Count);
    }

    // =======================================================================
    // 12. Durability across a restart
    // =======================================================================

    [TestMethod]
    public void LocalChange_SurvivesAReopenOfTheDatabase()
    {
        var node = Local("alice", "dev-reopen");
        node.Journal.EmitLocal(LineEnv("bob", Line("l-1", "durable")), new[] { "alice", "bob" });
        Assert.AreEqual(1, ChatLineCount(node.Db, "bob", "l-1"));
        Assert.AreEqual(1, EventCount(node.Db));
        Assert.IsTrue(PendingOutbox(node.Db) > 0);
    }

    [TestMethod]
    public void LocalChange_ActualRowAndEventAgreeOnCount()
    {
        var node = Local("alice", "dev-agree");
        for (var i = 0; i < 5; i++)
            node.Journal.EmitLocal(LineEnv("bob", Line("l-" + i), "v" + i), new[] { "alice", "bob" });
        Assert.AreEqual(5, Scalar(node.Db, "SELECT COUNT(*) FROM chat_lines WHERE handle = 'bob';"));
        Assert.AreEqual(5, EventCount(node.Db));
    }
}


