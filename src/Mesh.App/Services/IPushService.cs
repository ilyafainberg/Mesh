namespace Mesh.App.Services;

/// <summary>
/// Cross-platform push notification registration. Unlike <see cref="INotifier"/> (which shows a local
/// toast on the current device), push is about letting a remote server wake a backgrounded phone.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RegisterAsync"/> asks the OS to register the device with its push network (APNs on iOS,
/// FCM on Android) and returns an opaque device token. That token is later handed to the Mesh relay so
/// the relay can target this specific device: when a message arrives while the phone is backgrounded or
/// asleep, the relay uses the token to push a wake-up notification through APNs/FCM.
/// </para>
/// <para>
/// The relay endpoint that receives and stores this token (and the server-side APNs/FCM send logic) is a
/// separate workstream and is not implemented here. This abstraction only covers the client-side handshake
/// with the OS to obtain the token.
/// </para>
/// <para>
/// Platforms without a push implementation (Windows, Mac) use <see cref="NoopPushService"/>, which reports
/// <see cref="IsSupported"/> = false and returns a null token.
/// </para>
/// </remarks>
public interface IPushService
{
    /// <summary>
    /// Ask the OS for a push token (registers with APNs/FCM). Returns the device token, or null if unavailable
    /// (unsupported platform, user denied permission, or credentials/config not yet wired up).
    /// </summary>
    Task<string?> RegisterAsync(CancellationToken ct = default);

    /// <summary>The platform's push capability, for feature checks.</summary>
    bool IsSupported { get; }
}
