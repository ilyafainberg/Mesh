using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Maui.Networking;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class MeshClient
{
    private static readonly TimeSpan DurableMaintenanceInterval = TimeSpan.FromSeconds(15);
    public void ResumeTransport()
    {
        if (!wantConnected) return;
        var identity = authenticatedDeviceSyncIdentity;
        if (identity is not null && Connected && IsCurrentDeviceSyncIdentity(identity))
        {
            TrackBackground(RecoverDurableDeliveryAsync(identity), "durable delivery resume");
            return;
        }
        ScheduleRecovery();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        => ResumeTransport();

    private void ScheduleDurableDeliveryRetry()
    {
        if (!wantConnected || Interlocked.Exchange(ref durableRetryScheduled, 1) == 1) return;
        TrackBackground(Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                var identity = authenticatedDeviceSyncIdentity;
                if (identity is not null && Connected && IsCurrentDeviceSyncIdentity(identity))
                    await RecoverDurableDeliveryAsync(identity);
                else
                    ScheduleRecovery();
            }
            finally
            {
                Interlocked.Exchange(ref durableRetryScheduled, 0);
                if (wantConnected && Connected && HasLocalDurableWork())
                    ScheduleDurableDeliveryRetry();
            }
        }), "durable delivery retry");
    }

    private bool HasLocalDurableWork()
        => state.ListTopicOutbox().Any(item => item.State is TopicOutboxStates.Pending
            or TopicOutboxStates.CancelPending)
           || state.ListDeviceEnvelopeOutbox().Count > 0;
    private async Task AcknowledgeDeliveryAsync(HubConnection connection, MeshEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.RelayDeliveryId)) return;
        var acknowledged = await connection.InvokeAsync<bool>(
            MeshHubProtocol.AcknowledgeDelivery,
            envelope.RelayDeliveryId,
            envelope.RelayDeviceScoped);
        if (!acknowledged)
            TraceTransport("delivery-ack-rejected", envelope.RelayDeviceScoped ? "device" : "handle");
    }

    private async Task MaintainDurableDeliveryAsync(DeviceSyncIdentity identity)
    {
        while (IsCurrentDeviceSyncIdentity(identity))
        {
            await Task.Delay(DurableMaintenanceInterval);
            if (!IsCurrentDeviceSyncIdentity(identity)) return;
            try
            {
                await RecoverDurableDeliveryAsync(identity);
            }
            catch (Exception ex)
            {
                TraceTransport("delivery-maintenance-failed", ex.GetType().Name);
            }
        }
    }

    private async Task RequestPendingDeliveriesAsync(DeviceSyncIdentity identity)
    {
        if (!supportsDurableDelivery || !IsCurrentDeviceSyncIdentity(identity)) return;
        try
        {
            _ = await identity.Connection.InvokeAsync<int>(
                MeshHubProtocol.RequestPendingDeliveries,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            TraceTransport("delivery-drain-deferred", ex.GetType().Name);
        }
    }

    private async Task RecoverDurableDeliveryAsync(DeviceSyncIdentity identity)
    {
        if (!IsCurrentDeviceSyncIdentity(identity)) return;
        await RequestPendingDeliveriesAsync(identity);
        if (!IsCurrentDeviceSyncIdentity(identity)) return;
        RecoverInboundTopicRuns();

        await durableFlushGate.WaitAsync();
        try
        {
            if (!IsCurrentDeviceSyncIdentity(identity)) return;
            foreach (var item in state.ListTopicOutbox())
            {
                if (!IsCurrentDeviceSyncIdentity(identity)) return;
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
                if (!IsCurrentDeviceSyncIdentity(identity)) return;
                await TrySendDeviceEnvelopeOutboxItemAsync(identity, item, CancellationToken.None);
            }
        }
        finally
        {
            durableFlushGate.Release();
        }
        if (HasLocalDurableWork()) ScheduleDurableDeliveryRetry();
    }

    private void RecoverInboundTopicRuns()
    {
        foreach (var item in state.ListInboundTopicRuns(
                     InboundTopicRunStates.Accepted,
                     InboundTopicRunStates.Running))
        {
            if (activeTopicRuns.ContainsKey(item.RunId)) continue;
            if (string.Equals(item.State, InboundTopicRunStates.Accepted, StringComparison.Ordinal))
            {
                if (!TryStartInboundTopicRun(item.Request, item.SourceDeviceId))
                    QueueInterruptedInboundRun(item, "remote_execution_recovery_failed");
                continue;
            }
            QueueInterruptedInboundRun(item, "remote_execution_interrupted");
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
        var terminal = PersistInboundTopicTerminal(
            item.RunId, InboundTopicRunStates.Interrupted, update);
        PersistDeviceEnvelopeOutbox(
            item.SourceDeviceId,
            MeshKinds.TopicRunUpdate,
            TopicRunProtocol.UpdateBody(terminal),
            PushHintProtocol.ForTopicRunPhase(terminal.Phase));
    }

    private bool TryStartInboundTopicRun(TopicRunRequestPayload request, string sourceDeviceId)
    {
        if (!EnsureInboundTopicContext(request)) return false;
        var active = new ActiveTopicRun(
            request.RunId, request.ThreadId, sourceDeviceId, new CancellationTokenSource());
        if (!activeTopicRuns.TryAdd(request.RunId, active)) return true;
        TrackBackground(
            ExecuteInboundTopicRunAsync(request, active),
            $"topic run {request.RunId}");
        return true;
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
            return TopicDispatchResult.Ok(request.RunId, DurableDeliveryCodes.LocalQueued);

        var identity = authenticatedDeviceSyncIdentity;
        if (identity is null || !Connected || !IsCurrentDeviceSyncIdentity(identity))
        {
            ScheduleRecovery();
            return TopicDispatchResult.Ok(request.RunId, DurableDeliveryCodes.LocalQueued);
        }

        await durableFlushGate.WaitAsync(ct);
        try
        {
            var result = await TrySendTopicOutboxItemAsync(identity, item, ct);
            if (result is null)
            {
                ScheduleDurableDeliveryRetry();
                return TopicDispatchResult.Ok(request.RunId, DurableDeliveryCodes.LocalQueued);
            }
            if (result.Accepted)
                return TopicDispatchResult.Ok(request.RunId, result.Code);
            state.DeleteTopicOutbox(request.RunId);
            return TopicDispatchResult.Reject(result.Code, request.RunId, DescribeResult(result));
        }
        finally
        {
            durableFlushGate.Release();
        }
    }

    private async Task<MeshSendResult?> TrySendTopicOutboxItemAsync(
        DeviceSyncIdentity identity,
        MeshDb.TopicOutboxItem item,
        CancellationToken ct)
    {
        if (!IsCurrentDeviceSyncIdentity(identity)
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
        ScheduleDurableDeliveryRetry();
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
        if (request is not null)
        {
            if (request.State is TopicOutboxStates.Expired or TopicOutboxStates.Failed)
            {
                state.DeleteTopicOutbox(cancel.RunId);
                state.CompleteQueuedTopicRun(request.ThreadId, request.RunId);
                return true;
            }
            if (!state.SetTopicOutboxState(cancel.RunId, TopicOutboxStates.CancelPending))
                return false;
            state.SetQueuedTopicRunStage(request.ThreadId, request.RunId, TopicQueueStage.Cancelling);
        }

        var item = PersistDeviceEnvelopeOutbox(
            targetDeviceId,
            MeshKinds.TopicRunCancel,
            TopicRunProtocol.CancelBody(cancel),
            null);
        var identity = authenticatedDeviceSyncIdentity;
        if (identity is null || !Connected || !IsCurrentDeviceSyncIdentity(identity))
        {
            ScheduleRecovery();
            return true;
        }

        await durableFlushGate.WaitAsync(ct);
        try
        {
            var relayCancelled = request is not null
                                 && await TryCancelRelayTopicRequestAsync(identity, request, ct);
            var sent = await TrySendDeviceEnvelopeOutboxItemAsync(identity, item, ct);
            if (request is not null && (relayCancelled || sent?.Accepted == true))
            {
                state.DeleteTopicOutbox(request.RunId);
                state.CompleteQueuedTopicRun(request.ThreadId, request.RunId);
            }
            return relayCancelled || sent?.Accepted == true || state.GetTopicOutbox(cancel.RunId) is not null;
        }
        finally
        {
            durableFlushGate.Release();
        }
    }

    private async Task TryFlushTopicCancellationAsync(
        DeviceSyncIdentity identity,
        MeshDb.TopicOutboxItem request,
        CancellationToken ct)
    {
        var relayCancelled = await TryCancelRelayTopicRequestAsync(identity, request, ct);
        var cancellationId = DeviceEnvelopeId(
            MeshKinds.TopicRunCancel,
            TopicRunProtocol.CancelBody(new TopicRunCancelPayload(request.RunId, request.ThreadId)));
        var cancellation = state.ListDeviceEnvelopeOutbox().FirstOrDefault(item =>
            string.Equals(item.EnvelopeId, cancellationId, StringComparison.Ordinal));
        var sent = cancellation is null
            ? null
            : await TrySendDeviceEnvelopeOutboxItemAsync(identity, cancellation, ct);
        if (!relayCancelled && sent?.Accepted != true) return;
        state.DeleteTopicOutbox(request.RunId);
        state.CompleteQueuedTopicRun(request.ThreadId, request.RunId);
    }

    private async Task<bool> TryCancelRelayTopicRequestAsync(
        DeviceSyncIdentity identity,
        MeshDb.TopicOutboxItem request,
        CancellationToken ct)
    {
        if (!supportsDurableDelivery || !IsCurrentDeviceSyncIdentity(identity)) return false;
        try
        {
            var cancelled = await identity.Connection.InvokeAsync<bool>(
                MeshHubProtocol.CancelQueuedEnvelope,
                new CancelQueuedEnvelopeRequest(request.RunId, request.TargetDeviceId),
                ct);
            TraceTransport("topic-request-relay-cancel", cancelled ? "accepted" : "not-found");
            return cancelled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceTransport("topic-request-relay-cancel-deferred", ex.GetType().Name);
            return false;
        }
    }

    private async Task<bool> QueueDeviceEnvelopeAsync(
        string targetDeviceId,
        string kind,
        string plaintext,
        CancellationToken ct,
        string? pushHint)
    {
        var item = PersistDeviceEnvelopeOutbox(targetDeviceId, kind, plaintext, pushHint);
        var identity = authenticatedDeviceSyncIdentity;
        if (identity is null || !Connected || !IsCurrentDeviceSyncIdentity(identity))
        {
            ScheduleRecovery();
            return true;
        }

        await durableFlushGate.WaitAsync(ct);
        try
        {
            await TrySendDeviceEnvelopeOutboxItemAsync(identity, item, ct);
            return true;
        }
        finally
        {
            durableFlushGate.Release();
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
        DeviceSyncIdentity identity,
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
        else
        {
            if (result is not null)
                TraceTransport("device-envelope-deferred", result.Code);
            ScheduleDurableDeliveryRetry();
        }
        return result;
    }

    private async Task<MeshSendResult?> TrySendTargetedTopicEnvelopeCoreAsync(
        DeviceSyncIdentity identity,
        string targetDeviceId,
        string kind,
        string plaintext,
        string envelopeId,
        string? pushHint,
        CancellationToken ct)
    {
        if (!IsCurrentDeviceSyncIdentity(identity)
            || string.IsNullOrWhiteSpace(targetDeviceId)
            || string.Equals(targetDeviceId, identity.DeviceId, StringComparison.Ordinal))
            return null;

        var keys = await ResolveOwnDeviceKeysAsync(identity);
        var targetKeys = keys.Where(key =>
                string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (targetKeys.Length == 0)
        {
            keys = await ResolveOwnDeviceKeysAsync(identity, refresh: true);
            targetKeys = keys.Where(key =>
                    string.Equals(DeviceProtocol.DeviceId(key), targetDeviceId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        if (!IsCurrentDeviceSyncIdentity(identity) || targetKeys.Length != 1) return null;

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
            if (supportsSendResults)
                return await identity.Connection.InvokeAsync<MeshSendResult>(
                    MeshHubProtocol.SendEnvelope, envelope, ct);
            await identity.Connection.InvokeAsync(MeshHubProtocol.SendEnvelope, envelope, ct);
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
            && TopicRunProtocol.TryParseUpdate(plaintext, out var update)
            && update.Phase is TopicRunPhase.Completed
                or TopicRunPhase.Failed
                or TopicRunPhase.Cancelled)
            return StableEnvelopeId("topic.terminal", update.RunId);
        return Guid.NewGuid().ToString("n");
    }

    private static string StableEnvelopeId(string purpose, string runId)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{purpose}\0{runId}"))).ToLowerInvariant();

    private static bool IsPermanentTransportRejection(string code)
        => code is "invalid_signature" or "message_too_large" or "invalid_push_hint";

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
        Log?.Invoke(safeDetail is null
            ? $"transport {eventName}"
            : $"transport {eventName}: {safeDetail}");
    }
}
