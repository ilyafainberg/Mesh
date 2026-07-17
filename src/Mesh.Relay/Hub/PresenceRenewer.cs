using Mesh.Relay.Backplane;

namespace Mesh.Relay.Hub;

/// <summary>
/// Periodically re-asserts backplane presence for every handle connected to this instance, so
/// the presence TTL never lapses for an idle but still-connected client. Without this, an idle
/// connection's presence key would expire and its messages would wrongly fall through to the
/// offline inbox.
/// </summary>
public sealed class PresenceRenewer(ConnectionRegistry registry, IBackplane backplane) : BackgroundService
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
