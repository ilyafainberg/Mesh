namespace Mesh.App.Services;

/// <summary>
/// Single source of truth for where Mesh stores its per-identity data and keys.
///
/// The root is resolved ONCE, and is deliberately independent of the app package identity
/// (ApplicationId / publisher) on Windows. Historically MAUI's
/// <see cref="Microsoft.Maui.Storage.FileSystem.AppDataDirectory"/> resolved to
/// <c>%LOCALAPPDATA%\User Name\{ApplicationId}\</c>, so changing the ApplicationId or reinstalling
/// moved the whole data root and orphaned every identity plus its keys. Anchoring the root to a
/// fixed, user-scoped path (<c>%LOCALAPPDATA%\Mesh</c>) makes data survive those changes.
///
/// Layout under <see cref="Root"/>:
///   Data\  -> accounts.json + identity-{id}.meshdb (SQLCipher databases)
///   Keys\  -> meshdb-key-{id}.bin (DPAPI CurrentUser-protected SQLCipher keys)
/// </summary>
public static class StoragePaths
{
    /// <summary>The resolved storage root. See class remarks for resolution order.</summary>
    public static string Root { get; }

    /// <summary>Directory holding accounts.json and the per-identity .meshdb databases.</summary>
    public static string DataDir { get; }

    /// <summary>Directory holding the per-identity DPAPI-protected key files.</summary>
    public static string KeysDir { get; }

    static StoragePaths()
    {
        Root = ResolveRoot();
        DataDir = Path.Combine(Root, "Data");
        KeysDir = Path.Combine(Root, "Keys");

        // Create eagerly and defensively: never throw from the static initializer, otherwise a
        // headless test host that touches any member would fail with a TypeInitializationException.
        TryCreate(Root);
        TryCreate(DataDir);
        TryCreate(KeysDir);
    }

    private static string ResolveRoot()
    {
        // a) Explicit override wins (used by tests and for isolating parallel instances).
        var overrideDir = Environment.GetEnvironmentVariable("MESH_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;

        // b) On Windows use a FIXED, app-identity-independent, user-scoped path so data survives
        //    ApplicationId/publisher changes and reinstalls: %LOCALAPPDATA%\Mesh.
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mesh");

        // c) Other platforms (Android/iOS/MacCatalyst) already have a stable per-app sandbox.
        try
        {
            var appData = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
            if (!string.IsNullOrWhiteSpace(appData))
                return appData;
        }
        catch
        {
            // FileSystem may be unavailable in a headless test host; fall through to a temp dir.
        }

        return Path.Combine(Path.GetTempPath(), "Mesh");
    }

    private static void TryCreate(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* best effort */ }
    }
}
