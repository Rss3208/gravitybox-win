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

public sealed record AccountCardItem(
    string Id,
    string Email,
    string Name,
    string Tier,
    bool IsActive,
    bool StandbyDeferred,
    string StandbyBadgeText,
    double FiveHourPct,
    string ResetText);

public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private readonly LifecycleSyncService _lifecycle;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _allCollapsed;

    [ObservableProperty]
    private bool _hasRollbackAvailable;

    [ObservableProperty]
    private string _rollbackBannerText = string.Empty;

    [ObservableProperty]
    private bool _autoSwitchEnabled;

    [ObservableProperty]
    private string _autoSwitchStrategy = "priority";

    [ObservableProperty]
    private int _reservePercentage = 20;

    [ObservableProperty]
    private int _candidateMinPercentage = 30;

    [ObservableProperty]
    private string? _errorBanner;

    [ObservableProperty]
    private string? _statusToast;

    public ObservableCollection<AccountCardItem> Accounts { get; } = new();

    public AccountsViewModel(
        IBackendBridge bridge,
        ConfigurationStore config,
        L10nService l10n,
        LifecycleSyncService lifecycle)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _allCollapsed = _config.Value.AccountsCollapsed;
    }

    partial void OnAllCollapsedChanged(bool value)
    {
        _config.Update(c => c.AccountsCollapsed = value);
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorBanner = null;
        try
        {
            var listTask = _bridge.SendAsync("account_list", cancellationToken: cancellationToken);
            var policyTask = _bridge.SendAsync("auto_switch_get", cancellationToken: cancellationToken);
            await Task.WhenAll(listTask, policyTask).ConfigureAwait(false);

            ApplyAccountList(listTask.Result);
            ApplyPolicy(policyTask.Result);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task SaveCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        try
        {
            await _bridge.SendAsync("account_save_current", cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            StatusToast = "已保存当前账号";
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    public async Task ImportJsonAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths == null || paths.Count == 0) return;
        ErrorBanner = null;
        try
        {
            var arr = new JsonArray();
            foreach (var p in paths) arr.Add(p);
            await _bridge.SendAsync("account_import_json", new JsonObject { ["paths"] = arr }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    public async Task ExportBackupAsync(string path, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        ErrorBanner = null;
        try
        {
            await _bridge.SendAsync("account_export_backup", new JsonObject
            {
                ["path"] = path,
                ["confirmed"] = confirmed,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            StatusToast = "已导出账号备份";
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    public async Task AddAccountWithTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        ErrorBanner = null;
        await _bridge.SendAsync("account_add_token_start", new JsonObject
        {
            ["refreshToken"] = refreshToken.Trim(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        ErrorBanner = null;
        IsLoading = true;
        try
        {
            var preflight = await _bridge.SendAsync("account_switch_preflight", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (preflight["hasRunningTask"].AsBool() || preflight["safe"].AsBool() == false)
            {
                string reason = preflight["taskReason"].AsString() ?? "Antigravity 正在执行任务，无法切换账号";
                ErrorBanner = reason;
                return;
            }

            bool wasRunning = preflight["appRunning"].AsBool();
            string exePath = string.Empty;
            string profileDir = string.Empty;

            if (wasRunning)
            {
                var info = await _bridge.SendAsync("platform_info", cancellationToken: cancellationToken).ConfigureAwait(false);
                exePath = info["executablePath"].AsString() ?? string.Empty;
                profileDir = info["profileDir"].AsString() ?? string.Empty;
                _lifecycle.SuppressLifecycleAutoQuit(TimeSpan.FromSeconds(30));
                bool closed = await _lifecycle.QuitAntigravityAndWaitAsync(exePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!closed)
                {
                    ErrorBanner = "等待 Antigravity 优雅退出超时，已取消切号";
                    return;
                }
            }

            await _bridge.SendAsync("account_switch", new JsonObject
            {
                ["id"] = accountId,
                ["target"] = "all",
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (wasRunning && !string.IsNullOrWhiteSpace(exePath))
            {
                await _lifecycle.OpenAntigravityAsync(exePath, profileDir, cancellationToken).ConfigureAwait(false);
            }

            IsLoading = false;
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RollbackSwitchAsync(CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        try
        {
            await _bridge.SendAsync("account_switch_rollback", cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    [RelayCommand]
    public async Task RefreshQuotaAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        ErrorBanner = null;
        try
        {
            await _bridge.SendAsync("quota_refresh", new JsonObject { ["id"] = accountId }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    [RelayCommand]
    public async Task DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        ErrorBanner = null;
        try
        {
            await _bridge.SendAsync("account_delete", new JsonObject { ["id"] = accountId }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    public async Task SavePolicyAsync(bool enabled, string strategy, int reservePct, int minPct, CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        var res = await _bridge.SendAsync("auto_switch_set", new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["enabled"] = enabled,
                ["strategy"] = strategy,
                ["reserve_percentage"] = reservePct,
                ["candidate_min_percentage"] = minPct,
            },
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        ApplyPolicy(res);
    }

    private void ApplyAccountList(JsonObject res)
    {
        Accounts.Clear();
        var list = res["accounts"]?.AsArray();
        if (list != null)
        {
            foreach (var node in list)
            {
                var acc = node?.AsObject();
                if (acc == null) continue;
                string id = acc["id"].AsString() ?? string.Empty;
                string email = acc["email"].AsString() ?? string.Empty;
                string name = acc["name"].AsString() ?? email;
                string tier = acc["subscription_tier"].AsString() ?? string.Empty;
                bool isActive = acc["isActive"].AsBool();
                bool standbyDeferred = acc["standbyDeferred"].AsBool();
                string standbyBadge = standbyDeferred ? "预计已重置" : string.Empty;
                double pct = acc["five_hour_quota"]?["remaining_percentage"].AsDouble() ?? 0.0;
                string reset = acc["five_hour_quota"]?["reset_text"].AsString() ?? string.Empty;
                if (!string.IsNullOrEmpty(id))
                {
                    Accounts.Add(new AccountCardItem(id, email, name, tier, isActive, standbyDeferred, standbyBadge, pct, reset));
                }
            }
        }

        var recovery = res["recovery"]?.AsObject();
        HasRollbackAvailable = recovery?["available"].AsBool() == true;
        RollbackBannerText = HasRollbackAvailable ? "可撤销上次账号切换（7 天内有效）" : string.Empty;
    }

    private void ApplyPolicy(JsonObject res)
    {
        var cfg = res["config"]?.AsObject() ?? res;
        AutoSwitchEnabled = cfg["enabled"].AsBool();
        AutoSwitchStrategy = cfg["strategy"].AsString() ?? "priority";
        ReservePercentage = (int)cfg["reserve_percentage"].AsInt64(20);
        CandidateMinPercentage = (int)cfg["candidate_min_percentage"].AsInt64(30);
    }
}
