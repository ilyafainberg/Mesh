using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Domain;
using Mesh.App.Services;

using Mesh.Shared;
namespace Mesh.App.Tests;

[TestClass]
public sealed class NotificationPolicyTests
{
    [TestMethod]
    public void ContentPolicy_AlwaysUsesLocallyDecryptedContent()
    {
        var message = Activity(NotificationKind.Message, "private message");
        var topic = Activity(NotificationKind.TopicCompleted, "private topic title");

        Assert.AreEqual(
            "private message",
            NotificationContentPolicy.Build(message, true).Body);
        Assert.AreEqual(
            "private topic title",
            NotificationContentPolicy.Build(topic, true).Body);
    }

    [TestMethod]
    public void TopicIntent_UsesDecryptedResponseAsPreviewBody()
    {
        var intent = NotificationIntents.Topic(
            "run-1",
            "topic-1",
            "original prompt",
            NotificationKind.TopicCompleted,
            "final assistant response");

        Assert.AreEqual("final assistant response", intent.Body);
    }

    [TestMethod]
    public void DecisionPolicy_RequiresEveryBannerCondition()
    {
        var activity = Activity(NotificationKind.Message, "body");
        Assert.IsTrue(NotificationDecisionPolicy.ShouldShowBanner(
            activity, false, false, false));
        Assert.IsFalse(NotificationDecisionPolicy.ShouldShowBanner(
            activity, true, false, false));
        Assert.IsFalse(NotificationDecisionPolicy.ShouldShowBanner(
            activity, false, true, false));
        Assert.IsFalse(NotificationDecisionPolicy.ShouldShowBanner(
            activity, false, false, true));
        Assert.IsFalse(NotificationDecisionPolicy.ShouldShowBanner(
            activity with { IsHistorical = true }, false, false, false));
        Assert.IsFalse(NotificationDecisionPolicy.ShouldShowBanner(
            activity with { NotifyRequested = false }, false, false, false));
    }

    [TestMethod]
    public void RemoteWake_ShowsGenericAlertOnlyWhileBackgrounded()
    {
        Assert.IsTrue(RemoteWakeNotificationPolicy.ShouldShowGenericAlert(true, false));
        Assert.IsFalse(RemoteWakeNotificationPolicy.ShouldShowGenericAlert(true, true));
        Assert.IsFalse(RemoteWakeNotificationPolicy.ShouldShowGenericAlert(false, false));
    }

    [TestMethod]
    public void ForegroundPolicy_SuppressesOnlyActivityFromBeforeTheCurrentResume()
    {
        var resumedAt = DateTimeOffset.UtcNow;
        Assert.IsTrue(NotificationForegroundPolicy.IsCatchUp(resumedAt.AddSeconds(-1), true, resumedAt));
        Assert.IsTrue(NotificationForegroundPolicy.IsCatchUp(resumedAt, true, resumedAt));
        Assert.IsFalse(NotificationForegroundPolicy.IsCatchUp(resumedAt.AddSeconds(1), true, resumedAt));
        Assert.IsFalse(NotificationForegroundPolicy.IsCatchUp(resumedAt.AddSeconds(-1), false, resumedAt));
        Assert.IsFalse(NotificationForegroundPolicy.IsCatchUp(resumedAt.AddSeconds(-1), true, null));
        Assert.IsFalse(NotificationDecisionPolicy.ShouldShowBanner(
            Activity(NotificationKind.Message, "body"), false, false, false, foregroundCatchUp: true));
    }

    [TestMethod]
    public void ContentPolicy_PreservesActivityTimeForNativePresentationRaces()
    {
        var activity = Activity(NotificationKind.Message, "body");
        var notification = NotificationContentPolicy.Build(activity, true);

        Assert.AreEqual(activity.CreatedAt, notification.CreatedAt);
    }

    [TestMethod]
    public void WakeSession_ReferenceCountsSameWakeLeases()
    {
        var sessions = new NotificationWakeSession();
        using var quiet = sessions.Begin("wake-1", false);
        var firstVisible = sessions.Begin("wake-1", true);
        var secondVisible = sessions.Begin("wake-1", true);
        Assert.IsTrue(sessions.HasVisibleRemoteAlert);

        quiet.Dispose();
        firstVisible.Dispose();
        Assert.IsTrue(sessions.HasVisibleRemoteAlert);

        secondVisible.Dispose();
        Assert.IsFalse(sessions.HasVisibleRemoteAlert);
        secondVisible.Dispose();
        Assert.IsFalse(sessions.HasVisibleRemoteAlert);
    }

    [TestMethod]
    public async Task OperationGate_SerializesAccountResetBeforeLaterDelivery()
    {
        var gate = new NotificationOperationGate();
        var resetStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new List<string>();

        var reset = gate.RunAsync(async ct =>
        {
            sequence.Add("reset-start");
            resetStarted.TrySetResult(true);
            await releaseReset.Task.WaitAsync(ct);
            sequence.Add("reset-end");
        });
        await resetStarted.Task;

        var delivery = gate.RunAsync(_ =>
        {
            sequence.Add("delivery");
            return Task.CompletedTask;
        });

        Assert.IsFalse(delivery.IsCompleted);
        CollectionAssert.AreEqual(new[] { "reset-start" }, sequence);

        releaseReset.TrySetResult(true);
        await Task.WhenAll(reset, delivery);
        CollectionAssert.AreEqual(new[] { "reset-start", "reset-end", "delivery" }, sequence);
    }

    [TestMethod]
    public async Task OperationGate_DoesNotRunSqliteWorkInlineOnCaller()
    {
        var gate = new NotificationOperationGate();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var operation = gate.RunAsync(_ =>
        {
            Thread.Sleep(250);
            return Task.CompletedTask;
        });

        Assert.IsTrue(elapsed.ElapsedMilliseconds < 100);
        await operation;
    }

    [TestMethod]
    public async Task NativeCompletion_FromWorkerQueuesCallbackForMainThread()
    {
        var callbacks = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var completion = new NotificationCallbackCompletion(callbacks.Enqueue);
        var nativeCalls = 0;
        var dispatched = false;

        await Task.Run(() => completion.Complete(() =>
        {
            Assert.IsTrue(dispatched, "The native callback must execute through the UI dispatcher.");
            nativeCalls++;
        }));

        Assert.AreEqual(0, nativeCalls);
        Assert.IsTrue(callbacks.TryDequeue(out var callback));
        dispatched = true;
        callback();
        Assert.AreEqual(1, nativeCalls);
        Assert.IsTrue(callbacks.IsEmpty);
    }

    [TestMethod]
    public async Task NativeCompletion_ConcurrentSuccessAndTimeoutDispatchOnlyOnce()
    {
        var callbacks = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var completion = new NotificationCallbackCompletion(callbacks.Enqueue);
        var nativeCalls = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => completion.Complete(() => nativeCalls++))));

        Assert.AreEqual(1, callbacks.Count);
        Assert.AreEqual(0, nativeCalls);
        Assert.IsTrue(callbacks.TryDequeue(out var callback));
        callback();
        Assert.AreEqual(1, nativeCalls);
    }

    [TestMethod]
    public void WakeDeduplicator_CoalescesStableIdsAndAcceptsAfterRetention()
    {
        var deduplicator = new NotificationWakeDeduplicator(TimeSpan.FromMinutes(5), capacity: 4);
        var now = DateTimeOffset.UtcNow;

        Assert.IsTrue(deduplicator.TryAccept("wake-1", now));
        Assert.IsFalse(deduplicator.TryAccept("wake-1", now.AddMinutes(1)));
        Assert.IsTrue(deduplicator.TryAccept("wake-2", now.AddMinutes(1)));
        Assert.IsTrue(deduplicator.TryAccept("wake-1", now.AddMinutes(6)));
    }

    [TestMethod]
    public void AndroidWakeParser_AcceptsExactDataOnlyPayload()
    {
        var data = new Dictionary<string, string>
        {
            ["mesh_type"] = "sync",
            ["mesh_version"] = MeshProtocol.Version.ToString(),
            ["wake_id"] = "wake-1",
            ["show_alert"] = "1"
        };

        Assert.IsTrue(AndroidReplicationWakePolicy.TryParse(data, out var payload));
        Assert.AreEqual("wake-1", payload.WakeId);
        Assert.IsTrue(payload.ShowAlert);
    }

    [TestMethod]
    public void AndroidWakeWorkName_IsStablePerWakeAndIsolatesDifferentWakes()
    {
        var first = AndroidReplicationWakePolicy.WorkName("wake-1");

        Assert.AreEqual(first, AndroidReplicationWakePolicy.WorkName("wake-1"));
        Assert.AreNotEqual(first, AndroidReplicationWakePolicy.WorkName("wake-2"));
        StringAssert.StartsWith(first, AndroidReplicationWakePolicy.UniqueWorkName + ":");
    }

    private static CommittedActivity Activity(NotificationKind kind, string body)
    {
        var now = DateTimeOffset.UtcNow;
        return new CommittedActivity(
            $"activity:{kind}", "event-1", kind, "entity-1", "entity-1",
            NotificationRoutes.Messages("entity-1"), "Mesh activity", body,
            now, now, false, true, "alice");
    }
}
