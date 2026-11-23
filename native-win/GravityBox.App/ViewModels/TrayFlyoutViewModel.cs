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

public sealed record TrayAccountItem(
    string Id,
    string Email,
    string Name,
    string Tier,
    bool IsActive,
    double? FiveHourPct,
    string ResetText)
{
    public string FiveHourText => FiveHourPct.HasValue ? $"{FiveHourPct.Value:0}%" : "--";
    public string SummarySubtitle => string.IsNullOrEmpty(ResetText)
        ? Email
        : $"{Email} · {ResetText}";
}

public sealed record TrayQuotaRow(
    string Title,
    double Percentage,
    string RemainingText,
    string ResetText);

public sealed partial class TrayFlyoutViewModel : ObservableObject
{
    private static readonly string[] PeriodKeys = { "today", "7d", "30d", "all" };

    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private JsonObject? _lastPeriodSummary;

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private int _usagePeriod;

    [ObservableProperty]
    private string _trendMetric = "tokens";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _debugPortMissing;

    [ObservableProperty]
    private string _connectionStatusText = string.Empty;

    [ObservableProperty]
    private string _todayTokensText = "0";

    [ObservableProperty]
    private string _todayRequestsText = "0";

    [ObservableProperty]
    private string _todayCostText = "$0.00";

    [ObservableProperty]
    private string _cacheHitRateText = "0.0%";

    [ObservableProperty]
    private string _periodTrendTitle = "今日趋势";

    [ObservableProperty]
    private string _activeAccountName = "尚未保存账号";

    [ObservableProperty]
    private string _activeAccountEmail = string.Empty;

    [ObservableProperty]
    private string _activeAccountTier = string.Empty;

    [ObservableProperty]
    private string _quotaUpdatedText = string.Empty;

    [ObservableProperty]
    private bool _hasQuotaRows;

    [ObservableProperty]
    private bool _hasAccounts;

    [ObservableProperty]
    private bool _hasTopModels;

    [ObservableProperty]
    private string _accountsCountText = "0 个账号";

    [ObservableProperty]
    private string? _errorBanner;

    [ObservableProperty]
    private IReadOnlyList<TrendPoint> _trendPoints = Array.Empty<TrendPoint>();

    public ObservableCollection<UsageModelRow> TopModels { get; } = new();
    public ObservableCollection<TrayQuotaRow> QuotaRows { get; } = new();
    public ObservableCollection<TrayAccountItem> Accounts { get; } = new();

    public event Action? OpenMainWindowRequested;
    public event Action<int>? OpenMainPageRequested;
    public event Action? OpenAntigravityRequested;
    public event Action? SafeRelaunchRequested;

    public TrayFlyoutViewModel(IBackendBridge bridge, ConfigurationStore config, L10nService l10n)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));

        _selectedTab = Math.Clamp(_config.Value.MenuSelectedTab, 0, 2);
        _usagePeriod = Math.Clamp(_config.Value.MenuUsagePeriod, 0, 3);
        _trendMetric = _config.Value.MenuTrendMetric;
        _connectionStatusText = _l10n.T("status.waiting");
    }

    public string ActivePeriodKey => PeriodKeys[Math.Clamp(UsagePeriod, 0, 3)];

    partial void OnSelectedTabChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 2);
        if (clamped != value)
        {
            SelectedTab = clamped;
            return;
        }
        _config.Update(c => c.MenuSelectedTab = clamped);
    }

    partial void OnUsagePeriodChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 3);
        if (clamped != value)
        {
            UsagePeriod = clamped;
            return;
        }
        _config.Update(c => c.MenuUsagePeriod = clamped);
        _ = LoadUsagePeriodOnlyAsync();
    }

    partial void OnTrendMetricChanged(string value)
    {
        string normalized = value is "tokens" or "requests" ? value : "tokens";
        if (normalized != value)
        {
            TrendMetric = normalized;
            return;
        }
        _config.Update(c => c.MenuTrendMetric = normalized);
        if (_lastPeriodSummary != null)
        {
            ApplyPeriodUsage(_lastPeriodSummary);
        }
    }

    public void SyncLiveStatus(string mode, string message)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        UiDispatch.Run(() =>
        {
            DebugPortMissing = mode == "debug-port-missing";
            ConnectionStatusText = string.IsNullOrWhiteSpace(message) ? mode : message;
        });
    }

    [RelayCommand]
    public void SelectTab(object? indexParam)
    {
        if (indexParam is int i)
        {
            SelectedTab = Math.Clamp(i, 0, 2);
        }
        else if (int.TryParse(indexParam?.ToString(), out int parsed))
        {
            SelectedTab = Math.Clamp(parsed, 0, 2);
        }
    }

    [RelayCommand]
    public void SelectUsagePeriod(object? indexParam)
    {
        if (indexParam is int i)
        {
            UsagePeriod = Math.Clamp(i, 0, 3);
        }
        else if (int.TryParse(indexParam?.ToString(), out int parsed))
        {
            UsagePeriod = Math.Clamp(parsed, 0, 3);
        }
    }

    [RelayCommand]
    public void SelectTrendMetric(string? metric)
    {
        TrendMetric = metric == "requests" ? "requests" : "tokens";
    }

    [RelayCommand]
    public void OpenMainWindow() => OpenMainWindowRequested?.Invoke();

    [RelayCommand]
    public void OpenMainPage(object? pageParam)
    {
        if (int.TryParse(pageParam?.ToString(), out int idx))
        {
            OpenMainPageRequested?.Invoke(Math.Clamp(idx, 0, 4));
        }
        else
        {
            OpenMainWindowRequested?.Invoke();
        }
    }

    [RelayCommand]
    public void OpenAntigravity() => OpenAntigravityRequested?.Invoke();

    [RelayCommand]
    public void SafeRelaunch() => SafeRelaunchRequested?.Invoke();

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
            await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false);
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
            // Ignore partial quota refresh warnings
        }
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task SaveCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() => ErrorBanner = null);
        try
        {
            await _bridge.SendAsync("account_save_current", cancellationToken: cancellationToken).ConfigureAwait(false);
            await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    [RelayCommand]
    public async Task SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || IsLoading) return;
        UiDispatch.Run(() =>
        {
            IsLoading = true;
            ErrorBanner = null;
        });
        try
        {
            var preflight = await _bridge.SendAsync("account_switch_preflight", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (preflight["hasRunningTask"].AsBool() || !preflight["safe"].AsBool(true))
            {
                string reason = preflight["taskReason"].AsString() ?? "Antigravity 正在执行任务，无法切换账号";
                UiDispatch.Run(() => ErrorBanner = reason);
                return;
            }

            await _bridge.SendAsync(
                "account_switch",
                new JsonObject { ["id"] = accountId, ["target"] = "all" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task LoadAllCoreAsync(CancellationToken cancellationToken)
    {
        var statusTask = _bridge.SendAsync("status", new JsonObject { ["includeTasks"] = false }, cancellationToken: cancellationToken);
        var dashTask = _bridge.SendAsync("dashboard_summary", cancellationToken: cancellationToken);
        var accountsTask = _bridge.SendAsync("account_list", cancellationToken: cancellationToken);
        var usageTask = _bridge.SendAsync("usage_summary", new JsonObject
        {
            ["period"] = ActivePeriodKey,
            ["force"] = false,
        }, cancellationToken: cancellationToken);

        await Task.WhenAll(statusTask, dashTask, accountsTask, usageTask).ConfigureAwait(false);

        ApplyStatus(statusTask.Result);
        ApplyDashboard(dashTask.Result);
        ApplyAccounts(accountsTask.Result);
        ApplyPeriodUsage(usageTask.Result);
    }

    private async Task LoadUsagePeriodOnlyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _bridge.SendAsync("usage_summary", new JsonObject
            {
                ["period"] = ActivePeriodKey,
                ["force"] = false,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyPeriodUsage(res);
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ErrorBanner = ex.Message);
        }
    }

    private void ApplyStatus(JsonObject res)
    {
        var st = res["status"]?.AsObject();
        if (st == null) return;

        string mode = st["mode"].AsString() ?? "waiting";
        string message = st["message"].AsString() ?? mode;
        string email = st["activeAccountEmail"].AsString()
            ?? st["liveAccountEmail"].AsString()
            ?? string.Empty;
        string tier = st["activeSubscriptionTier"].AsString() ?? string.Empty;
        UiDispatch.Run(() =>
        {
            DebugPortMissing = mode == "debug-port-missing";
            ConnectionStatusText = message;
            if (!string.IsNullOrWhiteSpace(email))
            {
                ActiveAccountEmail = email;
                ActiveAccountName = email.Split('@')[0];
            }
            if (!string.IsNullOrWhiteSpace(tier)) ActiveAccountTier = tier;
        });
    }

    private void ApplyDashboard(JsonObject res)
    {
        var usage = res["usage"]?.AsObject();
        if (usage != null && (_lastPeriodSummary == null || UsagePeriod == 0))
        {
            var today = usage["today"]?.AsObject();
            long tokens = today?["total_tokens"].AsInt64(0) ?? usage["todayTokens"].AsInt64(0);
            long reqs = today?["requests"].AsInt64(0) ?? usage["requestCount"].AsInt64(0);
            double usd = today?["estimated_usd"].AsNullableDouble() ?? usage["estimatedUsd"].AsDouble();
            UiDispatch.Run(() =>
            {
                TodayTokensText = tokens.ToString("N0", CultureInfo.InvariantCulture);
                TodayRequestsText = reqs.ToString("N0", CultureInfo.InvariantCulture);
                TodayCostText = $"${usd:0.00}";
            });
        }

        var acctNode = res["accounts"]?.AsObject();
        string? acctName = acctNode?["activeName"].AsString();
        string? acctEmail = acctNode?["activeEmail"].AsString() ?? acctNode?["liveAccountEmail"].AsString();
        string? acctTier = acctNode?["activeTier"].AsString();
        long quotaUpdated = acctNode?["quotaLastUpdated"].AsInt64(0) ?? 0;
        string updatedStr = quotaUpdated > 0 ? $"更新于 {QuotaFormatting.FormatUpdatedAt(quotaUpdated)}" : string.Empty;

        var bars = QuotaFormatting.ExtractQuotaBars(acctNode ?? res);
        if (bars.Count == 0 && acctNode != null)
        {
            bars = QuotaFormatting.ExtractQuotaBars(res);
        }

        UiDispatch.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(acctEmail)) ActiveAccountEmail = acctEmail;
            if (!string.IsNullOrWhiteSpace(acctName)) ActiveAccountName = acctName;
            if (!string.IsNullOrWhiteSpace(acctTier)) ActiveAccountTier = acctTier;
            QuotaUpdatedText = updatedStr;

            QuotaRows.Clear();
            foreach (var b in bars)
            {
                QuotaRows.Add(new TrayQuotaRow(b.Title, b.Percentage, b.RemainingText, b.ResetText));
            }
            HasQuotaRows = QuotaRows.Count > 0;
        });
    }

    private void ApplyPeriodUsage(JsonObject res)
    {
        var periodNode = res["summary"]?.AsObject() ?? res[ActivePeriodKey]?.AsObject();
        var rootHasMetrics = periodNode != null
            || res["totalTokens"] != null
            || res["total_tokens"] != null
            || res["byModel"] != null;
        if (!rootHasMetrics)
        {
            return;
        }
        _lastPeriodSummary = res;

        var src = periodNode ?? res;
        long totalTok = src["totalTokens"].AsInt64(src["total_tokens"].AsInt64());
        long inputTok = src["promptTokens"].AsInt64(src["input_tokens"].AsInt64());
        long cachedTok = src["cachedTokens"].AsInt64(src["cached_tokens"].AsInt64());
        long reqs = src["requestCount"].AsInt64(src["request_count"].AsInt64(src["requests"].AsInt64()));
        double usd = src["estimatedUsd"].AsNullableDouble() ?? src["estimated_usd"].AsDouble();

        double cacheDenom = inputTok + cachedTok;
        double? rawHitRate = src["cacheHitRate"].AsNullableDouble();
        double hitRate = cacheDenom > 0
            ? Math.Clamp((cachedTok / cacheDenom) * 100.0, 0.0, 100.0)
            : (rawHitRate.HasValue ? Math.Clamp(rawHitRate.Value <= 1.0 ? rawHitRate.Value * 100.0 : rawHitRate.Value, 0.0, 100.0) : 0.0);

        bool useRequests = TrendMetric == "requests";
        var pts = new List<TrendPoint>();
        if (ActivePeriodKey == "today")
        {
            var hourly = res["todayHourly"]?.AsArray()
                ?? src["todayHourly"]?.AsArray()
                ?? src["hourly"]?.AsArray();
            pts = QuotaFormatting.BuildHourlyTrendPoints(hourly, padFull24Hours: true, useRequestMetric: useRequests);
        }
        else
        {
            var daily = res["dailyTrends"]?.AsArray()
                ?? src["dailyTrends"]?.AsArray()
                ?? src["daily"]?.AsArray();
            if (daily != null)
            {
                foreach (var dNode in daily)
                {
                    var obj = dNode?.AsObject();
                    if (obj == null) continue;
                    string rawDate = obj["date"].AsString() ?? obj["label"].AsString() ?? string.Empty;
                    string label = rawDate.Length >= 10 && rawDate[4] == '-' ? rawDate.Substring(5) : rawDate;
                    long tokVal = obj["total_tokens"].AsInt64(obj["totalTokens"].AsInt64(0));
                    long reqVal = obj["request_count"].AsInt64(obj["requestCount"].AsInt64(0));
                    double val = useRequests ? reqVal : tokVal;
                    string tip = useRequests
                        ? $"{rawDate} · {reqVal} 次请求"
                        : $"{rawDate} · {QuotaFormatting.FormatCompactCount(tokVal)} Token ({reqVal} 次)";
                    pts.Add(new TrendPoint(label, val, tip));
                }
            }
        }

        var topModels = new List<UsageModelRow>();
        var models = res["byModel"]?.AsArray()
            ?? res["models"]?.AsArray()
            ?? src["byModel"]?.AsArray()
            ?? src["models"]?.AsArray();
        if (models != null)
        {
            foreach (var mNode in models.Take(4))
            {
                var m = mNode?.AsObject();
                if (m == null) continue;
                string name = m["model"].AsString() ?? "unknown";
                long tok = m["totalTokens"].AsInt64(m["total_tokens"].AsInt64());
                long mReqs = m["requestCount"].AsInt64(m["request_count"].AsInt64(m["requests"].AsInt64()));
                double mUsd = m["estimatedUsd"].AsNullableDouble() ?? m["estimated_usd"].AsDouble();
                bool unpriced = m["priced"] != null ? !m["priced"].AsBool() : m["unpriced"].AsBool();
                double sharePct = totalTok > 0 ? Math.Clamp((tok / (double)totalTok) * 100.0, 0.0, 100.0) : 0.0;
                topModels.Add(new UsageModelRow(
                    name,
                    QuotaFormatting.FormatCompactCount(tok),
                    $"{mReqs} 次",
                    unpriced && mUsd <= 0 ? "待定价" : $"${mUsd:0.00}",
                    unpriced,
                    $"{sharePct:0.0}%",
                    sharePct));
            }
        }

        string trendTitle = ActivePeriodKey switch
        {
            "today" => "今日趋势",
            "7d" => "近 7 天趋势",
            "30d" => "近 30 天趋势",
            _ => "全部趋势",
        };

        UiDispatch.Run(() =>
        {
            TodayTokensText = totalTok.ToString("N0", CultureInfo.InvariantCulture);
            TodayRequestsText = reqs.ToString("N0", CultureInfo.InvariantCulture);
            TodayCostText = $"${usd:0.00}";
            CacheHitRateText = $"{hitRate:0.0}%";
            PeriodTrendTitle = trendTitle;
            TrendPoints = pts;
            TopModels.Clear();
            foreach (var m in topModels) TopModels.Add(m);
            HasTopModels = TopModels.Count > 0;
        });
    }

    private void ApplyAccounts(JsonObject res)
    {
        var list = res["accounts"]?.AsArray();
        var built = new List<TrayAccountItem>();
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
                var bars = QuotaFormatting.ExtractQuotaBars(acc);
                double? fiveHour = bars.Count > 0 ? bars[0].Percentage : acc["five_hour_quota"]?["remaining_percentage"].AsNullableDouble();
                string reset = bars.Count > 0 ? bars[0].ResetText : (acc["five_hour_quota"]?["reset_text"].AsString() ?? string.Empty);
                if (!string.IsNullOrEmpty(id))
                {
                    built.Add(new TrayAccountItem(id, email, name, tier, isActive, fiveHour, reset));
                }
            }
        }

        UiDispatch.Run(() =>
        {
            Accounts.Clear();
            foreach (var a in built) Accounts.Add(a);
            HasAccounts = Accounts.Count > 0;
            AccountsCountText = $"{Accounts.Count} 个账号";
        });
    }
}
