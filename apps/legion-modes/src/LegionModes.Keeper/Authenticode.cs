using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LegionModes.Keeper;

// Authenticode integrity (WinVerifyTrust) plus the signer thumbprint pin. The Team fork signs with a self-signed
// certificate, so an untrusted root is accepted and the pinned thumbprint decides who signed.
internal static class Authenticode
{
    const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    const uint WtdUiNone = 2;
    const uint WtdRevokeNone = 0;
    const uint WtdChoiceFile = 1;
    const uint WtdStateActionIgnore = 0;
    const uint WtdRevocationCheckNone = 0x10;
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WintrustData data);

    // 0 (trusted) and CERT_E_UNTRUSTEDROOT are accepted; every other result (tampered, unsigned, expired chain) is not.
    public static bool IntegrityOk(string file)
    {
        var path = Marshal.StringToHGlobalUni(file);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path }, info, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = info,
                dwStateAction = WtdStateActionIgnore,
                dwProvFlags = WtdRevocationCheckNone,
            };
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            return result == 0 || result == CertEUntrustedRoot;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(path);
        }
    }

    public static bool SignerMatches(string file, string thumbprint)
    {
        if (!IntegrityOk(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057   // the signer certificate of a PE file has no replacement API
            using var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return string.Equals(cert.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
