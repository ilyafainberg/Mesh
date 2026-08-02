using Mesh.Relay.Storage;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;

namespace Mesh.App.Tests;

[TestClass]
public sealed class CustodyBootstrapTests
{
    [TestMethod]
    public async Task NewHandle_Persists_Signed_Genesis_Authority()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = Convert.ToBase64String(ec.ExportPkcs8PrivateKey());
        var publicKey = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        var authority = OnlineReplicationProtocol.CreateCustodyEntry(
            "alice",
            0,
            OnlineReplicationProtocol.ZeroHash,
            CustodyAction.Genesis,
            publicKey,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            publicKey,
            privateKey);
        var store = new InMemoryRelayStore();

        var (created, authorized) = await store.UpsertHandleAsync(
            "alice", publicKey, "Alice", allowNewDevice: true, initialCustodyAuthority: authority);

        Assert.IsTrue(authorized);
        Assert.AreEqual(0, created.AuthGeneration);
        Assert.AreEqual(authority.EntryHash, created.CustodyHead);
        Assert.AreEqual(authority, created.CustodyAuthority);
    }
}
