using System.Diagnostics;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshClient
{
    private static readonly TimeSpan WakeAuthenticationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WakeIdlePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WakePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WakeDisconnectTimeout = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim wakeConnectGate = new(1, 1);
    private readonly object replicationStatusGate = new();
    private ReplicationStatus replicationStatus = new(
        ReplicationPhase.WaitingForPeer, 0, null, null, null);
    private int backgroundWakeLeaseCount;
    private TaskCompletionSource<bool>? connectionAuthentication;

    private sealed class InboundRetryException(string reason) : Exception(reason);
    private sealed class InboundPermanentRejectException(string reason, Exception? inner = null)
        : Exception(reason, inner);

    public ReplicationStatus CurrentReplicationStatus
    {
        get
        {
            lock (replicationStatusGate) return replicationStatus;
        }
    }

    public string ReplicationStatusText => ReplicationStatusFormatter.Format(CurrentReplicationStatus);

    public bool ShouldShowReplicationStatus
        => ReplicationStatusDisplayPolicy.ShouldShow(CurrentReplicationStatus);

    private int CountPendingReplicationEvents(IReadOnlyCollection<string>? targetAccounts = null)
    {
        var targets = targetAccounts;
        if (targets is null)
        {
            var ownHandle = AppState.Norm(state.Profile.Handle);
            targets = ownHandle.Length == 0 ? Array.Empty<string>() : new[] { ownHandle };
        }
        return replicationEngine?.CountPendingTargetEvents(targets)
               ?? state.CountPendingReplicationEvents(targetAccounts);
    }

    private void SetReplicationStatus(
        ReplicationPhase phase,
        string? peerDeviceId = null,
        string? reason = null)
    {
        var pending = CountPendingReplicationEvents();
        var checkpoint = state.GetLastSuccessfulReplication();
        var next = new ReplicationStatus(
            phase,
            pending,
            peerDeviceId ?? checkpoint?.PeerDeviceId,
            checkpoint?.At,
            reason);
        var changed = false;
        lock (replicationStatusGate)
        {
            if (replicationStatus != next)
            {
                replicationStatus = next;
                changed = true;
            }
        }
        if (changed) ReplicationStateChanged?.Invoke();
    }

    private void RefreshReplicationStatus(string? peerDeviceId = null)
    {
        var pending = CountPendingReplicationEvents();
        var checkpoint = state.GetLastSuccessfulReplication();
        var phase = pending == 0 && checkpoint is not null
            ? ReplicationPhase.UpToDate
            : ReplicationPhase.WaitingForPeer;
        var next = new ReplicationStatus(
            phase,
            pending,
            peerDeviceId ?? checkpoint?.PeerDeviceId,
            checkpoint?.At,
            null);
        var changed = false;
        lock (replicationStatusGate)
        {
            if (replicationStatus != next)
            {
                replicationStatus = next;
                changed = true;
            }
        }
        if (changed) ReplicationStateChanged?.Invoke();
    }

    private void OnReplicationEngineActivity(ReplicationEngineActivity activity)
    {
        switch (activity.Name)
        {
            case "session.started":
            case "session.retried":
                SetReplicationStatus(ReplicationPhase.Connecting, activity.PeerDeviceId);
                break;
            case "bootstrap.started":
            case "bootstrap.progress":
                SetReplicationStatus(ReplicationPhase.Bootstrapping, activity.PeerDeviceId);
                break;
            case "session.established":
            case "offer.sent":
            case "request.received":
            case "batch.sent":
            case "batch.committed":
            case "protocol.frame_received":
            case "protocol.frame_sent":
                SetReplicationStatus(ReplicationPhase.Synchronizing, activity.PeerDeviceId);
                break;
            case "receipt.received":
            case "receipt.sent":
            case "bootstrap.persisted":
                RefreshReplicationStatus(activity.PeerDeviceId);
                break;
        }
    }

    private async Task<InboundDisposition> ProcessInboundAsync(
        MeshEnvelope envelope,
        InboundProcessingMode mode,
        ReplicationConnectionIdentity? identity,
        bool sessionSupportsReplication,
        CancellationToken ct,
        Action<Func<Task>>? registerPostAcknowledgement = null)
    {
        if (mode == InboundProcessingMode.Background
            && OnlineReplicationWakeInboundPolicy.RequiresForeground(envelope.Kind))
            return InboundDisposition.Defer;

        try
        {
            await HandleInboundAsync(
                envelope,
                mode,
                identity,
                sessionSupportsReplication,
                ct,
                registerPostAcknowledgement);
            return InboundDisposition.Processed;
        }
        catch (InboundRetryException ex)
        {
            TraceTransport("receive-retry", ex.Message);
            return InboundDisposition.Retry;
        }
        catch (InboundPermanentRejectException ex)
        {
            var reason = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (reason.Length > 200) reason = reason[..200];
            var rejectionId = "envelope:" + StableEnvelopeId(
                "inbound.reject",
                $"{AppState.Norm(envelope.From)}\0{envelope.FromDevice}\0{envelope.Id}\0{envelope.Kind}");
            if (!state.SaveInboundRejection(new MeshDb.InboundRejectionItem(
                    rejectionId,
                    envelope.Id,
                    null,
                    envelope.Kind,
                    AppState.Norm(envelope.From),
                    envelope.FromDevice,
                    reason,
                    DateTimeOffset.UtcNow)))
            {
                TraceTransport("receive-rejection-persistence-failed", reason);
                return InboundDisposition.Retry;
            }
            TraceTransport("receive-permanent-reject", reason);
            return InboundDisposition.PermanentReject;
        }
    }

    public async Task<OnlineReplicationWakeResult> SynchronizePendingAsync(CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var sessionStartedAt = DateTimeOffset.UtcNow;
        ReplicationDiagnostics.Record("wake.connection_started", ("device_id", MyDeviceId));
        SetReplicationStatus(ReplicationPhase.Connecting);
        try
        {
            await using var lease = await EnsureConnectedAsync(
                ConnectionPurpose.BackgroundWake,
                ct).ConfigureAwait(false);
            if (!lease.IsConnected)
                return OnlineReplicationWakeResult.Failed("connection_unavailable");

            var engine = OnlineReplicationEngine;
            var poller = replicationPoller;
            if (engine is null || poller is null)
                return OnlineReplicationWakeResult.Failed("replication_unavailable");

            ReplicationDiagnostics.Record("wake.connection_authenticated", ("device_id", MyDeviceId));
            var before = engine.GetProgress();
            await poller.PollOnceAsync(ct).ConfigureAwait(false);
            var lastPoll = Stopwatch.GetTimestamp();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var progress = engine.GetProgress();
                if (WakeQuiescencePolicy.IsComplete(
                        DateTimeOffset.UtcNow,
                        progress.LastActivity,
                        sessionStartedAt,
                        WakeIdlePeriod,
                        poller.HasImmediatelyDeliverableWork))
                    break;

                if (Stopwatch.GetElapsedTime(lastPoll) >= WakePollInterval)
                {
                    await poller.PollOnceAsync(ct).ConfigureAwait(false);
                    lastPoll = Stopwatch.GetTimestamp();
                    progress = engine.GetProgress();
                }
                await engine.WaitForActivityAsync(
                    progress.ActivityVersion,
                    TimeSpan.FromMilliseconds(250),
                    ct).ConfigureAwait(false);
            }

            var after = engine.GetProgress();
            var deferred = CountPendingReplicationEvents(state.ReplicationPeerCandidates());
            if (deferred == 0 && poller.HasPendingSynchronizationWork) deferred = 1;
            RefreshReplicationStatus(poller.LastOnlinePeerDevice);
            return OnlineReplicationWakeResultPolicy.FromProgress(
                before.CommittedEvents, after.CommittedEvents, deferred);
        }
        catch (OnlineReplicationError ex)
        {
            SetReplicationStatus(ReplicationPhase.AuthenticationFailed, reason: ex.Message);
            return OnlineReplicationWakeResult.Failed("authentication_failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetReplicationStatus(
                lifecycle.IsForeground ? ReplicationPhase.Failed : ReplicationPhase.DeferredByOperatingSystem,
                reason: "background_budget_expired");
            throw;
        }
        catch (Exception ex)
        {
            SetReplicationStatus(ReplicationPhase.Failed, reason: ex.GetType().Name);
            return OnlineReplicationWakeResult.Failed(ex.GetType().Name);
        }
    }
}
