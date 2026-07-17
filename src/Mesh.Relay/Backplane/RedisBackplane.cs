using System.Text.Json;
using StackExchange.Redis;

namespace Mesh.Relay.Backplane;

/// <summary>
/// Redis (StackExchange.Redis) backed backplane that lights up multi-replica WebSocket
/// routing. Presence is stored as short-lived string keys with a TTL, and each instance
/// subscribes to its own pub/sub channel so other instances can forward messages to the
/// socket that actually lives here.
///
/// Key/channel naming scheme:
///   presence key : mesh:presence:{handle}  (value = owning InstanceId, TTL ~30s)
///   routing chan : mesh:inst:{InstanceId}   (per-instance pub/sub channel)
/// </summary>
public sealed class RedisBackplane : IBackplane
{
    private const string PresenceKeyPrefix = "mesh:presence:";
    private const string DevicePresenceKeyPrefix = "mesh:presence-device:";
    private const string InstanceChannelPrefix = "mesh:inst:";
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(30);

    // Lua: delete the presence key only if it still points at this instance, avoiding
    // clobbering presence that a different instance has since taken over.
    private const string ClearIfOwnerScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly string connectionString;
    private readonly SemaphoreSlim connectGate = new(1, 1);

    private ConnectionMultiplexer? multiplexer;
    private Func<string, string, Task>? deliverLocal;

    /// <summary>Creates a Redis backplane bound to the given StackExchange.Redis connection string.</summary>
    /// <param name="connectionString">A StackExchange.Redis connection string (host:port, options, etc.).</param>
    public RedisBackplane(string connectionString)
    {
        this.connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>This relay instance's stable id for the lifetime of the process.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("n")[..8];

    /// <summary>
    /// Connects (if needed) and subscribes to this instance's routing channel. Each inbound
    /// message carries a small JSON payload of { "to": ..., "json": ... }; on receipt we invoke
    /// the local delivery handler so the message reaches the live socket on this instance.
    /// </summary>
    public async Task StartAsync(Func<string, string, Task> deliverLocal, CancellationToken ct = default)
    {
        this.deliverLocal = deliverLocal ?? throw new ArgumentNullException(nameof(deliverLocal));

        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = RedisChannel.Literal(InstanceChannelPrefix + InstanceId);

        await mux.GetSubscriber().SubscribeAsync(channel, OnInstanceMessage).ConfigureAwait(false);
    }

    /// <summary>Records that <paramref name="handle"/> is connected on this instance, renewing the TTL.</summary>
    public async Task SetPresenceAsync(string handle, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await mux.GetDatabase()
            .StringSetAsync(PresenceKeyPrefix + handle, InstanceId, PresenceTtl)
            .ConfigureAwait(false);
    }

    public async Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await mux.GetDatabase()
            .StringSetAsync(DevicePresenceKey(handle, deviceId), InstanceId, PresenceTtl)
            .ConfigureAwait(false);
    }

    /// <summary>Clears presence for a handle, but only if this instance still owns it.</summary>
    public async Task ClearPresenceAsync(string handle, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await mux.GetDatabase()
            .ScriptEvaluateAsync(
                ClearIfOwnerScript,
                new RedisKey[] { PresenceKeyPrefix + handle },
                new RedisValue[] { InstanceId })
            .ConfigureAwait(false);
    }

    public async Task ClearDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await mux.GetDatabase()
            .ScriptEvaluateAsync(
                ClearIfOwnerScript,
                new RedisKey[] { DevicePresenceKey(handle, deviceId) },
                new RedisValue[] { InstanceId })
            .ConfigureAwait(false);
    }

    /// <summary>Returns the instance id currently holding the handle's socket, or null if absent/expired.</summary>
    public async Task<string?> GetInstanceForAsync(string handle, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var value = await mux.GetDatabase()
            .StringGetAsync(PresenceKeyPrefix + handle)
            .ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public async Task<string?> GetInstanceForDeviceAsync(
        string handle, string deviceId, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var value = await mux.GetDatabase()
            .StringGetAsync(DevicePresenceKey(handle, deviceId))
            .ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    /// <summary>
    /// Publishes an envelope to the instance that owns the handle so it can deliver it to the
    /// live socket. Returns true when at least one subscriber received the publish.
    /// </summary>
    public async Task<bool> PublishToOwnerAsync(string instanceId, string toHandle, string envelopeJson, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = RedisChannel.Literal(InstanceChannelPrefix + instanceId);
        var payload = JsonSerializer.Serialize(new RoutedMessage(toHandle, envelopeJson));

        var receivers = await mux.GetSubscriber()
            .PublishAsync(channel, payload)
            .ConfigureAwait(false);

        return receivers > 0;
    }

    private void OnInstanceMessage(RedisChannel channel, RedisValue message)
    {
        var handler = deliverLocal;
        if (handler is null || message.IsNullOrEmpty)
        {
            return;
        }

        RoutedMessage? routed;
        try
        {
            routed = JsonSerializer.Deserialize<RoutedMessage>((string)message!);
        }
        catch (JsonException)
        {
            return;
        }

        if (routed is null || routed.To is null || routed.Json is null)
        {
            return;
        }

        // Fire and forget: the subscriber callback is synchronous, so hand delivery off to the handler.
        _ = handler(routed.To, routed.Json);
    }

    private async Task<ConnectionMultiplexer> EnsureConnectedAsync(CancellationToken ct)
    {
        var existing = multiplexer;
        if (existing is not null && existing.IsConnected)
        {
            return existing;
        }

        await connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (multiplexer is null)
            {
                multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
            }

            return multiplexer;
        }
        finally
        {
            connectGate.Release();
        }
    }

    private static string DevicePresenceKey(string handle, string deviceId)
        => $"{DevicePresenceKeyPrefix}{handle}:{deviceId}";

    /// <summary>Tiny wire payload carried over the per-instance routing channel.</summary>
    private sealed record RoutedMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string To,
        [property: System.Text.Json.Serialization.JsonPropertyName("json")] string Json);
}
