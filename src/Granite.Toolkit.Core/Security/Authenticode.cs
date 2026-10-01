using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Granite.Toolkit.Core.Security;

/// <summary>
/// Checks a file's Authenticode signature with Windows' own WinVerifyTrust
/// (the same check Explorer and SmartScreen use): the file is unchanged
/// since signing and the certificate chains to a trusted root, honouring
/// the signing timestamp, so a signature made with a short-lived
/// certificate (Azure Artifact Signing's last 24 hours) stays valid.
/// </summary>
/// <remarks>
/// Revocation is checked from the local cache only, never by going online:
/// client servers are often offline, and a network fetch here would hang
/// the dashboard. When nothing is cached the rest of the check still runs
/// and the result says revocation wasn't checked; a certificate known to
/// be revoked always fails. Windows' own SmartScreen and Defender still do
/// their online checks independently.
/// </remarks>
public static class Authenticode
{
    public static SignatureInfo Verify(string path)
    {
        if (!File.Exists(path)) return new SignatureInfo(SignatureState.Invalid, null, "File not found");

        int hr = WinVerify(path, checkRevocation: true);
        bool revocationUnknown = false;
        if (hr is CERT_E_REVOCATION_FAILURE or CRYPT_E_REVOCATION_OFFLINE or CRYPT_E_NO_REVOCATION_CHECK)
        {
            // Offline server with no cached revocation list: verify everything
            // else, and say revocation couldn't be checked. A certificate that
            // IS known to be revoked still fails above (CERT_E_REVOKED).
            hr = WinVerify(path, checkRevocation: false);
            revocationUnknown = true;
        }
        string? subject = TryGetSigner(path);

        return hr switch
        {
            0 => new SignatureInfo(SignatureState.Valid, subject, revocationUnknown ? "Valid (revocation not checked: offline)" : "Valid"),
            TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN when subject is null
                => SignatureInfo.Unsigned(),
            _ => new SignatureInfo(SignatureState.Invalid, subject, Describe(hr))
        };
    }

    private static string? TryGetSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // X509Certificate.CreateFromSignedFile: only used to read the signer name after WinVerifyTrust
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch
        {
            return null; // not signed
        }
    }

    private static string Describe(int hr) => hr switch
    {
        TRUST_E_BAD_DIGEST => "the file was changed after it was signed",
        TRUST_E_EXPLICIT_DISTRUST => "the certificate is explicitly distrusted",
        CERT_E_UNTRUSTEDROOT => "the certificate doesn't chain to a trusted root",
        CERT_E_CHAINING => "the certificate chain couldn't be built",
        CERT_E_EXPIRED => "the certificate expired and the signature has no timestamp",
        CERT_E_REVOKED => "the certificate was revoked",
        TRUST_E_NOSIGNATURE => "no valid signature",
        _ => new Win32Exception(hr).Message + $" (0x{hr:X8})"
    };

    // ---- WinVerifyTrust ----------------------------------------------------

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
    private const int CRYPT_E_NO_REVOCATION_CHECK = unchecked((int)0x80092012);

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    private static int WinVerify(string path, bool checkRevocation)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = checkRevocation ? WTD_REVOKE_WHOLECHAIN : WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            int result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
