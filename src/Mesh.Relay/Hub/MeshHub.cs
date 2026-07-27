using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mesh.Relay.Backplane;
using Mesh.Relay.Observability;
using Mesh.Relay.RateLimiting;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// The Mesh transport hub. SignalR handles the connection, framing, keepalive, transport
/// fallback and client reconnection; this hub adds Mesh's device-key auth and message routing.
///
/// Auth: on connect the hub issues a fresh nonce (challenge). The client signs it with its
/// device private key and calls <see cref="Authenticate"/>. The hub verifies the signature
/// against the device public keys registered under the handle, then marks the connection ready,
/// sets presence, and drains any queued offline messages. Until then, sends are rejected.
///
/// Every inbound envelope is signature-verified against the connection's authenticated key and
/// its From is stamped by the server, so the relay always asserts the real sender.
/// </summary>
public sealed class MeshHub(
    ConnectionRegistry registry,
    MeshRouter router,
    AgentDispatchCoordinator agentDispatch,
    IRelayStore store,
    IBackplane backplane,
    IMessageRateLimiter rateLimiter,
    IHandleRatePolicyProvider ratePolicies,
    RelayMetrics metrics,
    ILogger<MeshHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var handle = Normalize(http?.Request.Query["handle"].ToString() ?? "");
        if (string.IsNullOrWhiteSpace(handle))
        {
            Context.Abort();
            return;
        }

        // Reject unknown handles up front (the client registers over REST before connecting).
        var record = await store.GetHandleAsync(handle);
        if (record is null)
        {
            Context.Abort();
            return;
        }

        var nonce = MeshCrypto.NewNonce();
        var supportsDurableDelivery = string.Equals(
            http?.Request.Query["deliveryAck"].ToString(), "1", StringComparison.Ordinal);
        registry.Add(Context.ConnectionId, handle, nonce, supportsDurableDelivery);
        metrics.ConnectionOpened();
        logger.LogInformation("hub connection opened: {Handle}", handle);
        await Clients.Caller.SendAsync(MeshHubProtocol.Challenge, nonce);
        await base.OnConnectedAsync();
    }

    /// <summary>Completes the challenge: verify the signed nonce against a registered device key.</summary>
    public async Task Authenticate(string publicKey, string signature)
    {
        var state = registry.Get(Context.ConnectionId);
        if (state?.Handle is null) { Context.Abort(); return; }

        var record = await store.GetHandleAsync(state.Handle);
        if (record is null
            || !record.DevicePublicKeys.Contains(publicKey)
            || !MeshCrypto.Verify(publicKey, state.Nonce, signature))
        {
            Context.Abort();
            return;
        }

        registry.MarkAuthenticated(Context.ConnectionId, publicKey);
        var deviceId = DeviceProtocol.DeviceId(publicKey);
        await backplane.SetPresenceAsync(state.Handle);
        await backplane.SetDevicePresenceAsync(state.Handle, deviceId);
        await Clients.Caller.SendAsync(MeshHubProtocol.Ready);
        logger.LogInformation(
            "hub authenticated: {Handle}; device={DeviceId}; durable={Durable}",
            state.Handle,
            deviceId,
            state.SupportsDurableDelivery);
        _ = DispatchAvailableAfterAuthenticationAsync(state.Handle);

        // Device-targeted fan-out must drain only on the intended device. Legacy handle-wide
        // messages are delivered afterward for backward compatibility.
        // Acknowledged clients request their first bounded batch after handling Ready. Leasing a
        // large Cosmos backlog here keeps Authenticate open and can strand the client in a retry loop.
        if (RelayInboxPolicy.UsesClientInitiatedDrain(state.SupportsDurableDelivery))
            return;

        foreach (var pending in await store.DrainInboxAsync(MeshRouter.DeviceInboxKey(state.Handle, deviceId)))
            await Clients.Caller.SendAsync(MeshHubProtocol.Receive, pending);
        foreach (var pending in await store.DrainInboxAsync(state.Handle))
            await Clients.Caller.SendAsync(MeshHubProtocol.Receive, pending);
    }

    public async Task<bool> AcknowledgeDelivery(string deliveryId, bool deviceScoped)
    {
        var connection = registry.Get(Context.ConnectionId);
        if (connection is not { Authenticated: true, SupportsDurableDelivery: true, Handle: not null, DeviceId: not null }
            || string.IsNullOrWhiteSpace(deliveryId))
            return false;

        await connection.DeliveryGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!ReferenceEquals(connection, registry.Get(Context.ConnectionId))) return false;
            var inboxKey = deviceScoped
                ? MeshRouter.DeviceInboxKey(connection.Handle, connection.DeviceId)
                : connection.Handle;
            var acknowledged = await store.AcknowledgeInboxAsync(
                inboxKey, deliveryId, Context.ConnectionAborted);
            if (acknowledged is not null)
            {
                metrics.DeliveryAcknowledged(DateTimeOffset.UtcNow - acknowledged.QueuedAt);
                await DeliverDurableInboxCoreAsync(connection, inboxKey, deviceScoped);
            }
            return acknowledged is not null;
        }
        finally
        {
            connection.DeliveryGate.Release();
        }
    }

    public async Task<int> RequestPendingDeliveries()
    {
        var connection = registry.Get(Context.ConnectionId);
        if (connection is not { Authenticated: true, SupportsDurableDelivery: true, Handle: not null, DeviceId: not null })
            return 0;

        await connection.DeliveryGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!ReferenceEquals(connection, registry.Get(Context.ConnectionId))) return 0;
            var deviceItems = await DeliverDurableInboxCoreAsync(
                connection, MeshRouter.DeviceInboxKey(connection.Handle, connection.DeviceId), deviceScoped: true);
            var handleItems = await DeliverDurableInboxCoreAsync(connection, connection.Handle, deviceScoped: false);
            return deviceItems + handleItems;
        }
        finally
        {
            connection.DeliveryGate.Release();
        }
    }

    public async Task<bool> CancelQueuedEnvelope(CancelQueuedEnvelopeRequest request)
    {
        var state = registry.Get(Context.ConnectionId);
        if (state is not { Authenticated: true, Handle: not null }
            || request is null
            || string.IsNullOrWhiteSpace(request.EnvelopeId)
            || string.IsNullOrWhiteSpace(request.TargetDeviceId))
            return false;
        if (request.TargetDeviceId.Length > 128)
            return false;
        // A device may have been unlinked after the request was queued. Sender scoping on the
        // inbox record is sufficient authorization and still lets the owner cancel that stale work.
        var deliveryId = InboxDeliveryId.Create(state.Handle, request.EnvelopeId);
        var cancelled = await store.CancelInboxAsync(
            MeshRouter.DeviceInboxKey(state.Handle, request.TargetDeviceId),
            deliveryId,
            state.Handle,
            Context.ConnectionAborted);
        if (cancelled) metrics.QueueCancelled();
        return cancelled;
    }

    private async Task<int> DeliverDurableInboxCoreAsync(
        ConnectionRegistry.ConnState connection,
        string inboxKey,
        bool deviceScoped,
        int maxItems = RelayInboxPolicy.DeliveryWindow)
    {
        if (!ReferenceEquals(connection, registry.Get(Context.ConnectionId))) return 0;
        var pending = await store.LeaseInboxAsync(
            inboxKey,
            Context.ConnectionId,
            Math.Clamp(maxItems, 1, RelayInboxPolicy.DeliveryWindow),
            ct: Context.ConnectionAborted);
        var delivered = 0;
        foreach (var item in pending)
        {
            MeshEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<MeshEnvelope>(item.Json, Json);
            }
            catch (JsonException)
            {
                await store.AcknowledgeInboxAsync(inboxKey, item.Id, Context.ConnectionAborted);
                continue;
            }
            if (envelope is null)
            {
                await store.AcknowledgeInboxAsync(inboxKey, item.Id, Context.ConnectionAborted);
                continue;
            }
            if (RelayInboxPolicy.ShouldDiscardAfterFailedDelivery(envelope.Kind, item.DeliveryAttempts))
            {
                await store.AcknowledgeInboxAsync(inboxKey, item.Id, Context.ConnectionAborted);
                logger.LogWarning(
                    "Discarded repeatedly failing snapshot request {DeliveryId} after {Attempts} attempts",
                    item.Id, item.DeliveryAttempts);
                continue;
            }
            if (item.DeliveryAttempts > 1) metrics.DeliveryRedelivered();
            var deliveryJson = JsonSerializer.Serialize(
                envelope with { RelayDeliveryId = item.Id, RelayDeviceScoped = deviceScoped }, Json);
            await Clients.Caller.SendAsync(MeshHubProtocol.Receive, deliveryJson);
            delivered++;
        }
        return delivered;
    }
    /// <summary>Receives an envelope from an authenticated connection and routes it.</summary>
    public async Task<MeshSendResult> SendEnvelope(MeshEnvelope env)
    {
        var state = registry.Get(Context.ConnectionId);
        if (state is null || !state.Authenticated || state.Handle is null || state.PublicKey is null)
            return MeshSendResult.Reject("unauthenticated");

        // Verify the message signature against the connection's authenticated key.
        if (!MeshCrypto.Verify(state.PublicKey, env.Body, env.Signature ?? ""))
            return MeshSendResult.Reject("invalid_signature");

        // Reject oversized envelopes before routing: the relay persists each one as a single Cosmos
        // item (hard 2 MB), so a body over the shared cap can never be stored. Large payloads must be
        // sent as blob attachment pointers, not inlined.
        if (System.Text.Encoding.UTF8.GetByteCount(env.Body ?? string.Empty) > MessageLimits.MaxEnvelopeBodyBytes)
            return MeshSendResult.Reject("message_too_large");

        var stamped = env with { From = state.Handle, FromDevice = state.DeviceId };
        if (stamped.PushHint is not null && !PushHintProtocol.IsTopicResponse(stamped))
            return MeshSendResult.Reject("invalid_push_hint");

        var isDeviceSync = DeviceSyncKinds.IsEnvelopeKind(env.Kind);
        if (!isDeviceSync
            && env.Kind?.StartsWith("device.sync.", StringComparison.OrdinalIgnoreCase) == true)
            return MeshSendResult.Reject("sync_kind_unknown");

        if (isDeviceSync)
        {
            if (Normalize(env.To) != state.Handle)
                return MeshSendResult.Reject("sync_same_handle_required");
            if (string.IsNullOrWhiteSpace(state.DeviceId))
                return MeshSendResult.Reject("unauthenticated");
            if (string.IsNullOrWhiteSpace(env.ToDevice))
                return MeshSendResult.Reject("sync_target_required");
            if (string.Equals(env.ToDevice, state.DeviceId, StringComparison.Ordinal))
                return MeshSendResult.Reject("sync_self_target");

            var registration = await store.GetHandleAsync(state.Handle, Context.ConnectionAborted);
            var targetKnown = registration?.DevicePublicKeys.Any(publicKey =>
                string.Equals(
                    DeviceProtocol.DeviceId(publicKey),
                    env.ToDevice,
                    StringComparison.Ordinal)) == true;
            if (!targetKnown)
                return MeshSendResult.Reject("sync_target_unknown");

            var syncEnvelope = stamped;
            var syncRoute = await router.RouteToDeviceAsync(syncEnvelope);
            if (syncRoute.NewlyQueued) metrics.QueueEnqueued();
            metrics.MessageRouted();
            return syncRoute.Queued
                ? MeshSendResult.Queued(syncRoute.DeliveryId)
                : MeshSendResult.Delivered(syncRoute.DeliveryId);
        }

        var (decision, policy) = await rateLimiter.TryAcquireAsync(
            state.Handle, MessageRateBucket.Direct, Context.ConnectionAborted);
        if (!policy.Enabled)
        {
            metrics.RateLimitRejected();
            logger.LogWarning("message disabled by policy: {Handle}", state.Handle);
            return MeshSendResult.Reject("disabled");
        }
        if (!decision.Allowed)
        {
            metrics.RateLimitRejected();
            logger.LogWarning("message rate limited: {Handle}", state.Handle);
            return MeshSendResult.Reject("rate_limited", decision.RetryAfterMs);
        }

        if (AgentDispatchProtocol.IsAtomicRequest(stamped.Kind))
        {
            var result = await agentDispatch.RouteRequestAsync(stamped, Context.ConnectionAborted);
            if (result.Accepted) metrics.MessageRouted();
            return result;
        }

        if (AgentDispatchProtocol.IsAtomicResponse(stamped.Kind))
        {
            if (!string.IsNullOrWhiteSpace(stamped.ToDevice)
                || !await agentDispatch.CompleteResponseAsync(
                    stamped,
                    state.DeviceId ?? "",
                    Context.ConnectionAborted))
                return MeshSendResult.Reject("invalid_agent_dispatch_response");

            var responseRoute = await router.RouteAsync(stamped with { AgentDispatchToken = null });
            if (responseRoute.NewlyQueued) metrics.QueueEnqueued();
            metrics.MessageRouted();
            return MeshSendResult.Ok();
        }

        if (stamped.Kind == MeshKinds.RemoteAgentRequest)
        {
            if (Normalize(stamped.To) != state.Handle)
                return MeshSendResult.Reject("remote_agent_same_handle_required");
            if (string.IsNullOrWhiteSpace(stamped.ToDevice))
                return MeshSendResult.Reject("home_device_required");

            var registration = await store.GetHandleAsync(state.Handle, Context.ConnectionAborted);
            var platform = registration?.DevicePlatforms.GetValueOrDefault(stamped.ToDevice);
            var remoteAgentEnabled =
                registration?.DeviceRemoteAgentEnabled.GetValueOrDefault(stamped.ToDevice) == true;
            if (!DevicePlatforms.IsDesktop(platform) || !remoteAgentEnabled)
                return MeshSendResult.Reject("home_device_not_eligible");

            var owner = await backplane.GetInstanceForDeviceAsync(
                state.Handle, stamped.ToDevice, Context.ConnectionAborted);
            if (owner is null)
                return MeshSendResult.Reject("home_device_offline");

            var delivered = await router.RouteToOnlineDeviceAsync(
                stamped, Context.ConnectionId, Context.ConnectionAborted);
            if (!delivered)
                return MeshSendResult.Reject("home_device_offline");

            metrics.MessageRouted();
            return MeshSendResult.Ok();
        }

        // Usage attestation note: a ServiceRequest envelope carries the serviceId inside its
        // end-to-end encrypted body (ServiceProtocol-framed), so the relay cannot observe which
        // service was invoked while routing here. Attested usage for reputation is therefore recorded
        // out-of-band via the signed POST /capabilities/{serviceId}/used endpoint the consumer calls
        // after a successful invocation. A future version can record it here once the serviceId is
        // exposed in a cleartext routing header. Routing itself is unchanged for every envelope kind.

        // When a device sends to its own handle (remote-to-desktop), exclude the sender's own
        // connection so the message reaches the owner's OTHER devices rather than echoing back.
        var exclude = Normalize(stamped.To) == state.Handle ? Context.ConnectionId : null;
        var route = await router.RouteAsync(stamped, exclude);
        if (route.NewlyQueued) metrics.QueueEnqueued();
        metrics.MessageRouted();
        return route.Queued
            ? MeshSendResult.Queued(route.DeliveryId)
            : MeshSendResult.Delivered(route.DeliveryId);
    }

    /// <summary>
    /// Routes one opaque ciphertext to a transient recipient list. The relay never inspects the
    /// encrypted dispatch metadata and never creates a durable group or membership record.
    /// </summary>
    public async Task<MeshSendResult> SendFanout(MeshFanoutRequest request)
    {
        var state = registry.Get(Context.ConnectionId);
        if (state is null || !state.Authenticated || state.Handle is null || state.PublicKey is null)
            return MeshSendResult.Reject("unauthenticated");
        if (request is null
            || string.IsNullOrWhiteSpace(request.Id)
            || string.IsNullOrWhiteSpace(request.Body)
            || string.IsNullOrWhiteSpace(request.Signature)
            || request.Recipients is null)
            return MeshSendResult.Reject("invalid_fanout");
        if (request.Recipients.Count > FanoutProtocol.MaxRecipients)
            return MeshSendResult.Reject("too_many_recipients");
        if (!MeshCrypto.Verify(state.PublicKey, request.Body, request.Signature))
            return MeshSendResult.Reject("invalid_signature");

        // Same 2 MB envelope ceiling applies to the fan-out body stored per recipient inbox.
        if (System.Text.Encoding.UTF8.GetByteCount(request.Body) > MessageLimits.MaxEnvelopeBodyBytes)
            return MeshSendResult.Reject("message_too_large");

        var recipients = new List<string>(request.Recipients.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in request.Recipients)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return MeshSendResult.Reject("invalid_fanout");
            var normalized = Normalize(raw);
            if (normalized.Length == 0)
                return MeshSendResult.Reject("invalid_fanout");
            if (seen.Add(normalized)) recipients.Add(normalized);
        }
        if (recipients.Count == 0)
            return MeshSendResult.Reject("invalid_fanout");

        var policy = await ratePolicies.GetPolicyAsync(state.Handle, Context.ConnectionAborted);
        if (!policy.Enabled)
        {
            metrics.RateLimitRejected();
            return MeshSendResult.Reject("disabled");
        }
        var maxRecipients = Math.Min(FanoutProtocol.MaxRecipients, Math.Max(1, policy.MaxFanoutRecipients));
        if (recipients.Count > maxRecipients)
            return MeshSendResult.Reject("too_many_recipients");

        // One accepted fan-out is one logical group message, regardless of recipient count.
        var (decision, effectivePolicy) = await rateLimiter.TryAcquireAsync(
            state.Handle, MessageRateBucket.Group, Context.ConnectionAborted);
        if (!effectivePolicy.Enabled)
        {
            metrics.RateLimitRejected();
            return MeshSendResult.Reject("disabled");
        }
        if (!decision.Allowed)
        {
            metrics.RateLimitRejected();
            logger.LogWarning("fan-out rate limited: {Handle}", state.Handle);
            return MeshSendResult.Reject("rate_limited", decision.RetryAfterMs);
        }

        var registrations = await Task.WhenAll(
            recipients.Select(handle => store.GetHandleAsync(handle)));
        if (registrations.Any(record => record is null || record.DevicePublicKeys.Count == 0))
            return MeshSendResult.Reject("invalid_recipient");

        var targets = registrations
            .SelectMany(record => record!.DevicePublicKeys
                .Select(publicKey => (record.Handle, DeviceId: DeviceProtocol.DeviceId(publicKey))))
            .Where(target => !(target.Handle == state.Handle && target.DeviceId == state.DeviceId))
            .Distinct()
            .ToList();

        var sentAt = request.SentAt == default ? DateTimeOffset.UtcNow : request.SentAt;
        var tasks = targets.Select(target =>
        {
            var envelope = new MeshEnvelope(
                request.Id,
                state.Handle,
                target.Handle,
                MeshKinds.Fanout,
                request.Body,
                request.Signature,
                sentAt,
                state.DeviceId,
                target.DeviceId);
            return router.RouteToDeviceAsync(envelope);
        });

        var routes = await Task.WhenAll(tasks);
        metrics.QueueEnqueued(routes.Count(route => route.NewlyQueued));
        metrics.MessageRouted(targets.Count);
        return MeshSendResult.Ok(recipients.Count);
    }

    private async Task DispatchAvailableAfterAuthenticationAsync(string handle)
    {
        try
        {
            await agentDispatch.DispatchAvailableAsync(handle);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent dispatch after authentication failed: {Handle}", handle);
        }
    }

    private async Task ReleaseDeliveryLeasesAfterDisconnectAsync(ConnectionRegistry.ConnState connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await store.ReleaseInboxLeasesAsync(
                MeshRouter.DeviceInboxKey(connection.Handle!, connection.DeviceId!),
                Context.ConnectionId,
                timeout.Token);
            await store.ReleaseInboxLeasesAsync(
                connection.Handle!,
                Context.ConnectionId,
                timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Timed out releasing inbox leases after disconnect: {Handle}", connection.Handle);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release inbox leases after disconnect: {Handle}", connection.Handle);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Only count a close for a connection we counted on open (present in the registry).
        var connection = registry.Get(Context.ConnectionId);
        var counted = connection is not null;
        var handle = registry.Remove(Context.ConnectionId);
        if (connection is { Authenticated: true, SupportsDurableDelivery: true, Handle: not null, DeviceId: not null })
            await ReleaseDeliveryLeasesAfterDisconnectAsync(connection);
        if (counted)
        {
            metrics.ConnectionClosed();
            logger.LogInformation(
                "hub connection closed: {Handle}; authenticated={Authenticated}; error={Error}",
                handle ?? connection?.Handle ?? "unknown",
                connection?.Authenticated == true,
                exception?.Message ?? "none");
        }
        if (connection is { Authenticated: true, Handle: not null, DeviceId: not null }
            && registry.ConnectionsForDevice(connection.Handle, connection.DeviceId).Count == 0)
            await backplane.ClearDevicePresenceAsync(connection.Handle, connection.DeviceId);
        if (handle is not null)
            await backplane.ClearPresenceAsync(handle); // only when it was the last local connection
        await base.OnDisconnectedAsync(exception);
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
