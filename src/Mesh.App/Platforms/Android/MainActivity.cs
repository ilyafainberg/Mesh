using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace Mesh.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density, WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

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

    /// <summary>Pads the content view by the system bars and display cutout (top + sides), leaving
    /// the bottom to the IME/AdjustResize handling so the keyboard still resizes the view.</summary>
    private sealed class SafeAreaInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null) return insets ?? WindowInsetsCompat.Consumed;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            v.SetPadding(bars.Left, bars.Top, bars.Right, 0);
            return insets;
        }
    }
}
