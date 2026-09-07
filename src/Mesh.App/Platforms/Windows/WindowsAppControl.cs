using System.Drawing;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Mesh.App.Services;
using MenuFlyout = Microsoft.UI.Xaml.Controls.MenuFlyout;
using MenuFlyoutItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;
using MenuFlyoutSeparator = Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator;

namespace Mesh.App.Platforms.Windows;

/// <summary>Windows tray, activation, headless-window, and graceful-exit integration.</summary>
public sealed class WindowsAppControl : IAppControl
{
    private static Microsoft.UI.Xaml.Window? window;
    private static AppWindow? appWindow;
    private static TaskbarIcon? tray;
    private static bool forceQuit;
    private static bool headless;

    public void ShowMainWindow() => Show();
    public void Quit() => QuitApp();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mesh";

    public bool IsLaunchAtStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public void SetLaunchAtStartup(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key is null) return;
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                    key.SetValue(RunValueName, $"\"{executable}\"");
            }
            else if (key.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
        }
    }

    public static void AttachTray(Microsoft.UI.Xaml.Window createdWindow)
    {
        if (window is not null) return;
        window = createdWindow;
        headless = false;
        appWindow = ResolveAppWindow(createdWindow);
        AppLifecycleState.SetForeground(true);
        createdWindow.Activated += (_, args) => AppLifecycleState.SetForeground(
            args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated
            && appWindow?.IsVisible == true);

        if (appWindow.Presenter is OverlappedPresenter presenter
            && Microsoft.Maui.Storage.Preferences.Get("win.maximized", false))
            presenter.Maximize();
        appWindow.Changed += (sender, _) =>
        {
            if (sender.Presenter is OverlappedPresenter current)
                Microsoft.Maui.Storage.Preferences.Set(
                    "win.maximized",
                    current.State == OverlappedPresenterState.Maximized);
        };
        appWindow.Closing += (_, args) =>
        {
            if (forceQuit) return;
            args.Cancel = true;
            AppLifecycleState.SetForeground(false);
            appWindow.Hide();
        };

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open Mesh", Command = new RelayCommand(Show) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit Mesh", Command = new RelayCommand(QuitApp) });
        tray = new TaskbarIcon
        {
            ToolTipText = "Mesh",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(Show),
            NoLeftClickDelay = true
        };

        var pngPath = Path.Combine(AppContext.BaseDirectory, "mesh-tray.png");
        if (File.Exists(pngPath))
        {
            try
            {
                using var bitmap = new Bitmap(pngPath);
                using var small = new Bitmap(bitmap, new System.Drawing.Size(32, 32));
                tray.Icon = Icon.FromHandle(small.GetHicon());
            }
            catch
            {
            }
        }
        tray.ForceCreate(false);
    }

    public static void AttachHeadless(Microsoft.UI.Xaml.Window createdWindow)
    {
        if (window is not null) return;
        window = createdWindow;
        headless = true;
        appWindow = ResolveAppWindow(createdWindow);
        appWindow.IsShownInSwitchers = false;
        AppLifecycleState.SetForeground(true);
        createdWindow.Activated += (_, _) =>
        {
            AppLifecycleState.SetForeground(true);
            appWindow?.Hide();
        };
        appWindow.Hide();
    }

    internal static void Activate(IReadOnlyList<string> arguments)
        => RunOnUiThread(() =>
        {
            ShowCore();
            Mesh.App.WinUI.App.DispatchFromArgs(arguments);
        });

    private static AppWindow ResolveAppWindow(Microsoft.UI.Xaml.Window value)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(value);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static void Show() => RunOnUiThread(ShowCore);

    private static void ShowCore()
    {
        if (headless || appWindow is null || window is null) return;
        appWindow.Show();
        window.Activate();
        AppLifecycleState.SetForeground(true);
        appWindow.MoveInZOrderAtTop();
    }

    private static void QuitApp()
    {
        if (MeshDesktopInstanceRuntime.RequestLocalShutdown()) return;
        ExitNow();
    }

    internal static void ExitNow()
        => RunOnUiThread(() =>
        {
            forceQuit = true;
            try { tray?.Dispose(); } catch { }
            tray = null;
            Microsoft.UI.Xaml.Application.Current.Exit();
        });

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = window?.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => action());
            return;
        }
        action();
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
