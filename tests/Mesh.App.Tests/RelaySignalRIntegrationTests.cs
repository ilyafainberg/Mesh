using System.Security.Cryptography;
using System.Text;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Relay.Observability;
using Mesh.Relay.Push;
using Mesh.Relay.RateLimiting;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// End-to-end SignalR contract tests that host the REAL Protocol 9 relay hub in-process over
/// Kestrel and connect a REAL <see cref="HubConnection"/> client through the full connect
/// challenge handshake. Unlike the socket-free switchboard harness (which records the delivery
/// object directly), these tests exercise the actual SignalR serializer, so they prove the relay
/// sends a typed <see cref="OnlineRelayDelivery"/> that binds to the client's
/// <c>On&lt;OnlineRelayDelivery&gt;</c> handler. This is the regression guard for the blocker
/// where the relay forwarded a JSON string that the typed client handler could not bind.
/// </summary>
[TestClass]
public sealed class RelaySignalRIntegrationTests
{
    private WebApplication _app = null!;
    private InMemoryRelayStore _store = null!;
    private string _baseUrl = null!;
    private readonly List<HubConnection> _connections = new();

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _store = new InMemoryRelayStore();
        var backplane = new InMemoryBackplane();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton<IRelayStore>(_store);
        builder.Services.AddSingleton<IBackplane>(backplane);
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<MeshRouter>();
        builder.Services.AddSingleton<RelayFrameDedup>();
        builder.Services.AddSingleton(new RelayMetrics());
        builder.Services.AddSingleton<PushDispatcher>();
        builder.Services.AddSingleton<IMessageRateLimiter, AllowAllRateLimiter>();
        builder.Services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = OnlineReplicationLimits.MaxTransportBytes + 64 * 1024;
        });

        _app = builder.Build();
        _app.MapHub<MeshHub>("/hub");

        // Route directed backplane forwards to this instance's local delivery (single node).
        var router = _app.Services.GetRequiredService<MeshRouter>();
        await backplane.StartAsync(router.DeliverFromBackplaneAsync);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        _baseUrl = addresses.First();
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
        _connections.Clear();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [TestMethod]
    public async Task Deliver_RoundTrips_As_Typed_Object_Over_Real_SignalR()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");

        var (connA, _) = await ConnectAsync(alice);
        var (connB, bobDeliveries) = await ConnectAsync(bob);

        var frameId = Guid.NewGuid().ToString("n");
        const string ciphertext = "opaque-e2e-ciphertext-payload";
        var frame = new OnlineRelayFrame(bob.Handle, bob.DeviceId, frameId, OnlinePushClasses.Normal, ciphertext);

        var result = await connA.InvokeAsync<OnlineRelaySendResult>(OnlineRelayMethods.Relay, frame);

        Assert.IsTrue(result.Accepted, $"relay send should be accepted, got code {result.Code}");
        Assert.AreEqual(OnlineRelaySendCodes.Delivered, result.Code);

        var delivery = await bobDeliveries.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNotNull(delivery, "bob should receive a typed OnlineRelayDelivery");
        Assert.AreEqual("alice", delivery.FromHandle);
        Assert.AreEqual(alice.DeviceId, delivery.FromDevice);
        Assert.AreEqual("bob", delivery.ToHandle);
        Assert.AreEqual(bob.DeviceId, delivery.ToDevice);
        Assert.AreEqual(frameId, delivery.FrameId);
        Assert.AreEqual(ciphertext, delivery.Ciphertext);
        Assert.AreEqual(OnlinePushClasses.Normal, delivery.PushClass);

        GC.KeepAlive(connB);
    }

    [TestMethod]
    public async Task ResolvePresence_Reports_Online_Sibling_Over_Real_SignalR()
    {
        var alice = await RegisterAsync("alice");
        var bobKeys = KeyPair.New();
        var bobDevice = DeviceProtocol.DeviceId(bobKeys.PublicB64);
        await _store.UpsertHandleAsync("alice", bobKeys.PublicB64, "display", allowNewDevice: true);
        await _store.SetDeviceMetadataAsync("alice", bobDevice, "sibling", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
        var bob = new Registration("alice", bobDevice, bobKeys);

        var (connA, _) = await ConnectAsync(alice);
        await ConnectAsync(bob);

        var snapshot = await connA.InvokeAsync<OnlinePresenceSnapshot>(
            OnlineRelayMethods.ResolvePresence, new[] { "alice" });

        var presence = snapshot.Handles.Single(h => h.Handle == "alice");
        Assert.IsTrue(presence.Online, "alice handle should be online");
        CollectionAssert.Contains(presence.Devices.ToArray(), alice.DeviceId);
        CollectionAssert.Contains(presence.Devices.ToArray(), bobDevice);
    }

    [TestMethod]
    public async Task SendEnvelope_Forwards_OnlineOnly_Control_To_TargetDevice()
    {
        var alice = await RegisterAsync("alice");
        var bob = await RegisterAsync("bob");
        var (connA, _) = await ConnectAsync(alice);
        var (connB, _) = await ConnectAsync(bob);
        var controls = new TypedSignal<string>();
        connB.On<string>(MeshHubProtocol.Receive, controls.Set);

        const string body = "opaque-control-body";
        var envelope = MeshEnvelope.Create(
            alice.Handle,
            bob.Handle,
            MeshKinds.Receipt,
            body,
            Sign(alice.Keys.PrivateB64, body),
            toDevice: bob.DeviceId);

        var result = await connA.InvokeAsync<MeshSendResult>(
            MeshHubProtocol.SendEnvelope, envelope);
        var receivedJson = await controls.WaitAsync(TimeSpan.FromSeconds(15));
        var received = System.Text.Json.JsonSerializer.Deserialize<MeshEnvelope>(
            receivedJson, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));

        Assert.IsTrue(result.Accepted);
        Assert.IsNotNull(received);
        Assert.AreEqual(alice.Handle, received.From);
        Assert.AreEqual(alice.DeviceId, received.FromDevice);
        Assert.AreEqual(bob.Handle, received.To);
        Assert.AreEqual(bob.DeviceId, received.ToDevice);
        Assert.AreEqual(MeshKinds.Receipt, received.Kind);
        Assert.AreEqual(body, received.Body);
    }

    // -------------------------------------------------------------------------

    private sealed record Registration(string Handle, string DeviceId, KeyPair Keys);

    private async Task<Registration> RegisterAsync(string handle)
    {
        var keys = KeyPair.New();
        var deviceId = DeviceProtocol.DeviceId(keys.PublicB64);
        await _store.UpsertHandleAsync(handle, keys.PublicB64, "display", allowNewDevice: true);
        await _store.SetDeviceMetadataAsync(handle, deviceId, "device", DevicePlatforms.IOS, false, false, MeshProtocol.Version);
        return new Registration(handle, deviceId, keys);
    }

    private async Task<(HubConnection Conn, TypedSignal<OnlineRelayDelivery> Deliveries)> ConnectAsync(Registration reg)
    {
        var record = (await _store.GetHandleAsync(reg.Handle))!;
        var url =
            $"{_baseUrl}/hub?handle={Uri.EscapeDataString(reg.Handle)}" +
            $"&deviceId={Uri.EscapeDataString(reg.DeviceId)}" +
            $"&protocolVersion={MeshProtocol.Version}" +
            $"&authGeneration={record.AuthGeneration}" +
            $"&custodyHead={Uri.EscapeDataString(record.CustodyHead)}";

        var conn = new HubConnectionBuilder().WithUrl(url).Build();
        _connections.Add(conn);

        var presence = new TaskCompletionSource<PresenceConfirmed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new TypedSignal<OnlineRelayDelivery>();

        conn.On<string>(MeshHubProtocol.Challenge, nonce =>
        {
            var canonical = RelayConnectChallenge.Canonical(
                nonce, reg.Handle, reg.DeviceId, MeshProtocol.Version, record.AuthGeneration, record.CustodyHead);
            // Fire-and-forget: replying from inside the receive callback with SendAsync avoids
            // re-entrant Invoke deadlocks. Authenticate returns void; the server replies with
            // PresenceConfirmed which completes the handshake below.
            return conn.SendAsync(MeshHubProtocol.Authenticate, reg.Keys.PublicB64, Sign(reg.Keys.PrivateB64, canonical));
        });
        conn.On<PresenceConfirmed>(MeshHubProtocol.PresenceConfirmed, p => presence.TrySetResult(p));
        conn.On<OnlineRelayDelivery>(OnlineRelayMethods.Deliver, d => deliveries.Set(d));

        await conn.StartAsync();
        await presence.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return (conn, deliveries);
    }

    private static string Sign(string privateKeyB64, string message)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    private sealed class TypedSignal<T>
    {
        private readonly TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(T value) => tcs.TrySetResult(value);
        public async Task<T> WaitAsync(TimeSpan timeout) => await tcs.Task.WaitAsync(timeout);
    }

    private sealed class AllowAllRateLimiter : IMessageRateLimiter
    {
        private static readonly HandleRatePolicy Policy = new(1_000_000, 1_000_000, 1_000_000, 1_000_000, 4096);

        public Task<(RateLimitDecision Decision, HandleRatePolicy Policy)> TryAcquireAsync(
            string handle, MessageRateBucket bucket, CancellationToken ct = default)
            => Task.FromResult((new RateLimitDecision(true, 0, 1_000_000), Policy));
    }
}
