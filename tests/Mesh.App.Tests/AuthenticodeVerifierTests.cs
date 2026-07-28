using Mesh.Updater;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AuthenticodeVerifierTests
{
    [TestMethod]
    public void IsTrusted_WindowsSystemBinary_IsAccepted()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Authenticode is Windows-only.");
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "dotnet.exe");
        Assert.IsTrue(File.Exists(path), path);

        var trusted = AuthenticodeVerifier.IsTrusted(path, out var result);

        Assert.IsTrue(trusted, $"WinVerifyTrust returned 0x{result:X8} for {path}.");
    }

    [TestMethod]
    public void IsTrusted_UnsignedFile_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Authenticode is Windows-only.");
        var path = Path.Combine(Path.GetTempPath(), "mesh-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(path, "not a signed executable");
            Assert.IsFalse(AuthenticodeVerifier.IsTrusted(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void HasExpectedPublisher_MatchingCommonNameAndOrganization_IsAccepted()
    {
        using var certificate = CreateCertificate("CN=Feincraft, O=Feincraft, C=BE");

        Assert.IsTrue(AuthenticodeVerifier.HasExpectedPublisher(certificate, "Feincraft"));
    }

    [TestMethod]
    public void HasExpectedPublisher_DifferentOrganization_IsRejected()
    {
        using var certificate = CreateCertificate("CN=Feincraft, O=Other Company, C=BE");

        Assert.IsFalse(AuthenticodeVerifier.HasExpectedPublisher(certificate, "Feincraft"));
    }

    [TestMethod]
    public void HasExpectedPublisher_DifferentCommonName_IsRejected()
    {
        using var certificate = CreateCertificate("CN=Other Product, O=Feincraft, C=BE");

        Assert.IsFalse(AuthenticodeVerifier.HasExpectedPublisher(certificate, "Feincraft"));
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
