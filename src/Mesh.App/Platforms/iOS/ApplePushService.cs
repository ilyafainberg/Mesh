using Mesh.App.Services;
#if IOS
using Foundation;
using UIKit;
using UserNotifications;
#endif

namespace Mesh.App.Platforms.iOS;

/// <summary>
/// iOS APNs (Apple Push Notification service) device-token registration scaffold.
/// </summary>
/// <remarks>
/// <para>
/// On iOS, the device token is not returned synchronously from the registration call. Instead the app asks
/// the OS to register, then the token (or a failure) is delivered later via the AppDelegate callbacks
/// (RegisteredForRemoteNotifications / FailedToRegisterForRemoteNotifications). To bridge that async, OS-driven
/// callback back to an awaitable, this class exposes a static <see cref="TaskCompletionSource{TResult}"/> that
/// the AppDelegate completes through <see cref="CompleteRegistration"/> or <see cref="FailRegistration"/>.
/// </para>
/// The result is shared across concurrent relay reconnects so iOS sees one authorization and token request at a
/// time. Once issued, the token is cached for the process lifetime and registered with the Mesh relay.
/// </remarks>
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
    /// Ask iOS to register with APNs and request user authorization for notifications. The actual token is
    /// delivered asynchronously by the AppDelegate, which must call <see cref="CompleteRegistration"/>.
    /// </summary>
    [global::System.Runtime.Versioning.SupportedOSPlatform("ios")]
    public async Task<string?> RegisterAsync(CancellationToken ct = default)
    {
#if IOS
        TaskCompletionSource<string?> pending;
        var startRegistration = false;
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(cachedToken)) return cachedToken;
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
            BeginRegistration();
            _ = TimeoutRegistrationAsync(pending);
        }
        return await pending.Task.WaitAsync(ct);
#else
        // Harmless fallback if this file is ever compiled for a non-iOS target.
        return null;
#endif
    }

#if IOS
    private static void BeginRegistration()
    {
        RuntimeDiagnostics.Current?.RecordEvent("apns-registration", "starting");
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
                            if (settings.AuthorizationStatus == UNAuthorizationStatus.Denied)
                            {
                                FailRegistration("Notification permission is denied.");
                                return;
                            }

                            if (settings.AuthorizationStatus == UNAuthorizationStatus.NotDetermined)
                            {
                                RequestAuthorization();
                                return;
                            }

                            RequestDeviceToken();
                        }
                        catch (Exception ex)
                        {
                            RuntimeDiagnostics.Current?.RecordException("apns-settings-callback", ex);
                            FailRegistration("Could not read notification settings.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Current?.RecordException("apns-settings-request", ex);
                    FailRegistration("Could not request notification settings.");
                }
            });
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("apns-main-thread-dispatch", ex);
            FailRegistration("Could not start APNs registration.");
        }
    }

    private static void RequestAuthorization()
    {
        UNUserNotificationCenter.Current.RequestAuthorization(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound,
            (granted, error) =>
            {
                try
                {
                    if (error is not null)
                    {
                        FailRegistration(error.LocalizedDescription);
                        return;
                    }
                    if (!granted)
                    {
                        FailRegistration("Notification permission was not granted.");
                        return;
                    }
                    RequestDeviceToken();
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Current?.RecordException("apns-authorization-callback", ex);
                    FailRegistration("APNs authorization callback failed.");
                }
            });
    }

    private static void RequestDeviceToken()
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

    private static async Task TimeoutRegistrationAsync(TaskCompletionSource<string?> pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(RegistrationTimeoutSeconds));
        lock (gate)
        {
            if (!ReferenceEquals(registration, pending) || pending.Task.IsCompleted) return;
            registrationStarted = false;
            pending.TrySetResult(null);
        }
        RuntimeDiagnostics.Current?.RecordEvent("apns-registration", "timed out");
    }
#endif

    /// <summary>
    /// Called by the AppDelegate's RegisteredForRemoteNotifications callback with the APNs device token
    /// (hex string) once iOS has issued it.
    /// </summary>
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

    /// <summary>
    /// Called by the AppDelegate's FailedToRegisterForRemoteNotifications callback (or when the user denies
    /// authorization) to unblock any pending <see cref="RegisterAsync"/> with a null token.
    /// </summary>
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
