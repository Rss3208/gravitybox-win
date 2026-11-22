using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GravityBox.Win32;

public sealed record ProcessEntryInfo(
    int Pid,
    int ParentPid,
    string Name,
    string ExecutablePath,
    string CommandLine,
    double CpuPercent,
    long CreationTimeMs
);

public sealed record ProcessAliveInfo(
    bool Alive,
    long? CreationTimeMs
);

public static class ProcessInspector
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint STILL_ACTIVE = 259;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        StringBuilder lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        int dwSize,
        out IntPtr lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    public static IReadOnlyList<ProcessEntryInfo> ListProcesses(string? nameFilter = null)
    {
        var results = new List<ProcessEntryInfo>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
        {
            return results;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return results;
            }

            string? normalizedFilter = string.IsNullOrWhiteSpace(nameFilter)
                ? null
                : nameFilter.Trim();

            do
            {
                int pid = (int)entry.th32ProcessID;
                int ppid = (int)entry.th32ParentProcessID;
                string exeName = entry.szExeFile ?? string.Empty;

                if (pid <= 4)
                {
                    continue;
                }

                if (normalizedFilter != null &&
                    !exeName.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string executablePath = string.Empty;
                string commandLine = string.Empty;
                long creationTimeMs = 0;

                IntPtr hLimited = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, entry.th32ProcessID);
                if (hLimited != IntPtr.Zero)
                {
                    try
                    {
                        executablePath = QueryImageName(hLimited) ?? string.Empty;
                        creationTimeMs = QueryCreationTimeMs(hLimited) ?? 0;
                    }
                    finally
                    {
                        CloseHandle(hLimited);
                    }
                }

                IntPtr hVm = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, entry.th32ProcessID);
                if (hVm != IntPtr.Zero)
                {
                    try
                    {
                        commandLine = TryReadProcessCommandLine(hVm) ?? string.Empty;
                    }
                    finally
                    {
                        CloseHandle(hVm);
                    }
                }

                if (string.IsNullOrEmpty(commandLine) && !string.IsNullOrEmpty(executablePath))
                {
                    commandLine = executablePath;
                }

                results.Add(new ProcessEntryInfo(
                    Pid: pid,
                    ParentPid: ppid,
                    Name: string.IsNullOrEmpty(exeName) && !string.IsNullOrEmpty(executablePath)
                        ? Path.GetFileName(executablePath)
                        : exeName,
                    ExecutablePath: executablePath,
                    CommandLine: commandLine,
                    CpuPercent: 0.0,
                    CreationTimeMs: creationTimeMs
                ));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return results;
    }

    public static ProcessAliveInfo CheckProcessAlive(int pid, long? expectedCreationTimeMs = null)
    {
        if (pid <= 0)
        {
            return new ProcessAliveInfo(false, null);
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess == IntPtr.Zero)
        {
            return new ProcessAliveInfo(false, null);
        }

        try
        {
            if (!GetExitCodeProcess(hProcess, out uint exitCode) || exitCode != STILL_ACTIVE)
            {
                return new ProcessAliveInfo(false, null);
            }

            long? actualCreationMs = QueryCreationTimeMs(hProcess);
            if (expectedCreationTimeMs.HasValue && actualCreationMs.HasValue)
            {
                if (Math.Abs(actualCreationMs.Value - expectedCreationTimeMs.Value) > 2000)
                {
                    return new ProcessAliveInfo(false, actualCreationMs);
                }
            }

            return new ProcessAliveInfo(true, actualCreationMs);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    public static string? QueryExecutablePathByPid(int pid)
    {
        if (pid <= 0) return null;
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            return QueryImageName(hProcess);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    public static string? ReadProcessEnvVar(int pid, string variableName)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(variableName))
        {
            return null;
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!TryGetProcessParametersAddress(hProcess, out IntPtr paramsAddr) || paramsAddr == IntPtr.Zero)
            {
                return null;
            }

            // On x64 Windows, RTL_USER_PROCESS_PARAMETERS.Environment is at offset 0x80,
            // and EnvironmentSize (ULONG_PTR) is at offset 0x3F0.
            IntPtr envPtr = ReadIntPtr(hProcess, IntPtr.Add(paramsAddr, 0x80));
            if (envPtr == IntPtr.Zero)
            {
                return null;
            }

            long envSize = ReadIntPtr(hProcess, IntPtr.Add(paramsAddr, 0x3F0)).ToInt64();
            int bytesToRead = envSize > 4 && envSize <= 256 * 1024
                ? (int)envSize
                : 64 * 1024;

            var buffer = new byte[bytesToRead];
            if (!ReadProcessMemory(hProcess, envPtr, buffer, buffer.Length, out IntPtr bytesRead) ||
                bytesRead.ToInt64() < 4)
            {
                return null;
            }

            int validBytes = (int)Math.Min(buffer.Length, bytesRead.ToInt64());
            validBytes -= validBytes % 2;
            string envText = Encoding.Unicode.GetString(buffer, 0, validBytes);
            string prefix = variableName + "=";

            int start = 0;
            while (start < envText.Length)
            {
                int nulIdx = envText.IndexOf('\0', start);
                if (nulIdx < 0) nulIdx = envText.Length;
                int len = nulIdx - start;
                if (len == 0)
                {
                    break;
                }

                ReadOnlySpan<char> entry = envText.AsSpan(start, len);
                if (entry.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Slice(prefix.Length).ToString();
                }

                start = nulIdx + 1;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static string? QueryImageName(IntPtr hProcess)
    {
        uint size = 1024;
        var sb = new StringBuilder((int)size);
        if (QueryFullProcessImageNameW(hProcess, 0, sb, ref size) && size > 0)
        {
            return sb.ToString(0, (int)size);
        }
        return null;
    }

    private static long? QueryCreationTimeMs(IntPtr hProcess)
    {
        if (!GetProcessTimes(hProcess, out long creationFileTime, out _, out _, out _) || creationFileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(creationFileTime).ToUnixTimeMilliseconds();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetProcessParametersAddress(IntPtr hProcess, out IntPtr processParametersAddr)
    {
        processParametersAddr = IntPtr.Zero;
        if (IntPtr.Size != 8)
        {
            return false;
        }

        int status = NtQueryInformationProcess(
            hProcess,
            0,
            out PROCESS_BASIC_INFORMATION pbi,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out _);

        if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
        {
            return false;
        }

        // On x64 PEB, ProcessParameters pointer is at offset 0x20.
        processParametersAddr = ReadIntPtr(hProcess, IntPtr.Add(pbi.PebBaseAddress, 0x20));
        return processParametersAddr != IntPtr.Zero;
    }

    private static string? TryReadProcessCommandLine(IntPtr hProcess)
    {
        try
        {
            if (!TryGetProcessParametersAddress(hProcess, out IntPtr paramsAddr) || paramsAddr == IntPtr.Zero)
            {
                return null;
            }

            // On x64 RTL_USER_PROCESS_PARAMETERS, CommandLine UNICODE_STRING is at offset 0x70.
            var usBuf = new byte[16];
            if (!ReadProcessMemory(hProcess, IntPtr.Add(paramsAddr, 0x70), usBuf, usBuf.Length, out IntPtr read) ||
                read.ToInt64() < 16)
            {
                return null;
            }

            ushort byteLength = BitConverter.ToUInt16(usBuf, 0);
            IntPtr strPtr = new(BitConverter.ToInt64(usBuf, 8));
            if (byteLength == 0 || strPtr == IntPtr.Zero || byteLength > 32766)
            {
                return null;
            }

            var cmdBytes = new byte[byteLength];
            if (!ReadProcessMemory(hProcess, strPtr, cmdBytes, cmdBytes.Length, out IntPtr cmdRead) ||
                cmdRead.ToInt64() < byteLength)
            {
                return null;
            }

            return Encoding.Unicode.GetString(cmdBytes).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr ReadIntPtr(IntPtr hProcess, IntPtr address)
    {
        var buf = new byte[8];
        if (!ReadProcessMemory(hProcess, address, buf, buf.Length, out IntPtr read) || read.ToInt64() < 8)
        {
            return IntPtr.Zero;
        }
        return new IntPtr(BitConverter.ToInt64(buf, 0));
    }
}
