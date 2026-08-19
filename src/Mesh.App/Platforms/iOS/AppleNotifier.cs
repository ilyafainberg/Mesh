#if IOS
using Foundation;
using Mesh.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using UIKit;
using UserNotifications;

namespace Mesh.App.Platforms.iOS;

public sealed class AppleNotifier(ILogger<AppleNotifier> logger) : INotifier
{
    internal const string RouteKey = "mesh_route";

    public async Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings.AuthorizationStatus is UNAuthorizationStatus.Denied or UNAuthorizationStatus.NotDetermined)
            return false;
        await RemoveDeliveredGenericWakeNotificationsAsync(ct).ConfigureAwait(false);

        var content = new UNMutableNotificationContent
        {
            Title = notification.Title,
            Body = notification.Body,
            ThreadIdentifier = Group(notification.Kind),
            Sound = notification.PlaySound ? UNNotificationSound.Default : null
        };
        var userInfo = new NSMutableDictionary
        {
            [new NSString(RouteKey)] = new NSString(notification.Route)
        };
        content.UserInfo = userInfo;
        var request = UNNotificationRequest.FromIdentifier(notification.StableId, content, null);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        UNUserNotificationCenter.Current.AddNotificationRequest(request, error =>
        {
            if (error is null) completion.TrySetResult(true);
            else
            {
                logger.LogWarning("iOS notification could not be scheduled: code={Code}", error.Code);
                completion.TrySetResult(false);
            }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private static async Task RemoveDeliveredGenericWakeNotificationsAsync(CancellationToken ct)
    {
        var delivered = await GetDeliveredNotificationsAsync(ct).ConfigureAwait(false);
        var genericIds = delivered
            .Where(item => AppDelegate.TryGetMeshSyncNotification(
                item.Request.Content.UserInfo,
                out _,
                out _))
            .Select(item => item.Request.Identifier)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        if (genericIds.Length > 0)
            UNUserNotificationCenter.Current.RemoveDeliveredNotifications(genericIds);
    }

    public Task RemoveAsync(string stableId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        UNUserNotificationCenter.Current.RemovePendingNotificationRequests([stableId]);
        UNUserNotificationCenter.Current.RemoveDeliveredNotifications([stableId]);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        UNUserNotificationCenter.Current.RemoveAllPendingNotificationRequests();
        UNUserNotificationCenter.Current.RemoveAllDeliveredNotifications();
        return Task.CompletedTask;
    }
    public Task SetBadgeAsync(int count, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var badge = Math.Max(0, count);
        if (OperatingSystem.IsIOSVersionAtLeast(16))
            return UNUserNotificationCenter.Current.SetBadgeCountAsync(badge);
        if (OperatingSystem.IsMacCatalystVersionAtLeast(16))
            return UNUserNotificationCenter.Current.SetBadgeCountAsync(badge);
#pragma warning disable CA1422 // iOS 15 requires the legacy badge API.
        return MainThread.InvokeOnMainThreadAsync(() =>
            UIApplication.SharedApplication.ApplicationIconBadgeNumber = badge);
#pragma warning restore CA1422
    }

    private static async Task<UNNotificationSettings> GetSettingsAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<UNNotificationSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        UNUserNotificationCenter.Current.GetNotificationSettings(settings => completion.TrySetResult(settings));
        return await completion.Task.ConfigureAwait(false);
    }

    private static async Task<UNNotification[]> GetDeliveredNotificationsAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<UNNotification[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        UNUserNotificationCenter.Current.GetDeliveredNotifications(
            notifications => completion.TrySetResult(notifications ?? []));
        return await completion.Task.ConfigureAwait(false);
    }

    private static string Group(NotificationKind kind) => kind switch
    {
        NotificationKind.Message or NotificationKind.ServiceResponse => "messages",
        NotificationKind.TopicCompleted or NotificationKind.TopicFailed or NotificationKind.TopicCancelled => "topics",
        NotificationKind.DecisionRequired => "decisions",
        NotificationKind.ApprovalRequired => "approvals",
        _ => "requests"
    };
}
#endif
