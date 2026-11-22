using System;
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
    private string _pricingStatusText = string.Empty;

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

        _selectedLanguage = _config.Value.AppLanguage ?? "zh-Hans";
        _hudEnabled = _config.Value.HudEnabled;
        _quotaRefreshMinutes = _config.Value.QuotaRefreshMinutes;
        _autoPricingSyncEnabled = _config.Value.AutoPricingSyncEnabled;
        _launchAtLogin = _config.Value.LaunchAtLogin;
        _syncAntigravityLifecycle = _config.Value.SyncAntigravityLifecycle;
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
        ErrorBanner = null;
        var res = await _bridge.SendAsync("shortcut_scan", cancellationToken: cancellationToken).ConfigureAwait(false);
        Shortcuts.Clear();
        var arr = res["shortcuts"]?.AsArray();
        if (arr == null) return;
        foreach (var node in arr)
        {
            var obj = node?.AsObject();
            if (obj == null) continue;
            Shortcuts.Add(new ShortcutStatusItem(
                obj["path"].AsString() ?? string.Empty,
                obj["location"].AsString() ?? "custom",
                obj["name"].AsString() ?? "Antigravity",
                obj["arguments"].AsString() ?? string.Empty,
                obj["hasDebugPort"].AsBool(),
                obj["hasBackup"].AsBool()));
        }
    }

    [RelayCommand]
    public async Task ApplyAllShortcutsAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        await _bridge.SendAsync("shortcut_apply", new JsonObject { ["confirmed"] = true }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ScanShortcutsAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task RestoreAllShortcutsAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        await _bridge.SendAsync("shortcut_restore", new JsonObject { ["confirmed"] = true }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ScanShortcutsAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task ScanMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        var scanTask = _bridge.SendAsync("maintenance_scan", cancellationToken: cancellationToken);
        var histTask = _bridge.SendAsync("maintenance_history", cancellationToken: cancellationToken);
        await Task.WhenAll(scanTask, histTask).ConfigureAwait(false);

        var scan = scanTask.Result;
        RuntimeCacheBytes = scan["runtimeBytes"].AsInt64(0);
        LogsBytes = scan["logsBytes"].AsInt64(0);
        ProtectedBytes = scan["protectedBytes"].AsInt64(0);

        var hist = histTask.Result["snapshots"]?.AsArray();
        if (hist != null && hist.Count > 0)
        {
            LatestSnapshotId = hist[0]?["id"].AsString() ?? string.Empty;
            HasRestorePoint = !string.IsNullOrEmpty(LatestSnapshotId);
        }
        else
        {
            LatestSnapshotId = string.Empty;
            HasRestorePoint = false;
        }
    }

    [RelayCommand]
    public async Task RunSmartClearAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        var preflight = await _bridge.SendAsync("maintenance_preflight", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (preflight["ok"].AsBool() == false)
        {
            ErrorBanner = preflight["error"].AsString() ?? "清理预检未通过";
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
        ErrorBanner = null;
        await _bridge.SendAsync("maintenance_restore", new JsonObject { ["snapshotId"] = LatestSnapshotId }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ScanMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task CheckPricingSyncAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        var res = await _bridge.SendAsync("pricing_sync_check", cancellationToken: cancellationToken).ConfigureAwait(false);
        PricingStatusText = res["status"]?["message"].AsString() ?? "价格表已核对";
    }

    [RelayCommand]
    public async Task PreviewRevalueAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        var res = await _bridge.SendAsync("api_pricing_revalue_preview", new JsonObject { ["mode"] = "force_all" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var prev = res["preview"]?.AsObject();
        RevaluePreviewId = prev?["previewId"].AsString() ?? string.Empty;
        double before = prev?["before"].AsDouble() ?? 0.0;
        double after = prev?["after"].AsDouble() ?? 0.0;
        RevaluePreviewSummary = $"重估预览: ${before:0.00} → ${after:0.00}";
    }

    [RelayCommand]
    public async Task ApplyRevalueAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(RevaluePreviewId)) return;
        ErrorBanner = null;
        await _bridge.SendAsync("api_pricing_revalue", new JsonObject
        {
            ["confirm"] = true,
            ["previewId"] = RevaluePreviewId,
            ["mode"] = "force_all",
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        RevaluePreviewSummary = "历史账单重估已应用";
    }

    [RelayCommand]
    public async Task RollbackRevalueAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(RevaluePreviewId)) return;
        ErrorBanner = null;
        await _bridge.SendAsync("api_pricing_revalue_rollback", new JsonObject
        {
            ["confirm"] = true,
            ["previewId"] = RevaluePreviewId,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        RevaluePreviewSummary = "已回滚历史账单重估";
    }
}
