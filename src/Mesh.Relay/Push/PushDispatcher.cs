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

    private async Task SafeSendAsync(string toHandle, string? deviceId, string kind, string from)
    {
        try
        {
            var alert = Compose(Classify(kind), from);
            if (alert is null) return;

            var rec = await store.GetHandleAsync(toHandle).ConfigureAwait(false);
            if (rec is null || rec.DevicePushTokens.Count == 0) return;

            var targets = deviceId is null
                ? rec.DevicePushTokens
                : rec.DevicePushTokens.Where(kv => string.Equals(kv.Key, deviceId, StringComparison.Ordinal));

            foreach (var (_, tok) in targets)
            {
                if (string.IsNullOrWhiteSpace(tok.Token)) continue;
                if (!byPlatform.TryGetValue(tok.Platform, out var sender)) continue;
                try
                {
                    await sender.SendAsync(tok.Token, alert).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "push send failed (platform {Platform})", tok.Platform);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "push dispatch failed");
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
