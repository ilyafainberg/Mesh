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
        var protocolVersion = int.TryParse(
            http?.Request.Query["protocolVersion"].ToString(),
            out var parsedProtocolVersion)
            ? parsedProtocolVersion
            : 0;
        if (string.IsNullOrWhiteSpace(handle))
        {
            Context.Abort();
            return;
        }
        if (protocolVersion != MeshProtocol.Version)
        {
            await Clients.Caller.SendAsync(
                MeshHubProtocol.Handshake,
                new HandshakeResponse(
                    MeshProtocol.Version,
                    HandshakeResult.VersionMismatch,
                    $"Relay protocol {MeshProtocol.Version} is required. Client sent {protocolVersion}."));
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
        var isBackgroundSync = string.Equals(
            http?.Request.Query["backgroundSync"].ToString(), "1", StringComparison.Ordinal);
        registry.Add(Context.ConnectionId, handle, nonce, supportsDurableDelivery, isBackgroundSync);
        metrics.ConnectionOpened();
        logger.LogInformation("hub connection opened: {Handle}", handle);
        await Clients.Caller.SendAsync(
            MeshHubProtocol.Handshake,
            new HandshakeResponse(MeshProtocol.Version, HandshakeResult.Accepted));
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
        var now = DateTimeOffset.UtcNow;
        if (!state.IsBackgroundSync)
        {
            await backplane.SetPresenceAsync(state.Handle);
            await backplane.SetDevicePresenceAsync(state.Handle, deviceId);
        }
        else
        {
            await backplane.SetTransientDeviceRouteAsync(state.Handle, deviceId);
        }
        await Clients.Caller.SendAsync(
            MeshHubProtocol.PresenceConfirmed,
            new PresenceConfirmed(
                state.Handle,
                deviceId,
                now,
                state.IsBackgroundSync ? now : now + TimeSpan.FromSeconds(30),
                backplane.InstanceId));
        logger.LogInformation(
            "hub authenticated: {Handle}; device={DeviceId}; background={Background}; durable={Durable}",
            state.Handle,
            deviceId,
            state.IsBackgroundSync,
            state.SupportsDurableDelivery);
        if (!state.IsBackgroundSync)
            _ = DispatchAvailableAfterAuthenticationAsync(state.Handle);

        // Protocol 8 clients initiate every bounded durable drain after authentication returns.
        // Authentication never scans or leases either durable store.
    }

    public async Task<QueueEnqueueResult> QueueEnqueue(QueueEnqueue request)
    {
        var authorization = await GetAuthorizedConnectionAsync();
        var connection = authorization.Connection;
        if (connection is not { Handle: not null, DeviceId: not null })
            return new QueueEnqueueResult(false, "", authorization.Revoked ? "device_revoked" : "unauthenticated");
        if (request is null
            || string.IsNullOrWhiteSpace(request.SourceDeviceId)
            || string.IsNullOrWhiteSpace(request.TargetDeviceId)
            || string.IsNullOrWhiteSpace(request.OperationId)
            || string.IsNullOrWhiteSpace(request.Payload))
            return new QueueEnqueueResult(false, "", "invalid_queue_request");
        if (!string.Equals(request.SourceDeviceId, connection.DeviceId, StringComparison.Ordinal))
            return new QueueEnqueueResult(false, "", "source_device_mismatch");
        if (string.Equals(request.TargetDeviceId, connection.DeviceId, StringComparison.Ordinal))
            return new QueueEnqueueResult(false, "", "sync_self_target");

        var registration = authorization.Registration!;
        var targetKnown = registration.DevicePublicKeys.Any(publicKey =>
            string.Equals(
                DeviceProtocol.DeviceId(publicKey),
                request.TargetDeviceId,
                StringComparison.Ordinal));
        if (!targetKnown)
            return new QueueEnqueueResult(false, "", "sync_target_unknown");

        var result = await store.EnqueueDeviceQueueAsync(
            connection.Handle,
            request,
            Context.ConnectionAborted);
        if (result.Created)
            metrics.QueueEnqueued();
        if (result.Accepted)
        {
            try
            {
                await NotifyDeviceQueueAvailableAsync(connection.Handle, request.TargetDeviceId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Device queue notification failed after durable enqueue: {Handle}; device={DeviceId}",
                    connection.Handle,
                    request.TargetDeviceId);
            }
        }
        return result;
    }

    public async Task<QueueDrainResponse> QueueDrain(QueueDrainRequest request)
    {
        var authorization = await GetAuthorizedConnectionAsync();
        var connection = authorization.Connection;
        if (connection is not { Handle: not null, DeviceId: not null })
            return new QueueDrainResponse([]);
        if (request is null
            || !string.Equals(request.TargetDeviceId, connection.DeviceId, StringComparison.Ordinal))
            return new QueueDrainResponse([]);
        return await store.DrainDeviceQueueAsync(
            connection.Handle,
            connection.DeviceId,
            Context.ConnectionId,
            Math.Clamp(request.MaxEntries, 1, DeviceQueueProtocol.DeliveryWindow),
            Context.ConnectionAborted);
    }

    public async Task<bool> QueueAck(QueueAck request)
    {
        var authorization = await GetAuthorizedConnectionAsync();
        var connection = authorization.Connection;
        if (connection is not { Handle: not null, DeviceId: not null }
            || request is null
            || string.IsNullOrWhiteSpace(request.EntryId)
            || !string.Equals(request.TargetDeviceId, connection.DeviceId, StringComparison.Ordinal))
            return false;
        return await store.AcknowledgeDeviceQueueAsync(
            connection.Handle,
            connection.DeviceId,
            request.EntryId,
            Context.ConnectionId,
            Context.ConnectionAborted);
    }

    public async Task<bool> AcknowledgeDelivery(string deliveryId, bool deviceScoped)
    {
        var authorization = await GetAuthorizedConnectionAsync();
        var connection = authorization.Connection;
        if (connection is not { SupportsDurableDelivery: true, Handle: not null, DeviceId: not null }
            || string.IsNullOrWhiteSpace(deliveryId))
            return false;

        await connection.DeliveryGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!connection.Authenticated
                || !ReferenceEquals(connection, registry.Get(Context.ConnectionId))) return false;
            var inboxKey = deviceScoped
                ? MeshRouter.DeviceInboxKey(connection.Handle, connection.DeviceId)
                : connection.Handle;
            var acknowledged = await store.AcknowledgeInboxAsync(
                inboxKey, deliveryId, Context.ConnectionId, Context.ConnectionAborted);
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
        var authorization = await GetAuthorizedConnectionAsync();
        var connection = authorization.Connection;
        if (connection is not { SupportsDurableDelivery: true, Handle: not null, DeviceId: not null })
            return 0;

        await connection.DeliveryGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!connection.Authenticated
                || !ReferenceEquals(connection, registry.Get(Context.ConnectionId))) return 0;
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
        var authorization = await GetAuthorizedConnectionAsync();
        var state = authorization.Connection;
        if (state is not { Handle: not null }
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
        if (!connection.Authenticated
            || !ReferenceEquals(connection, registry.Get(Context.ConnectionId))) return 0;
        var pending = await store.LeaseInboxAsync(
            inboxKey,
            Context.ConnectionId,
            Math.Clamp(maxItems, 1, RelayInboxPolicy.DeliveryWindow),
            includeForeground: !connection.IsBackgroundSync,
            ct: Context.ConnectionAborted);
        var delivered = 0;
        foreach (var item in pending)
        {
            if (!connection.Authenticated)
            {
                await store.ReleaseInboxLeasesAsync(
                    inboxKey, Context.ConnectionId, Context.ConnectionAborted);
                break;
            }
            MeshEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<MeshEnvelope>(item.Json, Json);
            }
            catch (JsonException)
            {
                await store.AcknowledgeInboxAsync(
                    inboxKey, item.Id, Context.ConnectionId, Context.ConnectionAborted);
                continue;
            }
            if (envelope is null)
            {
                await store.AcknowledgeInboxAsync(
                    inboxKey, item.Id, Context.ConnectionId, Context.ConnectionAborted);
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
        var authorization = await GetAuthorizedConnectionAsync();
        var state = authorization.Connection;
        if (state is null)
            return MeshSendResult.Reject(authorization.Revoked ? "device_revoked" : "unauthenticated");
        var currentRegistration = authorization.Registration!;
        var handle = state.Handle!;
        var publicKey = state.PublicKey!;

        // Verify the message signature against the connection's authenticated key.
        if (!MeshCrypto.Verify(publicKey, env.Body, env.Signature ?? ""))
            return MeshSendResult.Reject("invalid_signature");

        // Reject oversized envelopes before routing: the relay persists each one as a single Cosmos
        // item (hard 2 MB), so a body over the shared cap can never be stored. Large payloads must be
        // sent as blob attachment pointers, not inlined.
        if (System.Text.Encoding.UTF8.GetByteCount(env.Body ?? string.Empty) > MessageLimits.MaxEnvelopeBodyBytes)
            return MeshSendResult.Reject("message_too_large");

        var stamped = env with { From = handle, FromDevice = state.DeviceId };
        if (stamped.PushHint is not null && !PushHintProtocol.IsTopicResponse(stamped))
            return MeshSendResult.Reject("invalid_push_hint");

        var isDeviceSync = DeviceSyncKinds.IsEnvelopeKind(env.Kind);
        if (!isDeviceSync
            && env.Kind?.StartsWith("device.sync.", StringComparison.OrdinalIgnoreCase) == true)
            return MeshSendResult.Reject("sync_kind_unknown");
        if (isDeviceSync)
            return MeshSendResult.Reject("device_sync_use_queue");
        if (!string.IsNullOrWhiteSpace(stamped.ToDevice))
        {
            var targetHandle = Normalize(stamped.To);
            var targetRegistration = string.Equals(targetHandle, handle, StringComparison.Ordinal)
                ? currentRegistration
                : await store.GetHandleAsync(targetHandle, Context.ConnectionAborted);
            var targetKnown = targetRegistration?.DevicePublicKeys.Any(publicKey =>
                string.Equals(
                    DeviceProtocol.DeviceId(publicKey),
                    stamped.ToDevice,
                    StringComparison.Ordinal)) == true;
            if (!targetKnown)
                return MeshSendResult.Reject(isDeviceSync ? "sync_target_unknown" : "target_device_unknown");
        }

        var (decision, policy) = await rateLimiter.TryAcquireAsync(
            handle, MessageRateBucket.Direct, Context.ConnectionAborted);
        if (!policy.Enabled)
        {
            metrics.RateLimitRejected();
            logger.LogWarning("message disabled by policy: {Handle}", handle);
            return MeshSendResult.Reject("disabled");
        }
        if (!decision.Allowed)
        {
            metrics.RateLimitRejected();
            logger.LogWarning("message rate limited: {Handle}", handle);
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
            if (!string.IsNullOrWhiteSpace(stamped.ToDevice))
                return MeshSendResult.Reject("invalid_agent_dispatch_response");
            var completion = await agentDispatch.CompleteResponseAsync(
                stamped,
                state.DeviceId ?? "",
                Context.ConnectionAborted);
            if (!completion.Accepted || completion.Response is null)
                return MeshSendResult.Reject(completion.Error ?? "invalid_agent_dispatch_response");

            _ = await router.RouteEnqueuedAsync(
                completion.Response,
                new InboxEnqueueResult(
                    InboxDeliveryId.Create(completion.Response.From, completion.Response.Id),
                    Accepted: true,
                    Created: completion.Created));
            if (completion.Created) metrics.QueueEnqueued();
            metrics.MessageRouted();
            return MeshSendResult.Ok();
        }

        if (stamped.Kind == MeshKinds.RemoteAgentRequest)
        {
            if (Normalize(stamped.To) != handle)
                return MeshSendResult.Reject("remote_agent_same_handle_required");
            if (string.IsNullOrWhiteSpace(stamped.ToDevice))
                return MeshSendResult.Reject("home_device_required");

            var platform = currentRegistration.DevicePlatforms.GetValueOrDefault(stamped.ToDevice);
            var remoteAgentEnabled =
                currentRegistration.DeviceRemoteAgentEnabled.GetValueOrDefault(stamped.ToDevice);
            if (!DevicePlatforms.IsDesktop(platform) || !remoteAgentEnabled)
                return MeshSendResult.Reject("home_device_not_eligible");

            var owner = await backplane.GetInstanceForDeviceAsync(
                handle, stamped.ToDevice, Context.ConnectionAborted);
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
        var exclude = Normalize(stamped.To) == handle ? Context.ConnectionId : null;
        var routed = await RouteDurablyAsync(stamped, exclude);
        if (!routed.Admission.Accepted || routed.Route is null)
            return MeshSendResult.Reject(routed.Admission.Error ?? "inbox_admission_rejected");
        var route = routed.Route.Value;
        if (routed.Admission.Created) metrics.QueueEnqueued();
        metrics.MessageRouted();
        return route.Queued
            ? MeshSendResult.Queued(route.DeliveryId)
            : MeshSendResult.Delivered(route.DeliveryId);
    }

    public async Task<MeshSendResult> SendEphemeralEnvelope(MeshEnvelope env)
    {
        var authorization = await GetAuthorizedConnectionAsync();
        var state = authorization.Connection;
        if (state is null)
            return MeshSendResult.Reject(authorization.Revoked ? "device_revoked" : "unauthenticated");
        var registration = authorization.Registration!;
        var handle = state.Handle!;
        var publicKey = state.PublicKey!;
        if (!MeshCrypto.Verify(publicKey, env.Body, env.Signature ?? ""))
            return MeshSendResult.Reject("invalid_signature");
        if (System.Text.Encoding.UTF8.GetByteCount(env.Body ?? string.Empty) > MessageLimits.MaxEnvelopeBodyBytes)
            return MeshSendResult.Reject("message_too_large");
        if (!string.Equals(env.Kind, MeshKinds.TopicRunUpdate, StringComparison.Ordinal)
            || Normalize(env.To) != handle
            || string.IsNullOrWhiteSpace(state.DeviceId)
            || string.IsNullOrWhiteSpace(env.ToDevice)
            || string.Equals(env.ToDevice, state.DeviceId, StringComparison.Ordinal)
            || env.PushHint is not null)
            return MeshSendResult.Reject("ephemeral_route_invalid");
        var targetKnown = registration.DevicePublicKeys.Any(publicKey =>
            string.Equals(DeviceProtocol.DeviceId(publicKey), env.ToDevice, StringComparison.Ordinal));
        if (!targetKnown) return MeshSendResult.Reject("sync_target_unknown");

        var stamped = env with
        {
            From = handle,
            FromDevice = state.DeviceId,
            RelayDeliveryId = null,
            RelayDeviceScoped = false
        };
        var delivered = await router.RouteToOnlineDeviceAsync(
            stamped, Context.ConnectionId, Context.ConnectionAborted);
        if (!delivered) return MeshSendResult.Reject("ephemeral_not_delivered");
        metrics.MessageRouted();
        return MeshSendResult.Ok();
    }
    /// <summary>
    /// Routes one opaque ciphertext to a transient recipient list. The relay never inspects the
    /// encrypted dispatch metadata and never creates a durable group or membership record.
    /// </summary>
    public async Task<MeshSendResult> SendFanout(MeshFanoutRequest request)
    {
        var authorization = await GetAuthorizedConnectionAsync();
        var state = authorization.Connection;
        if (state is null)
            return MeshSendResult.Reject(authorization.Revoked ? "device_revoked" : "unauthenticated");
        var handle = state.Handle!;
        var publicKey = state.PublicKey!;
        if (request is null
            || string.IsNullOrWhiteSpace(request.Id)
            || string.IsNullOrWhiteSpace(request.Body)
            || string.IsNullOrWhiteSpace(request.Signature)
            || request.Recipients is null)
            return MeshSendResult.Reject("invalid_fanout");
        if (request.Recipients.Count > FanoutProtocol.MaxRecipients)
            return MeshSendResult.Reject("too_many_recipients");
        if (!MeshCrypto.Verify(publicKey, request.Body, request.Signature))
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

        var policy = await ratePolicies.GetPolicyAsync(handle, Context.ConnectionAborted);
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
            handle, MessageRateBucket.Group, Context.ConnectionAborted);
        if (!effectivePolicy.Enabled)
        {
            metrics.RateLimitRejected();
            return MeshSendResult.Reject("disabled");
        }
        if (!decision.Allowed)
        {
            metrics.RateLimitRejected();
            logger.LogWarning("fan-out rate limited: {Handle}", handle);
            return MeshSendResult.Reject("rate_limited", decision.RetryAfterMs);
        }

        var registrations = await Task.WhenAll(
            recipients.Select(handle => store.GetHandleAsync(handle)));
        if (registrations.Any(record => record is null || record.DevicePublicKeys.Count == 0))
            return MeshSendResult.Reject("invalid_recipient");

        var targets = registrations
            .SelectMany(record => record!.DevicePublicKeys
                .Select(publicKey => (record.Handle, DeviceId: DeviceProtocol.DeviceId(publicKey))))
            .Where(target => !(target.Handle == handle && target.DeviceId == state.DeviceId))
            .Distinct()
            .ToList();

        var sentAt = request.SentAt == default ? DateTimeOffset.UtcNow : request.SentAt;
        var tasks = targets.Select(async target =>
        {
            var envelope = new MeshEnvelope(
                request.Id,
                handle,
                target.Handle,
                MeshKinds.Fanout,
                request.Body,
                request.Signature,
                sentAt,
                state.DeviceId,
                target.DeviceId);
            return await RouteDurablyAsync(envelope);
        });

        var routes = await Task.WhenAll(tasks);
        if (routes.Any(result => !result.Admission.Accepted || result.Route is null))
            return MeshSendResult.Reject("inbox_admission_rejected");
        metrics.QueueEnqueued(routes.Count(result => result.Admission.Created));
        metrics.MessageRouted(targets.Count);
        return MeshSendResult.Ok(recipients.Count);
    }

    private async Task<(InboxEnqueueResult Admission, MeshRouteResult? Route)> RouteDurablyAsync(
        MeshEnvelope envelope,
        string? excludeConnectionId = null)
    {
        var clean = envelope with { RelayDeliveryId = null, RelayDeviceScoped = false };
        var to = Normalize(clean.To);
        var inboxKey = string.IsNullOrWhiteSpace(clean.ToDevice)
            ? to
            : RelayInboxKey.Device(to, clean.ToDevice);
        var admission = await store.EnqueueAsync(
            inboxKey,
            clean.Id,
            clean.From,
            JsonSerializer.Serialize(clean, Json),
            RelayInboxPriority.ForKind(clean.Kind),
            BackgroundSyncProtocol.RequiresForeground(clean.Kind),
            Context.ConnectionAborted);
        if (!admission.Accepted)
            return (admission, null);
        var route = string.IsNullOrWhiteSpace(clean.ToDevice)
            ? await router.RouteEnqueuedAsync(clean, admission, excludeConnectionId)
            : await router.RouteEnqueuedToDeviceAsync(clean, admission, excludeConnectionId);
        return (admission, route);
    }

    private async Task<(
        ConnectionRegistry.ConnState? Connection,
        StoredHandle? Registration,
        bool Revoked)> GetAuthorizedConnectionAsync()
    {
        var connection = registry.Get(Context.ConnectionId);
        if (connection is null
            || !connection.Authenticated
            || connection.Handle is null
            || connection.PublicKey is null)
            return (null, null, false);

        var registration = await store.GetHandleAsync(
            connection.Handle, Context.ConnectionAborted);
        if (registration?.DevicePublicKeys.Contains(connection.PublicKey) == true)
            return (connection, registration, false);

        if (!string.IsNullOrWhiteSpace(connection.DeviceId))
        {
            registry.RevokeDevice(connection.Handle, connection.DeviceId);
            await backplane.ClearDevicePresenceAsync(connection.Handle, connection.DeviceId);
            await backplane.ClearTransientDeviceRouteAsync(connection.Handle, connection.DeviceId);
        }
        else
        {
            connection.Authenticated = false;
        }
        if (registry.ConnectionsFor(connection.Handle, includeBackgroundSync: false).Count == 0)
            await backplane.ClearPresenceAsync(connection.Handle);
        return (null, null, true);
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

    private async Task NotifyDeviceQueueAvailableAsync(string handle, string targetDeviceId)
    {
        var localConnections = registry.ConnectionsForDevice(
            handle,
            targetDeviceId,
            includeBackgroundSync: false);
        if (localConnections.Count > 0)
        {
            await Clients.Clients(localConnections).SendAsync(MeshHubProtocol.DeviceQueueAvailable);
            return;
        }

        var notification = MeshEnvelope.Create(
            from: handle,
            to: handle,
            kind: MeshHubProtocol.DeviceQueueAvailable,
            body: "",
            toDevice: targetDeviceId);
        var notificationJson = System.Text.Json.JsonSerializer.Serialize(notification);

        // Prefer foreground presence on any replica before considering a transient route.
        var foregroundOwner = await backplane.GetInstanceForDeviceAsync(
            handle, targetDeviceId, Context.ConnectionAborted);
        if (foregroundOwner is not null
            && !string.Equals(foregroundOwner, backplane.InstanceId, StringComparison.Ordinal))
        {
            var receipt = await backplane.PublishToOwnerAsync(
                foregroundOwner, handle, notificationJson, Context.ConnectionAborted);
            if (receipt.Outcome == BackplaneDeliveryOutcome.Delivered)
                return;
        }

        var backgroundConnections = registry.ConnectionsForDevice(
            handle,
            targetDeviceId,
            includeBackgroundSync: true);
        if (backgroundConnections.Count > 0)
        {
            await Clients.Clients(backgroundConnections).SendAsync(MeshHubProtocol.DeviceQueueAvailable);
            return;
        }

        var transientOwner = await backplane.GetTransientInstanceForDeviceAsync(
            handle, targetDeviceId, Context.ConnectionAborted);
        if (transientOwner is not null
            && !string.Equals(transientOwner, backplane.InstanceId, StringComparison.Ordinal))
            await backplane.PublishToOwnerAsync(
                transientOwner, handle, notificationJson, Context.ConnectionAborted);
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
            await store.ReleaseDeviceQueueLeasesAsync(
                connection.Handle!,
                connection.DeviceId!,
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
        if (connection is { Handle: not null, DeviceId: not null })
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
        if (connection is { Authenticated: true, IsBackgroundSync: false, Handle: not null, DeviceId: not null }
            && registry.ConnectionsForDevice(
                connection.Handle, connection.DeviceId, includeBackgroundSync: false).Count == 0)
            await backplane.ClearDevicePresenceAsync(connection.Handle, connection.DeviceId);
        if (connection is { Authenticated: true, IsBackgroundSync: true, Handle: not null, DeviceId: not null }
            && !registry.HasBackgroundConnectionForDevice(connection.Handle, connection.DeviceId))
            await backplane.ClearTransientDeviceRouteAsync(connection.Handle, connection.DeviceId);
        if (handle is not null)
            await backplane.ClearPresenceAsync(handle); // only when it was the last local connection
        await base.OnDisconnectedAsync(exception);
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
