using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Behavioural foundation tests for the protocol-9 online-only replication layer: event
/// identity and signing, sequence allocation, the cursor and range state machine, atomic
/// database primitives, receipts, read watermarks, the custody hash chain and validators.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OnlineReplicationFoundationTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "online-replication-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(7, 32).Select(v => (byte)v).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private sealed record KeyPair(string PrivateB64, string PublicB64);

    private static KeyPair NewKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new KeyPair(
            Convert.ToBase64String(ec.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    private static ReplicationEvent MakeEvent(
        KeyPair signer,
        string originDevice,
        string logEpoch,
        ulong seq,
        string ciphertext = "cipher",
        string kind = ReplicationOpKinds.Message,
        string? conversationId = "conv-1",
        long authGeneration = 0)
        => OnlineReplicationProtocol.CreateEvent(
            originDevice, logEpoch, seq, "alice", authGeneration, kind, "entity-" + seq,
            conversationId, "v" + seq, 1_700_000_000_000 + (long)seq, ciphertext, signer.PrivateB64);

    private static ReplicationCursorEntry ApplyMany(ReplicationCursorEntry cursor, string epoch, params ulong[] seqs)
    {
        foreach (var s in seqs)
            OnlineReplicationProtocol.ApplyToCursor(cursor, epoch, s, out cursor);
        return cursor;
    }

    // ==================================================================
    // A. Event identity, canonicalisation, hash, signature.
    // ==================================================================

    [TestMethod]
    public void Event_CreateAndVerify_RoundTrips()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        Assert.IsTrue(OnlineReplicationProtocol.VerifyEvent(e, k.PublicB64));
        Assert.IsTrue(OnlineReplicationProtocol.EventIdentityMatches(e));
    }

    [TestMethod]
    public void Event_Id_IsDeterministic_ForSameInputs()
    {
        var k = NewKeyPair();
        var a = MakeEvent(k, "dev-a", "epoch-1", 5);
        var b = MakeEvent(k, "dev-a", "epoch-1", 5);
        Assert.AreEqual(a.EventId, b.EventId);
        Assert.AreEqual(a.ContentHash, b.ContentHash);
    }

    [TestMethod]
    public void Event_Id_Differs_ForDifferentPosition()
    {
        var k = NewKeyPair();
        var a = MakeEvent(k, "dev-a", "epoch-1", 5);
        var b = MakeEvent(k, "dev-a", "epoch-1", 6);
        Assert.AreNotEqual(a.EventId, b.EventId);
    }

    [TestMethod]
    public void Event_ContentHash_IsSha256OfCiphertext()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1, ciphertext: "opaque-frame");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("opaque-frame"))).ToLowerInvariant();
        Assert.AreEqual(expected, e.ContentHash);
    }

    [TestMethod]
    public void Event_Verify_FailsForWrongKey()
    {
        var k = NewKeyPair();
        var other = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        Assert.IsFalse(OnlineReplicationProtocol.VerifyEvent(e, other.PublicB64));
    }

    [TestMethod]
    public void Event_Verify_FailsWhenCiphertextTampered()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        var tampered = e with { Ciphertext = "different" };
        Assert.IsFalse(OnlineReplicationProtocol.EventIdentityMatches(tampered));
        Assert.IsFalse(OnlineReplicationProtocol.VerifyEvent(tampered, k.PublicB64));
    }

    [TestMethod]
    public void Event_Verify_FailsWhenSignatureTampered()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        var other = MakeEvent(k, "dev-a", "epoch-1", 2);
        var tampered = e with { Signature = other.Signature };
        Assert.IsFalse(OnlineReplicationProtocol.VerifyEvent(tampered, k.PublicB64));
    }

    [TestMethod]
    public void Event_ValidateShape_RejectsZeroSeq()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1) with { Seq = 0 };
        Assert.IsFalse(OnlineReplicationProtocol.ValidateEventShape(e, out var error));
        StringAssert.Contains(error, "Seq");
    }

    [TestMethod]
    public void Event_ValidateShape_RejectsUnknownKind()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1, kind: "not-a-kind");
        Assert.IsFalse(OnlineReplicationProtocol.ValidateEventShape(e, out _));
    }

    [TestMethod]
    public void Event_ValidateShape_AcceptsWellFormed()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        Assert.IsTrue(OnlineReplicationProtocol.ValidateEventShape(e, out _));
    }

    // ==================================================================
    // B. Sequence allocation.
    // ==================================================================

    [TestMethod]
    public void AllocateNextSequence_IsStrictlyMonotonic()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.EnsureLocalOrigin("dev-local", "epoch-x", 0);
        var (epoch, s1) = db.AllocateNextSequence("dev-local");
        var (_, s2) = db.AllocateNextSequence("dev-local");
        var (_, s3) = db.AllocateNextSequence("dev-local");
        Assert.AreEqual("epoch-x", epoch);
        Assert.AreEqual(1UL, s1);
        Assert.AreEqual(2UL, s2);
        Assert.AreEqual(3UL, s3);
    }

    [TestMethod]
    public void AllocateNextSequence_UnderParallelLoad_IsUniqueAndContiguous()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.EnsureLocalOrigin("dev-local", "epoch-x", 0);
        const int count = 200;
        var seqs = new ConcurrentBag<ulong>();
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            var (_, seq) = db.AllocateNextSequence("dev-local");
            seqs.Add(seq);
        });
        var ordered = seqs.OrderBy(v => v).ToArray();
        Assert.AreEqual(count, ordered.Length);
        Assert.AreEqual(count, ordered.Distinct().Count());
        for (var i = 0; i < count; i++)
            Assert.AreEqual((ulong)(i + 1), ordered[i]);
    }

    [TestMethod]
    public void AllocateNextSequence_ThrowsForUnregisteredOrigin()
    {
        using var db = MeshDb.Open(databasePath, key);
        Assert.ThrowsException<InvalidOperationException>(() => db.AllocateNextSequence("missing"));
    }

    // ==================================================================
    // C. Append idempotency and fork detection.
    // ==================================================================

    [TestMethod]
    public void AppendEvent_ExactDuplicate_IsIdempotent()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        Assert.AreEqual(MeshDb.ReplicationAppendResult.Inserted, db.AppendEvent(e));
        Assert.AreEqual(MeshDb.ReplicationAppendResult.Duplicate, db.AppendEvent(e));
        Assert.AreEqual(1, db.QueryEvents("dev-a", "epoch-1", 1, 100).Count);
    }

    [TestMethod]
    public void AppendEvent_ConflictingSamePosition_ProvesFork()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var first = MakeEvent(k, "dev-a", "epoch-1", 1, ciphertext: "one");
        var forked = MakeEvent(k, "dev-a", "epoch-1", 1, ciphertext: "two");
        db.AppendEvent(first);
        Assert.ThrowsException<MeshDb.ReplicationForkException>(() => db.AppendEvent(forked));
    }

    [TestMethod]
    public void AppendEvent_RejectsMalformedEvent()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var bad = MakeEvent(k, "dev-a", "epoch-1", 1) with { ContentHash = "zz" };
        Assert.ThrowsException<ArgumentException>(() => db.AppendEvent(bad));
    }

    [TestMethod]
    public void QueryEvents_ReturnsOrderedBoundedRange()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        for (ulong s = 1; s <= 10; s++) db.AppendEvent(MakeEvent(k, "dev-a", "epoch-1", s));
        var range = db.QueryEvents("dev-a", "epoch-1", 3, 6);
        CollectionAssert.AreEqual(new ulong[] { 3, 4, 5, 6 }, range.Select(e => e.Seq).ToArray());
    }

    [TestMethod]
    public void QueryEvents_RespectsLimit()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        for (ulong s = 1; s <= 10; s++) db.AppendEvent(MakeEvent(k, "dev-a", "epoch-1", s));
        var range = db.QueryEvents("dev-a", "epoch-1", 1, 100, limit: 4);
        Assert.AreEqual(4, range.Count);
    }

    // ==================================================================
    // D. Cursor state machine.
    // ==================================================================

    [TestMethod]
    public void Cursor_Empty_HasFixedBitsetSize()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        Assert.AreEqual(OnlineReplicationLimits.AheadBitsBytes, c.AheadBits.Length);
        Assert.AreEqual(128, c.AheadBits.Length);
        Assert.AreEqual(0UL, c.Contiguous);
    }

    [TestMethod]
    public void Cursor_ContiguousApply_AdvancesHead()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 1, out c);
        Assert.AreEqual(CursorApplyResult.AppliedContiguous, r);
        Assert.AreEqual(1UL, c.Contiguous);
    }

    [TestMethod]
    public void Cursor_Duplicate_BelowHead_IsDuplicate()
    {
        var c = ApplyMany(OnlineReplicationProtocol.EmptyCursor(), "epoch-1", 1, 2, 3);
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 2, out _);
        Assert.AreEqual(CursorApplyResult.Duplicate, r);
    }

    [TestMethod]
    public void Cursor_OutOfOrder_WithinWindow_IsAhead()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 5, out c);
        Assert.AreEqual(CursorApplyResult.AppliedAhead, r);
        Assert.AreEqual(0UL, c.Contiguous);
    }

    [TestMethod]
    public void Cursor_DuplicateAheadBit_IsDuplicate()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 5, out c);
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 5, out _);
        Assert.AreEqual(CursorApplyResult.Duplicate, r);
    }

    [TestMethod]
    public void Cursor_FillingGap_CollapsesAheadBits()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 3, out c);
        OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 2, out c);
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 1, out c);
        Assert.AreEqual(CursorApplyResult.AppliedContiguous, r);
        Assert.AreEqual(3UL, c.Contiguous);
    }

    [TestMethod]
    public void Cursor_AtWindowEdge_IsAccepted()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", (ulong)OnlineReplicationLimits.ReorderWindow, out _);
        Assert.AreEqual(CursorApplyResult.AppliedAhead, r);
    }

    [TestMethod]
    public void Cursor_BeyondWindow_IsRejected()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", (ulong)OnlineReplicationLimits.ReorderWindow + 1, out _);
        Assert.AreEqual(CursorApplyResult.RejectedTooFarAhead, r);
    }

    [TestMethod]
    public void Cursor_EpochMismatch_IsRejected()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(c, "epoch-A", 1, out c);
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-B", 2, out _);
        Assert.AreEqual(CursorApplyResult.RejectedEpochMismatch, r);
    }

    [TestMethod]
    public void Cursor_ZeroSeq_IsRejectedInvalid()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        var r = OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 0, out _);
        Assert.AreEqual(CursorApplyResult.RejectedInvalid, r);
    }

    [TestMethod]
    public void Cursor_ApplyIsPure_DoesNotMutateInput()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", 1, out _);
        Assert.AreEqual(0UL, c.Contiguous, "The input cursor must not be mutated.");
    }

    // ==================================================================
    // E. Range planner.
    // ==================================================================

    [TestMethod]
    public void MissingRanges_SparseGaps_ProducesDisjointRanges()
    {
        var c = ApplyMany(OnlineReplicationProtocol.EmptyCursor(), "epoch-1", 1, 2, 3, 4, 5, 8, 10);
        var ranges = OnlineReplicationProtocol.ComputeMissingRanges(c, 12);
        var pairs = ranges.Select(r => (r.FromSeq, r.ToSeq)).ToArray();
        CollectionAssert.AreEqual(
            new[] { (6UL, 7UL), (9UL, 9UL), (11UL, 12UL) }, pairs);
    }

    [TestMethod]
    public void MissingRanges_NothingOffered_IsEmpty()
    {
        var c = ApplyMany(OnlineReplicationProtocol.EmptyCursor(), "epoch-1", 1, 2, 3);
        Assert.AreEqual(0, OnlineReplicationProtocol.ComputeMissingRanges(c, 3).Count);
    }

    [TestMethod]
    public void MissingRanges_FullyContiguous_IsEmpty()
    {
        var c = ApplyMany(OnlineReplicationProtocol.EmptyCursor(), "epoch-1", 1, 2, 3, 4, 5);
        Assert.AreEqual(0, OnlineReplicationProtocol.ComputeMissingRanges(c, 5).Count);
    }

    [TestMethod]
    public void MissingRanges_BeyondWindow_MergesTail()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        var offered = (ulong)OnlineReplicationLimits.ReorderWindow + 500;
        var ranges = OnlineReplicationProtocol.ComputeMissingRanges(c, offered);
        Assert.AreEqual(1, ranges.Count);
        Assert.AreEqual(1UL, ranges[0].FromSeq);
        Assert.AreEqual(offered, ranges[0].ToSeq);
    }

    [TestMethod]
    public void MissingRanges_AreBounded()
    {
        var c = OnlineReplicationProtocol.EmptyCursor();
        // Present every even seq inside the window so odd seqs become singleton gaps.
        for (ulong s = 2; s <= (ulong)OnlineReplicationLimits.ReorderWindow; s += 2)
            OnlineReplicationProtocol.ApplyToCursor(c, "epoch-1", s, out c);
        var ranges = OnlineReplicationProtocol.ComputeMissingRanges(c, (ulong)OnlineReplicationLimits.ReorderWindow);
        Assert.IsTrue(ranges.Count <= OnlineReplicationLimits.MaxRangeRequests);
    }

    [TestMethod]
    public void PlanReplication_LowRetention_RequiresResync()
    {
        var c = OnlineReplicationProtocol.EmptyCursor(); // needs from seq 1
        var offer = new ReplicationOffer("dev-a", "epoch-1", AvailableFrom: 50, AvailableThrough: 100);
        var plan = OnlineReplicationProtocol.PlanReplication(c, offer);
        Assert.IsTrue(plan.RequiresResync);
        Assert.AreEqual(0, plan.Ranges.Count);
    }

    [TestMethod]
    public void PlanReplication_WithinRetention_ProducesRanges()
    {
        var c = ApplyMany(OnlineReplicationProtocol.EmptyCursor(), "epoch-1", 1, 2, 3);
        var offer = new ReplicationOffer("dev-a", "epoch-1", AvailableFrom: 1, AvailableThrough: 6);
        var plan = OnlineReplicationProtocol.PlanReplication(c, offer);
        Assert.IsFalse(plan.RequiresResync);
        CollectionAssert.AreEqual(new[] { (4UL, 6UL) }, plan.Ranges.Select(r => (r.FromSeq, r.ToSeq)).ToArray());
    }

    [TestMethod]
    public void PlanReplication_NothingNew_IsEmptyNoResync()
    {
        var c = ApplyMany(OnlineReplicationProtocol.EmptyCursor(), "epoch-1", 1, 2, 3);
        var offer = new ReplicationOffer("dev-a", "epoch-1", 1, 3);
        var plan = OnlineReplicationProtocol.PlanReplication(c, offer);
        Assert.IsFalse(plan.RequiresResync);
        Assert.AreEqual(0, plan.Ranges.Count);
    }

    // ==================================================================
    // F. Atomic event + cursor apply.
    // ==================================================================

    [TestMethod]
    public void ApplyInboundEvent_CommitsEventAndCursorTogether()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        var cursor = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1", 1, out cursor);
        db.ApplyInboundEvent(e, cursor);
        Assert.IsNotNull(db.GetEvent(e.EventId));
        Assert.AreEqual(1UL, db.GetCursor("dev-a")!.Contiguous);
    }

    [TestMethod]
    public void ApplyInboundEvent_WhenDomainApplyThrows_RollsBothBack()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();

        // Seed a known cursor so we can prove it is unchanged after the rollback.
        var seeded = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(seeded, "epoch-1", 1, out seeded);
        var firstEvent = MakeEvent(k, "dev-a", "epoch-1", 1);
        db.ApplyInboundEvent(firstEvent, seeded);

        var e2 = MakeEvent(k, "dev-a", "epoch-1", 2);
        var advanced = OnlineReplicationProtocol.EmptyCursor();
        advanced = ApplyMany(advanced, "epoch-1", 1, 2);

        Assert.ThrowsException<InvalidOperationException>(() =>
            db.ApplyInboundEvent(e2, advanced, (_, _) => throw new InvalidOperationException("boom")));

        Assert.IsNull(db.GetEvent(e2.EventId), "The event insert must roll back.");
        Assert.AreEqual(1UL, db.GetCursor("dev-a")!.Contiguous, "The cursor update must roll back.");
    }

    // ==================================================================
    // G. Local event + outbox atomicity and reference-only invariant.
    // ==================================================================

    [TestMethod]
    public void AppendLocalEventWithOutbox_CreatesReferencesAtomically()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        db.AppendLocalEventWithOutbox(e, new[] { "bob", "carol" });
        Assert.IsNotNull(db.GetEvent(e.EventId));
        Assert.AreEqual(1, db.QueryDueOutbox("bob", MeshDb.OutboxStatePending).Count);
        Assert.AreEqual(1, db.QueryDueOutbox("carol", MeshDb.OutboxStatePending).Count);
    }

    [TestMethod]
    public void OutboxTable_HasNoPayloadColumn()
    {
        using (var db = MeshDb.Open(databasePath, key))
        {
            // Force schema creation before reading it back through a raw connection.
            Assert.IsNotNull(db);
        }
        SqliteConnection.ClearAllPools();
        var columns = PragmaColumns("replication_outbox");
        CollectionAssert.Contains(columns, "event_id");
        CollectionAssert.Contains(columns, "target_account");
        CollectionAssert.Contains(columns, "state");
        CollectionAssert.DoesNotContain(columns, "ciphertext");
        CollectionAssert.DoesNotContain(columns, "payload");
        CollectionAssert.DoesNotContain(columns, "content");
    }

    [TestMethod]
    public void Outbox_MarkOffered_TransitionsState()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        db.AppendLocalEventWithOutbox(e, new[] { "bob" });
        db.MarkOutboxOffered(e.EventId, "bob");
        Assert.AreEqual(0, db.QueryDueOutbox("bob", MeshDb.OutboxStatePending).Count);
        Assert.AreEqual(1, db.QueryDueOutbox("bob", MeshDb.OutboxStateOffered).Count);
    }

    // ==================================================================
    // H. Receipts.
    // ==================================================================

    [TestMethod]
    public void Receipt_SignAndVerify_RoundTrips()
    {
        var receiver = NewKeyPair();
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            "recv-dev", "dev-a", "epoch-1", 5,
            OnlineReplicationProtocol.HashText("cursor"), OnlineReplicationProtocol.HashText("batch"),
            receiver.PrivateB64);
        Assert.IsTrue(OnlineReplicationProtocol.VerifyReceipt(receipt, receiver.PublicB64));
    }

    [TestMethod]
    public void Receipt_Verify_FailsWhenTampered()
    {
        var receiver = NewKeyPair();
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            "recv-dev", "dev-a", "epoch-1", 5,
            OnlineReplicationProtocol.HashText("cursor"), OnlineReplicationProtocol.HashText("batch"),
            receiver.PrivateB64);
        var tampered = receipt with { ThroughSeq = 6 };
        Assert.IsFalse(OnlineReplicationProtocol.VerifyReceipt(tampered, receiver.PublicB64));
    }

    [TestMethod]
    public void Receipt_Store_IsMonotonic()
    {
        using var db = MeshDb.Open(databasePath, key);
        var receiver = NewKeyPair();
        var high = OnlineReplicationProtocol.CreateReceipt("recv", "dev-a", "epoch-1", 10,
            OnlineReplicationProtocol.HashText("c10"), OnlineReplicationProtocol.HashText("b10"), receiver.PrivateB64);
        var low = OnlineReplicationProtocol.CreateReceipt("recv", "dev-a", "epoch-1", 4,
            OnlineReplicationProtocol.HashText("c4"), OnlineReplicationProtocol.HashText("b4"), receiver.PrivateB64);
        db.StoreReceipt(high);
        db.StoreReceipt(low);
        Assert.AreEqual(10UL, db.GetReceipt("recv", "dev-a", "epoch-1")!.ThroughSeq);
    }

    [TestMethod]
    public void Outbox_PersistedTransition_OnlyFromValidReceipt()
    {
        using var db = MeshDb.Open(databasePath, key);
        var origin = NewKeyPair();
        var receiver = NewKeyPair();
        for (ulong s = 1; s <= 3; s++)
            db.AppendLocalEventWithOutbox(MakeEvent(origin, "dev-a", "epoch-1", s), new[] { "bob" });

        var receipt = OnlineReplicationProtocol.CreateReceipt(
            "recv", "dev-a", "epoch-1", 2,
            OnlineReplicationProtocol.HashText("cursor"), OnlineReplicationProtocol.HashText("batch"),
            receiver.PrivateB64);
        var advanced = db.MarkOutboxPersistedFromReceipt(receipt, receiver.PublicB64, "bob");
        Assert.AreEqual(2, advanced);
        Assert.AreEqual(2, db.QueryDueOutbox("bob", MeshDb.OutboxStatePersisted).Count);
        Assert.AreEqual(1, db.QueryDueOutbox("bob", MeshDb.OutboxStatePending).Count);
    }

    [TestMethod]
    public void Outbox_PersistedTransition_RejectsInvalidReceipt()
    {
        using var db = MeshDb.Open(databasePath, key);
        var origin = NewKeyPair();
        var receiver = NewKeyPair();
        var attacker = NewKeyPair();
        db.AppendLocalEventWithOutbox(MakeEvent(origin, "dev-a", "epoch-1", 1), new[] { "bob" });
        var receipt = OnlineReplicationProtocol.CreateReceipt(
            "recv", "dev-a", "epoch-1", 1,
            OnlineReplicationProtocol.HashText("cursor"), OnlineReplicationProtocol.HashText("batch"),
            receiver.PrivateB64);
        Assert.ThrowsException<ArgumentException>(() =>
            db.MarkOutboxPersistedFromReceipt(receipt, attacker.PublicB64, "bob"));
    }

    // ==================================================================
    // I. Read watermarks (deterministic last-writer-wins).
    // ==================================================================

    [TestMethod]
    public void ReadWatermark_HigherVersion_Wins()
    {
        using var db = MeshDb.Open(databasePath, key);
        Assert.IsTrue(db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "e1", "d1", 1, 100)));
        Assert.IsTrue(db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "e2", "d2", 2, 90)));
        Assert.AreEqual("e2", db.GetReadWatermark("conv", "alice")!.ThroughEventId);
    }

    [TestMethod]
    public void ReadWatermark_LowerVersion_Ignored()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "e5", "d1", 5, 100));
        Assert.IsFalse(db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "e2", "d2", 2, 200)));
        Assert.AreEqual("e5", db.GetReadWatermark("conv", "alice")!.ThroughEventId);
    }

    [TestMethod]
    public void ReadWatermark_EqualVersion_TieBreaksDeterministically()
    {
        using var db = MeshDb.Open(databasePath, key);
        db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "aaa", "d1", 3, 100));
        Assert.IsTrue(db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "bbb", "d2", 3, 100)));
        Assert.AreEqual("bbb", db.GetReadWatermark("conv", "alice")!.ThroughEventId);
        Assert.IsFalse(db.UpsertReadWatermark(new ReadWatermarkPayload("conv", "alice", "aaa", "d3", 3, 100)));
    }

    [TestMethod]
    public void ReadWatermark_ParallelWriters_CannotRegress()
    {
        using var db = MeshDb.Open(databasePath, key);
        Parallel.For(1, 101, version =>
            db.UpsertReadWatermark(new ReadWatermarkPayload(
                "conv", "alice", $"event-{version:D3}", $"d-{version}", version, version)));

        var stored = db.GetReadWatermark("conv", "alice")!;
        Assert.AreEqual(100L, stored.Version);
        Assert.AreEqual("event-100", stored.ThroughEventId);
    }

    [TestMethod]
    public void CanonicalEncoding_DoesNotAllowFieldBoundaryAmbiguity()
    {
        var first = OnlineReplicationProtocol.EventCanonicalHeader(
            "device", "epoch", 1, "alice", 1,
            ReplicationOpKinds.Message, "entity", "a|b", "v", 100);
        var second = OnlineReplicationProtocol.EventCanonicalHeader(
            "device", "epoch", 1, "alice", 1,
            ReplicationOpKinds.Message, "entity|a", "b", "v", 100);

        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(
            OnlineReplicationProtocol.HashText(first),
            OnlineReplicationProtocol.HashText(second));
    }

    // ==================================================================
    // J. Custody hash chain.
    // ==================================================================

    private CustodyEntry Genesis(KeyPair signer, string handle, string device)
        => OnlineReplicationProtocol.CreateCustodyEntry(
            handle, 0, OnlineReplicationProtocol.ZeroHash, CustodyAction.Genesis,
            device, null, 1_700_000_000_000, signer.PublicB64, signer.PrivateB64);

    [TestMethod]
    public void Custody_Genesis_ValidatesAndVerifies()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        Assert.AreEqual(CustodyValidationResult.Valid, OnlineReplicationProtocol.ValidateCustodyAppend(null, genesis));
        Assert.IsTrue(OnlineReplicationProtocol.VerifyCustodyEntry(genesis, k.PublicB64));
    }

    [TestMethod]
    public void Custody_Genesis_RejectsNonZeroPrev()
    {
        var k = NewKeyPair();
        var bad = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice", 0, OnlineReplicationProtocol.HashText("x"), CustodyAction.Genesis,
            "device-a", null, 1, k.PublicB64, k.PrivateB64);
        Assert.AreEqual(CustodyValidationResult.InvalidGenesis, OnlineReplicationProtocol.ValidateCustodyAppend(null, bad));
    }

    [TestMethod]
    public void Custody_Genesis_RejectsNonZeroGeneration()
    {
        var k = NewKeyPair();
        var bad = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice", 1, OnlineReplicationProtocol.ZeroHash, CustodyAction.Genesis,
            "device-a", null, 1, k.PublicB64, k.PrivateB64);
        Assert.AreEqual(CustodyValidationResult.InvalidGenesis, OnlineReplicationProtocol.ValidateCustodyAppend(null, bad));
    }

    [TestMethod]
    public void Custody_AddDevice_LinksToHead()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        var add = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice", 1, genesis.EntryHash, CustodyAction.AddDevice,
            "device-b", null, 2, k.PublicB64, k.PrivateB64);
        Assert.AreEqual(CustodyValidationResult.Valid, OnlineReplicationProtocol.ValidateCustodyAppend(genesis, add));
    }

    [TestMethod]
    public void Custody_BrokenLink_IsRejected()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        var add = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice", 1, OnlineReplicationProtocol.HashText("wrong"), CustodyAction.AddDevice,
            "device-b", null, 2, k.PublicB64, k.PrivateB64);
        Assert.AreEqual(CustodyValidationResult.Fork, OnlineReplicationProtocol.ValidateCustodyAppend(genesis, add));
    }

    [TestMethod]
    public void Custody_HashTamper_IsDetected()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a") with { EntryHash = OnlineReplicationProtocol.HashText("fake") };
        Assert.AreEqual(CustodyValidationResult.HashMismatch, OnlineReplicationProtocol.ValidateCustodyAppend(null, genesis));
    }

    [TestMethod]
    public void Custody_FullChain_RekeyAndRemove_Validates()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        var add = OnlineReplicationProtocol.CreateCustodyEntry("alice", 1, genesis.EntryHash, CustodyAction.AddDevice,
            "device-b", null, 2, k.PublicB64, k.PrivateB64);
        var rekey = OnlineReplicationProtocol.CreateCustodyEntry("alice", 2, add.EntryHash, CustodyAction.RekeyRecovery,
            "device-a", "recovery-key", 3, k.PublicB64, k.PrivateB64);
        var remove = OnlineReplicationProtocol.CreateCustodyEntry("alice", 3, rekey.EntryHash, CustodyAction.RemoveDevice,
            "device-b", null, 4, k.PublicB64, k.PrivateB64);
        var chain = new[] { genesis, add, rekey, remove };
        Assert.AreEqual(CustodyValidationResult.Valid, OnlineReplicationProtocol.ValidateCustodyChain(chain));
        Assert.AreEqual(3L, OnlineReplicationProtocol.AuthGenerationOf(chain));
    }

    [TestMethod]
    public void Custody_ForkAtSameGeneration_IsDetected()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        var a = OnlineReplicationProtocol.CreateCustodyEntry("alice", 1, genesis.EntryHash, CustodyAction.AddDevice,
            "device-b", null, 2, k.PublicB64, k.PrivateB64);
        var b = OnlineReplicationProtocol.CreateCustodyEntry("alice", 1, genesis.EntryHash, CustodyAction.AddDevice,
            "device-c", null, 2, k.PublicB64, k.PrivateB64);
        Assert.IsTrue(OnlineReplicationProtocol.IsCustodyFork(a, b));
    }

    [TestMethod]
    public void Custody_DbAppend_Idempotent_And_ForkRejected()
    {
        using var db = MeshDb.Open(databasePath, key);
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        Assert.AreEqual(MeshDb.ReplicationAppendResult.Inserted, db.AppendCustodyEntry(genesis));
        Assert.AreEqual(MeshDb.ReplicationAppendResult.Duplicate, db.AppendCustodyEntry(genesis));

        var addB = OnlineReplicationProtocol.CreateCustodyEntry("alice", 1, genesis.EntryHash, CustodyAction.AddDevice,
            "device-b", null, 2, k.PublicB64, k.PrivateB64);
        db.AppendCustodyEntry(addB);
        var forkC = OnlineReplicationProtocol.CreateCustodyEntry("alice", 1, genesis.EntryHash, CustodyAction.AddDevice,
            "device-c", null, 2, k.PublicB64, k.PrivateB64);
        Assert.ThrowsException<MeshDb.ReplicationForkException>(() => db.AppendCustodyEntry(forkC));
        Assert.AreEqual(1L, db.GetCustodyHead("alice")!.Generation);
    }

    [TestMethod]
    public void Custody_RemoveDevicePolicy_ForbidsLastDevice()
    {
        var set = new[] { "device-a" };
        Assert.IsFalse(OnlineReplicationProtocol.CanRemoveDevice(set, "device-a"));
        var pair = new[] { "device-a", "device-b" };
        Assert.IsTrue(OnlineReplicationProtocol.CanRemoveDevice(pair, "device-a"));
        Assert.IsFalse(OnlineReplicationProtocol.CanRemoveDevice(pair, "device-unknown"));
    }

    // ==================================================================
    // K. Validators for batch/offer/request/flow.
    // ==================================================================

    [TestMethod]
    public void ValidateBatch_RejectsOverOpCount()
    {
        var k = NewKeyPair();
        var events = Enumerable.Range(1, OnlineReplicationLimits.MaxBatchOps + 1)
            .Select(i => MakeEvent(k, "dev-a", "epoch-1", (ulong)i)).ToList();
        var batch = new ReplicationBatch("dev-a", "epoch-1", events);
        Assert.IsFalse(OnlineReplicationProtocol.ValidateBatch(batch, out _));
    }

    [TestMethod]
    public void ValidateBatch_RejectsMixedOrigin()
    {
        var k = NewKeyPair();
        var events = new[] { MakeEvent(k, "dev-a", "epoch-1", 1), MakeEvent(k, "dev-b", "epoch-1", 1) };
        var batch = new ReplicationBatch("dev-a", "epoch-1", events);
        Assert.IsFalse(OnlineReplicationProtocol.ValidateBatch(batch, out _));
    }

    [TestMethod]
    public void ValidateBatch_AcceptsWellFormed()
    {
        var k = NewKeyPair();
        var events = new[] { MakeEvent(k, "dev-a", "epoch-1", 1), MakeEvent(k, "dev-a", "epoch-1", 2) };
        var batch = new ReplicationBatch("dev-a", "epoch-1", events);
        Assert.IsTrue(OnlineReplicationProtocol.ValidateBatch(batch, out _));
    }

    [TestMethod]
    public void ValidateRequest_RejectsTooManyRanges()
    {
        var ranges = Enumerable.Range(0, OnlineReplicationLimits.MaxRangeRequests + 1)
            .Select(i => new ReplicationRange((ulong)(i * 2 + 1), (ulong)(i * 2 + 1))).ToList();
        var request = new ReplicationRequest("dev-a", "epoch-1", ranges);
        Assert.IsFalse(OnlineReplicationProtocol.ValidateRequest(request, out _));
    }

    [TestMethod]
    public void ValidateRequest_RejectsOverlappingRanges()
    {
        var request = new ReplicationRequest("dev-a", "epoch-1",
            new[] { new ReplicationRange(1, 5), new ReplicationRange(4, 8) });
        Assert.IsFalse(OnlineReplicationProtocol.ValidateRequest(request, out _));
    }

    [TestMethod]
    public void ValidateRequest_AcceptsAscendingDisjoint()
    {
        var request = new ReplicationRequest("dev-a", "epoch-1",
            new[] { new ReplicationRange(1, 3), new ReplicationRange(5, 8) });
        Assert.IsTrue(OnlineReplicationProtocol.ValidateRequest(request, out _));
    }

    [TestMethod]
    public void ValidateOffer_RejectsInvertedBounds()
    {
        var offer = new ReplicationOffer("dev-a", "epoch-1", AvailableFrom: 10, AvailableThrough: 5);
        Assert.IsFalse(OnlineReplicationProtocol.ValidateOffer(offer, out _));
    }

    [TestMethod]
    public void ValidateFlow_RejectsOutOfBounds()
    {
        Assert.IsFalse(OnlineReplicationProtocol.ValidateFlow(
            new ReplicationFlow(-1, 10, 1000), out _));
        Assert.IsFalse(OnlineReplicationProtocol.ValidateFlow(
            new ReplicationFlow(1, OnlineReplicationLimits.MaxBatchOps + 1, 1000), out _));
    }

    [TestMethod]
    public void ValidateFlow_AcceptsWellFormed()
    {
        Assert.IsTrue(OnlineReplicationProtocol.ValidateFlow(
            new ReplicationFlow(8, 64, 1024), out _));
    }

    // ==================================================================
    // K2. Batch building and selection (OnlineReplicationState).
    // ==================================================================

    [TestMethod]
    public void BuildBatches_ChunksByOpCount()
    {
        var k = NewKeyPair();
        var events = Enumerable.Range(1, 200).Select(i => MakeEvent(k, "dev-a", "epoch-1", (ulong)i)).ToList();
        var flow = new ReplicationFlow(100, 64, OnlineReplicationLimits.MaxBatchBytes);
        var batches = OnlineReplicationState.BuildBatches("dev-a", "epoch-1", events, flow);
        Assert.AreEqual(4, batches.Count);
        Assert.AreEqual(64, batches[0].Events.Count);
        Assert.AreEqual(8, batches[3].Events.Count);
    }

    [TestMethod]
    public void BuildBatches_RespectsFlowCredits()
    {
        var k = NewKeyPair();
        var events = Enumerable.Range(1, 200).Select(i => MakeEvent(k, "dev-a", "epoch-1", (ulong)i)).ToList();
        var flow = new ReplicationFlow(2, 64, OnlineReplicationLimits.MaxBatchBytes);
        var batches = OnlineReplicationState.BuildBatches("dev-a", "epoch-1", events, flow);
        Assert.AreEqual(2, batches.Count);
    }

    [TestMethod]
    public void SelectForRequest_ReturnsOnlyRequestedSeqs()
    {
        var k = NewKeyPair();
        var events = Enumerable.Range(1, 10).Select(i => MakeEvent(k, "dev-a", "epoch-1", (ulong)i)).ToList();
        var request = new ReplicationRequest("dev-a", "epoch-1",
            new[] { new ReplicationRange(2, 3), new ReplicationRange(7, 9) });
        var selected = OnlineReplicationState.SelectForRequest(events, request);
        CollectionAssert.AreEqual(new ulong[] { 2, 3, 7, 8, 9 }, selected.Select(e => e.Seq).ToArray());
    }

    // ==================================================================
    // L. State tracking and receipts (OnlineReplicationState).
    // ==================================================================

    [TestMethod]
    public void State_TracksCursorsPerOrigin()
    {
        var state = new OnlineReplicationState();
        Assert.AreEqual(CursorApplyResult.AppliedContiguous, state.Apply("dev-a", "epoch-1", 1));
        Assert.AreEqual(CursorApplyResult.AppliedAhead, state.Apply("dev-a", "epoch-1", 3));
        Assert.AreEqual(1UL, state.GetCursor("dev-a").Contiguous);
        Assert.AreEqual(1, state.TrackedOriginCount);
    }

    [TestMethod]
    public void State_EnforcesTrackedOriginCap()
    {
        var state = new OnlineReplicationState();
        for (var i = 0; i < OnlineReplicationLimits.MaxTrackedOrigins; i++)
            state.Apply("dev-" + i, "epoch-1", 1);
        Assert.ThrowsException<InvalidOperationException>(() => state.Apply("dev-overflow", "epoch-1", 1));
    }

    [TestMethod]
    public void State_BuildReceipt_VerifiesUnderReceiverKey()
    {
        var receiver = NewKeyPair();
        var origin = NewKeyPair();
        var state = new OnlineReplicationState();
        state.Apply("dev-a", "epoch-1", 1);
        state.Apply("dev-a", "epoch-1", 2);
        var events = new[] { MakeEvent(origin, "dev-a", "epoch-1", 1), MakeEvent(origin, "dev-a", "epoch-1", 2) };
        var batch = new ReplicationBatch("dev-a", "epoch-1", events);
        var receipt = state.BuildReceipt("recv-dev", batch, receiver.PrivateB64);
        Assert.AreEqual(2UL, receipt.ThroughSeq);
        Assert.IsTrue(OnlineReplicationState.VerifyReceipt(receipt, receiver.PublicB64));
    }

    // ==================================================================
    // M. Session handshake.
    // ==================================================================

    [TestMethod]
    public void SessionInit_SignAndVerify_RoundTrips()
    {
        var k = NewKeyPair();
        var init = OnlineReplicationProtocol.CreateSessionInit(
            "sess-1", "dev-a", "dev-b", MeshCrypto.NewNonce(), OnlineReplicationProtocol.HashText("head"), 3, k.PrivateB64);
        Assert.IsTrue(OnlineReplicationProtocol.VerifySessionInit(init, k.PublicB64));
    }

    [TestMethod]
    public void SessionInit_Verify_FailsWhenNonceTampered()
    {
        var k = NewKeyPair();
        var init = OnlineReplicationProtocol.CreateSessionInit(
            "sess-1", "dev-a", "dev-b", MeshCrypto.NewNonce(), OnlineReplicationProtocol.HashText("head"), 3, k.PrivateB64);
        var tampered = init with { Nonce = MeshCrypto.NewNonce() };
        Assert.IsFalse(OnlineReplicationProtocol.VerifySessionInit(tampered, k.PublicB64));
    }

    [TestMethod]
    public void SessionAck_Verify_RequiresMatchingPeerNonce()
    {
        var k = NewKeyPair();
        var peerNonce = MeshCrypto.NewNonce();
        var ack = OnlineReplicationProtocol.CreateSessionAck(
            "sess-1", "dev-b", "dev-a", MeshCrypto.NewNonce(), peerNonce,
            OnlineReplicationProtocol.HashText("head"), 4, k.PrivateB64);
        Assert.IsTrue(OnlineReplicationProtocol.VerifySessionAck(ack, k.PublicB64, peerNonce));
        Assert.IsFalse(OnlineReplicationProtocol.VerifySessionAck(ack, k.PublicB64, MeshCrypto.NewNonce()));
    }

    // ==================================================================
    // N. Persistence across reopen and randomised property tests.
    // ==================================================================

    [TestMethod]
    public void Database_ReopenPreservesEventsAndCursors()
    {
        var k = NewKeyPair();
        var e = MakeEvent(k, "dev-a", "epoch-1", 1);
        var cursor = OnlineReplicationProtocol.EmptyCursor();
        OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1", 1, out cursor);
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.ApplyInboundEvent(e, cursor);
        }
        SqliteConnection.ClearAllPools();
        using (var reopened = MeshDb.Open(databasePath, key))
        {
            Assert.IsNotNull(reopened.GetEvent(e.EventId));
            Assert.AreEqual(1UL, reopened.GetCursor("dev-a")!.Contiguous);
        }
    }

    [TestMethod]
    public void Database_ReopenPreservesCustodyChain()
    {
        var k = NewKeyPair();
        var genesis = Genesis(k, "alice", "device-a");
        using (var db = MeshDb.Open(databasePath, key))
        {
            db.AppendCustodyEntry(genesis);
        }
        SqliteConnection.ClearAllPools();
        using (var reopened = MeshDb.Open(databasePath, key))
        {
            Assert.AreEqual(genesis.EntryHash, reopened.GetCustodyHead("alice")!.EntryHash);
        }
    }

    [TestMethod]
    public void Cursor_RandomisedPermutations_ConvergeToSameHead()
    {
        var rng = new Random(20260731);
        for (var trial = 0; trial < 25; trial++)
        {
            var n = rng.Next(5, 40);
            var seqs = Enumerable.Range(1, n).Select(i => (ulong)i).ToList();
            for (var i = seqs.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (seqs[i], seqs[j]) = (seqs[j], seqs[i]);
            }
            var cursor = OnlineReplicationProtocol.EmptyCursor();
            foreach (var s in seqs)
                OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1", s, out cursor);
            Assert.AreEqual((ulong)n, cursor.Contiguous, $"Trial {trial} did not converge.");
            Assert.AreEqual(0, OnlineReplicationProtocol.ComputeMissingRanges(cursor, (ulong)n).Count);
        }
    }

    [TestMethod]
    public void Cursor_RandomisedWithGaps_ReportsExactMissing()
    {
        var rng = new Random(42);
        for (var trial = 0; trial < 25; trial++)
        {
            var present = new SortedSet<ulong>();
            var max = (ulong)rng.Next(10, 60);
            var cursor = OnlineReplicationProtocol.EmptyCursor();
            for (ulong s = 1; s <= max; s++)
            {
                if (rng.NextDouble() < 0.6)
                {
                    present.Add(s);
                    OnlineReplicationProtocol.ApplyToCursor(cursor, "epoch-1", s, out cursor);
                }
            }
            var expectedMissing = Enumerable.Range(1, (int)max)
                .Select(i => (ulong)i)
                .Where(s => !present.Contains(s))
                .ToHashSet();
            var reported = new HashSet<ulong>();
            foreach (var r in OnlineReplicationProtocol.ComputeMissingRanges(cursor, max))
                for (var s = r.FromSeq; s <= r.ToSeq; s++) reported.Add(s);
            Assert.IsTrue(expectedMissing.SetEquals(reported), $"Trial {trial} mismatch.");
        }
    }

    // ==================================================================
    // O. Central-storage invariant contract.
    // ==================================================================

    [TestMethod]
    public void RelayDurableCategories_ForbidsPayloadCategories()
    {
        foreach (var forbidden in RelayDurableCategories.Forbidden)
        {
            Assert.IsTrue(RelayDurableCategories.IsForbidden(forbidden));
            Assert.IsFalse(RelayDurableCategories.IsAllowed(forbidden));
        }
    }

    [TestMethod]
    public void RelayDurableCategories_AllowsOnlyMetadata()
    {
        Assert.IsTrue(RelayDurableCategories.IsAllowed("presence"));
        Assert.IsTrue(RelayDurableCategories.IsAllowed("device_directory"));
        Assert.AreEqual(0, RelayDurableCategories.Allowed.Intersect(RelayDurableCategories.Forbidden).Count());
    }

    [TestMethod]
    public void RelaySendCodes_AreDistinctAndKnown()
    {
        var codes = new[]
        {
            OnlineRelaySendCodes.Delivered, OnlineRelaySendCodes.NotOnline,
            OnlineRelaySendCodes.TargetDeviceUnknown, OnlineRelaySendCodes.RateLimited,
            OnlineRelaySendCodes.TooLarge, OnlineRelaySendCodes.DeviceRevoked,
        };
        Assert.AreEqual(codes.Length, codes.Distinct().Count());
        Assert.IsTrue(codes.All(OnlineRelaySendCodes.IsKnown));
        Assert.IsFalse(OnlineRelaySendCodes.IsKnown("made_up"));
    }

    [TestMethod]
    public void ProtocolVersion_IsNine()
    {
        Assert.AreEqual(9, MeshProtocol.Version);
        Assert.AreEqual(9, OnlineReplicationProtocol.CanonicalVersion);
    }

    // ------------------------------------------------------------------

    private List<string> PragmaColumns(string table)
    {
        // The database file is the source of truth; read the schema back through a fresh
        // connection so the assertion is behavioural rather than reflective.
        SQLitePCL.Batteries_V2.Init();
        var columns = new List<string>();
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using (var keyCmd = conn.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
            keyCmd.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read()) columns.Add(r.GetString(1));
        return columns;
    }
}
