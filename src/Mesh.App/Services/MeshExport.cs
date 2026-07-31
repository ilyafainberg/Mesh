using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Portable, passphrase-encrypted profile export used to move an identity to another device.
/// The bundle carries ALL of the user's data (config, contacts, circles, knowledge, skills,
/// widgets, sources, the full chat history, and the handle RECOVERY private key) but NEVER the
/// device signing keys, those are unique per device. On import the new device generates a fresh
/// device keypair and re-authorizes itself under the same handle using the recovery key.
///
/// Format: MAGIC(8) | salt(16) | nonce(12) | tag(16) | ciphertext. The key is derived from the
/// passphrase with Argon2id (memory-hard) and the payload is sealed with AES-256-GCM.
/// </summary>
public static class MeshExport
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MESHEXP1");
    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private sealed class Payload
    {
        public int FormatVersion { get; set; } = 2;
        public MeshProfile Profile { get; set; } = new();
        public List<MeshExportSkillPackage> SkillPackages { get; set; } = new();
    }

    /// <summary>Serializes and encrypts a profile (device keys stripped) into a portable bundle.</summary>
    public static byte[] Create(MeshProfile profile, string passphrase)
        => Create(new MeshExportBundle { Profile = profile }, passphrase);

    public static byte[] Create(MeshExportBundle bundle, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("A passphrase is required to protect the export.", nameof(passphrase));

        // Round-trip clone so we can blank the device keys without touching the live profile.
        var clone = JsonSerializer.Deserialize<MeshProfile>(
            JsonSerializer.Serialize(bundle.Profile, JsonOpts), JsonOpts)!;
        clone.PrivateKey = "";
        clone.PublicKey = "";

        var payload = new Payload
        {
            Profile = clone,
            SkillPackages = bundle.SkillPackages.Select(package => package.Clone()).ToList()
        };
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));

        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var key = DeriveKey(passphrase, salt);

        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, plaintext, cipher, tag);

        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);
        return ms.ToArray();
    }

    /// <summary>
    /// Decrypts a bundle back into a profile. Throws <see cref="CryptographicException"/> when the
    /// passphrase is wrong or the bundle is corrupt, and <see cref="FormatException"/> when it is
    /// not a Mesh export at all.
    /// </summary>
    public static MeshProfile Open(byte[] blob, string passphrase)
        => OpenBundle(blob, passphrase).Profile;

    public static MeshExportBundle OpenBundle(byte[] blob, string passphrase)
    {
        var header = Magic.Length + SaltLen + NonceLen + TagLen;
        if (blob.Length < header || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new FormatException("This file is not a Mesh export.");

        var offset = Magic.Length;
        var salt = blob.AsSpan(offset, SaltLen).ToArray(); offset += SaltLen;
        var nonce = blob.AsSpan(offset, NonceLen).ToArray(); offset += NonceLen;
        var tag = blob.AsSpan(offset, TagLen).ToArray(); offset += TagLen;
        var cipher = blob.AsSpan(offset).ToArray();

        var key = DeriveKey(passphrase, salt);
        var plaintext = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Decrypt(nonce, cipher, tag, plaintext); // throws on wrong passphrase / tamper

        var json = Encoding.UTF8.GetString(plaintext);
        MeshProfile profile;
        List<MeshExportSkillPackage> packages;
        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("formatVersion", out _)
                && document.RootElement.TryGetProperty("profile", out _))
            {
                var payload = JsonSerializer.Deserialize<Payload>(json, JsonOpts)
                    ?? throw new FormatException("The export did not contain a valid payload.");
                profile = payload.Profile;
                packages = payload.SkillPackages ?? new();
            }
            else
            {
                profile = JsonSerializer.Deserialize<MeshProfile>(json, JsonOpts)
                    ?? throw new FormatException("The export did not contain a valid profile.");
                packages = new();
            }
        }
        // Device keys are never carried in an export; a new device mints its own on import.
        profile.PrivateKey = "";
        profile.PublicKey = "";
        return new MeshExportBundle { Profile = profile, SkillPackages = packages };
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = salt,
            DegreeOfParallelism = 4,
            MemorySize = 65536, // 64 MB
            Iterations = 3
        };
        return argon.GetBytes(KeyLen);
    }
}

public sealed class MeshExportBundle
{
    public MeshProfile Profile { get; set; } = new();
    public List<MeshExportSkillPackage> SkillPackages { get; set; } = new();
}

public sealed class MeshExportSkillPackage
{
    public string SkillId { get; set; } = "";
    public SkillPackageManifest Manifest { get; set; } = new();
    public Dictionary<string, byte[]> Files { get; set; } = new(StringComparer.Ordinal);

    internal MeshExportSkillPackage Clone() => new()
    {
        SkillId = SkillId,
        Manifest = JsonSerializer.Deserialize<SkillPackageManifest>(
            JsonSerializer.Serialize(Manifest)) ?? new SkillPackageManifest(),
        Files = Files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal)
    };
}
