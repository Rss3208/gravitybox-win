using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GravityBox.App.Core;

public enum UpdateStatusKind
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Error,
}

public sealed record ReleaseAssetInfo(
    string Version,
    string InstallerUrl,
    string Sha256SidecarUrl,
    string ReleaseNotesUrl);

public sealed class AppUpdateManager : IDisposable
{
    private static readonly Regex SemVerRegex = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);
    private static readonly Regex SidecarRegex = new(@"^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private CancellationTokenSource? _downloadCts;

    public string CurrentVersion { get; }
    public string ReleasesApiUrl { get; }
    public UpdateStatusKind Status { get; private set; } = UpdateStatusKind.Idle;
    public ReleaseAssetInfo? LatestRelease { get; private set; }
    public double DownloadProgress { get; private set; }
    public string StatusMessage { get; private set; } = string.Empty;

    public event Action? StateChanged;

    public AppUpdateManager(
        string currentVersion,
        string releasesApiUrl = "https://api.github.com/repos/gravitybox/gravitybox/releases/latest",
        HttpClient? httpClient = null)
    {
        CurrentVersion = NormalizeVersion(currentVersion);
        ReleasesApiUrl = releasesApiUrl;
        _ownsHttp = httpClient == null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("GravityBox-Windows/1.1.3");
        }
    }

    public static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
        var m = SemVerRegex.Match(raw);
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : "0.0.0";
    }

    public static int CompareVersions(string a, string b)
    {
        var va = Version.Parse(NormalizeVersion(a));
        var vb = Version.Parse(NormalizeVersion(b));
        return va.CompareTo(vb);
    }

    public static string ParseSha256Sidecar(string text, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("Empty sha256 sidecar");
        }
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            throw new InvalidDataException($"Expected 1 line in sha256 sidecar, got {lines.Length}");
        }
        var m = SidecarRegex.Match(lines[0]);
        if (!m.Success)
        {
            throw new InvalidDataException("Invalid sha256 sidecar format");
        }
        string hash = m.Groups[1].Value.ToLowerInvariant();
        string fileName = m.Groups[2].Value.Trim();
        if (!string.IsNullOrEmpty(expectedFileName) && !string.Equals(fileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Sha256 sidecar filename mismatch: {fileName} != {expectedFileName}");
        }
        return hash;
    }

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<ReleaseAssetInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        SetState(UpdateStatusKind.Checking, "正在检查更新…");
        try
        {
            string json = await _http.GetStringAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? string.Empty) : string.Empty;
            string remoteVer = NormalizeVersion(tag);
            string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? (urlEl.GetString() ?? string.Empty) : string.Empty;

            if (CompareVersions(remoteVer, CurrentVersion) <= 0)
            {
                LatestRelease = null;
                SetState(UpdateStatusKind.UpToDate, $"当前已是最新版本 ({CurrentVersion})");
                return null;
            }

            string expectedExeName = $"GravityBox-{remoteVer}-win-x64-setup.exe";
            string expectedShaName = $"{expectedExeName}.sha256";
            string? exeUrl = null;
            string? shaUrl = null;

            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? string.Empty) : string.Empty;
                    string dl = asset.TryGetProperty("browser_download_url", out var dEl) ? (dEl.GetString() ?? string.Empty) : string.Empty;
                    if (string.Equals(name, expectedExeName, StringComparison.OrdinalIgnoreCase)) exeUrl = dl;
                    else if (string.Equals(name, expectedShaName, StringComparison.OrdinalIgnoreCase)) shaUrl = dl;
                }
            }

            if (string.IsNullOrEmpty(exeUrl) || string.IsNullOrEmpty(shaUrl))
            {
                SetState(UpdateStatusKind.Error, "新版本尚未提供完整的 Windows 安装包或 SHA-256 校验文件");
                return null;
            }

            LatestRelease = new ReleaseAssetInfo(remoteVer, exeUrl, shaUrl, htmlUrl);
            SetState(UpdateStatusKind.Available, $"发现新版本 {remoteVer}");
            return LatestRelease;
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateStatusKind.Idle, "已取消检查更新");
            return null;
        }
        catch (Exception ex)
        {
            SetState(UpdateStatusKind.Error, ex.Message);
            return null;
        }
    }

    public async Task<string> DownloadAndVerifyAsync(
        ReleaseAssetInfo release,
        string downloadDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        Directory.CreateDirectory(downloadDir);

        _downloadCts?.Dispose();
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _downloadCts.Token;

        string fileName = $"GravityBox-{release.Version}-win-x64-setup.exe";
        string targetPath = Path.Combine(downloadDir, fileName);
        string tempPath = targetPath + ".downloading";

        DownloadProgress = 0;
        SetState(UpdateStatusKind.Downloading, $"正在下载 {fileName}…");
        try
        {
            string sidecarText = await _http.GetStringAsync(release.Sha256SidecarUrl, ct).ConfigureAwait(false);
            string expectedHash = ParseSha256Sidecar(sidecarText, fileName);

            using var resp = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? totalBytes = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long readTotal = 0;
                while (true)
                {
                    int n = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    readTotal += n;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        DownloadProgress = Math.Clamp((double)readTotal / totalBytes.Value, 0.0, 1.0);
                        StateChanged?.Invoke();
                    }
                }
            }

            string actualHash = await ComputeFileSha256Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempPath); } catch { }
                throw new InvalidDataException($"安装包 SHA-256 校验失败: expected {expectedHash}, got {actualHash}");
            }

            File.Move(tempPath, targetPath, overwrite: true);
            DownloadProgress = 1.0;
            SetState(UpdateStatusKind.ReadyToInstall, "下载并校验完成，准备安装");
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            SetState(UpdateStatusKind.Idle, "已取消下载");
            throw;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            SetState(UpdateStatusKind.Error, ex.Message);
            throw;
        }
    }

    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private void SetState(UpdateStatusKind status, string message)
    {
        Status = status;
        StatusMessage = message;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _downloadCts?.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
