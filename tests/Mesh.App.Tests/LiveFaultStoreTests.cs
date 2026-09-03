using System.Text.Json;
using Mesh.Relay.LiveFaults;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class LiveFaultStoreTests
{
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    [TestMethod]
    public void DisabledStoreCannotActivateAndNeverIntercepts()
    {
        var store = new LiveFaultStore(new LiveFaultOptions { Enabled = false });
        var request = Request("disabled");

        Assert.ThrowsExactly<InvalidOperationException>(() => store.Activate(request));
        Assert.IsNull(Apply(store, request));
        Assert.AreEqual(0, store.Audit().Count);
    }

    [TestMethod]
    public void AdminAuthorizationFailsClosed()
    {
        Assert.IsFalse(LiveFaultAdminAuthorization.IsAuthorized(null, "key"));
        Assert.IsFalse(LiveFaultAdminAuthorization.IsAuthorized("key", null));
        Assert.IsFalse(LiveFaultAdminAuthorization.IsAuthorized("key", "wrong"));
        Assert.IsTrue(LiveFaultAdminAuthorization.IsAuthorized("key", "key"));
    }

    [TestMethod]
    public void RuleIsTtlBoundIdempotentAndAutomaticallyExpires()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-22T18:00:00Z"));
        var store = Enabled(clock);
        var request = Request("ttl", ttlSeconds: 30);

        var first = store.Activate(request);
        var repeated = store.Activate(request);
        Assert.AreEqual(first, repeated);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsNull(Apply(store, request));
        Assert.IsFalse(store.Get(request.RuleId)!.Active);
        Assert.AreEqual(1, store.Audit().Count(entry => entry.Event == "expired"));
        Assert.IsFalse(store.Deactivate(request.RuleId));
    }

    [TestMethod]
    public void ScopeOrdinalAndStableHashMustAllMatch()
    {
        var store = Enabled();
        var request = Request(
            "scope",
            ordinal: 2,
            stableIdHash: LiveFaultIds.Hash("stable-1"));
        store.Activate(request);

        Assert.IsNull(store.TryApply(
            request.Direction, "other", request.SourceDevice!, request.TargetAccount!,
            request.TargetDevice, request.Kind!, "stable-1"));
        Assert.IsNull(store.TryApply(
            LiveFaultDirection.Inbound, request.SourceAccount, request.SourceDevice!, request.TargetAccount!,
            request.TargetDevice, request.Kind!, "stable-1"));
        Assert.IsNull(store.TryApply(
            request.Direction, request.SourceAccount, request.SourceDevice!, request.TargetAccount!,
            request.TargetDevice, request.Kind!, "wrong-id"));
        Assert.IsNull(Apply(store, request));
        Assert.IsNotNull(Apply(store, request));
        Assert.IsNull(Apply(store, request));

        var status = store.Get(request.RuleId)!;
        Assert.AreEqual(2, status.ObservedMatches);
        Assert.AreEqual(1, status.UseCount);
    }

    [TestMethod]
    public async Task ConcurrentOneShotIsConsumedExactlyOnce()
    {
        var store = Enabled();
        var request = Request("concurrent");
        store.Activate(request);
        using var ready = new ManualResetEventSlim();

        var attempts = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            ready.Wait();
            return Apply(store, request);
        })).ToArray();
        ready.Set();
        var results = await Task.WhenAll(attempts);

        Assert.AreEqual(1, results.Count(result => result is not null));
        Assert.AreEqual(1, store.Get(request.RuleId)!.UseCount);
    }

    [TestMethod]
    public async Task ConcurrentEquivalentActivationsNormalizeBeforeIdempotencyComparison()
    {
        var store = Enabled();
        var request = Request("normalized", stableIdHash: LiveFaultIds.Hash("stable-1"));
        var equivalent = request with
        {
            RuleId = " normalized ",
            SourceAccount = " @ALICE ",
            SourceDevice = $" {request.SourceDevice!.ToUpperInvariant()} ",
            TargetAccount = " BOB ",
            TargetDevice = $" {request.TargetDevice.ToUpperInvariant()} ",
            Kind = $" {request.Kind!.ToUpperInvariant()} ",
            StableIdHash = $" {request.StableIdHash!.ToUpperInvariant()} "
        };
        using var ready = new ManualResetEventSlim();
        var activations = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            ready.Wait();
            return store.Activate(index % 2 == 0 ? request : equivalent);
        })).ToArray();

        ready.Set();
        var statuses = await Task.WhenAll(activations);

        Assert.IsTrue(statuses.All(status => status == statuses[0]));
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.Audit().Count(entry => entry.Event == "activated"));
        Assert.AreEqual("alice", statuses[0].SourceAccount);
        Assert.AreEqual("bob", statuses[0].TargetAccount);
        Assert.AreEqual(request.TargetDevice, statuses[0].TargetDevice);
        Assert.AreEqual(request.StableIdHash, statuses[0].StableIdHash);
    }

    [TestMethod]
    public void NormalizedDeliveryInputsMatchCanonicalRule()
    {
        var store = Enabled();
        var request = Request("delivery-normalized", stableIdHash: LiveFaultIds.Hash("stable-1"));
        store.Activate(request);

        var decision = store.TryApply(
            request.Direction,
            " @ALICE ",
            $" {request.SourceDevice!.ToUpperInvariant()} ",
            " BOB ",
            $" {request.TargetDevice.ToUpperInvariant()} ",
            $" {request.Kind!.ToUpperInvariant()} ",
            "stable-1");

        Assert.IsNotNull(decision);
        Assert.AreEqual(1, store.Get(request.RuleId)!.UseCount);
    }

    [TestMethod]
    public void AmbiguousUnicodeControlAndPathInputsAreRejected()
    {
        var store = Enabled();
        var request = Request("invalid", stableIdHash: LiveFaultIds.Hash("stable-1"));

        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Activate(request with { SourceAccount = "al\u0131ce" }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Activate(request with { TargetAccount = "bob\nadmin" }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Activate(request with { Kind = "../topic.run.update" }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Activate(request with { TargetDevice = request.TargetDevice + "/" }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Activate(request with { StableIdHash = request.StableIdHash![..63] + "\u00e9" }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Activate(request with { Direction = (LiveFaultDirection)999 }));

        store.Activate(request);
        Assert.IsNull(store.TryApply(
            request.Direction,
            request.SourceAccount,
            request.SourceDevice!,
            request.TargetAccount!,
            request.TargetDevice,
            request.Kind!,
            "../stable-1"));
        Assert.AreEqual(0, store.Get(request.RuleId)!.UseCount);
    }

    [TestMethod]
    public void AuditContainsOnlyMetadataAndHashes()
    {
        const string payload = "super-secret-plaintext-or-ciphertext";
        var store = Enabled();
        var request = Request("audit", stableIdHash: LiveFaultIds.Hash("stable-1"));
        store.Activate(request);
        Apply(store, request);

        var serialized = JsonSerializer.Serialize(store.Audit());
        Assert.IsFalse(serialized.Contains(payload, StringComparison.Ordinal));
        StringAssert.Contains(serialized, request.StableIdHash!);
    }

    private static LiveFaultStore Enabled(TimeProvider? clock = null)
        => new(new LiveFaultOptions { Enabled = true, MaxTtlSeconds = 3600, MaxUses = 1000 }, clock);

    private static LiveFaultActivationRequest Request(
        string id,
        int ttlSeconds = 60,
        int ordinal = 1,
        string? stableIdHash = null)
    {
        var source = KeyPair.New();
        var target = KeyPair.New();
        return new LiveFaultActivationRequest(
            id,
            LiveFaultMode.SuccessDropBeforeDestination,
            LiveFaultDirection.Outbound,
            "alice",
            DeviceProtocol.DeviceId(target.PublicB64),
            ttlSeconds,
            Ordinal: ordinal,
            SourceDevice: DeviceProtocol.DeviceId(source.PublicB64),
            TargetAccount: "bob",
            Kind: LiveFaultStore.OnlineFrameKind,
            StableIdHash: stableIdHash);
    }

    private static LiveFaultDecision? Apply(LiveFaultStore store, LiveFaultActivationRequest request)
        => store.TryApply(
            request.Direction,
            request.SourceAccount,
            request.SourceDevice!,
            request.TargetAccount!,
            request.TargetDevice,
            request.Kind!,
            request.StableIdHash is null ? "any-stable-id" : "stable-1");
}
