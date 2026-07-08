using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Mesh.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density, WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Allow the window to draw into the display cutout on the short edges so the
        // CSS env(safe-area-inset-*) values are populated. ShortEdges is API 28+ (P).
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var attributes = Window?.Attributes;
            if (attributes is not null)
            {
                attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                Window!.Attributes = attributes;
            }
        }
    }
}
