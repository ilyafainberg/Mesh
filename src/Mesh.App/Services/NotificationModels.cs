using Mesh.App.Domain;

namespace Mesh.App.Services;

public enum NotificationKind
{
    None = -1,
    Message,
    TopicCompleted,
    TopicFailed,
    TopicCancelled,
    DecisionRequired,
    ApprovalRequired,
    ContactRequest,
    ServiceInvite,
    ServiceResponse
}

/// <summary>Encrypted notification metadata carried inside a Protocol 9 domain envelope.</summary>
public sealed record NotificationIntent(
    bool Notify,
    string? StableId = null,
    NotificationKind Kind = NotificationKind.None,
    string? EntityId = null,
    string? ConversationId = null,
    string? Route = null,
    string? Title = null,
    string? Body = null,
    bool IsHistorical = false,
    bool SuppressOnOriginAccount = false)
{
    public static NotificationIntent SuppressedHistorical { get; } = new(false, IsHistorical: true);
}

/// <summary>Activity that is eligible for policy only after encrypted state committed locally.</summary>
public sealed record CommittedActivity(
    string StableId,
    string SourceEventId,
    NotificationKind Kind,
    string EntityId,
    string? ConversationId,
    string Route,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset CommittedAt,
    bool IsHistorical,
    bool NotifyRequested,
    string? OriginAccount,
    bool SuppressOnOriginAccount = false);

public sealed record LocalNotification(
    string StableId,
    NotificationKind Kind,
    string Title,
    string Body,
    string Route,
    bool PlaySound);

public interface INotificationCoordinator
{
    Task OnCommittedActivityAsync(CommittedActivity activity, CancellationToken ct = default);
    Task RecoverPendingAsync(CancellationToken ct = default);
    Task MarkEntityReadAsync(string entityId, CancellationToken ct = default);
    Task MarkKindReadAsync(NotificationKind kind, CancellationToken ct = default);
    Task ClearEntityAsync(string entityId, CancellationToken ct = default);
    Task ResetForAccountAsync(CancellationToken ct = default);
    Task RefreshBadgeAsync(CancellationToken ct = default);
}

public interface INotificationState
{
    string LocalHandle { get; }
    bool DoNotDisturb { get; }
    NotificationPreviewMode NotificationPreview { get; }
    bool NotificationSound { get; }
    bool TryRecordNotificationActivity(CommittedActivity activity);
    void MarkNotificationBannerShown(string stableId);
    void MarkNotificationSuppressed(string stableId);
    void MarkNotificationRead(string stableId);
    IReadOnlyList<string> MarkNotificationEntityRead(string entityId);
    IReadOnlyList<string> MarkNotificationKindRead(NotificationKind kind);
    CommittedActivity? GetPendingNotificationActivity(string stableId);
    IReadOnlyList<CommittedActivity> ListPendingNotificationActivities(int limit);
    int GetUnreadNotificationCount();
    string? GetHighestPriorityNotificationRoute();
    bool IsNotificationEntityMuted(string entityId);
}

public static class NotificationRoutes
{
    public static string Messages(string conversationId)
        => $"mesh://messages/{Uri.EscapeDataString(conversationId)}";

    public static string Topic(string threadId)
        => $"mesh://me/{Uri.EscapeDataString(threadId)}";

    public static string Ask(string threadId, string promptId)
        => $"mesh://me/{Uri.EscapeDataString(threadId)}/ask/{Uri.EscapeDataString(promptId)}";

    public const string Requests = "mesh://requests";
    public const string Approvals = "mesh://approvals";
}

public enum NotificationRouteKind
{
    Messages,
    Topic,
    Ask,
    Requests,
    Approvals
}

public sealed record ParsedNotificationRoute(
    NotificationRouteKind Kind,
    string? EntityId = null,
    string? PromptId = null);

public static class NotificationRouteParser
{
    public static bool TryParse(string raw, out ParsedNotificationRoute route)
    {
        route = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "mesh", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        switch (uri.Host.ToLowerInvariant())
        {
            case "messages" when segments.Length == 1:
                route = new ParsedNotificationRoute(NotificationRouteKind.Messages, segments[0]);
                return true;
            case "me" when segments.Length == 1:
                route = new ParsedNotificationRoute(NotificationRouteKind.Topic, segments[0]);
                return true;
            case "me" when segments.Length == 3
                           && string.Equals(segments[1], "ask", StringComparison.OrdinalIgnoreCase):
                route = new ParsedNotificationRoute(NotificationRouteKind.Ask, segments[0], segments[2]);
                return true;
            case "requests" when segments.Length == 0:
                route = new ParsedNotificationRoute(NotificationRouteKind.Requests);
                return true;
            case "approvals" when segments.Length == 0:
                route = new ParsedNotificationRoute(NotificationRouteKind.Approvals);
                return true;
            default:
                return false;
        }
    }
}

public static class NotificationIntents
{
    public static NotificationIntent Message(
        string lineId,
        string conversationId,
        string sender,
        string body,
        bool suppressOnOriginAccount = true)
        => new(
            true,
            $"message:{lineId}",
            NotificationKind.Message,
            conversationId,
            conversationId,
            NotificationRoutes.Messages(conversationId),
            $"Message from {sender}",
            body,
            SuppressOnOriginAccount: suppressOnOriginAccount);

    public static NotificationIntent Topic(
        string runId,
        string threadId,
        string topicTitle,
        NotificationKind kind,
        string? body = null)
        => new(
            true,
            $"topic:{runId}:terminal",
            kind,
            threadId,
            threadId,
            NotificationRoutes.Topic(threadId),
            kind switch
            {
                NotificationKind.TopicCompleted => "Response ready",
                NotificationKind.TopicCancelled => "Response cancelled",
                _ => "Response failed"
            },
            string.IsNullOrWhiteSpace(body) ? topicTitle : body);

    public static NotificationIntent Ask(string promptId, string threadId, string question)
        => new(
            true,
            $"ask:{promptId}",
            NotificationKind.DecisionRequired,
            promptId,
            threadId,
            NotificationRoutes.Ask(threadId, promptId),
            "Decision required",
            question);

    public static CommittedActivity ToCommittedActivity(
        NotificationIntent intent,
        string sourceEventId,
        DateTimeOffset createdAt,
        DateTimeOffset committedAt,
        string? originAccount)
    {
        if (string.IsNullOrWhiteSpace(intent.StableId)
            || string.IsNullOrWhiteSpace(intent.EntityId)
            || string.IsNullOrWhiteSpace(intent.Route)
            || string.IsNullOrWhiteSpace(intent.Title))
            throw new ArgumentException("A notifying intent is incomplete.", nameof(intent));

        return new CommittedActivity(
            intent.StableId,
            sourceEventId,
            intent.Kind,
            intent.EntityId,
            intent.ConversationId,
            intent.Route,
            intent.Title,
            intent.Body ?? string.Empty,
            createdAt,
            committedAt,
            intent.IsHistorical,
            intent.Notify,
            originAccount,
            intent.SuppressOnOriginAccount);
    }
}
