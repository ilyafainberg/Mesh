using System.Security.Cryptography;
using System.Text;

namespace Mesh.App.Services;

/// <summary>
/// Encrypts profile data at rest using Windows DPAPI (Data Protection API),
/// scoped to the current Windows user. The ciphertext is bound to this user
/// account on this machine, copying the file elsewhere makes it unreadable.
/// No key to manage, no password prompt.
/// </summary>
public static class ProfileProtector
{
    // Marks a DPAPI-encrypted profile file so we can distinguish it from legacy plaintext JSON.
    private const string Magic = "MESHENC1:";

    // Extra entropy mixed into the DPAPI blob (defense in depth; not a secret).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mesh.Profile.v1");

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Serializes plaintext JSON into the on-disk representation (encrypted where supported).</summary>
    public static string Protect(string json)
    {
        if (!IsSupported) return json; // graceful fallback on non-Windows
        try
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
            return Magic + Convert.ToBase64String(cipher);
        }
        catch { return json; }
    }

    /// <summary>Reads the on-disk representation back to plaintext JSON (handles legacy plaintext too).</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Magic, StringComparison.Ordinal))
            return stored; // legacy plaintext profile, return as-is (will be re-encrypted on next save)

        var b64 = stored[Magic.Length..];
        var cipher = Convert.FromBase64String(b64);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>True if the stored content is already DPAPI-encrypted.</summary>
    public static bool IsProtected(string stored)
        => stored is not null && stored.StartsWith(Magic, StringComparison.Ordinal);
}
