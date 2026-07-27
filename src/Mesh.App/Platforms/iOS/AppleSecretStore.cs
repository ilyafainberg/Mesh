using System.Security.Cryptography;
using Foundation;
using Mesh.App.Services;
using Security;

namespace Mesh.App.Platforms.iOS;

/// <summary>Keeps SQLCipher keys available after the device's first unlock, including background wakes.</summary>
public sealed class AppleSecretStore : ISecretStore
{
    private const string Prefix = "meshdb-key-";
    private const string Service = "net.meshrelay.mesh.database";
    private const int KeyBytes = 32;

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
        using var query = Query(identityId);
        using var record = SecKeyChain.QueryAsRecord(query, out var status);
        if (status == SecStatusCode.Success)
            return record?.ValueData?.ToArray();
        if (status != SecStatusCode.ItemNotFound)
            throw new InvalidOperationException($"Keychain read failed ({status}).");

        var legacy = ReadLegacyKey(identityId);
        if (legacy is null) return null;
        PutDbKey(identityId, legacy);
        Microsoft.Maui.Storage.SecureStorage.Remove(Prefix + identityId);
        return legacy;
    }

    public void PutDbKey(string identityId, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes)
            throw new ArgumentException("Database keys must be exactly 32 bytes.", nameof(key));

        using var value = NSData.FromArray(key);
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Prefix + identityId,
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
            UseDataProtectionKeychain = true,
            ValueData = value
        };
        var status = SecKeyChain.Add(record);
        if (status == SecStatusCode.DuplicateItem)
        {
            using var query = Query(identityId);
            using var update = new SecRecord
            {
                Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
                ValueData = value
            };
            status = SecKeyChain.Update(query, update);
        }
        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"Keychain write failed ({status}).");
    }

    public void DeleteDbKey(string identityId)
    {
        using var query = Query(identityId);
        var status = SecKeyChain.Remove(query);
        if (status is not SecStatusCode.Success and not SecStatusCode.ItemNotFound)
            throw new InvalidOperationException($"Keychain delete failed ({status}).");
        Microsoft.Maui.Storage.SecureStorage.Remove(Prefix + identityId);
    }

    private static SecRecord Query(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        return new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Prefix + identityId,
            UseDataProtectionKeychain = true
        };
    }

    private static byte[]? ReadLegacyKey(string identityId)
    {
        try
        {
            var value = Task.Run(() => Microsoft.Maui.Storage.SecureStorage.GetAsync(Prefix + identityId))
                .GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(value) ? null : Convert.FromBase64String(value);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("ios-keychain-migration", ex);
            return null;
        }
    }
}
