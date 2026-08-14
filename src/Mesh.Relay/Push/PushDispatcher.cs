using System.Collections.Concurrent;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Push;

public enum PushSendStatus { Sent, Rejected, InvalidToken }

public sealed record PushSendResult(PushSendStatus Status, int StatusCode = 0, string? Reason = null)
{
    public static PushSendResult Sent() => new(PushSendStatus.Sent);
    public static PushSendResult Rejected(int statusCode, string? reason = null)
        => new(PushSendStatus.Rejected, statusCode, reason);
    public static PushSendResult InvalidToken(int statusCode, string? reason = null)
        => new(PushSendStatus.InvalidToken, statusCode, reason);
}

public interface IPushSender
{
    string Platform { get; }
    Task<PushSendResult> SendWakeAsync(
        string token,
        PushWakeMode mode,
        string wakeId,
        CancellationToken ct = default);
}

public enum PushDispatchOutcome { Sent, Coalesced, Throttled, NoTarget, Failed }

/// <summary>Sends contentless sync wakes with stable wake-ID deduplication and per-device throttles.</summary>
public sealed class PushDispatcher(
    IRelayStore store,
    IEnumerable<IPushSender> senders,
    ILogger<PushDispatcher> logger)
{
    internal static readonly TimeSpan VisibleWakeMinimumInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SilentWakeMinimumInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WakeWindow = TimeSpan.FromHours(1);
    internal const int MaxVisibleWakesPerWindow = 60;
    internal const int MaxSilentWakesPerWindow = 12;
    private readonly ConcurrentDictionary<string, DateTimeOffset> recentWakeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPushSender> byPlatform =
        senders.ToDictionary(sender => sender.Platform, StringComparer.OrdinalIgnoreCase);

    public bool Enabled => byPlatform.Count > 0;


    public async Task<PushDispatchOutcome> RequestWakeAsync(
        string toHandle,
        string? deviceId,
        string wakeId,
        bool notificationWorthy,
        CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(wakeId)) return PushDispatchOutcome.NoTarget;
        var handle = Normalize(toHandle);
        var dedupKey = $"{handle}:{deviceId ?? "*"}:{wakeId}";
        var now = DateTimeOffset.UtcNow;
        PruneWakeIds(now);
        if (!recentWakeIds.TryAdd(dedupKey, now)) return PushDispatchOutcome.Coalesced;

        var record = await store.GetHandleAsync(handle, ct).ConfigureAwait(false);
        if (record is null || record.DevicePushTokens.Count == 0)
        {
            recentWakeIds.TryRemove(dedupKey, out _);
            return PushDispatchOutcome.NoTarget;
        }
        var targets = (deviceId is null
                ? record.DevicePushTokens
                : record.DevicePushTokens.Where(item =>
                    string.Equals(item.Key, deviceId, StringComparison.Ordinal)))
            .ToArray();
        if (targets.Length == 0)
        {
            recentWakeIds.TryRemove(dedupKey, out _);
            return PushDispatchOutcome.NoTarget;
        }

        var outcomes = new List<PushDispatchOutcome>(targets.Length);
        foreach (var (targetDeviceId, token) in targets)
        {
            outcomes.Add(await TryWakeAsync(handle, targetDeviceId, token, wakeId, notificationWorthy, ct)
                .ConfigureAwait(false));
        }

        var aggregate = outcomes.Contains(PushDispatchOutcome.Sent)
            ? PushDispatchOutcome.Sent
            : outcomes.Contains(PushDispatchOutcome.Coalesced)
                ? PushDispatchOutcome.Coalesced
                : outcomes.Contains(PushDispatchOutcome.Throttled)
                    ? PushDispatchOutcome.Throttled
                    : outcomes.Contains(PushDispatchOutcome.Failed)
                        ? PushDispatchOutcome.Failed
                        : PushDispatchOutcome.NoTarget;
        if (aggregate is PushDispatchOutcome.Throttled or PushDispatchOutcome.NoTarget or PushDispatchOutcome.Failed)
            recentWakeIds.TryRemove(dedupKey, out _);
        return aggregate;
    }

    private async Task<PushDispatchOutcome> TryWakeAsync(
        string handle,
        string deviceId,
        DevicePushToken token,
        string wakeId,
        bool notificationWorthy,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token.Token)
            || !byPlatform.TryGetValue(token.Platform, out var sender))
            return PushDispatchOutcome.NoTarget;

        var mode = notificationWorthy && token.AlertsEnabled
            ? PushWakeMode.AlertAndSync
            : PushWakeMode.SyncOnly;
        var maxCount = mode == PushWakeMode.AlertAndSync
            ? MaxVisibleWakesPerWindow
            : MaxSilentWakesPerWindow;
        var minimumInterval = mode == PushWakeMode.AlertAndSync
            ? VisibleWakeMinimumInterval
            : SilentWakeMinimumInterval;
        var acquired = await store.TryAcquireBackgroundPushAsync(
            handle,
            deviceId,
            mode,
            DateTimeOffset.UtcNow,
            minimumInterval,
            WakeWindow,
            maxCount,
            ct).ConfigureAwait(false);
        if (!acquired)
        {
            logger.LogDebug("wake throttled for {Handle} device {DeviceId}", handle, deviceId);
            return PushDispatchOutcome.Throttled;
        }

        var result = await sender.SendWakeAsync(token.Token, mode, wakeId, ct).ConfigureAwait(false);
        if (result.Status == PushSendStatus.Sent)
        {
            logger.LogInformation(
                "wake sent to {Handle} (platform {Platform}, mode {Mode})",
                handle,
                token.Platform,
                mode);
            return PushDispatchOutcome.Sent;
        }
        if (result.Status == PushSendStatus.InvalidToken)
        {
            await store.RemoveDevicePushTokenAsync(handle, deviceId, ct).ConfigureAwait(false);
            logger.LogWarning(
                "removed invalid push token for {Handle} device {DeviceId}: {Reason}",
                handle,
                deviceId,
                result.Reason ?? result.StatusCode.ToString());
            return PushDispatchOutcome.NoTarget;
        }
        logger.LogWarning(
            "wake rejected for {Handle} (platform {Platform}, status {Status}): {Reason}",
            handle,
            token.Platform,
            result.StatusCode,
            result.Reason ?? "unknown");
        return PushDispatchOutcome.Failed;
    }

    private void PruneWakeIds(DateTimeOffset now)
    {
        foreach (var item in recentWakeIds)
            if (now - item.Value >= WakeWindow) recentWakeIds.TryRemove(item.Key, out _);
    }


    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
