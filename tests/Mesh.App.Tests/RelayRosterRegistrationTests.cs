using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class RelayRosterRegistrationTests
{
    [TestMethod]
    public async Task LegitimateDeviceRegistration_ReassertsIdempotently()
    {
        var store = new InMemoryRelayStore();
        var key = KeyPair.New().PublicB64;
        var (_, createdAuthorized) = await store.UpsertHandleAsync(
            "alice", key, "Alice", allowNewDevice: true);

        var (reasserted, reassertedAuthorized) = await store.UpsertHandleAsync(
            "alice", key, "Alice", allowNewDevice: false);

        Assert.IsTrue(createdAuthorized);
        Assert.IsTrue(reassertedAuthorized);
        CollectionAssert.AreEqual(new[] { key }, reasserted.DevicePublicKeys);
    }

    [TestMethod]
    public async Task UnlinkedDeviceRegistration_DoesNotEnterAuthoritativeRoster()
    {
        var store = new InMemoryRelayStore();
        var authorizedKey = KeyPair.New().PublicB64;
        var unlinkedKey = KeyPair.New().PublicB64;
        await store.UpsertHandleAsync(
            "alice", authorizedKey, "Alice", allowNewDevice: true);

        var (record, unlinkedAuthorized) = await store.UpsertHandleAsync(
            "alice", unlinkedKey, "Alice", allowNewDevice: false);

        Assert.IsFalse(unlinkedAuthorized);
        CollectionAssert.AreEqual(new[] { authorizedKey }, record.DevicePublicKeys);
    }
}
