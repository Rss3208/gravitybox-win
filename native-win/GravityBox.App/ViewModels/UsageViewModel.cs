using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Controls;
using GravityBox.App.Core;
using Color = System.Windows.Media.Color;

namespace GravityBox.App.ViewModels;

public sealed record UsageModelRow(string ModelName, string TokensText, string RequestsText, string CostText, bool Unpriced);
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
    private string _totalRequestsText = "0";

    [ObservableProperty]
    private string _estimatedCostText = "$0.00";

    [ObservableProperty]
    private string _cacheHitRateText = "0%";

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
    public void SelectPeriod(int index)
    {
        SelectedPeriodIndex = Math.Clamp(index, 0, 3);
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorBanner = null;
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
            ErrorBanner = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadEventsPageAsync(int page = 0, CancellationToken cancellationToken = default)
    {
        ErrorBanner = null;
        CurrentPage = Math.Max(0, page);
        var payload = new JsonObject
        {
            ["period"] = ActivePeriodKey,
            ["query"] = SearchQuery ?? string.Empty,
            ["offset"] = CurrentPage * 50,
            ["limit"] = 50,
        };
        if (!string.IsNullOrEmpty(_activeSnapshotId))
        {
            payload["snapshotId"] = _activeSnapshotId;
        }

        var res = await _bridge.SendAsync("usage_events", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        EventRows.Clear();
        TotalEventsCount = (int)res["total"].AsInt64(0);
        var rows = res["rows"]?.AsArray();
        if (rows != null)
        {
            foreach (var r in rows)
            {
                var obj = r?.AsObject();
                if (obj == null) continue;
                long ts = obj["timestamp_seconds"].AsInt64(0);
                string timeText = ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("MM-dd HH:mm:ss")
                    : string.Empty;
                string model = obj["model"].AsString() ?? "unknown";
                string tok = obj["total_tokens"].AsInt64(0).ToString("N0");
                string cost = $"${obj["estimated_usd"].AsDouble():0.0000}";
                string src = obj["source_id"].AsString() ?? string.Empty;
                EventRows.Add(new UsageLogEventRow(timeText, model, tok, cost, src));
            }
        }
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
        sb.AppendLine("Model,Tokens,Requests,EstimatedUSD,Unpriced");
        foreach (var row in ModelRows)
        {
            sb.AppendLine($"\"{row.ModelName.Replace("\"", "\"\"")}\",\"{row.TokensText}\",\"{row.RequestsText}\",\"{row.CostText}\",{row.Unpriced}");
        }
        return sb.ToString();
    }

    private void ApplySummary(JsonObject res)
    {
        _activeSnapshotId = res["snapshot_id"].AsString();
        var periodNode = res["summary"]?.AsObject() ?? res[ActivePeriodKey]?.AsObject() ?? res;

        long totalTok = periodNode["total_tokens"].AsInt64();
        long inputTok = periodNode["input_tokens"].AsInt64();
        long cachedTok = periodNode["cached_tokens"].AsInt64();
        long reqs = periodNode["requests"].AsInt64();
        double usd = periodNode["estimated_usd"].AsDouble();

        TotalTokensText = totalTok.ToString("N0");
        TotalRequestsText = reqs.ToString("N0");
        EstimatedCostText = $"${usd:0.00}";

        double denom = inputTok + cachedTok;
        double hitRate = denom > 0 ? Math.Clamp((cachedTok / denom) * 100.0, 0.0, 100.0) : 0.0;
        CacheHitRateText = $"{hitRate:0.0}%";

        ModelRows.Clear();
        var slices = new List<DonutSlice>();
        bool anyUnpriced = false;

        var models = periodNode["models"]?.AsArray();
        if (models != null)
        {
            int idx = 0;
            foreach (var mNode in models)
            {
                var m = mNode?.AsObject();
                if (m == null) continue;
                string name = m["model"].AsString() ?? "unknown";
                long tok = m["total_tokens"].AsInt64();
                long mReqs = m["requests"].AsInt64();
                double mUsd = m["estimated_usd"].AsDouble();
                bool unpriced = m["unpriced"].AsBool();
                anyUnpriced |= unpriced;

                ModelRows.Add(new UsageModelRow(name, tok.ToString("N0"), mReqs.ToString("N0"), $"${mUsd:0.00}", unpriced));
                if (tok > 0)
                {
                    slices.Add(new DonutSlice(name, tok, SlicePalette[idx % SlicePalette.Length]));
                }
                idx++;
            }
        }

        HasUnpricedModels = anyUnpriced;
        DonutSlices = slices;
    }
}
