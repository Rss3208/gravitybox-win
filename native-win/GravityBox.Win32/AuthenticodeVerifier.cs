using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace GravityBox.Win32;

public sealed record AuthenticodeVerificationInfo(
    bool Valid,
    string Status,
    string Subject,
    string Issuer,
    string Thumbprint,
    string FileVersion,
    string ProductName,
    string CompanyName
);

public static class AuthenticodeVerifier
{
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_EXPLICIT_DISTRUST = 0x800B0111;
    private const uint TRUST_E_SUBJECT_NOT_TRUSTED = 0x800B0004;
    private const uint CRYPT_E_SECURITY_SETTINGS = 0x80092026;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
    private static extern uint WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

    public static AuthenticodeVerificationInfo Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new AuthenticodeVerificationInfo(
                Valid: false,
                Status: "NotFound",
                Subject: string.Empty,
                Issuer: string.Empty,
                Thumbprint: string.Empty,
                FileVersion: string.Empty,
                ProductName: string.Empty,
                CompanyName: string.Empty
            );
        }

        string fullPath = Path.GetFullPath(filePath);
        string fileVersion = string.Empty;
        string productName = string.Empty;
        string companyName = string.Empty;

        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(fullPath);
            fileVersion = (fvi.FileVersion ?? fvi.ProductVersion ?? string.Empty).Trim();
            productName = (fvi.ProductName ?? string.Empty).Trim();
            companyName = (fvi.CompanyName ?? string.Empty).Trim();
        }
        catch
        {
            // Ignore PE version parse errors on non-PE files.
        }

        uint trustResult = VerifyEmbeddedSignature(fullPath);
        string status = trustResult switch
        {
            0 => "Valid",
            TRUST_E_NOSIGNATURE => "NotSigned",
            TRUST_E_EXPLICIT_DISTRUST => "ExplicitDistrust",
            TRUST_E_SUBJECT_NOT_TRUSTED => "SubjectNotTrusted",
            CRYPT_E_SECURITY_SETTINGS => "SecuritySettingsBlocked",
            _ => $"InvalidSignature(0x{trustResult:X8})",
        };

        string subject = string.Empty;
        string issuer = string.Empty;
        string thumbprint = string.Empty;

        try
        {
#pragma warning disable SYSLIB0057
            using var baseCert = X509Certificate.CreateFromSignedFile(fullPath);
            using var cert2 = new X509Certificate2(baseCert);
#pragma warning restore SYSLIB0057
            subject = cert2.Subject ?? string.Empty;
            issuer = cert2.Issuer ?? string.Empty;
            thumbprint = cert2.Thumbprint ?? string.Empty;
        }
        catch
        {
            // File has no embedded X.509 certificate or uses catalog signing.
        }

        return new AuthenticodeVerificationInfo(
            Valid: trustResult == 0,
            Status: status,
            Subject: subject,
            Issuer: issuer,
            Thumbprint: thumbprint,
            FileVersion: fileVersion,
            ProductName: productName,
            CompanyName: companyName
        );
    }

    private static uint VerifyEmbeddedSignature(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

        var trustData = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSIPClientData = IntPtr.Zero,
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = fileInfoPtr,
            dwStateAction = WTD_STATEACTION_VERIFY,
            hWVTStateData = IntPtr.Zero,
            pwszURLReference = IntPtr.Zero,
            dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            dwUIContext = 0,
            pSignatureSettings = IntPtr.Zero,
        };

        try
        {
            return WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref trustData);
        }
        finally
        {
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref trustData);
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }
}
