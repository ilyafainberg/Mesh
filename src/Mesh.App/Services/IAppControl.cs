namespace Mesh.App.Services;

/// <summary>
/// Cross-platform hook for desktop window lifecycle actions the UI can trigger: showing the main
/// window (e.g. from the tray) and quitting the app for real. On Windows this is implemented by the
/// tray integration so a "Quit" genuinely exits instead of hiding to the tray; other platforms get
/// a simple default.
/// </summary>
public interface IAppControl
{
    /// <summary>Bring the main window to the foreground (used by the tray "Open" action).</summary>
    void ShowMainWindow();

    /// <summary>Quit the application for good (bypasses close-to-tray).</summary>
    void Quit();

    /// <summary>Whether "launch at Windows startup" is currently enabled for this user.</summary>
    bool IsLaunchAtStartupEnabled();

    /// <summary>Enables or disables launching this app when the user signs in to Windows.</summary>
    void SetLaunchAtStartup(bool enabled);
}

/// <summary>Default no-frills implementation for platforms without a system tray.</summary>
public sealed class DefaultAppControl : IAppControl
{
    public void ShowMainWindow() { }

    public void Quit() => Microsoft.Maui.Controls.Application.Current?.Quit();

    public bool IsLaunchAtStartupEnabled() => false;

    public void SetLaunchAtStartup(bool enabled) { }
}
