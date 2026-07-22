using Foundation;
using Mesh.App.Platforms.iOS;
using Mesh.App.Services;
using Microsoft.Identity.Client;
using UIKit;
using UserNotifications;

namespace Mesh.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Present relay-composed alerts even while the app is foregrounded (iOS suppresses them by default).
        UNUserNotificationCenter.Current.Delegate = new MeshNotificationCenterDelegate();
        return base.FinishedLaunching(application, launchOptions);
    }

    // APNs issued this device a token: forward it to ApplePushService so the pending RegisterAsync completes.
    // The token is emitted as a lowercase hex string, which is exactly what the relay's APNs sender targets.
    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
    {
        var bytes = deviceToken.ToArray();
        var hex = new global::System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        ApplePushService.CompleteRegistration(hex.ToString());
    }

    // APNs registration failed (missing entitlement, no network, restricted state): unblock any pending
    // RegisterAsync with a null token so the client simply proceeds without push.
    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
    {
        ApplePushService.FailRegistration();
    }

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        var value = url.AbsoluteString;
        if (value?.StartsWith(MsalAuthService.MobileRedirectUri, StringComparison.OrdinalIgnoreCase) == true)
            return AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(url);

        if (base.OpenUrl(application, url, options))
            return true;

        if (value?.StartsWith("mesh://", StringComparison.OrdinalIgnoreCase) == true)
        {
            DeepLinkDispatch.Dispatch(value);
            return true;
        }
        return false;
    }

    public override bool ContinueUserActivity(
        UIApplication application,
        NSUserActivity userActivity,
        UIApplicationRestorationHandler completionHandler)
    {
        var value = userActivity.WebPageUrl?.AbsoluteString;
        if (value?.StartsWith("https://meshrelay.net/link", StringComparison.OrdinalIgnoreCase) == true)
        {
            DeepLinkDispatch.Dispatch(value);
            return true;
        }
        return base.ContinueUserActivity(application, userActivity, completionHandler);
    }
}

// Presents notifications while the app is in the foreground so the user still sees the relay-composed
// "Message from @sender" / "New group message" banner rather than having it silently dropped by iOS.
public sealed class MeshNotificationCenterDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(
        UNUserNotificationCenter center,
        UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler)
        => completionHandler(
            UNNotificationPresentationOptions.Banner
            | UNNotificationPresentationOptions.Sound
            | UNNotificationPresentationOptions.Badge);
}
