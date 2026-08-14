#if ANDROID && MESH_FIREBASE
using Android.App;
using Firebase.Messaging;
using Mesh.App.Services;

namespace Mesh.App.Platforms.Android;

[Service(Name = "net.meshrelay.mesh.MeshFirebaseMessagingService", Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class MeshFirebaseMessagingService : FirebaseMessagingService
{
    private static readonly NotificationWakeDeduplicator WakeDeduplicator = new(TimeSpan.FromHours(1));

    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        FirebasePushService.OnTokenRefresh(token);
        Observe(PushRegistrationBridge.RegisterCurrentTokenAsync());
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        var data = message.Data?.Select(pair =>
                new KeyValuePair<string, string>(pair.Key, pair.Value))
            ?? Enumerable.Empty<KeyValuePair<string, string>>();
        if (AndroidReplicationWakePolicy.TryParse(data, out var payload))
        {
            var wakeId = payload.WakeId ?? Guid.NewGuid().ToString("n");
            if (!WakeDeduplicator.TryAccept(wakeId, DateTimeOffset.UtcNow)) return;
            var visible = RemoteWakeNotificationPolicy.ShouldShowGenericAlert(
                              payload.ShowAlert,
                              AppLifecycleState.IsProcessForeground)
                          && AndroidNotifier.ShowGenericWake(this, wakeId);
            MeshReplicationSyncWorker.Enqueue(this, wakeId, visible);
            return;
        }

        if (AndroidReplicationWakePolicy.Classify(data)
            == AndroidReplicationWakePayloadKind.UnsupportedMeshPayload)
            System.Diagnostics.Debug.WriteLine("Unsupported Mesh wake payload ignored.");
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static completed => System.Diagnostics.Debug.WriteLine(completed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
#endif
