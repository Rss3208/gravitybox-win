using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Controls;
using GravityBox.App.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace GravityBox.App.ViewModels;

public sealed record UsageModelRow(
    string ModelName,
    string TokensText,
    string RequestsText,
    string CostText,
    bool Unpriced,
    string ShareText = "0%",
    double SharePct = 0.0,
    Color AccentColor = default)
{
    public Brush AccentBrush
    {
        get
        {
            var c = AccentColor == default ? Color.FromRgb(0x25, 0x63, 0xEB) : AccentColor;
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
    }

    public string SummaryRightText => $"{CostText} · {ShareText}";
}

public sealed record UsageLogEventRow(string Timestamp, string Model, string TokensText, string CostText, string SourceId);

public sealed partial class UsageViewModel : ObservableObject
{
    private static readonly string[] PeriodKeys = { "today", "7d", "30d", "all" };
    private static readonly Color[] SlicePalette =
    {
        Color.FromRgb(0x25, 0x63, 0xEB),
        Color.FromRgb(0x10, 0xB9, 0x81),
        Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6),
        Color.FromRgb(0xEC, 0x48, 0x99),
        Color.FromRgb(0x06, 0xB6, 0xD4),
    };

    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;

    private string? _activeSnapshotId;

    [ObservableProperty]
    private int _selectedPeriodIndex;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _totalTokensText = "0";

    [ObservableProperty]
    private string _tokenCompositionText = "输入 0 · 缓存读取 0 · 输出 0";

    [ObservableProperty]
    private string _totalRequestsText = "0";

    [ObservableProperty]
    private string _avgTokensPerRequestText = "0";

    [ObservableProperty]
    private string _estimatedCostText = "$0.00";

    [ObservableProperty]
    private string _cacheHitRateText = "0.0%";

    [ObservableProperty]
    private double _cacheHitRatePct;

    [ObservableProperty]
    private string _cacheReadSummaryText = "缓存读取 0 / 总输入 0";

    [ObservableProperty]
    private string _trendBadgeText = "今日 24 小时";

    [ObservableProperty]
    private bool _hasModels;

    [ObservableProperty]
    private bool _hasUnpricedModels;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _currentPage;

    [ObservableProperty]
    private int _totalEventsCount;

    [ObservableProperty]
    private string? _errorBanner;

    [ObservableProperty]
    private IReadOnlyList<TrendPoint> _trendPoints = Array.Empty<TrendPoint>();

    [ObservableProperty]
    private IReadOnlyList<DonutSlice> _donutSlices = Array.Empty<DonutSlice>();

    public ObservableCollection<UsageModelRow> ModelRows { get; } = new();
    public ObservableCollection<UsageModelRow> TopLegendRows { get; } = new();
    public ObservableCollection<UsageLogEventRow> EventRows { get; } = new();

    public UsageViewModel(IBackendBridge bridge, ConfigurationStore config, L10nService l10n)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        _selectedPeriodIndex = Math.Clamp(_config.Value.MenuUsagePeriod, 0, 3);
    }

    public string ActivePeriodKey => PeriodKeys[Math.Clamp(SelectedPeriodIndex, 0, 3)];

    partial void OnSelectedPeriodIndexChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 3);
        if (clamped != value)
        {
            SelectedPeriodIndex = clamped;
            return;
        }
        _config.Update(c => c.MenuUsagePeriod = clamped);
        _ = RefreshAsync();
    }

    [RelayCommand]
    public void SelectPeriod(object? indexParam)
    {
        if (indexParam is int i)
        {
            SelectedPeriodIndex = Math.Clamp(i, 0, 3);
        }
        else if (int.TryParse(indexParam?.ToString(), out int parsed))
        {
            SelectedPeriodIndex = Math.Clamp(parsed, 0, 3);
        }
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
            var res = await _bridge.SendAsync("usage_summary", new JsonObject
            {
                ["period"] = ActivePeriodKey,
                ["force"] = true,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            ApplySummary(res);
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

    public async Task LoadEventsPageAsync(int page = 0, CancellationToken cancellationToken = default)
    {
        UiDispatch.Run(() =>
        {
            ErrorBanner = null;
            CurrentPage = Math.Max(0, page);
        });
        var payload = new JsonObject
        {
            ["period"] = ActivePeriodKey,
            ["query"] = SearchQuery ?? string.Empty,
            ["offset"] = Math.Max(0, page) * 50,
            ["limit"] = 50,
        };
        if (!string.IsNullOrEmpty(_activeSnapshotId))
        {
            payload["snapshotId"] = _activeSnapshotId;
        }

        var res = await _bridge.SendAsync("usage_events", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        var built = new List<UsageLogEventRow>();
        int total = (int)res["total"].AsInt64(0);
        var rows = res["rows"]?.AsArray();
        if (rows != null)
        {
            foreach (var r in rows)
            {
                var obj = r?.AsObject();
                if (obj == null) continue;
                long ts = obj["timestamp_seconds"].AsInt64(obj["timestampSeconds"].AsInt64(0));
                string timeText = ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : string.Empty;
                string model = obj["model"].AsString() ?? "unknown";
                long tokVal = obj["total_tokens"].AsInt64(obj["totalTokens"].AsInt64(0));
                double usdVal = obj["estimated_usd"].AsNullableDouble() ?? obj["estimatedUsd"].AsDouble();
                string src = obj["source_id"].AsString() ?? obj["sourceId"].AsString() ?? string.Empty;
                built.Add(new UsageLogEventRow(timeText, model, tokVal.ToString("N0", CultureInfo.InvariantCulture), $"${usdVal:0.0000}", src));
            }
        }

        UiDispatch.Run(() =>
        {
            TotalEventsCount = total;
            EventRows.Clear();
            foreach (var row in built) EventRows.Add(row);
        });
    }

    public async Task ReleaseSnapshotAsync()
    {
        if (string.IsNullOrEmpty(_activeSnapshotId)) return;
        string snap = _activeSnapshotId;
        _activeSnapshotId = null;
        try
        {
            await _bridge.SendAsync("usage_snapshot_release", new JsonObject { ["snapshotId"] = snap }).ConfigureAwait(false);
        }
        catch
        {
            // Best effort release
        }
    }

    public string ExportCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Model,Tokens,Requests,Share,EstimatedUSD,Unpriced");
        foreach (var row in ModelRows)
        {
            sb.AppendLine($"\"{row.ModelName.Replace("\"", "\"\"")}\",\"{row.TokensText}\",\"{row.RequestsText}\",\"{row.ShareText}\",\"{row.CostText}\",{row.Unpriced}");
        }
        return sb.ToString();
    }

    private void ApplySummary(JsonObject res)
    {
        _activeSnapshotId = res["snapshotId"].AsString() ?? res["snapshot_id"].AsString();
        var periodNode = res["summary"]?.AsObject() ?? res[ActivePeriodKey]?.AsObject() ?? res;

        long totalTok = periodNode["totalTokens"].AsInt64(periodNode["total_tokens"].AsInt64());
        long inputTok = periodNode["promptTokens"].AsInt64(periodNode["input_tokens"].AsInt64());
        long outputTok = periodNode["candidatesTokens"].AsInt64(periodNode["output_tokens"].AsInt64());
        long cachedTok = periodNode["cachedTokens"].AsInt64(periodNode["cached_tokens"].AsInt64());
        long reqs = periodNode["requestCount"].AsInt64(periodNode["request_count"].AsInt64(periodNode["requests"].AsInt64()));
        double usd = periodNode["estimatedUsd"].AsNullableDouble() ?? periodNode["estimated_usd"].AsDouble();

        double? rawHitRate = periodNode["cacheHitRate"].AsNullableDouble();
        double cacheDenom = inputTok + cachedTok;
        double hitRate = cacheDenom > 0
            ? Math.Clamp((cachedTok / cacheDenom) * 100.0, 0.0, 100.0)
            : (rawHitRate.HasValue ? Math.Clamp(rawHitRate.Value <= 1.0 ? rawHitRate.Value * 100.0 : rawHitRate.Value, 0.0, 100.0) : 0.0);

        var rows = new List<UsageModelRow>();
        var slices = new List<DonutSlice>();
        bool anyUnpriced = false;

        var models = res["byModel"]?.AsArray()
            ?? res["models"]?.AsArray()
            ?? periodNode["byModel"]?.AsArray()
            ?? periodNode["models"]?.AsArray();
        if (models != null)
        {
            int idx = 0;
            foreach (var mNode in models)
            {
                var m = mNode?.AsObject();
                if (m == null) continue;
                string name = m["model"].AsString() ?? "unknown";
                long tok = m["totalTokens"].AsInt64(m["total_tokens"].AsInt64());
                long mReqs = m["requestCount"].AsInt64(m["request_count"].AsInt64(m["requests"].AsInt64()));
                double mUsd = m["estimatedUsd"].AsNullableDouble() ?? m["estimated_usd"].AsDouble();
                bool unpriced = m["priced"] != null ? !m["priced"].AsBool() : m["unpriced"].AsBool();
                anyUnpriced |= unpriced;

                double sharePct = totalTok > 0 ? Math.Clamp((tok / (double)totalTok) * 100.0, 0.0, 100.0) : 0.0;
                Color sliceColor = SlicePalette[idx % SlicePalette.Length];
                string shareText = $"{sharePct:0.0}%";
                string costText = unpriced && mUsd <= 0 ? "待定价" : $"${mUsd:0.00}";

                rows.Add(new UsageModelRow(
                    name,
                    tok.ToString("N0", CultureInfo.InvariantCulture),
                    mReqs.ToString("N0", CultureInfo.InvariantCulture),
                    costText,
                    unpriced,
                    shareText,
                    sharePct,
                    sliceColor));
                double sliceVal = mUsd > 0 ? mUsd : tok;
                if (sliceVal > 0)
                {
                    slices.Add(new DonutSlice(name, sliceVal, sliceColor));
                }
                idx++;
            }
        }

        var trendPts = BuildTrendPoints(res, periodNode);
        string periodBadge = ActivePeriodKey switch
        {
            "today" => "今日 24 小时",
            "7d" => "近 7 天按日趋势",
            "30d" => "近 30 天按日趋势",
            _ => "全部累计趋势",
        };
        long avgPerReq = reqs > 0 ? totalTok / reqs : 0;

        UiDispatch.Run(() =>
        {
            TotalTokensText = totalTok.ToString("N0", CultureInfo.InvariantCulture);
            TokenCompositionText = $"输入 {QuotaFormatting.FormatCompactCount(inputTok)} · 缓存读取 {QuotaFormatting.FormatCompactCount(cachedTok)} · 输出 {QuotaFormatting.FormatCompactCount(outputTok)}";
            TotalRequestsText = reqs.ToString("N0", CultureInfo.InvariantCulture);
            AvgTokensPerRequestText = $"平均每次 {QuotaFormatting.FormatCompactCount(avgPerReq)} Token";
            EstimatedCostText = $"${usd:0.00}";
            CacheHitRatePct = hitRate;
            CacheHitRateText = $"{hitRate:0.0}%";
            CacheReadSummaryText = $"缓存命中 {QuotaFormatting.FormatCompactCount(cachedTok)} / 输入基数 {QuotaFormatting.FormatCompactCount(inputTok + cachedTok)}";
            TrendBadgeText = periodBadge;

            ModelRows.Clear();
            TopLegendRows.Clear();
            foreach (var row in rows)
            {
                ModelRows.Add(row);
                if (TopLegendRows.Count < 5)
                {
                    TopLegendRows.Add(row);
                }
            }

            HasModels = rows.Count > 0;
            HasUnpricedModels = anyUnpriced;
            DonutSlices = slices;
            TrendPoints = trendPts;
        });
    }

    private List<TrendPoint> BuildTrendPoints(JsonObject res, JsonObject periodNode)
    {
        var pts = new List<TrendPoint>();
        if (ActivePeriodKey == "today")
        {
            var hourly = res["todayHourly"]?.AsArray()
                ?? periodNode["todayHourly"]?.AsArray()
                ?? periodNode["hourly"]?.AsArray();
            if (hourly != null)
            {
                foreach (var hNode in hourly)
                {
                    var obj = hNode?.AsObject();
                    if (obj == null) continue;
                    string label = obj["label"].AsString() ?? string.Empty;
                    if (string.IsNullOrEmpty(label))
                    {
                        long hour = obj["hour"].AsInt64(-1);
                        label = hour >= 0 ? $"{hour:00}时" : string.Empty;
                    }
                    double val = obj["total_tokens"].AsNullableDouble()
                        ?? obj["totalTokens"].AsNullableDouble()
                        ?? obj["tokens"].AsDouble();
                    long reqs = obj["request_count"].AsInt64(obj["requestCount"].AsInt64(0));
                    string tip = $"{label} · {QuotaFormatting.FormatCompactCount((long)val)} Token ({reqs} 次)";
                    pts.Add(new TrendPoint(label, val, tip));
                }
            }
        }
        else
        {
            var daily = res["dailyTrends"]?.AsArray()
                ?? periodNode["dailyTrends"]?.AsArray()
                ?? periodNode["daily"]?.AsArray();
            if (daily != null)
            {
                foreach (var dNode in daily)
                {
                    var obj = dNode?.AsObject();
                    if (obj == null) continue;
                    string rawDate = obj["date"].AsString() ?? obj["label"].AsString() ?? string.Empty;
                    string label = rawDate.Length >= 10 && rawDate[4] == '-' ? rawDate.Substring(5) : rawDate;
                    double val = obj["total_tokens"].AsNullableDouble()
                        ?? obj["totalTokens"].AsNullableDouble()
                        ?? obj["tokens"].AsDouble();
                    long reqs = obj["request_count"].AsInt64(obj["requestCount"].AsInt64(0));
                    string tip = $"{rawDate} · {QuotaFormatting.FormatCompactCount((long)val)} Token ({reqs} 次)";
                    pts.Add(new TrendPoint(label, val, tip));
                }
            }
        }
        return pts;
    }
}
