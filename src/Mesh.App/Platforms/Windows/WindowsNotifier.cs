using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Mesh.App.Services;

namespace Mesh.App.Platforms.Windows;

/// <summary>
/// Windows toast notifications via the Windows App SDK AppNotificationManager, which works for
/// unpackaged apps after Register(). Clicking a toast raises the app and (when a route is attached)
/// asks the app to navigate there. Registration is done once, lazily, on the first notification.
/// </summary>
public sealed class WindowsNotifier : INotifier
{
    private static bool registered;
    private static readonly object gate = new();

    /// <summary>Optional hook the app sets so a clicked toast can bring the window up + navigate.</summary>
    public static Action<string?>? OnActivated { get; set; }

    public void Notify(string title, string body, NotifyKind kind, string? route = null)
    {
        try
        {
            EnsureRegistered();
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);
            if (!string.IsNullOrWhiteSpace(route))
                builder.AddArgument("route", route);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { /* toast failures must never break message handling */ }
    }

    /// <summary>Registers the notification manager eagerly (e.g. at startup) so the first toast pops
    /// reliably on unpackaged apps instead of being dropped while registration is still pending.</summary>
    public static void Prime() => EnsureRegistered();

    private static void EnsureRegistered()
    {
        if (registered) return;
        lock (gate)
        {
            if (registered) return;
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, args) =>
            {
                var route = args.Arguments.TryGetValue("route", out var r) ? r : null;
                OnActivated?.Invoke(route);
            };
            manager.Register();
            registered = true;
        }
    }
}
