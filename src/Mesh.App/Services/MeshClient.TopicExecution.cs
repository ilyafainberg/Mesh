using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Maui.Networking;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshClient
{
    private static readonly TimeSpan DurableMaintenanceInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MobileBackgroundTopicHandoffTimeout = TimeSpan.FromSeconds(8);

    private bool ShouldMaintainContinuousTransport
        => ContinuousTransportPolicy.ShouldRun(
            PlatformCaps.IsMobile,
            lifecycle.IsForeground,
            MeshProcessContext.IsHeadless);

    private void OnForegroundChanged(bool isForeground)
    {
        if (isForeground)
        {
            TrackBackground(
                Task.Run(() =>
                {
                    using var operation = ManagedOperationDiagnostics.Begin("lifecycle.foreground-recovery");
                    DrainDeferredTopicRunUpdates();
                    ResumeTransport();
                }),
                "foreground topic recovery");
            return;
        }

        if (ShouldMaintainContinuousTransport)
        {
            TraceTransport("lifecycle-transport-retained", "desktop-or-headless-background");
            return;
        }

        TrackBackground(SuspendForegroundTransportAsync(), "mobile background transport suspension");
    }

    private void DrainDeferredTopicRunUpdates()
    {
        var changed = false;
        foreach (var deferred in state.ListDeferredTopicRunUpdates())
        {
            if (!state.TryApplyRemoteRunUpdate(deferred.Update))
            {
                TraceTransport("deferred-topic-update-failed", deferred.Update.RunId);
                break;
            }
            if (!state.DeleteDeferredTopicRunUpdate(deferred.EnvelopeId))
            {
                TraceTransport("deferred-topic-update-delete-failed", deferred.Update.RunId);
                break;
            }
            RememberReplay(topicEnvelopeReplay, deferred.EnvelopeId);
            changed = true;
        }
        if (changed) StateChanged?.Invoke();
    }

    private async Task SuspendForegroundTransportAsync()
    {
        if (ShouldMaintainContinuousTransport || Volatile.Read(ref backgroundWakeLeaseCount) > 0) return;
        await CompleteMobileBackgroundTopicHandoffAsync().ConfigureAwait(false);
        if (ShouldMaintainContinuousTransport || Volatile.Read(ref backgroundWakeLeaseCount) > 0) return;
        await connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ShouldMaintainContinuousTransport || Volatile.Read(ref backgroundWakeLeaseCount) > 0) return;
            await StopReplicationAsync("mobile-background").ConfigureAwait(false);
            var current = hub;
            hub = null;
            authenticated = false;
            connectionAuthentication?.TrySetCanceled();
            connectionAuthentication = null;
            authenticatedReplicationConnectionIdentity = null;
            StateChanged?.Invoke();
            if (current is null) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await current.StopAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TraceTransport("background-suspend-timeout", "foreground connection stop timed out");
            }
            catch (Exception ex) { TraceTransport("background-suspend-stop-failed", ex.Message); }

            try { await current.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { TraceTransport("background-suspend-dispose-failed", ex.Message); }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public void ResumeTransport()
    {
        if (MeshProcessContext.IsShuttingDown
            || !wantConnected || !ShouldMaintainContinuousTransport) return;
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is not null && Connected && IsCurrentReplicationConnectionIdentity(identity))
        {
            TryRegisterPushToken();
            WakeOnlineDelivery(identity, "transport-resumed");
            return;
        }
        ScheduleRecovery();
    }

    private void WakeOnlineDelivery(
        ReplicationConnectionIdentity identity,
        string reason)
        => WakeOnlineDelivery(identity, targetDeviceIds: null, reason);

    private void WakeOnlineDelivery(
        ReplicationConnectionIdentity identity,
        IReadOnlyCollection<string>? targetDeviceIds,
        string reason)
    {
        if (!Connected
            || !IsCurrentReplicationConnectionIdentity(identity)
            || (targetDeviceIds is null
                ? !HasLocalDurableWork()
                : !HasLocalDurableWorkFor(targetDeviceIds)))
            return;
        TraceTransport(
            "topic-delivery-wake",
            targetDeviceIds is null
                ? reason
                : $"{reason};targets={string.Join(",", targetDeviceIds.Order(StringComparer.Ordinal))}");
        lock (onlineRecoverySync)
        {
            pendingOnlineRecoveryIdentity = identity;
            if (targetDeviceIds is null)
            {
                pendingFullOnlineRecovery = true;
                pendingOnlineRecoveryTargets.Clear();
            }
            else if (!pendingFullOnlineRecovery)
            {
                foreach (var target in targetDeviceIds)
                {
                    if (!string.IsNullOrWhiteSpace(target))
                        pendingOnlineRecoveryTargets.Add(target);
                }
            }
        }
        if (targetDeviceIds is null)
        {
            onlineDeliveryRetry.Wake();
            return;
        }
        onlineDeliveryRetry.Wake();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        => ResumeTransport();

    private void ScheduleOnlineDeliveryRetry()
    {
        if (!wantConnected || !ShouldMaintainContinuousTransport) return;
        onlineDeliveryRetry.Schedule();
    }

    private async Task AttemptOnlineDeliveryRetryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is not null && Connected && IsCurrentReplicationConnectionIdentity(identity))
        {
            IReadOnlyCollection<string>? targets;
            lock (onlineRecoverySync)
            {
                targets = pendingFullOnlineRecovery || pendingOnlineRecoveryTargets.Count == 0
                    ? null
                    : pendingOnlineRecoveryTargets.ToArray();
                pendingFullOnlineRecovery = false;
                pendingOnlineRecoveryTargets.Clear();
            }
            await RecoverOnlineDeliveryAsync(identity, targets).ConfigureAwait(false);
        }
        else
            ScheduleRecovery();
    }

    private bool HasLocalDurableWork()
        => state.ListTopicOutbox().Any(item =>
               TopicOutboxStates.NeedsRemoteAcceptance(item.State)
               || item.State == TopicOutboxStates.CancelPending)
           || state.ListDeviceEnvelopeOutbox().Any(item =>
               item.State is not TopicOutboxStates.DeadLetter
                   and not TopicOutboxStates.Expired
               && !TopicTransportPolicy.IsControlDeliveryExpired(
                   item, timeProvider.GetUtcNow()));

    private bool HasLocalDurableWorkFor(IReadOnlyCollection<string> deviceIds)
    {
        if (deviceIds.Count == 0) return false;
        var online = new HashSet<string>(deviceIds, StringComparer.Ordinal);
        return state.ListTopicOutbox().Any(item =>
                  online.Contains(item.TargetDeviceId)
                  && (TopicOutboxStates.NeedsRemoteAcceptance(item.State)
                      || item.State == TopicOutboxStates.CancelPending))
               || state.ListDeviceEnvelopeOutbox().Any(item =>
                  online.Contains(item.TargetDeviceId)
                  && item.State is not TopicOutboxStates.DeadLetter
                      and not TopicOutboxStates.Expired
                  && !TopicTransportPolicy.IsControlDeliveryExpired(
                      item, timeProvider.GetUtcNow()));
    }

    private bool HasTopicRequestsAwaitingRemoteAcceptance()
        => state.ListTopicOutbox().Any(item =>
            TopicOutboxStates.NeedsRemoteAcceptance(item.State));

    public IReadOnlyList<TopicControlRecoveryStatus> GetTopicControlRecoveryStatus()
        => state.ListDeviceEnvelopeOutbox()
            .Where(item => TopicTransportPolicy.IsReceiptGatedControl(item)
                           && (item.State == TopicOutboxStates.DeadLetter
                               || item.RecoveryCount > 0))
            .Select(item =>
            {
                _ = TopicRunProtocol.TryParseUpdate(item.Plaintext, out var update);
                var now = timeProvider.GetUtcNow();
                return new TopicControlRecoveryStatus(
                    item.EnvelopeId,
                    update?.RunId ?? "",
                    update is null ? "unknown" : TopicControlProtocol.ControlPurpose(update),
                    item.State,
                    item.RecoveryCount,
                    item.State == TopicOutboxStates.DeadLetter
                    && item.RecoveryCount < TopicTransportPolicy.MaximumControlRecoveryCount
                    && now < item.CreatedAt + TopicTransportPolicy.DeadLetterRecoveryWindow,
                    item.LastError);
            })
            .ToArray();

    public async Task<TopicControlRecoveryBatchResult> RecoverDeadLetteredTopicControlsAsync(
        CancellationToken cancellationToken = default)
    {
        var recovery = new TopicControlOutboxRecovery(state, timeProvider);
        var results = new List<TopicControlRecoveryResult>();
        foreach (var item in state.ListDeviceEnvelopeOutbox()
                     .Where(candidate => candidate.State == TopicOutboxStates.DeadLetter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = recovery.Recover(item);
            results.Add(result);
            TraceTransport(
                result.Recovered
                    ? "topic-control-recovery-queued"
                    : "topic-control-recovery-deferred",
                $"{result.EnvelopeId}:{result.Kind}:{result.RecoveryCount}");
        }
        if (results.Count == 0)
            return new(0, 0, results);

        StateChanged?.Invoke();
        var recovered = results.Count(result => result.Recovered);
        if (recovered > 0)
        {
            var identity = authenticatedReplicationConnectionIdentity;
            if (identity is not null
                && Connected
                && IsCurrentReplicationConnectionIdentity(identity))
                await RecoverOnlineDeliveryAsync(identity).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            else
                ResumeTransport();
        }
        return new(
            recovered,
            results.Count - recovered,
            results);
    }

    private async Task CompleteMobileBackgroundTopicHandoffAsync()
    {
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null
            || !Connected
            || !IsCurrentReplicationConnectionIdentity(identity)
            || !HasTopicRequestsAwaitingRemoteAcceptance())
            return;

        var deadline = timeProvider.GetUtcNow() + MobileBackgroundTopicHandoffTimeout;
        while (!ShouldMaintainContinuousTransport
               && IsCurrentReplicationConnectionIdentity(identity)
               && HasTopicRequestsAwaitingRemoteAcceptance()
               && timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                await RecoverOnlineDeliveryAsync(identity).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TraceTransport("topic-background-handoff-failed", ex.GetType().Name);
            }

            if (!HasTopicRequestsAwaitingRemoteAcceptance()) return;
            await Task.Delay(
                    TimeSpan.FromMilliseconds(250), timeProvider, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }


    private async Task MaintainOnlineDeliveryAsync(ReplicationConnectionIdentity identity)
    {
        while (ShouldMaintainContinuousTransport && IsCurrentReplicationConnectionIdentity(identity))
        {
            await Task.Delay(
                DurableMaintenanceInterval, timeProvider, CancellationToken.None);
            if (!IsCurrentReplicationConnectionIdentity(identity)) return;
            try
            {
                await RecoverOnlineDeliveryAsync(identity);
            }
            catch (Exception ex)
            {
                TraceTransport("delivery-maintenance-failed", ex.GetType().Name);
            }
        }
    }

    private Task RecoverOnlineDeliveryAsync(
        ReplicationConnectionIdentity identity,
        IReadOnlyCollection<string>? targetDeviceIds = null)
    {
        lock (onlineRecoverySync)
        {
            pendingOnlineRecoveryIdentity = identity;
            if (targetDeviceIds is null)
            {
                pendingFullOnlineRecovery = true;
                pendingOnlineRecoveryTargets.Clear();
            }
            else if (!pendingFullOnlineRecovery)
            {
                foreach (var target in targetDeviceIds)
                {
                    if (!string.IsNullOrWhiteSpace(target))
                        pendingOnlineRecoveryTargets.Add(target);
                }
            }
            if (onlineRecoveryRunning) return onlineRecoveryTask;
            onlineRecoveryRunning = true;
            onlineRecoveryTask = Task.Run(DrainOnlineRecoveryAsync);
            return onlineRecoveryTask;
        }
    }

    private async Task DrainOnlineRecoveryAsync()
    {
        try
        {
            while (true)
            {
                ReplicationConnectionIdentity identity;
                IReadOnlyCollection<string>? targetDeviceIds;
                lock (onlineRecoverySync)
                {
                    identity = pendingOnlineRecoveryIdentity!;
                    pendingOnlineRecoveryIdentity = null;
                    targetDeviceIds = pendingFullOnlineRecovery
                        ? null
                        : pendingOnlineRecoveryTargets.ToArray();
                    pendingFullOnlineRecovery = false;
                    pendingOnlineRecoveryTargets.Clear();
                }
                await RecoverOnlineDeliveryCoreAsync(identity, targetDeviceIds).ConfigureAwait(false);
                lock (onlineRecoverySync)
                {
                    if (pendingOnlineRecoveryIdentity is not null) continue;
                    onlineRecoveryRunning = false;
                    return;
                }
            }
        }
        catch
        {
            lock (onlineRecoverySync)
            {
                pendingOnlineRecoveryIdentity = null;
                pendingFullOnlineRecovery = false;
                pendingOnlineRecoveryTargets.Clear();
                onlineRecoveryRunning = false;
            }
            throw;
        }
    }

    private async Task RecoverOnlineDeliveryCoreAsync(
        ReplicationConnectionIdentity identity,
        IReadOnlyCollection<string>? targetDeviceIds)
    {
        // Protocol 9: the relay keeps no durable staging to drain, so recovery is local only.
        // Inbound events replay through the online replication engine's persisted cursors/outbox;
        // here we only recover local topic-execution outbox state.
        if (!IsCurrentReplicationConnectionIdentity(identity)) return;
        await onlineFlushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentReplicationConnectionIdentity(identity)) return;
            var scope = new OnlineDeliveryTargetScope(targetDeviceIds);
            if (targetDeviceIds is null) RecoverInboundTopicRuns();
            foreach (var item in state.ListTopicOutbox())
            {
                if (!IsCurrentReplicationConnectionIdentity(identity)) return;
                if (!scope.Includes(item.TargetDeviceId)) continue;
                if (IsExpired(item))
                {
                    ExpireTopicRequest(item);
                    continue;
                }
                if (string.Equals(item.State, TopicOutboxStates.CancelPending, StringComparison.Ordinal))
                    await TryFlushTopicCancellationAsync(identity, item, CancellationToken.None);
                else if (item.State is not TopicOutboxStates.Expired and not TopicOutboxStates.Failed)
                    await TrySendTopicOutboxItemAsync(identity, item, CancellationToken.None);
            }

            foreach (var item in state.ListDeviceEnvelopeOutbox())
            {
                if (!IsCurrentReplicationConnectionIdentity(identity)) return;
                if (!scope.Includes(item.TargetDeviceId)) continue;
                if (!TopicTransportPolicy.ShouldRetryDeviceEnvelope(
                        item, timeProvider.GetUtcNow()))
                    continue;
                var candidate = item;
                if (item.State == TopicOutboxStates.DeadLetter)
                {
                    var recovery = new TopicControlOutboxRecovery(state, timeProvider)
                        .Recover(item);
                    TraceTransport(
                        recovery.Recovered
                            ? "topic-control-recovery-queued"
                            : "topic-control-recovery-deferred",
                        $"{recovery.EnvelopeId}:{recovery.Kind}:{recovery.RecoveryCount}");
                    if (!recovery.Recovered) continue;
                    candidate = state.GetDeviceEnvelopeOutbox(item.EnvelopeId);
                    if (candidate is null) continue;
                    StateChanged?.Invoke();
                }
                await TrySendDeviceEnvelopeOutboxItemAsync(
                    identity, candidate, CancellationToken.None);
            }
        }
        finally
        {
            onlineFlushGate.Release();
        }
        if (targetDeviceIds is null
                ? HasLocalDurableWork()
                : HasLocalDurableWorkFor(targetDeviceIds))
            ScheduleOnlineDeliveryRetry();
    }

    private void RecoverInboundTopicRuns()
    {
        var resumedThreads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.ListInboundTopicRuns(
                     InboundTopicRunStates.Accepted,
                     InboundTopicRunStates.Running))
        {
            var threadId = item.Request.ThreadId;
            if (activeTopicRuns.ContainsKey(item.RunId))
                continue;
            if (string.Equals(item.State, InboundTopicRunStates.Running, StringComparison.Ordinal))
            {
                QueueInterruptedInboundRun(item, "remote_execution_interrupted");
                continue;
            }
            if (!resumedThreads.Add(threadId)) continue;
            if (!TryStartInboundTopicRun(item.Request, item.SourceDeviceId))
            {
                resumedThreads.Remove(threadId);
                TraceTransport(
                    "topic-accepted-recovery-deferred",
                    item.RunId);
            }
        }
    }

    private void QueueInterruptedInboundRun(MeshDb.InboundTopicRunItem item, string failureCode)
    {
        var update = new TopicRunUpdatePayload(
            item.RunId,
            item.Request.ThreadId,
            TopicRunPhase.Failed,
            Error: "The remote device restarted before this run completed.",
            FailureCode: failureCode,
            Timestamp: timeProvider.GetUtcNow(),
            TriggerLineId: item.Request.TriggerLineId);
        _ = PersistInboundTopicTerminal(
            item.RunId,
            InboundTopicRunStates.Interrupted,
            update,
            item.SourceDeviceId);
    }

    private bool TryStartInboundTopicRun(TopicRunRequestPayload request, string sourceDeviceId)
    {
        if (!EnsureInboundTopicContext(request)) return false;
        var next = state.ListInboundTopicRuns(
                InboundTopicRunStates.Accepted,
                InboundTopicRunStates.Running)
            .FirstOrDefault(item => string.Equals(
                item.Request.ThreadId, request.ThreadId, StringComparison.Ordinal));
        if (next is null
            || !string.Equals(next.RunId, request.RunId, StringComparison.Ordinal))
            return true;
        var active = new ActiveTopicRun(
            request.RunId, request.ThreadId, sourceDeviceId, new CancellationTokenSource());
        if (!activeTopicRuns.TryAdd(request.RunId, active)) return true;
        var persisted = state.GetInboundTopicRun(request.RunId);
        if (persisted is null)
        {
            activeTopicRuns.TryRemove(request.RunId, out _);
            active.Cancellation.Dispose();
            return false;
        }
        if (!string.Equals(persisted.State, InboundTopicRunStates.Accepted, StringComparison.Ordinal))
            active.Cancellation.Cancel();
        TrackBackground(
            ExecuteInboundTopicRunAsync(request, active),
            $"topic run {request.RunId}");
        return true;
    }

    private void StartNextInboundTopicRun(string threadId)
    {
        while (true)
        {
            var next = state.ListInboundTopicRuns(
                    InboundTopicRunStates.Accepted,
                    InboundTopicRunStates.Running)
                .FirstOrDefault(item => string.Equals(
                    item.Request.ThreadId, threadId, StringComparison.Ordinal));
            if (next is null || activeTopicRuns.ContainsKey(next.RunId)) return;
            if (string.Equals(next.State, InboundTopicRunStates.Running, StringComparison.Ordinal))
            {
                QueueInterruptedInboundRun(next, "remote_execution_interrupted");
                continue;
            }
            if (!TryStartInboundTopicRun(next.Request, next.SourceDeviceId))
                TraceTransport("topic-accepted-recovery-deferred", next.RunId);
            return;
        }
    }
    private bool EnsureInboundTopicContext(TopicRunRequestPayload request)
    {
        OwnThread thread;
        try
        {
            thread = state.EnsureOwnThreadForDeviceRun(
                request.ThreadId,
                new ExecutionDevice(
                    MyDeviceId,
                    string.IsNullOrWhiteSpace(state.Profile.DeviceName)
                        ? null
                        : state.Profile.DeviceName,
                    PlatformCaps.DevicePlatform),
                request.TriggerAt);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or KeyNotFoundException)
        {
            TraceTransport("topic-context-rejected", ex.GetType().Name);
            return false;
        }

        var trigger = thread.Lines.FirstOrDefault(line =>
            string.Equals(line.Id, request.TriggerLineId, StringComparison.Ordinal));
        if (trigger is not null)
            return string.Equals(trigger.Role, "user", StringComparison.Ordinal)
                   && string.Equals(trigger.Text, request.TriggerText, StringComparison.Ordinal)
                   && trigger.At == request.TriggerAt;

        state.AddOwnChatLine(request.ThreadId, new ChatLine
        {
            Id = request.TriggerLineId,
            Role = "user",
            Text = request.TriggerText,
            Via = "agent",
            AddressedToAgent = true,
            At = request.TriggerAt
        });
        return true;
    }

    private async Task<TopicDispatchResult> QueueTopicRequestAsync(
        string targetDeviceId,
        TopicRunRequestPayload request,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken ct)
    {
        MeshDb.TopicOutboxItem item;
        try
        {
            item = topicRequestOutboxHandler.Queue(
                targetDeviceId, request, attachments);
        }
        catch (InvalidOperationException ex) when (
            ex.Message is "run_id_conflict" or "local_persistence_failed")
        {
            return TopicDispatchResult.Reject(
                ex.Message,
                request.RunId,
                ex.Message == "local_persistence_failed"
                    ? "The request could not be saved on this device."
                    : null);
        }

        if (IsExpired(item))
        {
            ExpireTopicRequest(item);
            return TopicDispatchResult.Reject("request_expired", request.RunId);
        }
        if (string.Equals(item.State, TopicOutboxStates.CancelPending, StringComparison.Ordinal))
            return TopicDispatchResult.Ok(request.RunId, "local_pending");

        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null || !Connected || !IsCurrentReplicationConnectionIdentity(identity))
        {
            ScheduleRecovery();
            return TopicDispatchResult.Ok(request.RunId, "local_pending");
        }

        await onlineFlushGate.WaitAsync(ct);
        try
        {
            var result = await TrySendTopicOutboxItemAsync(identity, item, ct);
            if (result is null)
            {
                ScheduleOnlineDeliveryRetry();
                return TopicDispatchResult.Ok(request.RunId, "local_pending");
            }
            if (result.Accepted)
                return TopicDispatchResult.Ok(
                    request.RunId,
                    TopicExecutionStatus.RelayAccepted);
            state.DeleteTopicOutbox(request.RunId);
            return TopicDispatchResult.Reject(result.Code, request.RunId, DescribeResult(result));
        }
        finally
        {
            onlineFlushGate.Release();
        }
    }

    private async Task<TopicDispatchResult> DispatchPersistedTopicRequestAsync(
        MeshDb.TopicOutboxItem item,
        CancellationToken ct)
    {
        if (IsExpired(item))
        {
            ExpireTopicRequest(item);
            return TopicDispatchResult.Reject(
                "request_expired", item.RunId, durable: true);
        }
        if (string.Equals(item.State, TopicOutboxStates.CancelPending, StringComparison.Ordinal))
            return TopicDispatchResult.Ok(item.RunId, "local_pending", durable: true);

        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null || !Connected || !IsCurrentReplicationConnectionIdentity(identity))
        {
            ScheduleRecovery();
            return TopicDispatchResult.Ok(item.RunId, "local_pending", durable: true);
        }

        await onlineFlushGate.WaitAsync(ct);
        try
        {
            var result = await TrySendTopicOutboxItemAsync(identity, item, ct);
            if (result is null)
            {
                ScheduleOnlineDeliveryRetry();
                return TopicDispatchResult.Ok(item.RunId, "local_pending", durable: true);
            }
            return result.Accepted
                ? TopicDispatchResult.Ok(
                    item.RunId, TopicExecutionStatus.RelayAccepted, durable: true)
                : TopicDispatchResult.Reject(
                    result.Code, item.RunId, DescribeResult(result), durable: true);
        }
        finally
        {
            onlineFlushGate.Release();
        }
    }

    private async Task<MeshSendResult?> TrySendTopicOutboxItemAsync(
        ReplicationConnectionIdentity identity,
        MeshDb.TopicOutboxItem item,
        CancellationToken ct)
    {
        if (!IsCurrentReplicationConnectionIdentity(identity)
            || !TopicTransportPolicy.ShouldAttemptRequestDelivery(
                item.State,
                item.UpdatedAt,
                timeProvider.GetUtcNow()))
            return null;

        var prepared = await PrepareTopicOutboxItemAsync(item, ct);
        if (prepared is null) return null;
        item = prepared;
        var delivery = await topicRequestOutboxDelivery.TrySendAsync(item, ct);
        var result = delivery.TransportResult;
        if (result is null) return null;
        if (result.Accepted)
        {
            var persistence = delivery.PersistenceResult
                              ?? TopicSendOutcomePersistenceResult.Ignored;
            if (persistence == TopicSendOutcomePersistenceResult.Applied)
                state.SetQueuedTopicRunStage(item.ThreadId, item.RunId, TopicQueueStage.Relay);
            else
                TraceStaleTopicSendCompletion(item, persistence, result.Code);
            TraceTransport("topic-request-relay-accepted", result.Code);
            return result;
        }

        if (IsPermanentTransportRejection(result.Code))
        {
            var persistence = delivery.PersistenceResult
                              ?? TopicSendOutcomePersistenceResult.Ignored;
            if (persistence != TopicSendOutcomePersistenceResult.Applied)
            {
                TraceStaleTopicSendCompletion(item, persistence, result.Code);
                return MeshSendResult.Ok();
            }
            state.SetQueuedTopicRunStage(item.ThreadId, item.RunId, TopicQueueStage.Failed);
            return result;
        }
        var deferred = delivery.PersistenceResult
                       ?? TopicSendOutcomePersistenceResult.Ignored;
        if (deferred != TopicSendOutcomePersistenceResult.Applied)
        {
            TraceStaleTopicSendCompletion(item, deferred, result.Code);
            return MeshSendResult.Ok();
        }
        TraceTransport("topic-request-deferred", result.Code);
        ScheduleOnlineDeliveryRetry();
        return null;
    }

    private void TraceStaleTopicSendCompletion(
        MeshDb.TopicOutboxItem attempted,
        TopicSendOutcomePersistenceResult persistence,
        string resultCode)
    {
        var current = state.GetTopicOutbox(attempted.RunId);
        TraceTransport(
            "topic-request-stale-send-completion",
            $"{attempted.RunId}:{resultCode}:{persistence}:"
            + (current is null
                ? "terminal-or-removed"
                : $"{current.State}:{current.RemoteStageOrdinal}"));
    }

    private async Task<MeshDb.TopicOutboxItem?> PrepareTopicOutboxItemAsync(
        MeshDb.TopicOutboxItem item,
        CancellationToken ct)
    {
        if (item.Attachments.Count == 0) return item;
        var manifest = item.Request.Attachments ?? Array.Empty<TopicRunAttachment>();
        if (manifest.Count != item.Attachments.Count) return null;

        var pointers = new List<TopicRunAttachment>(item.Attachments.Count);
        for (var index = 0; index < item.Attachments.Count; index++)
        {
            AttachmentPointer? pointer;
            try
            {
                pointer = await UploadAttachmentAsync(item.Attachments[index], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                TraceTransport("topic-attachment-upload-deferred", ex.GetType().Name);
                return null;
            }
            if (pointer is null) return null;
            pointers.Add(manifest[index] with
            {
                BlobId = pointer.BlobId,
                Key = pointer.Key,
                Sha256 = pointer.Sha256
            });
        }

        var now = timeProvider.GetUtcNow();
        var prepared = item with
        {
            Request = item.Request with
            {
                Attachments = pointers,
                AttachmentIds = pointers.Select(pointer => pointer.Id).ToList()
            },
            Attachments = [],
            UpdatedAt = now,
            LastError = null
        };
        if (!state.SaveTopicOutbox(prepared))
            throw new InvalidOperationException("The uploaded topic request could not be persisted.");
        return prepared;
    }

    private async Task<bool> QueueTopicCancellationAsync(
        string targetDeviceId,
        TopicRunCancelPayload cancel,
        CancellationToken ct)
    {
        var request = state.GetTopicOutbox(cancel.RunId);
        if (request is not null
            && string.Equals(request.State, TopicOutboxStates.Failed, StringComparison.Ordinal))
        {
            state.DeleteTopicOutbox(cancel.RunId);
            state.CompleteQueuedTopicRun(request.ThreadId, request.RunId);
            return true;
        }

        var item = PersistDeviceEnvelopeOutbox(
            targetDeviceId,
            MeshKinds.TopicRunCancel,
            TopicRunProtocol.CancelBody(cancel),
            null);
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null || !Connected || !IsCurrentReplicationConnectionIdentity(identity))
        {
            MarkTopicCancellationPending(request);
            ScheduleRecovery();
            return true;
        }

        await onlineFlushGate.WaitAsync(ct);
        try
        {
            var relayCancelled = request is not null
                                 && await TryCancelRelayTopicRequestAsync(identity, request, ct);
            if (relayCancelled)
            {
                state.DeleteDeviceEnvelopeOutbox(item.EnvelopeId);
                CompletePreDeliveryCancelledTopicRequest(request!);
                return true;
            }

            var sent = await TrySendDeviceEnvelopeOutboxItemAsync(identity, item, ct);
            if (sent is null || sent.Accepted)
            {
                MarkTopicCancellationPending(request);
                return true;
            }

            state.DeleteDeviceEnvelopeOutbox(item.EnvelopeId);
            return false;
        }
        finally
        {
            onlineFlushGate.Release();
        }
    }

    private async Task TryFlushTopicCancellationAsync(
        ReplicationConnectionIdentity identity,
        MeshDb.TopicOutboxItem request,
        CancellationToken ct)
    {
        var relayCancelled = await TryCancelRelayTopicRequestAsync(identity, request, ct);
        var cancellationId = DeviceEnvelopeId(
            MeshKinds.TopicRunCancel,
            TopicRunProtocol.CancelBody(new TopicRunCancelPayload(request.RunId, request.ThreadId)));
        var cancellation = state.ListDeviceEnvelopeOutbox().FirstOrDefault(item =>
            string.Equals(item.EnvelopeId, cancellationId, StringComparison.Ordinal));
        if (relayCancelled)
        {
            if (cancellation is not null)
                state.DeleteDeviceEnvelopeOutbox(cancellation.EnvelopeId);
            CompletePreDeliveryCancelledTopicRequest(request);
            return;
        }
        if (cancellation is not null)
            _ = await TrySendDeviceEnvelopeOutboxItemAsync(identity, cancellation, ct);
    }

    private void MarkTopicCancellationPending(MeshDb.TopicOutboxItem? request)
    {
        if (request is null) return;
        if (!state.SetTopicOutboxState(request.RunId, TopicOutboxStates.CancelPending))
            throw new InvalidOperationException(
                "The topic cancellation delivery state could not be persisted.");
        state.SetQueuedTopicRunStage(request.ThreadId, request.RunId, TopicQueueStage.Cancelling);
    }

    private void CompletePreDeliveryCancelledTopicRequest(MeshDb.TopicOutboxItem request)
    {
        var cancelled = new TopicRunUpdatePayload(
            request.RunId,
            request.ThreadId,
            TopicRunPhase.Cancelled,
            Status: "Cancelled",
            Timestamp: timeProvider.GetUtcNow(),
            TriggerLineId: request.TriggerLineId);
        if (!state.TryApplyRemoteRunUpdate(cancelled))
            throw new InvalidOperationException("The local cancellation state could not be persisted.");
        state.CompleteQueuedTopicRun(request.ThreadId, request.RunId);
        state.DeleteTopicOutbox(request.RunId);
    }

    private Task<bool> TryCancelRelayTopicRequestAsync(
        ReplicationConnectionIdentity identity,
        MeshDb.TopicOutboxItem request,
        CancellationToken ct)
    {
        // Protocol 9: there is no durable relay envelope queue to cancel. A queued topic request
        // that never reached the peer is cancelled purely from local state by the caller; the relay
        // is a pure opaque forwarder with nothing to retract. Report "no relay-side cancel".
        _ = identity;
        _ = request;
        _ = ct;
        return Task.FromResult(false);
    }

    private async Task<bool> QueueDeviceEnvelopeAsync(
        string targetDeviceId,
        string kind,
        string plaintext,
        CancellationToken ct,
        string? pushHint)
    {
        var item = await Task.Run(
            () => PersistDeviceEnvelopeOutbox(targetDeviceId, kind, plaintext, pushHint),
            ct).ConfigureAwait(false);
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is null || !Connected || !IsCurrentReplicationConnectionIdentity(identity))
        {
            ScheduleRecovery();
            return true;
        }

        await onlineFlushGate.WaitAsync(ct);
        try
        {
            await TrySendDeviceEnvelopeOutboxItemAsync(identity, item, ct);
            return true;
        }
        finally
        {
            onlineFlushGate.Release();
        }
    }

    private MeshDb.DeviceEnvelopeOutboxItem PersistDeviceEnvelopeOutbox(
        string targetDeviceId,
        string kind,
        string plaintext,
        string? pushHint)
    {
        var item = new MeshDb.DeviceEnvelopeOutboxItem(
            DeviceEnvelopeId(kind, plaintext),
            targetDeviceId,
            kind,
            plaintext,
            pushHint,
            timeProvider.GetUtcNow());
        if (string.Equals(kind, MeshKinds.TopicRunUpdate, StringComparison.Ordinal)
            && TopicRunProtocol.TryParseUpdate(plaintext, out var update)
            && TopicControlProtocol.IsReceipt(update))
        {
            var persistence = state.GetOrCreateTopicReceiptOutbox(item);
            RuntimeDiagnostics.Current?.RecordEvent(
                "topic-receipt-outbox",
                $"envelope={AppState.StableDiagnosticId(item.EnvelopeId)}"
                + $";run={AppState.StableDiagnosticId(update.RunId)}"
                + $";result={persistence.Kind.ToString().ToLowerInvariant()}");
            if (persistence.Kind == TopicReceiptOutboxPersistenceKind.IdentityConflict)
                throw new InvalidOperationException("topic_receipt_outbox_identity_conflict");
            return persistence.Item;
        }
        if (!state.SaveDeviceEnvelopeOutbox(item))
            throw new InvalidOperationException("The durable device envelope could not be persisted.");
        return state.GetDeviceEnvelopeOutbox(item.EnvelopeId)
               ?? throw new InvalidOperationException(
                   "The durable device envelope could not be read.");
    }

    private async Task<MeshSendResult?> TrySendDeviceEnvelopeOutboxItemAsync(
        ReplicationConnectionIdentity identity,
        MeshDb.DeviceEnvelopeOutboxItem item,
        CancellationToken ct)
    {
        if (!IsCurrentReplicationConnectionIdentity(identity))
            return null;
        var result = await topicControlOutboxDelivery.TrySendAsync(item, ct);
        var persisted = state.GetDeviceEnvelopeOutbox(item.EnvelopeId);
        if (result?.Accepted == true)
        {
            if (RequiresDevicePersistenceReceipt(item))
            {
                TraceTransport(
                    "device-control-relay-accepted-awaiting-receipt",
                    item.EnvelopeId);
                ScheduleOnlineDeliveryRetry();
            }
            else
            {
                TraceTransport("device-envelope-relay-accepted", result.Code);
            }
        }
        else if (persisted?.State == TopicOutboxStates.DeadLetter)
        {
            TraceTransport(
                "device-control-dead-lettered",
                $"{item.EnvelopeId}:{persisted.LastError}");
            StateChanged?.Invoke();
        }
        else if (result is not null && IsPermanentTransportRejection(result.Code))
        {
            TraceTransport("device-envelope-permanent-reject", result.Code);
        }
        else
        {
            if (result is not null)
                TraceTransport("device-envelope-deferred", result.Code);
            if (persisted is not null) ScheduleOnlineDeliveryRetry();
        }
        return result;
    }

    private async Task<MeshSendResult?> TrySendTargetedTopicEnvelopeCoreAsync(
        ReplicationConnectionIdentity identity,
        string targetDeviceId,
        string kind,
        string plaintext,
        string envelopeId,
        string? pushHint,
        CancellationToken ct,
        bool ephemeral = false)
    {
        if (!IsCurrentReplicationConnectionIdentity(identity)
            || string.IsNullOrWhiteSpace(targetDeviceId)
            || string.Equals(targetDeviceId, identity.DeviceId, StringComparison.Ordinal))
            return null;

        var keys = await ResolveDeviceKeysAsync(identity.NormalizedHandle);
        var targetKeys = keys.Where(key =>
                string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (targetKeys.Length == 0)
        {
            keys = await ResolveDeviceKeysAsync(identity.NormalizedHandle, refresh: true);
            targetKeys = keys.Where(key =>
                    string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        if (!IsCurrentReplicationConnectionIdentity(identity) || targetKeys.Length != 1) return null;

        var ciphertext = MessageCrypto.Encrypt(plaintext, targetKeys);
        if (ciphertext is null) return null;
        var envelope = MeshEnvelope.Create(
            identity.Handle,
            identity.NormalizedHandle,
            kind,
            ciphertext,
            IdentityService.Sign(identity.PrivateKey, ciphertext),
            fromDevice: identity.DeviceId,
            toDevice: targetDeviceId,
            pushHint: pushHint,
            id: envelopeId);
        try
        {
            var method = ephemeral
                ? MeshHubProtocol.SendEphemeralEnvelope
                : MeshHubProtocol.SendEnvelope;
            if (string.Equals(kind, MeshKinds.TopicRunRequest, StringComparison.Ordinal))
            {
                var request = JsonSerializer.Deserialize<TopicRunRequestPayload>(
                    plaintext, Json);
                var attempt = request is null
                    ? null
                    : state.BeginTopicTransportAttempt(request.RunId);
                if (attempt is null) return null;
                RuntimeDiagnostics.Current?.RecordEvent(
                    "topic-transport-attempt",
                    $"operation={AppState.StableDiagnosticId(attempt.TriggerId)}"
                    + $";run={AppState.StableDiagnosticId(attempt.RunId)}"
                    + $";envelope={AppState.StableDiagnosticId(envelopeId)}"
                    + $";attempt={attempt.Ordinal};transport_entered=true");
            }
            if (supportsSendResults)
                return await identity.Connection.InvokeAsync<MeshSendResult>(method, envelope, ct);
            await identity.Connection.InvokeAsync(method, envelope, ct);
            return MeshSendResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceTransport("device-envelope-send-deferred", ex.GetType().Name);
            return null;
        }
    }

    private static string DeviceEnvelopeId(string kind, string plaintext)
    {
        if (string.Equals(kind, MeshKinds.TopicRunCancel, StringComparison.Ordinal)
            && TopicRunProtocol.TryParseCancel(plaintext, out var cancel))
            return StableEnvelopeId("topic.cancel", cancel.RunId);
        if (string.Equals(kind, MeshKinds.TopicRunUpdate, StringComparison.Ordinal)
            && TopicRunProtocol.TryParseUpdate(plaintext, out var update))
        {
            if (TopicControlProtocol.IsAcceptance(update)
                || TopicControlProtocol.IsExecutionQueued(update)
                || TopicControlProtocol.IsTerminal(update)
                || TopicControlProtocol.IsReceipt(update))
                return StableEnvelopeId(
                    TopicControlProtocol.ControlPurpose(update), update.RunId);
        }
        return Guid.NewGuid().ToString("n");
    }

    private static bool RequiresDevicePersistenceReceipt(
        MeshDb.DeviceEnvelopeOutboxItem item)
        => TopicTransportPolicy.IsReceiptGatedControl(item);

    private static string StableEnvelopeId(string purpose, string runId)
        => TopicControlProtocol.EnvelopeId(purpose, runId);

    private static bool IsPermanentTransportRejection(string code)
        => TopicTransportPolicy.IsPermanentRejection(code);

    private bool IsExpired(MeshDb.TopicOutboxItem item)
        => timeProvider.GetUtcNow() >= item.CreatedAt + TopicTransportPolicy.RequestLifetime;

    private void ExpireTopicRequest(MeshDb.TopicOutboxItem item)
    {
        state.SetTopicOutboxState(item.RunId, TopicOutboxStates.Expired, "request_expired");
        state.SetQueuedTopicRunStage(item.ThreadId, item.RunId, TopicQueueStage.Expired);
        state.ClearRemoteRunProjection(item.ThreadId, item.RunId);
        TraceTransport("topic-request-expired", "ttl");
    }

    private void TraceTransport(string eventName, string? detail = null)
    {
        var safeDetail = string.IsNullOrWhiteSpace(detail)
            ? null
            : detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safeDetail?.Length > 160) safeDetail = safeDetail[..160];
        var message = safeDetail is null
            ? $"transport {eventName}"
            : $"transport {eventName}: {safeDetail}";
        Log?.Invoke(message);
        RuntimeDiagnostics.Current?.RecordEvent("transport", message);
    }
}
