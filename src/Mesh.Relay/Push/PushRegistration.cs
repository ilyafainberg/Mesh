namespace Mesh.Relay.Push;

/// <summary>
/// Wires push senders (APNs / FCM) from configuration and registers the <see cref="PushDispatcher"/>.
///
/// All settings are optional. With none configured, no <see cref="IPushSender"/> is registered and the
/// dispatcher is a no-op (<see cref="PushDispatcher.Enabled"/> = false), so the relay behaves exactly as
/// before. Configure APNs and/or FCM to enable Option-1 wake pushes for offline mobile devices.
///
/// APNs (iOS):
///   APNS_KEY_ID       - the 10-char APNs auth-key id (from the Apple developer portal)
///   APNS_TEAM_ID      - your Apple Developer team id
///   APNS_BUNDLE_ID    - the app bundle id (used as apns-topic)
///   APNS_PRIVATE_KEY  - the .p8 PEM contents, or a path to the .p8 file
///   APNS_PRODUCTION   - "true" for production APNs; otherwise the sandbox host is used
///
/// FCM (Android):
///   FCM_SERVICE_ACCOUNT_JSON - the Google service-account JSON, or a path to the .json file
/// </summary>
public static class PushRegistration
{
    public static IServiceCollection AddMeshPush(this IServiceCollection services, IConfiguration cfg)
    {
        // APNs (iOS)
        var apnsKeyId = Read(cfg, "APNS_KEY_ID", "Push:Apns:KeyId");
        var apnsTeamId = Read(cfg, "APNS_TEAM_ID", "Push:Apns:TeamId");
        var apnsBundleId = Read(cfg, "APNS_BUNDLE_ID", "Push:Apns:BundleId");
        var apnsKey = ReadValueOrFile(Read(cfg, "APNS_PRIVATE_KEY", "Push:Apns:PrivateKey"));
        if (!string.IsNullOrWhiteSpace(apnsKeyId) && !string.IsNullOrWhiteSpace(apnsTeamId)
            && !string.IsNullOrWhiteSpace(apnsBundleId) && !string.IsNullOrWhiteSpace(apnsKey))
        {
            var production = string.Equals(
                Read(cfg, "APNS_PRODUCTION", "Push:Apns:Production"), "true", StringComparison.OrdinalIgnoreCase);
            services.AddSingleton<IPushSender>(sp => new ApnsPushSender(
                apnsKeyId!, apnsTeamId!, apnsBundleId!, apnsKey!, production,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ApnsPushSender>()));
        }

        // FCM (Android)
        var fcmJson = ReadValueOrFile(Read(cfg, "FCM_SERVICE_ACCOUNT_JSON", "Push:Fcm:ServiceAccountJson"));
        if (!string.IsNullOrWhiteSpace(fcmJson))
        {
            services.AddSingleton<IPushSender>(sp => new FcmPushSender(
                fcmJson!, sp.GetRequiredService<ILoggerFactory>().CreateLogger<FcmPushSender>()));
        }

        services.AddSingleton<PushDispatcher>();
        return services;
    }

    // A value that may be supplied inline or as a path to a file holding the value.
    private static string? ReadValueOrFile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            if (!value.Contains('\n') && value.Length < 1024 && File.Exists(value))
                return File.ReadAllText(value);
        }
        catch
        {
            // Fall back to treating the value as inline content.
        }
        return value;
    }

    // Environment variable first, then configuration key (mirrors the relay's Config() helper).
    private static string? Read(IConfiguration cfg, string envVar, string configKey)
    {
        var v = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(v) ? v : cfg[configKey];
    }
}
