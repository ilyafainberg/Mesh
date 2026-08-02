using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Maui.Networking;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshClient
{
    private static readonly TimeSpan DurableMaintenanceInterval = TimeSpan.FromSeconds(15);

    private void OnForegroundChanged(bool isForeground)
    {
        if (isForeground)
        {
            ResumeTransport();
            return;
        }

        TrackBackground(SuspendForegroundTransportAsync(), "foreground transport suspension");
    }

    private async Task SuspendForegroundTransportAsync()
    {
        await connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (lifecycle.IsForeground) return;
            var current = hub;
            hub = null;
            authenticated = false;
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
        if (!wantConnected || !lifecycle.IsForeground) return;
        var identity = authenticatedReplicationConnectionIdentity;
        if (identity is not null && Connected && IsCurrentReplicationConnectionIdentity(identity))
        {
            TryRegisterPushToken();
            TrackBackground(RecoverOnlineDeliveryAsync(identity), "durable delivery resume");
            return;
        }
        ScheduleRecovery();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        => ResumeTransport();

    private void ScheduleOnlineDeliveryRetry()
    {
        if (!wantConnected || !lifecycle.IsForeground || Interlocked.Exchange(ref onlineRetryScheduled, 1) == 1) return;
        TrackBackground(Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                var identity = authenticatedReplicationConnectionIdentity;
                if (identity is not null && Connected && IsCurrentReplicationConnectionIdentity(identity))
                    await RecoverOnlineDeliveryAsync(identity);
                else
                    ScheduleRecovery();
            }
            finally
            {
                Interlocked.Exchange(ref onlineRetryScheduled, 0);
                if (wantConnected && lifecycle.IsForeground && Connected && HasLocalDurableWork())
                    ScheduleOnlineDeliveryRetry();
            }
        }), "durable delivery retry");
    }

    private bool HasLocalDurableWork()
        => state.ListTopicOutbox().Any(item => item.State is TopicOutboxStates.Pending
            or TopicOutboxStates.CancelPending)
           || state.ListDeviceEnvelopeOutbox().Count > 0;


    private async Task MaintainOnlineDeliveryAsync(ReplicationConnectionIdentity identity)
    {
        while (lifecycle.IsForeground && IsCurrentReplicationConnectionIdentity(identity))
        {
            await Task.Delay(DurableMaintenanceInterval);
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

    private async Task RecoverOnlineDeliveryAsync(ReplicationConnectionIdentity identity)
    {
        // Protocol 9: the relay keeps no durable staging to drain, so recovery is local only.
        // Inbound events replay through the online replication engine's persisted cursors/outbox;
        // here we only recover local topic-execution outbox state.
        if (!IsCurrentReplicationConnectionIdentity(identity)) return;
        RecoverInboundTopicRuns();

        await onlineFlushGate.WaitAsync();
        try
        {
            if (!IsCurrentReplicationConnectionIdentity(identity)) return;
            foreach (var item in state.ListTopicOutbox())
            {
                if (!IsCurrentReplicationConnectionIdentity(identity)) return;
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
                await TrySendDeviceEnvelopeOutboxItemAsync(identity, item, CancellationToken.None);
            }
        }
        finally
        {
            onlineFlushGate.Release();
        }
        if (HasLocalDurableWork()) ScheduleOnlineDeliveryRetry();
    }

    private void RecoverInboundTopicRuns()
    {
        var blockedThreads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.ListInboundTopicRuns(
                     InboundTopicRunStates.Accepted,
                     InboundTopicRunStates.Running))
        {
            var threadId = item.Request.ThreadId;
            if (blockedThreads.Contains(threadId) || activeTopicRuns.ContainsKey(item.RunId))
                continue;
            if (string.Equals(item.State, InboundTopicRunStates.Accepted, StringComparison.Ordinal))
            {
                if (!TryStartInboundTopicRun(item.Request, item.SourceDeviceId))
                {
                    QueueInterruptedInboundRun(item, "remote_execution_recovery_failed");
                    blockedThreads.Add(threadId);
                }
                continue;
            }
            QueueInterruptedInboundRun(item, "remote_execution_interrupted");
            blockedThreads.Add(threadId);
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
            Timestamp: DateTimeOffset.UtcNow);
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
        var next = state.ListInboundTopicRuns(
                InboundTopicRunStates.Accepted,
                InboundTopicRunStates.Running)
            .FirstOrDefault(item => string.Equals(
                item.Request.ThreadId, threadId, StringComparison.Ordinal));
        if (next is null || activeTopicRuns.ContainsKey(next.RunId)) return;
        if (string.Equals(next.State, InboundTopicRunStates.Running, StringComparison.Ordinal))
        {
            QueueInterruptedInboundRun(next, "remote_execution_interrupted");
            return;
        }
        if (!TryStartInboundTopicRun(next.Request, next.SourceDeviceId))
            QueueInterruptedInboundRun(next, "remote_execution_recovery_failed");
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
        var existing = state.GetTopicOutbox(request.RunId);
        MeshDb.TopicOutboxItem item;
        if (existing is not null)
        {
            if (!string.Equals(existing.ThreadId, request.ThreadId, StringComparison.Ordinal)
                || !string.Equals(existing.TriggerLineId, request.TriggerLineId, StringComparison.Ordinal)
                || !string.Equals(existing.TargetDeviceId, targetDeviceId, StringComparison.Ordinal))
                return TopicDispatchResult.Reject("run_id_conflict", request.RunId);
            item = existing;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            item = new MeshDb.TopicOutboxItem(
                request.RunId,
                request.ThreadId,
                request.TriggerLineId,
                targetDeviceId,
                request,
                attachments.Select(CloneAttachment).ToList(),
                TopicOutboxStates.Pending,
                now,
                now);
            if (!state.SaveTopicOutbox(item))
                return TopicDispatchResult.Reject(
                    "local_persistence_failed", request.RunId,
                    "The request could not be saved on this device.");
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
                return TopicDispatchResult.Ok(request.RunId, result.Code);
            state.DeleteTopicOutbox(request.RunId);
            return TopicDispatchResult.Reject(result.Code, request.RunId, DescribeResult(result));
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
            || !string.Equals(item.State, TopicOutboxStates.Pending, StringComparison.Ordinal))
            return null;

        var prepared = await PrepareTopicOutboxItemAsync(item, ct);
        if (prepared is null) return null;
        item = prepared;
        var result = await TrySendTargetedTopicEnvelopeCoreAsync(
            identity,
            item.TargetDeviceId,
            MeshKinds.TopicRunRequest,
            TopicRunProtocol.RequestBody(item.Request),
            item.RunId,
            null,
            ct);
        if (result is null) return null;
        if (result.Accepted)
        {
            if (!state.SetTopicOutboxState(item.RunId, TopicOutboxStates.RelayQueued))
                throw new InvalidOperationException("The topic request delivery state could not be persisted.");
            state.SetQueuedTopicRunStage(item.ThreadId, item.RunId, TopicQueueStage.Relay);
            TraceTransport("topic-request-relay-accepted", result.Code);
            return result;
        }

        if (IsPermanentTransportRejection(result.Code))
        {
            state.SetTopicOutboxState(item.RunId, TopicOutboxStates.Failed, result.Code);
            state.SetQueuedTopicRunStage(item.ThreadId, item.RunId, TopicQueueStage.Failed);
            return result;
        }
        state.SetTopicOutboxState(item.RunId, TopicOutboxStates.Pending, result.Code);
        TraceTransport("topic-request-deferred", result.Code);
        ScheduleOnlineDeliveryRetry();
        return null;
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

        var now = DateTimeOffset.UtcNow;
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
            Timestamp: DateTimeOffset.UtcNow,
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
        var item = PersistDeviceEnvelopeOutbox(targetDeviceId, kind, plaintext, pushHint);
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
            DateTimeOffset.UtcNow);
        if (!state.SaveDeviceEnvelopeOutbox(item))
            throw new InvalidOperationException("The durable device envelope could not be persisted.");
        return item;
    }

    private async Task<MeshSendResult?> TrySendDeviceEnvelopeOutboxItemAsync(
        ReplicationConnectionIdentity identity,
        MeshDb.DeviceEnvelopeOutboxItem item,
        CancellationToken ct)
    {
        var result = await TrySendTargetedTopicEnvelopeCoreAsync(
            identity,
            item.TargetDeviceId,
            item.Kind,
            item.Plaintext,
            item.EnvelopeId,
            item.PushHint,
            ct);
        if (result?.Accepted == true)
        {
            state.DeleteDeviceEnvelopeOutbox(item.EnvelopeId);
            TraceTransport("device-envelope-relay-accepted", result.Code);
        }
        else if (result is not null && IsPermanentTransportRejection(result.Code))
        {
            state.DeleteDeviceEnvelopeOutbox(item.EnvelopeId);
            TraceTransport("device-envelope-permanent-reject", result.Code);
        }
        else
        {
            if (result is not null)
                TraceTransport("device-envelope-deferred", result.Code);
            ScheduleOnlineDeliveryRetry();
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
            if (update.Phase == TopicRunPhase.Queued)
                return StableEnvelopeId("topic.queued", update.RunId);
            if (update.Phase is TopicRunPhase.Completed
                or TopicRunPhase.Failed
                or TopicRunPhase.Cancelled)
                return StableEnvelopeId("topic.terminal", update.RunId);
        }
        return Guid.NewGuid().ToString("n");
    }

    private static string StableEnvelopeId(string purpose, string runId)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{purpose}\0{runId}"))).ToLowerInvariant();

    private static bool IsPermanentTransportRejection(string code)
        => code is "invalid_signature"
            or "message_too_large"
            or "invalid_push_hint"
            or "target_device_unknown"
            or "sync_target_unknown"
            or "device_revoked";

    private static bool IsExpired(MeshDb.TopicOutboxItem item)
        => DateTimeOffset.UtcNow - item.CreatedAt >= TopicTransportPolicy.RequestLifetime;

    private void ExpireTopicRequest(MeshDb.TopicOutboxItem item)
    {
        state.SetTopicOutboxState(item.RunId, TopicOutboxStates.Expired, "request_expired");
        state.SetQueuedTopicRunStage(item.ThreadId, item.RunId, TopicQueueStage.Expired);
        state.ClearRemoteRunProjection(item.ThreadId, item.RunId);
        TraceTransport("topic-request-expired", "ttl");
    }

    private static ChatAttachment CloneAttachment(ChatAttachment attachment)
        => new(attachment.Name, attachment.MimeType, attachment.Data.ToArray());

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
