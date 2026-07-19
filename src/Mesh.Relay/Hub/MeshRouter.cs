using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Mesh.Relay.Backplane;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// Directed message router. Delivers an envelope to the recipient using, in order:
///  1. a local hub connection on THIS instance (fast path),
///  2. the instance that currently holds the recipient's socket, via the backplane
///     (a single directed forward, NOT a fan-out to all servers),
///  3. the durable offline inbox, when the recipient is not connected anywhere.
///
/// This is the deliberate alternative to SignalR's Redis backplane, which would broadcast
/// every message to every server. Presence lookup plus a per-node forward keeps Redis load
/// proportional to delivered messages, so the relay scales by adding replicas.
/// </summary>
public sealed class MeshRouter(
    IHubContext<MeshHub> hub,
    ConnectionRegistry registry,
    IRelayStore store,
    IBackplane backplane)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Routes a fully-formed envelope to its recipient.</summary>
    /// <param name="excludeConnectionId">
    /// A connection to skip on local delivery, used when a device sends to its OWN handle
    /// (remote-to-desktop) so the message reaches the owner's OTHER devices, not an echo to itself.
    /// </param>
    public async Task RouteAsync(MeshEnvelope env, string? excludeConnectionId = null)
    {
        var to = Normalize(env.To);
        var toDevice = env.ToDevice;

        if (!string.IsNullOrWhiteSpace(toDevice))
        {
            await RouteToDeviceAsync(env, excludeConnectionId);
            return;
        }

        var envelopeJson = JsonSerializer.Serialize(env, Json);
        // 1. Local delivery to any of the recipient's connections on this instance.
        if (await DeliverLocalAsync(to, envelopeJson, excludeConnectionId)) return;

        // 2. Directed cross-instance forward to whichever instance holds the socket.
        var owner = await backplane.GetInstanceForAsync(to);
        if (owner is not null && owner != backplane.InstanceId
            && await backplane.PublishToOwnerAsync(owner, to, envelopeJson))
            return;

        // 3. Nobody is connected: queue for delivery on next connect.
        await store.EnqueueAsync(to, envelopeJson);
    }

    /// <summary>
    /// Routes an envelope to exactly one device. If that device is offline, its envelope is queued
    /// under a device-specific inbox key so an online sibling cannot consume or discard it.
    /// </summary>
    public async Task RouteToDeviceAsync(MeshEnvelope env, string? excludeConnectionId = null)
    {
        if (string.IsNullOrWhiteSpace(env.ToDevice))
            throw new ArgumentException("A strict device route requires ToDevice.", nameof(env));

        var to = Normalize(env.To);
        var envelopeJson = JsonSerializer.Serialize(env, Json);
        if (await DeliverLocalAsync(to, envelopeJson, excludeConnectionId, env.ToDevice)) return;

        var owner = await backplane.GetInstanceForDeviceAsync(to, env.ToDevice);
        if (owner is not null && owner != backplane.InstanceId)
        {
            if (await backplane.PublishToOwnerAsync(owner, to, envelopeJson))
                return;
        }

        await store.EnqueueAsync(DeviceInboxKey(to, env.ToDevice), envelopeJson);
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
            && await backplane.PublishToOwnerAsync(owner, to, envelopeJson, ct);
    }

    /// <summary>
    /// Delivers an envelope JSON to every local connection for a handle (optionally excluding one
    /// connection). Returns true if at least one local connection received it. Used both by the
    /// local fast path and by the backplane when another instance forwards a message to this one.
    /// </summary>
    /// <param name="toDevice">
    /// When non-null, restrict delivery to the connections whose authenticated device id matches this
    /// value (one specific device of the handle). When null, behavior is unchanged: deliver to every
    /// connection of the handle. The backplane path parses ToDevice out of the envelope JSON and passes
    /// it here so a cross-instance forward re-applies the same per-device filter on the owning instance.
    /// </param>
    public async Task<bool> DeliverLocalAsync(
        string handle, string envelopeJson, string? excludeConnectionId = null, string? toDevice = null)
    {
        var normalized = Normalize(handle);
        var conns = toDevice is not null
            ? registry.ConnectionsForDevice(normalized, toDevice)
            : registry.ConnectionsFor(normalized);
        if (excludeConnectionId is not null)
            conns = conns.Where(c => c != excludeConnectionId).ToList();
        if (conns.Count == 0) return false;
        await hub.Clients.Clients(conns).SendAsync(MeshHubProtocol.Receive, envelopeJson);
        return true;
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();

    public static string DeviceInboxKey(string handle, string deviceId)
        => $"{Normalize(handle)}\u001f{deviceId}";
}
