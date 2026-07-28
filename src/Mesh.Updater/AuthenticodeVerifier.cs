using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mesh.Updater
{
    internal static class AuthenticodeVerifier
    {
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        public static bool IsTrusted(string filePath, out int trustResult)
        {
            using (var fileInfo = new WinTrustFileInfo(filePath))
            using (var trustData = new WinTrustData(fileInfo))
            {
                trustResult = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, trustData);
                return trustResult == 0;
            }
        }

        public static bool IsSignedByPublisher(string filePath, string expectedPublisher)
        {
            if (string.IsNullOrWhiteSpace(expectedPublisher))
                throw new ArgumentException("An expected publisher is required.", nameof(expectedPublisher));

            try
            {
#pragma warning disable SYSLIB0057
                using (var signedCertificate = X509Certificate.CreateFromSignedFile(filePath))
                using (var certificate = new X509Certificate2(signedCertificate))
#pragma warning restore SYSLIB0057
                {
                    return HasExpectedPublisher(certificate, expectedPublisher);
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        internal static bool HasExpectedPublisher(X509Certificate2 certificate, string expectedPublisher)
        {
            if (!string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), expectedPublisher,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var subjectParts = certificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var subjectPart in subjectParts)
            {
                var separator = subjectPart.IndexOf('=');
                if (separator <= 0) continue;
                var key = subjectPart.Substring(0, separator).Trim();
                var value = subjectPart.Substring(separator + 1).Trim().Trim('"');
                if (string.Equals(key, "O", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value, expectedPublisher, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo : IDisposable
        {
            public uint StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            public IntPtr FilePath;
            public IntPtr FileHandle = IntPtr.Zero;
            public IntPtr KnownSubject = IntPtr.Zero;

            public WinTrustFileInfo(string filePath)
            {
                FilePath = Marshal.StringToCoTaskMemUni(filePath);
            }

            public void Dispose()
            {
                if (FilePath != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(FilePath);
                    FilePath = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            private const uint UiNone = 2;
            private const uint RevocationChecksNone = 0;
            private const uint ChoiceFile = 1;
            private const uint StateActionIgnore = 0;
            private const uint ProviderFlags = 0x00000110;
            private const uint UiContextExecute = 0;

            public uint StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData));
            public IntPtr PolicyCallbackData = IntPtr.Zero;
            public IntPtr SipClientData = IntPtr.Zero;
            public uint UiChoice = UiNone;
            public uint RevocationChecks = RevocationChecksNone;
            public uint UnionChoice = ChoiceFile;
            public IntPtr FileInfo;
            public uint StateAction = StateActionIgnore;
            public IntPtr StateData = IntPtr.Zero;
            public IntPtr UrlReference = IntPtr.Zero;
            public uint ProvFlags = ProviderFlags;
            public uint UiContext = UiContextExecute;

            public WinTrustData(WinTrustFileInfo fileInfo)
            {
                FileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, FileInfo, false);
            }

            public void Dispose()
            {
                if (FileInfo != IntPtr.Zero)
                {
                    Marshal.DestroyStructure(FileInfo, typeof(WinTrustFileInfo));
                    Marshal.FreeCoTaskMem(FileInfo);
                    FileInfo = IntPtr.Zero;
                }
            }
        }
    }
}
