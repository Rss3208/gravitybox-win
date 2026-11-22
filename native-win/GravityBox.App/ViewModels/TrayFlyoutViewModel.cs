using System;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Core;

namespace GravityBox.App.ViewModels;

public sealed record TrayAccountItem(
    string Id,
    string Email,
    string Name,
    string Tier,
    bool IsActive,
    double? FiveHourPct,
    string ResetText);

public sealed record TrayQuotaRow(
    string Title,
    double Percentage,
    string RemainingText,
    string ResetText);

public sealed partial class TrayFlyoutViewModel : ObservableObject
{
    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;

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
    private string _activeAccountEmail = string.Empty;

    [ObservableProperty]
    private string _activeAccountTier = string.Empty;

    [ObservableProperty]
    private string? _errorBanner;

    public ObservableCollection<TrayQuotaRow> QuotaRows { get; } = new();
    public ObservableCollection<TrayAccountItem> Accounts { get; } = new();

    public event Action? OpenMainWindowRequested;
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
    }

    partial void OnTrendMetricChanged(string value)
    {
        string normalized = value is "tokens" or "requests" ? value : "tokens";
        _config.Update(c => c.MenuTrendMetric = normalized);
    }

    [RelayCommand]
    public void SelectTab(int index)
    {
        SelectedTab = Math.Clamp(index, 0, 2);
    }

    [RelayCommand]
    public void OpenMainWindow() => OpenMainWindowRequested?.Invoke();

    [RelayCommand]
    public void OpenAntigravity() => OpenAntigravityRequested?.Invoke();

    [RelayCommand]
    public void SafeRelaunch() => SafeRelaunchRequested?.Invoke();

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorBanner = null;
        try
        {
            var statusTask = _bridge.SendAsync("status", new JsonObject { ["includeTasks"] = false }, cancellationToken: cancellationToken);
            var dashTask = _bridge.SendAsync("dashboard_summary", cancellationToken: cancellationToken);
            var accountsTask = _bridge.SendAsync("account_list", cancellationToken: cancellationToken);

            await Task.WhenAll(statusTask, dashTask, accountsTask).ConfigureAwait(false);

            ApplyStatus(statusTask.Result);
            ApplyDashboard(dashTask.Result);
            ApplyAccounts(accountsTask.Result);
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
    public async Task SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        IsLoading = true;
        ErrorBanner = null;
        try
        {
            await _bridge.SendAsync(
                "account_switch",
                new JsonObject { ["id"] = accountId, ["target"] = "all" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
            IsLoading = false;
        }
    }

    private void ApplyStatus(JsonObject res)
    {
        var st = res["status"]?.AsObject();
        if (st == null) return;

        string mode = st["mode"].AsString() ?? "waiting";
        DebugPortMissing = mode == "debug-port-missing";
        ConnectionStatusText = st["message"].AsString() ?? mode;
        ActiveAccountEmail = st["activeAccountEmail"].AsString()
            ?? st["liveAccountEmail"].AsString()
            ?? string.Empty;
        ActiveAccountTier = st["activeSubscriptionTier"].AsString() ?? string.Empty;
    }

    private void ApplyDashboard(JsonObject res)
    {
        var usage = res["usage"]?.AsObject();
        if (usage != null)
        {
            var today = usage["today"]?.AsObject() ?? usage;
            long tokens = today["total_tokens"].AsInt64();
            long reqs = today["requests"].AsInt64();
            double usd = today["estimated_usd"].AsDouble();
            TodayTokensText = tokens.ToString("N0");
            TodayRequestsText = reqs.ToString("N0");
            TodayCostText = $"${usd:0.00}";
        }

        QuotaRows.Clear();
        var quotaGroups = res["quota"]?["groups"]?.AsArray();
        if (quotaGroups != null)
        {
            foreach (var gNode in quotaGroups)
            {
                var g = gNode?.AsObject();
                if (g == null) continue;
                string name = g["displayName"].AsString() ?? "Quota";
                var buckets = g["buckets"]?.AsArray();
                if (buckets == null || buckets.Count == 0) continue;
                var b = buckets[0]?.AsObject();
                if (b == null) continue;
                double frac = b["remainingFraction"].AsDouble();
                double pct = Math.Clamp(frac * 100.0, 0.0, 100.0);
                string reset = b["resetTime"].AsString() ?? string.Empty;
                QuotaRows.Add(new TrayQuotaRow(name, pct, $"{pct:0}%", reset));
            }
        }
    }

    private void ApplyAccounts(JsonObject res)
    {
        Accounts.Clear();
        var list = res["accounts"]?.AsArray();
        if (list == null) return;

        foreach (var node in list)
        {
            var acc = node?.AsObject();
            if (acc == null) continue;
            string id = acc["id"].AsString() ?? string.Empty;
            string email = acc["email"].AsString() ?? string.Empty;
            string name = acc["name"].AsString() ?? email;
            string tier = acc["subscription_tier"].AsString() ?? string.Empty;
            bool isActive = acc["isActive"].AsBool();
            double? fiveHour = acc["five_hour_quota"]?["remaining_percentage"].AsNullableDouble();
            string reset = acc["five_hour_quota"]?["reset_text"].AsString() ?? string.Empty;
            if (!string.IsNullOrEmpty(id))
            {
                Accounts.Add(new TrayAccountItem(id, email, name, tier, isActive, fiveHour, reset));
            }
        }
    }
}
