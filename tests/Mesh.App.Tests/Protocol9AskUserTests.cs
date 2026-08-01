using System.Security.Cryptography;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Protocol 9 ask-user interaction tests for the owner/Me agent path.
///
/// The always-on class below exercises the reliable store semantics the feature relies on
/// (first-writer resolution, expiry-cannot-resolve, cancel-cannot-resolve, exactly-once
/// context resume, and option-schema invariants) using only source files already linked into
/// this test project via &lt;Compile Include&gt; (AskUserStore.cs, InteractionModels.cs, MeshDb*).
///
/// The coordinator / view-state / tool-schema behaviours require the new pure source file
/// <c>src\Mesh.App\Services\AskUserInteractionCoordinator.cs</c> to be linked. That file is
/// MAUI-free and depends only on Mesh.App.Domain and IAgentTool.cs (both already linked), so it
/// can be added with a single &lt;Compile Include&gt; entry. Those tests are guarded behind the
/// <c>PROTOCOL9_ASKUSER_TESTS</c> compilation constant so this file always compiles as-is.
/// See the report for the exact link + constant needed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Protocol9AskUserTests
{
    private string directory = null!;
    private string databasePath = null!;
    private byte[] key = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            AppContext.BaseDirectory,
            "protocol9-askuser-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "profile.meshdb");
        key = RandomNumberGenerator.GetBytes(32);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    // ------------------------------------------------------------------
    // Reliable store: first-writer resolution wins, loser observes winner.
    // ------------------------------------------------------------------

    [TestMethod]
    public void Resolve_FirstWriterWins_LoserSeesWinnerSelection()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("ask-fww")).GetAwaiter().GetResult();

        var winner = store.ResolveAsync("ask-fww", "yes", "dev-owner", "tok-owner")
            .GetAwaiter().GetResult();
        var loser = store.ResolveAsync("ask-fww", "no", "dev-other", "tok-other")
            .GetAwaiter().GetResult();

        Assert.AreEqual(AskUserState.Resolved, winner.State);
        Assert.AreEqual("yes", winner.Selection);
        // The second writer is fenced out and observes the winner's committed answer.
        Assert.AreEqual(AskUserState.Resolved, loser.State);
        Assert.AreEqual("yes", loser.Selection);
        Assert.AreEqual("dev-owner", loser.ResolutionDeviceId);
    }

    // ------------------------------------------------------------------
    // Expiry: an expired prompt cannot be resolved (requirement 8).
    // ------------------------------------------------------------------

    [TestMethod]
    public void ExpiredPrompt_CannotResolve()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("ask-exp")).GetAwaiter().GetResult();

        var expired = store.ExpireAsync("ask-exp").GetAwaiter().GetResult();
        Assert.AreEqual(AskUserState.Expired, expired.State);

        // A resolution attempt after expiry must not flip the row to Resolved.
        var afterResolve = store.ResolveAsync("ask-exp", "yes", "dev-owner", "tok-late")
            .GetAwaiter().GetResult();
        Assert.AreEqual(AskUserState.Expired, afterResolve.State);
        Assert.AreNotEqual(AskUserState.Resolved, afterResolve.State);
        Assert.IsNull(afterResolve.Selection);
    }

    // ------------------------------------------------------------------
    // Cancellation: a cancelled run's prompt cannot later resolve, and
    // reliable pending state survives until explicitly cancelled/resolved.
    // ------------------------------------------------------------------

    [TestMethod]
    public void CancelledPrompt_CannotResolve()
    {
        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.CreateAsync(MakePendingPrompt("ask-cancel")).GetAwaiter().GetResult();

        var cancelled = store.CancelAsync("ask-cancel").GetAwaiter().GetResult();
        Assert.AreEqual(AskUserState.Cancelled, cancelled.State);

        var afterResolve = store.ResolveAsync("ask-cancel", "yes", "dev-owner", "tok-late")
            .GetAwaiter().GetResult();
        Assert.AreEqual(AskUserState.Cancelled, afterResolve.State);
        Assert.AreNotEqual(AskUserState.Resolved, afterResolve.State);
    }

    [TestMethod]
    public void PendingPrompt_SurvivesReopen_ForRestartRecovery()
    {
        // App shutdown/restart must leave reliable pending state (requirement 8).
        using (var db = MeshDb.Open(databasePath, key))
        {
            var store = new AskUserStore(db);
            store.CreateAsync(MakePendingPrompt("ask-reliable")).GetAwaiter().GetResult();
        }
        SqliteConnection.ClearAllPools();

        using (var db = MeshDb.Open(databasePath, key))
        {
            var store = new AskUserStore(db);
            var reloaded = store.GetAsync("ask-reliable").GetAwaiter().GetResult();
            Assert.IsNotNull(reloaded);
            Assert.AreEqual(AskUserState.Pending, reloaded.State);
        }
    }

    // ------------------------------------------------------------------
    // Restart exactly-once resume: the reliable context fence admits a
    // single continuation even across sequential resume attempts.
    // ------------------------------------------------------------------

    [TestMethod]
    public void ContextResume_ExactlyOnce_SequentialAttempts()
    {
        var ctx = new SuspendedAgentContext(
            ContextId: "ctx-ask-once",
            PromptId: "ask-once",
            ThreadId: "thread-x",
            RunId: "run-x",
            ContextJson: """{"triggerLineId":"line-1","selection":"yes"}""",
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ResumedAt: null);

        using var db = MeshDb.Open(databasePath, key);
        var store = new AskUserStore(db);
        store.SaveSuspendedContextAsync(ctx).GetAwaiter().GetResult();

        var now = DateTimeOffset.UtcNow;
        var first = store.MarkContextResumedAsync("ctx-ask-once", now).GetAwaiter().GetResult();
        var second = store.MarkContextResumedAsync("ctx-ask-once", now.AddSeconds(1))
            .GetAwaiter().GetResult();

        Assert.IsTrue(first, "The first resume must win the exactly-once fence.");
        Assert.IsFalse(second, "A second resume must be fenced out to avoid duplicate continuations.");
    }

    // ------------------------------------------------------------------
    // Option schema invariants (requirement 1 / invalid options).
    // ------------------------------------------------------------------

    [TestMethod]
    public void Validate_RejectsDuplicateOptionIds()
    {
        var options = new List<AskUserOption>
        {
            new("dup", "First", null),
            new("dup", "Second", null)
        };
        var ex = Assert.ThrowsException<ArgumentException>(() =>
            AskUserPrompt.Validate(options, null));
        StringAssert.Contains(ex.Message, "Duplicate option id");
    }

    [TestMethod]
    public void Validate_RejectsBlankOptionTitle()
    {
        var options = new List<AskUserOption>
        {
            new("a", "  ", null),
            new("b", "B", null)
        };
        Assert.ThrowsException<ArgumentException>(() =>
            AskUserPrompt.Validate(options, null));
    }

    [TestMethod]
    public void Validate_RejectsRecommendedIndexOutOfRange()
    {
        var options = new List<AskUserOption>
        {
            new("a", "A", null),
            new("b", "B", null)
        };
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            AskUserPrompt.Validate(options, 2));
    }

    [TestMethod]
    public void Validate_AcceptsTwoToFiveOptionsWithRecommended()
    {
        var options = new List<AskUserOption>
        {
            new("a", "A", "first"),
            new("b", "B", null),
            new("c", "C", null)
        };
        // Should not throw for a well-formed 3-option prompt with a valid recommended index.
        AskUserPrompt.Validate(options, 1);
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
}

// ======================================================================
// Behavioural coverage for the pure, MAUI-free interaction types.
// ======================================================================
[TestClass]
public sealed class Protocol9AskUserCoordinatorTests
{
    // ---- Coordinator: first-writer resolution relayed to the single waiter ----

    [TestMethod]
    public void Coordinator_FirstSignalWins_WaiterReceivesFirstResult()
    {
        var coordinator = new AskUserInteractionCoordinator();
        var wait = coordinator.WaitAsync("p-1");
        Assert.IsTrue(coordinator.HasWaiter("p-1"));

        var first = coordinator.TrySignalResolved(ResolvedPrompt("p-1", "yes"));
        var second = coordinator.TrySignalResolved(ResolvedPrompt("p-1", "no"));

        Assert.IsTrue(first, "The first resolution must be handed to the live waiter.");
        Assert.IsTrue(second, "The live waiter must retain resume ownership after the first signal.");
        Assert.IsTrue(wait.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("yes", wait.Result.Selection);
        Assert.IsTrue(coordinator.HasWaiter("p-1"));
        coordinator.Complete("p-1");
        Assert.IsFalse(coordinator.HasWaiter("p-1"));
    }

    [TestMethod]
    public void Coordinator_NoWaiter_SignalReturnsFalse()
    {
        var coordinator = new AskUserInteractionCoordinator();
        Assert.IsFalse(coordinator.TrySignalResolved(ResolvedPrompt("absent", "yes")));
    }

    [TestMethod]
    public void Coordinator_Cancellation_RemovesWaiterAndCancelsTask()
    {
        var coordinator = new AskUserInteractionCoordinator();
        using var cts = new CancellationTokenSource();
        var wait = coordinator.WaitAsync("p-cancel", cts.Token);

        cts.Cancel();

        Assert.ThrowsException<TaskCanceledException>(() => wait.GetAwaiter().GetResult());
        Assert.IsFalse(coordinator.HasWaiter("p-cancel"));
    }

    // ---- View mapping: prompt -> bubble view state ----

    [TestMethod]
    public void BubbleView_Pending_IsInteractiveWithRecommendedBadge()
    {
        var prompt = MakePrompt("p-pending", AskUserState.Pending, selection: null, recommended: 1);
        var view = AskUserBubbleView.From(prompt, DateTimeOffset.UtcNow);

        Assert.AreEqual(AskUserBubbleStatus.Pending, view.Status);
        Assert.IsTrue(view.IsInteractive);
        Assert.IsFalse(view.Options[0].IsRecommended);
        Assert.IsTrue(view.Options[1].IsRecommended);
        Assert.IsNull(view.SelectedOptionId);
    }

    [TestMethod]
    public void BubbleView_Answered_MarksSelectionAndIsNotInteractive()
    {
        var prompt = MakePrompt("p-answered", AskUserState.Resolved, selection: "no", recommended: null);
        var view = AskUserBubbleView.From(prompt, DateTimeOffset.UtcNow);

        Assert.AreEqual(AskUserBubbleStatus.Answered, view.Status);
        Assert.IsFalse(view.IsInteractive);
        Assert.AreEqual("no", view.SelectedOptionId);
        Assert.AreEqual("No", view.SelectedOptionTitle);
        Assert.IsTrue(view.Options.Single(o => o.Id == "no").IsSelected);
    }

    [TestMethod]
    public void BubbleView_PendingPastDeadline_RendersExpired()
    {
        var prompt = MakePrompt(
            "p-late", AskUserState.Pending, selection: null, recommended: null,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var view = AskUserBubbleView.From(prompt, DateTimeOffset.UtcNow);

        Assert.AreEqual(AskUserBubbleStatus.Expired, view.Status);
        Assert.IsFalse(view.IsInteractive);
    }

    [TestMethod]
    public void BubbleView_Cancelled_RendersCancelled()
    {
        var prompt = MakePrompt("p-x", AskUserState.Cancelled, selection: null, recommended: null);
        var view = AskUserBubbleView.From(prompt, DateTimeOffset.UtcNow);
        Assert.AreEqual(AskUserBubbleStatus.Cancelled, view.Status);
        Assert.IsFalse(view.IsInteractive);
    }

    // ---- Schema: owner-only tool argument parsing ----

    [TestMethod]
    public void Schema_ParseRequest_ParsesQuestionOptionsRecommendedAndExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var args = JsonDocument.Parse("""
        {
          "question": "  Ship it?  ",
          "options": [
            { "id": "ship", "title": "Ship", "description": "Release now" },
            { "id": "hold", "title": "Hold" }
          ],
          "recommended_index": 0,
          "expires_in_seconds": 120
        }
        """).RootElement;

        var request = AskUserToolSchema.ParseRequest(args, "thread-1", "run-1", "line-1", now);

        Assert.AreEqual("Ship it?", request.Question);
        Assert.AreEqual(2, request.Options.Count);
        Assert.AreEqual("ship", request.Options[0].Id);
        Assert.AreEqual("Release now", request.Options[0].Description);
        Assert.AreEqual(0, request.RecommendedIndex);
        Assert.AreEqual("thread-1", request.ThreadId);
        Assert.AreEqual("run-1", request.RunId);
        Assert.AreEqual("line-1", request.TriggerLineId);
        Assert.IsNotNull(request.ExpiresAt);
        Assert.AreEqual(now.AddSeconds(120).UtcTicks, request.ExpiresAt!.Value.UtcTicks);
    }

    [TestMethod]
    public void Schema_ParseRequest_RejectsTooFewOptions()
    {
        var args = JsonDocument.Parse("""
        { "question": "Pick", "options": [ { "id": "a", "title": "A" } ] }
        """).RootElement;

        Assert.ThrowsException<ArgumentException>(() =>
            AskUserToolSchema.ParseRequest(args, "t", "r", null, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Schema_ParseRequest_RejectsBlankQuestion()
    {
        var args = JsonDocument.Parse("""
        { "question": "   ", "options": [ { "id": "a", "title": "A" }, { "id": "b", "title": "B" } ] }
        """).RootElement;

        Assert.ThrowsException<ArgumentException>(() =>
            AskUserToolSchema.ParseRequest(args, "t", "r", null, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Schema_ToolName_IsAskUser()
    {
        Assert.AreEqual("ask_user", AskUserToolSchema.ToolName);
    }

    private static AskUserPrompt ResolvedPrompt(string id, string selection) =>
        MakePrompt(id, AskUserState.Resolved, selection, recommended: null);

    private static AskUserPrompt MakePrompt(
        string id,
        AskUserState state,
        string? selection,
        int? recommended,
        DateTimeOffset? expiresAt = null) =>
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
            RecommendedIndex: recommended,
            State: state,
            Selection: selection,
            OriginDeviceId: "origin",
            ResolutionDeviceId: state == AskUserState.Resolved ? "dev-owner" : null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: expiresAt,
            ResolvedAt: state == AskUserState.Resolved ? DateTimeOffset.UtcNow : null,
            Revision: 1);
}
