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

    public Task<bool> CompleteResponseAsync(
        MeshEnvelope response,
        string respondingDeviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!MessageCrypto.IsEncrypted(response.Body)
            || string.IsNullOrWhiteSpace(response.AgentRequestId)
            || string.IsNullOrWhiteSpace(response.AgentDispatchToken)
            || string.IsNullOrWhiteSpace(respondingDeviceId))
            return Task.FromResult(false);

        var ownerHandle = Normalize(response.From);
        var originalSender = Normalize(response.To);
        var dispatchId = AgentDispatchKey.Create(originalSender, response.AgentRequestId);
        return store.CompleteAgentDispatchAsync(
            ownerHandle,
            dispatchId,
            originalSender,
            response.AgentDispatchToken,
            respondingDeviceId,
            ct);
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

    private async Task DeliverAssignedAsync(
        string handle,
        string deviceId,
        IReadOnlyList<string> fallbackDeviceIds,
        CancellationToken ct)
    {
        while (true)
        {
            var deliveries = await store
                .TakeAssignedAgentDispatchesAsync(handle, deviceId, ct)
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
                        handle, dispatch.Id, deviceId, fallback, ct).ConfigureAwait(false);
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
                        continue;
                    if (outcome == BackplaneDeliveryOutcome.NotDelivered)
                    {
                        var fallback = AgentDispatchRecipientPolicy.ChooseDevice(
                            encryptedDeviceIds, fallbackDeviceIds);
                        await store.ReleaseAgentDispatchAsync(
                            handle, dispatch.Id, deviceId, fallback, ct).ConfigureAwait(false);
                        continue;
                    }

                    logger.LogWarning(
                        "Atomic agent dispatch {DispatchId} had an uncertain cross-instance delivery",
                        dispatch.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Delivery outcome is uncertain after a transport exception. Keep the request fenced as
                    // delivered rather than risk running it on a second device.
                    logger.LogError(ex, "Atomic agent dispatch {DispatchId} had an uncertain delivery", dispatch.Id);
                }
            }
        }
    }

    private static string Normalize(string handle) => LinkProtocol.Normalize(handle);
}
