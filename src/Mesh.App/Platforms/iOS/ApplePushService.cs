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
/// <para>
/// TODO: Wire AppDelegate.RegisteredForRemoteNotifications to call ApplePushService.CompleteRegistration(token).
/// Needs an Apple Developer account + APNs auth key + entitlements (aps-environment). The AppDelegate is not
/// edited here because those entitlements and code signing must be set up by a human first; this class only
/// provides the hook the AppDelegate calls.
/// </para>
/// <para>
/// The returned token is later sent to the Mesh relay so the relay can wake a backgrounded phone when a message
/// arrives. That relay endpoint is a separate workstream.
/// </para>
/// </remarks>
public sealed class ApplePushService : IPushService
{
    private static TaskCompletionSource<string?> registration =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly object gate = new();

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <summary>
    /// Ask iOS to register with APNs and request user authorization for notifications. The actual token is
    /// delivered asynchronously by the AppDelegate, which must call <see cref="CompleteRegistration"/>.
    /// </summary>
    [global::System.Runtime.Versioning.SupportedOSPlatform("ios")]
    public Task<string?> RegisterAsync(CancellationToken ct = default)
    {
#if IOS
        TaskCompletionSource<string?> pending;
        lock (gate)
        {
            if (registration.Task.IsCompleted)
                registration = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = registration;
        }

        ct.Register(() => pending.TrySetCanceled(ct));

        // Kick off the OS handshake on the UI thread. The token itself arrives later in the AppDelegate.
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var center = UNUserNotificationCenter.Current;
            center.RequestAuthorization(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound,
                (granted, error) =>
                {
                    if (!granted)
                    {
                        // User declined: no token will ever arrive, so unblock the caller with null.
                        FailRegistration();
                        return;
                    }

                    UIApplication.SharedApplication.InvokeOnMainThread(() =>
                        UIApplication.SharedApplication.RegisterForRemoteNotifications());
                });
        });

        return pending.Task;
#else
        // Harmless fallback if this file is ever compiled for a non-iOS target.
        return Task.FromResult<string?>(null);
#endif
    }

    /// <summary>
    /// Called by the AppDelegate's RegisteredForRemoteNotifications callback with the APNs device token
    /// (hex string) once iOS has issued it.
    /// </summary>
    public static void CompleteRegistration(string token)
    {
        lock (gate)
        {
            registration.TrySetResult(token);
        }
    }

    /// <summary>
    /// Called by the AppDelegate's FailedToRegisterForRemoteNotifications callback (or when the user denies
    /// authorization) to unblock any pending <see cref="RegisterAsync"/> with a null token.
    /// </summary>
    public static void FailRegistration()
    {
        lock (gate)
        {
            registration.TrySetResult(null);
        }
    }
}
