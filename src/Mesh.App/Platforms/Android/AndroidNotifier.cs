#if ANDROID
using System.Security.Cryptography;
using System.Text;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Mesh.App.Services;
using Microsoft.Extensions.Logging;

namespace Mesh.App.Platforms.Android;

public sealed class AndroidNotifier(ILogger<AndroidNotifier> logger) : INotifier
{
    private const string MessageChannel = "mesh_messages";
    private const string TopicChannel = "mesh_topics";
    private const string DecisionChannel = "mesh_decisions";
    private static readonly int GenericWakeNotificationId = NativeId("mesh-generic-wake");
    private static int badgeCount;
    private readonly Context context = global::Android.App.Application.Context
        ?? throw new InvalidOperationException("Android application context is unavailable.");

    public Task<bool> ShowAsync(LocalNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanNotify(context)) return Task.FromResult(false);
        try
        {
            EnsureChannels(context);
            var manager = NotificationManagerCompat.From(context);
            if (manager is null) return Task.FromResult(false);
            manager.Cancel(GenericWakeNotificationId);

            var intent = new Intent(context, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            intent.SetData(global::Android.Net.Uri.Parse(notification.Route));
            intent.PutExtra("mesh_notification_route", notification.Route);
            var pending = PendingIntent.GetActivity(
                context,
                NativeId(notification.StableId),
                intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var builder = new NotificationCompat.Builder(context, Channel(notification.Kind));
            var style = new NotificationCompat.BigTextStyle();
            style.BigText(notification.Body);
            builder.SetSmallIcon(context.ApplicationInfo?.Icon ?? 0);
            builder.SetContentTitle(notification.Title);
            builder.SetContentText(notification.Body);
            builder.SetStyle(style);
            builder.SetAutoCancel(true);
            builder.SetContentIntent(pending);
            builder.SetGroup(Group(notification.Kind));
            builder.SetNumber(Math.Max(0, Volatile.Read(ref badgeCount)));
            builder.SetPriority(NotificationCompat.PriorityHigh);
            if (!notification.PlaySound) builder.SetSilent(true);
            var native = builder.Build();
            if (native is null) return Task.FromResult(false);
            manager.Notify(NativeId(notification.StableId), native);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Android notification could not be shown: kind={Kind}", notification.Kind);
            return Task.FromResult(false);
        }
    }

    public Task RemoveAsync(string stableId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        NotificationManagerCompat.From(context)?.Cancel(NativeId(stableId));
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Volatile.Write(ref badgeCount, 0);
        NotificationManagerCompat.From(context)?.CancelAll();
        return Task.CompletedTask;
    }

    public Task SetBadgeAsync(int count, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Volatile.Write(ref badgeCount, Math.Max(0, count));
        return Task.CompletedTask;
    }

    internal static bool ShowGenericWake(Context context, string wakeId)
    {
        if (!CanNotify(context)) return false;
        EnsureChannels(context);
        var manager = NotificationManagerCompat.From(context);
        if (manager is null) return false;

        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra("mesh_notification_generic", true);
        intent.PutExtra("mesh_wake_id", wakeId);
        var pending = PendingIntent.GetActivity(
            context,
            NativeId($"wake:{wakeId}"),
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(context, MessageChannel);
        builder.SetSmallIcon(context.ApplicationInfo?.Icon ?? 0);
        builder.SetContentTitle("Mesh");
        builder.SetContentText("New activity");
        builder.SetAutoCancel(true);
        builder.SetContentIntent(pending);
        builder.SetGroup("mesh_wakes");
        builder.SetPriority(NotificationCompat.PriorityHigh);
        var notification = builder.Build();
        if (notification is null) return false;
        manager.Notify(GenericWakeNotificationId, notification);
        return true;
    }

    internal static bool CanNotify(Context context)
    {
        var manager = NotificationManagerCompat.From(context);
        if (manager?.AreNotificationsEnabled() != true) return false;
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true;
        return ContextCompat.CheckSelfPermission(context, Manifest.Permission.PostNotifications)
               == Permission.Granted;
    }

    private static void EnsureChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager is null) return;
        manager.CreateNotificationChannel(new NotificationChannel(
            MessageChannel,
            "Mesh messages",
            NotificationImportance.Default));
        manager.CreateNotificationChannel(new NotificationChannel(
            TopicChannel,
            "Mesh topics",
            NotificationImportance.Default));
        manager.CreateNotificationChannel(new NotificationChannel(
            DecisionChannel,
            "Mesh decisions",
            NotificationImportance.High));
    }

    private static string Channel(NotificationKind kind)
        => kind switch
        {
            NotificationKind.Message or NotificationKind.ServiceResponse => MessageChannel,
            NotificationKind.TopicCompleted or NotificationKind.TopicFailed or NotificationKind.TopicCancelled
                => TopicChannel,
            _ => DecisionChannel
        };

    private static string Group(NotificationKind kind) => kind switch
    {
        NotificationKind.Message or NotificationKind.ServiceResponse => "mesh_messages",
        NotificationKind.TopicCompleted or NotificationKind.TopicFailed or NotificationKind.TopicCancelled => "mesh_topics",
        NotificationKind.DecisionRequired => "mesh_decisions",
        NotificationKind.ApprovalRequired => "mesh_approvals",
        _ => "mesh_requests"
    };

    private static int NativeId(string stableId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stableId));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }
}
#endif
