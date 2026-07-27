using Mesh.Relay.Backplane;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Push;

public enum PushCategory { None, Message, Group, TopicResponse }

public enum PushDeliveryMode { Alert, AlertAndBackground, Background }

public sealed record PushAlert(
    string Title,
    string Body,
    string Category,
    PushDeliveryMode Mode = PushDeliveryMode.Alert);

public enum PushSendStatus { Sent, Rejected, InvalidToken }

public sealed record PushSendResult(PushSendStatus Status, int StatusCode = 0, string? Reason = null)
{
    public static PushSendResult Sent() => new(PushSendStatus.Sent);
    public static PushSendResult Rejected(int statusCode, string? reason = null)
        => new(PushSendStatus.Rejected, statusCode, reason);
    public static PushSendResult InvalidToken(int statusCode, string? reason = null)
        => new(PushSendStatus.InvalidToken, statusCode, reason);
}

public sealed record PushDispatchOptions(bool BackgroundSyncEnabled);

public interface IPushSender
{
    string Platform { get; }
    Task<PushSendResult> SendAsync(string token, PushAlert alert, CancellationToken ct = default);
}

/// <summary>
/// Sends metadata-only alert and background wakes. APNs is only a wake signal; the durable relay inbox
/// remains authoritative and the client acknowledges each encrypted envelope after local persistence.
/// </summary>
public sealed class PushDispatcher(
    IRelayStore store,
    IBackplane backplane,
    IEnumerable<IPushSender> senders,
    PushDispatchOptions options,
    ILogger<PushDispatcher> logger)
{
    private static readonly TimeSpan BackgroundPushMinimumInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan BackgroundPushWindow = TimeSpan.FromHours(1);
    private const int MaxBackgroundPushesPerWindow = 3;

    private readonly Dictionary<string, IPushSender> byPlatform =
        senders.ToDictionary(s => s.Platform, StringComparer.OrdinalIgnoreCase);

    public bool Enabled => byPlatform.Count > 0;

    public static PushCategory Classify(MeshEnvelope env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return PushHintProtocol.IsTopicResponse(env) ? PushCategory.TopicResponse : Classify(env.Kind);
    }

    public static PushCategory Classify(string kind) => kind switch
    {
        MeshKinds.Fanout or MeshKinds.GroupMessage => PushCategory.Group,
        MeshKinds.Chat or MeshKinds.DirectMessage or MeshKinds.AgentRequest
            or MeshKinds.AgentResponse or MeshKinds.AtomicAgentRequest or MeshKinds.AtomicAgentResponse
            or MeshKinds.ServiceRequest or MeshKinds.ServiceResponse
            => PushCategory.Message,
        _ => PushCategory.None,
    };

    public static bool SupportsBackgroundSync(string kind) => kind is
        MeshKinds.Receipt
        or MeshKinds.GroupControl
        or MeshKinds.GroupMessage
        or MeshKinds.Fanout
        or MeshKinds.DirectMessage
        or MeshKinds.AgentResponse
        or MeshKinds.AtomicAgentResponse
        or MeshKinds.ServiceResponse
        or MeshKinds.Report
        or MeshKinds.TopicRunUpdate
        or DeviceSyncKinds.EnvelopeOperation;

    public void NotifyOffline(string toHandle, string? deviceId, MeshEnvelope env)
    {
        if (!Enabled) return;
        var category = Classify(env);
        var backgroundEligible = SupportsBackgroundSync(env.Kind);
        if (category == PushCategory.None && (!options.BackgroundSyncEnabled || !backgroundEligible)) return;
        _ = SafeSendAsync(toHandle, deviceId, category, backgroundEligible, env.From);
    }

    public void NotifyOfflineSiblings(string toHandle, MeshEnvelope env)
    {
        if (!Enabled) return;
        var category = Classify(env);
        var backgroundEligible = SupportsBackgroundSync(env.Kind);
        if (category == PushCategory.None && (!options.BackgroundSyncEnabled || !backgroundEligible)) return;
        _ = WakeOfflineSiblingsAsync(toHandle, category, backgroundEligible, env.From);
    }

    private async Task SafeSendAsync(
        string toHandle,
        string? deviceId,
        PushCategory category,
        bool backgroundEligible,
        string from)
    {
        try
        {
            var handle = Normalize(toHandle);
            var rec = await store.GetHandleAsync(handle).ConfigureAwait(false);
            if (rec is null || rec.DevicePushTokens.Count == 0) return;

            var targets = (deviceId is null
                    ? rec.DevicePushTokens
                    : rec.DevicePushTokens.Where(kv => string.Equals(kv.Key, deviceId, StringComparison.Ordinal)))
                .ToArray();

            foreach (var (targetDeviceId, token) in targets)
                await TrySendAsync(handle, targetDeviceId, token, category, backgroundEligible, from)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push dispatch failed");
        }
    }

    public Task WakeOfflineSiblingsAsync(string toHandle, string kind, string from)
        => WakeOfflineSiblingsAsync(toHandle, Classify(kind), SupportsBackgroundSync(kind), from);

    private async Task WakeOfflineSiblingsAsync(
        string toHandle,
        PushCategory category,
        bool backgroundEligible,
        string from)
    {
        try
        {
            var handle = Normalize(toHandle);
            var rec = await store.GetHandleAsync(handle).ConfigureAwait(false);
            if (rec is null || rec.DevicePushTokens.Count == 0) return;

            foreach (var (deviceId, token) in rec.DevicePushTokens.ToArray())
            {
                var owner = await backplane.GetInstanceForDeviceAsync(handle, deviceId).ConfigureAwait(false);
                if (owner is not null) continue;
                await TrySendAsync(handle, deviceId, token, category, backgroundEligible, from)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push dispatch failed");
        }
    }

    private async Task TrySendAsync(
        string handle,
        string deviceId,
        DevicePushToken token,
        PushCategory category,
        bool backgroundEligible,
        string from)
    {
        if (string.IsNullOrWhiteSpace(token.Token)
            || !byPlatform.TryGetValue(token.Platform, out var sender))
            return;

        var push = Compose(category, backgroundEligible, from, token);
        if (push is null) return;
        if (push.Mode == PushDeliveryMode.Background)
        {
            var acquired = await store.TryAcquireBackgroundPushAsync(
                handle,
                deviceId,
                DateTimeOffset.UtcNow,
                BackgroundPushMinimumInterval,
                BackgroundPushWindow,
                MaxBackgroundPushesPerWindow).ConfigureAwait(false);
            if (!acquired)
            {
                logger.LogDebug("background push coalesced for {Handle} device {DeviceId}", handle, deviceId);
                return;
            }
        }

        try
        {
            var result = await sender.SendAsync(token.Token, push).ConfigureAwait(false);
            if (result.Status == PushSendStatus.Sent)
            {
                logger.LogInformation(
                    "push sent to {Handle} (platform {Platform}, mode {Mode})",
                    handle, token.Platform, push.Mode);
                return;
            }

            if (result.Status == PushSendStatus.InvalidToken)
            {
                await store.RemoveDevicePushTokenAsync(handle, deviceId).ConfigureAwait(false);
                logger.LogWarning(
                    "removed invalid push token for {Handle} device {DeviceId}: {Reason}",
                    handle, deviceId, result.Reason ?? result.StatusCode.ToString());
                return;
            }

            logger.LogWarning(
                "push rejected for {Handle} (platform {Platform}, status {Status}): {Reason}",
                handle, token.Platform, result.StatusCode, result.Reason ?? "unknown");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push send failed (platform {Platform})", token.Platform);
        }
    }

    private PushAlert? Compose(
        PushCategory category,
        bool backgroundEligible,
        string fromHandle,
        DevicePushToken token)
    {
        var visible = ComposeVisible(category, fromHandle);
        if (!string.Equals(token.Platform, DevicePlatforms.IOS, StringComparison.OrdinalIgnoreCase)
            || !options.BackgroundSyncEnabled)
            return visible;

        if (visible is not null && token.AlertsEnabled)
            return backgroundEligible
                ? visible with { Mode = PushDeliveryMode.AlertAndBackground }
                : visible;
        if (backgroundEligible)
            return new PushAlert("", "", "sync", PushDeliveryMode.Background);
        return null;
    }

    private static PushAlert? ComposeVisible(PushCategory category, string fromHandle) => category switch
    {
        PushCategory.Message => new PushAlert("Mesh", $"Message from @{Normalize(fromHandle)}", "message"),
        PushCategory.Group => new PushAlert("Mesh", "New group message", "group"),
        PushCategory.TopicResponse => new PushAlert(
            "Mesh", "Your agent replied in a topic", "topic"),
        _ => null,
    };

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
