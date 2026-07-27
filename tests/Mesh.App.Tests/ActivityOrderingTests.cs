using Mesh.App.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ActivityOrderingTests
{
    private static OwnThread Thread(string id, DateTimeOffset created, DateTimeOffset? activity = null, bool pinned = false)
        => new OwnThread { Id = id, Title = id, CreatedAt = created, LastActivityAt = activity, IsPinned = pinned };

    private static Conversation Conv(string handle, DateTimeOffset? created = null, DateTimeOffset? activity = null, bool pinned = false)
        => new Conversation { Handle = handle, CreatedAt = created, LastActivityAt = activity, IsPinned = pinned };

    [TestMethod]
    public void OwnThreadOrdering_PinnedThreadSortsFirst()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var t1 = DateTimeOffset.UtcNow.AddDays(-1);
        var unpinned = Thread("a", t0, t1);
        var pinned = Thread("b", t0.AddDays(-5), t0, pinned: true);

        var sorted = OwnThreadOrdering.ByActivity(new[] { unpinned, pinned }).ToList();

        Assert.AreEqual("b", sorted[0].Id, "Pinned thread should sort first regardless of activity");
    }

    [TestMethod]
    public void OwnThreadOrdering_NewerActivitySortsFirst()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var t1 = DateTimeOffset.UtcNow.AddDays(-1);
        var t2 = DateTimeOffset.UtcNow.AddDays(-2);
        var recent = Thread("r", t0, t1);
        var older = Thread("o", t0, t2);

        var sorted = OwnThreadOrdering.ByActivity(new[] { older, recent }).ToList();

        Assert.AreEqual("r", sorted[0].Id, "More recently active thread should sort first");
    }

    [TestMethod]
    public void OwnThreadOrdering_UsesCreatedAtWhenActivityNull()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var t1 = DateTimeOffset.UtcNow.AddDays(-1);
        var newer = Thread("n", t1, null);
        var older = Thread("o", t0, null);

        var sorted = OwnThreadOrdering.ByActivity(new[] { older, newer }).ToList();

        Assert.AreEqual("n", sorted[0].Id, "No-activity thread should sort by created desc");
    }

    [TestMethod]
    public void OwnThreadOrdering_TieBreaksByIdForStability()
    {
        var t = DateTimeOffset.UtcNow.AddDays(-1);
        var threads = new[]
        {
            Thread("zzz", t, t),
            Thread("aaa", t, t),
            Thread("mmm", t, t)
        };

        var sorted = OwnThreadOrdering.ByActivity(threads).ToList();

        Assert.AreEqual("aaa", sorted[0].Id);
        Assert.AreEqual("mmm", sorted[1].Id);
        Assert.AreEqual("zzz", sorted[2].Id);
    }

    [TestMethod]
    public void ConversationOrdering_PinnedConversationSortsFirst()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var unpinned = Conv("alice", t0, t0.AddHours(5));
        var pinned = Conv("bob", t0.AddDays(-5), t0, pinned: true);

        var sorted = ConversationOrdering.ByActivity(new[] { unpinned, pinned }).ToList();

        Assert.AreEqual("bob", sorted[0].Handle, "Pinned conversation sorts first");
    }

    [TestMethod]
    public void ConversationOrdering_NewerActivitySortsFirst()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var recent = Conv("r", t0, t0.AddDays(2));
        var stale = Conv("s", t0, t0.AddDays(1));

        var sorted = ConversationOrdering.ByActivity(new[] { stale, recent }).ToList();

        Assert.AreEqual("r", sorted[0].Handle);
    }

    [TestMethod]
    public void OwnThreadOrdering_PinnedBothSortByActivity()
    {
        var t0 = DateTimeOffset.UtcNow;
        var pin1 = Thread("p1", t0.AddDays(-5), t0.AddDays(-1), pinned: true);
        var pin2 = Thread("p2", t0.AddDays(-5), t0, pinned: true);

        var sorted = OwnThreadOrdering.ByActivity(new[] { pin1, pin2 }).ToList();

        Assert.AreEqual("p2", sorted[0].Id, "Among pinned threads, more recently active sorts first");
    }

    [TestMethod]
    public void ActivityTimestamp_FirstUpdateInitializesThenKeepsNewest()
    {
        DateTimeOffset? activity = null;
        var first = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

        activity = ActivityTimestamp.Advance(activity, first);
        Assert.AreEqual(first, activity);
        activity = ActivityTimestamp.Advance(activity, first.AddMinutes(-1));
        Assert.AreEqual(first, activity);
    }

    [TestMethod]
    public void RemoteRunCorrelation_RejectsMismatchAndAcceptsRegisteredRun()
    {
        var thread = Thread("topic", DateTimeOffset.UnixEpoch);
        Assert.IsFalse(RemoteRunCorrelation.IsExpected(thread, "topic", "arbitrary"));

        thread.ExecutionRunId = "expected";
        Assert.IsFalse(RemoteRunCorrelation.IsExpected(thread, "topic", "other"));
        Assert.IsFalse(RemoteRunCorrelation.IsExpected(thread, "other-topic", "expected"));
        Assert.IsTrue(RemoteRunCorrelation.IsExpected(thread, "topic", "expected"));
    }

    [TestMethod]
    public void RemoteRunActivity_PersistedRunIsFreshOnlyWithinWindow()
    {
        var now = new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);
        var thread = Thread("topic", now.AddHours(-1));

        // No persisted run id: never busy from persisted state.
        Assert.IsFalse(RemoteRunActivity.IsPersistedRunFresh(thread, now));

        thread.ExecutionRunId = "run-1";

        // Run id set but no timestamps: not fresh (cannot pin the indicator).
        Assert.IsFalse(RemoteRunActivity.IsPersistedRunFresh(thread, now));

        // Recent activity: fresh (a live remote run keeps advancing this).
        thread.LastActivityAt = now.AddMinutes(-1);
        Assert.IsTrue(RemoteRunActivity.IsPersistedRunFresh(thread, now));

        // Stale activity (a stranded run id from a prior session): not fresh.
        thread.LastActivityAt = now - RemoteRunActivity.StaleAfter - TimeSpan.FromMinutes(1);
        Assert.IsFalse(RemoteRunActivity.IsPersistedRunFresh(thread, now));

        // Falls back to ExecutionAt when LastActivityAt is absent.
        thread.LastActivityAt = null;
        thread.ExecutionAt = now.AddMinutes(-2);
        Assert.IsTrue(RemoteRunActivity.IsPersistedRunFresh(thread, now));
    }

    private static RemoteRunProjection Projection(string runId, string threadId, DateTimeOffset timestamp)
        => new() { RunId = runId, ThreadId = threadId, Timestamp = timestamp };

    [TestMethod]
    public void RemoteRunActivity_ProjectionIsFreshOnlyWithinWindow()
    {
        var now = new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);

        // No timestamp: not fresh (cannot pin the indicator).
        Assert.IsFalse(RemoteRunActivity.IsProjectionFresh(Projection("run-1", "topic", default), now));

        // Recent update: fresh (a live remote run keeps advancing this).
        Assert.IsTrue(RemoteRunActivity.IsProjectionFresh(
            Projection("run-1", "topic", now.AddMinutes(-1)), now));

        // Stale update (the terminal update was missed): not fresh, so the phantom self-heals.
        Assert.IsFalse(RemoteRunActivity.IsProjectionFresh(
            Projection("run-1", "topic", now - RemoteRunActivity.StaleAfter - TimeSpan.FromMinutes(1)), now));
    }

    [TestMethod]
    public void RemoteRunActivity_BusyCoversLocalLiveAndPersistedRemoteRuns()
    {
        var now = new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);
        var thread = Thread("topic", now.AddHours(-1));
        var freshProjection = Projection("run-1", "topic", now.AddMinutes(-1));
        var staleProjection = Projection(
            "run-1",
            "topic",
            now - RemoteRunActivity.StaleAfter - TimeSpan.FromMinutes(1));

        Assert.IsFalse(RemoteRunActivity.IsBusy(thread, false, null, false, now));
        Assert.IsTrue(RemoteRunActivity.IsBusy(thread, true, null, false, now));
        Assert.IsTrue(RemoteRunActivity.IsBusy(thread, false, freshProjection, false, now));
        Assert.IsFalse(RemoteRunActivity.IsBusy(thread, false, staleProjection, false, now));

        thread.ExecutionRunId = "run-1";
        thread.LastActivityAt = now.AddMinutes(-1);
        Assert.IsFalse(RemoteRunActivity.IsBusy(thread, false, null, false, now),
            "Persisted run state must not make a topic assigned to this device look busy after restart.");
        Assert.IsTrue(RemoteRunActivity.IsBusy(thread, false, null, true, now));

        thread.LastActivityAt = now - RemoteRunActivity.StaleAfter - TimeSpan.FromMinutes(1);
        Assert.IsFalse(RemoteRunActivity.IsBusy(thread, false, null, true, now));
    }

    [TestMethod]
    public void RemoteRunPresentation_PrefersLocalStateAndDoesNotRepeatToolLabel()
    {
        var projection = Projection("run-1", "topic", DateTimeOffset.UtcNow);
        projection.Phase = Mesh.Shared.TopicRunPhase.Executing;
        projection.Status = "Ran PowerShell";
        projection.Steps = new[]
        {
            new Mesh.Shared.TopicRunStep(
                "run_powershell",
                "Ran PowerShell",
                Mesh.Shared.TopicRunItemState.Completed)
        };

        Assert.AreSame(projection, RemoteRunPresentation.VisibleProjection(
            projection, localTurnActive: false));
        Assert.IsNull(RemoteRunPresentation.VisibleProjection(
            projection, localTurnActive: true));
        Assert.AreEqual("executing", RemoteRunPresentation.StatusLabel(projection));

        projection.Status = "Waiting for approval";
        Assert.AreEqual("Waiting for approval", RemoteRunPresentation.StatusLabel(projection));
    }

    [TestMethod]
    public void TopicComposerPresentation_SwitchesBetweenStopAndSendDuringExecution()
    {
        Assert.IsFalse(TopicComposerPresentation.ShowStop(topicBusy: false, hasSendableDraft: false));
        Assert.IsTrue(TopicComposerPresentation.ShowStop(topicBusy: true, hasSendableDraft: false));
        Assert.IsFalse(TopicComposerPresentation.ShowStop(topicBusy: true, hasSendableDraft: true));
        Assert.IsTrue(TopicComposerPresentation.ShowStop(topicBusy: true, hasSendableDraft: false));
    }

    [TestMethod]
    public void QueuedTopicRunState_ShowsOnlyWaitingLinesAndRetainsStartedCorrelation()
    {
        var state = new QueuedTopicRunState();

        Assert.IsTrue(state.MarkWaiting("topic", "run-1", "line-1"));
        Assert.IsTrue(state.MarkWaiting("topic", "run-2", "line-2"));
        Assert.IsFalse(state.MarkWaiting("topic", "run-2", "line-2"));
        Assert.AreEqual(2, state.WaitingCount("topic"));
        Assert.IsTrue(state.IsLineWaiting("line-1"));
        Assert.IsTrue(state.IsKnownRun("topic", "run-1"));

        Assert.IsTrue(state.MarkStarted("topic", "run-1"));
        Assert.IsFalse(state.IsLineWaiting("line-1"));
        Assert.IsTrue(state.IsKnownRun("topic", "run-1"));
        Assert.AreEqual(1, state.WaitingCount("topic"));

        Assert.IsTrue(state.Complete("topic", "run-1"));
        Assert.IsFalse(state.IsKnownRun("topic", "run-1"));
        Assert.IsTrue(state.ClearThread("topic"));
        Assert.AreEqual(0, state.WaitingCount("topic"));
        Assert.IsFalse(state.IsKnownRun("topic", "run-2"));
    }

    [TestMethod]
    public void QueuedTopicRunState_FindsSameLineIdWithinItsThread()
    {
        var state = new QueuedTopicRunState();
        state.MarkWaiting("topic-1", "run-1", "line");
        state.MarkWaiting("topic-2", "run-2", "line");

        Assert.AreEqual("run-1", state.FindByLine("topic-1", "line")?.RunId);
        Assert.AreEqual("run-2", state.FindByLine("topic-2", "line")?.RunId);
    }

    [TestMethod]
    public void TopicTranscriptOrdering_PairsQueuedPromptsWithTheirReplies()
    {
        var lines = new[]
        {
            new ChatLine { Id = "prompt-1", Role = "user", Text = "one" },
            new ChatLine { Id = "prompt-2", Role = "user", Text = "two" },
            new ChatLine { Id = "prompt-3", Role = "user", Text = "three" },
            new ChatLine { Id = "reply-1", Role = "assistant", Text = "ONE", ReplyToLineId = "prompt-1" },
            new ChatLine { Id = "reply-2", Role = "assistant", Text = "TWO", ReplyToLineId = "prompt-2" },
            new ChatLine { Id = "reply-3", Role = "assistant", Text = "THREE", ReplyToLineId = "prompt-3" }
        };

        var ordered = TopicTranscriptOrdering.OrderForDisplay(lines);

        CollectionAssert.AreEqual(
            new[] { "prompt-1", "reply-1", "prompt-2", "reply-2", "prompt-3", "reply-3" },
            ordered.Select(line => line.Id).ToArray());
    }

    [TestMethod]
    public void TopicTranscriptOrdering_KeepsWaitingPromptsBelowTheLiveResponse()
    {
        var queue = new QueuedTopicRunState();
        queue.MarkWaiting("topic", "run-2", "prompt-2");
        queue.MarkWaiting("topic", "run-3", "prompt-3");
        var ordered = TopicTranscriptOrdering.OrderForDisplay(new[]
        {
            new ChatLine { Id = "prompt-1", Role = "user" },
            new ChatLine { Id = "prompt-2", Role = "user" },
            new ChatLine { Id = "prompt-3", Role = "user" },
            new ChatLine { Id = "reply-1", Role = "assistant", ReplyToLineId = "prompt-1" }
        });

        var transcript = ordered
            .Where(line => !queue.IsLineWaiting(line.Id))
            .Select(line => line.Id)
            .ToArray();
        var waiting = ordered
            .Where(line => queue.IsLineWaiting(line.Id))
            .Select(line => line.Id)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "prompt-1", "reply-1" }, transcript);
        CollectionAssert.AreEqual(new[] { "prompt-2", "prompt-3" }, waiting);
    }

    [TestMethod]
    public void TopicTranscriptOrdering_PreservesLegacyAndOrphanReplies()
    {
        var lines = new[]
        {
            new ChatLine { Id = "legacy-prompt", Role = "user" },
            new ChatLine { Id = "legacy-reply", Role = "assistant" },
            new ChatLine { Id = "new-prompt", Role = "user" },
            new ChatLine { Id = "waiting-prompt", Role = "user" },
            new ChatLine { Id = "new-reply", Role = "assistant", ReplyToLineId = "new-prompt" },
            new ChatLine { Id = "orphan-reply", Role = "assistant", ReplyToLineId = "missing" }
        };

        var ordered = TopicTranscriptOrdering.OrderForDisplay(lines);

        CollectionAssert.AreEqual(
            new[]
            {
                "legacy-prompt", "legacy-reply", "new-prompt", "new-reply",
                "waiting-prompt", "orphan-reply"
            },
            ordered.Select(line => line.Id).ToArray());
    }

    [TestMethod]
    public void RemoteRunReconciliation_AnswerFinalizesOnlyWhenNotOlderThanProjection()
    {
        var at = new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);

        // No live projection: nothing to finalize.
        Assert.IsFalse(RemoteRunReconciliation.ShouldFinalizeOnAnswer(null, at));

        // Answer with no timestamp: cannot finalize.
        Assert.IsFalse(RemoteRunReconciliation.ShouldFinalizeOnAnswer(Projection("run-1", "topic", at), default));

        // Answer at or after the projection's last update finalizes the missed-terminal run.
        Assert.IsTrue(RemoteRunReconciliation.ShouldFinalizeOnAnswer(Projection("run-1", "topic", at.AddSeconds(-5)), at));
        Assert.IsTrue(RemoteRunReconciliation.ShouldFinalizeOnAnswer(Projection("run-1", "topic", at), at));

        // A genuinely newer run (projection timestamp ahead of this answer) is left running.
        Assert.IsFalse(RemoteRunReconciliation.ShouldFinalizeOnAnswer(Projection("run-2", "topic", at.AddSeconds(5)), at));
    }

    [TestMethod]
    public void ClockBehindMetadataMutations_KeepFutureActivity()
    {
        var future = DateTimeOffset.UtcNow.AddDays(2);
        var clockBehindPin = DateTimeOffset.UtcNow;
        var clockBehindMove = clockBehindPin.AddMinutes(-1);
        var clockBehindRename = clockBehindPin.AddMinutes(-2);

        var activity = ActivityTimestamp.Advance(future, clockBehindPin);
        activity = ActivityTimestamp.Advance(activity, clockBehindMove);
        activity = ActivityTimestamp.Advance(activity, clockBehindRename);

        Assert.AreEqual(future, activity);
    }
}
