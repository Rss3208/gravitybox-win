using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GravityBox.Win32;

public static class RecycleBinHelper
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>
    /// 将指定文件或目录移入系统回收站（FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI）。
    /// </summary>
    public static bool MoveToRecycleBin(IEnumerable<string> paths)
    {
        if (paths == null) return true;

        var existingPaths = new List<string>();
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full = Path.GetFullPath(raw);
            if (File.Exists(full) || Directory.Exists(full))
            {
                existingPaths.Add(full);
            }
        }

        if (existingPaths.Count == 0)
        {
            return true;
        }

        // SHFileOperationW requires double-null terminated list of paths
        string doubleNullFrom = string.Join('\0', existingPaths) + "\0\0";
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = doubleNullFrom,
            pTo = null,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI),
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null,
        };

        int result = SHFileOperationW(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }
}
