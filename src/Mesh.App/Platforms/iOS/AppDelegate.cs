using Foundation;
using Mesh.App.Services;
using Microsoft.Identity.Client;
using UIKit;

namespace Mesh.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

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
