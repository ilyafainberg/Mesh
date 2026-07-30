using System.Security.Cryptography;
using System.Text.Json;
using Mesh.Relay.Backplane;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

public static class AgentRoutingPolicy
{
    public static string? EffectivePrimaryDeviceId(StoredHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!string.IsNullOrWhiteSpace(handle.AgentPrimaryDeviceId))
            return handle.AgentPrimaryDeviceId;

        return handle.DevicePublicKeys
            .Select(DeviceProtocol.DeviceId)
            .FirstOrDefault(deviceId => IsSelectableDevice(handle, deviceId));
    }

    public static bool IsSelectableDevice(StoredHandle handle, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var registered = handle.DevicePublicKeys.Any(publicKey =>
            string.Equals(DeviceProtocol.DeviceId(publicKey), deviceId, StringComparison.Ordinal));
        return registered
            && DevicePlatforms.IsDesktop(handle.DevicePlatforms.GetValueOrDefault(deviceId))
            && handle.DeviceAtomicAgentDispatchEnabled.GetValueOrDefault(deviceId);
    }

    public static bool IsExecutionReady(StoredHandle handle, string? deviceId)
        => IsSelectableDevice(handle, deviceId)
           && handle.DeviceRemoteAgentEnabled.GetValueOrDefault(deviceId!);

    public static string? ChooseOnlineDevice(StoredHandle handle, IReadOnlySet<string> onlineDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(onlineDeviceIds);
        var primary = EffectivePrimaryDeviceId(handle);
        if (IsExecutionReady(handle, primary) && onlineDeviceIds.Contains(primary!))
            return primary;

        var failover = handle.AgentFailoverDeviceId;
        return !string.Equals(primary, failover, StringComparison.Ordinal)
               && IsExecutionReady(handle, failover)
               && onlineDeviceIds.Contains(failover!)
            ? failover
            : null;
    }

    public static AgentRoutingInfo ToInfo(StoredHandle handle)
        => new(
            EffectivePrimaryDeviceId(handle),
            handle.AgentFailoverDeviceId,
            handle.AgentRoutingVersion,
            handle.AgentPrimaryWasSelectedAutomatically);
}

/// <summary>
/// Assigns encrypted agent questions to one configured device and fences the answer with a durable
/// relay token. The relay observes routing metadata only; question and answer bodies remain opaque.
/// </summary>
public sealed class AgentDispatchCoordinator(
    IRelayStore store,
    IBackplane backplane,
    MeshRouter router,
    ILogger<AgentDispatchCoordinator> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record ResponseCompletionResult(
        bool Accepted,
        bool Created,
        MeshEnvelope? Response,
        string? Error = null);

    public async Task<MeshSendResult> RouteRequestAsync(MeshEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var from = Normalize(envelope.From);
        var to = Normalize(envelope.To);
        if (string.IsNullOrWhiteSpace(envelope.Id) || from.Length == 0 || to.Length == 0)
            return MeshSendResult.Reject("invalid_agent_dispatch");
        if (!string.IsNullOrWhiteSpace(envelope.ToDevice)
            || !string.IsNullOrWhiteSpace(envelope.AgentRequestId)
            || !string.IsNullOrWhiteSpace(envelope.AgentDispatchToken))
            return MeshSendResult.Reject("invalid_agent_dispatch_metadata");
        if (!MessageCrypto.IsEncrypted(envelope.Body))
            return MeshSendResult.Reject("agent_dispatch_encryption_required");

        var registration = await store.GetHandleAsync(to, ct).ConfigureAwait(false);
        if (registration is null)
            return MeshSendResult.Reject("agent_recipient_unknown");
        var registeredDeviceIds = registration.DevicePublicKeys
            .Select(DeviceProtocol.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
        var recipientDeviceIds = MessageCrypto.EncryptedDeviceIds(envelope.Body)
            .Where(registeredDeviceIds.Contains)
            .ToList();
        if (recipientDeviceIds.Count == 0)
            return MeshSendResult.Reject("agent_dispatch_encryption_required");

        var cleanEnvelope = envelope with
        {
            To = to,
            ToDevice = null,
            AgentRequestId = null,
            AgentDispatchToken = null
        };
        var dispatchId = AgentDispatchKey.Create(from, cleanEnvelope.Id);
        var envelopeJson = JsonSerializer.Serialize(cleanEnvelope, Json);
        var dispatch = new StoredAgentDispatch
        {
            Id = dispatchId,
            RequestId = cleanEnvelope.Id,
            From = from,
            To = to,
            EnvelopeJson = envelopeJson,
            EnvelopeHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(envelopeJson))).ToLowerInvariant(),
            RecipientDeviceIds = recipientDeviceIds,
            DispatchToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            State = AgentDispatchStates.Pending,
            QueuedAt = DateTimeOffset.UtcNow
        };
        var created = await store.CreateAgentDispatchAsync(dispatch, ct).ConfigureAwait(false);
        if (created.Status == AgentDispatchCreateStatus.Conflict)
            return MeshSendResult.Reject("agent_dispatch_id_conflict");

        await DispatchPendingAsync(to, ct).ConfigureAwait(false);
        var current = await store.GetAgentDispatchAsync(to, dispatchId, ct).ConfigureAwait(false);
        var accepted = current is not null
                       && current.State is AgentDispatchStates.Delivered or AgentDispatchStates.Completed;
        return new MeshSendResult(
            true,
            accepted ? AgentDispatchCodes.Accepted : AgentDispatchCodes.Queued);
    }

    public async Task DispatchAvailableAsync(
        string handle,
        CancellationToken ct = default)
    {
        var normalized = Normalize(handle);
        await DispatchPendingAsync(normalized, ct).ConfigureAwait(false);
    }

    public async Task DispatchPendingAsync(string handle, CancellationToken ct = default)
    {
        var normalized = Normalize(handle);
        var registration = await store.GetHandleAsync(normalized, ct).ConfigureAwait(false);
        if (registration is null) return;
        var targets = await SelectOnlineDevicesAsync(normalized, registration, ct).ConfigureAwait(false);
        if (targets.Count == 0) return;

        await store.AssignPendingAgentDispatchesAsync(normalized, targets, ct).ConfigureAwait(false);
        for (var index = 0; index < targets.Count; index++)
            await DeliverAssignedAsync(
                normalized, targets[index], targets.Skip(index + 1).ToArray(), ct).ConfigureAwait(false);
    }

    public async Task<ResponseCompletionResult> CompleteResponseAsync(
        MeshEnvelope response,
        string respondingDeviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!MessageCrypto.IsEncrypted(response.Body)
            || string.IsNullOrWhiteSpace(response.AgentRequestId)
            || string.IsNullOrWhiteSpace(response.AgentDispatchToken)
            || string.IsNullOrWhiteSpace(respondingDeviceId))
            return new ResponseCompletionResult(false, false, null, "invalid_agent_dispatch_response");

        var ownerHandle = Normalize(response.From);
        var originalSender = Normalize(response.To);
        var dispatchId = AgentDispatchKey.Create(originalSender, response.AgentRequestId);
        var cleanResponse = response with { AgentDispatchToken = null };
        var responseJson = JsonSerializer.Serialize(cleanResponse, Json);
        var responseHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(responseJson))).ToLowerInvariant();
        var staged = await store.StageAgentDispatchResponseAsync(
            ownerHandle,
            dispatchId,
            originalSender,
            response.AgentDispatchToken,
            respondingDeviceId,
            cleanResponse.Id,
            responseJson,
            responseHash,
            ct).ConfigureAwait(false);
        if (!staged.Accepted || string.IsNullOrWhiteSpace(staged.ResponseJson))
            return new ResponseCompletionResult(false, false, null, "invalid_agent_dispatch_response");

        MeshEnvelope? persistedResponse;
        try
        {
            persistedResponse = JsonSerializer.Deserialize<MeshEnvelope>(staged.ResponseJson, Json);
        }
        catch (JsonException)
        {
            return new ResponseCompletionResult(false, false, null, "invalid_agent_dispatch_response");
        }
        if (persistedResponse is null)
            return new ResponseCompletionResult(false, false, null, "invalid_agent_dispatch_response");

        if (!staged.Completed)
        {
            var admission = await store.EnqueueAsync(
                originalSender,
                persistedResponse.Id,
                ownerHandle,
                staged.ResponseJson,
                RelayInboxPriority.ForKind(persistedResponse.Kind),
                BackgroundSyncProtocol.RequiresForeground(persistedResponse.Kind),
                ct).ConfigureAwait(false);
            if (!admission.Accepted)
                return new ResponseCompletionResult(
                    false, false, persistedResponse, "response_admission_rejected");

            if (!await store.CompleteAgentDispatchResponseAsync(
                    ownerHandle,
                    dispatchId,
                    persistedResponse.Id,
                    ct).ConfigureAwait(false))
                return new ResponseCompletionResult(
                    false, admission.Created, persistedResponse, "response_completion_retry");
            return new ResponseCompletionResult(true, admission.Created, persistedResponse);
        }

        return new ResponseCompletionResult(true, false, persistedResponse);
    }

    public async Task<int> ReplayResponseOutboxAsync(CancellationToken ct = default)
    {
        var completed = 0;
        var pending = await store.GetPendingAgentResponsesAsync(ct: ct).ConfigureAwait(false);
        foreach (var dispatch in pending)
        {
            if (string.IsNullOrWhiteSpace(dispatch.ResponseId)
                || string.IsNullOrWhiteSpace(dispatch.ResponseJson))
                continue;
            MeshEnvelope? response;
            try
            {
                response = JsonSerializer.Deserialize<MeshEnvelope>(dispatch.ResponseJson, Json);
            }
            catch (JsonException)
            {
                logger.LogError("Staged response for agent dispatch {DispatchId} is invalid", dispatch.Id);
                continue;
            }
            if (response is null
                || !string.Equals(response.Id, dispatch.ResponseId, StringComparison.Ordinal))
                continue;
            var admission = await store.EnqueueAsync(
                dispatch.From,
                response.Id,
                dispatch.To,
                dispatch.ResponseJson,
                RelayInboxPriority.ForKind(response.Kind),
                BackgroundSyncProtocol.RequiresForeground(response.Kind),
                ct).ConfigureAwait(false);
            if (!admission.Accepted) continue;
            if (await store.CompleteAgentDispatchResponseAsync(
                    dispatch.To, dispatch.Id, response.Id, ct).ConfigureAwait(false))
                completed++;
        }
        return completed;
    }

    private async Task<IReadOnlyList<string>> SelectOnlineDevicesAsync(
        string handle,
        StoredHandle registration,
        CancellationToken ct)
    {
        var result = new List<string>(2);
        var primary = AgentRoutingPolicy.EffectivePrimaryDeviceId(registration);
        if (AgentRoutingPolicy.IsExecutionReady(registration, primary)
            && await backplane.GetInstanceForDeviceAsync(handle, primary!, ct).ConfigureAwait(false) is not null)
            result.Add(primary!);

        var failover = registration.AgentFailoverDeviceId;
        if (!string.Equals(primary, failover, StringComparison.Ordinal)
            && AgentRoutingPolicy.IsExecutionReady(registration, failover)
            && await backplane.GetInstanceForDeviceAsync(handle, failover!, ct).ConfigureAwait(false) is not null)
            result.Add(failover!);

        return result;
    }

    public sealed class AgentResponseOutboxWorker(
        AgentDispatchCoordinator coordinator,
        ILogger<AgentResponseOutboxWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                try
                {
                    await coordinator.ReplayResponseOutboxAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Agent response outbox replay failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }

    private async Task DeliverAssignedAsync(
        string handle,
        string deviceId,
        IReadOnlyList<string> fallbackDeviceIds,
        CancellationToken ct)
    {
        while (true)
        {
            var leaseOwner = Guid.NewGuid().ToString("N");
            var deliveries = await store
                .TakeAssignedAgentDispatchesAsync(
                    handle, deviceId, leaseOwner, ct: ct)
                .ConfigureAwait(false);
            if (deliveries.Count == 0) return;
            foreach (var dispatch in deliveries)
            {
                MeshEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<MeshEnvelope>(dispatch.EnvelopeJson, Json);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Stored agent dispatch {DispatchId} is invalid", dispatch.Id);
                    continue;
                }
                if (envelope is null
                    || !AgentDispatchProtocol.IsAtomicRequest(envelope.Kind)
                    || !string.Equals(Normalize(envelope.To), handle, StringComparison.Ordinal)
                    || !string.Equals(envelope.Id, dispatch.RequestId, StringComparison.Ordinal))
                {
                    logger.LogError("Stored agent dispatch {DispatchId} failed validation", dispatch.Id);
                    continue;
                }

                var encryptedDeviceIds = MessageCrypto.EncryptedDeviceIds(envelope.Body);
                if (!encryptedDeviceIds.Contains(deviceId, StringComparer.Ordinal))
                {
                    var fallback = AgentDispatchRecipientPolicy.ChooseDevice(
                        encryptedDeviceIds, fallbackDeviceIds);
                    await store.ReleaseAgentDispatchAsync(
                        handle, dispatch.Id, deviceId, leaseOwner, fallback, ct).ConfigureAwait(false);
                    logger.LogWarning(
                        "Atomic agent dispatch {DispatchId} has no key slot for device {DeviceId}",
                        dispatch.Id,
                        deviceId);
                    continue;
                }

                var routed = envelope with
                {
                    ToDevice = deviceId,
                    AgentRequestId = dispatch.RequestId,
                    AgentDispatchToken = dispatch.DispatchToken
                };
                try
                {
                    var outcome = await router
                        .RouteAtomicAgentRequestAsync(routed, ct)
                        .ConfigureAwait(false);
                    if (outcome == BackplaneDeliveryOutcome.Delivered)
                    {
                        if (!await store.MarkAgentDispatchDeliveredAsync(
                                handle, dispatch.Id, deviceId, leaseOwner, ct).ConfigureAwait(false))
                            logger.LogWarning(
                                "Atomic agent dispatch {DispatchId} was delivered after its claim expired; "
                                + "stable envelope-id deduplication makes lease redelivery safe",
                                dispatch.Id);
                        continue;
                    }
                    if (outcome == BackplaneDeliveryOutcome.NotDelivered)
                    {
                        var fallback = AgentDispatchRecipientPolicy.ChooseDevice(
                            encryptedDeviceIds, fallbackDeviceIds);
                        await store.ReleaseAgentDispatchAsync(
                            handle, dispatch.Id, deviceId, leaseOwner, fallback, ct).ConfigureAwait(false);
                        continue;
                    }

                    logger.LogWarning(
                        "Atomic agent dispatch {DispatchId} had an uncertain cross-instance delivery; "
                        + "the claim will expire for safe envelope-id-deduplicated redelivery",
                        dispatch.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The lease remains until expiry. A retry can duplicate transport delivery, but the
                    // persisted client chat line uses the stable envelope id to suppress re-execution.
                    logger.LogError(
                        ex,
                        "Atomic agent dispatch {DispatchId} had an uncertain delivery; its claim will expire",
                        dispatch.Id);
                }
            }
        }
    }

    private static string Normalize(string handle) => LinkProtocol.Normalize(handle);
}
