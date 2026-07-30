using Mesh.Relay.Backplane;

namespace Mesh.Relay.Hub;

/// <summary>
/// Periodically re-asserts backplane presence for every foreground handle connected to this
/// instance, so an idle foreground client's messages do not wrongly fall through to the inbox.
/// Short-lived background drains renew a separate transient route and are deliberately excluded
/// from ordinary online presence.
/// </summary>
public sealed class PresenceRenewer(
    ConnectionRegistry registry,
    IBackplane backplane,
    AgentDispatchCoordinator agentDispatch) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                foreach (var handle in registry.LocalHandles(includeBackgroundSync: false))
                {
                    await backplane.SetPresenceAsync(handle, stoppingToken);
                    // Also re-drive expired delivery claims. An uncertain send or relay crash leaves
                    // the request leased, and the stable request envelope id makes redelivery safe.
                    await agentDispatch.DispatchAvailableAsync(handle, stoppingToken);
                }
                foreach (var (handle, deviceId) in registry.LocalDevices(includeBackgroundSync: false))
                    await backplane.SetDevicePresenceAsync(handle, deviceId, stoppingToken);
                foreach (var (handle, deviceId) in registry.LocalBackgroundDevices())
                {
                    await backplane.SetTransientDeviceRouteAsync(handle, deviceId, stoppingToken);
                    if (!registry.HasBackgroundConnectionForDevice(handle, deviceId))
                        await backplane.ClearTransientDeviceRouteAsync(handle, deviceId, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient backplane hiccup: try again next tick */ }
        }
    }
}
