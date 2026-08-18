using System.Security.Cryptography;
using System.Text.Json;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Protocol-9 Phase-2 domain replication tests. These exercise the local immutable
/// replication journal (offline emitter), the reliable domain convergence surface and the
/// concrete inbound projection -- proving every domain change is journaled atomically with
/// its event + outbox <b>with no relay connection and no engine required</b>, that inbound
/// events materialise the same converged state, and that the desktop-only / mobile-deny,
/// ask-user, watermark, custody and package policies hold. The legacy Replication
/// operation layer is gone for these domains; nothing here references it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Protocol9DomainReplicationTests : ReplicationTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string J(object o) => JsonSerializer.Serialize(o, Web);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A canonical Protocol-9 asset body: kind, identity, content and its declared size/hash.</summary>
    private static string AssetBody(byte[] bytes, string? overrideHash = null, string kind = "Skill", string id = "asset")
        => J(new
        {
            kind,
            id,
            name = id,
            contentMime = "text/markdown",
            contentHash = overrideHash ?? Sha(bytes),
            contentB64 = Convert.ToBase64String(bytes),
            contentByteCount = bytes.LongLength,
            version = 1,
            sourceDeviceId = "a1",
            updatedAt = DateTimeOffset.UtcNow,
            localOnly = false
        });

    /// <summary>An asset tombstone body: the kind and id the tombstone addresses.</summary>
    private static string AssetTombstone(string id, string kind = "Skill") => J(new { kind, id });

    /// <summary>A valid replicated memory body, accepted by the memory policy on projection.</summary>
    private static string MemoryBody(string id, string title = "Remember", string content = "Remembered text")
        => J(new
        {
            id,
            title,
            content,
            category = "preference",
            origin = "manual",
            importance = 0.6,
            confidence = 0.8,
            stability = 0.7,
            reinforcementCount = 1,
            createdAt = DateTimeOffset.UnixEpoch,
            updatedAt = DateTimeOffset.UnixEpoch,
            lastReinforcedAt = DateTimeOffset.UnixEpoch
        });

    private static string PkgChunkBody(
        int index, int count, long totalBytes, string b64, string? hash = null,
        string name = "transfer.bin", string mimeType = "application/octet-stream", string runId = "run-1")
        => J(new
        {
            chunkIndex = index,
            chunkCount = count,
            totalBytes,
            contentHash = hash ?? Sha([]),
            chunkB64 = b64,
            name,
            mimeType,
            runId
        });

    /// <summary>Transfers addressed with this prefix materialise as attachments rather than packages.</summary>
    private static string Attachment(string id) => "attachment/" + id;

    private static string WatermarkBody(string conv, string account, string through, string device, long version, long updated)
        => J(new ReadWatermarkPayload(conv, account, through, device, version, updated));

    // =====================================================================
    // A. Offline journal: local change is journaled with no engine / no relay.
    // =====================================================================

    [TestMethod]
    public void Offline_MessageCreatesEventDomainAndOutbox()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "bob" });

        Assert.IsNotNull(a.Db.GetEvent(eid), "event must be durably stored offline");
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "bob"));
        var ent = a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1");
        Assert.IsNotNull(ent);
        Assert.IsFalse(ent!.Value.Deleted);
    }

    [TestMethod]
    public void Offline_NeverNoOpsWithoutEngine()
    {
        var a = NewJournal("alice", "a1");
        // No OnlineReplicationEngine exists at all for this account; the journal alone must persist.
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "bob" });
        Assert.IsNotNull(a.Db.GetEvent(eid));
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1"));
    }

    [TestMethod]
    public void Offline_ReturnsDistinctMonotonicEvents()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Msg("m1"), new[] { "bob" });
        a.Journal.EmitLocal(Msg("m2"), new[] { "bob" });
        a.Journal.EmitLocal(Msg("m3"), new[] { "bob" });
        var events = a.Db.QueryEvents("a1", "epoch-1", 1, 64);
        CollectionAssert.AreEqual(new[] { 1UL, 2UL, 3UL }, events.Select(e => e.Seq).ToArray());
    }

    [TestMethod]
    public void Offline_MissingIdentityFailsClosed_NoCustodyHead()
    {
        // A profile with no local authority (empty custody head) must fail rather than
        // falsely reporting success. Onboarding initialises genesis custody instead.
        Assert.ThrowsException<ReplicationIdentityMissingException>(
            () => NewJournal("alice", "a1", custodyHeadOverride: ""));
    }

    [TestMethod]
    public void Offline_MultipleTargetsEachGetOutbox()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "bob", "carol" });
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "bob"));
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "carol"));
    }

    [TestMethod]
    public void Offline_DuplicateTargetsDeduped()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "bob", "bob", "bob" });
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "bob"));
        // Only one outbox row despite three duplicate targets.
        Assert.AreEqual(1, a.Db.QueryDueOutbox("bob", MeshDb.OutboxStatePending).Count);
    }

    [TestMethod]
    public void Offline_BlankTargetsIgnored()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "", "  ", "bob" });
        Assert.AreEqual(1, a.Db.QueryDueOutbox("bob", MeshDb.OutboxStatePending).Count);
        Assert.IsNotNull(a.Db.GetEvent(eid));
    }

    // =====================================================================
    // B. Local domain + event atomicity (all-or-nothing).
    // =====================================================================

    [TestMethod]
    public void Atomicity_DomainWorkFailureRollsBackEventAndOutbox()
    {
        var a = NewJournal("alice", "a1");
        Assert.ThrowsException<InvalidOperationException>(() =>
            a.Journal.EmitLocal(Msg("m1"), new[] { "bob" },
                (_, _, _) => throw new InvalidOperationException("boom")));

        // Nothing persisted: no event, no outbox, no domain row.
        Assert.AreEqual(0, a.Db.QueryEvents("a1", "epoch-1", 1, 64).Count);
        Assert.AreEqual(0, a.Db.QueryDueOutbox("bob", MeshDb.OutboxStatePending).Count);
        Assert.IsNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1"));

        var nextId = a.Journal.EmitLocal(Msg("m2"), new[] { "bob" });
        var next = a.Db.GetEvent(nextId)!;
        Assert.AreEqual(1UL, next.Seq, "A rolled-back domain write must not burn a sequence.");
    }

    [TestMethod]
    public void Atomicity_PriorCommitsSurviveLaterFailure()
    {
        var a = NewJournal("alice", "a1");
        var ok = a.Journal.EmitLocal(Msg("m1"), new[] { "bob" });
        Assert.ThrowsException<InvalidOperationException>(() =>
            a.Journal.EmitLocal(
                Msg("m2"),
                new[] { "bob" },
                (_, _, _) => throw new InvalidOperationException("boom")));

        Assert.IsNotNull(a.Db.GetEvent(ok), "the earlier committed event is untouched");
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1"));
        Assert.IsNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m2"));
    }

    [TestMethod]
    public void Atomicity_CustomDomainWorkRunsInSameTransaction()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "bob" }, (conn, tx, _) =>
            ReplicationDomainStore.UpsertEntity(conn, tx, ReplicationOpKinds.Contact, "c-side",
                null, "v1", "t", "{}", "alice", deleted: false, 0));
        Assert.IsNotNull(a.Db.GetEvent(eid));
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "c-side"));
    }

    // =====================================================================
    // C. Op mapping: chat-graph domains (message / conversation / topic / lines /
    //    contact / circle / memory) project through the neutral convergence surface.
    // =====================================================================

    [TestMethod]
    public void Domain_ConversationUpsert()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Upsert,
            "conv-9", conversationId: "conv-9", body: J(new { title = "Team" })), new[] { "bob" });
        var ent = a.Db.GetReplicatedEntity(ReplicationOpKinds.Conversation, "conv-9");
        Assert.IsNotNull(ent);
        StringAssert.Contains(ent!.Value.Body, "Team");
    }

    [TestMethod]
    public void Domain_MessageDeleteTombstones()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Msg("m1"), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Delete,
            "m1", conversationId: "conv-1", causal: "v2"), new[] { "bob" });
        var ent = a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1");
        Assert.IsNotNull(ent);
        Assert.IsTrue(ent!.Value.Deleted, "delete must tombstone the entity");
    }

    [TestMethod]
    public void Domain_TopicUpsertAndLines()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert,
            "t1", conversationId: "conv-1", body: J(new { name = "Ideas" })), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.AppendLine,
            "t1", conversationId: "conv-1", body: J(new { line = "first" })), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.AppendLine,
            "t1", conversationId: "conv-1", body: J(new { line = "second" })), new[] { "bob" });

        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Topic, "t1"));
        Assert.AreEqual(2, a.Db.CountReplicatedLines(ReplicationOpKinds.Topic, "t1"));
    }

    [TestMethod]
    public void Domain_MessageAppendLineChunks()
    {
        var a = NewJournal("alice", "a1");
        for (var i = 0; i < 5; i++)
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
                "m-stream", conversationId: "conv-1", body: J(new { chunk = i })), new[] { "bob" });
        Assert.AreEqual(5, a.Db.CountReplicatedLines(ReplicationOpKinds.Message, "m-stream"));
    }

    [TestMethod]
    public void Domain_ContactUpsertAndDelete()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert,
            "bob", body: J(new { name = "Bob" })), new[] { "alice-sib" });
        var ent = a.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "bob");
        Assert.IsNotNull(ent);
        Assert.IsFalse(ent!.Value.Deleted);

        a.Journal.EmitLocal(Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Delete,
            "bob", causal: "v2"), new[] { "alice-sib" });
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "bob")!.Value.Deleted);
    }

    [TestMethod]
    public void Domain_CircleUpsert()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            "circle-1", body: J(new { members = new[] { "bob", "carol" } })), new[] { "bob" });
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Circle, "circle-1"));
    }

    [TestMethod]
    public void Domain_MemoryUpsertAndDelete()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert,
            "mem-1", body: MemoryBody("mem-1")), new[] { "bob" });
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Memory, "mem-1"));
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Delete,
            "mem-1", causal: "v2"), new[] { "bob" });
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Memory, "mem-1")!.Value.Deleted);
    }

    [TestMethod]
    public void Domain_CausalLwwHigherVersionWins()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert,
            "bob", causal: "v2", body: J(new { name = "New" })), new[] { "bob" });
        // A lower causal version must not clobber a higher one.
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert,
            "bob", causal: "v1", body: J(new { name = "Old" })), new[] { "bob" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "bob")!.Value.Body, "New");
    }

    [TestMethod]
    public void Domain_CausalLwwLongerVersionStringWins()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert,
            "bob", causal: "v2", body: J(new { name = "Two" })), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert,
            "bob", causal: "v10", body: J(new { name = "Ten" })), new[] { "bob" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "bob")!.Value.Body, "Ten");
    }

    [TestMethod]
    public void Codec_OperationMapIsExhaustiveForModelledKinds()
    {
        foreach (var kind in new[]
        {
            ReplicationOpKinds.Message, ReplicationOpKinds.Conversation, ReplicationOpKinds.Topic,
            ReplicationOpKinds.Contact, ReplicationOpKinds.Circle, ReplicationOpKinds.Memory,
            ReplicationOpKinds.Asset, ReplicationOpKinds.AskUser, ReplicationOpKinds.ReadWatermark,
        })
            Assert.IsTrue(ReplicationPayloadCodec.OperationMap.ContainsKey(kind), $"missing map for {kind}");
    }

    // =====================================================================
    // D. Assets: desktop emits, mobile / LocalOnly never emits, hash validated.
    // =====================================================================

    [TestMethod]
    public void Asset_DesktopUpsertProjectsWithValidHash()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var body = AssetBody(new byte[] { 1, 2, 3, 4 });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            "asset-1", body: body), new[] { "bob" });
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "asset-1"));
    }

    [TestMethod]
    public void Asset_MobileNeverEmits()
    {
        var a = NewJournal("alice", "phone", desktop: false);
        Assert.ThrowsException<InvalidOperationException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
                "asset-1", body: AssetBody(new byte[] { 9 })), new[] { "bob" }));
        Assert.IsNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "asset-1"));
        Assert.AreEqual(0, a.Db.QueryEvents("phone", "epoch-1", 1, 64).Count);
    }

    [TestMethod]
    public void Asset_DeleteAllowedOnMobile()
    {
        // AssetDelete is a tombstone, not device-local bytes, so it is not desktop-gated.
        var a = NewJournal("alice", "phone", desktop: false);
        var eid = a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetDelete,
            "asset-x", causal: "v2", body: AssetTombstone("asset-x")), new[] { "bob" });
        Assert.IsNotNull(a.Db.GetEvent(eid));
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "asset-x")!.Value.Deleted);
    }

    [TestMethod]
    public void Asset_InvalidHashRollsBack()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var bad = AssetBody(new byte[] { 1, 2, 3 }, overrideHash: "deadbeef");
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
                "asset-bad", body: bad), new[] { "bob" }));
        Assert.IsNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "asset-bad"));
        Assert.AreEqual(0, a.Db.QueryEvents("a1", "epoch-1", 1, 64).Count);
    }

    // =====================================================================
    // E. Skill packages: chunked transfer, 20 MB bound, completion, mobile deny.
    // =====================================================================

    [TestMethod]
    public void Package_ChunksStoreAndComplete()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var id = Attachment("pkg-1");
        byte[] first = [1, 2, 3], second = [4, 5, 6];
        var hash = Sha([.. first, .. second]);
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
            id, body: PkgChunkBody(0, 2, 6, Convert.ToBase64String(first), hash)), new[] { "bob" });
        Assert.AreEqual(1, a.Db.GetPackageChunkCount(id));
        Assert.IsNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, id), "incomplete until all chunks arrive");

        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
            id, body: PkgChunkBody(1, 2, 6, Convert.ToBase64String(second), hash)), new[] { "bob" });
        Assert.AreEqual(2, a.Db.GetPackageChunkCount(id));
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, id), "completed transfer materialises");
        Assert.IsNotNull(a.Db.GetReplicatedAttachment("pkg-1"), "the assembled attachment reaches its actual table");
    }

    [TestMethod]
    public void Package_DuplicateChunkIsExactOnce()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var id = Attachment("pkg-2");
        var hash = Sha([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
            id, body: PkgChunkBody(0, 3, 9, "AQID", hash)), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
            id, body: PkgChunkBody(0, 3, 9, "AQID", hash)), new[] { "bob" });
        Assert.AreEqual(1, a.Db.GetPackageChunkCount(id));
    }

    [TestMethod]
    public void Package_ExceedsBoundRollsBack()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "pkg-big", body: PkgChunkBody(0, 1, ReplicationDomainStore.MaxPackageBytes + 1, "AAAA")), new[] { "bob" }));
        Assert.AreEqual(0, a.Db.GetPackageChunkCount("pkg-big"));
        Assert.AreEqual(0, a.Db.QueryEvents("a1", "epoch-1", 1, 64).Count);
    }

    [TestMethod]
    public void Package_ChunkWithoutContentHashRollsBack()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var body = J(new { chunkIndex = 0, chunkCount = 2, totalBytes = 8L, contentHash = "h", chunkB64 = "AAAA" });
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "pkg-nohash", body: body), new[] { "bob" }));
        Assert.AreEqual(0, a.Db.GetPackageChunkCount("pkg-nohash"));
    }

    [TestMethod]
    public void Package_TamperedChunkFailsAssembledHash()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var id = Attachment("pkg-tamper");
        var hash = Sha([1, 2, 3, 4, 5, 6]);
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                id, body: PkgChunkBody(0, 1, 6, Convert.ToBase64String([9, 9, 9, 9, 9, 9]), hash)), new[] { "bob" }));
        Assert.IsNull(a.Db.GetReplicatedAttachment("pkg-tamper"));
        Assert.AreEqual(0, a.Db.QueryEvents("a1", "epoch-1", 1, 64).Count);
    }

    [TestMethod]
    public void Package_MobileDeny()
    {
        var a = NewJournal("alice", "phone", desktop: false);
        Assert.ThrowsException<InvalidOperationException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "pkg-m", body: PkgChunkBody(0, 1, 4, "AAAA")), new[] { "bob" }));
        Assert.AreEqual(0, a.Db.GetPackageChunkCount("pkg-m"));
    }

    [TestMethod]
    public void Package_OutOfRangeIndexRollsBack()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                "pkg-oor", body: PkgChunkBody(5, 2, 8, "AAAA")), new[] { "bob" }));
        Assert.AreEqual(0, a.Db.GetPackageChunkCount("pkg-oor"));
    }

    // =====================================================================
    // F. ask_user: prompt / resolution, first-writer-wins, out-of-order.
    // =====================================================================

    [TestMethod]
    public void AskUser_PromptThenResolve()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            "ask-1", body: J(new { resolved = false, q = "Proceed?" })), new[] { "alice-phone" });
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-1"));

        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve,
            "ask-1", causal: "v2", body: J(new { resolved = true, answer = "yes" })), new[] { "alice-phone" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-1")!.Value.Body, "yes");
    }

    [TestMethod]
    public void AskUser_FirstResolutionWins()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve,
            "ask-2", body: J(new { resolved = true, answer = "first" })), new[] { "alice-phone" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve,
            "ask-2", causal: "v9", body: J(new { resolved = true, answer = "second" })), new[] { "alice-phone" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-2")!.Value.Body, "first");
    }

    [TestMethod]
    public void AskUser_OutOfOrderPromptDoesNotClobberResolution()
    {
        var a = NewJournal("alice", "a1");
        // Resolution lands first (carries the prompt snapshot); a late prompt must not revert it.
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve,
            "ask-3", causal: "v5", body: J(new { resolved = true, answer = "done" })), new[] { "alice-phone" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            "ask-3", causal: "v1", body: J(new { resolved = false, q = "late" })), new[] { "alice-phone" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-3")!.Value.Body, "done");
    }

    [TestMethod]
    public void AskUser_MobileParticipates()
    {
        // ask_user targets all own devices including mobile (unlike assets).
        var a = NewJournal("alice", "phone", desktop: false);
        var eid = a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            "ask-m", body: J(new { resolved = false, q = "hi" })), new[] { "alice-desk" });
        Assert.IsNotNull(a.Db.GetEvent(eid));
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-m"));
    }

    // =====================================================================
    // G. Read watermark: emitted from a read action, persisted separately.
    // =====================================================================

    [TestMethod]
    public void Watermark_ProjectsIntoWatermarkTable()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            "conv-1", conversationId: "conv-1", body: WatermarkBody("conv-1", "alice", "e-5", "a1", 5, 100)),
            new[] { "alice-phone" });
        var wm = a.Db.GetReadWatermark("conv-1", "alice");
        Assert.IsNotNull(wm);
        Assert.AreEqual("e-5", wm!.ThroughEventId);
    }

    [TestMethod]
    public void Watermark_LwwAdvancesOnlyForward()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            "conv-1", conversationId: "conv-1", body: WatermarkBody("conv-1", "alice", "e-9", "a1", 9, 200)),
            new[] { "alice-phone" });
        // A lower version must not move the watermark backward.
        a.Journal.EmitLocal(Env(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            "conv-1", conversationId: "conv-1", body: WatermarkBody("conv-1", "alice", "e-3", "a1", 3, 210)),
            new[] { "alice-phone" });
        Assert.AreEqual("e-9", a.Db.GetReadWatermark("conv-1", "alice")!.ThroughEventId);
    }

    [TestMethod]
    public void Watermark_IsNotAMessageEntity()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            "conv-7", conversationId: "conv-7", body: WatermarkBody("conv-7", "alice", "e-1", "a1", 1, 5)),
            new[] { "alice-phone" });
        // Watermarks live in their own table, not the domain-entity table.
        Assert.IsNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.ReadWatermark, "conv-7"));
    }

    // =====================================================================
    // H. Attachments: chunked, encrypted, local events (no relay blob).
    // =====================================================================

    [TestMethod]
    public void Attachment_ChunkedAsMessageLines()
    {
        var a = NewJournal("alice", "a1");
        for (var i = 0; i < 4; i++)
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
                "attach-1", conversationId: "conv-1", body: J(new { part = i, data = "ZW5j" })), new[] { "bob" });
        Assert.AreEqual(4, a.Db.CountReplicatedLines(ReplicationOpKinds.Message, "attach-1"));
    }

    [TestMethod]
    public void Attachment_EventBodyIsCiphertextNotPlaintext()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Env(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.AppendLine,
            "attach-2", conversationId: "conv-1", body: J(new { secret = "TOPSECRETMARKER" })), new[] { "bob" });
        var evt = a.Db.GetEvent(eid);
        Assert.IsNotNull(evt);
        Assert.IsFalse(evt!.Ciphertext.Contains("TOPSECRETMARKER"), "attachment bytes must be encrypted at rest");
    }

    // =====================================================================
    // I. Inbound projection end-to-end: exact-once and invalid rollback.
    // =====================================================================

    [TestMethod]
    public async Task Inbound_MessageProjectsOnReceiver()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(b);
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetEvent(eid));
        Assert.IsNotNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1"));
    }

    [TestMethod]
    public async Task Inbound_DuplicateDeliveryProjectsExactlyOnce()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(b);
        await a.Engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.AppendLine, "t1",
                conversationId: "conv-1", body: J(new { line = "one" })), new[] { b.Handle });
        await ConnectAsync(a, b);
        // Re-run presence/drain: a re-offer must not re-project the same line.
        await ConnectAsync(a, b);

        Assert.AreEqual(1, b.Db.CountReplicatedLines(ReplicationOpKinds.Topic, "t1"));
    }

    [TestMethod]
    public async Task Inbound_InvalidAssetHashRollsBackAndDoesNotStore()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1", desktop: true);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);

        var origin = AddOrigin("mallory", "m1");
        var badEnv = Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            "asset-bad", body: AssetBody(new byte[] { 1, 2, 3 }, overrideHash: "cafe"));
        var evt = MakeEvent(origin, "mallory", "m1", 1, badEnv, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("m1", new[] { evt }));

        // Permanent projection failure: nothing stored, cursor never advanced.
        Assert.IsNull(b.Db.GetEvent(evt.EventId));
        Assert.IsNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "asset-bad"));
        var cursor = b.Db.GetCursor("m1");
        Assert.IsTrue(cursor is null || cursor.Contiguous == 0);
    }

    [TestMethod]
    public async Task Inbound_MobileStoresAssetLogPositionWithoutMaterialisingBytes()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "phone", desktop: false);
        UseProjectingApplier(b);
        await EstablishAsync(a, b);

        var origin = AddOrigin("alice2", "a2");
        var env = Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            "asset-d", body: AssetBody([5, 6, 7], id: "asset-d"));
        var evt = MakeEvent(origin, "alice2", "a2", 1, env, new[] { b.Keys.PublicB64 });
        await DeliverBatchAsync(a, b, Batch("a2", new[] { evt }));

        Assert.IsNotNull(b.Db.GetEvent(evt.EventId));
        Assert.IsNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "asset-d"));
        Assert.AreEqual(1UL, b.Db.GetCursor("a2")?.Contiguous);
        Assert.IsFalse(b.Engine.IsHalted("a2"));
        Assert.AreEqual(0, b.Applier.Count);
    }

    [TestMethod]
    public async Task Inbound_ContactConvergesBothDirections()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(a);
        UseProjectingApplier(b);
        await a.Engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert, "x",
                body: J(new { from = "alice" })), new[] { b.Handle });
        await b.Engine.EmitLocalAsync(
            Env(ReplicationOpKinds.Contact, ReplicationPayloadCodec.DomainAction.Upsert, "y",
                body: J(new { from = "bob" })), new[] { a.Handle });
        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "x"));
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Contact, "y"));
    }

    // =====================================================================
    // J. Event target / custody: normalise, dedup, own-account sibling rule.
    // =====================================================================

    [TestMethod]
    public void Custody_SoleOwnDeviceHasNoOutboxTarget()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "alice" });
        Assert.IsNotNull(a.Db.GetEvent(eid), "locally persisted (complete) even with no sibling");
        Assert.IsNull(a.Db.GetOutboxState(eid, "alice"), "no false remote custody target");
    }

    [TestMethod]
    public void Custody_OwnAccountTrackedWhenSiblingExists()
    {
        var a = NewJournal("alice", "a1");
        _ = AddSibling("alice", "a2");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "alice" });
        Assert.AreEqual(MeshDb.OutboxStatePending, a.Db.GetOutboxState(eid, "alice"));
    }

    [TestMethod]
    public async Task Custody_OneReceiptMarksPersisted()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await ConnectAsync(a, b);
        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.AreEqual(MeshDb.OutboxStatePersisted, a.Db.GetOutboxState(eid, "bob"));
    }

    [TestMethod]
    public void Custody_GenesisOnboardingInitialisesLocalAuthority()
    {
        var a = NewJournal("alice", "a1");
        Assert.IsTrue(a.Db.HasLocalAuthority("alice"));
        Assert.AreNotEqual(OnlineReplicationProtocol.ZeroHash, a.Db.GetCustodyHeadHash("alice"));
    }

    [TestMethod]
    public void Custody_GenesisIsIdempotent()
    {
        var a = NewJournal("alice", "a1");
        var head1 = a.Db.GetCustodyHeadHash("alice");
        var head2 = a.Db.InitializeGenesisCustody("alice", a.Keys.PublicB64, a.Keys.PrivateB64);
        Assert.AreEqual(head1, head2);
        Assert.AreEqual(1, a.Db.GetCustodyChain("alice").Count);
    }

    [TestMethod]
    public void Custody_ControlEntryAppendsThroughValidatedPath()
    {
        var a = NewJournal("alice", "a1");
        var sibling = AddSibling("alice", "a2");
        var head = a.Db.GetCustodyHead("alice")!;
        var next = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice", generation: 1, prevHash: head.EntryHash, action: CustodyAction.AddDevice,
            subjectDeviceKey: sibling.PublicB64, recoveryPublicKey: null,
            effectiveAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            signerKey: a.Keys.PublicB64, signerPrivateKeyB64: a.Keys.PrivateB64);
        a.Db.AppendCustodyEntry(next);
        Assert.AreEqual(2, a.Db.GetCustodyChain("alice").Count);
    }

    // =====================================================================
    // K. Reconnect: an offline-journaled change is drained once a session forms.
    // =====================================================================

    [TestMethod]
    public async Task Reconnect_PendingOutboxDeliversOnConnect()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(b);
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle }); // no session yet
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
        Assert.IsNull(b.Db.GetEvent(eid));

        await ConnectAsync(a, b);

        Assert.IsNotNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m1"));
        Assert.AreEqual(ReplicationDeliveryState.Persisted, a.Engine.GetDeliveryState(eid, "bob"));
    }

    [TestMethod]
    public async Task Reconnect_OfflineTargetLeavesOutboxPending()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        Fabric.SetOnline(b.Device, false);
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        await a.Engine.OnPresenceOnlineAsync(b.Handle, b.Device);
        await Fabric.DrainAsync();
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
    }

    // =====================================================================
    // L. Restart / crash recovery: journaled domain state survives reopen.
    // =====================================================================

    [TestMethod]
    public async Task Restart_DomainRowsSurviveReopen()
    {
        var a = NewNode("alice", "a1");
        await a.Engine.EmitLocalAsync(Msg("m-restart"), new[] { "bob" });
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m-restart"));

        var reopened = Reopen(a);
        Assert.IsNotNull(reopened.Db.GetReplicatedEntity(ReplicationOpKinds.Message, "m-restart"),
            "projected domain state must survive a restart");
    }

    [TestMethod]
    public async Task Restart_EventsAndOutboxSurviveReopen()
    {
        var a = NewNode("alice", "a1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m-2"), new[] { "bob" });
        var reopened = Reopen(a);
        Assert.IsNotNull(reopened.Db.GetEvent(eid));
        Assert.AreEqual(MeshDb.OutboxStatePending, reopened.Db.GetOutboxState(eid, "bob"));
    }

    [TestMethod]
    public async Task Restart_LinesSurviveReopen()
    {
        var a = NewNode("alice", "a1");
        for (var i = 0; i < 3; i++)
            await a.Engine.EmitLocalAsync(
                Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.AppendLine, "t-r",
                    conversationId: "conv-1", body: J(new { i })), new[] { "bob" });
        var reopened = Reopen(a);
        Assert.AreEqual(3, reopened.Db.CountReplicatedLines(ReplicationOpKinds.Topic, "t-r"));
    }

    // =====================================================================
    // M. Delivery-state surface (stored / pending / persisted).
    // =====================================================================

    [TestMethod]
    public async Task DeliveryState_StoredForSoleOwnDevice()
    {
        var a = NewNode("alice", "a1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { a.Handle });
        Assert.AreEqual(ReplicationDeliveryState.Stored, a.Engine.GetDeliveryState(eid, "alice"));
    }

    [TestMethod]
    public async Task DeliveryState_PendingBeforeConnect()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        var eid = await a.Engine.EmitLocalAsync(Msg("m1"), new[] { b.Handle });
        Assert.AreEqual(ReplicationDeliveryState.Pending, a.Engine.GetDeliveryState(eid, "bob"));
    }

    // =====================================================================
    // N. Codec projection direct-call coverage (validation + fail-closed).
    // =====================================================================

    [TestMethod]
    public void Projection_UnknownKindThrows()
    {
        var a = NewJournal("alice", "a1");
        using var tx = BeginTx(a.Db, out var conn);
        var evt = SyntheticEvent("bogus", "e1");
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            ReplicationPayloadCodec.Project(conn, tx, evt,
                new ReplicationPayloadCodec.DomainEnvelope("bogus", ReplicationPayloadCodec.DomainAction.Upsert, "e1", null, "v1", "{}"),
                deviceIsDesktop: true));
        tx.Rollback();
    }

    [TestMethod]
    public void Projection_EmptyEntityIdThrows()
    {
        var a = NewJournal("alice", "a1");
        using var tx = BeginTx(a.Db, out var conn);
        var evt = SyntheticEvent(ReplicationOpKinds.Message, "");
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            ReplicationPayloadCodec.Project(conn, tx, evt,
                new ReplicationPayloadCodec.DomainEnvelope(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert, "", "conv-1", "v1", "{}"),
                deviceIsDesktop: true));
        tx.Rollback();
    }

    [TestMethod]
    public void Projection_MobilePackageTransferFailsClosed()
    {
        var a = NewJournal("alice", "a1");
        using var tx = BeginTx(a.Db, out var conn);
        var evt = SyntheticEvent(ReplicationOpKinds.Asset, "pkg-skip");
        // A mobile device cannot materialise package bytes. Advancing past the change would strand
        // it forever, so the projection fails closed and nothing is written.
        Assert.ThrowsException<ReplicationProjectionException>(() =>
            ReplicationPayloadCodec.Project(conn, tx, evt,
                new ReplicationPayloadCodec.DomainEnvelope(ReplicationOpKinds.Asset,
                    ReplicationPayloadCodec.DomainAction.PackageTransfer, "pkg-skip", null, "v1",
                    PkgChunkBody(0, 1, 4, "AAAA")), deviceIsDesktop: false));
        tx.Rollback();
        Assert.AreEqual(0, a.Db.GetPackageChunkCount("pkg-skip"));
    }

    [TestMethod]
    public void Codec_RequiresDesktopOnlyForAssetBytes()
    {
        Assert.IsTrue(ReplicationPayloadCodec.RequiresDesktop(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert));
        Assert.IsTrue(ReplicationPayloadCodec.RequiresDesktop(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer));
        Assert.IsFalse(ReplicationPayloadCodec.RequiresDesktop(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetDelete));
        Assert.IsFalse(ReplicationPayloadCodec.RequiresDesktop(ReplicationOpKinds.Message, ReplicationPayloadCodec.DomainAction.Upsert));
    }

    [TestMethod]
    public void Store_CompareCausalOrdersByLengthThenOrdinal()
    {
        Assert.IsTrue(ReplicationDomainStore.CompareCausal("v10", "v2") > 0);
        Assert.IsTrue(ReplicationDomainStore.CompareCausal("v2", "v10") < 0);
        Assert.IsTrue(ReplicationDomainStore.CompareCausal("v3", "v3") == 0);
        Assert.IsTrue(ReplicationDomainStore.CompareCausal("vb", "va") > 0);
    }

    // =====================================================================
    // O. Additional domain / convergence coverage (breadth).
    // =====================================================================

    [TestMethod]
    public void Offline_ConversationDeleteTombstones()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Upsert,
            "conv-x", conversationId: "conv-x", body: J(new { title = "X" })), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Conversation, ReplicationPayloadCodec.DomainAction.Delete,
            "conv-x", conversationId: "conv-x", causal: "v2"), new[] { "bob" });
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Conversation, "conv-x")!.Value.Deleted);
    }

    [TestMethod]
    public void Offline_TopicDeleteTombstones()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert,
            "t-del", conversationId: "conv-1", body: J(new { name = "T" })), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Delete,
            "t-del", conversationId: "conv-1", causal: "v2"), new[] { "bob" });
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Topic, "t-del")!.Value.Deleted);
    }

    [TestMethod]
    public void Offline_CircleDelete()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Upsert,
            "circle-d", body: J(new { m = 1 })), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Circle, ReplicationPayloadCodec.DomainAction.Delete,
            "circle-d", causal: "v2"), new[] { "bob" });
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Circle, "circle-d")!.Value.Deleted);
    }

    [TestMethod]
    public void Offline_EmitReturnsNonEmptyEventId()
    {
        var a = NewJournal("alice", "a1");
        var eid = a.Journal.EmitLocal(Msg("m1"), new[] { "bob" });
        Assert.IsFalse(string.IsNullOrWhiteSpace(eid));
    }

    [TestMethod]
    public void Domain_MemoryCausalLww()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert,
            "mem-lww", causal: "v3", body: MemoryBody("mem-lww", content: "newer text")), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert,
            "mem-lww", causal: "v2", body: MemoryBody("mem-lww", content: "older text")), new[] { "bob" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.Memory, "mem-lww")!.Value.Body, "newer text");
    }

    [TestMethod]
    public void Asset_DeleteThenLowerUpsertKeepsDelete()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetDelete,
            "as-lww", causal: "v2", body: AssetTombstone("as-lww")), new[] { "bob" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.AssetUpsert,
            "as-lww", causal: "v1", body: AssetBody([1], id: "as-lww")), new[] { "bob" });
        Assert.IsTrue(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, "as-lww")!.Value.Deleted,
            "a lower causal upsert must not resurrect a higher-versioned delete");
    }

    [TestMethod]
    public void Watermark_DistinctConversationsIndependent()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            "conv-a", conversationId: "conv-a", body: WatermarkBody("conv-a", "alice", "e-2", "a1", 2, 1)), new[] { "alice-phone" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.ReadWatermark, ReplicationPayloadCodec.DomainAction.ReadWatermark,
            "conv-b", conversationId: "conv-b", body: WatermarkBody("conv-b", "alice", "e-7", "a1", 7, 1)), new[] { "alice-phone" });
        Assert.AreEqual("e-2", a.Db.GetReadWatermark("conv-a", "alice")!.ThroughEventId);
        Assert.AreEqual("e-7", a.Db.GetReadWatermark("conv-b", "alice")!.ThroughEventId);
    }

    [TestMethod]
    public void AskUser_DistinctEntitiesIndependent()
    {
        var a = NewJournal("alice", "a1");
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            "ask-a", body: J(new { resolved = false, q = "A" })), new[] { "alice-phone" });
        a.Journal.EmitLocal(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserResolve,
            "ask-b", body: J(new { resolved = true, answer = "B" })), new[] { "alice-phone" });
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-a")!.Value.Body, "A");
        StringAssert.Contains(a.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-b")!.Value.Body, "B");
    }

    [TestMethod]
    public void Package_ThreeChunksComplete()
    {
        var a = NewJournal("alice", "a1", desktop: true);
        var id = Attachment("pkg-3");
        byte[][] parts = [[1, 2, 3], [4, 5, 6], [7, 8, 9]];
        var hash = Sha([.. parts[0], .. parts[1], .. parts[2]]);
        for (var i = 0; i < 3; i++)
            a.Journal.EmitLocal(Env(ReplicationOpKinds.Asset, ReplicationPayloadCodec.DomainAction.PackageTransfer,
                id, body: PkgChunkBody(i, 3, 9, Convert.ToBase64String(parts[i]), hash)), new[] { "bob" });
        Assert.AreEqual(3, a.Db.GetPackageChunkCount(id));
        Assert.IsNotNull(a.Db.GetReplicatedEntity(ReplicationOpKinds.Asset, id));
        Assert.IsNotNull(a.Db.GetReplicatedAttachment("pkg-3"));
    }

    [TestMethod]
    public void Custody_InitCustodyFalseHasNoLocalAuthority()
    {
        // ZeroHash is a valid identity sentinel, but with no genesis chain there is no local
        // authority yet -- HasLocalAuthority must report false until onboarding runs.
        var a = NewJournal("alice", "a1", initCustody: false);
        Assert.IsFalse(a.Db.HasLocalAuthority("alice"));
    }

    [TestMethod]
    public void Custody_ChainContainsValidatedControlEntry()
    {
        var a = NewJournal("alice", "a1");
        var sibling = AddSibling("alice", "a2");
        var head = a.Db.GetCustodyHead("alice")!;
        var next = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice", generation: 1, prevHash: head.EntryHash, action: CustodyAction.AddDevice,
            subjectDeviceKey: sibling.PublicB64, recoveryPublicKey: null,
            effectiveAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            signerKey: a.Keys.PublicB64, signerPrivateKeyB64: a.Keys.PrivateB64);
        a.Db.AppendCustodyEntry(next);
        Assert.IsTrue(a.Db.GetCustodyChain("alice").Any(e => e.EntryHash == next.EntryHash));
    }

    [TestMethod]
    public async Task Inbound_TopicProjectsOnReceiver()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(b);
        await a.Engine.EmitLocalAsync(Env(ReplicationOpKinds.Topic, ReplicationPayloadCodec.DomainAction.Upsert,
            "t-in", conversationId: "conv-1", body: J(new { name = "N" })), new[] { b.Handle });
        await ConnectAsync(a, b);
        Assert.IsNotNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Topic, "t-in"));
    }

    [TestMethod]
    public async Task Inbound_AskUserProjectsOnReceiver()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(b);
        await a.Engine.EmitLocalAsync(Env(ReplicationOpKinds.AskUser, ReplicationPayloadCodec.DomainAction.AskUserPrompt,
            "ask-in", body: J(new { resolved = false, q = "Q" })), new[] { b.Handle });
        await ConnectAsync(a, b);
        Assert.IsNotNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.AskUser, "ask-in"));
    }

    [TestMethod]
    public async Task Inbound_MemoryProjectsOnReceiver()
    {
        var a = NewNode("alice", "a1");
        var b = NewNode("bob", "b1");
        UseProjectingApplier(b);
        await a.Engine.EmitLocalAsync(Env(ReplicationOpKinds.Memory, ReplicationPayloadCodec.DomainAction.Upsert,
            "mem-in", body: MemoryBody("mem-in")), new[] { b.Handle });
        await ConnectAsync(a, b);
        Assert.IsNotNull(b.Db.GetReplicatedEntity(ReplicationOpKinds.Memory, "mem-in"));
    }

    // -------------------------------------------------------------------
    // Local helpers for direct projection tests.
    // -------------------------------------------------------------------

    private static Microsoft.Data.Sqlite.SqliteTransaction BeginTx(MeshDb db, out Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        conn = db.RawConnectionForTest;
        return (Microsoft.Data.Sqlite.SqliteTransaction)conn.BeginTransaction();
    }

    private static ReplicationEvent SyntheticEvent(string kind, string entityId)
        => new(
            EventId: Guid.NewGuid().ToString("n"),
            ConversationId: "conv-1",
            OriginAccount: "alice",
            OriginDeviceId: "a1",
            LogEpoch: "epoch-1",
            Seq: 1,
            AuthGeneration: 0,
            Kind: kind,
            EntityId: entityId,
            CausalVersion: "v1",
            CreatedAtUnixMs: 0,
            Ciphertext: "x",
            ContentHash: "h",
            Signature: "s");
}
