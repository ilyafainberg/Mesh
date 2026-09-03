using Mesh.Shared;

namespace Mesh.App.Services;

internal readonly record struct DeviceKeyResolution(
    IReadOnlyList<string> Keys,
    bool Refreshed,
    bool DirectoryAvailable);

internal readonly record struct DeviceKeyDirectorySnapshot(
    bool Available,
    IReadOnlyList<string> Keys)
{
    public static DeviceKeyDirectorySnapshot Unavailable => new(false, Array.Empty<string>());

    public static DeviceKeyDirectorySnapshot FromKeys(IReadOnlyList<string> keys)
        => new(true, keys ?? throw new ArgumentNullException(nameof(keys)));
}

internal static class DeviceKeyRefreshPolicy
{
    public static async Task<DeviceKeyResolution> ResolveForDeviceAsync(
        IReadOnlyList<string> resolvedKeys,
        string targetDeviceId,
        Func<Task<DeviceKeyDirectorySnapshot>> refresh)
    {
        ArgumentNullException.ThrowIfNull(resolvedKeys);
        ArgumentNullException.ThrowIfNull(refresh);

        var selected = Select(resolvedKeys, targetDeviceId);
        if (selected.Count > 0)
            return new DeviceKeyResolution(
                selected,
                Refreshed: false,
                DirectoryAvailable: true);

        var refreshed = await refresh().ConfigureAwait(false);
        return new DeviceKeyResolution(
            Select(refreshed.Keys, targetDeviceId),
            Refreshed: true,
            refreshed.Available);
    }

    public static IReadOnlyList<string> SelectTrustedDirectoryKeys(
        bool isOwnHandle,
        IReadOnlyList<string> authoritativeKeys,
        IReadOnlyList<string> pinnedContactKeys)
    {
        ArgumentNullException.ThrowIfNull(authoritativeKeys);
        ArgumentNullException.ThrowIfNull(pinnedContactKeys);
        return (isOwnHandle ? authoritativeKeys : pinnedContactKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> Select(
        IReadOnlyList<string> keys,
        string targetDeviceId)
        => keys
            .Where(key => string.Equals(
                DeviceProtocol.DeviceId(key),
                targetDeviceId,
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

internal enum DeviceRosterReconciliationState
{
    Converged,
    DirectoryUnavailable,
    RegistrationRejected,
    RegistrationNotConverged
}

internal enum DeviceRosterRegistrationResult
{
    Succeeded,
    Rejected,
    Unavailable
}

internal readonly record struct DeviceRosterReconciliationResult(
    DeviceRosterReconciliationState State,
    int FetchAttempts,
    int RegistrationAttempts)
{
    public bool Converged => State == DeviceRosterReconciliationState.Converged;

    public bool IsTerminal => State is DeviceRosterReconciliationState.RegistrationRejected
        or DeviceRosterReconciliationState.RegistrationNotConverged;

    public string Remediation => State switch
    {
        DeviceRosterReconciliationState.RegistrationRejected
            => "This device is not authorized for the account. Link it again or restore an account backup.",
        DeviceRosterReconciliationState.RegistrationNotConverged
            => "The relay did not publish this device after registration. Check the relay environment, then reconnect.",
        DeviceRosterReconciliationState.DirectoryUnavailable
            => "The relay device directory is unavailable. Check the connection and retry.",
        _ => ""
    };
}

internal static class DeviceRosterReconciliationPolicy
{
    internal const int MaxFetchAttempts = 3;
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(250);

    public static async Task<DeviceRosterReconciliationResult> ReconcileCurrentDeviceAsync(
        string currentPublicKey,
        Func<CancellationToken, Task<IReadOnlyList<string>?>> fetchAuthoritativeKeys,
        Func<CancellationToken, Task<DeviceRosterRegistrationResult>> registerCurrentDevice,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (string.IsNullOrWhiteSpace(currentPublicKey))
            return new DeviceRosterReconciliationResult(
                DeviceRosterReconciliationState.RegistrationRejected, 0, 0);
        ArgumentNullException.ThrowIfNull(fetchAuthoritativeKeys);
        ArgumentNullException.ThrowIfNull(registerCurrentDevice);
        delay ??= static (duration, ct) => Task.Delay(duration, ct);

        var fetchAttempts = 0;
        IReadOnlyList<string>? keys = null;
        for (var attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fetchAttempts++;
            keys = await fetchAuthoritativeKeys(cancellationToken).ConfigureAwait(false);
            if (keys is not null) break;
            if (attempt + 1 < MaxFetchAttempts)
                await delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
        }

        if (keys is null)
            return new DeviceRosterReconciliationResult(
                DeviceRosterReconciliationState.DirectoryUnavailable, fetchAttempts, 0);
        if (ContainsExact(keys, currentPublicKey))
            return new DeviceRosterReconciliationResult(
                DeviceRosterReconciliationState.Converged, fetchAttempts, 0);

        var registration = await registerCurrentDevice(cancellationToken).ConfigureAwait(false);
        if (registration == DeviceRosterRegistrationResult.Rejected)
            return new DeviceRosterReconciliationResult(
                DeviceRosterReconciliationState.RegistrationRejected, fetchAttempts, 1);
        if (registration == DeviceRosterRegistrationResult.Unavailable)
            return new DeviceRosterReconciliationResult(
                DeviceRosterReconciliationState.DirectoryUnavailable, fetchAttempts, 1);

        var sawDirectory = false;
        for (var attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
                await delay(Backoff(attempt - 1), cancellationToken).ConfigureAwait(false);
            fetchAttempts++;
            keys = await fetchAuthoritativeKeys(cancellationToken).ConfigureAwait(false);
            if (keys is null) continue;
            sawDirectory = true;
            if (ContainsExact(keys, currentPublicKey))
                return new DeviceRosterReconciliationResult(
                    DeviceRosterReconciliationState.Converged, fetchAttempts, 1);
        }

        return new DeviceRosterReconciliationResult(
            sawDirectory
                ? DeviceRosterReconciliationState.RegistrationNotConverged
                : DeviceRosterReconciliationState.DirectoryUnavailable,
            fetchAttempts,
            1);
    }

    private static bool ContainsExact(IReadOnlyList<string> keys, string currentPublicKey)
        => keys.Any(key => string.Equals(key, currentPublicKey, StringComparison.Ordinal));

    private static TimeSpan Backoff(int zeroBasedAttempt)
        => TimeSpan.FromMilliseconds(
            InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Clamp(zeroBasedAttempt, 0, 2)));
}
