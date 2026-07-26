using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Mesh.Relay.Backplane;
using Mesh.Relay.Push;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

public readonly record struct MeshRouteResult(
    bool Delivered, bool Queued, string DeliveryId, bool NewlyQueued);

/// <summary>
/// Directed message router. Every envelope is enqueued first, then live delivery is attempted using:
///  1. a local hub connection on THIS instance (fast path),
///  2. the instance that currently holds the recipient's socket, via the backplane
///     (a single directed forward, NOT a fan-out to all servers).
/// The inbox record remains until a durable client acknowledges it, or until legacy delivery succeeds.
///
/// This is the deliberate alternative to SignalR's Redis backplane, which would broadcast
/// every message to every server. Presence lookup plus a per-node forward keeps Redis load
/// proportional to delivered messages, so the relay scales by adding replicas.
/// </summary>
public sealed class MeshRouter(
    IHubContext<MeshHub> hub,
    ConnectionRegistry registry,
    IRelayStore store,
    IBackplane backplane,
    PushDispatcher push)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Routes a fully-formed envelope to its recipient.</summary>
    /// <param name="excludeConnectionId">
    /// A connection to skip on local delivery, used when a device sends to its OWN handle
    /// (remote-to-desktop) so the message reaches the owner's OTHER devices, not an echo to itself.
    /// </param>
    public async Task<MeshRouteResult> RouteAsync(MeshEnvelope env, string? excludeConnectionId = null)
    {
        var clean = env with { RelayDeliveryId = null, RelayDeviceScoped = false };
        var to = Normalize(clean.To);
        if (!string.IsNullOrWhiteSpace(clean.ToDevice))
            return await RouteToDeviceAsync(clean, excludeConnectionId);

        var originalJson = JsonSerializer.Serialize(clean, Json);
        var enqueued = await store.EnqueueAsync(to, clean.Id, clean.From, originalJson);
        var deliveryId = enqueued.DeliveryId;
        var leaseOwner = LiveLeaseOwner();
        if (!await store.TryLeaseInboxItemAsync(to, deliveryId, leaseOwner))
            return new MeshRouteResult(false, true, deliveryId, enqueued.Created);
        var envelopeJson = JsonSerializer.Serialize(
            clean with { RelayDeliveryId = deliveryId, RelayDeviceScoped = false }, Json);
        // A thrown or uncertain send may have reached the owning socket. In that case the live lease
        // remains until timeout so an immediate retry cannot race the first delivery attempt.
        var receipt = await DeliverLocalWithReceiptAsync(to, envelopeJson, excludeConnectionId);
        if (receipt.Outcome != BackplaneDeliveryOutcome.Delivered)
        {
            var owner = await backplane.GetInstanceForAsync(to);
            if (owner is not null && owner != backplane.InstanceId)
                receipt = await backplane.PublishToOwnerAsync(owner, to, envelopeJson);
        }

        if (receipt.Outcome == BackplaneDeliveryOutcome.Delivered)
        {
            if (!receipt.DurableAckExpected)
                await store.AcknowledgeInboxAsync(to, deliveryId);
            if (Normalize(clean.From) != to)
                push.NotifyOfflineSiblings(to, clean);
            return new MeshRouteResult(true, false, deliveryId, enqueued.Created);
        }

        if (receipt.Outcome == BackplaneDeliveryOutcome.NotDelivered)
        {
            await store.ReleaseInboxLeaseAsync(to, deliveryId, leaseOwner);
            push.NotifyOffline(to, null, clean);
        }
        return new MeshRouteResult(false, true, deliveryId, enqueued.Created);
    }

    /// <summary>
    /// Routes an envelope to exactly one device. If that device is offline, its envelope is queued
    /// under a device-specific inbox key so an online sibling cannot consume or discard it.
    /// </summary>
    public async Task<MeshRouteResult> RouteToDeviceAsync(MeshEnvelope env, string? excludeConnectionId = null)
    {
        if (string.IsNullOrWhiteSpace(env.ToDevice))
            throw new ArgumentException("A strict device route requires ToDevice.", nameof(env));

        var clean = env with { RelayDeliveryId = null, RelayDeviceScoped = false };
        var to = Normalize(clean.To);
        var inboxKey = DeviceInboxKey(to, clean.ToDevice!);
        var originalJson = JsonSerializer.Serialize(clean, Json);
        var enqueued = await store.EnqueueAsync(inboxKey, clean.Id, clean.From, originalJson);
        var deliveryId = enqueued.DeliveryId;
        var leaseOwner = LiveLeaseOwner();
        if (!await store.TryLeaseInboxItemAsync(inboxKey, deliveryId, leaseOwner))
            return new MeshRouteResult(false, true, deliveryId, enqueued.Created);
        var envelopeJson = JsonSerializer.Serialize(
            clean with { RelayDeliveryId = deliveryId, RelayDeviceScoped = true }, Json);
        var receipt = await DeliverLocalWithReceiptAsync(
            to, envelopeJson, excludeConnectionId, clean.ToDevice);
        if (receipt.Outcome != BackplaneDeliveryOutcome.Delivered)
        {
            var owner = await backplane.GetInstanceForDeviceAsync(to, clean.ToDevice);
            if (owner is not null && owner != backplane.InstanceId)
                receipt = await backplane.PublishToOwnerAsync(owner, to, envelopeJson);
        }

        if (receipt.Outcome == BackplaneDeliveryOutcome.Delivered)
        {
            if (!receipt.DurableAckExpected)
                await store.AcknowledgeInboxAsync(inboxKey, deliveryId);
            return new MeshRouteResult(true, false, deliveryId, enqueued.Created);
        }

        if (receipt.Outcome == BackplaneDeliveryOutcome.NotDelivered)
        {
            await store.ReleaseInboxLeaseAsync(inboxKey, deliveryId, leaseOwner);
            push.NotifyOffline(to, clean.ToDevice, clean);
        }
        return new MeshRouteResult(false, true, deliveryId, enqueued.Created);
    }

    /// <summary>
    /// Routes to one device only when its live presence exists. This path never queues and never
    /// broadcasts, so callers can reject operations that require an immediately available device.
    /// </summary>
    public async Task<bool> RouteToOnlineDeviceAsync(
        MeshEnvelope env,
        string? excludeConnectionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(env.ToDevice))
            return false;

        var to = Normalize(env.To);
        var envelopeJson = JsonSerializer.Serialize(env, Json);
        if (await DeliverLocalAsync(to, envelopeJson, excludeConnectionId, env.ToDevice))
            return true;

        var owner = await backplane.GetInstanceForDeviceAsync(to, env.ToDevice, ct);
        return owner is not null
            && owner != backplane.InstanceId
            && (await backplane.PublishToOwnerAsync(owner, to, envelopeJson, ct)).Outcome
               == BackplaneDeliveryOutcome.Delivered;
    }

    /// <summary>
    /// Delivers one atomic agent request to one connection of one device. This path never queues or
    /// fans out. Durable assignment and retry state live in AgentDispatchCoordinator.
    /// </summary>
    public async Task<BackplaneDeliveryOutcome> RouteAtomicAgentRequestAsync(
        MeshEnvelope env,
        CancellationToken ct = default)
    {
        if (!AgentDispatchProtocol.IsAtomicRequest(env.Kind)
            || string.IsNullOrWhiteSpace(env.ToDevice))
            return BackplaneDeliveryOutcome.NotDelivered;

        var to = Normalize(env.To);
        var envelopeJson = JsonSerializer.Serialize(env, Json);
        if (await DeliverSingleLocalDeviceAsync(to, envelopeJson, env.ToDevice))
            return BackplaneDeliveryOutcome.Delivered;

        var owner = await backplane.GetInstanceForDeviceAsync(to, env.ToDevice, ct);
        if (owner is null || owner == backplane.InstanceId)
            return BackplaneDeliveryOutcome.NotDelivered;

        return await backplane.PublishAtomicToOwnerAsync(owner, to, envelopeJson, ct);
    }

    /// <summary>
    /// Delivers an envelope JSON to every local connection for a handle (optionally excluding one
    /// connection). Returns the outcome and whether a durable acknowledgement is expected. Used by the
    /// local fast path and by the backplane when another instance forwards a message to this one.
    /// </summary>
    /// <param name="toDevice">
    /// When non-null, restrict delivery to the connections whose authenticated device id matches this
    /// value (one specific device of the handle). When null, behavior is unchanged: deliver to every
    /// connection of the handle. The backplane path parses ToDevice out of the envelope JSON and passes
    /// it here so a cross-instance forward re-applies the same per-device filter on the owning instance.
    /// </param>
    public async Task<BackplaneDeliveryReceipt> DeliverLocalWithReceiptAsync(
        string handle, string envelopeJson, string? excludeConnectionId = null, string? toDevice = null)
    {
        var normalized = Normalize(handle);
        var conns = toDevice is not null
            ? registry.ConnectionsForDevice(normalized, toDevice)
            : registry.ConnectionsFor(normalized);
        if (excludeConnectionId is not null)
            conns = conns.Where(c => c != excludeConnectionId).ToList();
        if (conns.Count == 0) return BackplaneDeliveryReceipt.NotDelivered;
        await hub.Clients.Clients(conns).SendAsync(MeshHubProtocol.Receive, envelopeJson);
        return new BackplaneDeliveryReceipt(
            BackplaneDeliveryOutcome.Delivered,
            conns.Any(registry.SupportsDurableDelivery));
    }

    public async Task<bool> DeliverLocalAsync(
        string handle, string envelopeJson, string? excludeConnectionId = null, string? toDevice = null)
        => (await DeliverLocalWithReceiptAsync(
            handle, envelopeJson, excludeConnectionId, toDevice)).Outcome
           == BackplaneDeliveryOutcome.Delivered;

    public async Task<BackplaneDeliveryReceipt> DeliverSingleLocalDeviceWithReceiptAsync(
        string handle,
        string envelopeJson,
        string deviceId,
        string? excludeConnectionId = null)
    {
        var connection = registry.ConnectionsForDevice(Normalize(handle), deviceId)
            .Where(id => !string.Equals(id, excludeConnectionId, StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (connection is null) return BackplaneDeliveryReceipt.NotDelivered;
        await hub.Clients.Client(connection).SendAsync(MeshHubProtocol.Receive, envelopeJson);
        return new BackplaneDeliveryReceipt(
            BackplaneDeliveryOutcome.Delivered,
            registry.SupportsDurableDelivery(connection));
    }

    public async Task<bool> DeliverSingleLocalDeviceAsync(
        string handle,
        string envelopeJson,
        string deviceId,
        string? excludeConnectionId = null)
        => (await DeliverSingleLocalDeviceWithReceiptAsync(
            handle, envelopeJson, deviceId, excludeConnectionId)).Outcome
           == BackplaneDeliveryOutcome.Delivered;

    private string LiveLeaseOwner()
        => $"live:{backplane.InstanceId}:{Guid.NewGuid():n}";

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();

    public static string DeviceInboxKey(string handle, string deviceId)
        => $"{Normalize(handle)}\u001f{deviceId}";
}
