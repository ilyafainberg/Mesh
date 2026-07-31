using System.Security.Cryptography;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Mesh 1.17 device-sync tests: desktop-only asset routing, ask-user cross-platform routing,
/// deterministic/idempotent operation ids, equal-version conflict resolution, durable outbox
/// retry, snapshot paging bounds, first-writer prompt convergence, payload validation and the
/// ask-user deep link. Pure helpers (Mesh.Shared) are exercised directly; store behaviours run
/// against a real encrypted MeshDb, mirroring the foundation tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Mesh117SyncTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    private const string Windows = "windows";
    private const string MacOS = "macos";
    private const string Android = "android";
    private const string IOS = "ios";
    private const string Unknown = "who-knows";

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "mesh-117-sync-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    // ------------------------------------------------------------------
    // Asset routing policy: desktop <-> desktop only.
    // ------------------------------------------------------------------

    [TestMethod]
    public void AssetRoute_DesktopToDesktop_IsAllowed()
    {
        Assert.IsTrue(AssetSyncPolicy.IsAllowed(Windows, Windows, isAssetOperation: true));
        Assert.IsTrue(AssetSyncPolicy.IsAllowed(Windows, MacOS, isAssetOperation: true));
        Assert.IsTrue(AssetSyncPolicy.IsAllowed(MacOS, Windows, isAssetOperation: true));
        Assert.IsTrue(AssetSyncPolicy.IsAllowed(MacOS, MacOS, isAssetOperation: true));
    }

    [TestMethod]
    public void AssetRoute_AnyMobileOrUnknown_SourceOrTarget_IsDenied()
    {
        string?[] nonDesktop = [Android, IOS, Unknown, null, ""];
        string?[] all = [Windows, MacOS, Android, IOS, Unknown, null, ""];

        // Every combination where either endpoint is not a desktop must be denied.
        foreach (var source in all)
        foreach (var target in all)
        {
            var bothDesktop = DevicePlatforms.IsDesktop(source) && DevicePlatforms.IsDesktop(target);
            var allowed = AssetSyncPolicy.IsAllowed(source, target, isAssetOperation: true);
            Assert.AreEqual(bothDesktop, allowed,
                $"Asset route {source ?? "null"} -> {target ?? "null"} allowed={allowed}");
        }

        // Explicit spot-checks: mobile as source and as target are both denied.
        foreach (var mobile in nonDesktop)
        {
            Assert.IsFalse(AssetSyncPolicy.IsAllowed(mobile, Windows, isAssetOperation: true),
                $"{mobile ?? "null"} source -> desktop must be denied.");
            Assert.IsFalse(AssetSyncPolicy.IsAllowed(Windows, mobile, isAssetOperation: true),
                $"desktop -> {mobile ?? "null"} target must be denied.");
        }
    }

    // ------------------------------------------------------------------
    // Ask-user routing: reaches every device including mobile.
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUserRoute_NonAssetOperation_ReachesEveryPlatform()
    {
        // Ask-user operations are not asset operations, so the desktop-only policy never restricts
        // them: mobile is an eligible source and target.
        string?[] all = [Windows, MacOS, Android, IOS, Unknown, null, ""];
        foreach (var source in all)
        foreach (var target in all)
            Assert.IsTrue(AssetSyncPolicy.IsAllowed(source, target, isAssetOperation: false),
                $"Ask-user route {source ?? "null"} -> {target ?? "null"} must be allowed.");
    }

    // ------------------------------------------------------------------
    // Deterministic / idempotent operation ids.
    // ------------------------------------------------------------------

    [TestMethod]
    public void AssetOperationId_IsStableForSameLogicalOperation()
    {
        var a = AssetOperationId.Create(AssetKind.Skill, "s-1", "upsert", 3, "dev-1");
        var b = AssetOperationId.Create(AssetKind.Skill, "s-1", "upsert", 3, "dev-1");
        Assert.AreEqual(a, b, "The same logical asset operation must yield the same id (idempotent).");

        var newer = AssetOperationId.Create(AssetKind.Skill, "s-1", "upsert", 4, "dev-1");
        Assert.AreNotEqual(a, newer, "A newer version must yield a different id.");

        var deleted = AssetOperationId.Create(AssetKind.Skill, "s-1", "delete", 3, "dev-1");
        Assert.AreNotEqual(a, deleted, "A different operation must yield a different id.");
    }

    [TestMethod]
    public void Mesh117OperationId_Stable_IsDeterministicAndSensitiveToParts()
    {
        var a = Mesh117OperationId.Stable(Mesh117SyncKinds.AskUserResolution, "p-1", "dev-a", "p-1:yes");
        var b = Mesh117OperationId.Stable(Mesh117SyncKinds.AskUserResolution, "p-1", "dev-a", "p-1:yes");
        Assert.AreEqual(a, b, "Identical parts must hash to the same id.");

        var differentResolver =
            Mesh117OperationId.Stable(Mesh117SyncKinds.AskUserResolution, "p-1", "dev-b", "p-1:yes");
        Assert.AreNotEqual(a, differentResolver, "A different resolver must yield a different id.");

        // Length-prefixing prevents a boundary collision between adjacent parts.
        var left = Mesh117OperationId.Stable("k", "ab", "c");
        var right = Mesh117OperationId.Stable("k", "a", "bc");
        Assert.AreNotEqual(left, right, "Length-prefixed parts must not collide across boundaries.");
    }

    [TestMethod]
    public void Mesh117SyncKinds_ClassifyOperationKinds()
    {
        Assert.IsTrue(Mesh117SyncKinds.IsAssetKind(Mesh117SyncKinds.AssetUpsert));
        Assert.IsTrue(Mesh117SyncKinds.IsAssetKind(Mesh117SyncKinds.AssetDelete));
        Assert.IsFalse(Mesh117SyncKinds.IsAssetKind(Mesh117SyncKinds.AskUserPrompt));

        Assert.IsTrue(Mesh117SyncKinds.IsAskUserKind(Mesh117SyncKinds.AskUserPrompt));
        Assert.IsTrue(Mesh117SyncKinds.IsAskUserKind(Mesh117SyncKinds.AskUserResolution));
        Assert.IsFalse(Mesh117SyncKinds.IsAskUserKind(Mesh117SyncKinds.AssetUpsert));

        Assert.IsTrue(Mesh117SyncKinds.IsOperationKind(Mesh117SyncKinds.AssetUpsert));
        Assert.IsTrue(Mesh117SyncKinds.IsOperationKind(Mesh117SyncKinds.AskUserResolution));
        Assert.IsFalse(Mesh117SyncKinds.IsOperationKind("device.sync.operation"));
        Assert.IsFalse(Mesh117SyncKinds.IsOperationKind(null));
    }

    // ------------------------------------------------------------------
    // Deterministic equal-version conflict resolution.
    // ------------------------------------------------------------------

    [TestMethod]
    public void AssetConflict_EqualVersion_IsDeterministicAndIdempotent()
    {
        var local = MakeSkill("c-1", version: 4, sourceDeviceId: "dev-local");
        var remoteSame = MakeSkill("c-1", version: 4, sourceDeviceId: "dev-local");

        // An identical equal-version record never re-wins: apply is a no-op (idempotent).
        Assert.IsFalse(AssetConflict.RemoteWins(local, remoteSame),
            "An identical equal-version remote must not supersede local (idempotent).");

        // Whatever the tie-break, it is deterministic and antisymmetric across two different sources.
        var remoteOther = MakeSkill("c-1", version: 4, sourceDeviceId: "dev-remote");
        var forward = AssetConflict.RemoteWins(local, remoteOther);
        var backward = AssetConflict.RemoteWins(remoteOther, local);
        Assert.AreNotEqual(forward, backward,
            "Equal-version tie-break must be deterministic and antisymmetric between distinct sources.");
    }

    [TestMethod]
    public void ApplyRemoteUpsert_EqualVersionDuplicate_IsIdempotentNoOp()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var content = "payload"u8.ToArray();
        var record = MakeSkillWithContent("dup-1", version: 2, content, sourceDeviceId: "dev-a");

        var first = store.ApplyRemoteUpsertAsync(record, content).GetAwaiter().GetResult();
        Assert.IsTrue(first, "First remote upsert applies.");

        // Duplicate delivery of the exact same version/source is an idempotent no-op.
        var second = store.ApplyRemoteUpsertAsync(record, content).GetAwaiter().GetResult();
        Assert.IsFalse(second, "Duplicate equal-version remote upsert must be a no-op.");
    }

    [TestMethod]
    public void ApplyRemoteUpsert_LocalOnly_RejectsRemote()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var content = "local"u8.ToArray();
        var localOnly = MakeSkillWithContent("lo-1", version: 1, content, sourceDeviceId: "dev-a")
            with { LocalOnly = true };
        store.UpsertAsync(localOnly, content, createOutboxEntry: false).GetAwaiter().GetResult();

        var remote = MakeSkillWithContent("lo-1", version: 5, content, sourceDeviceId: "dev-b");
        var applied = store.ApplyRemoteUpsertAsync(remote, content).GetAwaiter().GetResult();
        Assert.IsFalse(applied, "A LocalOnly asset must reject remote operations even at a higher version.");
    }

    // ------------------------------------------------------------------
    // Durable outbox: success removes, failure retains for retry.
    // ------------------------------------------------------------------

    [TestMethod]
    public void Outbox_MarkFailure_RetainsEntryForRetry()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        store.UpsertAsync(MakeSkill("ob-1", 1), [1], createOutboxEntry: true).GetAwaiter().GetResult();

        var item = store.DequeueOutboxAsync(5).GetAwaiter().GetResult()[0];
        store.MarkOutboxAttemptAsync(item.OperationId, success: false, "target deferred")
            .GetAwaiter().GetResult();

        var remaining = store.ListOutboxAsync(5).GetAwaiter().GetResult();
        Assert.AreEqual(1, remaining.Count, "A failed enqueue must retain the outbox entry for retry.");
        Assert.AreEqual("target deferred", remaining[0].LastError);
        Assert.IsTrue(remaining[0].Attempts >= 1, "A failed attempt must be counted.");

        // A later success clears it.
        store.MarkOutboxAttemptAsync(item.OperationId, success: true, null).GetAwaiter().GetResult();
        Assert.AreEqual(0, store.ListOutboxAsync(5).GetAwaiter().GetResult().Count,
            "A successful enqueue must remove the outbox entry.");
    }

    [TestMethod]
    public void Outbox_LocalOnlyMutation_NeverEnqueues()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var localOnly = MakeSkill("lo-out-1", 1) with { LocalOnly = true };
        store.UpsertAsync(localOnly, [1], createOutboxEntry: true).GetAwaiter().GetResult();
        Assert.AreEqual(0, store.ListOutboxAsync(5).GetAwaiter().GetResult().Count,
            "LocalOnly (mobile) assets must never enter the outbox.");
    }

    // ------------------------------------------------------------------
    // Snapshot paging bounds (10k / 100k) and clamps.
    // ------------------------------------------------------------------

    [TestMethod]
    public void SnapshotPlanner_PageCount_IsBoundedAndCorrect()
    {
        // 10k and 100k summaries at the max page size stay bounded to a small number of pages.
        Assert.AreEqual(20, Mesh117SnapshotPlanner.PageCount(10_000, Mesh117SnapshotPlanner.MaxPageSize));
        Assert.AreEqual(200, Mesh117SnapshotPlanner.PageCount(100_000, Mesh117SnapshotPlanner.MaxPageSize));

        // An oversized requested page size is clamped down to the store bound before counting.
        Assert.AreEqual(200, Mesh117SnapshotPlanner.PageCount(100_000, 10_000_000));

        // A partial final page is counted (ceil division).
        Assert.AreEqual(21, Mesh117SnapshotPlanner.PageCount(10_001, Mesh117SnapshotPlanner.MaxPageSize));

        // Empty stores need no pages.
        Assert.AreEqual(0, Mesh117SnapshotPlanner.PageCount(0, Mesh117SnapshotPlanner.MaxPageSize));
    }

    [TestMethod]
    public void SnapshotPlanner_ClampPageSize_StaysWithinBounds()
    {
        Assert.AreEqual(Mesh117SnapshotPlanner.MinPageSize, Mesh117SnapshotPlanner.ClampPageSize(0));
        Assert.AreEqual(Mesh117SnapshotPlanner.MinPageSize, Mesh117SnapshotPlanner.ClampPageSize(-5));
        Assert.AreEqual(250, Mesh117SnapshotPlanner.ClampPageSize(250));
        Assert.AreEqual(Mesh117SnapshotPlanner.MaxPageSize, Mesh117SnapshotPlanner.ClampPageSize(999_999));
    }

    [TestMethod]
    public void SnapshotPaging_WalksAllSummaries_WithoutLoadingAllAtOnce()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);

        const int total = 10_000;
        var summaries = Enumerable.Range(0, total)
            .Select(i => MakeSkill($"bulk-{i:D6}", 1))
            .ToList();
        db.BulkInsertAssetSummaries(summaries);

        const int pageSize = Mesh117SnapshotPlanner.MaxPageSize;
        var expectedPages = Mesh117SnapshotPlanner.PageCount(total, pageSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        var pages = 0;

        while (true)
        {
            var page = store.PageSummariesAsync(AssetKind.Skill, pageSize, afterId).GetAwaiter().GetResult();
            if (page.Count == 0) break;
            Assert.IsTrue(page.Count <= pageSize, "No page may exceed the bounded page size.");
            foreach (var record in page)
                Assert.IsTrue(seen.Add(record.Id), $"Summary {record.Id} paged more than once.");
            afterId = page[^1].Id;
            pages++;
            if (pages > expectedPages + 1) Assert.Fail("Paging did not terminate within the expected bound.");
        }

        Assert.AreEqual(total, seen.Count, "Paging must visit every summary exactly once.");
        Assert.AreEqual(expectedPages, pages, "Actual page count must match the planner's prediction.");
    }

    // ------------------------------------------------------------------
    // Per-envelope size guard: large content fails explicitly, never truncates.
    // ------------------------------------------------------------------

    [TestMethod]
    public void SnapshotPlanner_OversizedAssetContent_FailsExplicitly()
    {
        var ex = Assert.ThrowsException<Mesh117PayloadTooLargeException>(() =>
            Mesh117SnapshotPlanner.EnsureAssetContentFits(
                "big-1", Mesh117SnapshotPlanner.MaxAssetContentBytes + 1));
        StringAssert.Contains(ex.Message, "big-1");

        // At the limit it is accepted (boundary).
        Mesh117SnapshotPlanner.EnsureAssetContentFits("edge-1", Mesh117SnapshotPlanner.MaxAssetContentBytes);
    }

    [TestMethod]
    public void SnapshotPlanner_OversizedOperationPlaintext_FailsExplicitly()
    {
        Assert.ThrowsException<Mesh117PayloadTooLargeException>(() =>
            Mesh117SnapshotPlanner.EnsureOperationFits(
                "op-1", Mesh117SnapshotPlanner.MaxOperationPlaintextBytes + 1));

        Mesh117SnapshotPlanner.EnsureOperationFits("op-2", Mesh117SnapshotPlanner.MaxOperationPlaintextBytes);
    }

    // ------------------------------------------------------------------
    // Payload validation before apply.
    // ------------------------------------------------------------------

    [TestMethod]
    public void PayloadGuard_ValidateAsset_AcceptsMatchingHashAndCount()
    {
        var content = "hello mesh"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var payload = MakeAssetPayload("ok-1", version: 1, content, hash);

        var decoded = Mesh117PayloadGuard.ValidateAsset(payload);
        CollectionAssert.AreEqual(content, decoded, "The guard returns the decoded bytes once.");
    }

    [TestMethod]
    public void PayloadGuard_ValidateAsset_RejectsHashMismatch()
    {
        var content = "hello mesh"u8.ToArray();
        var wrongHash = Convert.ToHexString(SHA256.HashData("tampered"u8.ToArray())).ToLowerInvariant();
        var payload = MakeAssetPayload("bad-hash", version: 1, content, wrongHash);

        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidateAsset(payload));
    }

    [TestMethod]
    public void PayloadGuard_ValidateAsset_RejectsByteCountMismatch()
    {
        var content = "hello mesh"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var payload = MakeAssetPayload("bad-count", version: 1, content, hash) with { ContentByteCount = 999 };

        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidateAsset(payload));
    }

    [TestMethod]
    public void PayloadGuard_ValidateAsset_RejectsOversizedContent()
    {
        var content = new byte[Mesh117SnapshotPlanner.MaxAssetContentBytes + 1];
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var payload = MakeAssetPayload("too-big", version: 1, content, hash);

        Assert.ThrowsException<Mesh117PayloadTooLargeException>(() =>
            Mesh117PayloadGuard.ValidateAsset(payload));
    }

    [TestMethod]
    public void PayloadGuard_ValidateAsset_TombstoneMustNotCarryContent()
    {
        var payload = new Asset117Payload(
            Kind: nameof(AssetKind.Skill), Id: "t-1", Name: "T", MetadataJson: null,
            ContentMime: null, ContentHash: null, ContentByteCount: 0, Version: 2,
            SourceDeviceId: "dev-a", UpdatedAtUnixMs: 0, IsDeleted: true, LocalOnly: false,
            ContentBase64: Convert.ToBase64String("oops"u8.ToArray()));

        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidateAsset(payload));
    }

    [TestMethod]
    public void PayloadGuard_ValidatePrompt_EnforcesOptionRules()
    {
        var good = MakePromptPayload("p-1", ["yes", "no"]);
        Mesh117PayloadGuard.ValidatePrompt(good);

        var tooFew = MakePromptPayload("p-2", ["only"]);
        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidatePrompt(tooFew));

        var duplicate = MakePromptPayload("p-3", ["a", "a"]);
        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidatePrompt(duplicate));
    }

    [TestMethod]
    public void PayloadGuard_ValidateResolution_RequiresAllFields()
    {
        var good = new AskUser117ResolutionPayload("p-1", "yes", "dev-a", "p-1:yes", 0);
        Mesh117PayloadGuard.ValidateResolution(good);

        var missing = good with { Selection = "" };
        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidateResolution(missing));
    }

    [TestMethod]
    public void PayloadGuard_ResolutionPromptSnapshot_MustMatchSelection()
    {
        var prompt = MakePromptPayload("p-embedded", ["yes", "no"]);
        var good = new AskUser117ResolutionPayload(
            "p-embedded", "yes", "dev-a", "p-embedded:yes", 0, prompt);
        Mesh117PayloadGuard.ValidateResolution(good);

        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidateResolution(good with { PromptId = "other" }));
        Assert.ThrowsException<Mesh117PayloadInvalidException>(() =>
            Mesh117PayloadGuard.ValidateResolution(good with { Selection = "missing" }));
    }

    // ------------------------------------------------------------------
    // Ask-user deep link round trip.
    // ------------------------------------------------------------------

    [TestMethod]
    public void DeepLink_RoundTrips()
    {
        var link = Mesh117DeepLink.ForPrompt("prompt-42");
        Assert.AreEqual("mesh://ask/prompt-42", link);

        Assert.IsTrue(Mesh117DeepLink.TryParse(link, out var id));
        Assert.AreEqual("prompt-42", id);

        Assert.IsFalse(Mesh117DeepLink.TryParse("https://example.com/ask/x", out _));
        Assert.IsFalse(Mesh117DeepLink.TryParse("mesh://ask/", out _));
        Assert.IsFalse(Mesh117DeepLink.TryParse(null, out _));
    }

    // ------------------------------------------------------------------
    // Ask-user first-writer convergence across devices.
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_FirstResolutionWins_AllDevicesConverge()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("p-conv-1")).GetAwaiter().GetResult();

        // Two devices race to resolve the same prompt with different selections.
        var win = store.ResolveAsync("p-conv-1", "yes", "dev-a", "p-conv-1:yes").GetAwaiter().GetResult();
        var lose = store.ResolveAsync("p-conv-1", "no", "dev-b", "p-conv-1:no").GetAwaiter().GetResult();

        Assert.AreEqual(AskUserState.Resolved, win.State);
        Assert.AreEqual("yes", win.Selection, "The first writer's selection is the winner.");
        // The store is atomic first-writer-wins: the loser observes the SAME resolved winner.
        Assert.AreEqual("yes", lose.Selection, "All devices converge on the first writer's selection.");
        Assert.AreEqual("dev-a", lose.ResolutionDeviceId, "Convergence includes the winning resolver id.");
    }

    [TestMethod]
    public void AskUser_DuplicateResolution_IsIdempotent()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("p-dup-1")).GetAwaiter().GetResult();

        var first = store.ResolveAsync("p-dup-1", "yes", "dev-a", "p-dup-1:yes").GetAwaiter().GetResult();
        // Redelivery of the identical resolution converges on the same winner without error.
        var again = store.ResolveAsync("p-dup-1", "yes", "dev-a", "p-dup-1:yes").GetAwaiter().GetResult();

        Assert.AreEqual(first.Selection, again.Selection);
        Assert.AreEqual(first.ResolutionDeviceId, again.ResolutionDeviceId);
        Assert.AreEqual(AskUserState.Resolved, again.State);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static AssetRecord MakeSkill(string id, int version, string sourceDeviceId = "dev-1") =>
        new(
            Kind: AssetKind.Skill,
            Id: id,
            Name: $"Skill {id}",
            MetadataJson: null,
            ContentMime: "application/octet-stream",
            ContentHash: null,
            ContentByteCount: 0,
            Version: version,
            SourceDeviceId: sourceDeviceId,
            UpdatedAt: DateTimeOffset.UtcNow,
            IsDeleted: false,
            LocalOnly: false);

    private static AssetRecord MakeSkillWithContent(
        string id, int version, byte[] content, string sourceDeviceId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return MakeSkill(id, version, sourceDeviceId) with
        {
            ContentHash = hash,
            ContentByteCount = content.Length
        };
    }

    private static Asset117Payload MakeAssetPayload(string id, int version, byte[] content, string hash) =>
        new(
            Kind: nameof(AssetKind.Skill),
            Id: id,
            Name: $"Skill {id}",
            MetadataJson: null,
            ContentMime: "application/octet-stream",
            ContentHash: hash,
            ContentByteCount: content.Length,
            Version: version,
            SourceDeviceId: "dev-1",
            UpdatedAtUnixMs: 0,
            IsDeleted: false,
            LocalOnly: false,
            ContentBase64: Convert.ToBase64String(content));

    private static AskUser117PromptPayload MakePromptPayload(string id, string[] optionIds) =>
        new(
            PromptId: id,
            ThreadId: "thread-x",
            RunId: "run-x",
            Question: "Pick one",
            Options: optionIds.Select(o => new AskUser117Option(o, o.ToUpperInvariant(), null)).ToList(),
            RecommendedIndex: null,
            OriginDeviceId: "origin",
            CreatedAtUnixMs: 0,
            ExpiresAtUnixMs: null,
            Revision: 1,
            Version: 1,
            DeepLink: Mesh117DeepLink.ForPrompt(id));

    private static AskUserPrompt MakePendingPrompt(string id) =>
        new(
            PromptId: id,
            ThreadId: "thread-x",
            RunId: "run-x",
            Question: "Yes or no?",
            Options:
            [
                new AskUserOption("yes", "Yes", null),
                new AskUserOption("no", "No", null)
            ],
            RecommendedIndex: null,
            State: AskUserState.Pending,
            Selection: null,
            OriginDeviceId: "origin",
            ResolutionDeviceId: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: null,
            ResolvedAt: null,
            Revision: 1);
}
