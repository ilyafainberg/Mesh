using System.Text.Json;
using Mesh.Relay.Backplane;
using Mesh.Relay.Hub;
using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AgentDispatchTests
{
    private sealed class OutcomeBackplane(BackplaneDeliveryOutcome outcome) : IBackplane
    {
        public string InstanceId => "local";

        public Task StartAsync(Func<string, string, Task<bool>> deliverLocal, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SetPresenceAsync(string handle, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearPresenceAsync(string handle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearDevicePresenceAsync(string handle, string deviceId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string?> GetInstanceForAsync(string handle, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetInstanceForDeviceAsync(
            string handle,
            string deviceId,
            CancellationToken ct = default)
            => Task.FromResult<string?>("remote");
        public Task<bool> PublishToOwnerAsync(
            string instanceId,
            string toHandle,
            string envelopeJson,
            CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<BackplaneDeliveryOutcome> PublishAtomicToOwnerAsync(
            string instanceId,
            string toHandle,
            string envelopeJson,
            CancellationToken ct = default)
            => Task.FromResult(outcome);
    }

    [TestMethod]
    public async Task FirstCompatibleDesktopBecomesStickyDefaultPrimary()
    {
        var store = new InMemoryRelayStore();
        await store.UpsertHandleAsync("owner", "mobile-key", null, allowNewDevice: true);
        await store.UpsertHandleAsync("owner", "desktop-one-key", null, allowNewDevice: true);
        await store.UpsertHandleAsync("owner", "desktop-two-key", null, allowNewDevice: true);
        var mobile = DeviceProtocol.DeviceId("mobile-key");
        var first = DeviceProtocol.DeviceId("desktop-one-key");
        var second = DeviceProtocol.DeviceId("desktop-two-key");

        await store.SetDeviceMetadataAsync(
            "owner", mobile, "Phone", DevicePlatforms.Android,
            remoteAgentEnabled: false, atomicAgentDispatchEnabled: true);
        await store.SetDeviceMetadataAsync(
            "owner", first, "Desktop one", DevicePlatforms.Windows,
            remoteAgentEnabled: false, atomicAgentDispatchEnabled: true);
        await store.SetDeviceMetadataAsync(
            "owner", second, "Desktop two", DevicePlatforms.Windows,
            remoteAgentEnabled: true, atomicAgentDispatchEnabled: true);

        var handle = await store.GetHandleAsync("owner");
        Assert.IsNotNull(handle);
        Assert.AreEqual(first, handle.AgentPrimaryDeviceId);
        Assert.IsTrue(handle.AgentPrimaryWasSelectedAutomatically);
        Assert.IsFalse(string.IsNullOrWhiteSpace(handle.AgentRoutingVersion));
    }

    [TestMethod]
    public void RoutingPolicyPrefersPrimaryThenConfiguredFailover()
    {
        var handle = NewRoutingHandle();
        var primary = DeviceProtocol.DeviceId("primary-key");
        var failover = DeviceProtocol.DeviceId("failover-key");
        var primaryOnly = new HashSet<string>(StringComparer.Ordinal) { primary };
        var failoverOnly = new HashSet<string>(StringComparer.Ordinal) { failover };
        var both = new HashSet<string>(StringComparer.Ordinal) { primary, failover };

        Assert.AreEqual(primary, AgentRoutingPolicy.ChooseOnlineDevice(handle, both));
        Assert.AreEqual(primary, AgentRoutingPolicy.ChooseOnlineDevice(handle, primaryOnly));
        Assert.AreEqual(failover, AgentRoutingPolicy.ChooseOnlineDevice(handle, failoverOnly));

        handle.DeviceRemoteAgentEnabled[primary] = false;
        Assert.AreEqual(failover, AgentRoutingPolicy.ChooseOnlineDevice(handle, both));
        handle.DeviceRemoteAgentEnabled[primary] = true;

        handle.AgentFailoverDeviceId = null;
        Assert.IsNull(AgentRoutingPolicy.ChooseOnlineDevice(handle, failoverOnly));
    }

    [TestMethod]
    public async Task RoutingUpdateUsesCompareAndSwapVersion()
    {
        var store = new InMemoryRelayStore();
        await SeedDesktopAsync(store, "owner", "primary-key", "Primary");
        await SeedDesktopAsync(store, "owner", "failover-key", "Failover");
        var current = await store.GetHandleAsync("owner");
        Assert.IsNotNull(current);
        var primary = DeviceProtocol.DeviceId("primary-key");
        var failover = DeviceProtocol.DeviceId("failover-key");

        Assert.IsTrue(await store.SetAgentRoutingAsync(
            "owner", primary, failover, current.AgentRoutingVersion));
        Assert.IsFalse(await store.SetAgentRoutingAsync(
            "owner", failover, primary, current.AgentRoutingVersion));

        var saved = await store.GetHandleAsync("owner");
        Assert.IsNotNull(saved);
        Assert.AreEqual(primary, saved.AgentPrimaryDeviceId);
        Assert.AreEqual(failover, saved.AgentFailoverDeviceId);
        Assert.IsFalse(saved.AgentPrimaryWasSelectedAutomatically);
    }

    [TestMethod]
    public async Task DispatchCanBeDeliveredAndCompletedOnlyOnce()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        var created = await store.CreateAgentDispatchAsync(dispatch);
        Assert.AreEqual(AgentDispatchCreateStatus.Created, created.Status);

        await store.AssignPendingAgentDispatchesAsync(dispatch.To, ["primary"]);
        var takes = await Task.WhenAll(
            store.TakeAssignedAgentDispatchesAsync(dispatch.To, "primary"),
            store.TakeAssignedAgentDispatchesAsync(dispatch.To, "primary"));
        Assert.AreEqual(1, takes.Sum(items => items.Count));

        var completions = await Task.WhenAll(
            store.CompleteAgentDispatchAsync(
                dispatch.To, dispatch.Id, dispatch.From, dispatch.DispatchToken, "primary"),
            store.CompleteAgentDispatchAsync(
                dispatch.To, dispatch.Id, dispatch.From, dispatch.DispatchToken, "primary"));
        Assert.AreEqual(1, completions.Count(result => result));

        var completed = await store.GetAgentDispatchAsync(dispatch.To, dispatch.Id);
        Assert.IsNotNull(completed);
        Assert.AreEqual(AgentDispatchStates.Completed, completed.State);
        Assert.AreEqual("", completed.EnvelopeJson);
        Assert.AreEqual(dispatch.EnvelopeHash, completed.EnvelopeHash);
        Assert.AreEqual(0, completed.RecipientDeviceIds.Count);

        var retry = await store.CreateAgentDispatchAsync(dispatch);
        Assert.AreEqual(AgentDispatchCreateStatus.Duplicate, retry.Status);
    }

    [TestMethod]
    public async Task WrongDeviceOrTokenCannotCompleteDispatch()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        await store.CreateAgentDispatchAsync(dispatch);
        await store.AssignPendingAgentDispatchesAsync(dispatch.To, ["primary"]);
        _ = await store.TakeAssignedAgentDispatchesAsync(dispatch.To, "primary");

        Assert.IsFalse(await store.CompleteAgentDispatchAsync(
            dispatch.To, dispatch.Id, dispatch.From, "wrong-token", "primary"));
        Assert.IsFalse(await store.CompleteAgentDispatchAsync(
            dispatch.To, dispatch.Id, dispatch.From, dispatch.DispatchToken, "failover"));
        Assert.IsTrue(await store.CompleteAgentDispatchAsync(
            dispatch.To, dispatch.Id, dispatch.From, dispatch.DispatchToken, "primary"));
    }

    [TestMethod]
    public async Task AssignedDispatchCanBeReclaimedBeforeDelivery()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        await store.CreateAgentDispatchAsync(dispatch);
        await store.AssignPendingAgentDispatchesAsync(dispatch.To, ["primary"]);

        await store.AssignPendingAgentDispatchesAsync(dispatch.To, ["failover"]);

        Assert.AreEqual(0, (await store.TakeAssignedAgentDispatchesAsync(dispatch.To, "primary")).Count);
        Assert.AreEqual(1, (await store.TakeAssignedAgentDispatchesAsync(dispatch.To, "failover")).Count);
    }

    [TestMethod]
    public async Task ConfirmedDeliveryFailureReturnsRequestToPending()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        await store.CreateAgentDispatchAsync(dispatch);
        await store.AssignPendingAgentDispatchesAsync(dispatch.To, ["primary"]);
        _ = await store.TakeAssignedAgentDispatchesAsync(dispatch.To, "primary");

        Assert.IsTrue(await store.ReleaseAgentDispatchAsync(dispatch.To, dispatch.Id, "primary"));
        var pending = await store.GetAgentDispatchAsync(dispatch.To, dispatch.Id);
        Assert.IsNotNull(pending);
        Assert.AreEqual(AgentDispatchStates.Pending, pending.State);
        Assert.IsNull(pending.AssignedDeviceId);
    }

    [TestMethod]
    public async Task ConfirmedDeliveryFailureCanMoveDirectlyToFailover()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        await store.CreateAgentDispatchAsync(dispatch);
        await store.AssignPendingAgentDispatchesAsync(dispatch.To, ["primary", "failover"]);
        _ = await store.TakeAssignedAgentDispatchesAsync(dispatch.To, "primary");

        Assert.IsTrue(await store.ReleaseAgentDispatchAsync(
            dispatch.To, dispatch.Id, "primary", "failover"));

        var failoverTake = await store.TakeAssignedAgentDispatchesAsync(
            dispatch.To, "failover");
        Assert.AreEqual(1, failoverTake.Count);
        Assert.AreEqual(AgentDispatchStates.Delivered, failoverTake[0].State);
        Assert.AreEqual("failover", failoverTake[0].AssignedDeviceId);
    }

    [TestMethod]
    public async Task AssignmentUsesFirstReadyDeviceWithAnEncryptedKeySlot()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        dispatch.RecipientDeviceIds = ["failover"];
        await store.CreateAgentDispatchAsync(dispatch);

        await store.AssignPendingAgentDispatchesAsync(
            dispatch.To, ["new-primary", "failover"]);

        Assert.AreEqual(0, (await store.TakeAssignedAgentDispatchesAsync(
            dispatch.To, "new-primary")).Count);
        Assert.AreEqual(1, (await store.TakeAssignedAgentDispatchesAsync(
            dispatch.To, "failover")).Count);
    }

    [TestMethod]
    public async Task DeliveryClaimsOneAssignedRequestAtATime()
    {
        var store = new InMemoryRelayStore();
        var first = NewDispatch();
        var second = NewDispatch("question-2");
        await store.CreateAgentDispatchAsync(first);
        await store.CreateAgentDispatchAsync(second);
        await store.AssignPendingAgentDispatchesAsync(first.To, ["primary"]);

        var firstTake = await store.TakeAssignedAgentDispatchesAsync(first.To, "primary");
        var secondTake = await store.TakeAssignedAgentDispatchesAsync(first.To, "primary");

        Assert.AreEqual(1, firstTake.Count);
        Assert.AreEqual(1, secondTake.Count);
        Assert.AreNotEqual(firstTake[0].Id, secondTake[0].Id);
    }

    [TestMethod]
    public async Task UncertainCrossReplicaDeliveryRemainsFenced()
    {
        var dispatch = await DispatchWithOutcomeAsync(BackplaneDeliveryOutcome.Uncertain);

        Assert.AreEqual(AgentDispatchStates.Delivered, dispatch.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(dispatch.AssignedDeviceId));
    }

    [TestMethod]
    public async Task ConfirmedCrossReplicaMissReturnsRequestToPending()
    {
        var dispatch = await DispatchWithOutcomeAsync(BackplaneDeliveryOutcome.NotDelivered);

        Assert.AreEqual(AgentDispatchStates.Pending, dispatch.State);
        Assert.IsNull(dispatch.AssignedDeviceId);
    }

    [TestMethod]
    public async Task ReusedRequestIdWithDifferentEnvelopeIsRejectedAsConflict()
    {
        var store = new InMemoryRelayStore();
        var dispatch = NewDispatch();
        var first = await store.CreateAgentDispatchAsync(dispatch);
        var duplicate = await store.CreateAgentDispatchAsync(dispatch);
        var conflict = await store.CreateAgentDispatchAsync(WithEnvelope("different"));

        Assert.AreEqual(AgentDispatchCreateStatus.Created, first.Status);
        Assert.AreEqual(AgentDispatchCreateStatus.Duplicate, duplicate.Status);
        Assert.AreEqual(AgentDispatchCreateStatus.Conflict, conflict.Status);

        StoredAgentDispatch WithEnvelope(string json) => new()
        {
            Id = dispatch.Id,
            RequestId = dispatch.RequestId,
            From = dispatch.From,
            To = dispatch.To,
            EnvelopeJson = json,
            EnvelopeHash = json,
            DispatchToken = dispatch.DispatchToken,
            State = dispatch.State,
            QueuedAt = dispatch.QueuedAt
        };
    }

    [TestMethod]
    public async Task AtomicDispatchRejectsPlaintextRequestAndResponse()
    {
        var coordinator = new AgentDispatchCoordinator(
            null!,
            null!,
            null!,
            NullLogger<AgentDispatchCoordinator>.Instance);
        var request = MeshEnvelope.Create(
            "alice", "owner", MeshKinds.AtomicAgentRequest, "plaintext", id: "question-1");
        var response = MeshEnvelope.Create(
            "owner", "alice", MeshKinds.AtomicAgentResponse, "plaintext",
            agentRequestId: "question-1", agentDispatchToken: "token-1");

        var result = await coordinator.RouteRequestAsync(request);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("agent_dispatch_encryption_required", result.Code);
        Assert.IsFalse(await coordinator.CompleteResponseAsync(response, "primary"));
    }

    [TestMethod]
    public void AtomicEnvelopeRoundTripsDispatchMetadataAndStableId()
    {
        var envelope = MeshEnvelope.Create(
            "alice",
            "owner",
            MeshKinds.AtomicAgentRequest,
            "ciphertext",
            toDevice: "primary",
            agentRequestId: "question-1",
            agentDispatchToken: "token-1",
            id: "question-1");

        Assert.AreEqual("question-1", envelope.Id);
        Assert.AreEqual("question-1", envelope.AgentRequestId);
        Assert.AreEqual("token-1", envelope.AgentDispatchToken);
        Assert.AreEqual("primary", envelope.ToDevice);
    }

    private static StoredHandle NewRoutingHandle()
    {
        var primary = DeviceProtocol.DeviceId("primary-key");
        var failover = DeviceProtocol.DeviceId("failover-key");
        return new StoredHandle
        {
            Handle = "owner",
            DevicePublicKeys = ["primary-key", "failover-key"],
            DevicePlatforms = new Dictionary<string, string>
            {
                [primary] = DevicePlatforms.Windows,
                [failover] = DevicePlatforms.MacOS
            },
            DeviceRemoteAgentEnabled = new Dictionary<string, bool>
            {
                [primary] = true,
                [failover] = true
            },
            DeviceAtomicAgentDispatchEnabled = new Dictionary<string, bool>
            {
                [primary] = true,
                [failover] = true
            },
            AgentPrimaryDeviceId = primary,
            AgentFailoverDeviceId = failover
        };
    }

    private static async Task SeedDesktopAsync(
        InMemoryRelayStore store,
        string handle,
        string publicKey,
        string name)
    {
        await store.UpsertHandleAsync(handle, publicKey, null, allowNewDevice: true);
        await store.SetDeviceMetadataAsync(
            handle,
            DeviceProtocol.DeviceId(publicKey),
            name,
            DevicePlatforms.Windows,
            remoteAgentEnabled: true,
            atomicAgentDispatchEnabled: true);
    }

    private static async Task<StoredAgentDispatch> DispatchWithOutcomeAsync(
        BackplaneDeliveryOutcome outcome)
    {
        var store = new InMemoryRelayStore();
        await SeedDesktopAsync(store, "owner", "primary-key", "Primary");
        var primary = DeviceProtocol.DeviceId("primary-key");
        var body = JsonSerializer.Serialize(new
        {
            alg = "ECIES-P256-AESGCM",
            keys = new Dictionary<string, object>
            {
                [primary] = new { iv = "", wrap = "", tag = "" }
            }
        });
        var envelope = MeshEnvelope.Create(
            "alice", "owner", MeshKinds.AtomicAgentRequest, body, id: "question-1");
        var envelopeJson = JsonSerializer.Serialize(
            envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var dispatch = NewDispatch();
        dispatch.EnvelopeJson = envelopeJson;
        dispatch.EnvelopeHash = "fixture-envelope-hash";
        dispatch.RecipientDeviceIds = [primary];
        await store.CreateAgentDispatchAsync(dispatch);

        var backplane = new OutcomeBackplane(outcome);
        var router = new MeshRouter(
            null!, new ConnectionRegistry(), store, backplane, null!);
        var coordinator = new AgentDispatchCoordinator(
            store,
            backplane,
            router,
            NullLogger<AgentDispatchCoordinator>.Instance);

        await coordinator.DispatchPendingAsync("owner");
        var current = await store.GetAgentDispatchAsync("owner", dispatch.Id);
        Assert.IsNotNull(current);
        return current;
    }

    private static StoredAgentDispatch NewDispatch(string requestId = "question-1") => new()
    {
        Id = AgentDispatchKey.Create("alice", requestId),
        RequestId = requestId,
        From = "alice",
        To = "owner",
        EnvelopeJson = "ciphertext-envelope",
        EnvelopeHash = $"envelope-hash-{requestId}",
        RecipientDeviceIds = ["primary", "failover"],
        DispatchToken = "dispatch-token",
        State = AgentDispatchStates.Pending,
        QueuedAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero)
    };
}
