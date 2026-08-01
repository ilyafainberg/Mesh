using Mesh.Relay.Backplane;

namespace Mesh.Relay.Hub;

/// <summary>
/// Periodically re-asserts ephemeral backplane presence (handle and per-device) for every
/// authenticated socket connected to this instance, so presence keys stay live under their TTL
/// while a client is idle. Presence is ephemeral only: nothing here queues, leases or persists a
/// payload, and a lost renew simply lets the TTL expire, marking the device offline.
/// </summary>
public sealed class PresenceRenewer(
    ConnectionRegistry registry,
    IBackplane backplane) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                foreach (var handle in registry.LocalHandles())
                    await backplane.SetPresenceAsync(handle, stoppingToken);
                foreach (var (handle, deviceId) in registry.LocalDevices())
                    await backplane.SetDevicePresenceAsync(handle, deviceId, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient backplane hiccup: try again next tick */ }
        }
    }
}