using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GravityBox.Win32;

/// <summary>
/// 绑定 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，确保父进程退出或崩溃时自动销毁子进程，零孤儿残留（ADR-002）。
/// </summary>
public sealed class JobObjectGuard : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _jobHandle;
    private bool _disposed;

    public JobObjectGuard()
    {
        _jobHandle = CreateJobObjectW(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObjectW failed with Win32 error {Marshal.GetLastWin32Error()}");
        }

        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        extendedInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ref extendedInfo, length))
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
            throw new InvalidOperationException($"SetInformationJobObject failed with Win32 error {err}");
        }
    }

    public bool AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return AssignProcess(process.Handle);
    }

    public bool AssignProcess(IntPtr processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_jobHandle == IntPtr.Zero || processHandle == IntPtr.Zero)
        {
            return false;
        }
        return AssignProcessToJobObject(_jobHandle, processHandle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }
}
