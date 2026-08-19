using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mesh.App.Domain;
using Mesh.App.Services;

using Mesh.Shared;
namespace Mesh.App.Tests;

[TestClass]
public sealed class NotificationPolicyTests
{
    [TestMethod]
    public void PreviewPolicy_HidesContentUntilAllowed()
    {
        var message = Activity(NotificationKind.Message, "private message");
        var topic = Activity(NotificationKind.TopicCompleted, "private topic title");

        Assert.AreEqual(
            "Open Mesh to read the message.",
            NotificationPreviewPolicy.Build(
                message, NotificationPreviewMode.Never, true, true).Body);
        Assert.AreEqual(
            "Open Mesh to view the topic.",
            NotificationPreviewPolicy.Build(
                topic, NotificationPreviewMode.Never, true, true).Body);
        Assert.AreEqual(
            "Open Mesh to read the message.",
            NotificationPreviewPolicy.Build(
                message, NotificationPreviewMode.WhenUnlocked, false, true).Body);
        Assert.AreEqual(
            "private message",
            NotificationPreviewPolicy.Build(
                message, NotificationPreviewMode.WhenUnlocked, true, true).Body);
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

    private static CommittedActivity Activity(NotificationKind kind, string body)
    {
        var now = DateTimeOffset.UtcNow;
        return new CommittedActivity(
            $"activity:{kind}", "event-1", kind, "entity-1", "entity-1",
            NotificationRoutes.Messages("entity-1"), "Mesh activity", body,
            now, now, false, true, "alice");
    }
}
