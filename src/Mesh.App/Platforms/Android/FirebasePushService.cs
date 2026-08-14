using Mesh.App.Services;
#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
#endif
#if ANDROID && MESH_FIREBASE
using Firebase.Messaging;
#endif

namespace Mesh.App.Platforms.Android;

/// <summary>
/// Android FCM (Firebase Cloud Messaging) token registration.
/// </summary>
/// <remarks>
/// <para>
/// The Firebase SDK is compiled in only when the project is built with <c>MeshPushEnabled=true</c> (which
/// defines <c>MESH_FIREBASE</c> and references Xamarin.Firebase.Messaging + a google-services.json). In that
/// build, <see cref="RegisterAsync"/> returns the device's FCM token, which <see cref="MeshClient"/> then
/// hands to the relay so it can wake this device with a metadata-only "Option 1" alert when a message is
/// queued while the app is offline.
/// </para>
/// <para>
/// Without that flag (the default), there is no token source, so <see cref="RegisterAsync"/> still requests
/// the Android 13+ notifications permission but returns null, and the relay simply cannot wake this device.
/// </para>
/// </remarks>
public sealed class FirebasePushService : IPushService
{
    /// <inheritdoc />
    public bool IsSupported => true;

#if ANDROID
    // Latest FCM token observed, either from an explicit GetToken() or an OnNewToken refresh. Cached so a
    // reconnect can re-register the current token without another round-trip to the SDK.
    private static volatile string? latestToken;

    /// <summary>Called by <see cref="MeshFirebaseMessagingService"/> when FCM rotates the device token.</summary>
    internal static void OnTokenRefresh(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) latestToken = token;
    }
#endif

    /// <inheritdoc />
    public Task<PushRegistrationInfo?> RegisterAsync(CancellationToken ct = default)
    {
#if ANDROID
        RequestPostNotificationsIfNeeded();
#if MESH_FIREBASE
        return GetFcmTokenAsync(ct);
#else
        // No Firebase SDK in this build (MeshPushEnabled not set): the permission is requested above, but
        // there is no token source yet, so the relay cannot wake this device.
        return Task.FromResult<PushRegistrationInfo?>(null);
#endif
#else
        // Harmless fallback if this file is ever compiled for a non-Android target.
        return Task.FromResult<PushRegistrationInfo?>(null);
#endif
    }

#if ANDROID
    private static void RequestPostNotificationsIfNeeded()
    {
        // Android 13+ (API 33) gates notifications behind a runtime permission. Request it if we are on a
        // new enough OS and it has not already been granted.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var activity = Platform.CurrentActivity;
            if (activity is not null)
            {
                const string postNotifications = "android.permission.POST_NOTIFICATIONS";
                if (ContextCompat.CheckSelfPermission(activity, postNotifications) != Permission.Granted)
                {
                    ActivityCompat.RequestPermissions(activity, new[] { postNotifications }, requestCode: 9101);
                }
            }
        }
    }

    internal static bool AreAlertsEnabled()
        => AndroidNotifier.CanNotify(global::Android.App.Application.Context);
#endif

#if ANDROID && MESH_FIREBASE
    private static async Task<PushRegistrationInfo?> GetFcmTokenAsync(CancellationToken ct)
    {
        var token = await GetFcmTokenValueAsync(ct).ConfigureAwait(false);
        return PushRegistrationPolicy.Create(token, AreAlertsEnabled());
    }

    private static async Task<string?> GetFcmTokenValueAsync(CancellationToken ct)
    {
        if (latestToken is not null) return latestToken;
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetResult(null));
            FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenListener(tcs));
            var token = await tcs.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token)) latestToken = token;
            return token;
        }
        catch
        {
            return null;
        }
    }

    // Bridges the Google Play Services Task<string> (from FirebaseMessaging.GetToken) back to a .NET Task.
    private sealed class TokenListener : Java.Lang.Object, global::Android.Gms.Tasks.IOnCompleteListener
    {
        private readonly TaskCompletionSource<string?> tcs;
        public TokenListener(TaskCompletionSource<string?> tcs) => this.tcs = tcs;

        public void OnComplete(global::Android.Gms.Tasks.Task task)
        {
            try { tcs.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null); }
            catch { tcs.TrySetResult(null); }
        }
    }
#endif
}
