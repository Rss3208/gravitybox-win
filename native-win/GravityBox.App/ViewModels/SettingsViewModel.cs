using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Core;

namespace GravityBox.App.ViewModels;

public sealed record ShortcutStatusItem(
    string Path,
    string Location,
    string Name,
    string Arguments,
    bool HasDebugPort,
    bool HasBackup);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private readonly LifecycleSyncService _lifecycle;

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    private bool _hudEnabled;

    [ObservableProperty]
    private int _quotaRefreshMinutes;

    [ObservableProperty]
    private bool _autoPricingSyncEnabled;

    [ObservableProperty]
    private bool _launchAtLogin;

    [ObservableProperty]
    private bool _syncAntigravityLifecycle;

    // Maintenance state
    [ObservableProperty]
    private long _runtimeCacheBytes;

    [ObservableProperty]
    private long _logsBytes;

    [ObservableProperty]
    private long _protectedBytes;

    [ObservableProperty]
    private string _runtimeCacheSizeText = "0 B";

    [ObservableProperty]
    private string _logsSizeText = "0 B";

    [ObservableProperty]
    private string _protectedSizeText = "0 B";

    [ObservableProperty]
    private string _storageSummaryText = "可撤回清理运行缓存与超期日志，支持一键原位还原";

    [ObservableProperty]
    private string _shortcutsSummaryText = "管理桌面与开始菜单快捷方式调试参数，支持一键无损还原";

    [ObservableProperty]
    private bool _clearRuntimeCache = true;

    [ObservableProperty]
    private bool _clearLogs = true;

    [ObservableProperty]
    private int _logRetentionDays = 7;

    [ObservableProperty]
    private string _latestSnapshotId = string.Empty;

    [ObservableProperty]
    private bool _hasRestorePoint;

    // Pricing sync & revalue state
    [ObservableProperty]
    private string _pricingStatusText = "核对远端价格表或对历史账单执行可回滚重估";

    [ObservableProperty]
    private string _revaluePreviewId = string.Empty;

    [ObservableProperty]
    private string _revaluePreviewSummary = string.Empty;

    [ObservableProperty]
    private string? _errorBanner;

    public ObservableCollection<ShortcutStatusItem> Shortcuts { get; } = new();

    public SettingsViewModel(
        IBackendBridge bridge,
        ConfigurationStore config,
        L10nService l10n,
        LifecycleSyncService lifecycle)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        _selectedLanguage = L10nService.NormalizeLanguageCode(_config.Value.AppLanguage);
        _hudEnabled = _config.Value.HudEnabled;
        _quotaRefreshMinutes = _config.Value.QuotaRefreshMinutes;
        _autoPricingSyncEnabled = _config.Value.AutoPricingSyncEnabled;
        _launchAtLogin = _config.Value.LaunchAtLogin;
        _syncAntigravityLifecycle = _config.Value.SyncAntigravityLifecycle;
    }

    partial void OnQuotaRefreshMinutesChanged(int value)
    {
        int normalized = value is 0 or 5 or 10 or 15 or 30 or 60 ? value : 0;
        if (normalized != value)
        {
            QuotaRefreshMinutes = normalized;
            return;
        }
        _config.Update(c => c.QuotaRefreshMinutes = normalized);
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.WhenAll(
                ScanShortcutsAsync(cancellationToken),
                ScanMaintenanceAsync(cancellationToken),
                LoadPricingStatusAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            // Individual loaders report ErrorBanner if needed
        }
    }

    private async Task LoadPricingStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var res = await _bridge.SendAsync("pricing_sync_status", cancellationToken: cancellationToken).ConfigureAwait(false);
            var status = res["status"]?.AsObject();
            string? msg = status?["message"].AsString();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                UiDispatch.Run(() => PricingStatusText = msg);
            }
        }
        catch
        {
            // Keep default subtitle
        }
    }

    public async Task SetLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        string normalized = L10nService.NormalizeLanguageCode(language);
        SelectedLanguage = normalized;
        _config.Update(c => c.AppLanguage = normalized);
        _l10n.SetLanguage(normalized);
        await _bridge.SendAsync("language_set", new JsonObject { ["language"] = normalized }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetHudEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        HudEnabled = enabled;
        _config.Update(c => c.HudEnabled = enabled);
        await _bridge.SendAsync("hud_set", new JsonObject { ["enabled"] = enabled }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    [RelayCommand]
    public void SelectQuotaRefreshMinutes(object? minutesParam)
    {
        if (minutesParam is int i)
        {
            QuotaRefreshMinutes = i;
        }
        else if (int.TryParse(minutesParam?.ToString(), out int parsed))
        {
            QuotaRefreshMinutes = parsed;
        }
    }

    public void SetLaunchAtLogin(bool enabled, string? exePath = null)
    {
        LaunchAtLogin = enabled;
        _config.Update(c => c.LaunchAtLogin = enabled);
        _lifecycle.SetLaunchAtLogin(enabled, exePath);
    }

    public void SetSyncAntigravityLifecycle(bool enabled)
    {
        SyncAntigravityLifecycle = enabled;
        _config.Update(c => c.SyncAntigravityLifecycle = enabled);
    }

    public async Task SetAutoPricingSyncAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        AutoPricingSyncEnabled = enabled;
        _config.Update(c => c.AutoPricingSyncEnabled = enabled);
        await _bridge.SendAsync("pricing_sync_set", new JsonObject { ["enabled"] = enabled }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task ScanShortcutsAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        var res = await _bridge.SendAsync("shortcut_scan", cancellationToken: cancellationToken).ConfigureAwait(false);
        var built = new List<ShortcutStatusItem>();
        var arr = res["shortcuts"]?.AsArray();
        if (arr != null)
        {
            foreach (var node in arr)
            {
                var obj = node?.AsObject();
                if (obj == null) continue;
                built.Add(new ShortcutStatusItem(
                    obj["path"].AsString() ?? string.Empty,
                    obj["location"].AsString() ?? "custom",
                    obj["name"].AsString() ?? "Antigravity",
                    obj["arguments"].AsString() ?? string.Empty,
                    obj["hasDebugPort"].AsBool(),
                    obj["hasBackup"].AsBool()));
            }
        }
        int enabledCount = built.Count(s => s.HasDebugPort);
        string summary = built.Count > 0
            ? $"已扫描 {built.Count} 个快捷方式（{enabledCount} 个已启用无感调试参数）"
            : "未检测到 Antigravity 官方快捷方式";

        UiDispatch.Run(() =>
        {
            Shortcuts.Clear();
            foreach (var s in built) Shortcuts.Add(s);
            ShortcutsSummaryText = summary;
        });
    }

    [RelayCommand]
    public async Task ApplyAllShortcutsAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        await _bridge.SendAsync("shortcut_apply", new JsonObject { ["confirmed"] = true }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ScanShortcutsAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task RestoreAllShortcutsAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        await _bridge.SendAsync("shortcut_restore", new JsonObject { ["confirmed"] = true }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ScanShortcutsAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task ScanMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        var scanTask = _bridge.SendAsync("maintenance_scan", cancellationToken: cancellationToken);
        var histTask = _bridge.SendAsync("maintenance_history", cancellationToken: cancellationToken);
        await Task.WhenAll(scanTask, histTask).ConfigureAwait(false);

        var scan = scanTask.Result;
        long runtimeBytes = scan["buckets"]?["runtimeCache"]?["bytes"].AsInt64(0)
            ?? scan["runtimeBytes"].AsInt64(0);
        long logsBytes = scan["buckets"]?["historyLogs"]?["bytes"].AsInt64(0)
            ?? scan["logsBytes"].AsInt64(0);
        long protBytes = scan["protectedBytes"].AsInt64(0);

        var hist = histTask.Result["snapshots"]?.AsArray();
        string snapId = string.Empty;
        if (hist != null && hist.Count > 0)
        {
            snapId = hist[0]?["id"].AsString() ?? string.Empty;
        }
        long releasable = runtimeBytes + logsBytes;
        string summary = releasable > 0
            ? $"可清理运行缓存 {QuotaFormatting.FormatBytes(runtimeBytes)} · 历史日志 {QuotaFormatting.FormatBytes(logsBytes)} · 已保护会话 {QuotaFormatting.FormatBytes(protBytes)}"
            : $"存储整洁 · 已保护会话资产 {QuotaFormatting.FormatBytes(protBytes)}";

        UiDispatch.Run(() =>
        {
            RuntimeCacheBytes = runtimeBytes;
            LogsBytes = logsBytes;
            ProtectedBytes = protBytes;
            RuntimeCacheSizeText = QuotaFormatting.FormatBytes(runtimeBytes);
            LogsSizeText = QuotaFormatting.FormatBytes(logsBytes);
            ProtectedSizeText = QuotaFormatting.FormatBytes(protBytes);
            StorageSummaryText = summary;
            LatestSnapshotId = snapId;
            HasRestorePoint = !string.IsNullOrEmpty(snapId);
        });
    }

    [RelayCommand]
    public async Task RunSmartClearAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        var preflight = await _bridge.SendAsync("maintenance_preflight", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (preflight["ok"].AsBool() == false)
        {
            UiDispatch.Run(() => ErrorBanner = preflight["error"].AsString() ?? "清理预检未通过");
            return;
        }

        var scopes = new JsonArray();
        if (ClearRuntimeCache) scopes.Add("runtime");
        if (ClearLogs) scopes.Add("logs");
        await _bridge.SendAsync("maintenance_smart_clear", new JsonObject
        {
            ["scopes"] = scopes,
            ["retentionDays"] = LogRetentionDays,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await ScanMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task RestoreLatestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(LatestSnapshotId)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        await _bridge.SendAsync("maintenance_restore", new JsonObject { ["snapshotId"] = LatestSnapshotId }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ScanMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task CheckPricingSyncAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        var res = await _bridge.SendAsync("pricing_sync_check", cancellationToken: cancellationToken).ConfigureAwait(false);
        string msg = res["status"]?["message"].AsString() ?? "价格表已核对";
        UiDispatch.Run(() => PricingStatusText = msg);
    }

    [RelayCommand]
    public async Task PreviewRevalueAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        var res = await _bridge.SendAsync("api_pricing_revalue_preview", new JsonObject { ["mode"] = "force_all" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var prev = res["preview"]?.AsObject();
        string pid = prev?["previewId"].AsString() ?? string.Empty;
        double before = prev?["before"].AsDouble() ?? 0.0;
        double after = prev?["after"].AsDouble() ?? 0.0;
        UiDispatch.Run(() =>
        {
            RevaluePreviewId = pid;
            RevaluePreviewSummary = $"重估预览: ${before:0.00} → ${after:0.00}";
        });
    }

    [RelayCommand]
    public async Task ApplyRevalueAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(RevaluePreviewId)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        await _bridge.SendAsync("api_pricing_revalue", new JsonObject
        {
            ["confirm"] = true,
            ["previewId"] = RevaluePreviewId,
            ["mode"] = "force_all",
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        UiDispatch.Run(() => RevaluePreviewSummary = "历史账单重估已应用");
    }

    [RelayCommand]
    public async Task RollbackRevalueAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(RevaluePreviewId)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        await _bridge.SendAsync("api_pricing_revalue_rollback", new JsonObject
        {
            ["confirm"] = true,
            ["previewId"] = RevaluePreviewId,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        UiDispatch.Run(() => RevaluePreviewSummary = "已回滚历史账单重估");
    }
}
