using System.Collections.Concurrent;

namespace Mesh.Relay.Backplane;

/// <summary>
/// Default single-instance backplane. Presence is tracked locally and every handle is
/// owned by this one instance, so cross-instance publish is never needed. Used whenever
/// no Redis connection is configured.
/// </summary>
public sealed class InMemoryBackplane : IBackplane
{
    private readonly ConcurrentDictionary<string, byte> present = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> presentDevices = new(StringComparer.OrdinalIgnoreCase);

    public string InstanceId { get; } = Guid.NewGuid().ToString("n")[..8];

    public Task StartAsync(Func<string, string, Task<bool>> deliverLocal, CancellationToken ct = default)
        => Task.CompletedTask; // nothing to subscribe to on a single instance

    public Task SetPresenceAsync(string handle, CancellationToken ct = default)
    {
        present[handle] = 0;
        return Task.CompletedTask;
    }

    public Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        presentDevices[DeviceKey(handle, deviceId)] = 0;
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
        => Task.FromResult<string?>(present.ContainsKey(handle) ? InstanceId : null);

    public Task<string?> GetInstanceForDeviceAsync(string handle, string deviceId, CancellationToken ct = default)
        => Task.FromResult<string?>(presentDevices.ContainsKey(DeviceKey(handle, deviceId)) ? InstanceId : null);

    public Task<bool> PublishToOwnerAsync(string instanceId, string toHandle, string envelopeJson, CancellationToken ct = default)
        => Task.FromResult(false); // single instance: caller already tried the local socket

    private static string DeviceKey(string handle, string deviceId) => $"{handle}\u001f{deviceId}";
}
