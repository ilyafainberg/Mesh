namespace Mesh.App.Services;

public interface IAppLifecycleState
{
    bool IsForeground { get; }
    event Action<bool>? ForegroundChanged;
}

/// <summary>Process-wide lifecycle state shared by MAUI services and native platform callbacks.</summary>
public sealed class AppLifecycleState : IAppLifecycleState
{
    private static int foreground = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? 0 : 1;
    private static AppLifecycleState? current;

    public AppLifecycleState()
        => Volatile.Write(ref current, this);

    public static bool IsProcessForeground => Volatile.Read(ref foreground) != 0;

    public bool IsForeground => IsProcessForeground;
    public event Action<bool>? ForegroundChanged;

    public static void SetForeground(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref foreground, next) == next) return;
        Volatile.Read(ref current)?.ForegroundChanged?.Invoke(value);
    }
}
