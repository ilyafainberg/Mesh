using System.Security.Cryptography;
using System.Text;

namespace Mesh.Shared;

/// <summary>
/// Shared ECDSA (P-256) verification used by the relay to authenticate handles
/// and validate message signatures. Public keys are base64 SubjectPublicKeyInfo.
/// </summary>
public static class MeshCrypto
{
    public static bool Verify(string publicKeyB64, string message, string signatureB64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyB64) || string.IsNullOrWhiteSpace(signatureB64))
            return false;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(message),
                Convert.FromBase64String(signatureB64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>True if the signature is valid for any of the supplied public keys.</summary>
    public static bool VerifyAny(IEnumerable<string> publicKeys, string message, string signatureB64)
    {
        foreach (var pk in publicKeys)
            if (Verify(pk, message, signatureB64)) return true;
        return false;
    }

    public static string NewNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
}

/// <summary>Frames exchanged during the WebSocket auth handshake.</summary>
public record AuthChallenge(string Nonce)
{
    public string Type { get; init; } = "auth.challenge";
}

public record AuthResponse(string PublicKey, string Signature)
{
    public string Type { get; init; } = "auth.response";
}

public record AuthResult(bool Ok, string? Error = null)
{
    public string Type { get; init; } = "auth.result";
}
