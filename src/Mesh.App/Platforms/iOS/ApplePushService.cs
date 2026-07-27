using Mesh.App.Services;
#if IOS
using Foundation;
using UIKit;
using UserNotifications;
#endif

namespace Mesh.App.Platforms.iOS;

/// <summary>Registers this iOS device with APNs independently of visible-alert permission.</summary>
public sealed class ApplePushService : IPushService
{
    private const int RegistrationTimeoutSeconds = 30;
    private static readonly object gate = new();
    private static TaskCompletionSource<string?>? registration;
    private static string? cachedToken;
    private static bool registrationStarted;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <summary>
    /// Registers for remote notifications even when alert authorization is denied. Alert permission is
    /// requested separately and returned as metadata so the relay can choose an alert or a silent wake.
    /// </summary>
    [global::System.Runtime.Versioning.SupportedOSPlatform("ios")]
    public async Task<PushRegistrationInfo?> RegisterAsync(CancellationToken ct = default)
    {
#if IOS
        var tokenTask = GetDeviceTokenAsync(ct);
        var alertsTask = GetAlertsEnabledAsync(ct);
        var token = await tokenTask.ConfigureAwait(false);
        var alertsEnabled = await alertsTask.ConfigureAwait(false);
        return PushRegistrationPolicy.Create(token, alertsEnabled);
#else
        return null;
#endif
    }

#if IOS
    private static Task<string?> GetDeviceTokenAsync(CancellationToken ct)
    {
        TaskCompletionSource<string?> pending;
        var startRegistration = false;
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(cachedToken))
                return Task.FromResult<string?>(cachedToken);
            if (registration is null || registration.Task.IsCompleted)
                registration = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = registration;
            if (!registrationStarted)
            {
                registrationStarted = true;
                startRegistration = true;
            }
        }

        if (startRegistration)
        {
            RequestDeviceToken();
            _ = TimeoutRegistrationAsync(pending);
        }
        return pending.Task.WaitAsync(ct);
    }

    private static async Task<bool> GetAlertsEnabledAsync(CancellationToken ct)
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            UIApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                try
                {
                    UNUserNotificationCenter.Current.GetNotificationSettings(settings =>
                    {
                        try
                        {
                            if (settings.AuthorizationStatus == UNAuthorizationStatus.NotDetermined)
                            {
                                RequestAuthorization(pending);
                                return;
                            }
                            pending.TrySetResult(IsAlertAuthorized(settings.AuthorizationStatus));
                        }
                        catch (Exception ex)
                        {
                            RuntimeDiagnostics.Current?.RecordException("apns-settings-callback", ex);
                            pending.TrySetResult(false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Current?.RecordException("apns-settings-request", ex);
                    pending.TrySetResult(false);
                }
            });
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("apns-main-thread-dispatch", ex);
            pending.TrySetResult(false);
        }
        return await pending.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private static void RequestAuthorization(TaskCompletionSource<bool> pending)
    {
        UNUserNotificationCenter.Current.RequestAuthorization(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound,
            (granted, error) =>
            {
                try
                {
                    if (error is not null)
                    {
                        RuntimeDiagnostics.Current?.RecordEvent(
                            "apns-authorization", error.LocalizedDescription);
                        pending.TrySetResult(false);
                        return;
                    }
                    pending.TrySetResult(granted);
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Current?.RecordException("apns-authorization-callback", ex);
                    pending.TrySetResult(false);
                }
            });
    }

    private static bool IsAlertAuthorized(UNAuthorizationStatus status)
        => status is UNAuthorizationStatus.Authorized
            or UNAuthorizationStatus.Provisional
            or UNAuthorizationStatus.Ephemeral;

    private static void RequestDeviceToken()
    {
        RuntimeDiagnostics.Current?.RecordEvent("apns-registration", "starting");
        try
        {
            UIApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                try
                {
                    UIApplication.SharedApplication.RegisterForRemoteNotifications();
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Current?.RecordException("apns-device-token-request", ex);
                    FailRegistration("Could not request an APNs device token.");
                }
            });
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("apns-main-thread-dispatch", ex);
            FailRegistration("Could not start APNs registration.");
        }
    }

    private static async Task TimeoutRegistrationAsync(TaskCompletionSource<string?> pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(RegistrationTimeoutSeconds)).ConfigureAwait(false);
        lock (gate)
        {
            if (!ReferenceEquals(registration, pending) || pending.Task.IsCompleted) return;
            registrationStarted = false;
            pending.TrySetResult(null);
        }
        RuntimeDiagnostics.Current?.RecordEvent("apns-registration", "timed out");
    }
#endif

    /// <summary>Completes the pending APNs device-token request.</summary>
    public static void CompleteRegistration(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("APNs returned an empty device token.", nameof(token));
        lock (gate)
        {
            cachedToken = token;
            registrationStarted = false;
            registration?.TrySetResult(token);
        }
        RuntimeDiagnostics.Current?.RecordEvent("apns-registration", "device token received");
    }

    /// <summary>Fails only token registration; visible-alert denial does not call this path.</summary>
    public static void FailRegistration(string? reason = null)
    {
        lock (gate)
        {
            registrationStarted = false;
            registration?.TrySetResult(null);
        }
        if (!string.IsNullOrWhiteSpace(reason))
            RuntimeDiagnostics.Current?.RecordEvent("apns-registration", reason);
    }
}
