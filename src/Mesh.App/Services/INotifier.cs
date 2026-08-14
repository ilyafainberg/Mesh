namespace Mesh.App.Services;

/// <summary>
/// Platform notification surface. Stable IDs let every operating system replace and remove a
/// logical activity without using user content as an identifier.
/// </summary>
public interface INotifier
{
    Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default);
    Task RemoveAsync(string stableId, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
    Task SetBadgeAsync(int count, CancellationToken ct = default);
}

/// <summary>No-op fallback used only when a platform has no native notification surface.</summary>
public sealed class DefaultNotifier : INotifier
{
    public Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task RemoveAsync(string stableId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SetBadgeAsync(int count, CancellationToken ct = default) => Task.CompletedTask;
}
