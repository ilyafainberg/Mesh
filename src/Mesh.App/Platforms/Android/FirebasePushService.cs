using Mesh.App.Services;
#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
#endif

namespace Mesh.App.Platforms.Android;

/// <summary>
/// Android FCM (Firebase Cloud Messaging) token registration scaffold.
/// </summary>
/// <remarks>
/// <para>
/// This is a scaffold only. The Firebase SDK (Xamarin.Firebase.Messaging) is not referenced by the client
/// csproj, so no Firebase types are used here. On Android 13+ (API 33) the app must hold the runtime
/// POST_NOTIFICATIONS permission before notifications can be shown, so <see cref="RegisterAsync"/> requests
/// that permission when needed, then returns null because there is no FCM token source yet.
/// </para>
/// <para>
/// TODO: Add Xamarin.Firebase.Messaging + google-services.json and return the FCM token from
/// FirebaseMessaging.Instance.GetToken(). Needs a Firebase project. Once the SDK is referenced, replace the
/// null return below with the awaited FCM token, and forward it to the Mesh relay.
/// </para>
/// <para>
/// The returned token is later sent to the Mesh relay so the relay can wake a backgrounded phone when a
/// message arrives. That relay endpoint is a separate workstream.
/// </para>
/// </remarks>
public sealed class FirebasePushService : IPushService
{
    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public Task<string?> RegisterAsync(CancellationToken ct = default)
    {
#if ANDROID
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

        // TODO: Add Xamarin.Firebase.Messaging + google-services.json and return the FCM token from
        // FirebaseMessaging.Instance.GetToken(). Needs a Firebase project. Until then there is no token.
        return Task.FromResult<string?>(null);
#else
        // Harmless fallback if this file is ever compiled for a non-Android target.
        return Task.FromResult<string?>(null);
#endif
    }
}
