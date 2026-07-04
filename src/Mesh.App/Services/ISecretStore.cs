using System.Security.Cryptography;

namespace Mesh.App.Services;

/// <summary>
/// Holds the per-identity master key that encrypts that identity's SQLCipher database.
/// The key never leaves the device: it lives in the platform secure enclave (Windows DPAPI,
/// iOS Keychain, Android Keystore) via MAUI <see cref="Microsoft.Maui.Storage.SecureStorage"/>.
/// A moved profile is re-keyed on the new device (import generates a fresh master key), so the
/// database key is not portable, only the passphrase-wrapped export is.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the identity's 32-byte database key, creating and persisting one if absent.</summary>
    byte[] GetOrCreateDbKey(string identityId);

    /// <summary>Returns the identity's database key, or null when none has been stored yet.</summary>
    byte[]? GetDbKey(string identityId);

    /// <summary>Stores a database key for an identity (used by import to adopt a freshly generated key).</summary>
    void PutDbKey(string identityId, byte[] key);

    /// <summary>Removes an identity's database key (used when deleting an account).</summary>
    void DeleteDbKey(string identityId);
}

/// <summary>
/// <see cref="ISecretStore"/> backed by MAUI SecureStorage. Keys are stored base64 under
/// <c>meshdb-key-{id}</c>. If the platform secure store is unavailable (for example a headless
/// test host), it falls back to a process-lifetime in-memory map so the app still runs.
/// </summary>
public sealed class SecretStore : ISecretStore
{
    private const string Prefix = "meshdb-key-";
    private const int KeyBytes = 32; // 256-bit SQLCipher key

    private readonly Dictionary<string, byte[]> fallback = new();
    private readonly object gate = new();

    public byte[] GetOrCreateDbKey(string identityId)
    {
        var existing = GetDbKey(identityId);
        if (existing is not null) return existing;
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        PutDbKey(identityId, key);
        return key;
    }

    public byte[]? GetDbKey(string identityId)
    {
        var name = Prefix + identityId;
        try
        {
            var b64 = Microsoft.Maui.Storage.SecureStorage.GetAsync(name).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(b64)) return Convert.FromBase64String(b64);
        }
        catch { /* secure storage unavailable, use fallback */ }

        lock (gate)
            return fallback.TryGetValue(identityId, out var k) ? k : null;
    }

    public void PutDbKey(string identityId, byte[] key)
    {
        var name = Prefix + identityId;
        var b64 = Convert.ToBase64String(key);
        try { Microsoft.Maui.Storage.SecureStorage.SetAsync(name, b64).GetAwaiter().GetResult(); }
        catch { /* fall through to in-memory */ }
        lock (gate) fallback[identityId] = key;
    }

    public void DeleteDbKey(string identityId)
    {
        try { Microsoft.Maui.Storage.SecureStorage.Remove(Prefix + identityId); } catch { }
        lock (gate) fallback.Remove(identityId);
    }
}
