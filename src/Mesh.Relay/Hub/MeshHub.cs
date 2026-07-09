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
    IRelayStore store,
    IBackplane backplane,
    PerHandleRateLimiter rateLimiter,
    RelayMetrics metrics,
    ILogger<MeshHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
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
        registry.Add(Context.ConnectionId, handle, nonce);
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
        await backplane.SetPresenceAsync(state.Handle);
        await Clients.Caller.SendAsync(MeshHubProtocol.Ready);

        // Flush any messages queued while the recipient was offline.
        foreach (var pending in await store.DrainInboxAsync(state.Handle))
            await Clients.Caller.SendAsync(MeshHubProtocol.Receive, pending);
    }

    /// <summary>Receives an envelope from an authenticated connection and routes it.</summary>
    public async Task SendEnvelope(MeshEnvelope env)
    {
        var state = registry.Get(Context.ConnectionId);
        if (state is null || !state.Authenticated || state.Handle is null || state.PublicKey is null)
            return; // not authenticated: drop

        // Verify the message signature against the connection's authenticated key.
        if (!MeshCrypto.Verify(state.PublicKey, env.Body, env.Signature ?? ""))
            return; // forged or tampered: drop

        // Per-handle message rate limit: drop (do not route) when the sender is over its limit.
        // A single over-limit message is dropped, not disconnected, so a bursty client recovers.
        if (!rateLimiter.TryAcquire(state.Handle))
        {
            metrics.RateLimitRejected();
            logger.LogWarning("message rate limited: {Handle}", state.Handle);
            return;
        }

        var stamped = env with { From = state.Handle }; // relay asserts the authenticated sender
        stamped = stamped with { FromDevice = state.DeviceId }; // stamp the sending device (set at auth)

        // Usage attestation note: a ServiceRequest envelope carries the serviceId inside its
        // end-to-end encrypted body (ServiceProtocol-framed), so the relay cannot observe which
        // service was invoked while routing here. Attested usage for reputation is therefore recorded
        // out-of-band via the signed POST /capabilities/{serviceId}/used endpoint the consumer calls
        // after a successful invocation. A future version can record it here once the serviceId is
        // exposed in a cleartext routing header. Routing itself is unchanged for every envelope kind.

        // When a device sends to its own handle (remote-to-desktop), exclude the sender's own
        // connection so the message reaches the owner's OTHER devices rather than echoing back.
        var exclude = Normalize(stamped.To) == state.Handle ? Context.ConnectionId : null;
        await router.RouteAsync(stamped, exclude);
        metrics.MessageRouted();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Only count a close for a connection we counted on open (present in the registry).
        var counted = registry.Get(Context.ConnectionId) is not null;
        var handle = registry.Remove(Context.ConnectionId);
        if (counted)
        {
            metrics.ConnectionClosed();
            logger.LogInformation("hub connection closed: {Handle}", handle ?? "unknown");
        }
        if (handle is not null)
            await backplane.ClearPresenceAsync(handle); // only when it was the last local connection
        await base.OnDisconnectedAsync(exception);
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
