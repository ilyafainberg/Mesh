using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using Mesh.App.Services;

namespace Mesh.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density, WindowSoftInputMode = SoftInput.AdjustResize)]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "mesh", DataHost = "link")]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "mesh", DataHost = "service")]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "mesh", DataHost = "user")]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "https", DataHost = "meshrelay.net", DataPathPrefix = "/link", AutoVerify = true)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);

        // Cold start: the app was launched by a clicked mesh:// link (SingleTask so there is one task).
        HandleDeepLinkIntent(Intent);

        // Let the window draw into the display cutout on the short edges (API 28+ / P).
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var attributes = Window?.Attributes;
            if (attributes is not null)
            {
                attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                Window!.Attributes = attributes;
            }
        }

        // The Android WebView does NOT expose the status bar height through CSS
        // env(safe-area-inset-top) (it only reports physical display cutouts), so the
        // web layer alone cannot keep the top bar below the status bar. Pad the content
        // view by the real system-bar + cutout insets natively instead. This keeps the
        // app clear of the status bar (portrait) and the side camera cutout (landscape)
        // on every device. The bottom/IME inset is left to WindowSoftInputMode=AdjustResize.
        if (Window is not null)
        {
            Window.SetStatusBarColor(Android.Graphics.Color.White);
            var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
            if (controller is not null)
                controller.AppearanceLightStatusBars = true; // dark icons on the light status bar
        }

        var content = FindViewById(Android.Resource.Id.Content);
        if (content is not null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SafeAreaInsetsListener());
            ViewCompat.RequestApplyInsets(content);
        }
    }

    /// <summary>Warm start: a clicked mesh:// link arrives while the app is already running (SingleTask).</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        if (intent is not null)
            Intent = intent;
        HandleDeepLinkIntent(intent);
        base.OnNewIntent(intent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        Microsoft.Identity.Client.AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(
            requestCode, resultCode, data);
    }

    /// <summary>Routes a mesh:// deep link from an intent to the Blazor UI, if present.</summary>
    private static void HandleDeepLinkIntent(Intent? intent)
    {
        var data = intent?.DataString;
        if (!string.IsNullOrWhiteSpace(data) &&
            (data.StartsWith("mesh://", System.StringComparison.OrdinalIgnoreCase) ||
             data.StartsWith("https://meshrelay.net/link", System.StringComparison.OrdinalIgnoreCase)))
            DeepLinkDispatch.Dispatch(data);
    }

    /// <summary>Pads the content view by the system bars and display cutout (top + sides) and by the
    /// IME (keyboard) at the bottom, so the WebView shrinks and the focused field stays above the
    /// keyboard. In edge-to-edge mode WindowSoftInputMode=AdjustResize does not resize the WebView on
    /// its own, so the keyboard inset must be applied here.</summary>
    private sealed class SafeAreaInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null) return insets ?? WindowInsetsCompat.Consumed;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            var bottom = System.Math.Max(bars.Bottom, ime.Bottom);
            v.SetPadding(bars.Left, bars.Top, bars.Right, bottom);
            return insets;
        }
    }
}
