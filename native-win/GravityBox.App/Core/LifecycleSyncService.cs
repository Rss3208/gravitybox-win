using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.Win32;
using Microsoft.Win32;

namespace GravityBox.App.Core;

public sealed record SafeRelaunchResult(bool Ok, bool TimedOut, string Message);

public interface ILifecycleHostAdapter
{
    bool IsAntigravityRunning(string officialExePath);
    bool HasActiveDebugPort(string profileDir);
    int CloseAntigravityGracefully(string officialExePath);
    bool LaunchAntigravityWithDebugPort(string officialExePath);
}

public sealed class Win32LifecycleHostAdapter : ILifecycleHostAdapter
{
    public bool IsAntigravityRunning(string officialExePath)
    {
        if (string.IsNullOrWhiteSpace(officialExePath)) return false;
        string targetNorm = Path.GetFullPath(officialExePath).TrimEnd('\\');
        var procs = ProcessInspector.ListProcesses("Antigravity");
        return procs.Any(p => string.Equals(
            p.ExecutablePath.TrimEnd('\\'),
            targetNorm,
            StringComparison.OrdinalIgnoreCase));
    }

    public bool HasActiveDebugPort(string profileDir)
    {
        if (string.IsNullOrWhiteSpace(profileDir)) return false;
        string portFile = Path.Combine(profileDir, "DevToolsActivePort");
        if (!File.Exists(portFile)) return false;
        try
        {
            string[] lines = File.ReadAllLines(portFile);
            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out int port) || port <= 0)
            {
                return false;
            }
            return NetworkInspector.FindTcpListeners(filterPort: port).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public int CloseAntigravityGracefully(string officialExePath)
    {
        if (string.IsNullOrWhiteSpace(officialExePath)) return 0;
        return WindowCloser.RequestGracefulCloseByExecutablePath(Path.GetFullPath(officialExePath));
    }

    public bool LaunchAntigravityWithDebugPort(string officialExePath)
    {
        if (string.IsNullOrWhiteSpace(officialExePath) || !File.Exists(officialExePath))
        {
            return false;
        }
        var psi = new ProcessStartInfo
        {
            FileName = officialExePath,
            WorkingDirectory = Path.GetDirectoryName(officialExePath) ?? string.Empty,
            UseShellExecute = true,
        };
        psi.ArgumentList.Add("--remote-debugging-port=0");
        return Process.Start(psi) != null;
    }
}

public sealed class LifecycleSyncService : IDisposable
{
    public const string DefaultRunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "GravityBox";

    private readonly ILifecycleHostAdapter _host;
    private readonly string _runRegistrySubKey;
    private readonly object _suppressLock = new();
    private DateTimeOffset _suppressAutoQuitUntil = DateTimeOffset.MinValue;
    private bool _wasRunning;
    private bool _disposed;

    public TimeSpan GraceWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan QuitWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan PostExitBuffer { get; set; } = TimeSpan.FromMilliseconds(300);

    public event Action? AutoExitRequested;

    public LifecycleSyncService(
        ILifecycleHostAdapter? host = null,
        string runRegistrySubKey = DefaultRunRegistrySubKey)
    {
        _host = host ?? new Win32LifecycleHostAdapter();
        _runRegistrySubKey = string.IsNullOrWhiteSpace(runRegistrySubKey)
            ? DefaultRunRegistrySubKey
            : runRegistrySubKey;
    }

    public void SuppressLifecycleAutoQuit(TimeSpan duration)
    {
        lock (_suppressLock)
        {
            var until = DateTimeOffset.UtcNow + duration;
            if (until > _suppressAutoQuitUntil)
            {
                _suppressAutoQuitUntil = until;
            }
        }
    }

    public bool IsAutoQuitSuppressed
    {
        get
        {
            lock (_suppressLock)
            {
                return DateTimeOffset.UtcNow < _suppressAutoQuitUntil;
            }
        }
    }

    public async Task<SafeRelaunchResult> OpenAntigravityAsync(
        string officialExePath,
        string profileDir,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(officialExePath))
        {
            return new SafeRelaunchResult(false, false, "Antigravity path not found");
        }

        if (!_host.IsAntigravityRunning(officialExePath))
        {
            bool started = _host.LaunchAntigravityWithDebugPort(officialExePath);
            return new SafeRelaunchResult(started, false, started ? "Launched" : "Failed to launch Antigravity");
        }

        if (_host.HasActiveDebugPort(profileDir))
        {
            return new SafeRelaunchResult(true, false, "Already running with debug port");
        }

        return await SafeRelaunchAsync(officialExePath, profileDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SafeRelaunchResult> SafeRelaunchAsync(
        string officialExePath,
        string profileDir,
        CancellationToken cancellationToken = default)
    {
        SuppressLifecycleAutoQuit(TimeSpan.FromSeconds(30));

        if (_host.IsAntigravityRunning(officialExePath))
        {
            bool exited = await QuitAntigravityAndWaitAsync(officialExePath, GraceWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!exited)
            {
                // INV-04: Never force-kill Antigravity on timeout
                return new SafeRelaunchResult(false, true, "Timed out waiting for Antigravity to close gracefully");
            }
            await Task.Delay(PostExitBuffer, cancellationToken).ConfigureAwait(false);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_host.LaunchAntigravityWithDebugPort(officialExePath))
            {
                await Task.Delay(PostExitBuffer, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_host.HasActiveDebugPort(profileDir))
                {
                    return new SafeRelaunchResult(true, false, "Relaunched with debug port");
                }
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return new SafeRelaunchResult(false, false, "Antigravity launched but debug port did not become ready");
    }

    public async Task<bool> QuitAntigravityAndWaitAsync(
        string officialExePath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsAntigravityRunning(officialExePath))
        {
            return true;
        }

        _host.CloseAntigravityGracefully(officialExePath);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? QuitWaitTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_host.IsAntigravityRunning(officialExePath))
            {
                return true;
            }
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return !_host.IsAntigravityRunning(officialExePath);
    }

    public async Task CheckLifecycleSyncTickAsync(
        string officialExePath,
        bool syncEnabled,
        CancellationToken cancellationToken = default)
    {
        if (!syncEnabled || string.IsNullOrWhiteSpace(officialExePath))
        {
            _wasRunning = false;
            return;
        }

        bool running = _host.IsAntigravityRunning(officialExePath);
        if (running)
        {
            _wasRunning = true;
            return;
        }

        if (_wasRunning && !IsAutoQuitSuppressed)
        {
            // 600ms 二次确认，避免安全重启或瞬态切换误触发
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            if (!IsAutoQuitSuppressed && !_host.IsAntigravityRunning(officialExePath))
            {
                _wasRunning = false;
                AutoExitRequested?.Invoke();
            }
        }
    }

    public bool IsLaunchAtLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runRegistrySubKey, writable: false);
        var val = key?.GetValue(RunValueName) as string;
        return !string.IsNullOrWhiteSpace(val);
    }

    public void SetLaunchAtLogin(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runRegistrySubKey, writable: true);
        if (key == null) return;

        if (enabled)
        {
            string resolvedExe = !string.IsNullOrWhiteSpace(exePath)
                ? Path.GetFullPath(exePath)
                : (Environment.ProcessPath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(resolvedExe))
            {
                key.SetValue(RunValueName, $"\"{resolvedExe}\" --minimized", RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
