using Mesh.Relay.Backplane;

namespace Mesh.Relay.Hub;

/// <summary>
/// Periodically re-asserts backplane presence for every foreground handle connected to this
/// instance, so an idle foreground client's messages do not wrongly fall through to the inbox.
/// Short-lived background drains are deliberately excluded from ordinary online presence.
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
                foreach (var handle in registry.LocalHandles(includeBackgroundSync: false))
                    await backplane.SetPresenceAsync(handle, stoppingToken);
                foreach (var (handle, deviceId) in registry.LocalDevices(includeBackgroundSync: false))
                    await backplane.SetDevicePresenceAsync(handle, deviceId, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient backplane hiccup: try again next tick */ }
        }
    }
}
