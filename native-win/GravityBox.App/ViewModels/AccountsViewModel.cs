using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    string ResetText,
    IReadOnlyList<OverviewQuotaBar>? QuotaBars = null,
    string UpdatedAtText = "",
    string CollapsedSummaryText = "",
    bool IsRecommended = false,
    string? QuotaError = null);

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
    private bool _hasAccounts;

    [ObservableProperty]
    private string _detectedLiveEmail = string.Empty;

    [ObservableProperty]
    private string _detectedLiveTier = string.Empty;

    [ObservableProperty]
    private bool _hasDetectedLiveAccount;

    [ObservableProperty]
    private bool _oauthClientConfigured;

    [ObservableProperty]
    private bool _hasRollbackAvailable;

    [ObservableProperty]
    private string _rollbackBannerText = string.Empty;

    [ObservableProperty]
    private bool _autoSwitchEnabled;

    [ObservableProperty]
    private string _autoSwitchStrategy = "priority";

    [ObservableProperty]
    private int _reservePercentage = 10;

    [ObservableProperty]
    private int _candidateMinPercentage = 30;

    [ObservableProperty]
    private string _policyStatusBadgeText = "已停用";

    [ObservableProperty]
    private string _policySummaryText = "关闭时不会主动提示切换；你仍可随时手动切换任意已保存账号";

    [ObservableProperty]
    private string _recommendedAccountBanner = string.Empty;

    [ObservableProperty]
    private string _collapseToggleText = "收起配额";

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
        _collapseToggleText = _allCollapsed ? "展开配额" : "收起配额";
    }

    partial void OnAllCollapsedChanged(bool value)
    {
        _config.Update(c => c.AccountsCollapsed = value);
        CollapseToggleText = value ? "展开配额" : "收起配额";
    }

    [RelayCommand]
    public void ToggleCollapseAll()
    {
        AllCollapsed = !AllCollapsed;
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        UiDispatch.Run(() =>
        {
            IsLoading = true;
            ErrorBanner = null;
        });
        try
        {
            var listTask = _bridge.SendAsync("account_list", cancellationToken: cancellationToken);
            var policyTask = _bridge.SendAsync("auto_switch_get", cancellationToken: cancellationToken);
            var statusTask = _bridge.SendAsync("status", new JsonObject { ["includeTasks"] = false, ["includeAccounts"] = true }, cancellationToken: cancellationToken);

            await Task.WhenAll(listTask, policyTask, statusTask).ConfigureAwait(false);

            ApplyStatusMeta(statusTask.Result);

            string? recommendedId = null;
            string recommendNote = string.Empty;
            var acctsArray = listTask.Result["accounts"]?.AsArray();
            if (acctsArray != null && acctsArray.Count > 1)
            {
                try
                {
                    var preview = await _bridge.SendAsync("auto_switch_best", cancellationToken: cancellationToken).ConfigureAwait(false);
                    var cand = preview["bestCandidate"]?.AsObject() ?? preview["account"]?.AsObject();
                    if (preview["shouldSwitch"].AsBool() && cand != null)
                    {
                        recommendedId = cand["id"].AsString();
                        string candName = cand["name"].AsString() ?? cand["email"].AsString() ?? string.Empty;
                        recommendNote = $"推荐备用：{candName} · {preview["reason"].AsString() ?? "配额充足"}";
                    }
                    else if (!string.IsNullOrWhiteSpace(preview["reason"].AsString()))
                    {
                        recommendNote = preview["reason"].AsString()!;
                    }
                }
                catch
                {
                    // Optional recommendation preview
                }
            }

            ApplyAccountList(listTask.Result, recommendedId, recommendNote);
            ApplyPolicy(policyTask.Result);
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
        finally
        {
            UiDispatch.Run(() => IsLoading = false);
        }
    }

    [RelayCommand]
    public async Task RefreshAllQuotasAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        UiDispatch.Run(() =>
        {
            IsLoading = true;
            ErrorBanner = null;
        });
        try
        {
            await _bridge.SendAsync("quota_refresh_all", cancellationToken: cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() =>
            {
                StatusToast = "已刷新全部账号配额";
                IsLoading = false;
            });
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() =>
            {
                ErrorBanner = ex.Message;
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    public async Task ToggleAutoSwitchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SavePolicyAsync(AutoSwitchEnabled, AutoSwitchStrategy, ReservePercentage, CandidateMinPercentage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    [RelayCommand]
    public async Task SaveCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            await _bridge.SendAsync("account_save_current", cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = "已保存当前账号到 GravityBox");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    public async Task ImportJsonAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths == null || paths.Count == 0) return;
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            var arr = new JsonArray();
            foreach (var p in paths) arr.Add(p);
            var res = await _bridge.SendAsync("account_import_json", new JsonObject { ["paths"] = arr }, cancellationToken: cancellationToken).ConfigureAwait(false);
            long imported = res["imported"].AsInt64(0);
            long updated = res["updated"].AsInt64(0);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = $"导入完成：新增 {imported} 个，更新 {updated} 个");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    public async Task ExportBackupAsync(string path, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            var res = await _bridge.SendAsync("account_export_backup", new JsonObject
            {
                ["path"] = path,
                ["confirmed"] = confirmed,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            long count = res["count"].AsInt64(0);
            UiDispatch.Run(() => StatusToast = $"已导出 {count} 个账号备份");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    public async Task AddAccountWithTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            await _bridge.SendAsync("account_add_token_start", new JsonObject
            {
                ["refreshToken"] = refreshToken.Trim(),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = "已提交 Refresh Token 添加请求");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> LoginWithGoogleOAuthAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            var beginRes = await _bridge.SendAsync("account_oauth_begin", cancellationToken: cancellationToken).ConfigureAwait(false);
            string sessionId = beginRes["id"].AsString() ?? string.Empty;
            string authUrl = beginRes["url"].AsString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(authUrl))
            {
                return (false, "未返回有效的授权会话");
            }

            onProgress?.Invoke("请在浏览器中完成 Google 授权登录…");
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                var st = await _bridge.SendAsync("account_oauth_status", new JsonObject { ["id"] = sessionId }, cancellationToken: cancellationToken).ConfigureAwait(false);
                string phase = st["phase"].AsString() ?? string.Empty;
                string msg = st["message"].AsString() ?? string.Empty;
                if (phase == "succeeded")
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    UiDispatch.Run(() => StatusToast = "Google 账号登录成功");
                    return (true, string.IsNullOrWhiteSpace(msg) ? "登录成功" : msg);
                }
                if (phase is "failed" or "cancelled" or "expired")
                {
                    return (false, string.IsNullOrWhiteSpace(msg) ? $"授权未完成（{phase}）" : msg);
                }
                if (phase is "exchanging" or "saving")
                {
                    onProgress?.Invoke("正在交换凭据并保存账号…");
                }
            }

            try { await _bridge.SendAsync("account_oauth_cancel", new JsonObject { ["id"] = sessionId }).ConfigureAwait(false); } catch { }
            return (false, "授权超时，已取消");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [RelayCommand]
    public async Task SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        UiDispatch.Run(() =>
        {
            ErrorBanner = null;
            IsLoading = true;
        });
        try
        {
            var preflight = await _bridge.SendAsync("account_switch_preflight", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (preflight["hasRunningTask"].AsBool() || preflight["safe"].AsBool() == false)
            {
                string reason = preflight["taskReason"].AsString() ?? "Antigravity 正在执行任务，无法切换账号";
                UiDispatch.Run(() => ErrorBanner = reason);
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
                    UiDispatch.Run(() => ErrorBanner = "等待 Antigravity 优雅退出超时，已取消切号");
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

            UiDispatch.Run(() => IsLoading = false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = "账号切换完成");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
        finally
        {
            UiDispatch.Run(() => IsLoading = false);
        }
    }

    [RelayCommand]
    public async Task RollbackSwitchAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            await _bridge.SendAsync("account_switch_rollback", cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = "已撤销切换并恢复原账号");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    [RelayCommand]
    public async Task RefreshQuotaAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            await _bridge.SendAsync("quota_refresh", new JsonObject { ["id"] = accountId }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = "配额已刷新");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    [RelayCommand]
    public async Task DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            await _bridge.SendAsync("account_delete", new JsonObject { ["id"] = accountId }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() => StatusToast = "已移除账号");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    public async Task SavePolicyAsync(bool enabled, string strategy, int reservePct, int minPct, CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        var res = await _bridge.SendAsync("auto_switch_set", new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["enabled"] = enabled,
                ["strategy"] = strategy == "round_robin" ? "round_robin" : "priority",
                ["reserve_percentage"] = Math.Clamp(reservePct, 1, 98),
                ["candidate_min_percentage"] = Math.Clamp(minPct, Math.Clamp(reservePct, 1, 98) + 1, 100),
            },
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        ApplyPolicy(res);
    }

    private void ApplyStatusMeta(JsonObject res)
    {
        var st = res["status"]?.AsObject();
        if (st == null) return;
        string liveEmail = st["liveAccountEmail"].AsString()
            ?? st["activeAccountEmail"].AsString()
            ?? string.Empty;
        string tier = st["activeSubscriptionTier"].AsString() ?? string.Empty;
        bool oauthReady = st["oauthClientConfigured"].AsBool();
        UiDispatch.Run(() =>
        {
            DetectedLiveEmail = liveEmail;
            DetectedLiveTier = tier;
            HasDetectedLiveAccount = !string.IsNullOrWhiteSpace(liveEmail);
            OauthClientConfigured = oauthReady;
        });
    }

    private void ApplyAccountList(JsonObject res, string? recommendedId, string recommendNote)
    {
        var built = new List<AccountCardItem>();
        var list = res["accounts"]?.AsArray();
        if (list != null)
        {
            foreach (var node in list)
            {
                var acc = node?.AsObject();
                if (acc == null) continue;
                string id = acc["id"].AsString() ?? string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                string email = acc["email"].AsString() ?? string.Empty;
                string name = acc["name"].AsString() ?? email;
                string tier = acc["subscription_tier"].AsString() ?? string.Empty;
                bool isActive = acc["isActive"].AsBool();
                bool standbyDeferred = acc["standbyDeferred"].AsBool();
                string standbyBadge = standbyDeferred ? "预计已重置" : string.Empty;
                string? quotaError = acc["quota_refresh_error"].AsString();

                var parsedBars = QuotaFormatting.ExtractQuotaBars(acc);
                var quotaBars = parsedBars
                    .Select(b => new OverviewQuotaBar(b.Title, b.Percentage, b.RemainingText, b.ResetText))
                    .ToList();

                double fiveHourPct = parsedBars.Count > 0
                    ? parsedBars[0].Percentage
                    : acc["five_hour_quota"]?["remaining_percentage"].AsDouble() ?? 0.0;
                string reset = parsedBars.Count > 0
                    ? parsedBars[0].ResetText
                    : (acc["five_hour_quota"]?["reset_text"].AsString() ?? string.Empty);

                long updatedAt = acc["quota"]?["last_updated"].AsInt64(0) ?? 0;
                string updatedText = updatedAt > 0
                    ? $"更新于 {QuotaFormatting.FormatUpdatedAt(updatedAt)}"
                    : string.Empty;

                string collapsedSummary = quotaBars.Count > 0
                    ? string.Join(" · ", quotaBars.Take(3).Select(q => $"{q.Title} {q.RemainingText}"))
                    : "待刷新配额";

                built.Add(new AccountCardItem(
                    id,
                    email,
                    name,
                    tier,
                    isActive,
                    standbyDeferred,
                    standbyBadge,
                    fiveHourPct,
                    reset,
                    quotaBars,
                    updatedText,
                    collapsedSummary,
                    !isActive && string.Equals(id, recommendedId, StringComparison.Ordinal),
                    quotaError));
            }
        }

        var recovery = res["recovery"]?.AsObject();
        bool rollbackAvailable = recovery?["available"].AsBool() == true;

        UiDispatch.Run(() =>
        {
            Accounts.Clear();
            foreach (var item in built) Accounts.Add(item);
            HasAccounts = Accounts.Count > 0;
            HasRollbackAvailable = rollbackAvailable;
            RollbackBannerText = rollbackAvailable ? "可撤销上次账号切换（7 天内有效）" : string.Empty;
            RecommendedAccountBanner = recommendNote;
        });
    }

    private void ApplyPolicy(JsonObject res)
    {
        var cfg = res["config"]?.AsObject() ?? res;
        bool enabled = cfg["enabled"].AsBool();
        string strategy = cfg["strategy"].AsString() ?? "priority";
        int reserve = (int)cfg["reserve_percentage"].AsInt64(cfg["reservePercentage"].AsInt64(10));
        int minCand = (int)cfg["candidate_min_percentage"].AsInt64(cfg["candidateMinPercentage"].AsInt64(30));

        string strategyName = strategy == "round_robin" ? "轮询均衡" : "按列表顺序优先";
        string summary = enabled
            ? $"{strategyName} · 当前余量低于 {reserve}% 时推荐余量 ≥ {minCand}% 的账号"
            : "已停用智能推荐；保留阈值与候选策略已保存，开启后即生效";

        UiDispatch.Run(() =>
        {
            AutoSwitchEnabled = enabled;
            AutoSwitchStrategy = strategy;
            ReservePercentage = reserve;
            CandidateMinPercentage = minCand;
            PolicyStatusBadgeText = enabled ? "已启用" : "已停用";
            PolicySummaryText = summary;
        });
    }
}
