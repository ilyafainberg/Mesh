namespace Mesh.App.Services;

public interface IAppLifecycleState
{
    bool IsForeground { get; }
    DateTimeOffset? ForegroundedAt { get; }
    event Action<bool>? ForegroundChanged;
}

/// <summary>Process-wide lifecycle state shared by MAUI services and native platform callbacks.</summary>
public sealed class AppLifecycleState : IAppLifecycleState
{
    private sealed record LifecycleSnapshot(bool IsForeground, DateTimeOffset? ForegroundedAt);
    private static LifecycleSnapshot snapshot = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? new(false, null)
        : new(true, DateTimeOffset.UtcNow);
    private static AppLifecycleState? current;

    public AppLifecycleState()
        => Volatile.Write(ref current, this);

    public static bool IsProcessForeground => Volatile.Read(ref snapshot).IsForeground;
    public static DateTimeOffset? ProcessForegroundedAt => Volatile.Read(ref snapshot).ForegroundedAt;

    public bool IsForeground => IsProcessForeground;
    public DateTimeOffset? ForegroundedAt => ProcessForegroundedAt;
    public event Action<bool>? ForegroundChanged;

    public static void SetForeground(bool value)
    {
        while (true)
        {
            var previous = Volatile.Read(ref snapshot);
            if (previous.IsForeground == value) return;
            var next = new LifecycleSnapshot(value, value ? DateTimeOffset.UtcNow : null);
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, next, previous), previous))
                break;
        }
        Volatile.Read(ref current)?.ForegroundChanged?.Invoke(value);
    }
}
