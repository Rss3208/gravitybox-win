using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace GravityBox.Win32;

public sealed record TcpListenerEntryInfo(
    int Pid,
    string LocalAddress,
    int LocalPort,
    string ExecutablePath
);

public static class NetworkInspector
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int MIB_TCP_STATE_LISTEN = 2;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    public static IReadOnlyList<TcpListenerEntryInfo> FindTcpListeners(int? filterPort = null, int? filterPid = null)
    {
        var results = new List<TcpListenerEntryInfo>();
        var exeCache = new Dictionary<int, string>();

        ReadIpv4Listeners(results, exeCache, filterPort, filterPid);
        ReadIpv6Listeners(results, exeCache, filterPort, filterPid);

        return results;
    }

    private static void ReadIpv4Listeners(
        List<TcpListenerEntryInfo> results,
        Dictionary<int, string> exeCache,
        int? filterPort,
        int? filterPid)
    {
        int bufferSize = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (ret != ERROR_INSUFFICIENT_BUFFER || bufferSize <= 0)
        {
            return;
        }

        IntPtr tablePtr = Marshal.AllocHGlobal(bufferSize);
        try
        {
            ret = GetExtendedTcpTable(tablePtr, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (ret != 0)
            {
                return;
            }

            int numEntries = Marshal.ReadInt32(tablePtr);
            IntPtr rowPtr = IntPtr.Add(tablePtr, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.dwState != MIB_TCP_STATE_LISTEN)
                {
                    continue;
                }

                int port = ConvertNetworkPort(row.dwLocalPort);
                int pid = (int)row.dwOwningPid;

                if (filterPort.HasValue && port != filterPort.Value) continue;
                if (filterPid.HasValue && pid != filterPid.Value) continue;

                string address = new IPAddress(row.dwLocalAddr).ToString();
                string exePath = ResolveExeCached(pid, exeCache);

                results.Add(new TcpListenerEntryInfo(
                    Pid: pid,
                    LocalAddress: address,
                    LocalPort: port,
                    ExecutablePath: exePath
                ));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(tablePtr);
        }
    }

    private static void ReadIpv6Listeners(
        List<TcpListenerEntryInfo> results,
        Dictionary<int, string> exeCache,
        int? filterPort,
        int? filterPid)
    {
        int bufferSize = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (ret != ERROR_INSUFFICIENT_BUFFER || bufferSize <= 0)
        {
            return;
        }

        IntPtr tablePtr = Marshal.AllocHGlobal(bufferSize);
        try
        {
            ret = GetExtendedTcpTable(tablePtr, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (ret != 0)
            {
                return;
            }

            int numEntries = Marshal.ReadInt32(tablePtr);
            IntPtr rowPtr = IntPtr.Add(tablePtr, 4);
            int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.dwState != MIB_TCP_STATE_LISTEN)
                {
                    continue;
                }

                int port = ConvertNetworkPort(row.dwLocalPort);
                int pid = (int)row.dwOwningPid;

                if (filterPort.HasValue && port != filterPort.Value) continue;
                if (filterPid.HasValue && pid != filterPid.Value) continue;

                string address = row.ucLocalAddr != null && row.ucLocalAddr.Length == 16
                    ? new IPAddress(row.ucLocalAddr).ToString()
                    : "::";
                string exePath = ResolveExeCached(pid, exeCache);

                results.Add(new TcpListenerEntryInfo(
                    Pid: pid,
                    LocalAddress: address,
                    LocalPort: port,
                    ExecutablePath: exePath
                ));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(tablePtr);
        }
    }

    private static int ConvertNetworkPort(uint dwPort)
    {
        return (int)(((dwPort & 0xFF) << 8) | ((dwPort >> 8) & 0xFF));
    }

    private static string ResolveExeCached(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out string? existing))
        {
            return existing;
        }
        string resolved = ProcessInspector.QueryExecutablePathByPid(pid) ?? string.Empty;
        cache[pid] = resolved;
        return resolved;
    }
}
