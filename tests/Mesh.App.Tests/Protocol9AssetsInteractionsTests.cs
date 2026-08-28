using System.Security.Cryptography;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Protocol9FoundationTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "protocol9-foundation-tests",
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
    // Ask-user: validation
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_Validate_RejectsFewer_Than_2_Options()
    {
        var ex = Assert.ThrowsException<ArgumentException>(() =>
            AskUserPrompt.Validate([new AskUserOption("a", "A", null)], null));
        StringAssert.Contains(ex.Message, "2 and 5");
    }

    [TestMethod]
    public void AskUser_Validate_RejectsMore_Than_5_Options()
    {
        var options = Enumerable.Range(0, 6)
            .Select(i => new AskUserOption($"o{i}", $"Option {i}", null))
            .ToList();
        var ex = Assert.ThrowsException<ArgumentException>(() =>
            AskUserPrompt.Validate(options, null));
        StringAssert.Contains(ex.Message, "2 and 5");
    }

    [TestMethod]
    public void AskUser_Validate_AcceptsBoundaryOptionCounts()
    {
        // 2 options: minimum
        AskUserPrompt.Validate(
            [new AskUserOption("a", "A", null), new AskUserOption("b", "B", null)], null);

        // 5 options: maximum
        AskUserPrompt.Validate(
            Enumerable.Range(0, 5).Select(i => new AskUserOption($"o{i}", $"O{i}", null)).ToList(),
            null);
    }

    [TestMethod]
    public void AskUser_Validate_RejectsOutOfRangeRecommendedIndex()
    {
        var options = new List<AskUserOption>
        {
            new("a", "A", null),
            new("b", "B", null)
        };
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            AskUserPrompt.Validate(options, 2));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            AskUserPrompt.Validate(options, -1));
    }

    [TestMethod]
    public void AskUser_Validate_AcceptsValidRecommendedIndex()
    {
        var options = new List<AskUserOption>
        {
            new("a", "A", null),
            new("b", "B", null),
            new("c", "C", null)
        };
        // Index 0, 1, 2 should all be valid.
        for (int i = 0; i < options.Count; i++)
            AskUserPrompt.Validate(options, i);
    }

    // ------------------------------------------------------------------
    // Ask-user: DB round trip
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_DbRoundTrip_PersistsAllFields()
    {
        var created = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
        var expires = created.AddMinutes(30);
        var options = new List<AskUserOption>
        {
            new("yes", "Yes", "Approve"),
            new("no", "No", "Reject"),
            new("defer", "Later", null)
        };
        var prompt = new AskUserPrompt(
            PromptId: "p-roundtrip-1",
            ThreadId: "thread-1",
            RunId: "run-1",
            Question: "Do you approve?",
            Options: options,
            RecommendedIndex: 0,
            State: AskUserState.Pending,
            Selection: null,
            OriginDeviceId: "dev-origin",
            ResolutionDeviceId: null,
            CreatedAt: created,
            ExpiresAt: expires,
            ResolvedAt: null,
            Revision: 1);

        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);

        var created2 = store.CreateAsync(prompt).GetAwaiter().GetResult();
        Assert.AreEqual(prompt.PromptId, created2.PromptId);

        var loaded = store.GetAsync("p-roundtrip-1").GetAwaiter().GetResult();
        Assert.IsNotNull(loaded);
        Assert.AreEqual("thread-1", loaded.ThreadId);
        Assert.AreEqual("run-1", loaded.RunId);
        Assert.AreEqual("Do you approve?", loaded.Question);
        Assert.AreEqual(3, loaded.Options.Count);
        Assert.AreEqual("defer", loaded.Options[2].Id);
        Assert.AreEqual(0, loaded.RecommendedIndex);
        Assert.AreEqual(AskUserState.Pending, loaded.State);
        Assert.IsNull(loaded.Selection);
        Assert.AreEqual("dev-origin", loaded.OriginDeviceId);
        Assert.AreEqual(created.UtcTicks, loaded.CreatedAt.UtcTicks);
        Assert.AreEqual(expires.UtcTicks, loaded.ExpiresAt!.Value.UtcTicks);
    }

    // ------------------------------------------------------------------
    // Ask-user: atomic double resolution
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_AtomicResolution_FirstWriterWins()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        var prompt = MakePendingPrompt("p-atomic-1");
        store.CreateAsync(prompt).GetAwaiter().GetResult();

        // First resolution wins.
        var r1 = store.ResolveAsync("p-atomic-1", "yes", "dev-a", "tok-a").GetAwaiter().GetResult();
        // Second resolution with a different selection and token is rejected by the fence.
        var r2 = store.ResolveAsync("p-atomic-1", "no", "dev-b", "tok-b").GetAwaiter().GetResult();

        Assert.AreEqual(AskUserState.Resolved, r1.State);
        Assert.AreEqual("yes", r1.Selection);

        // Loser receives the current (winner's) state, not its own selection.
        Assert.AreEqual(AskUserState.Resolved, r2.State);
        Assert.AreEqual("yes", r2.Selection);
        Assert.AreEqual("dev-a", r2.ResolutionDeviceId);
    }

    // ------------------------------------------------------------------
    // Ask-user: idempotent same-token resolution
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_IdempotentSameToken_ReturnsSameState()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        var prompt = MakePendingPrompt("p-idem-1");
        store.CreateAsync(prompt).GetAwaiter().GetResult();

        var r1 = store.ResolveAsync("p-idem-1", "yes", "dev-a", "idem-token").GetAwaiter().GetResult();
        var r2 = store.ResolveAsync("p-idem-1", "yes", "dev-a", "idem-token").GetAwaiter().GetResult();

        Assert.AreEqual(AskUserState.Resolved, r1.State);
        Assert.AreEqual("yes", r1.Selection);
        Assert.AreEqual(AskUserState.Resolved, r2.State);
        Assert.AreEqual("yes", r2.Selection);
    }

    // ------------------------------------------------------------------
    // Ask-user: expire and cancel
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_Expire_SetsExpiredState()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("p-expire-1")).GetAwaiter().GetResult();
        var expired = store.ExpireAsync("p-expire-1").GetAwaiter().GetResult();
        Assert.AreEqual(AskUserState.Expired, expired.State);
    }

    [TestMethod]
    public void AskUser_Cancel_SetsCancelledState()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("p-cancel-1")).GetAwaiter().GetResult();
        var cancelled = store.CancelAsync("p-cancel-1").GetAwaiter().GetResult();
        Assert.AreEqual(AskUserState.Cancelled, cancelled.State);
    }

    // ------------------------------------------------------------------
    // Ask-user: suspended context expiry and resume
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_SuspendedContext_ExpiryAndResume()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var ctx = new SuspendedAgentContext(
            ContextId: "ctx-1",
            PromptId: "p-1",
            ThreadId: "t-1",
            RunId: "r-1",
            ContextJson: """{"step":42,"payload":"abc"}""",
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: expires,
            ResumedAt: null);

        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.SaveSuspendedContextAsync(ctx).GetAwaiter().GetResult();

        var loaded = store.GetSuspendedContextAsync("ctx-1").GetAwaiter().GetResult();
        Assert.IsNotNull(loaded);
        Assert.AreEqual("""{"step":42,"payload":"abc"}""", loaded.ContextJson);
        Assert.IsNotNull(loaded.ExpiresAt);
        Assert.IsNull(loaded.ResumedAt);

        var resumedAt = DateTimeOffset.UtcNow;
        store.MarkContextResumedAsync("ctx-1", resumedAt).GetAwaiter().GetResult();

        var resumed = store.GetSuspendedContextAsync("ctx-1").GetAwaiter().GetResult();
        Assert.IsNotNull(resumed);
        Assert.IsNotNull(resumed.ResumedAt);
        Assert.AreEqual(resumedAt.UtcTicks, resumed.ResumedAt!.Value.UtcTicks);
    }

    // ------------------------------------------------------------------
    // Asset: upsert/list does not retrieve content
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_PageSummaries_DoesNotReturnContentBytes()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var content = new byte[1024];
        Random.Shared.NextBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var summary = new AssetRecord(AssetKind.Skill, "skill-1", "My Skill", null,
            "application/octet-stream", hash, content.Length, 1, "dev-1",
            DateTimeOffset.UtcNow, false, false);
        store.UpsertAsync(summary, content).GetAwaiter().GetResult();

        var page = store.PageSummariesAsync(AssetKind.Skill, 10, null).GetAwaiter().GetResult();

        Assert.AreEqual(1, page.Count);
        Assert.AreEqual("skill-1", page[0].Id);
        Assert.AreEqual(content.Length, page[0].ContentByteCount);
        Assert.AreEqual(hash, page[0].ContentHash);
        // Summaries carry byte count and hash but NOT the raw bytes.
        // GetFullAsset is required to load content.
    }

    // ------------------------------------------------------------------
    // Asset: content hash round trip
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_ContentHash_RoundTrip()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var content = "Hello, Protocol 9!"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var summary = new AssetRecord(AssetKind.Knowledge, "kb-1", "Doc", null,
            "text/plain", hash, content.Length, 1, "dev-1",
            DateTimeOffset.UtcNow, false, false);
        store.UpsertAsync(summary, content).GetAwaiter().GetResult();

        var full = store.GetFullAssetAsync(AssetKind.Knowledge, "kb-1").GetAwaiter().GetResult();
        Assert.IsNotNull(full);
        Assert.AreEqual("kb-1", full.Value.Summary.Id);
        Assert.AreEqual(hash, full.Value.Summary.ContentHash);
        CollectionAssert.AreEqual(content, full.Value.Content);
        Assert.AreEqual(
            hash,
            Convert.ToHexString(SHA256.HashData(full.Value.Content)).ToLowerInvariant());
    }

    // ------------------------------------------------------------------
    // Asset: update / delete tombstone
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_Delete_SetsTombstone()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var summary = MakeSkillRecord("w-1", 1);
        store.UpsertAsync(summary, [1, 2, 3]).GetAwaiter().GetResult();
        var tombstone = store.DeleteAsync(AssetKind.Skill, "w-1", "dev-1")
            .GetAwaiter().GetResult();
        Assert.IsTrue(tombstone.IsDeleted);
        Assert.AreEqual(2, tombstone.Version, "Tombstone must be existing version + 1.");

        var full = store.GetFullAssetAsync(AssetKind.Skill, "w-1").GetAwaiter().GetResult();
        Assert.IsNotNull(full);
        Assert.IsTrue(full.Value.Summary.IsDeleted, "Expected tombstone.");
    }

    // ------------------------------------------------------------------
    // Asset: outbox transactional creation
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Asset: LocalOnly never produces outbox
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Asset: remote version ordering
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_ApplyRemoteUpsert_RejectsOlderVersion()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var v2 = MakeSkillRecord("ver-1", 2);
        store.UpsertAsync(v2, [2]).GetAwaiter().GetResult();

        var v1 = MakeSkillRecord("ver-1", 1);
        bool applied = store.ApplyRemoteUpsertAsync(v1, [1]).GetAwaiter().GetResult();
        Assert.IsFalse(applied, "Version 1 should be rejected when version 2 is stored.");

        var loaded = store.PageSummariesAsync(AssetKind.Skill, 1, null).GetAwaiter().GetResult();
        Assert.AreEqual(2, loaded[0].Version);
    }

    [TestMethod]
    public void Asset_ApplyRemoteUpsert_AcceptsNewerVersion()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var v1 = MakeSkillRecord("ver-2", 1);
        store.UpsertAsync(v1, [1]).GetAwaiter().GetResult();

        var v3 = MakeSkillRecord("ver-2", 3);
        bool applied = store.ApplyRemoteUpsertAsync(v3, [3]).GetAwaiter().GetResult();
        Assert.IsTrue(applied);

        var loaded = store.PageSummariesAsync(AssetKind.Skill, 1, null).GetAwaiter().GetResult();
        Assert.AreEqual(3, loaded[0].Version);
    }

    [TestMethod]
    public void Asset_ApplyRemoteDelete_RejectsLowerVersion()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var v5 = MakeSkillRecord("del-ver-1", 5);
        store.UpsertAsync(v5, []).GetAwaiter().GetResult();

        var tombstone = MakeSkillRecord("del-ver-1", 3) with { IsDeleted = true };
        bool applied = store.ApplyRemoteDeleteAsync(tombstone)
            .GetAwaiter().GetResult();
        Assert.IsFalse(applied);

        var full = store.GetFullAssetAsync(AssetKind.Skill, "del-ver-1").GetAwaiter().GetResult();
        Assert.IsNotNull(full);
        Assert.IsFalse(full!.Value.Summary.IsDeleted);
    }

    // ------------------------------------------------------------------
    // Asset: pagination
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_Pagination_ReturnsCorrectPage()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);

        for (int i = 0; i < 10; i++)
            store.UpsertAsync(MakeSkillRecord($"pg-{i:D3}", 1), [])
                .GetAwaiter().GetResult();

        var page1 = store.PageSummariesAsync(AssetKind.Skill, 4, null)
            .GetAwaiter().GetResult();
        Assert.AreEqual(4, page1.Count);
        Assert.AreEqual("pg-000", page1[0].Id);
        Assert.AreEqual("pg-003", page1[3].Id);

        var page2 = store.PageSummariesAsync(AssetKind.Skill, 4, page1[3].Id)
            .GetAwaiter().GetResult();
        Assert.AreEqual(4, page2.Count);
        Assert.AreEqual("pg-004", page2[0].Id);
    }

    // ------------------------------------------------------------------
    // Asset: 10k summary rows - bounded, content not loaded
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_10kSummaryRows_BoundedAndContentNotLoaded()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);

        // Build 10k summary records efficiently in a single transaction.
        var summaries = Enumerable.Range(0, 10_000)
            .Select(i => MakeSkillRecord($"bulk-{i:D5}", 1, localOnly: false))
            .ToList();
        db.BulkInsertAssetSummaries(summaries);

        // A page of 50 must return exactly 50, not 10k.
        var page = store.PageSummariesAsync(AssetKind.Skill, 50, null)
            .GetAwaiter().GetResult();
        Assert.AreEqual(50, page.Count);

        // Summaries should report byte count (0 since BulkInsert stores no content) not bytes.
        foreach (var s in page)
            Assert.AreEqual(0L, s.ContentByteCount);
    }

    // ------------------------------------------------------------------
    // Asset: outbox dequeue / mark
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // AssetSyncPolicy matrix
    // ------------------------------------------------------------------

    [TestMethod]
    public void AssetSyncPolicy_DesktopToDesktop_AllowsAssets()
    {
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, DevicePlatforms.MacOS, true));
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.MacOS, DevicePlatforms.Windows, true));
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, DevicePlatforms.Windows, true));
    }

    [TestMethod]
    public void AssetSyncPolicy_DesktopToMobile_DeniesAssets()
    {
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, DevicePlatforms.Android, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, DevicePlatforms.IOS, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.MacOS, DevicePlatforms.Android, true));
    }

    [TestMethod]
    public void AssetSyncPolicy_MobileToDesktop_DeniesAssets()
    {
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Android, DevicePlatforms.Windows, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.IOS, DevicePlatforms.MacOS, true));
    }

    [TestMethod]
    public void AssetSyncPolicy_MobileToMobile_DeniesAssets()
    {
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Android, DevicePlatforms.IOS, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.IOS, DevicePlatforms.Android, true));
    }

    [TestMethod]
    public void AssetSyncPolicy_UnknownPlatform_DeniesAssets()
    {
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Unknown, DevicePlatforms.Windows, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, DevicePlatforms.Unknown, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(null, DevicePlatforms.Windows, true));
        Assert.IsFalse(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, null, true));
    }

    [TestMethod]
    public void AssetSyncPolicy_NonAssetOperation_AlwaysAllowed()
    {
        // Non-asset operations are not restricted by platform.
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Android, DevicePlatforms.IOS, false));
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(null, null, false));
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Unknown, DevicePlatforms.Unknown, false));
        Assert.IsTrue(
            AssetSyncPolicy.IsAllowed(DevicePlatforms.Windows, DevicePlatforms.Android, false));
    }

    // ------------------------------------------------------------------
    // Schema: idempotent creation and existing DB remains readable
    // ------------------------------------------------------------------

    [TestMethod]
    public void Schema_AssetsInteractions_IdempotentAndExistingDbReadable()
    {
        // First open creates all tables.
        using (var db = MeshDb.Open(databasePath, key))
        {
            var store = new AssetStore(db);
            store.UpsertAsync(MakeSkillRecord("idem-1", 1), [42])
                .GetAwaiter().GetResult();
        }
        SqliteConnection.ClearAllPools();

        // Second open calls CreateAssetsInteractionsSchema again (idempotent IF NOT EXISTS).
        using (var db2 = MeshDb.Open(databasePath, key))
        {
            var store2 = new AssetStore(db2);
            var page = store2.PageSummariesAsync(AssetKind.Skill, 10, null)
                .GetAwaiter().GetResult();
            Assert.AreEqual(1, page.Count, "Existing row must survive re-open.");
            Assert.AreEqual("idem-1", page[0].Id);
        }
    }

    // ------------------------------------------------------------------
    // ProfilePersistenceCoordinator
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task ProfileCoordinator_WritesOffCallerThread()
    {
        var callerContext = new SynchronizationContext();
        SynchronizationContext? writerContext = null;
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, ct) =>
            {
                writerContext = SynchronizationContext.Current;
                tcs.TrySetResult(true);
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero);

        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            coordinator.Schedule("payload");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.FlushAsync();

        Assert.AreNotSame(
            callerContext,
            writerContext,
            "Save delegate must not inherit the caller synchronization context.");
    }

    [TestMethod]
    public async Task ProfileCoordinator_Coalesces100RapidRevisions()
    {
        int writeCount = 0;
        string? lastSeen = null;

        await using var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, ct) =>
            {
                Interlocked.Increment(ref writeCount);
                lastSeen = snapshot;
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.FromMilliseconds(50));

        for (int i = 0; i < 100; i++)
            coordinator.Schedule($"revision-{i}");

        await coordinator.FlushAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.IsTrue(writeCount < 100,
            $"Expected fewer than 100 writes due to coalescing, got {writeCount}.");
        Assert.AreEqual("revision-99", lastSeen,
            "The last written snapshot must be the latest revision.");
    }

    [TestMethod]
    public async Task ProfileCoordinator_SerializedSingleWriter()
    {
        int concurrent = 0;
        int maxConcurrent = 0;

        await using var coordinator = new ProfilePersistenceCoordinator<int>(
            async (snapshot, ct) =>
            {
                int c = Interlocked.Increment(ref concurrent);
                int prev = maxConcurrent;
                while (c > prev)
                {
                    prev = Interlocked.CompareExchange(ref maxConcurrent, c, prev);
                }
                await Task.Delay(5, ct).ConfigureAwait(false);
                Interlocked.Decrement(ref concurrent);
            },
            debounce: TimeSpan.Zero);

        for (int i = 0; i < 20; i++)
        {
            coordinator.Schedule(i);
            await Task.Delay(2);
        }
        await coordinator.FlushAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.AreEqual(1, maxConcurrent,
            "The save delegate must never be invoked concurrently.");
    }

    [TestMethod]
    public async Task ProfileCoordinator_FlushObservesLatestRevision()
    {
        string? lastSeen = null;

        await using var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, ct) =>
            {
                lastSeen = snapshot;
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero);

        coordinator.Schedule("first");
        coordinator.Schedule("second");
        coordinator.Schedule("latest");
        await coordinator.FlushAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.AreEqual("latest", lastSeen);
    }

    [TestMethod]
    public async Task ProfileCoordinator_FailurePropagates_And_LastError_Set()
    {
        var boom = new InvalidOperationException("disk full");

        var coordinator = new ProfilePersistenceCoordinator<string>(
            (_, _) => throw boom,
            debounce: TimeSpan.Zero);

        coordinator.Schedule("any");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.FlushAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        Assert.IsNotNull(coordinator.LastError);
        Assert.AreSame(boom, ex.InnerException);

        var disposeEx = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.DisposeAsync().AsTask());
        Assert.IsNotNull(disposeEx.InnerException);
    }

    [TestMethod]
    public async Task ProfileCoordinator_DisposeTimeout_DoesNotReportSuccess()
    {
        var coordinator = new ProfilePersistenceCoordinator<string>(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero,
            disposeTimeout: TimeSpan.FromMilliseconds(50));

        coordinator.Schedule("blocked");

        // Give the worker time to enter the save before disposal starts its timeout.
        await Task.Delay(20);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.IsInstanceOfType<TimeoutException>(ex.InnerException);
        Assert.IsInstanceOfType<TimeoutException>(coordinator.LastError);
    }

    [TestMethod]
    public async Task ProfileCoordinator_DisposeAsync_FlushesPending()
    {
        string? lastSeen = null;

        var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, ct) =>
            {
                lastSeen = snapshot;
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero);

        coordinator.Schedule("before-dispose");
        await Task.Delay(50); // allow debounce window to pass
        await coordinator.DisposeAsync();

        // After disposal the value written before the CTS was cancelled should be visible.
        Assert.AreEqual("before-dispose", lastSeen);
    }

    [TestMethod]
    public void ProfileCoordinator_ScheduleAfterDispose_Throws()
    {
        var coordinator = new ProfilePersistenceCoordinator<string>(
            (_, _) => Task.CompletedTask, debounce: TimeSpan.Zero);
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert.ThrowsException<ObjectDisposedException>(() => coordinator.Schedule("late"));
    }

    [TestMethod]
    public async Task ProfileCoordinator_ScheduleDuringActiveWrite_IsPersisted()
    {
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0;
        int maxConcurrent = 0;
        string? lastSeen = null;
        bool firstWrite = true;

        await using var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, ct) =>
            {
                int c = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, c);
                if (firstWrite)
                {
                    firstWrite = false;
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                lastSeen = snapshot;
                Interlocked.Decrement(ref concurrent);
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero);

        coordinator.Schedule("first");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Schedule a new revision while the first save is still executing.
        coordinator.Schedule("second");
        releaseFirst.TrySetResult(true);

        await coordinator.FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.AreEqual("second", lastSeen, "The revision scheduled mid-write must be persisted.");
        Assert.AreEqual(1, maxConcurrent, "Writes must remain serialized.");
    }

    [TestMethod]
    public async Task ProfileCoordinator_ImmediateDispose_FlushesPending()
    {
        string? lastSeen = null;

        var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, _) =>
            {
                lastSeen = snapshot;
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.FromMilliseconds(20));

        coordinator.Schedule("pending");
        // Dispose immediately, potentially before the worker has taken the snapshot.
        await coordinator.DisposeAsync();

        Assert.AreEqual("pending", lastSeen, "Queued work must be flushed by disposal, never abandoned.");
    }

    [TestMethod]
    public async Task ProfileCoordinator_ConcurrentFlushAndDispose_PersistsBeforeSuccess()
    {
        var saveEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? persisted = null;

        var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, _) =>
            {
                saveEntered.TrySetResult(true);
                await releaseSave.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                persisted = snapshot;
            },
            debounce: TimeSpan.Zero);

        coordinator.Schedule("must-survive");
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flush = coordinator.FlushAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var dispose = coordinator.DisposeAsync().AsTask();
        releaseSave.TrySetResult(true);

        await Task.WhenAll(flush, dispose);
        Assert.AreEqual("must-survive", persisted);
    }

    [TestMethod]
    public async Task ProfileCoordinator_FailureThenRecovery_ClearsLastError()
    {
        int calls = 0;
        string? lastSeen = null;

        await using var coordinator = new ProfilePersistenceCoordinator<string>(
            async (snapshot, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("transient");
                lastSeen = snapshot;
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero);

        coordinator.Schedule("v1");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        Assert.IsNotNull(coordinator.LastError);

        coordinator.Schedule("v2");
        await coordinator.FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.IsNull(coordinator.LastError, "A later successful revision must clear LastError.");
        Assert.AreEqual("v2", lastSeen);
    }

    [TestMethod]
    public async Task ProfileCoordinator_RepeatFlush_NoDuplicateWrite()
    {
        int writeCount = 0;

        await using var coordinator = new ProfilePersistenceCoordinator<string>(
            async (_, _) =>
            {
                Interlocked.Increment(ref writeCount);
                await Task.CompletedTask.ConfigureAwait(false);
            },
            debounce: TimeSpan.Zero);

        coordinator.Schedule("only");
        await coordinator.FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await coordinator.FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await coordinator.FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.AreEqual(1, writeCount, "Re-flushing without new work must not rewrite.");
    }

    // ------------------------------------------------------------------
    // Scheduler: behavioural off-caller-thread execution
    // ------------------------------------------------------------------

    [TestMethod]
    public void Scheduler_RunsWorkOffCallerThread()
    {
        IStoreScheduler scheduler = TaskRunStoreScheduler.Shared;
        using var workEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        Task task = scheduler.RunAsync(
            () =>
            {
                workEntered.Set();
                release.Wait();
            },
            CancellationToken.None);

        // Occupy the caller thread. If the scheduled work can enter while this thread is
        // blocked here, it is provably executing on a different thread. Had the work been
        // run inline on the caller thread, workEntered could never be signalled and this
        // would time out.
        bool ranConcurrently = workEntered.Wait(TimeSpan.FromSeconds(5));
        release.Set();
        task.GetAwaiter().GetResult();

        Assert.IsTrue(
            ranConcurrently,
            "Store work must execute off the calling thread (it ran while the caller was blocked).");
    }

    // ------------------------------------------------------------------
    // Ask-user: concurrent resolution and resume winners
    // ------------------------------------------------------------------

    [TestMethod]
    public void AskUser_ConcurrentResolution_SingleWinnerObservedByAll()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("p-conc-1")).GetAwaiter().GetResult();

        const int workers = 16;
        using var start = new ManualResetEventSlim(false);
        var results = new AskUserPrompt[workers];
        var tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            int idx = i;
            string selection = idx % 2 == 0 ? "yes" : "no";
            tasks[i] = Task.Run(() =>
            {
                start.Wait();
                results[idx] = store
                    .ResolveAsync("p-conc-1", selection, $"dev-{idx}", $"tok-{idx}")
                    .GetAwaiter().GetResult();
            });
        }

        start.Set();
        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));

        var final = store.GetAsync("p-conc-1").GetAwaiter().GetResult()!;
        Assert.AreEqual(AskUserState.Resolved, final.State);
        foreach (var r in results)
        {
            Assert.AreEqual(AskUserState.Resolved, r.State);
            Assert.AreEqual(final.Selection, r.Selection);
            Assert.AreEqual(final.ResolutionDeviceId, r.ResolutionDeviceId);
        }
    }

    [TestMethod]
    public void AskUser_ConcurrentResume_ExactlyOneTrue()
    {
        var ctx = new SuspendedAgentContext(
            ContextId: "ctx-conc",
            PromptId: "p",
            ThreadId: "t",
            RunId: "r",
            ContextJson: "{}",
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ResumedAt: null);

        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.SaveSuspendedContextAsync(ctx).GetAwaiter().GetResult();

        const int workers = 16;
        using var start = new ManualResetEventSlim(false);
        var results = new bool[workers];
        var resumedAt = DateTimeOffset.UtcNow;
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(() =>
        {
            start.Wait();
            results[i] = store
                .MarkContextResumedAsync("ctx-conc", resumedAt)
                .GetAwaiter().GetResult();
        })).ToArray();

        start.Set();
        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));

        Assert.AreEqual(1, results.Count(x => x), "Exactly one resume must win.");
    }

    // ------------------------------------------------------------------
    // Asset: hash tamper, empty replacement
    // ------------------------------------------------------------------

    [TestMethod]
    public void Asset_UpsertHashMismatch_Throws()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var tampered = MakeSkillRecord("tamper-1", 1) with
        {
            ContentHash = "00000000000000000000000000000000000000000000000000000000000000ff"
        };

        Assert.ThrowsException<InvalidOperationException>(() =>
            store.UpsertAsync(tampered, [1, 2, 3]).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Asset_UpsertByteCountMismatch_Throws()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var tampered = MakeSkillRecord("tamper-2", 1) with { ContentByteCount = 999 };

        Assert.ThrowsException<InvalidOperationException>(() =>
            store.UpsertAsync(tampered, [1, 2, 3]).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Asset_EmptyContent_StoresRowAndHash_ThenReplaces()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AssetStore(db);
        var emptyHash = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();

        store.UpsertAsync(MakeSkillRecord("empty-1", 1), []).GetAwaiter().GetResult();

        var full = store.GetFullAssetAsync(AssetKind.Skill, "empty-1").GetAwaiter().GetResult();
        Assert.IsNotNull(full);
        Assert.AreEqual(0, full!.Value.Content.Length);
        Assert.AreEqual(emptyHash, full.Value.Summary.ContentHash);
        Assert.AreEqual(0L, full.Value.Summary.ContentByteCount);

        var content = "now with content"u8.ToArray();
        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        store.UpsertAsync(MakeSkillRecord("empty-1", 2), content).GetAwaiter().GetResult();

        var full2 = store.GetFullAssetAsync(AssetKind.Skill, "empty-1").GetAwaiter().GetResult();
        Assert.IsNotNull(full2);
        CollectionAssert.AreEqual(content, full2!.Value.Content);
        Assert.AreEqual(expected, full2.Value.Summary.ContentHash);
        Assert.AreEqual(content.Length, full2.Value.Summary.ContentByteCount);
    }

    // ------------------------------------------------------------------
    // Asset: deterministic conflict comparer and operation ids
    // ------------------------------------------------------------------

    [TestMethod]
    public void AssetConflict_RemoteWins_IsDeterministic()
    {
        var baseRec = MakeSkillRecord("c", 2);

        Assert.IsTrue(AssetConflict.RemoteWins(null, baseRec), "Missing existing is superseded.");
        Assert.IsTrue(AssetConflict.RemoteWins(baseRec, MakeSkillRecord("c", 3)), "Higher version wins.");
        Assert.IsFalse(AssetConflict.RemoteWins(baseRec, MakeSkillRecord("c", 1)), "Lower version loses.");

        var lowSource = baseRec with { SourceDeviceId = "dev-a" };
        var highSource = baseRec with { SourceDeviceId = "dev-b" };
        Assert.IsTrue(AssetConflict.RemoteWins(lowSource, highSource), "Greater ordinal source wins.");
        Assert.IsFalse(AssetConflict.RemoteWins(highSource, lowSource));

        var live = baseRec with { SourceDeviceId = "dev-a", IsDeleted = false };
        var tomb = baseRec with { SourceDeviceId = "dev-a", IsDeleted = true };
        Assert.IsTrue(AssetConflict.RemoteWins(live, tomb), "Tombstone beats live on a tie.");
        Assert.IsFalse(AssetConflict.RemoteWins(tomb, live));
        Assert.IsFalse(AssetConflict.RemoteWins(live, live), "Exact duplicate loses.");

        var local = baseRec with { LocalOnly = true };
        Assert.IsFalse(
            AssetConflict.RemoteWins(local, MakeSkillRecord("c", 99)),
            "LocalOnly rows reject remote mutation.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int prev = Volatile.Read(ref target);
        while (value > prev)
            prev = Interlocked.CompareExchange(ref target, value, prev);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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

    private static AssetRecord MakeSkillRecord(
        string id, int version, bool localOnly = false) =>
        new(
            Kind: AssetKind.Skill,
            Id: id,
            Name: $"Skill {id}",
            MetadataJson: null,
            ContentMime: "application/octet-stream",
            ContentHash: null,
            ContentByteCount: 0,
            Version: version,
            SourceDeviceId: "dev-1",
            UpdatedAt: DateTimeOffset.UtcNow,
            IsDeleted: false,
            LocalOnly: localOnly);
}
