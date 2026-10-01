#if IOS
using Foundation;
using System.Globalization;
using Mesh.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using UIKit;
using UserNotifications;

namespace Mesh.App.Platforms.iOS;

public sealed class AppleNotifier(
    ILogger<AppleNotifier> logger,
    NotificationViewState views) : INotifier
{
    internal const string RouteKey = "mesh_route";
    internal const string CreatedAtKey = "mesh_created_at";

    public async Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (views.ShouldSuppressCatchUp(notification.CreatedAt))
            return false;
        if (!await GetAlertsEnabledAsync(ct).ConfigureAwait(false))
            return false;
        try
        {
            await RemoveDeliveredGenericWakeNotificationsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "iOS generic wake notification could not be removed.");
        }

        if (views.ShouldSuppressCatchUp(notification.CreatedAt))
            return false;

        using var content = new UNMutableNotificationContent
        {
            Title = notification.Title,
            Body = notification.Body,
            ThreadIdentifier = Group(notification.Kind),
            Sound = notification.PlaySound ? UNNotificationSound.Default : null
        };
        using var routeKey = new NSString(RouteKey);
        using var route = new NSString(notification.Route);
        using var createdAtKey = new NSString(CreatedAtKey);
        using var createdAt = new NSString(notification.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        using var userInfo = new NSMutableDictionary
        {
            [routeKey] = route,
            [createdAtKey] = createdAt
        };
        content.UserInfo = userInfo;
        using var request = UNNotificationRequest.FromIdentifier(notification.StableId, content, null);
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
        var genericIds = await GetDeliveredGenericWakeIdsAsync(ct).ConfigureAwait(false);
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

    private static async Task<bool> GetAlertsEnabledAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        UNUserNotificationCenter.Current.GetNotificationSettings(settings =>
        {
            using (settings)
                completion.TrySetResult(settings.AuthorizationStatus is UNAuthorizationStatus.Authorized
                    or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral);
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private static async Task<string[]> GetDeliveredGenericWakeIdsAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        UNUserNotificationCenter.Current.GetDeliveredNotifications(notifications =>
        {
            var ids = new List<string>();
            foreach (var notification in notifications ?? [])
            {
                using (notification)
                using (var request = notification.Request)
                using (var content = request.Content)
                using (var userInfo = content.UserInfo)
                {
                    if (AppDelegate.TryGetMeshSyncNotification(userInfo, out _, out _)
                        && !string.IsNullOrWhiteSpace(request.Identifier))
                        ids.Add(request.Identifier);
                }
            }
            completion.TrySetResult(ids.ToArray());
        });
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
