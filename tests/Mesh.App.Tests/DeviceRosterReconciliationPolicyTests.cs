using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceRosterReconciliationPolicyTests
{
    [TestMethod]
    public async Task MissingCurrentKey_SupportedRegistrationConverges()
    {
        var currentKey = KeyPair.New().PublicB64;
        IReadOnlyList<string> roster = [KeyPair.New().PublicB64];
        var registrations = 0;

        var result = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            currentKey,
            _ => Task.FromResult<IReadOnlyList<string>?>(roster),
            _ =>
            {
                registrations++;
                roster = [.. roster, currentKey];
                return Task.FromResult(DeviceRosterRegistrationResult.Succeeded);
            },
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(DeviceRosterReconciliationState.Converged, result.State);
        Assert.AreEqual(1, registrations);
        Assert.AreEqual(2, result.FetchAttempts);
        Assert.AreEqual(1, result.RegistrationAttempts);
    }

    [TestMethod]
    public async Task CurrentKeyAlreadyRegistered_IsIdempotentAndDoesNotUpsertAgain()
    {
        var currentKey = KeyPair.New().PublicB64;
        var registrations = 0;

        var result = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            currentKey,
            _ => Task.FromResult<IReadOnlyList<string>?>([currentKey]),
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Succeeded);
            },
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(DeviceRosterReconciliationState.Converged, result.State);
        Assert.AreEqual(0, registrations);
        Assert.AreEqual(1, result.FetchAttempts);
    }

    [TestMethod]
    public async Task UnauthorizedKey_IsRejectedWithoutTrustingItOrRetryingRegistration()
    {
        var currentKey = KeyPair.New().PublicB64;
        var registrations = 0;

        var result = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            currentKey,
            _ => Task.FromResult<IReadOnlyList<string>?>([KeyPair.New().PublicB64]),
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Rejected);
            },
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(DeviceRosterReconciliationState.RegistrationRejected, result.State);
        Assert.IsTrue(result.IsTerminal);
        Assert.AreEqual(1, registrations);
        Assert.AreEqual(1, result.FetchAttempts);
        Assert.IsFalse(result.Converged);
    }

    [TestMethod]
    public async Task DirectoryFailure_IsBoundedAndDoesNotAttemptRegistration()
    {
        var fetches = 0;
        var registrations = 0;
        var delays = new List<TimeSpan>();

        var result = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            KeyPair.New().PublicB64,
            _ =>
            {
                fetches++;
                return Task.FromResult<IReadOnlyList<string>?>(null);
            },
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Succeeded);
            },
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        Assert.AreEqual(DeviceRosterReconciliationState.DirectoryUnavailable, result.State);
        Assert.IsFalse(result.IsTerminal);
        Assert.AreEqual(3, fetches);
        Assert.AreEqual(0, registrations);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) },
            delays);
    }

    [TestMethod]
    public async Task RegistrationAcceptedButRosterNeverConverges_HasBoundedTerminalFailure()
    {
        var fetches = 0;
        var registrations = 0;

        var result = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            KeyPair.New().PublicB64,
            _ =>
            {
                fetches++;
                return Task.FromResult<IReadOnlyList<string>?>([]);
            },
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Succeeded);
            },
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(DeviceRosterReconciliationState.RegistrationNotConverged, result.State);
        Assert.IsTrue(result.IsTerminal);
        Assert.AreEqual(4, fetches);
        Assert.AreEqual(1, registrations);
    }

    [TestMethod]
    public async Task RegistrationTransportFailure_RemainsRetryableWithoutInlineStorm()
    {
        var fetches = 0;
        var registrations = 0;

        var result = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            KeyPair.New().PublicB64,
            _ =>
            {
                fetches++;
                return Task.FromResult<IReadOnlyList<string>?>([]);
            },
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Unavailable);
            },
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(DeviceRosterReconciliationState.DirectoryUnavailable, result.State);
        Assert.IsFalse(result.IsTerminal);
        Assert.AreEqual(1, fetches);
        Assert.AreEqual(1, registrations);
    }

    [TestMethod]
    public async Task RestartAfterTransientFailure_RecoversWithoutDuplicateRegistration()
    {
        var currentKey = KeyPair.New().PublicB64;
        var available = false;
        var registrations = 0;

        Task<IReadOnlyList<string>?> Fetch(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<string>?>(
                available ? [currentKey] : null);

        var beforeRestart = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            currentKey,
            Fetch,
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Succeeded);
            },
            delay: static (_, _) => Task.CompletedTask);

        available = true;
        var afterRestart = await DeviceRosterReconciliationPolicy.ReconcileCurrentDeviceAsync(
            currentKey,
            Fetch,
            _ =>
            {
                registrations++;
                return Task.FromResult(DeviceRosterRegistrationResult.Succeeded);
            },
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(DeviceRosterReconciliationState.DirectoryUnavailable, beforeRestart.State);
        Assert.AreEqual(DeviceRosterReconciliationState.Converged, afterRestart.State);
        Assert.AreEqual(0, registrations);
    }
}
