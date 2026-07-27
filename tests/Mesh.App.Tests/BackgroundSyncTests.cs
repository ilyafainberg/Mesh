using System.Text.Json;
using Mesh.App.Services;
using Mesh.Relay.Hub;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class BackgroundSyncTests
{
    private sealed class FakeTransport(
        Func<CancellationToken, Task<BackgroundSyncResult>> run) : IBackgroundSyncTransport
    {
        public int Calls;

        public Task<BackgroundSyncResult> SynchronizePendingAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return run(ct);
        }
    }

    [TestMethod]
    public async Task CoordinatorCoalescesConcurrentWakeSources()
    {
        var release = new TaskCompletionSource<BackgroundSyncResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(_ => release.Task);
        var coordinator = new BackgroundSyncCoordinator(transport);

        var first = coordinator.SynchronizePendingAsync(TimeSpan.FromSeconds(2));
        var second = coordinator.SynchronizePendingAsync(TimeSpan.FromSeconds(2));
        release.SetResult(BackgroundSyncResult.NewData(2));

        var results = await Task.WhenAll(first, second);
        Assert.AreEqual(1, transport.Calls);
        Assert.IsTrue(results.All(result => result.Outcome == BackgroundSyncOutcome.NewData));
    }

    [TestMethod]
    public async Task CoordinatorCancelsSessionAtBudget()
    {
        var transport = new FakeTransport(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return BackgroundSyncResult.NoData();
        });
        var coordinator = new BackgroundSyncCoordinator(transport);

        var result = await coordinator.SynchronizePendingAsync(TimeSpan.FromMilliseconds(25));

        Assert.AreEqual(BackgroundSyncOutcome.Failed, result.Outcome);
        Assert.AreEqual("timeout", result.Error);
    }

    [DataTestMethod]
    [DataRow("{\"protocolVersion\":6,\"durableDelivery\":true,\"backgroundSync\":true}", true)]
    [DataRow("{\"protocolVersion\":6,\"durableDelivery\":true,\"backgroundSync\":false}", false)]
    [DataRow("{\"protocolVersion\":6,\"durableDelivery\":false,\"backgroundSync\":true}", false)]
    [DataRow("{\"protocolVersion\":5,\"durableDelivery\":true}", false)]
    public void BackgroundSyncRequiresBothRelayCapabilities(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual(expected, BackgroundSyncCapabilityPolicy.IsSupported(document.RootElement));
    }

    [DataTestMethod]
    [DataRow(MeshKinds.Chat)]
    [DataRow(MeshKinds.AgentRequest)]
    [DataRow(MeshKinds.AtomicAgentRequest)]
    [DataRow(MeshKinds.ServiceRequest)]
    [DataRow(MeshKinds.TopicRunRequest)]
    [DataRow(MeshKinds.TopicRunCancel)]
    [DataRow(MeshKinds.AttachmentChunk)]
    [DataRow(MeshKinds.TopicAttachmentChunk)]
    [DataRow(DeviceSyncKinds.EnvelopeSnapshotRequest)]
    public void ActiveWorkRemainsQueuedForForeground(string kind)
        => Assert.IsTrue(BackgroundInboundPolicy.RequiresForeground(kind));

    [DataTestMethod]
    [DataRow(MeshKinds.DirectMessage)]
    [DataRow(MeshKinds.GroupControl)]
    [DataRow(MeshKinds.GroupMessage)]
    [DataRow(MeshKinds.Fanout)]
    [DataRow(MeshKinds.AgentResponse)]
    [DataRow(MeshKinds.AtomicAgentResponse)]
    [DataRow(MeshKinds.ServiceResponse)]
    [DataRow(MeshKinds.Report)]
    [DataRow(MeshKinds.Receipt)]
    [DataRow(MeshKinds.TopicRunUpdate)]
    [DataRow(DeviceSyncKinds.EnvelopeOperation)]
    public void PassiveUpdatesCanBeAppliedInBackground(string kind)
        => Assert.IsFalse(BackgroundInboundPolicy.RequiresForeground(kind));

    [TestMethod]
    public void DeniedAlertPermissionStillRetainsPushToken()
    {
        var registration = PushRegistrationPolicy.Create("apns-token", alertsEnabled: false);

        Assert.IsNotNull(registration);
        Assert.AreEqual("apns-token", registration.Token);
        Assert.IsFalse(registration.AlertsEnabled);
    }

    [TestMethod]
    public void ForegroundRoutingCanExcludeBackgroundSyncConnections()
    {
        var registry = new ConnectionRegistry();
        registry.Add("foreground", "ifain", "nonce-1", supportsDurableDelivery: true);
        registry.Add(
            "background", "ifain", "nonce-2",
            supportsDurableDelivery: true, isBackgroundSync: true);
        registry.MarkAuthenticated("foreground", "same-device-key");
        registry.MarkAuthenticated("background", "same-device-key");
        var deviceId = DeviceProtocol.DeviceId("same-device-key");

        CollectionAssert.AreEquivalent(
            new[] { "foreground", "background" }, registry.ConnectionsForDevice("ifain", deviceId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "foreground" },
            registry.ConnectionsForDevice("ifain", deviceId, includeBackgroundSync: false).ToArray());
    }

    [TestMethod]
    public void BackgroundConnectionsDoNotHoldForegroundPresenceOpen()
    {
        var registry = new ConnectionRegistry();
        registry.Add("foreground", "ifain", "nonce-1", supportsDurableDelivery: true);
        registry.Add(
            "background", "ifain", "nonce-2",
            supportsDurableDelivery: true, isBackgroundSync: true);
        registry.MarkAuthenticated("foreground", "same-device-key");
        registry.MarkAuthenticated("background", "same-device-key");

        Assert.AreEqual(1, registry.LocalHandles(includeBackgroundSync: false).Count);
        Assert.AreEqual(1, registry.LocalDevices(includeBackgroundSync: false).Count);
        Assert.AreEqual("ifain", registry.Remove("foreground"));
        Assert.AreEqual(0, registry.LocalHandles(includeBackgroundSync: false).Count);
        Assert.AreEqual(0, registry.LocalDevices(includeBackgroundSync: false).Count);
        Assert.IsNull(registry.Remove("background"));
    }

    [DataTestMethod]
    [DataRow("user", true)]
    [DataRow("assistant", false)]
    [DataRow("tool", false)]
    public void OnlyInboundConversationLinesBecomeUnread(string role, bool expected)
        => Assert.AreEqual(expected, DeviceSyncUnreadPolicy.ShouldMarkConversationUnread(role));
}
