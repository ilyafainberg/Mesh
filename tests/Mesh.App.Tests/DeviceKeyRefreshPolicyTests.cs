using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceKeyRefreshPolicyTests
{
    [TestMethod]
    public async Task EmptyCachedKeys_RefreshesOnceAndFindsTarget()
    {
        var target = KeyPair.New().PublicB64;
        var refreshes = 0;

        var result = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
            [],
            DeviceProtocol.DeviceId(target),
            () =>
            {
                refreshes++;
                return Task.FromResult(DeviceKeyDirectorySnapshot.FromKeys([target]));
            });

        Assert.IsTrue(result.Refreshed);
        Assert.IsTrue(result.DirectoryAvailable);
        Assert.AreEqual(1, refreshes);
        CollectionAssert.AreEqual(new[] { target }, result.Keys.ToArray());
    }

    [TestMethod]
    public async Task RefreshedKeysStillAbsent_RemainsUnavailableWithoutInlineSpin()
    {
        var other = KeyPair.New().PublicB64;
        var targetId = DeviceProtocol.DeviceId(KeyPair.New().PublicB64);
        var refreshes = 0;

        var result = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
            [other],
            targetId,
            () =>
            {
                refreshes++;
                return Task.FromResult(DeviceKeyDirectorySnapshot.FromKeys([other]));
            });

        Assert.IsTrue(result.Refreshed);
        Assert.IsTrue(result.DirectoryAvailable);
        Assert.AreEqual(1, refreshes);
        Assert.HasCount(0, result.Keys);
    }

    [TestMethod]
    public async Task RefreshFailure_IsNotReportedAsSuccessAndCanRetryLater()
    {
        var target = KeyPair.New().PublicB64;
        var targetId = DeviceProtocol.DeviceId(target);
        var refreshes = 0;

        var failed = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
            [],
            targetId,
            () =>
            {
                refreshes++;
                return Task.FromResult(DeviceKeyDirectorySnapshot.FromKeys([]));
            });
        var retried = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
            [],
            targetId,
            () =>
            {
                refreshes++;
                return Task.FromResult(DeviceKeyDirectorySnapshot.FromKeys([target]));
            });

        Assert.HasCount(0, failed.Keys);
        CollectionAssert.AreEqual(new[] { target }, retried.Keys.ToArray());
        Assert.AreEqual(2, refreshes, "each delivery attempt may force at most one refresh");
    }

    [TestMethod]
    public async Task RefreshException_IsNotConvertedToAResolvedKey()
    {
        var refreshes = 0;

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
                [],
                DeviceProtocol.DeviceId(KeyPair.New().PublicB64),
                () =>
                {
                    refreshes++;
                    throw new HttpRequestException("directory unavailable");
                }));

        Assert.AreEqual(1, refreshes);
    }

    [TestMethod]
    public async Task Resolution_FiltersOnlyTheExactTargetDevice()
    {
        var target = KeyPair.New().PublicB64;
        var other = KeyPair.New().PublicB64;
        var refreshes = 0;

        var result = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
            [other, target, other],
            DeviceProtocol.DeviceId(target),
            () =>
            {
                refreshes++;
                return Task.FromResult(DeviceKeyDirectorySnapshot.FromKeys([]));
            });

        Assert.IsFalse(result.Refreshed);
        Assert.AreEqual(0, refreshes);
        CollectionAssert.AreEqual(new[] { target }, result.Keys.ToArray());
    }

    [TestMethod]
    public async Task RefreshCancellation_PropagatesWithoutReturningKeys()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
                [],
                DeviceProtocol.DeviceId(KeyPair.New().PublicB64),
                () => Task.FromCanceled<DeviceKeyDirectorySnapshot>(cancellation.Token)));
    }

    [TestMethod]
    public async Task UnavailableDirectory_RemainsRetryableInsteadOfTrustingMissingKey()
    {
        var result = await DeviceKeyRefreshPolicy.ResolveForDeviceAsync(
            [],
            DeviceProtocol.DeviceId(KeyPair.New().PublicB64),
            () => Task.FromResult(DeviceKeyDirectorySnapshot.Unavailable));

        Assert.IsTrue(result.Refreshed);
        Assert.IsFalse(result.DirectoryAvailable);
        Assert.HasCount(0, result.Keys);
    }

    [TestMethod]
    public void OwnHandle_UsesAuthoritativeRosterInsteadOfStaleContactPins()
    {
        var oldKey = KeyPair.New().PublicB64;
        var currentKey = KeyPair.New().PublicB64;

        var trusted = DeviceKeyRefreshPolicy.SelectTrustedDirectoryKeys(
            isOwnHandle: true,
            authoritativeKeys: [oldKey, currentKey],
            pinnedContactKeys: [oldKey]);

        CollectionAssert.AreEquivalent(new[] { oldKey, currentKey }, trusted.ToArray());
    }

    [TestMethod]
    public void OtherHandle_PreservesExplicitContactPinning()
    {
        var pinnedKey = KeyPair.New().PublicB64;
        var unverifiedKey = KeyPair.New().PublicB64;

        var trusted = DeviceKeyRefreshPolicy.SelectTrustedDirectoryKeys(
            isOwnHandle: false,
            authoritativeKeys: [pinnedKey, unverifiedKey],
            pinnedContactKeys: [pinnedKey]);

        CollectionAssert.AreEqual(new[] { pinnedKey }, trusted.ToArray());
    }
}
