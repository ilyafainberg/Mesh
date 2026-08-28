namespace Mesh.App.Services;

public static class RelayTransportPolicy
{
#if DEBUG
    public const bool LocalHttpEnabled = true;
#else
    public const bool LocalHttpEnabled = false;
#endif

    public static bool IsTransportAllowed(Uri uri, bool allowLocalHttp)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) return false;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;
        return allowLocalHttp
               && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && IsLocalDevelopmentHost(uri);
    }

    public static bool TryValidateBaseUrl(
        string? value,
        bool allowLocalHttp,
        out string error)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Enter a valid HTTPS relay URL without credentials, a query, or a fragment.";
            return false;
        }

        if (IsTransportAllowed(uri, allowLocalHttp))
        {
            error = "";
            return true;
        }

        error = allowLocalHttp
            ? "Relay URLs must use HTTPS. Debug builds allow HTTP only for localhost, loopback IPs, or the Android emulator host."
            : "Relay URLs must use HTTPS in production builds.";
        return false;
    }

    public static void EnsureAllowed(Uri uri)
    {
        if (IsTransportAllowed(uri, LocalHttpEnabled)) return;
        throw new RelayTransportPolicyException(
            LocalHttpEnabled
                ? "The relay must use HTTPS; HTTP is limited to loopback and the Android emulator host in debug builds."
                : "The relay must use HTTPS in this build.");
    }

    private static bool IsLocalDevelopmentHost(Uri uri)
        => uri.IsLoopback
           || uri.Host.Equals("10.0.2.2", StringComparison.OrdinalIgnoreCase);
}

public sealed class RelayTransportPolicyException : InvalidOperationException
{
    public RelayTransportPolicyException(string message) : base(message) { }
}

public sealed class RelayTransportPolicyHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
            RelayTransportPolicy.EnsureAllowed(uri);
        return base.SendAsync(request, cancellationToken);
    }
}
