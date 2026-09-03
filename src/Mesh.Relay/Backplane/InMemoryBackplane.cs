using System.Collections.Concurrent;

namespace Mesh.Relay.Backplane;

/// <summary>
/// Default single-instance backplane. Presence is tracked locally and every handle is
/// owned by this one instance, so cross-instance publish is never needed. Used whenever
/// no Redis connection is configured. Holds no payloads and no durable state.
/// </summary>
public sealed class InMemoryBackplane : IBackplane
{
    internal static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> present = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> presentDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock;

    public InMemoryBackplane(TimeProvider? timeProvider = null)
        => clock = timeProvider ?? TimeProvider.System;

    public string InstanceId { get; } = Guid.NewGuid().ToString("n")[..8];

    public Task StartAsync(
        Func<string, string, Task<BackplaneDeliveryReceipt>> deliverLocal, CancellationToken ct = default)
        => Task.CompletedTask; // nothing to subscribe to on a single instance

    public Task SetPresenceAsync(string handle, CancellationToken ct = default)
    {
        present[handle] = clock.GetUtcNow() + PresenceTtl;
        return Task.CompletedTask;
    }

    public Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        presentDevices[DeviceKey(handle, deviceId)] = clock.GetUtcNow() + PresenceTtl;
        return Task.CompletedTask;
    }

    public Task ClearPresenceAsync(string handle, CancellationToken ct = default)
    {
        present.TryRemove(handle, out _);
        return Task.CompletedTask;
    }

    public Task ClearDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        presentDevices.TryRemove(DeviceKey(handle, deviceId), out _);
        return Task.CompletedTask;
    }

    public Task<string?> GetInstanceForAsync(string handle, CancellationToken ct = default)
        => Task.FromResult(GetLiveOwner(present, handle));

    public Task<string?> GetInstanceForDeviceAsync(string handle, string deviceId, CancellationToken ct = default)
        => Task.FromResult(GetLiveOwner(presentDevices, DeviceKey(handle, deviceId)));

    public Task<BackplaneDeliveryReceipt> PublishToOwnerAsync(
        string instanceId, string toHandle, string deliveryJson, CancellationToken ct = default)
        => Task.FromResult(BackplaneDeliveryReceipt.NotDelivered); // caller already tried the local socket

    private string? GetLiveOwner(
        ConcurrentDictionary<string, DateTimeOffset> leases,
        string key)
    {
        if (!leases.TryGetValue(key, out var expiresAt)) return null;
        if (clock.GetUtcNow() < expiresAt) return InstanceId;
        leases.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, expiresAt));
        return null;
    }

    private static string DeviceKey(string handle, string deviceId) => $"{handle}\u001f{deviceId}";
}
