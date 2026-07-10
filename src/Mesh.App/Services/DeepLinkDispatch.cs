namespace Mesh.App.Services;

/// <summary>
/// Bridges an OS deep-link activation (a clicked mesh:// URI, delivered by the Windows or Android
/// platform layer) to the Blazor UI. Mirrors the WindowsNotifier.OnActivated pattern: the platform
/// code calls <see cref="Dispatch"/> with the raw URI, and the root layout registers a handler via
/// <see cref="SetHandler"/> to route it. If a link arrives before the UI is ready (cold start), it is
/// held and delivered as soon as a handler subscribes.
/// </summary>
public static class DeepLinkDispatch
{
    private static readonly object Gate = new();
    private static Action<string>? handler;
    private static string? pending;

    /// <summary>Registers (or clears) the UI handler. On registration, any queued cold-start link is delivered.</summary>
    public static void SetHandler(Action<string>? h)
    {
        string? queued = null;
        lock (Gate)
        {
            handler = h;
            if (h is not null && pending is not null)
            {
                queued = pending;
                pending = null;
            }
        }
        if (queued is not null) h!(queued);
    }

    /// <summary>Called by the platform layer with a raw mesh:// URI. Routed now, or held until the UI subscribes.</summary>
    public static void Dispatch(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        Action<string>? h;
        lock (Gate)
        {
            h = handler;
            if (h is null) { pending = uri; return; }
        }
        h(uri);
    }
}
