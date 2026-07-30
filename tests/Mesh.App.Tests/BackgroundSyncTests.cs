using System.Text.Json;
using Mesh.App.Services;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class BackgroundSyncTests
{
    [TestMethod]
    public async Task SnapshotResponsePolicyStartsTrackedWorkAfterAcknowledgement()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = DeviceSyncSnapshotResponsePolicy.Start(async () =>
        {
            started.TrySetResult();
            await release.Task;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(response.IsCompleted);
        release.TrySetResult();
        await response.WaitAsync(TimeSpan.FromSeconds(2));
    }

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
    [DataRow("{\"protocolVersion\":8,\"durableDelivery\":true,\"backgroundSync\":true}", true)]
    [DataRow("{\"protocolVersion\":8,\"durableDelivery\":true,\"backgroundSync\":false}", false)]
    [DataRow("{\"protocolVersion\":8,\"durableDelivery\":false,\"backgroundSync\":true}", false)]
    [DataRow("{\"protocolVersion\":6,\"durableDelivery\":true,\"backgroundSync\":true}", false)]
    public void BackgroundSyncRequiresBothRelayCapabilities(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual(expected, BackgroundSyncCapabilityPolicy.IsSupported(document.RootElement));
    }

    [TestMethod]
    public async Task AuthenticatedHandshakeDrainsBeforeDirectoryFailureAndRetriesDiscovery()
    {
        var sequence = new List<string>();
        var discoveryAttempts = 0;
        var failures = 0;

        await DeviceSyncHandshakeCoordinator.RunAsync(
            drain: () =>
            {
                sequence.Add("drain");
                return Task.FromResult(DeviceSyncQueueDrainResult.Completed(0));
            },
            discoverSnapshots: () =>
            {
                sequence.Add("directory");
                discoveryAttempts++;
                if (discoveryAttempts < 3)
                    throw new HttpRequestException("directory unavailable");
                return Task.CompletedTask;
            },
            shouldContinue: () => true,
            reportFailure: _ => failures++,
            retryDelay: _ => Task.CompletedTask);

        CollectionAssert.AreEqual(
            new[] { "drain", "directory", "directory", "directory" },
            sequence);
        Assert.AreEqual(2, failures);
    }

    [TestMethod]
    public async Task DrainExceptionAbortsSnapshotDiscovery()
    {
        var discoveryCalls = 0;
        var recoveryCalls = 0;

        var result = await DeviceSyncHandshakeCoordinator.RunAsync(
            drain: () => throw new HttpRequestException("drain unavailable"),
            discoverSnapshots: () =>
            {
                discoveryCalls++;
                return Task.CompletedTask;
            },
            shouldContinue: () => true,
            reportFailure: _ => recoveryCalls++);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("queue_drain_exception", result.Error);
        Assert.IsInstanceOfType<HttpRequestException>(result.Exception);
        Assert.AreEqual(0, discoveryCalls);
        Assert.AreEqual(1, recoveryCalls);
    }

    [TestMethod]
    public async Task DrainRejectionAbortsSnapshotDiscovery()
    {
        var discoveryCalls = 0;
        var recoveryCalls = 0;

        var result = await DeviceSyncHandshakeCoordinator.RunAsync(
            drain: () => Task.FromResult(
                DeviceSyncQueueDrainResult.Failed("queue_ack_rejected")),
            discoverSnapshots: () =>
            {
                discoveryCalls++;
                return Task.CompletedTask;
            },
            shouldContinue: () => true,
            reportFailure: _ => recoveryCalls++);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("queue_ack_rejected", result.Error);
        Assert.AreEqual(0, discoveryCalls);
        Assert.AreEqual(1, recoveryCalls);
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

    [TestMethod]
    public async Task BackgroundRouteIsSeparateFromForegroundOnlinePresence()
    {
        var backplane = new InMemoryBackplane();

        await backplane.SetTransientDeviceRouteAsync("ifain", "phone");

        Assert.IsNull(await backplane.GetInstanceForAsync("ifain"));
        Assert.IsNull(await backplane.GetInstanceForDeviceAsync("ifain", "phone"));
        Assert.AreEqual(
            backplane.InstanceId,
            await backplane.GetTransientInstanceForDeviceAsync("ifain", "phone"));
        await backplane.SetDevicePresenceAsync("ifain", "phone");
        await backplane.ClearTransientDeviceRouteAsync("ifain", "phone");
        Assert.IsNull(await backplane.GetTransientInstanceForDeviceAsync("ifain", "phone"));
        Assert.AreEqual(backplane.InstanceId, await backplane.GetInstanceForDeviceAsync("ifain", "phone"));
    }

    [TestMethod]
    public void RegistryTracksBackgroundRoutesWithoutForegroundPresence()
    {
        var registry = new ConnectionRegistry();
        registry.Add(
            "background", "ifain", "nonce",
            supportsDurableDelivery: true, isBackgroundSync: true);
        registry.MarkAuthenticated("background", "phone-key");
        var deviceId = DeviceProtocol.DeviceId("phone-key");

        Assert.HasCount(0, registry.LocalHandles(includeBackgroundSync: false));
        Assert.HasCount(0, registry.LocalDevices(includeBackgroundSync: false));
        CollectionAssert.AreEqual(
            new[] { ("ifain", deviceId) },
            registry.LocalBackgroundDevices().ToArray());
        Assert.IsTrue(registry.HasBackgroundConnectionForDevice("ifain", deviceId));

        registry.Remove("background");
        Assert.IsFalse(registry.HasBackgroundConnectionForDevice("ifain", deviceId));
    }

    [TestMethod]
    public void RevokedDeviceConnectionsAreRemovedFromEveryDeliveryIndex()
    {
        var registry = new ConnectionRegistry();
        registry.Add("current", "ifain", "nonce-1", supportsDurableDelivery: true);
        registry.Add("revoked", "ifain", "nonce-2", supportsDurableDelivery: true);
        registry.MarkAuthenticated("current", "current-device-key");
        registry.MarkAuthenticated("revoked", "revoked-device-key");
        var revokedDeviceId = DeviceProtocol.DeviceId("revoked-device-key");

        var revoked = registry.RevokeUnauthorizedDevices(
            "ifain",
            new HashSet<string>(["current-device-key"], StringComparer.Ordinal));

        CollectionAssert.AreEqual(new[] { revokedDeviceId }, revoked.ToArray());
        CollectionAssert.AreEqual(new[] { "current" }, registry.ConnectionsFor("ifain").ToArray());
        Assert.HasCount(0, registry.ConnectionsForDevice("ifain", revokedDeviceId));
        Assert.IsFalse(registry.Get("revoked")!.Authenticated);
    }

    [DataTestMethod]
    [DataRow("attachment inbox is disposed", true)]
    [DataRow("attachment inbox is full", true)]
    [DataRow("attachment storage is unavailable", true)]
    [DataRow("duplicate or conflicting attachment storage", true)]
    [DataRow("duplicate attachment chunk", false)]
    [DataRow("conflicting attachment metadata", false)]
    public void AttachmentPersistenceFailuresRemainRetryable(string error, bool expected)
        => Assert.AreEqual(expected, InboundAttachmentFailurePolicy.ShouldRetry(error));

    [DataTestMethod]
    [DataRow("user", true)]
    [DataRow("assistant", false)]
    [DataRow("tool", false)]
    public void OnlyInboundConversationLinesBecomeUnread(string role, bool expected)
        => Assert.AreEqual(expected, DeviceSyncUnreadPolicy.ShouldMarkConversationUnread(role));

    [TestMethod]
    public void InboundAcknowledgementRequiresTerminalProcessingOutcome()
    {
        Assert.IsTrue(InboundAcknowledgementPolicy.ShouldAcknowledge(InboundDisposition.Processed));
        Assert.IsTrue(InboundAcknowledgementPolicy.ShouldAcknowledge(InboundDisposition.PermanentReject));
        Assert.IsFalse(InboundAcknowledgementPolicy.ShouldAcknowledge(InboundDisposition.Retry));
        Assert.IsFalse(InboundAcknowledgementPolicy.ShouldAcknowledge(InboundDisposition.Defer));
    }

    [TestMethod]
    public void QueueDrainRetryFailsWhileBackgroundDeferralSucceeds()
    {
        var retry = DeviceSyncQueueDrainPolicy.StopResult(InboundDisposition.Retry, 2);
        var defer = DeviceSyncQueueDrainPolicy.StopResult(InboundDisposition.Defer, 2);

        Assert.IsFalse(retry.Succeeded);
        Assert.AreEqual("queue_processing_retry", retry.Error);
        Assert.AreEqual(2, retry.ProcessedEnvelopes);
        Assert.IsTrue(defer.Succeeded);
        Assert.IsNull(defer.Error);
        Assert.AreEqual(2, defer.ProcessedEnvelopes);
    }

    [TestMethod]
    public void SnapshotCompletionUsesBoundedDeviceQueueTransport()
    {
        var method = DeviceSyncTransportPolicy.MethodFor(
            DeviceSyncKinds.EnvelopeSnapshotComplete);

        Assert.AreEqual(MeshHubProtocol.QueueEnqueue, method);
        Assert.AreNotEqual(MeshHubProtocol.SendEnvelope, method);
    }
}
