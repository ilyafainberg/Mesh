using Microsoft.Identity.Client;

namespace Mesh.App.Services;

/// <summary>
/// Handles "Sign in with Microsoft" for the user, using the Mesh public client
/// app registration. Supports multiple accounts (work/school AND personal), each
/// addressed by its stable home-account id. Tokens are cached in-memory by MSAL
/// and never sent to the relay.
/// </summary>
public sealed class MsalAuthService
{
    // Mesh Agent Client, multi-tenant + personal accounts, http://localhost redirect.
    public const string ClientId = "562957d8-0f97-47eb-a445-a93d4a938f5a";
    private const string Authority = "https://login.microsoftonline.com/common";
    public const string AndroidRedirectUri = "msal562957d8-0f97-47eb-a445-a93d4a938f5a://auth";
    public const string IosRedirectUri = "msauth.net.meshrelay.mesh://auth";
    public static string MobileRedirectUri => OperatingSystem.IsIOS() ? IosRedirectUri : AndroidRedirectUri;

    // The well-known tenant id used by consumer (personal) Microsoft accounts.
    private const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    // Work/school accounts get Teams + files + sites; personal accounts get mail + files.
    public static readonly string[] WorkScopes = { "User.Read", "Mail.Read", "Chat.Read", "Files.Read.All", "Sites.Read.All" };
    public static readonly string[] PersonalScopes = { "User.Read", "Mail.Read", "Files.Read.All" };

    private readonly IPublicClientApplication app;
    private readonly string cacheFile;
    private static readonly object CacheLock = new();

    public MsalAuthService()
    {
        var dir = Environment.GetEnvironmentVariable("MESH_PROFILE_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
        Directory.CreateDirectory(dir);
        cacheFile = Path.Combine(dir, "msal-cache.bin");

        var builder = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(Authority)
            .WithRedirectUri(IsMobile ? MobileRedirectUri : "http://localhost");
#if ANDROID
        builder = builder.WithParentActivityOrWindow(() => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity!);
#elif IOS
        builder = builder
            .WithParentActivityOrWindow(() => Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController())
            .WithIosKeychainSecurityGroup("com.microsoft.adalcache");
#endif
        app = builder.Build();

        // Persist the token cache across restarts (DPAPI-protected, CurrentUser), so
        // connected Microsoft accounts don't need to re-auth every launch. This custom cache
        // serialization is only supported on desktop: on mobile (Android/iOS/MacCatalyst) MSAL
        // manages and persists the cache itself via platform secure storage, and calling
        // SetBeforeAccess/SetAfterAccess throws (TokenCache.Validate), so we skip it there.
        if (OperatingSystem.IsWindows())
        {
            app.UserTokenCache.SetBeforeAccess(OnBeforeAccess);
            app.UserTokenCache.SetAfterAccess(OnAfterAccess);
        }
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        lock (CacheLock)
        {
            try
            {
                if (!File.Exists(cacheFile)) return;
                var stored = File.ReadAllBytes(cacheFile);
                var plain = OperatingSystem.IsWindows()
                    ? System.Security.Cryptography.ProtectedData.Unprotect(stored, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)
                    : stored;
                args.TokenCache.DeserializeMsalV3(plain);
            }
            catch { /* start with an empty cache on any problem */ }
        }
    }

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;
        lock (CacheLock)
        {
            try
            {
                var plain = args.TokenCache.SerializeMsalV3();
                var toStore = OperatingSystem.IsWindows()
                    ? System.Security.Cryptography.ProtectedData.Protect(plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)
                    : plain;
                File.WriteAllBytes(cacheFile, toStore);
            }
            catch { /* best-effort persistence */ }
        }
    }

    public event Action? Changed;

    public static bool IsPersonal(IAccount account)
        => account.HomeAccountId?.TenantId == ConsumerTenantId;

    /// <summary>No-op initializer kept for launch wiring; accounts are resolved on demand.</summary>
    public async Task InitializeAsync()
    {
        await app.GetAccountsAsync();
        Changed?.Invoke();
    }

    /// <summary>
    /// Interactively signs in and returns the chosen account. Always prompts for
    /// account selection so the user can add a second (or different) account.
    /// </summary>
    public async Task<(bool ok, IAccount? account, string? error)> SignInInteractiveAsync(string[] scopes, CancellationToken ct = default)
    {
        try
        {
            var builder = app.AcquireTokenInteractive(scopes)
                .WithPrompt(Prompt.SelectAccount);
            builder = ConfigureInteractive(builder);
            var result = await builder.ExecuteAsync(ct);
            BrowserLauncher.CloseAuthWindow();
            Changed?.Invoke();
            return (true, result.Account, null);
        }
        catch (MsalClientException ex)
        {
            BrowserLauncher.CloseAuthWindow();
            return (false, null, FormatMsalError(ex));
        }
        catch (Exception ex)
        {
            BrowserLauncher.CloseAuthWindow();
            return (false, null, ex.Message);
        }
    }

    /// <summary>Opens MSAL's sign-in in a dedicated front-most window (not a background tab).</summary>
    private static SystemWebViewOptions FrontWindowOptions() => new()
    {
        OpenBrowserAsync = uri => BrowserLauncher.OpenAsync(uri.AbsoluteUri),
        HtmlMessageSuccess = BrowserLauncher.SuccessHtml("Signed in. Returning to Mesh…")
    };

    /// <summary>
    /// Acquires a token for a specific account (by home-account id). Tries silent
    /// first, then falls back to interactive for that account.
    /// </summary>
    public async Task<(bool ok, string? token, string? error)> GetTokenAsync(
        string? accountId, string[] scopes, CancellationToken ct = default)
    {
        try
        {
            var accounts = await app.GetAccountsAsync();
            var account = accountId is null
                ? accounts.FirstOrDefault()
                : accounts.FirstOrDefault(a => a.HomeAccountId?.Identifier == accountId) ?? accounts.FirstOrDefault();

            AuthenticationResult result;
            try
            {
                result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                var builder = app.AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false);
                if (account is not null) builder = builder.WithAccount(account);
                builder = ConfigureInteractive(builder);
                result = await builder.ExecuteAsync(ct);
                BrowserLauncher.CloseAuthWindow();
            }
            Changed?.Invoke();
            return (true, result.AccessToken, null);
        }
        catch (MsalClientException ex)
        {
            BrowserLauncher.CloseAuthWindow();
            return (false, null, FormatMsalError(ex));
        }
        catch (Exception ex)
        {
            BrowserLauncher.CloseAuthWindow();
            return (false, null, ex.Message);
        }
    }

    public async Task RemoveAccountAsync(string? accountId)
    {
        var accounts = await app.GetAccountsAsync();
        foreach (var acc in accounts.Where(a => accountId is null || a.HomeAccountId?.Identifier == accountId))
            await app.RemoveAsync(acc);
        Changed?.Invoke();
    }

    private static bool IsMobile => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    private static string FormatMsalError(MsalClientException ex)
        => IsMobile
            ? $"Microsoft mobile sign-in could not complete. Confirm the app registration includes redirect URI {MobileRedirectUri}. Details: {ex.Message}"
            : ex.Message;

    private static AcquireTokenInteractiveParameterBuilder ConfigureInteractive(
        AcquireTokenInteractiveParameterBuilder builder)
    {
        builder = builder.WithUseEmbeddedWebView(false);
        if (IsMobile)
        {
#if ANDROID
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null) builder = builder.WithParentActivityOrWindow(activity);
#elif IOS
            var controller = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();
            if (controller is not null) builder = builder.WithParentActivityOrWindow(controller);
#endif
            return builder;
        }

        builder = builder.WithSystemWebViewOptions(FrontWindowOptions());
        var handle = ParentWindow.GetHandle();
        return handle == IntPtr.Zero ? builder : builder.WithParentActivityOrWindow(handle);
    }
}
