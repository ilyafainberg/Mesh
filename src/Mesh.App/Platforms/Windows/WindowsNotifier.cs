#if WINDOWS
using System.Security.Cryptography;
using System.Text;
using Mesh.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.BadgeNotifications;

namespace Mesh.App.Platforms.Windows;

public sealed class WindowsNotifier(ILogger<WindowsNotifier> logger) : INotifier
{
    private static int registered;

    public static void Prime()
    {
        if (Interlocked.Exchange(ref registered, 1) != 0) return;
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, args) =>
        {
            var route = ParseArgument(args.Argument, "route");
            if (!string.IsNullOrWhiteSpace(route)) DeepLinkDispatch.Dispatch(route);
        };
        manager.Register();
    }

    public Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Prime();
            var native = new AppNotificationBuilder()
                .AddText(notification.Title)
                .AddText(notification.Body)
                .AddArgument("route", notification.Route);
            if (!notification.PlaySound) native.MuteAudio();
            var built = native.BuildNotification();
            built.Tag = Tag(notification.StableId);
            built.Group = Group(notification.Kind);
            AppNotificationManager.Default.Show(built);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows notification could not be shown: kind={Kind}", notification.Kind);
            return Task.FromResult(false);
        }
    }

    public async Task RemoveAsync(string stableId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Prime();
            await AppNotificationManager.Default.RemoveByTagAsync(Tag(stableId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows notification could not be removed.");
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Prime();
            await AppNotificationManager.Default.RemoveAllAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows notifications could not be cleared.");
        }
    }
    public Task SetBadgeAsync(int count, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (count <= 0) BadgeNotificationManager.Current.ClearBadge();
            else BadgeNotificationManager.Current.SetBadgeAsCount((uint)count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows notification badge could not be updated.");
        }
        return Task.CompletedTask;
    }

    private static string Tag(string stableId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableId)))[..16].ToLowerInvariant();

    private static string Group(NotificationKind kind) => kind switch
    {
        NotificationKind.Message or NotificationKind.ServiceResponse => "messages",
        NotificationKind.TopicCompleted or NotificationKind.TopicFailed or NotificationKind.TopicCancelled => "topics",
        NotificationKind.DecisionRequired => "decisions",
        NotificationKind.ApprovalRequired => "approvals",
        _ => "requests"
    };

    private static string? ParseArgument(string argument, string key)
    {
        foreach (var pair in argument.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (string.Equals(Uri.UnescapeDataString(pair[..separator]), key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return null;
    }
}
#endif
