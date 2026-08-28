namespace Mesh.App.Services;

public static class StartupConnectionRecovery
{
    public static async Task<string?> TryConnectAsync(
        Func<Task> connect,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        try
        {
            await connect().ConfigureAwait(false);
            return null;
        }
        catch (OnlineReplicationError ex)
        {
            diagnostic?.Invoke(ex.Message);
            return $"Mesh is offline: {ex.Message} Check the relay URL or network, then retry.";
        }
    }
}
