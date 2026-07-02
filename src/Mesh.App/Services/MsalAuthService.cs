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
    // Mesh Agent Client, Feincraft tenant, multi-tenant + personal accounts, http://localhost redirect.
    public const string ClientId = "562957d8-0f97-47eb-a445-a93d4a938f5a";
    private const string Authority = "https://login.microsoftonline.com/common";

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

        app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(Authority)
            // Desktop system-browser flow requires a loopback redirect (http://localhost).
            .WithRedirectUri("http://localhost")
            .Build();

        // Persist the token cache across restarts (DPAPI-protected, CurrentUser), so
        // connected Microsoft accounts don't need to re-auth every launch.
        app.UserTokenCache.SetBeforeAccess(OnBeforeAccess);
        app.UserTokenCache.SetAfterAccess(OnAfterAccess);
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
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(FrontWindowOptions());
            var handle = ParentWindow.GetHandle();
            if (handle != IntPtr.Zero)
                builder = builder.WithParentActivityOrWindow(handle);
            var result = await builder.ExecuteAsync(ct);
            BrowserLauncher.CloseAuthWindow();
            Changed?.Invoke();
            return (true, result.Account, null);
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
                    .WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(FrontWindowOptions());
                if (account is not null) builder = builder.WithAccount(account);
                var handle = ParentWindow.GetHandle();
                if (handle != IntPtr.Zero) builder = builder.WithParentActivityOrWindow(handle);
                result = await builder.ExecuteAsync(ct);
                BrowserLauncher.CloseAuthWindow();
            }
            Changed?.Invoke();
            return (true, result.AccessToken, null);
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
}
