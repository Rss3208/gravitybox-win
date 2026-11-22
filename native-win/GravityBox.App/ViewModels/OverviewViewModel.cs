using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
public sealed record OverviewActiveModel(string ModelName, string TokensText, string CostText);

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private readonly LifecycleSyncService _lifecycle;

    private string _lastDashboardFingerprint = string.Empty;
    private DateTimeOffset _lastDashboardRefreshAt = DateTimeOffset.MinValue;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _debugPortMissing;

    [ObservableProperty]
    private bool _showShortcutOnboardingCard;

    [ObservableProperty]
    private string _unconfiguredShortcutCountText = string.Empty;

    [ObservableProperty]
    private string _heroAccountName = "未检测到已保存账号";

    [ObservableProperty]
    private string _heroAccountEmail = string.Empty;

    [ObservableProperty]
    private string _heroAccountTier = string.Empty;

    [ObservableProperty]
    private string _heroStatusNote = string.Empty;

    [ObservableProperty]
    private string _todayTokensText = "0";

    [ObservableProperty]
    private string _todayRequestsText = "0";

    [ObservableProperty]
    private string _todayCostText = "$0.00";

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

        IsLoading = true;
        ErrorBanner = null;
        try
        {
            var statusTask = _bridge.SendAsync("status", new JsonObject { ["includeTasks"] = false }, cancellationToken: cancellationToken);
            var dashTask = _bridge.SendAsync("dashboard_summary", cancellationToken: cancellationToken);
            Task<JsonObject?> shortcutTask = _config.Value.ShortcutGuideDismissed
                ? Task.FromResult<JsonObject?>(null)
                : _bridge.SendAsync("shortcut_scan", cancellationToken: cancellationToken).ContinueWith(t => (JsonObject?)t.Result, cancellationToken);

            await Task.WhenAll((Task)statusTask, dashTask, shortcutTask).ConfigureAwait(false);

            ApplyStatus(statusTask.Result);
            ApplyDashboard(dashTask.Result);
            if (shortcutTask.Result != null)
            {
                ApplyShortcuts(shortcutTask.Result);
            }
            else
            {
                ShowShortcutOnboardingCard = false;
            }
            _lastDashboardRefreshAt = DateTimeOffset.UtcNow;
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
    public async Task ApplySeamlessShortcutsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _bridge.SendAsync(
                "shortcut_apply",
                new JsonObject { ["confirmed"] = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            ShowShortcutOnboardingCard = false;
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    [RelayCommand]
    public void DismissShortcutOnboarding()
    {
        _config.Update(c => c.ShortcutGuideDismissed = true);
        ShowShortcutOnboardingCard = false;
    }

    [RelayCommand]
    public async Task SafeRelaunchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await _bridge.SendAsync("platform_info", cancellationToken: cancellationToken).ConfigureAwait(false);
            string exe = info["executablePath"].AsString() ?? string.Empty;
            string profile = info["profileDir"].AsString() ?? string.Empty;
            var res = await _lifecycle.SafeRelaunchAsync(exe, profile, cancellationToken).ConfigureAwait(false);
            if (!res.Ok)
            {
                ErrorBanner = res.Message;
            }
            else
            {
                await RefreshAsync(force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    private void ApplyStatus(JsonObject res)
    {
        var st = res["status"]?.AsObject();
        if (st == null) return;
        string mode = st["mode"].AsString() ?? "waiting";
        DebugPortMissing = mode == "debug-port-missing";
        HeroAccountEmail = st["activeAccountEmail"].AsString()
            ?? st["liveAccountEmail"].AsString()
            ?? string.Empty;
        HeroAccountTier = st["activeSubscriptionTier"].AsString() ?? string.Empty;
        HeroAccountName = string.IsNullOrWhiteSpace(HeroAccountEmail)
            ? "实时账号未保存"
            : HeroAccountEmail.Split('@')[0];
    }

    private void ApplyShortcuts(JsonObject res)
    {
        var arr = res["shortcuts"]?.AsArray();
        if (arr == null || _config.Value.ShortcutGuideDismissed)
        {
            ShowShortcutOnboardingCard = false;
            return;
        }

        int unconfigured = arr.Count(item => item?["hasDebugPort"].AsBool() != true);
        ShowShortcutOnboardingCard = unconfigured > 0;
        UnconfiguredShortcutCountText = $"发现 {unconfigured} 个官方快捷方式尚未配置无感调试参数";
    }

    private void ApplyDashboard(JsonObject res)
    {
        string fp = res.ToJsonString();
        if (fp == _lastDashboardFingerprint)
        {
            return;
        }
        _lastDashboardFingerprint = fp;

        var usage = res["usage"]?.AsObject();
        if (usage != null)
        {
            var today = usage["today"]?.AsObject() ?? usage;
            TodayTokensText = today["total_tokens"].AsInt64().ToString("N0");
            TodayRequestsText = today["requests"].AsInt64().ToString("N0");
            TodayCostText = $"${today["estimated_usd"].AsDouble():0.00}";

            var hourly = today["hourly"]?.AsArray();
            if (hourly != null && hourly.Count > 0)
            {
                var pts = new List<TrendPoint>(hourly.Count);
                foreach (var h in hourly)
                {
                    var obj = h?.AsObject();
                    if (obj == null) continue;
                    string label = obj["label"].AsString() ?? string.Empty;
                    double val = obj["tokens"].AsDouble();
                    pts.Add(new TrendPoint(label, val));
                }
                TrendPoints = pts;
            }
        }

        QuotaBars.Clear();
        var groups = res["quota"]?["groups"]?.AsArray();
        if (groups != null)
        {
            foreach (var gNode in groups)
            {
                var g = gNode?.AsObject();
                if (g == null) continue;
                string name = g["displayName"].AsString() ?? "Quota";
                var buckets = g["buckets"]?.AsArray();
                if (buckets == null) continue;
                foreach (var bNode in buckets)
                {
                    var b = bNode?.AsObject();
                    if (b == null) continue;
                    string win = b["window"].AsString() ?? string.Empty;
                    double pct = Math.Clamp(b["remainingFraction"].AsDouble() * 100.0, 0.0, 100.0);
                    string reset = b["resetTime"].AsString() ?? string.Empty;
                    string label = string.IsNullOrEmpty(win) ? name : $"{name} ({win})";
                    QuotaBars.Add(new OverviewQuotaBar(label, pct, $"{pct:0}%", reset));
                }
            }
        }

        ActiveModels.Clear();
        var models = res["usage"]?["today"]?["models"]?.AsArray();
        if (models != null)
        {
            foreach (var mNode in models)
            {
                var m = mNode?.AsObject();
                if (m == null) continue;
                string modelName = m["model"].AsString() ?? "unknown";
                string tok = m["total_tokens"].AsInt64().ToString("N0");
                string cost = $"${m["estimated_usd"].AsDouble():0.00}";
                ActiveModels.Add(new OverviewActiveModel(modelName, tok, cost));
            }
        }
    }
}
