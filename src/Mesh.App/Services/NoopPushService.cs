namespace Mesh.App.Services;

/// <summary>
/// Default no-op push service for platforms without a push implementation (Windows, Mac). Reports no push
/// support and never returns a token, so callers can safely feature-check via <see cref="IsSupported"/>.
/// </summary>
public sealed class NoopPushService : IPushService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<string?> RegisterAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
