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
/// <see cref="ISecretStore"/> that keeps each identity's 32-byte SQLCipher key in its own file so
/// N identities coexist for the same Windows user.
///
/// On Windows the key is protected with DPAPI at <see cref="DataProtectionScope.CurrentUser"/>
/// scope and written to <c>{KeysDir}\meshdb-key-{id}.bin</c>. CurrentUser DPAPI is scoped to the
/// Windows USER account, NOT to any file path or app package identity, so the encrypted key
/// survives ApplicationId/publisher changes and reinstalls (the exact orphaning bug this fixes).
/// The key directory itself (<see cref="StoragePaths.KeysDir"/>) is a fixed, app-identity-independent
/// location, so the ciphertext file is never moved out from under the app either.
///
/// On non-Windows platforms DPAPI is unavailable, so the key is kept in the platform secure enclave
/// via MAUI <see cref="Microsoft.Maui.Storage.SecureStorage"/> (Android Keystore, iOS Keychain),
/// exactly as before.
///
/// A process-lifetime in-memory map backs both paths so the app still runs on a headless test host
/// where neither DPAPI nor SecureStorage is available.
/// </summary>
public sealed class SecretStore : ISecretStore
{
    private const string Prefix = "meshdb-key-";
    private const int KeyBytes = 32; // 256-bit SQLCipher key

    private readonly Dictionary<string, byte[]> fallback = new();
    private readonly object gate = new();

    // KeysDir is obtained statically from StoragePaths (the shared source of truth) rather than via
    // a constructor parameter, so DI registration in MauiProgram stays unchanged.
    private static string KeyFilePath(string identityId) =>
        Path.Combine(StoragePaths.KeysDir, $"{Prefix}{identityId}.bin");

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
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var path = KeyFilePath(identityId);
                if (File.Exists(path))
                {
                    var protectedBytes = File.ReadAllBytes(path);
                    var key = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    lock (gate) fallback[identityId] = key;
                    return key;
                }
            }
            catch { /* corrupt/unreadable file, fall back to in-memory */ }

            lock (gate)
                return fallback.TryGetValue(identityId, out var k) ? k : null;
        }

        // Non-Windows: MAUI SecureStorage (secure enclave), with in-memory fallback.
        var name = Prefix + identityId;
        try
        {
            // Offload to the threadpool: SecureStorage is async and AppState is constructed on the
            // UI thread during DI, so a direct .GetAwaiter().GetResult() would deadlock on the
            // captured UI SynchronizationContext.
            var b64 = Task.Run(() => Microsoft.Maui.Storage.SecureStorage.GetAsync(name)).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(b64)) return Convert.FromBase64String(b64);
        }
        catch { /* secure storage unavailable, use fallback */ }

        lock (gate)
            return fallback.TryGetValue(identityId, out var k) ? k : null;
    }

    public void PutDbKey(string identityId, byte[] key)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateDirectory(StoragePaths.KeysDir);
                var protectedBytes = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(KeyFilePath(identityId), protectedBytes);
            }
            catch { /* fall through to in-memory */ }
            lock (gate) fallback[identityId] = key;
            return;
        }

        // Non-Windows: persist to the secure enclave, mirror into the in-memory fallback.
        var name = Prefix + identityId;
        var b64 = Convert.ToBase64String(key);
        try { Task.Run(() => Microsoft.Maui.Storage.SecureStorage.SetAsync(name, b64)).GetAwaiter().GetResult(); }
        catch { /* fall through to in-memory */ }
        lock (gate) fallback[identityId] = key;
    }

    public void DeleteDbKey(string identityId)
    {
        if (OperatingSystem.IsWindows())
        {
            try { var p = KeyFilePath(identityId); if (File.Exists(p)) File.Delete(p); } catch { }
        }
        else
        {
            try { Microsoft.Maui.Storage.SecureStorage.Remove(Prefix + identityId); } catch { }
        }
        lock (gate) fallback.Remove(identityId);
    }
}
