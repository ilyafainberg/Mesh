using System.Text.Json;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReliableSynchronizationPolicyTests
{
    [TestMethod]
    public void ConnectionPurpose_BackgroundWakeBypassesOnlyContinuousTransportGuard()
    {
        Assert.IsFalse(ConnectionPurposePolicy.AllowsConnection(
            ConnectionPurpose.Foreground, isForeground: false, isMobile: true, isHeadless: false));
        Assert.IsTrue(ConnectionPurposePolicy.AllowsConnection(
            ConnectionPurpose.Foreground, isForeground: true, isMobile: true, isHeadless: false));
        Assert.IsTrue(ConnectionPurposePolicy.AllowsConnection(
            ConnectionPurpose.Foreground, isForeground: false, isMobile: false, isHeadless: false));
        Assert.IsTrue(ConnectionPurposePolicy.AllowsConnection(
            ConnectionPurpose.Foreground, isForeground: false, isMobile: true, isHeadless: true));
        Assert.IsTrue(ConnectionPurposePolicy.AllowsConnection(
            ConnectionPurpose.BackgroundWake, isForeground: false, isMobile: true, isHeadless: false));
    }

    [TestMethod]
    public void ContinuousTransport_DesktopDeactivationAndHeadlessModeStayConnected()
    {
        Assert.IsTrue(ContinuousTransportPolicy.ShouldRun(
            isMobile: false, isForeground: false, isHeadless: false));
        Assert.IsTrue(ContinuousTransportPolicy.ShouldRun(
            isMobile: false, isForeground: false, isHeadless: true));
        Assert.IsFalse(ContinuousTransportPolicy.ShouldRun(
            isMobile: true, isForeground: false, isHeadless: false));
    }

    [TestMethod]
    public void WakeCapability_RequiresOnlineReplicationWakeAndContentlessPush()
    {
        using var supported = JsonDocument.Parse(
            """{"protocolVersion":9,"onlineReplication":true,"onlineWake":true,"contentlessPush":true}""");
        using var noPush = JsonDocument.Parse(
            """{"protocolVersion":9,"onlineReplication":true,"onlineWake":true,"contentlessPush":false}""");
        using var oldProtocol = JsonDocument.Parse(
            """{"protocolVersion":8,"onlineReplication":true,"onlineWake":true,"contentlessPush":true}""");

        Assert.IsTrue(OnlineReplicationWakeCapabilityPolicy.IsSupported(supported.RootElement));
        Assert.IsFalse(OnlineReplicationWakeCapabilityPolicy.IsSupported(noPush.RootElement));
        Assert.IsFalse(OnlineReplicationWakeCapabilityPolicy.IsSupported(oldProtocol.RootElement));
    }

    [TestMethod]
    public async Task ConnectionLease_ReleasesExactlyOnce()
    {
        var releases = 0;
        var lease = new ReplicationConnectionLease(
            ConnectionPurpose.BackgroundWake,
            isConnected: true,
            () =>
            {
                Interlocked.Increment(ref releases);
                return ValueTask.CompletedTask;
            });

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public void AndroidWakePayload_RequiresSyncAndCurrentProtocol()
    {
        var current = new Dictionary<string, string>
        {
            ["mesh"] = $"{{\"type\":\"sync\",\"v\":{MeshProtocol.Version}}}"
        };
        var stale = new Dictionary<string, string>
        {
            ["mesh"] = "{\"type\":\"sync\",\"v\":8}"
        };
        var missingVersion = new Dictionary<string, string>
        {
            ["mesh"] = "{\"type\":\"sync\"}"
        };
        var notification = new Dictionary<string, string>
        {
            ["kind"] = "message"
        };
        var flat = new Dictionary<string, string>
        {
            ["mesh.type"] = "sync",
            ["mesh.v"] = MeshProtocol.Version.ToString()
        };

        Assert.AreEqual(AndroidReplicationWakePayloadKind.Sync, AndroidReplicationWakePolicy.Classify(current));
        Assert.AreEqual(AndroidReplicationWakePayloadKind.Sync, AndroidReplicationWakePolicy.Classify(flat));
        Assert.AreEqual(
            AndroidReplicationWakePayloadKind.UnsupportedMeshPayload,
            AndroidReplicationWakePolicy.Classify(stale));
        Assert.AreEqual(
            AndroidReplicationWakePayloadKind.UnsupportedMeshPayload,
            AndroidReplicationWakePolicy.Classify(missingVersion));
        Assert.AreEqual(AndroidReplicationWakePayloadKind.None, AndroidReplicationWakePolicy.Classify(notification));
    }

    [TestMethod]
    public void WakeQuiescence_RequiresIdlePeriodAndNoDeliverableWork()
    {
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var idle = TimeSpan.FromSeconds(2);

        Assert.IsFalse(WakeQuiescencePolicy.IsComplete(
            started.AddSeconds(1), null, started, idle, hasImmediatelyDeliverableWork: false));
        Assert.IsFalse(WakeQuiescencePolicy.IsComplete(
            started.AddSeconds(3), started, started, idle, hasImmediatelyDeliverableWork: true));
        Assert.IsTrue(WakeQuiescencePolicy.IsComplete(
            started.AddSeconds(3), started, started, idle, hasImmediatelyDeliverableWork: false));
    }

    [TestMethod]
    public void WakeResult_ReportsProcessedAndDeferredCounts()
    {
        var changed = OnlineReplicationWakeResultPolicy.FromProgress(10, 13, deferred: 2);
        var unchanged = OnlineReplicationWakeResultPolicy.FromProgress(13, 13, deferred: 4);

        Assert.AreEqual(OnlineReplicationWakeOutcome.NewData, changed.Outcome);
        Assert.AreEqual(3, changed.ProcessedEnvelopes);
        Assert.AreEqual(2, changed.DeferredEnvelopes);
        Assert.AreEqual(OnlineReplicationWakeOutcome.NoData, unchanged.Outcome);
        Assert.AreEqual(0, unchanged.ProcessedEnvelopes);
        Assert.AreEqual(4, unchanged.DeferredEnvelopes);
    }

    [TestMethod]
    public void ReplicationStatus_HidesRoutineProgressAndFormatsFailures()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var routinePhases = new[]
        {
            ReplicationPhase.UpToDate,
            ReplicationPhase.WaitingForPeer,
            ReplicationPhase.Connecting,
            ReplicationPhase.Synchronizing,
            ReplicationPhase.Bootstrapping,
            ReplicationPhase.DeferredByOperatingSystem
        };

        foreach (var phase in routinePhases)
        {
            var status = new ReplicationStatus(phase, 1, "peer", null, null);
            Assert.IsFalse(ReplicationStatusDisplayPolicy.ShouldShow(status));
            Assert.AreEqual(string.Empty, ReplicationStatusFormatter.Format(status, now));
        }

        var authenticationFailure = new ReplicationStatus(
            ReplicationPhase.AuthenticationFailed, 0, "peer", null, "unauthorized");
        Assert.IsTrue(ReplicationStatusDisplayPolicy.ShouldShow(authenticationFailure));
        Assert.AreEqual(
            "Synchronization authentication failed",
            ReplicationStatusFormatter.Format(authenticationFailure, now));

        var failure = new ReplicationStatus(
            ReplicationPhase.Failed, 0, "peer", now.AddMinutes(-5), "network");
        Assert.IsTrue(ReplicationStatusDisplayPolicy.ShouldShow(failure));
        Assert.AreEqual(
            "Last synced 5 minutes ago",
            ReplicationStatusFormatter.Format(failure, now));
    }

    [TestMethod]
    public async Task WakeCoordinator_CoalescesConcurrentNativeCallbacks()
    {
        var transport = new BlockingWakeTransport();
        var coordinator = new OnlineReplicationWakeCoordinator(transport);

        var first = coordinator.SynchronizePendingAsync(TimeSpan.FromSeconds(2));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.SynchronizePendingAsync(TimeSpan.FromSeconds(2));

        Assert.AreSame(first, second);
        transport.Completion.TrySetResult(OnlineReplicationWakeResult.NewData(2, 1));
        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, transport.Calls);
        Assert.IsTrue(results.All(result => result.Outcome == OnlineReplicationWakeOutcome.NewData));
    }

    [TestMethod]
    public async Task WakeCoordinator_EnforcesBoundedBudget()
    {
        var coordinator = new OnlineReplicationWakeCoordinator(new NeverCompletesWakeTransport());

        var result = await coordinator.SynchronizePendingAsync(TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(OnlineReplicationWakeOutcome.Failed, result.Outcome);
        Assert.AreEqual("timeout", result.Error);
    }

    private sealed class BlockingWakeTransport : IOnlineReplicationWakeTransport
    {
        public int Calls;
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OnlineReplicationWakeResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OnlineReplicationWakeResult> SynchronizePendingAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult(true);
            return await Completion.Task.WaitAsync(ct);
        }
    }

    private sealed class NeverCompletesWakeTransport : IOnlineReplicationWakeTransport
    {
        public async Task<OnlineReplicationWakeResult> SynchronizePendingAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return OnlineReplicationWakeResult.NoData();
        }
    }
}
