using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Platform capability checks used to gate features that cannot run inside a mobile
/// app sandbox. "Mobile" means Android or iOS; Windows and MacCatalyst are treated as
/// desktop where spawning child processes and driving local hardware is allowed.
/// </summary>
public static class PlatformCaps
{
    /// <summary>True on Android and iOS, where the app cannot spawn arbitrary child processes.</summary>
    public static bool IsMobile => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public static bool CanHostHomeAgent => !IsMobile;

    public static string DevicePlatform =>
        OperatingSystem.IsWindows() ? Mesh.Shared.DevicePlatforms.Windows :
        OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() ? Mesh.Shared.DevicePlatforms.MacOS :
        OperatingSystem.IsAndroid() ? Mesh.Shared.DevicePlatforms.Android :
        OperatingSystem.IsIOS() ? Mesh.Shared.DevicePlatforms.IOS :
        Mesh.Shared.DevicePlatforms.Unknown;
}

/// <summary>Capability helpers for <see cref="LocalToolKind"/>.</summary>
public static class LocalToolKindExtensions
{
    /// <summary>
    /// Desktop-only tools depend on spawning a native child process or driving desktop
    /// hardware (a shell, a script runtime, a real browser), none of which are possible in
    /// the Android/iOS sandbox. These are hidden and never offered to the agent on mobile.
    /// </summary>
    public static bool IsDesktopOnly(this LocalToolKind kind) => kind switch
    {
        LocalToolKind.PowerShell => true,
        LocalToolKind.Cmd => true,
        LocalToolKind.Python => true,
        LocalToolKind.CSharpScript => true,
        LocalToolKind.Browser => true,
        LocalToolKind.HeadlessBrowser => true,
        LocalToolKind.WorkIq => true,
        _ => false
    };
}
