using System.Security.Cryptography;
using System.Text;

namespace Mesh.App.Services;

/// <summary>
/// Stateless helper for device identity: an ECDSA P-256 keypair whose public key
/// is the device's identity. Keys are generated locally and never leave the device.
/// </summary>
public static class IdentityService
{
    public static (string privateKeyB64, string publicKeyB64) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = ec.ExportPkcs8PrivateKey();
        var pub = ec.ExportSubjectPublicKeyInfo();
        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
    }

    public static string Sign(string privateKeyB64, string message)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        var sig = ec.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(sig);
    }

    public static bool Verify(string publicKeyB64, string message, string signatureB64)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(message),
                Convert.FromBase64String(signatureB64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}
