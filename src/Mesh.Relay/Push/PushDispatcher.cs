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

/// <summary>
/// A platform push transport. In the online-only relay a push is ONLY ever a contentless wake:
/// the native payload carries no sender, event, body or frame id, just enough for the device to
/// know it should reconnect and pull. Custody stays with the sender until an online socket receives
/// the frame, so a wake never counts as delivery.
/// </summary>
public interface IPushSender
{
    string Platform { get; }

    /// <summary>Sends a single contentless wake to one device token.</summary>
    Task<PushSendResult> SendWakeAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// Emits contentless wakes to the offline devices of a recipient so they reconnect and pull. The
/// wake never carries sender identity, message content, an event id or a frame id; the only signal
/// is "sync". Wakes are throttled per device via the store's ephemeral push-throttle metadata, and
/// an invalid token is pruned from the push-token directory. The relay never treats a wake as
/// delivery, so the send result the caller returns to the submitter remains not_online.
/// </summary>
public sealed class PushDispatcher(
    IRelayStore store,
    IEnumerable<IPushSender> senders,
    ILogger<PushDispatcher> logger)
{
    private static readonly TimeSpan WakeMinimumInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan WakeWindow = TimeSpan.FromHours(1);
    private const int MaxWakesPerWindow = 3;

    private readonly Dictionary<string, IPushSender> byPlatform =
        senders.ToDictionary(s => s.Platform, StringComparer.OrdinalIgnoreCase);

    public bool Enabled => byPlatform.Count > 0;

    /// <summary>
    /// Fire-and-forget: wake the given offline device (or every offline-tokened device of the handle
    /// when <paramref name="deviceId"/> is null) with a contentless sync signal.
    /// </summary>
    public void QueueWake(string toHandle, string? deviceId = null)
    {
        if (!Enabled) return;
        _ = SafeWakeAsync(Normalize(toHandle), deviceId);
    }

    private async Task SafeWakeAsync(string handle, string? deviceId)
    {
        try
        {
            var rec = await store.GetHandleAsync(handle).ConfigureAwait(false);
            if (rec is null || rec.DevicePushTokens.Count == 0) return;

            var targets = (deviceId is null
                    ? rec.DevicePushTokens
                    : rec.DevicePushTokens.Where(kv =>
                        string.Equals(kv.Key, deviceId, StringComparison.Ordinal)))
                .ToArray();

            foreach (var (targetDeviceId, token) in targets)
                await TryWakeAsync(handle, targetDeviceId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push wake failed");
        }
    }

    private async Task TryWakeAsync(string handle, string deviceId, DevicePushToken token)
    {
        if (string.IsNullOrWhiteSpace(token.Token)
            || !byPlatform.TryGetValue(token.Platform, out var sender))
            return;

        var acquired = await store.TryAcquireBackgroundPushAsync(
            handle,
            deviceId,
            DateTimeOffset.UtcNow,
            WakeMinimumInterval,
            WakeWindow,
            MaxWakesPerWindow).ConfigureAwait(false);
        if (!acquired)
        {
            logger.LogDebug("wake coalesced for {Handle} device {DeviceId}", handle, deviceId);
            return;
        }

        try
        {
            var result = await sender.SendWakeAsync(token.Token).ConfigureAwait(false);
            if (result.Status == PushSendStatus.Sent)
            {
                logger.LogInformation("wake sent to {Handle} (platform {Platform})", handle, token.Platform);
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
                "wake rejected for {Handle} (platform {Platform}, status {Status}): {Reason}",
                handle, token.Platform, result.StatusCode, result.Reason ?? "unknown");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "wake send failed (platform {Platform})", token.Platform);
        }
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
