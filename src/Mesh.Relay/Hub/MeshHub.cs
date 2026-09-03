using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mesh.Relay.Backplane;
#if MESH_TEST_RELAY_FAULTS
using Mesh.Relay.LiveFaults;
#endif
using Mesh.Relay.Observability;
using Mesh.Relay.Push;
using Mesh.Relay.RateLimiting;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.Hub;

/// <summary>
/// The Protocol 9 online-only relay hub: an authenticated opaque switchboard. SignalR owns the
/// socket, framing, keepalive and reconnection; this hub adds device-key authentication and
/// forwards opaque encrypted frames to the live sockets that own the target.
///
/// The relay NEVER persists a message, sync, attachment or agent payload. It does not queue, lease,
/// acknowledge, or store frames. A frame is either delivered to at least one online socket right now
/// (locally or via the transient backplane) or answered not_online, optionally after emitting a
/// contentless push wake so a backgrounded device reconnects and pulls. Custody stays with the
/// sender until an online socket receives the frame, so a wake is never delivery.
///
/// Auth (exact Protocol 9): the connect query carries handle, deviceId, protocolVersion,
/// authGeneration and custodyHead. The hub rejects an unknown handle, a device that is not
/// authorized, a protocol other than 9, or stale authority (auth generation / custody head that do
/// not match the store) BEFORE any presence is set. It then issues a fresh nonce; the client signs
/// the canonical connect challenge (see <see cref="RelayConnectChallenge"/>) with its device private
/// key and calls <see cref="Authenticate"/>. Only after the signature verifies is presence set.
///
/// Sender identity on every delivery is stamped by the hub from the authenticated connection; any
/// sender metadata a caller might try to supply is ignored.
/// </summary>
public sealed class MeshHub(
    ConnectionRegistry registry,
    MeshRouter router,
    IRelayStore store,
    IBackplane backplane,
    IMessageRateLimiter rateLimiter,
    RelayFrameDedup dedup,
    PushDispatcher push,
    RelayMetrics metrics,
    TimeProvider clock,
#if MESH_TEST_RELAY_FAULTS
    LiveFaultStore liveFaults,
    LiveFaultHandshakeObserver handshakeObserver,
    LiveFaultTransportObserver transportObserver,
#endif
    ILogger<MeshHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> OnlineControlKinds = new(StringComparer.Ordinal)
    {
        MeshKinds.AtomicAgentRequest,
        MeshKinds.AtomicAgentResponse,
        MeshKinds.ServiceRequest,
        MeshKinds.ServiceResponse,
        MeshKinds.Receipt,
        MeshKinds.Report,
        MeshKinds.TopicRunRequest,
        MeshKinds.TopicRunUpdate,
        MeshKinds.TopicRunCancel,
        MeshKinds.AttachmentChunk,
        MeshKinds.TopicAttachmentChunk
    };

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var handle = Normalize(http?.Request.Query["handle"].ToString() ?? "");
        var deviceId = http?.Request.Query["deviceId"].ToString() ?? "";
        var protocolVersion = int.TryParse(http?.Request.Query["protocolVersion"].ToString(), out var pv) ? pv : 0;
        var authGeneration = long.TryParse(http?.Request.Query["authGeneration"].ToString(), out var ag) ? ag : -1;
        var custodyHead = http?.Request.Query["custodyHead"].ToString() ?? "";

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

        if (string.IsNullOrWhiteSpace(handle) || !DeviceProtocol.IsValidDeviceId(deviceId))
        {
            Context.Abort();
            return;
        }

        // Reject an unregistered handle, an unauthorized/revoked device, or stale authority
        // BEFORE any presence is set. The client registers over REST and syncs custody first.
        var record = await store.GetHandleAsync(handle);
        if (record is null
            || !AuthorizedDeviceIds(record).Contains(deviceId)
            || record.AuthGeneration != authGeneration
            || !string.Equals(record.CustodyHead, custodyHead, StringComparison.Ordinal))
        {
#if MESH_TEST_RELAY_FAULTS
            handshakeObserver.Record(new LiveFaultHandshakeEvent(
                "rejected-before-challenge",
                handle,
                deviceId,
                authGeneration,
                custodyHead));
#endif
            Context.Abort();
            return;
        }

        var nonce = MeshCrypto.NewNonce();
#if MESH_TEST_RELAY_FAULTS
        handshakeObserver.Record(new LiveFaultHandshakeEvent(
            "challenge",
            handle,
            deviceId,
            authGeneration,
            custodyHead,
            nonce));
#endif
        registry.Add(Context.ConnectionId, handle, nonce, protocolVersion, authGeneration, custodyHead);
        var state = registry.Get(Context.ConnectionId);
        if (state is not null) state.DeviceId = deviceId; // claimed device, proven at Authenticate
        metrics.ConnectionOpened();
        logger.LogInformation("hub connection opened: handle={Handle} device={Device}", handle, deviceId);

        await Clients.Caller.SendAsync(
            MeshHubProtocol.Handshake,
            new HandshakeResponse(MeshProtocol.Version, HandshakeResult.Accepted));
        await Clients.Caller.SendAsync(MeshHubProtocol.Challenge, nonce);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Completes the connect challenge. Verifies the signature over the canonical connect string
    /// against a device public key registered under the handle whose derived device id matches the
    /// device claimed at connect, and re-checks current authority. Only then is presence set.
    /// </summary>
    public async Task Authenticate(string publicKey, string signature)
    {
        var state = registry.Get(Context.ConnectionId);
        if (state?.Handle is null || string.IsNullOrWhiteSpace(state.DeviceId))
        {
            Context.Abort();
            return;
        }

        var record = await store.GetHandleAsync(state.Handle);
        var canonical = RelayConnectChallenge.Canonical(
            state.Nonce, state.Handle, state.DeviceId, state.ProtocolVersion, state.AuthGeneration, state.CustodyHead);

        var accepted = record is not null
            && record.DevicePublicKeys.Contains(publicKey)
            && string.Equals(DeviceProtocol.DeviceId(publicKey), state.DeviceId, StringComparison.Ordinal)
            && record.AuthGeneration == state.AuthGeneration
            && string.Equals(record.CustodyHead, state.CustodyHead, StringComparison.Ordinal)
            && MeshCrypto.Verify(publicKey, canonical, signature);
#if MESH_TEST_RELAY_FAULTS
        handshakeObserver.Record(new LiveFaultHandshakeEvent(
            "authenticate",
            state.Handle,
            state.DeviceId,
            state.AuthGeneration,
            state.CustodyHead,
            state.Nonce,
            canonical,
            signature,
            accepted));
#endif
        if (!accepted)
        {
            Context.Abort();
            return;
        }

        registry.MarkAuthenticated(Context.ConnectionId, publicKey);
        var now = clock.GetUtcNow();
        await backplane.SetPresenceAsync(state.Handle);
        await backplane.SetDevicePresenceAsync(state.Handle, state.DeviceId);

        await Clients.Caller.SendAsync(
            MeshHubProtocol.PresenceConfirmed,
            new PresenceConfirmed(state.Handle, state.DeviceId, now, now + PresenceTtl, backplane.InstanceId));
        logger.LogInformation("hub authenticated: handle={Handle} device={Device}", state.Handle, state.DeviceId);
    }

    /// <summary>
    /// Online-only opaque forward. Stamps sender identity from the authenticated connection,
    /// validates route/size/push-class, rate limits, de-duplicates, then delivers to the target's
    /// live socket(s) or answers not_online (emitting a contentless wake for offline devices).
    /// </summary>
    public async Task<OnlineRelaySendResult> Relay(OnlineRelayFrame frame)
    {
        var (connection, _, revoked) = await GetAuthorizedConnectionAsync();
        if (revoked)
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.DeviceRevoked);
        if (connection?.Handle is null || connection.DeviceId is null)
        {
            Context.Abort();
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
        }

        if (frame is null
            || string.IsNullOrWhiteSpace(frame.ToHandle)
            || string.IsNullOrWhiteSpace(frame.FrameId)
            || string.IsNullOrEmpty(frame.Ciphertext))
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);

        if (Encoding.UTF8.GetByteCount(frame.Ciphertext) > OnlineReplicationLimits.MaxTransportBytes)
        {
            metrics.FrameRejectedTooLarge();
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.TooLarge);
        }

        var pushClass = OnlinePushClasses.IsKnown(frame.PushClass) ? frame.PushClass : OnlinePushClasses.Normal;
        var toHandle = Normalize(frame.ToHandle);
        var directed = !string.IsNullOrWhiteSpace(frame.ToDevice);
#if MESH_TEST_RELAY_FAULTS
        if (directed)
        {
            var faultResult = ApplyOnlineFrameFault(
                connection.Handle,
                connection.DeviceId,
                toHandle,
                frame.ToDevice!,
                frame.FrameId);
            if (faultResult is not null) return faultResult;
        }
#endif

        var admission = dedup.TryBegin(frame.FrameId);
        if (admission == RelayFrameDedup.Admission.Delivered)
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        if (admission == RelayFrameDedup.Admission.InFlight)
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.RateLimited, 100);

        try
        {
            var bucket = directed ? MessageRateBucket.Direct : MessageRateBucket.Group;
            var (decision, policy) = await rateLimiter.TryAcquireAsync(
                connection.Handle, bucket, Context.ConnectionAborted);
            if (!decision.Allowed)
            {
                metrics.RateLimitRejected();
                dedup.Release(frame.FrameId);
                return new OnlineRelaySendResult(
                    false, OnlineRelaySendCodes.RateLimited, decision.RetryAfterMs);
            }

            var target = await store.GetHandleAsync(toHandle, Context.ConnectionAborted);
            if (target is null)
            {
                metrics.OfflineNack();
                dedup.Release(frame.FrameId);
                return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
            }

            var authorizedDevices = AuthorizedDeviceIds(target);
            var excludeSelf = string.Equals(toHandle, connection.Handle, StringComparison.Ordinal)
                ? Context.ConnectionId
                : null;

            var result = directed
                ? await RelayDirectedAsync(
                    connection,
                    toHandle,
                    frame.ToDevice!,
                    frame,
                    pushClass,
                    authorizedDevices,
                    excludeSelf)
                : await RelayAccountAsync(
                    connection,
                    toHandle,
                    frame,
                    pushClass,
                    authorizedDevices,
                    policy,
                    excludeSelf);
            if (result.Accepted)
                dedup.Commit(frame.FrameId);
            else
                dedup.Release(frame.FrameId);
            return result;
        }
        catch
        {
            dedup.Release(frame.FrameId);
            throw;
        }
    }
    /// <summary>Authenticates and emits an ephemeral contentless wake for one authorized device.</summary>
    public async Task<OnlineWakeResult> Wake(OnlineWakeRequest request)
    {
        var (connection, _, revoked) = await GetAuthorizedConnectionAsync();
        if (revoked) return new OnlineWakeResult(false, OnlineWakeCodes.DeviceRevoked);
        if (connection?.Handle is null || connection.DeviceId is null)
        {
            Context.Abort();
            return new OnlineWakeResult(false, OnlineWakeCodes.Invalid);
        }
        if (request is null
            || string.IsNullOrWhiteSpace(request.ToHandle)
            || !DeviceProtocol.IsValidDeviceId(request.ToDevice)
            || string.IsNullOrWhiteSpace(request.WakeId)
            || request.WakeId.Length > 128)
            return new OnlineWakeResult(false, OnlineWakeCodes.Invalid);

        var (decision, policy) = await rateLimiter.TryAcquireAsync(
            connection.Handle, MessageRateBucket.Direct, Context.ConnectionAborted);
        if (!policy.Enabled || !decision.Allowed)
        {
            metrics.RateLimitRejected();
            return new OnlineWakeResult(
                false, OnlineWakeCodes.RateLimited, decision.RetryAfterMs);
        }

        var toHandle = Normalize(request.ToHandle);
        var target = await store.GetHandleAsync(toHandle, Context.ConnectionAborted);
        if (target is null || !AuthorizedDeviceIds(target).Contains(request.ToDevice))
            return new OnlineWakeResult(false, OnlineWakeCodes.TargetDeviceUnknown);
        if (registry.ConnectionsForDevice(toHandle, request.ToDevice).Count > 0
            || await backplane.GetInstanceForDeviceAsync(
                toHandle,
                request.ToDevice,
                Context.ConnectionAborted) is not null)
            return new OnlineWakeResult(true, OnlineWakeCodes.Accepted);


        var outcome = await push.RequestWakeAsync(
            toHandle,
            request.ToDevice,
            request.WakeId,
            request.NotificationWorthy,
            Context.ConnectionAborted);
        if (outcome == PushDispatchOutcome.Sent) metrics.PushWake();
        return outcome switch
        {
            PushDispatchOutcome.Sent or PushDispatchOutcome.Coalesced
                => new OnlineWakeResult(true, OnlineWakeCodes.Accepted),
            PushDispatchOutcome.Throttled
                => new OnlineWakeResult(
                    false, OnlineWakeCodes.RateLimited,
                    request.NotificationWorthy ? 5_000 : 30_000),
            PushDispatchOutcome.NoTarget
                => new OnlineWakeResult(false, OnlineWakeCodes.TargetUnavailable),
            _ => new OnlineWakeResult(false, OnlineWakeCodes.DeliveryFailed)
        };
    }


    /// <summary>
    /// Routes an encrypted control envelope only while the target is online. This is not a message
    /// queue: the relay never stores the envelope and returns not_online when no target socket exists.
    /// Durable human messages and history use the Protocol 9 replication journal instead.
    /// </summary>
    public async Task<MeshSendResult> SendEnvelope(MeshEnvelope env)
    {
        var (connection, registration, revoked) = await GetAuthorizedConnectionAsync();
        if (revoked) return MeshSendResult.Reject(OnlineRelaySendCodes.DeviceRevoked);
        if (connection?.Handle is null || connection.DeviceId is null || registration is null)
            return MeshSendResult.Reject("unauthenticated");
        if (env is null
            || !OnlineControlKinds.Contains(env.Kind)
            || string.IsNullOrWhiteSpace(env.To)
            || string.IsNullOrWhiteSpace(env.Id)
            || string.IsNullOrEmpty(env.Body))
            return MeshSendResult.Reject("unsupported_control");
        if (!MeshCrypto.Verify(connection.PublicKey!, env.Body, env.Signature ?? ""))
            return MeshSendResult.Reject("invalid_signature");
        if (Encoding.UTF8.GetByteCount(env.Body) > MessageLimits.MaxEnvelopeBodyBytes)
            return MeshSendResult.Reject("message_too_large");

        var (decision, policy) = await rateLimiter.TryAcquireAsync(
            connection.Handle, MessageRateBucket.Direct, Context.ConnectionAborted);
        if (!policy.Enabled) return MeshSendResult.Reject("disabled");
        if (!decision.Allowed)
        {
            metrics.RateLimitRejected();
            return MeshSendResult.Reject(OnlineRelaySendCodes.RateLimited, decision.RetryAfterMs);
        }

        var toHandle = Normalize(env.To);
        var target = await store.GetHandleAsync(toHandle, Context.ConnectionAborted);
        if (target is null) return MeshSendResult.Reject(OnlineRelaySendCodes.NotOnline);

        var stamped = env with
        {
            From = connection.Handle,
            FromDevice = connection.DeviceId,
            To = toHandle
        };
        string? directedDevice = string.IsNullOrWhiteSpace(stamped.ToDevice) ? null : stamped.ToDevice;
        if (stamped.Kind is MeshKinds.AtomicAgentRequest or MeshKinds.ServiceRequest)
        {
            var online = (await OnlineDevicesAsync(toHandle)).ToHashSet(StringComparer.Ordinal);
            directedDevice = AgentRoutingPolicy.ChooseOnlineDevice(target, online);
            if (directedDevice is null)
                return MeshSendResult.Reject("agent_unavailable");
            stamped = stamped with { ToDevice = directedDevice };
        }

        var authorized = AuthorizedDeviceIds(target);
        if (directedDevice is not null)
        {
            if (!authorized.Contains(directedDevice))
                return MeshSendResult.Reject(OnlineRelaySendCodes.TargetDeviceUnknown);
#if MESH_TEST_RELAY_FAULTS
            var faultResult = ApplyControlFault(
                connection.Handle,
                connection.DeviceId,
                toHandle,
                directedDevice,
                stamped.Kind,
                stamped.Id);
            if (faultResult is not null) return faultResult;
#endif
            var outcome = await router.ForwardControlToDeviceAsync(
                toHandle,
                directedDevice,
                stamped,
                string.Equals(toHandle, connection.Handle, StringComparison.Ordinal)
                    ? Context.ConnectionId
                    : null,
                Context.ConnectionAborted);
            if (outcome != BackplaneDeliveryOutcome.Delivered)
                return MeshSendResult.Reject(OnlineRelaySendCodes.NotOnline);
            metrics.OnlineDelivered();
            return MeshSendResult.Ok();
        }

        var delivered = 0;
        foreach (var device in authorized)
        {
#if MESH_TEST_RELAY_FAULTS
            var faultResult = ApplyControlFault(
                connection.Handle,
                connection.DeviceId,
                toHandle,
                device,
                stamped.Kind,
                stamped.Id);
            if (faultResult is { Accepted: true })
            {
                delivered++;
                continue;
            }
            if (faultResult is not null) continue;
#endif

            var outcome = await router.ForwardControlToDeviceAsync(
                toHandle,
                device,
                stamped with { ToDevice = device },
                string.Equals(toHandle, connection.Handle, StringComparison.Ordinal)
                    ? Context.ConnectionId
                    : null,
                Context.ConnectionAborted);
            if (outcome == BackplaneDeliveryOutcome.Delivered) delivered++;
        }
        if (delivered == 0) return MeshSendResult.Reject(OnlineRelaySendCodes.NotOnline);
        metrics.OnlineDelivered(delivered);
        return MeshSendResult.Ok(delivered);
    }

    public Task<MeshSendResult> SendEphemeralEnvelope(MeshEnvelope env) => SendEnvelope(env);

#if MESH_TEST_RELAY_FAULTS
    private OnlineRelaySendResult? ApplyOnlineFrameFault(
        string sourceAccount,
        string sourceDevice,
        string targetAccount,
        string targetDevice,
        string stableId)
    {
        var decision = liveFaults.TryApply(
            LiveFaultDirection.Outbound,
            sourceAccount,
            sourceDevice,
            targetAccount,
            targetDevice,
            LiveFaultStore.OnlineFrameKind,
            stableId);
        if (decision is null) return null;
        logger.LogWarning(
            "live fault consumed: rule={RuleId} mode={Mode} source={Source} sourceDevice={SourceDevice} target={Target} targetDevice={TargetDevice} kind={Kind} idHash={StableIdHash}",
            decision.RuleId, decision.Mode, sourceAccount, sourceDevice, targetAccount, targetDevice,
            LiveFaultStore.OnlineFrameKind, LiveFaultIds.Hash(stableId));
        return decision.Mode switch
        {
            LiveFaultMode.RejectBeforeForwarding
                => new OnlineRelaySendResult(false, LiveFaultStore.RejectedCode),
            LiveFaultMode.DropBeforeForwarding
                => new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline),
            _ => new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered)
        };
    }

    private MeshSendResult? ApplyControlFault(
        string sourceAccount,
        string sourceDevice,
        string targetAccount,
        string targetDevice,
        string kind,
        string stableId)
    {
        transportObserver.RecordAttempt(stableId);
        var decision = liveFaults.TryApply(
            LiveFaultDirection.Outbound,
            sourceAccount,
            sourceDevice,
            targetAccount,
            targetDevice,
            kind,
            stableId);
        if (decision is null) return null;
        logger.LogWarning(
            "live fault consumed: rule={RuleId} mode={Mode} source={Source} sourceDevice={SourceDevice} target={Target} targetDevice={TargetDevice} kind={Kind} idHash={StableIdHash}",
            decision.RuleId, decision.Mode, sourceAccount, sourceDevice, targetAccount, targetDevice,
            kind, LiveFaultIds.Hash(stableId));
        return decision.Mode switch
        {
            LiveFaultMode.RejectBeforeForwarding
                => MeshSendResult.Reject(LiveFaultStore.RejectedCode),
            LiveFaultMode.DropBeforeForwarding
                => MeshSendResult.Reject(OnlineRelaySendCodes.NotOnline),
            _ => MeshSendResult.Ok()
        };
    }
#endif

    private async Task<OnlineRelaySendResult> RelayDirectedAsync(
        ConnectionRegistry.ConnState connection,
        string toHandle,
        string toDevice,
        OnlineRelayFrame frame,
        string pushClass,
        IReadOnlySet<string> authorizedDevices,
        string? excludeSelf)
    {
        if (!authorizedDevices.Contains(toDevice))
            return new OnlineRelaySendResult(false, OnlineRelaySendCodes.TargetDeviceUnknown);

        var delivery = StampDelivery(connection, toHandle, toDevice, frame, pushClass);
        var outcome = await router.ForwardToDeviceAsync(toHandle, toDevice, delivery, excludeSelf, Context.ConnectionAborted);
        if (outcome == BackplaneDeliveryOutcome.Delivered)
        {
            metrics.OnlineDelivered();
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        metrics.OfflineNack();
        return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
    }

    private async Task<OnlineRelaySendResult> RelayAccountAsync(
        ConnectionRegistry.ConnState connection,
        string toHandle,
        OnlineRelayFrame frame,
        string pushClass,
        IReadOnlySet<string> authorizedDevices,
        HandleRatePolicy policy,
        string? excludeSelf)
    {
        var fanout = authorizedDevices.Take(Math.Max(1, policy.MaxFanoutRecipients)).ToArray();
        var delivered = 0;
        foreach (var device in fanout)
        {
            var delivery = StampDelivery(connection, toHandle, device, frame, pushClass);
            var outcome = await router.ForwardToDeviceAsync(toHandle, device, delivery, excludeSelf, Context.ConnectionAborted);
            if (outcome == BackplaneDeliveryOutcome.Delivered) delivered++;
        }

        if (delivered > 0)
        {
            metrics.OnlineDelivered(delivered);
            return new OnlineRelaySendResult(true, OnlineRelaySendCodes.Delivered);
        }

        metrics.OfflineNack(Math.Max(1, fanout.Length));
        return new OnlineRelaySendResult(false, OnlineRelaySendCodes.NotOnline);
    }

    /// <summary>Resolves which of the requested handles have a live device online right now.</summary>
    public async Task<OnlinePresenceSnapshot> ResolvePresence(string[] handles)
    {
        var (connection, _, _) = await GetAuthorizedConnectionAsync();
        if (connection?.Handle is null)
        {
            Context.Abort();
            return new OnlinePresenceSnapshot(Array.Empty<OnlineHandlePresence>());
        }

        var result = new List<OnlineHandlePresence>();
        foreach (var raw in handles ?? Array.Empty<string>())
        {
            var handle = Normalize(raw);
            if (string.IsNullOrWhiteSpace(handle)) continue;
            var devices = await OnlineDevicesAsync(handle);
            result.Add(new OnlineHandlePresence(handle, devices.Count > 0, devices));
        }
        return new OnlinePresenceSnapshot(result);
    }

    private async Task<IReadOnlyList<string>> OnlineDevicesAsync(string handle)
    {
        var record = await store.GetHandleAsync(handle, Context.ConnectionAborted);
        if (record is null) return Array.Empty<string>();

        var online = new List<string>();
        foreach (var device in AuthorizedDeviceIds(record))
        {
            if (registry.ConnectionsForDevice(handle, device).Count > 0
                || await backplane.GetInstanceForDeviceAsync(handle, device, Context.ConnectionAborted) is not null)
                online.Add(device);
        }
        return online;
    }

    private OnlineRelayDelivery StampDelivery(
        ConnectionRegistry.ConnState connection,
        string toHandle,
        string? toDevice,
        OnlineRelayFrame frame,
        string pushClass)
        => new(
            FromHandle: connection.Handle!,
            FromDevice: connection.DeviceId!,
            ToHandle: toHandle,
            ToDevice: toDevice,
            FrameId: frame.FrameId,
            PushClass: pushClass,
            Ciphertext: frame.Ciphertext);


    private async Task<(
        ConnectionRegistry.ConnState? Connection,
        StoredHandle? Registration,
        bool Revoked)> GetAuthorizedConnectionAsync()
    {
        var connection = registry.Get(Context.ConnectionId);
        if (connection is null
            || !connection.Authenticated
            || connection.Handle is null
            || connection.PublicKey is null
            || connection.DeviceId is null)
            return (null, null, false);

        var registration = await store.GetHandleAsync(connection.Handle, Context.ConnectionAborted);
        if (registration?.DevicePublicKeys.Contains(connection.PublicKey) == true)
            return (connection, registration, false);

        // The device was revoked (or the handle deleted) since this socket authenticated.
        registry.RevokeDevice(connection.Handle, connection.DeviceId);
        await backplane.ClearDevicePresenceAsync(connection.Handle, connection.DeviceId);
        if (registry.ConnectionsFor(connection.Handle).Count == 0)
            await backplane.ClearPresenceAsync(connection.Handle);
        return (null, null, true);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connection = registry.Get(Context.ConnectionId);
        var counted = connection is not null;
        var handle = registry.Remove(Context.ConnectionId);

        if (counted)
        {
            metrics.ConnectionClosed();
            logger.LogInformation(
                "hub connection closed: handle={Handle} authenticated={Authenticated} error={Error}",
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

    private static IReadOnlySet<string> AuthorizedDeviceIds(StoredHandle record)
        => record.DevicePublicKeys.Select(DeviceProtocol.DeviceId).ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
