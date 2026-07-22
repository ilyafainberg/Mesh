#if ANDROID && MESH_FIREBASE
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Firebase.Messaging;

namespace Mesh.App.Platforms.Android;

/// <summary>
/// Receives FCM callbacks: token refreshes (forwarded to <see cref="FirebasePushService"/> so the next relay
/// registration uses the fresh token) and incoming messages. When the app is backgrounded or killed, the FCM
/// SDK auto-displays the relay-composed notification payload; this service only needs to draw the banner for
/// the foreground case, reusing the same metadata-only title/body the relay composed
/// ("Message from @sender" / "New group message").
/// </summary>
[Service(Exported = false)]
[IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
public sealed class MeshFirebaseMessagingService : FirebaseMessagingService
{
    public const string ChannelId = "mesh_messages";
    private const string ChannelName = "Messages";

    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        FirebasePushService.OnTokenRefresh(token);
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        var n = message.GetNotification();
        var title = n?.Title ?? "Mesh";
        var body = n?.Body ?? "New message";
        EnsureChannel(this);
        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetSmallIcon(ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.SymActionEmail)
            .SetAutoCancel(true)
            .SetPriority(NotificationCompat.PriorityHigh);
        NotificationManagerCompat.From(this).Notify(global::System.Environment.TickCount, builder.Build());
    }

    // Creates the notification channel the relay-composed alerts post to. Idempotent (Android ignores a
    // repeat create for an existing channel). No-op below Android 8, where channels did not exist yet.
    public static void EnsureChannel(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (mgr is null || mgr.GetNotificationChannel(ChannelId) is not null) return;
        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.High)
        {
            Description = "Message and group notifications",
        };
        mgr.CreateNotificationChannel(channel);
    }
}
#endif
