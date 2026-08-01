using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace Mesh.Relay.Backplane;

/// <summary>
/// Redis (StackExchange.Redis) backed backplane that lights up multi-replica WebSocket
/// routing for the online-only relay. Presence is stored as short-lived string keys with a
/// TTL, and each instance subscribes to its own pub/sub channel so other instances can forward
/// an in-flight opaque frame to the socket that actually lives here.
///
/// Nothing durable is written: only ephemeral presence keys (TTL) and transient pub/sub frames.
///
/// Key/channel naming scheme:
///   presence key : mesh:presence:{handle}                 (value = owning InstanceId, TTL ~30s)
///   device key   : mesh:presence-device:{handle}:{device} (value = owning InstanceId, TTL ~30s)
///   routing chan : mesh:inst:{InstanceId}                 (per-instance pub/sub channel)
///   ack chan     : mesh:ack:{InstanceId}                  (per-instance delivery-ack channel)
/// </summary>
public sealed class RedisBackplane : IBackplane
{
    private const string PresenceKeyPrefix = "mesh:presence:";
    private const string DevicePresenceKeyPrefix = "mesh:presence-device:";
    private const string InstanceChannelPrefix = "mesh:inst:";
    private const string AckChannelPrefix = "mesh:ack:";
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FanoutReceiptTimeout = TimeSpan.FromSeconds(5);

    // Lua: delete the presence key only if it still points at this instance, avoiding
    // clobbering presence that a different instance has since taken over.
    private const string ClearIfOwnerScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly string connectionString;
    private readonly SemaphoreSlim connectGate = new(1, 1);

    private ConnectionMultiplexer? multiplexer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BackplaneDeliveryReceipt>> pendingAcks = new();
    private Func<string, string, Task<BackplaneDeliveryReceipt>>? deliverLocal;

    public RedisBackplane(string connectionString)
    {
        this.connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public string InstanceId { get; } = Guid.NewGuid().ToString("n")[..8];

    public async Task StartAsync(
        Func<string, string, Task<BackplaneDeliveryReceipt>> deliverLocal, CancellationToken ct = default)
    {
        this.deliverLocal = deliverLocal ?? throw new ArgumentNullException(nameof(deliverLocal));

        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = RedisChannel.Literal(InstanceChannelPrefix + InstanceId);
        var ackChannel = RedisChannel.Literal(AckChannelPrefix + InstanceId);

        await mux.GetSubscriber().SubscribeAsync(channel, OnInstanceMessage).ConfigureAwait(false);
        await mux.GetSubscriber().SubscribeAsync(ackChannel, OnAckMessage).ConfigureAwait(false);
    }

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
    /// Publishes an in-flight opaque frame to the instance that owns the handle so it can deliver it
    /// to the live socket. Waits for a directed acknowledgement so the caller learns the true outcome.
    /// </summary>
    public async Task<BackplaneDeliveryReceipt> PublishToOwnerAsync(
        string instanceId, string toHandle, string deliveryJson, CancellationToken ct = default)
    {
        var mux = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = RedisChannel.Literal(InstanceChannelPrefix + instanceId);

        var correlationId = Guid.NewGuid().ToString("n");
        var completion = new TaskCompletionSource<BackplaneDeliveryReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingAcks[correlationId] = completion;
        var payload = JsonSerializer.Serialize(
            new RoutedFrame(toHandle, deliveryJson, InstanceId, correlationId));

        try
        {
            var receivers = await mux.GetSubscriber()
                .PublishAsync(channel, payload)
                .ConfigureAwait(false);
            if (receivers == 0)
                return BackplaneDeliveryReceipt.NotDelivered;
            return await completion.Task
                .WaitAsync(FanoutReceiptTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new BackplaneDeliveryReceipt(BackplaneDeliveryOutcome.Uncertain);
        }
        finally
        {
            pendingAcks.TryRemove(correlationId, out _);
        }
    }

    private void OnInstanceMessage(RedisChannel channel, RedisValue message)
    {
        var handler = deliverLocal;
        if (handler is null || message.IsNullOrEmpty)
            return;

        RoutedFrame? routed;
        try
        {
            routed = JsonSerializer.Deserialize<RoutedFrame>((string)message!);
        }
        catch (JsonException)
        {
            return;
        }

        if (routed is null || routed.To is null || routed.Json is null)
            return;

        _ = DeliverAndAcknowledgeAsync(handler, routed);
    }

    private async Task DeliverAndAcknowledgeAsync(
        Func<string, string, Task<BackplaneDeliveryReceipt>> handler,
        RoutedFrame routed)
    {
        var receipt = BackplaneDeliveryReceipt.NotDelivered;
        var uncertain = false;
        try
        {
            receipt = await handler(routed.To, routed.Json).ConfigureAwait(false);
        }
        catch
        {
            uncertain = true;
        }

        if (string.IsNullOrWhiteSpace(routed.ReplyInstance)
            || string.IsNullOrWhiteSpace(routed.CorrelationId))
            return;
        try
        {
            var mux = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
            var channel = RedisChannel.Literal(AckChannelPrefix + routed.ReplyInstance);
            await mux.GetSubscriber()
                .PublishAsync(channel, JsonSerializer.Serialize(
                    new FanoutReceipt(
                        routed.CorrelationId,
                        receipt.Outcome == BackplaneDeliveryOutcome.Delivered,
                        uncertain)))
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void OnAckMessage(RedisChannel channel, RedisValue message)
    {
        if (message.IsNullOrEmpty)
            return;
        try
        {
            var ack = JsonSerializer.Deserialize<FanoutReceipt>((string)message!);
            if (ack is not null
                && pendingAcks.TryGetValue(ack.CorrelationId, out var completion))
                completion.TrySetResult(new BackplaneDeliveryReceipt(
                    ack.Delivered
                        ? BackplaneDeliveryOutcome.Delivered
                        : ack.Uncertain
                            ? BackplaneDeliveryOutcome.Uncertain
                            : BackplaneDeliveryOutcome.NotDelivered));
        }
        catch (JsonException)
        {
        }
    }

    private async Task<ConnectionMultiplexer> EnsureConnectedAsync(CancellationToken ct)
    {
        var existing = multiplexer;
        if (existing is not null && existing.IsConnected)
            return existing;

        await connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            multiplexer ??= await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
            return multiplexer;
        }
        finally
        {
            connectGate.Release();
        }
    }

    private static string DevicePresenceKey(string handle, string deviceId)
        => $"{DevicePresenceKeyPrefix}{handle}:{deviceId}";

    /// <summary>Tiny transient wire payload carried over the per-instance routing channel.</summary>
    private sealed record RoutedFrame(
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string To,
        [property: System.Text.Json.Serialization.JsonPropertyName("json")] string Json,
        [property: System.Text.Json.Serialization.JsonPropertyName("replyInstance")] string? ReplyInstance = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("correlationId")] string? CorrelationId = null);

    private sealed record FanoutReceipt(
        [property: System.Text.Json.Serialization.JsonPropertyName("correlationId")] string CorrelationId,
        [property: System.Text.Json.Serialization.JsonPropertyName("delivered")] bool Delivered,
        [property: System.Text.Json.Serialization.JsonPropertyName("uncertain")] bool Uncertain = false);
}
