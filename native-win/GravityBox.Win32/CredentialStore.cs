using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GravityBox.Win32;

public sealed record CredentialRecord(
    string Target,
    string? UserName,
    string Secret
);

public static class CredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    public const int MaxCredentialBlobBytes = 2560;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(
        ref CREDENTIALW userCredential,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(
        string target,
        uint type,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    public static CredentialRecord? ReadGeneric(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr) || credPtr == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                return null;
            }
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIALW>(credPtr);
            string? userName = cred.UserName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.UserName)
                : null;

            string secret = string.Empty;
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                var blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, (int)cred.CredentialBlobSize);
                secret = DecodeCredentialBlob(blob);
            }

            return new CredentialRecord(target, userName, secret);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void WriteGeneric(string target, string? userName, string secret)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Target name is required.", nameof(target));
        }
        ArgumentNullException.ThrowIfNull(secret);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(secret);
        if (utf8Bytes.Length > MaxCredentialBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"Credential payload ({utf8Bytes.Length} bytes) exceeds Win32 CRED_MAX_CREDENTIAL_BLOB_SIZE ({MaxCredentialBlobBytes} bytes).");
        }

        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(target);
        IntPtr userPtr = !string.IsNullOrEmpty(userName)
            ? Marshal.StringToCoTaskMemUni(userName)
            : IntPtr.Zero;
        IntPtr blobPtr = utf8Bytes.Length > 0
            ? Marshal.AllocCoTaskMem(utf8Bytes.Length)
            : IntPtr.Zero;

        try
        {
            if (utf8Bytes.Length > 0)
            {
                Marshal.Copy(utf8Bytes, 0, blobPtr, utf8Bytes.Length);
            }

            var cred = new CREDENTIALW
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                CredentialBlobSize = (uint)utf8Bytes.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userPtr,
            };

            if (!CredWriteW(ref cred, 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CredWriteW failed with Win32 error {err}");
            }
        }
        finally
        {
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
        }
    }

    public static bool DeleteGeneric(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        {
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == ERROR_NOT_FOUND)
        {
            return false;
        }

        throw new InvalidOperationException($"CredDeleteW failed with Win32 error {err}");
    }

    private static string DecodeCredentialBlob(byte[] blob)
    {
        // CHK-04: zalando/go-keyring (wincred) writes raw UTF-8 bytes into CredentialBlob.
        // If a legacy tool wrote UTF-16LE (containing alternating 0x00 bytes on ASCII), fall back cleanly.
        bool looksLikeUtf16Le = blob.Length >= 4 && blob.Length % 2 == 0 && blob[1] == 0 && blob[3] == 0;
        if (looksLikeUtf16Le)
        {
            return Encoding.Unicode.GetString(blob).TrimEnd('\0');
        }
        return Encoding.UTF8.GetString(blob).TrimEnd('\0');
    }
}
