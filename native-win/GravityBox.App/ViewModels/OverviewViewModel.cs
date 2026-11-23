using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Controls;
using GravityBox.App.Core;

namespace GravityBox.App.ViewModels;

public sealed record OverviewQuotaBar(string Title, double Percentage, string RemainingText, string ResetText);

public sealed record OverviewActiveModel(
    string ModelName,
    string TokensText,
    string CostText,
    string SubtitleText = "",
    double SharePct = 0.0);

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private readonly LifecycleSyncService _lifecycle;

    private string _lastDashboardFingerprint = string.Empty;
    private DateTimeOffset _lastDashboardRefreshAt = DateTimeOffset.MinValue;
    private string _lastKnownStatusMode = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isRelaunching;

    [ObservableProperty]
    private bool _debugPortMissing;

    [ObservableProperty]
    private string? _statusToast;

    [ObservableProperty]
    private bool _showShortcutOnboardingCard;

    [ObservableProperty]
    private string _unconfiguredShortcutCountText = string.Empty;

    [ObservableProperty]
    private string _heroAccountName = "尚未保存账号";

    [ObservableProperty]
    private string _heroAccountEmail = string.Empty;

    [ObservableProperty]
    private string _heroAccountTier = string.Empty;

    [ObservableProperty]
    private string _heroStatusNote = string.Empty;

    [ObservableProperty]
    private bool _hasQuotaBars;

    [ObservableProperty]
    private string _todayTokensText = "0";

    [ObservableProperty]
    private string _totalTokensSubtext = "累计 0 Token";

    [ObservableProperty]
    private string _todayRequestsText = "0";

    [ObservableProperty]
    private string _totalRequestsSubtext = "累计 0 次";

    [ObservableProperty]
    private string _todayCostText = "$0.00";

    [ObservableProperty]
    private string _totalCostSubtext = "累计 $0.00";

    [ObservableProperty]
    private string _trendBadgeText = "24 小时分布";

    [ObservableProperty]
    private string _activeModelsBadgeText = "暂无调用";

    [ObservableProperty]
    private bool _hasActiveModels;

    [ObservableProperty]
    private string? _errorBanner;

    [ObservableProperty]
    private IReadOnlyList<TrendPoint> _trendPoints = Array.Empty<TrendPoint>();

    public ObservableCollection<OverviewQuotaBar> QuotaBars { get; } = new();
    public ObservableCollection<OverviewActiveModel> ActiveModels { get; } = new();

    public OverviewViewModel(
        IBackendBridge bridge,
        ConfigurationStore config,
        L10nService l10n,
        LifecycleSyncService lifecycle)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    [RelayCommand]
    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        if (!force && (DateTimeOffset.UtcNow - _lastDashboardRefreshAt) < TimeSpan.FromSeconds(15))
        {
            return;
        }

        UiDispatch.Run(() =>
        {
            IsLoading = true;
            ErrorBanner = null;
        });
        try
        {
            var statusTask = _bridge.SendAsync("status", new JsonObject { ["includeTasks"] = false }, cancellationToken: cancellationToken);
            var dashTask = _bridge.SendAsync("dashboard_summary", cancellationToken: cancellationToken);
            Task<JsonObject?> shortcutTask = _config.Value.ShortcutGuideDismissed
                ? Task.FromResult<JsonObject?>(null)
                : _bridge.SendAsync("shortcut_scan", cancellationToken: cancellationToken).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, cancellationToken);

            await Task.WhenAll((Task)statusTask, dashTask, shortcutTask).ConfigureAwait(false);

            ApplyStatus(statusTask.Result);
            ApplyDashboard(dashTask.Result);
            ApplyShortcuts(shortcutTask.Result);
            _lastDashboardRefreshAt = DateTimeOffset.UtcNow;
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
    public async Task RefreshActiveQuotaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _bridge.SendAsync("quota_refresh", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore partial quota errors here and still refresh dashboard
        }
        await RefreshAsync(force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task ApplySeamlessShortcutsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _bridge.SendAsync(
                "shortcut_apply",
                new JsonObject { ["confirmed"] = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            int updatedCount = res["updated"]?.AsArray()?.Count ?? 0;
            UiDispatch.Run(() =>
            {
                ShowShortcutOnboardingCard = false;
                StatusToast = updatedCount > 0
                    ? $"已为 {updatedCount} 个官方快捷方式开启无感启动！后续无论先启动哪一方，从桌面 / 开始菜单 / 任务栏打开 Antigravity 均会直连。"
                    : "快捷方式已处于无感启动状态。";
            });
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    [RelayCommand]
    public void DismissShortcutOnboarding()
    {
        _config.Update(c => c.ShortcutGuideDismissed = true);
        UiDispatch.Run(() => ShowShortcutOnboardingCard = false);
    }

    [RelayCommand]
    public async Task SafeRelaunchAsync(CancellationToken cancellationToken = default)
    {
        if (IsRelaunching) return;
        UiDispatch.Run(() =>
        {
            IsRelaunching = true;
            ErrorBanner = null;
            StatusToast = "正在安全请求 Antigravity 退出并带调试端口重启，请稍候…";
        });
        try
        {
            var info = await _bridge.SendAsync("platform_info", cancellationToken: cancellationToken).ConfigureAwait(false);
            string exe = info["executablePath"].AsString() ?? string.Empty;
            string profile = info["profileDir"].AsString() ?? string.Empty;
            var res = await _lifecycle.SafeRelaunchAsync(exe, profile, cancellationToken).ConfigureAwait(false);
            if (!res.Ok)
            {
                UiDispatch.Run(() =>
                {
                    StatusToast = null;
                    ErrorBanner = res.Message;
                });
            }
            else
            {
                UiDispatch.Run(() =>
                {
                    DebugPortMissing = false;
                    StatusToast = "Antigravity 已携带调试端口重启，正在挂载热汉化与 HUD…";
                });
                await Task.Delay(800, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() =>
            {
                StatusToast = null;
                ErrorBanner = ex.Message;
            });
        }
        finally
        {
            UiDispatch.Run(() => IsRelaunching = false);
        }
    }

    public void SyncLiveStatus(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        string prev = _lastKnownStatusMode;
        if (prev == mode) return;
        _lastKnownStatusMode = mode;
        bool missing = mode == "debug-port-missing";

        UiDispatch.Run(() =>
        {
            DebugPortMissing = missing;
            if (!missing && prev == "debug-port-missing")
            {
                StatusToast = "已直连 Antigravity，热汉化与 HUD 已就绪";
            }
        });

        if (prev == "debug-port-missing" && mode is "connected" or "adaptive")
        {
            _ = RefreshAsync(force: true);
        }
    }

    private void ApplyStatus(JsonObject res)
    {
        var st = res["status"]?.AsObject();
        if (st == null) return;
        string mode = st["mode"].AsString() ?? "waiting";
        _lastKnownStatusMode = mode;
        string email = st["activeAccountEmail"].AsString()
            ?? st["liveAccountEmail"].AsString()
            ?? string.Empty;
        string tier = st["activeSubscriptionTier"].AsString() ?? string.Empty;
        string name = string.IsNullOrWhiteSpace(email)
            ? "实时账号未保存"
            : email.Split('@')[0];

        UiDispatch.Run(() =>
        {
            DebugPortMissing = mode == "debug-port-missing";
            HeroAccountEmail = email;
            if (!string.IsNullOrWhiteSpace(tier)) HeroAccountTier = tier;
            HeroAccountName = name;
        });
    }

    private void ApplyShortcuts(JsonObject? res)
    {
        var arr = res?["shortcuts"]?.AsArray();
        if (arr == null || _config.Value.ShortcutGuideDismissed)
        {
            UiDispatch.Run(() => ShowShortcutOnboardingCard = false);
            return;
        }

        int unconfigured = arr.Count(item => item?["hasDebugPort"].AsBool() != true);
        UiDispatch.Run(() =>
        {
            ShowShortcutOnboardingCard = unconfigured > 0;
            UnconfiguredShortcutCountText = $"发现 {unconfigured} 个官方快捷方式尚未配置无感调试参数";
        });
    }

    private void ApplyDashboard(JsonObject res)
    {
        string fp = res.ToJsonString();
        if (fp == _lastDashboardFingerprint)
        {
            return;
        }
        _lastDashboardFingerprint = fp;

        var acc = res["accounts"]?.AsObject();
        string? acctName = acc?["activeName"].AsString();
        string? acctEmail = acc?["activeEmail"].AsString() ?? acc?["liveAccountEmail"].AsString() ?? acc?["currentEmail"].AsString();
        string? acctTier = acc?["activeTier"].AsString();
        long quotaUpdated = acc?["quotaLastUpdated"].AsInt64(0) ?? 0;
        long totalSaved = acc?["total"].AsInt64(0) ?? 0;
        string statusNote = quotaUpdated > 0
            ? $"更新于 {QuotaFormatting.FormatUpdatedAt(quotaUpdated)}"
            : totalSaved == 0 ? "前往「账号」页即可保存当前登录账号" : string.Empty;

        var parsedBars = QuotaFormatting.ExtractQuotaBars(acc ?? res);
        if (parsedBars.Count == 0 && acc != null)
        {
            parsedBars = QuotaFormatting.ExtractQuotaBars(res);
        }

        var usage = res["usage"]?.AsObject();
        long todayTok = 0;
        long allTok = 0;
        long todayReqs = 0;
        long allReqs = 0;
        double todayUsd = 0;
        double allUsd = 0;
        var trendPts = new List<TrendPoint>();
        var activeModels = new List<OverviewActiveModel>();
        string trendBadge = "24 小时分布";
        string modelsBadge = "暂无调用";

        if (usage != null)
        {
            var today = usage["today"]?.AsObject();
            todayTok = today?["total_tokens"].AsInt64(0)
                ?? usage["todayTokens"].AsInt64(0);
            allTok = usage["totalTokens"].AsInt64(todayTok);
            todayReqs = today?["requests"].AsInt64(0)
                ?? usage["requestCount"].AsInt64(0);
            allReqs = usage["totalRequestCount"].AsInt64(todayReqs);
            todayUsd = today?["estimated_usd"].AsNullableDouble()
                ?? usage["estimatedUsd"].AsDouble();
            allUsd = usage["allTimeEstimatedUsd"].AsNullableDouble() ?? todayUsd;

            var hourly = usage["todayHourly"]?.AsArray()
                ?? today?["hourly"]?.AsArray();
            trendPts = QuotaFormatting.BuildHourlyTrendPoints(hourly, padFull24Hours: true);
            if (trendPts.Count > 0)
            {
                var peak = trendPts.OrderByDescending(p => p.Value).FirstOrDefault();
                if (peak != null && peak.Value > 0)
                {
                    trendBadge = $"峰值 {peak.Label} · {QuotaFormatting.FormatCompactCount((long)peak.Value)}";
                }
            }

            var models = usage["todayByModel"]?.AsArray()
                ?? today?["models"]?.AsArray();
            if (models != null && models.Count > 0)
            {
                modelsBadge = $"共 {models.Count} 个模型";
                foreach (var mNode in models.Take(5))
                {
                    var m = mNode?.AsObject();
                    if (m == null) continue;
                    string modelName = m["model"].AsString() ?? "unknown";
                    long tokVal = m["totalTokens"].AsInt64(m["total_tokens"].AsInt64());
                    long reqVal = m["requestCount"].AsInt64(m["request_count"].AsInt64(m["requests"].AsInt64()));
                    double costVal = m["estimatedUsd"].AsNullableDouble() ?? m["estimated_usd"].AsDouble();
                    double share = todayTok > 0 ? Math.Clamp((tokVal / (double)todayTok) * 100.0, 0.0, 100.0) : 0.0;
                    string sub = $"今日调用 {reqVal} 次 · 占比 {share:0.0}%";
                    activeModels.Add(new OverviewActiveModel(
                        modelName,
                        QuotaFormatting.FormatCompactCount(tokVal),
                        $"${costVal:0.00}",
                        sub,
                        share));
                }
            }
        }

        UiDispatch.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(acctEmail))
            {
                HeroAccountEmail = acctEmail;
            }
            if (!string.IsNullOrWhiteSpace(acctName))
            {
                HeroAccountName = acctName;
            }
            else if (!string.IsNullOrWhiteSpace(HeroAccountEmail))
            {
                HeroAccountName = HeroAccountEmail.Split('@')[0];
            }
            if (!string.IsNullOrWhiteSpace(acctTier))
            {
                HeroAccountTier = acctTier;
            }
            HeroStatusNote = statusNote;

            QuotaBars.Clear();
            foreach (var b in parsedBars)
            {
                QuotaBars.Add(new OverviewQuotaBar(b.Title, b.Percentage, b.RemainingText, b.ResetText));
            }
            HasQuotaBars = QuotaBars.Count > 0;

            TodayTokensText = todayTok.ToString("N0", CultureInfo.InvariantCulture);
            TotalTokensSubtext = $"累计 {QuotaFormatting.FormatCompactCount(allTok)} Token";
            TodayRequestsText = todayReqs.ToString("N0", CultureInfo.InvariantCulture);
            TotalRequestsSubtext = $"累计 {allReqs.ToString("N0", CultureInfo.InvariantCulture)} 次";
            TodayCostText = $"${todayUsd:0.00}";
            TotalCostSubtext = $"累计 ${allUsd:0.00}";

            TrendBadgeText = trendBadge;
            if (trendPts.Count > 0) TrendPoints = trendPts;

            ActiveModels.Clear();
            foreach (var m in activeModels)
            {
                ActiveModels.Add(m);
            }
            ActiveModelsBadgeText = modelsBadge;
            HasActiveModels = ActiveModels.Count > 0;
        });
    }
}
