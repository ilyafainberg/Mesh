using Microsoft.Maui.Storage;

namespace Mesh.App.Services;

/// <summary>
/// Persists the main window's size and position across runs so Mesh reopens where the user left it.
/// Values are stored in MAUI Preferences (not sensitive). The default size on a fresh install is
/// 1470 x 350, centered. Maximized state is persisted separately by the Windows platform code.
/// </summary>
public static class WindowGeometry
{
    public const double DefaultWidth = 1470;
    public const double DefaultHeight = 990;

    private const string KeyW = "win.width";
    private const string KeyH = "win.height";
    private const string KeyX = "win.x";
    private const string KeyY = "win.y";
    private const string KeyHasPos = "win.hasPos";

    /// <summary>Applies the saved geometry (or the centered default) to a freshly created window.</summary>
    public static void Apply(Window window)
    {
        var w = Preferences.Get(KeyW, DefaultWidth);
        var h = Preferences.Get(KeyH, DefaultHeight);
        if (double.IsNaN(w) || w < 400) w = DefaultWidth;
        if (double.IsNaN(h) || h < 250) h = DefaultHeight;
        window.Width = w;
        window.Height = h;

        if (Preferences.Get(KeyHasPos, false))
        {
            var x = Preferences.Get(KeyX, double.NaN);
            var y = Preferences.Get(KeyY, double.NaN);
            if (!double.IsNaN(x) && !double.IsNaN(y) && IsOnScreen(x, y, w, h))
            {
                window.X = x;
                window.Y = y;
                return;
            }
        }
        CenterOnScreen(window, w, h);
    }

    /// <summary>Saves the window's current size and position. Ignores minimized/zero geometry.</summary>
    public static void Save(Window window)
    {
        try
        {
            var w = window.Width;
            var h = window.Height;
            if (double.IsNaN(w) || double.IsNaN(h) || w < 400 || h < 250) return;
            Preferences.Set(KeyW, w);
            Preferences.Set(KeyH, h);

            var x = window.X;
            var y = window.Y;
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                Preferences.Set(KeyX, x);
                Preferences.Set(KeyY, y);
                Preferences.Set(KeyHasPos, true);
            }
        }
        catch { /* best-effort: never let persistence crash shutdown */ }
    }

    private static void CenterOnScreen(Window window, double w, double h)
    {
        try
        {
            var display = DeviceDisplay.Current.MainDisplayInfo;
            var dw = display.Width / display.Density;
            var dh = display.Height / display.Density;
            window.X = Math.Max(0, (dw - w) / 2);
            window.Y = Math.Max(0, (dh - h) / 2);
        }
        catch { /* fall back to platform default position */ }
    }

    private static bool IsOnScreen(double x, double y, double w, double h)
    {
        try
        {
            var display = DeviceDisplay.Current.MainDisplayInfo;
            var dw = display.Width / display.Density;
            var dh = display.Height / display.Density;
            // Require a little of the window (100px) to be within the primary display bounds.
            return x + w > 100 && y + h > 100 && x < dw - 100 && y < dh - 100;
        }
        catch { return true; }
    }
}
