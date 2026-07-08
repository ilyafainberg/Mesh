using System.Windows.Input;
using System.Drawing;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Mesh.App.Services;
using MenuFlyout = Microsoft.UI.Xaml.Controls.MenuFlyout;
using MenuFlyoutItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;
using MenuFlyoutSeparator = Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator;

namespace Mesh.App.Platforms.Windows;

/// <summary>
/// Windows desktop integration: a system tray icon, close-to-tray behavior, and a real quit.
/// Closing the window hides it to the tray instead of exiting; the tray menu (and an in-app Quit
/// button) exit for good. Backed by static state so the DI-resolved <see cref="IAppControl"/> and
/// the window-created lifecycle hook share one tray.
/// </summary>
public sealed class WindowsAppControl : IAppControl
{
    private static Microsoft.UI.Xaml.Window? window;
    private static AppWindow? appWindow;
    private static TaskbarIcon? tray;
    private static bool forceQuit;

    public void ShowMainWindow() => Show();
    public void Quit() => QuitApp();

    // "Launch at startup" is stored in the per-user Run key so it works for both the installer and
    // the portable build, and points at the actual running executable.
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mesh";

    public bool IsLaunchAtStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(RunValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    public void SetLaunchAtStartup(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe)) key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort: a registry failure should not crash the app */ }
    }

    /// <summary>Wires the tray + close-to-tray onto the app's main window. Called once at window creation.</summary>
    public static void AttachTray(Microsoft.UI.Xaml.Window w)
    {
        if (tray is not null) return; // already attached
        window = w;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);

        // Restore the maximized state from the last run, and persist it whenever it changes, so the
        // window reopens the way the user left it (size/position are handled by WindowGeometry).
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            if (Microsoft.Maui.Storage.Preferences.Get("win.maximized", false))
                presenter.Maximize();
        }
        appWindow.Changed += (sender, args) =>
        {
            if (sender.Presenter is OverlappedPresenter p)
                Microsoft.Maui.Storage.Preferences.Set("win.maximized", p.State == OverlappedPresenterState.Maximized);
        };

        // Intercept the window close: hide to tray unless the user really chose Quit.
        appWindow.Closing += (_, args) =>
        {
            if (forceQuit) return;
            args.Cancel = true;
            appWindow.Hide();
        };

        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open Mesh", Command = new RelayCommand(Show) };
        var quit = new MenuFlyoutItem { Text = "Quit Mesh", Command = new RelayCommand(QuitApp) };
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);

        tray = new TaskbarIcon
        {
            ToolTipText = "Mesh",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(Show),
            NoLeftClickDelay = true
        };

        // Load the tray glyph as a System.Drawing.Icon (set synchronously via the icon handle),
        // which is reliable at 16px, unlike an async BitmapImage that can render blank.
        var pngPath = Path.Combine(AppContext.BaseDirectory, "mesh-tray.png");
        if (File.Exists(pngPath))
        {
            try
            {
                using var bmp = new Bitmap(pngPath);
                using var small = new Bitmap(bmp, new System.Drawing.Size(32, 32));
                tray.Icon = Icon.FromHandle(small.GetHicon());
            }
            catch { /* fall back to no icon rather than crash */ }
        }

        tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private static void Show()
    {
        if (appWindow is null || window is null) return;
        appWindow.Show();
        window.Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd); // no-op keep-alive for interop
        appWindow.MoveInZOrderAtTop();
    }

    private static void QuitApp()
    {
        forceQuit = true;
        try { tray?.Dispose(); } catch { }
        tray = null;
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    /// <summary>Minimal ICommand for wiring tray clicks to an action.</summary>
    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
