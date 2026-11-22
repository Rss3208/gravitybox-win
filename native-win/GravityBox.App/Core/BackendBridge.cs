using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.Win32;

namespace GravityBox.App.Core;

public sealed class BackendBridge : IBackendBridge
{
    public const int MaxReplyBytes = 4_000_000;
    public const long MaxLogBytes = 1_048_576;
    public const int MaxArchivedLogs = 10;

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "ping",
        "status",
        "dashboard_summary",
        "task_list",
        "platform_info",
        "shortcut_scan",
        "account_list",
        "account_get",
        "account_switch_preflight",
        "account_recovery_info",
        "account_oauth_status",
        "auto_switch_get",
        "quota_refresh",
        "quota_refresh_start",
        "usage_summary",
        "usage_events",
        "usage_snapshot_release",
        "usage_ledger_review",
        "api_pricing_get",
        "pricing_sync_status",
        "maintenance_scan",
        "maintenance_history",
    };

    private static readonly int[] StartupBackoffMs = { 18, 25, 35, 50, 75, 100, 125, 150, 175, 200, 220, 220 };

    private readonly string _resourcesDir;
    private readonly string _logDir;
    private readonly string? _nodeExeOverride;
    private readonly string? _mainJsOverride;
    private readonly Func<IReadOnlyList<string>> _systemLocalesProvider;
    private readonly SemaphoreSlim _readSemaphore = new(8, 8);
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private readonly object _processLock = new();
    private readonly object _logLock = new();

    private Process? _child;
    private JobObjectGuard? _jobGuard;
    private bool _isLaunching;
    private bool _stopping;
    private bool _disposed;

    public string StateDir { get; }
    public string PipeName { get; }
    public string? PreferredLocale { get; set; } = "zh-Hans";

    public bool IsOwnedProcessRunning
    {
        get
        {
            lock (_processLock)
            {
                return _isLaunching || (_child != null && !_child.HasExited);
            }
        }
    }

    public int? OwnedPid
    {
        get
        {
            lock (_processLock)
            {
                return _child != null && !_child.HasExited ? _child.Id : null;
            }
        }
    }

    public event Action<string>? OnExit;

    public BackendBridge(
        string resourcesDir,
        string stateDir,
        string? logDir = null,
        string? nodeExeOverride = null,
        string? mainJsOverride = null,
        Func<IReadOnlyList<string>>? systemLocalesProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcesDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        _resourcesDir = Path.GetFullPath(resourcesDir);
        StateDir = Path.GetFullPath(stateDir);
        _logDir = Path.GetFullPath(logDir ?? Path.Combine(StateDir, "logs"));
        _nodeExeOverride = nodeExeOverride;
        _mainJsOverride = mainJsOverride;
        _systemLocalesProvider = systemLocalesProvider ?? SystemLocaleHelper.GetPreferredUiLanguages;
        PipeName = ComputePipeName(StateDir);
    }

    public static string ComputePipeName(string stateDir)
    {
        string normalized = Path.GetFullPath(stateDir).TrimEnd('\\', '/').ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"gravitybox-{hex[..24]}";
    }

    public static bool IsReadOnlyCommand(string command, JsonObject? parameters = null)
    {
        if (command == "auto_switch_best")
        {
            return parameters?["execute"]?.GetValue<bool>() != true;
        }
        return ReadOnlyCommands.Contains(command);
    }

    public static TimeSpan DefaultTimeoutFor(string command)
    {
        return command switch
        {
            "ping" or "status" or "task_list" or "platform_info" or "shortcut_scan" => TimeSpan.FromSeconds(8),
            "account_switch" or "account_switch_rollback" => TimeSpan.FromSeconds(65),
            "stop" => TimeSpan.FromSeconds(30),
            "maintenance_clear" or "maintenance_smart_clear" or "maintenance_restore" => TimeSpan.FromSeconds(120),
            _ => TimeSpan.FromSeconds(45),
        };
    }

    public Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            lock (_processLock)
            {
                if (_child != null && !_child.HasExited)
                {
                    return;
                }
                _stopping = false;
                _isLaunching = true;
            }

            try
            {
                Directory.CreateDirectory(StateDir);
                Directory.CreateDirectory(_logDir);

                string nodeExe = ResolveNodeExecutable();
                string mainJs = ResolveMainJs();

                var psi = new ProcessStartInfo
                {
                    FileName = nodeExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = _resourcesDir,
                };

                psi.ArgumentList.Add("--max-old-space-size=256");
                psi.ArgumentList.Add(mainJs);
                psi.ArgumentList.Add("--state-dir");
                psi.ArgumentList.Add(StateDir);

                string locale = string.IsNullOrWhiteSpace(PreferredLocale) ? "zh-Hans" : PreferredLocale!;
                psi.ArgumentList.Add("--locale");
                psi.ArgumentList.Add(locale);

                var sysLocales = _systemLocalesProvider();
                if (sysLocales.Count > 0)
                {
                    psi.ArgumentList.Add("--system-locales");
                    psi.ArgumentList.Add(string.Join(",", sysLocales));
                }

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, args) =>
                {
                    if (args.Data != null) WriteBackendLog(args.Data);
                };
                proc.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data != null) WriteBackendLog(args.Data);
                };
                proc.Exited += (_, _) =>
                {
                    bool expectedStop;
                    int exitCode = -1;
                    try { exitCode = proc.ExitCode; } catch { }
                    lock (_processLock)
                    {
                        expectedStop = _stopping;
                        if (ReferenceEquals(_child, proc))
                        {
                            _child = null;
                        }
                    }
                    if (!expectedStop)
                    {
                        OnExit?.Invoke($"Backend exited unexpectedly (code={exitCode})");
                    }
                };

                if (!proc.Start())
                {
                    throw new BackendFailureException("Failed to start Node.js backend process.");
                }

                var guard = new JobObjectGuard();
                guard.AssignProcess(proc);

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                lock (_processLock)
                {
                    _jobGuard?.Dispose();
                    _jobGuard = guard;
                    _child = proc;
                }
            }
            finally
            {
                lock (_processLock)
                {
                    _isLaunching = false;
                }
            }
        }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? proc;
        lock (_processLock)
        {
            _stopping = true;
            proc = _child;
        }

        if (proc != null && !proc.HasExited)
        {
            try
            {
                await SendAsync("stop", timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Fallback to job/process termination
            }

            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(3));
                await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        lock (_processLock)
        {
            _jobGuard?.Dispose();
            _jobGuard = null;
            _child?.Dispose();
            _child = null;
        }
    }

    public async Task<JsonObject> SendAsync(
        string command,
        JsonObject? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool isReadOnly = IsReadOnlyCommand(command, parameters);
        var gate = isReadOnly ? _readSemaphore : _writeSemaphore;
        var effectiveTimeout = timeout ?? DefaultTimeoutFor(command);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int maxAttempts = IsOwnedProcessRunning ? StartupBackoffMs.Length : 1;
            Exception? lastError = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await SendOnceAsync(command, parameters, effectiveTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException && attempt + 1 < maxAttempts)
                {
                    lastError = ex;
                    await Task.Delay(StartupBackoffMs[attempt], cancellationToken).ConfigureAwait(false);
                }
            }

            throw new BackendFailureException(
                lastError?.Message ?? "Failed to communicate with GravityBox backend.",
                retryable: lastError is IOException or TimeoutException,
                lastError);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<JsonObject> SendOnceAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var req = new JsonObject
        {
            ["command"] = command,
        };

        if (parameters != null)
        {
            foreach (var kv in parameters)
            {
                req[kv.Key] = kv.Value?.DeepClone();
            }
        }

        string? authToken = ReadIpcAuthToken();
        if (!string.IsNullOrEmpty(authToken) && !req.ContainsKey("authToken"))
        {
            req["authToken"] = authToken;
        }

        byte[] payloadBytes = Encoding.UTF8.GetBytes(req.ToJsonString() + "\n");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        int connectMs = Math.Max(500, Math.Min((int)timeout.TotalMilliseconds, 5000));
        await client.ConnectAsync(connectMs, cts.Token).ConfigureAwait(false);

        await client.WriteAsync(payloadBytes, cts.Token).ConfigureAwait(false);
        await client.FlushAsync(cts.Token).ConfigureAwait(false);

        string line = await ReadLineBoundedAsync(client, MaxReplyBytes, cts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new BackendFailureException("Backend returned an empty IPC response.", retryable: true);
        }

        var response = JsonNode.Parse(line) as JsonObject
            ?? throw new BackendFailureException("Backend IPC response is not a JSON object.");

        if (response["ok"]?.GetValue<bool>() == false)
        {
            string errMsg = response["error"]?.GetValue<string>() ?? "Unknown backend error";
            throw new BackendFailureException(errMsg, retryable: false);
        }

        return response;
    }

    private static async Task<string> ReadLineBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            int newlineIdx = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newlineIdx >= 0)
            {
                if (ms.Length + newlineIdx > maxBytes)
                {
                    throw new BackendFailureException($"IPC response exceeded {maxBytes} bytes limit.");
                }
                ms.Write(buffer, 0, newlineIdx);
                break;
            }

            if (ms.Length + read > maxBytes)
            {
                throw new BackendFailureException($"IPC response exceeded {maxBytes} bytes limit.");
            }
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
    }

    private string? ReadIpcAuthToken()
    {
        string tokenPath = Path.Combine(StateDir, "ipc.token");
        try
        {
            if (!File.Exists(tokenPath)) return null;
            string token = File.ReadAllText(tokenPath, Encoding.UTF8).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private string ResolveNodeExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_nodeExeOverride) && File.Exists(_nodeExeOverride))
        {
            return _nodeExeOverride!;
        }

        string bundled = Path.Combine(_resourcesDir, "runtime", "node.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return "node.exe";
    }

    private string ResolveMainJs()
    {
        if (!string.IsNullOrWhiteSpace(_mainJsOverride) && File.Exists(_mainJsOverride))
        {
            return _mainJsOverride!;
        }

        string coreDist = Path.Combine(_resourcesDir, "core", "dist", "main.js");
        if (File.Exists(coreDist))
        {
            return coreDist;
        }

        string devDist = Path.Combine(_resourcesDir, "dist", "main.js");
        if (File.Exists(devDist))
        {
            return devDist;
        }

        return coreDist;
    }

    private void WriteBackendLog(string line)
    {
        lock (_logLock)
        {
            try
            {
                Directory.CreateDirectory(_logDir);
                string logPath = Path.Combine(_logDir, "backend.log");
                RotateLogIfNeeded(logPath, MaxLogBytes, MaxArchivedLogs);
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }

    public static void RotateLogIfNeeded(string logPath, long maxBytes = MaxLogBytes, int maxArchives = MaxArchivedLogs)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var info = new FileInfo(logPath);
        if (info.Length < maxBytes)
        {
            return;
        }

        string logDir = Path.GetDirectoryName(logPath) ?? ".";
        string archiveDir = Path.Combine(logDir, "archive");
        Directory.CreateDirectory(archiveDir);

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string archivePath = Path.Combine(archiveDir, $"backend-{stamp}.log");
        File.Move(logPath, archivePath, overwrite: true);

        var archives = new DirectoryInfo(archiveDir)
            .GetFiles("backend-*.log");
        Array.Sort(archives, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
        for (int i = maxArchives; i < archives.Length; i++)
        {
            try { archives[i].Delete(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _readSemaphore.Dispose();
        _writeSemaphore.Dispose();
    }
}
