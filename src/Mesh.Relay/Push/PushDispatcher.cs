using Mesh.Relay.Backplane;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Push;

/// <summary>The user-facing category a push alert falls into. Derived purely from the cleartext envelope Kind.</summary>
public enum PushCategory { None, Message, Group }

/// <summary>
/// A ready-to-send, metadata-only push alert. Title/Body are composed by the relay from metadata it already
/// routes on (the sender handle and group-vs-direct). No message plaintext is ever included.
/// </summary>
public sealed record PushAlert(string Title, string Body, string Category);

/// <summary>Sends a composed alert to one device token on a specific push platform (APNs / FCM).</summary>
public interface IPushSender
{
    /// <summary>The device platform this sender handles: <c>ios</c> or <c>android</c> (see <see cref="DevicePlatforms"/>).</summary>
    string Platform { get; }

    /// <summary>Delivers <paramref name="alert"/> to <paramref name="token"/>. Should not throw for a single bad token.</summary>
    Task SendAsync(string token, PushAlert alert, CancellationToken ct = default);
}

/// <summary>
/// Composes and dispatches "Option 1" push alerts when a message is queued for an offline recipient.
///
/// Privacy: the relay only ever sees the cleartext envelope Kind and the From/To handles, never message
/// bodies (they are end-to-end encrypted). So the two alerts it can honestly compose are:
///   - direct message -> "Message from @sender"
///   - group message  -> "New group message" (the relay never sees the group name; it is E2EE)
/// Everything else (sync, receipts, topic-internal, control) produces no push.
///
/// The alert is dispatched fire-and-forget from the routing path via <see cref="NotifyOffline"/>, so it never
/// adds latency to (or throws on) message delivery. When no push backend is configured, every call is a no-op.
/// </summary>
public sealed class PushDispatcher(
    IRelayStore store,
    IBackplane backplane,
    IEnumerable<IPushSender> senders,
    ILogger<PushDispatcher> logger)
{
    private readonly Dictionary<string, IPushSender> byPlatform =
        senders.ToDictionary(s => s.Platform, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when at least one push backend (APNs/FCM) is configured; otherwise every call is a no-op.</summary>
    public bool Enabled => byPlatform.Count > 0;

    /// <summary>Maps a cleartext envelope Kind to a user-facing push category (or None to suppress the push).</summary>
    public static PushCategory Classify(string kind) => kind switch
    {
        MeshKinds.Fanout or MeshKinds.GroupMessage => PushCategory.Group,
        MeshKinds.Chat or MeshKinds.DirectMessage or MeshKinds.AgentRequest
            or MeshKinds.AgentResponse or MeshKinds.ServiceRequest or MeshKinds.ServiceResponse
            => PushCategory.Message,
        _ => PushCategory.None,
    };

    /// <summary>
    /// Fire-and-forget wake for an offline recipient. Safe to call on the hot routing path: it returns
    /// immediately and never throws. Does nothing when push is unconfigured or the Kind is not notifiable.
    /// </summary>
    /// <param name="toHandle">Recipient handle (normalized or not).</param>
    /// <param name="deviceId">When non-null, push only this device (device-targeted route, e.g. group fan-out).</param>
    /// <param name="env">The envelope being queued; only its Kind and From are read.</param>
    public void NotifyOffline(string toHandle, string? deviceId, MeshEnvelope env)
    {
        if (!Enabled) return;
        if (Classify(env.Kind) == PushCategory.None) return;
        _ = SafeSendAsync(toHandle, deviceId, env.Kind, env.From);
    }

    /// <summary>
    /// Fire-and-forget wake for the recipient's OFFLINE devices after a notifiable message was
    /// delivered live to at least one of their other (online) devices. Registered push tokens whose
    /// device is currently connected on any instance are skipped; the rest are woken, so a phone is
    /// notified even while a desktop is open. Safe on the hot routing path: returns immediately and
    /// never throws. Never enqueues; offline siblings receive the content via device sync on reconnect.
    /// </summary>
    public void NotifyOfflineSiblings(string toHandle, MeshEnvelope env)
    {
        if (!Enabled) return;
        if (Classify(env.Kind) == PushCategory.None) return;
        _ = WakeOfflineSiblingsAsync(toHandle, env.Kind, env.From);
    }

    private async Task SafeSendAsync(string toHandle, string? deviceId, string kind, string from)
    {
        try
        {
            var alert = Compose(Classify(kind), from);
            if (alert is null) return;

            var handle = Normalize(toHandle);
            var rec = await store.GetHandleAsync(handle).ConfigureAwait(false);
            if (rec is null || rec.DevicePushTokens.Count == 0) return;

            var targets = deviceId is null
                ? rec.DevicePushTokens
                : rec.DevicePushTokens.Where(kv => string.Equals(kv.Key, deviceId, StringComparison.Ordinal));

            foreach (var (_, tok) in targets)
                await TrySendAsync(handle, tok, alert).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push dispatch failed");
        }
    }

    /// <summary>
    /// Pushes the recipient's registered tokens whose device is NOT currently connected on any
    /// instance (offline = registered push-token devices minus live device presence). Presence is
    /// read from the backplane, so this is correct for both a single node and multiple replicas.
    /// Exposed (and self-guarded so it never throws) so the router can fire it, and so the offline-set
    /// computation is unit testable.
    /// </summary>
    public async Task WakeOfflineSiblingsAsync(string toHandle, string kind, string from)
    {
        try
        {
            var alert = Compose(Classify(kind), from);
            if (alert is null) return;

            var handle = Normalize(toHandle);
            var rec = await store.GetHandleAsync(handle).ConfigureAwait(false);
            if (rec is null || rec.DevicePushTokens.Count == 0) return;

            foreach (var (deviceId, tok) in rec.DevicePushTokens)
            {
                var owner = await backplane.GetInstanceForDeviceAsync(handle, deviceId).ConfigureAwait(false);
                if (owner is not null) continue; // device is connected somewhere; no wake needed
                await TrySendAsync(handle, tok, alert).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push dispatch failed");
        }
    }

    // Sends one composed alert to one device token. Logs a delivered wake at info (so success is
    // observable in the relay logs) and swallows a single bad token as a warning so one failure
    // cannot abort the rest of the batch.
    private async Task TrySendAsync(string handle, DevicePushToken tok, PushAlert alert)
    {
        if (string.IsNullOrWhiteSpace(tok.Token)) return;
        if (!byPlatform.TryGetValue(tok.Platform, out var sender)) return;
        try
        {
            await sender.SendAsync(tok.Token, alert).ConfigureAwait(false);
            logger.LogInformation("push sent to {Handle} (platform {Platform})", handle, tok.Platform);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push send failed (platform {Platform})", tok.Platform);
        }
    }

    private static PushAlert? Compose(PushCategory category, string fromHandle) => category switch
    {
        PushCategory.Message => new PushAlert("Mesh", $"Message from @{Normalize(fromHandle)}", "message"),
        PushCategory.Group => new PushAlert("Mesh", "New group message", "group"),
        _ => null,
    };

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
