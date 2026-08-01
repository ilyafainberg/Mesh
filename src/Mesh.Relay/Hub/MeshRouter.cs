using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Mesh.Relay.Backplane;
using Mesh.Relay.Observability;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// Online-only opaque frame forwarder. The router never persists, queues, leases or acknowledges
/// anything: it delivers a stamped <see cref="OnlineRelayDelivery"/> to the live socket(s) that own
/// the target, either on THIS instance or, when the socket lives elsewhere, via a single directed
/// backplane forward to the owning instance. A target that is not online yields NotDelivered.
///
/// This is the deliberate alternative to a broadcast backplane: presence lookup plus a per-node
/// forward keeps Redis load proportional to delivered frames, so the relay scales by adding replicas.
/// </summary>
public sealed class MeshRouter(
    IHubContext<MeshHub> hub,
    ConnectionRegistry registry,
    IBackplane backplane,
    RelayMetrics metrics)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string InstanceId => backplane.InstanceId;

    /// <summary>
    /// Delivers a stamped <see cref="OnlineRelayDelivery"/> to the authenticated local sockets of a
    /// handle as a typed hub argument (never a JSON string), so the client's typed
    /// <c>On&lt;OnlineRelayDelivery&gt;(Deliver, ...)</c> handler binds exactly. When
    /// <paramref name="toDevice"/> is set, delivery is restricted to that one device. Each send is
    /// gated by the connection's bounded outbound buffer so a slow consumer cannot grow unbounded.
    /// Returns the number of local sockets the frame reached.
    /// </summary>
    public async Task<int> DeliverLocalAsync(
        string handle,
        OnlineRelayDelivery delivery,
        string? toDevice = null,
        string? excludeConnectionId = null)
    {
        var normalized = Normalize(handle);
        var conns = toDevice is not null
            ? registry.ConnectionsForDevice(normalized, toDevice)
            : registry.ConnectionsFor(normalized);
        if (excludeConnectionId is not null)
            conns = conns.Where(c => !string.Equals(c, excludeConnectionId, StringComparison.Ordinal)).ToArray();
        if (conns.Count == 0) return 0;

        var delivered = 0;
        foreach (var connectionId in conns)
        {
            var state = registry.Get(connectionId);
            if (state is not { Authenticated: true }) continue;
            if (!state.TryReserveOutbound()) continue; // bounded buffer full: drop, sender may retry
            try
            {
                await hub.Clients.Client(connectionId).SendAsync(OnlineRelayMethods.Deliver, delivery);
                delivered++;
            }
            finally
            {
                state.ReleaseOutbound();
            }
        }
        return delivered;
    }

    /// <summary>
    /// Backplane entry point: another instance forwarded an in-flight frame to us as opaque JSON.
    /// The delivery is deserialized back into a typed <see cref="OnlineRelayDelivery"/> exactly once
    /// here at the destination, then handed to the local sockets as a typed hub argument. Reports
    /// whether a live socket received it.
    /// </summary>
    public async Task<BackplaneDeliveryReceipt> DeliverFromBackplaneAsync(string handle, string deliveryJson)
    {
        OnlineRelayDelivery? delivery;
        try
        {
            delivery = JsonSerializer.Deserialize<OnlineRelayDelivery>(deliveryJson, Json);
        }
        catch (JsonException)
        {
            return BackplaneDeliveryReceipt.NotDelivered;
        }
        if (delivery is null) return BackplaneDeliveryReceipt.NotDelivered;

        var toDevice = string.IsNullOrWhiteSpace(delivery.ToDevice) ? null : delivery.ToDevice;
        var delivered = await DeliverLocalAsync(handle, delivery, toDevice);
        return delivered > 0 ? BackplaneDeliveryReceipt.Delivered : BackplaneDeliveryReceipt.NotDelivered;
    }

    /// <summary>
    /// Forwards a typed delivery to exactly one device: local sockets first (typed hub argument),
    /// then a single directed backplane forward to the instance that currently owns that device's
    /// socket. Only the cross-instance hop serializes the delivery to opaque JSON; the destination
    /// instance deserializes once and delivers the typed object. Never queues.
    /// </summary>
    public async Task<BackplaneDeliveryOutcome> ForwardToDeviceAsync(
        string handle,
        string deviceId,
        OnlineRelayDelivery delivery,
        string? excludeConnectionId = null,
        CancellationToken ct = default)
    {
        var normalized = Normalize(handle);
        if (await DeliverLocalAsync(normalized, delivery, deviceId, excludeConnectionId) > 0)
            return BackplaneDeliveryOutcome.Delivered;

        var owner = await backplane.GetInstanceForDeviceAsync(normalized, deviceId, ct);
        if (owner is null || string.Equals(owner, backplane.InstanceId, StringComparison.Ordinal))
            return BackplaneDeliveryOutcome.NotDelivered;

        var deliveryJson = JsonSerializer.Serialize(delivery, Json);
        var outcome = (await backplane.PublishToOwnerAsync(owner, normalized, deliveryJson, ct)).Outcome;
        if (outcome == BackplaneDeliveryOutcome.Delivered)
            metrics.BackplaneForwarded();
        return outcome;
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
