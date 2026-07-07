namespace Mesh.App.Services;

/// <summary>The kind of thing a notification is about, so the UI/OS can style or route it.</summary>
public enum NotifyKind
{
    Message,   // a person sent the owner a direct message
    Request,   // an unknown handle wants to reach the owner
    Approval   // the owner's agent drafted a reply that needs approval
}

/// <summary>
/// Cross-platform desktop/OS notifications (toast popups). On Windows this is backed by the Windows
/// App SDK AppNotificationManager; other platforms get a no-op default until implemented. Mirrors the
/// <see cref="IAppControl"/> pattern so platform behavior is injected.
/// </summary>
public interface INotifier
{
    /// <summary>Shows an OS toast. <paramref name="route"/> is an optional in-app path to open on click.</summary>
    void Notify(string title, string body, NotifyKind kind, string? route = null);
}

/// <summary>No-op notifier for platforms without a toast implementation yet.</summary>
public sealed class DefaultNotifier : INotifier
{
    public void Notify(string title, string body, NotifyKind kind, string? route = null) { }
}
