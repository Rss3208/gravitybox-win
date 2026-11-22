using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GravityBox.Win32;

public static class WindowCloser
{
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 枚举指定可执行路径（或指定 PID 集合）的可见顶层窗口并发送优雅关闭消息 WM_CLOSE。
    /// 严禁调用 TerminateProcess（INV-04 / FR-004）。
    /// </summary>
    public static int RequestGracefulCloseByExecutablePath(string executablePath, bool requireVisible = true)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return 0;
        }

        string normalizedTarget = executablePath.Trim().Replace('/', '\\');
        var matchingPids = new HashSet<uint>();
        foreach (var proc in ProcessInspector.ListProcesses())
        {
            string candidate = (proc.ExecutablePath ?? string.Empty).Replace('/', '\\');
            if (string.Equals(candidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                matchingPids.Add((uint)proc.Pid);
            }
        }

        if (matchingPids.Count == 0)
        {
            return 0;
        }

        return RequestGracefulCloseByPids(matchingPids, requireVisible);
    }

    public static int RequestGracefulCloseByPids(IReadOnlySet<uint> targetPids, bool requireVisible = true)
    {
        if (targetPids == null || targetPids.Count == 0)
        {
            return 0;
        }

        var windowsToClose = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (requireVisible && !IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0 && targetPids.Contains(pid))
            {
                windowsToClose.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);

        int closedCount = 0;
        foreach (IntPtr hWnd in windowsToClose)
        {
            if (PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
            {
                closedCount++;
            }
        }
        return closedCount;
    }
}
