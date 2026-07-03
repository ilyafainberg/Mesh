using Microsoft.AspNetCore.SignalR;
using Mesh.Relay.Backplane;
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
    IBackplane backplane) : Microsoft.AspNetCore.SignalR.Hub
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

        var stamped = env with { From = state.Handle }; // relay asserts the authenticated sender
        await router.RouteAsync(stamped);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var handle = registry.Remove(Context.ConnectionId);
        if (handle is not null)
            await backplane.ClearPresenceAsync(handle); // only when it was the last local connection
        await base.OnDisconnectedAsync(exception);
    }

    private static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();
}
