using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.App.Core;
using GravityBox.App.ViewModels;
using Xunit;

namespace GravityBox.Tests.ViewModelTests;

[Trait("Category", "Usage")]
public sealed class UsageTests : IDisposable
{
    private readonly string _tempRoot;

    public UsageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-usage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private sealed class FakeUsageBridge : IBackendBridge
    {
        public string? ReleasedSnapshotId { get; private set; }
        public string StateDir => string.Empty;
        public string PipeName => "stub";
        public bool IsOwnedProcessRunning => true;
        public int? OwnedPid => 100;
        public string? PreferredLocale { get; set; } = "zh-Hans";
        public event Action<string>? OnExit { add { } remove { } }
        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JsonObject> SendAsync(string command, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (command == "usage_summary")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["snapshot_id"] = "snap-123",
                    ["summary"] = new JsonObject
                    {
                        ["total_tokens"] = 50000,
                        ["input_tokens"] = 10000,
                        ["cached_tokens"] = 30000,
                        ["requests"] = 15,
                        ["estimated_usd"] = 0.88,
                        ["models"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["model"] = "gemini-3.8-flash",
                                ["total_tokens"] = 50000,
                                ["requests"] = 15,
                                ["estimated_usd"] = 0.88,
                                ["unpriced"] = false,
                            },
                        },
                    },
                });
            }
            if (command == "usage_events")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["total"] = 1,
                    ["rows"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["timestamp_seconds"] = 1760000000,
                            ["model"] = "gemini-3.8-flash",
                            ["total_tokens"] = 1500,
                            ["estimated_usd"] = 0.012,
                            ["source_id"] = "conv-1",
                        },
                    },
                });
            }
            if (command == "usage_snapshot_release")
            {
                ReleasedSnapshotId = parameters?["snapshotId"].AsString();
                return Task.FromResult(new JsonObject { ["ok"] = true });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }

    [Fact]
    public async Task UsageViewModel_ComputesCacheHitRate_LoadsPagedEvents_AndReleasesSnapshot()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot);
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        var bridge = new FakeUsageBridge();
        var vm = new UsageViewModel(bridge, config, l10n);

        await vm.RefreshAsync();
        Assert.Equal("50,000", vm.TotalTokensText);
        Assert.Equal("$0.88", vm.EstimatedCostText);
        Assert.Equal("75.0%", vm.CacheHitRateText);
        Assert.Single(vm.ModelRows);
        Assert.Single(vm.DonutSlices);

        await vm.LoadEventsPageAsync(0);
        Assert.Single(vm.EventRows);

        await vm.ReleaseSnapshotAsync();
        Assert.Equal("snap-123", bridge.ReleasedSnapshotId);
    }

    [Fact]
    public async Task UsageViewModel_ParsesCanonicalNodeIpcPayload_ByModelAndTrends()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot) { Value = { MenuUsagePeriod = 0 } };
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        var vm = new UsageViewModel(new CanonicalUsageBridge(), config, l10n);

        await vm.RefreshAsync();

        Assert.Equal("264,367,614", vm.TotalTokensText);
        Assert.Equal("$57.31", vm.EstimatedCostText);
        Assert.Equal("1,234", vm.TotalRequestsText);
        Assert.Equal("95.8%", vm.CacheHitRateText);
        Assert.Equal(2, vm.ModelRows.Count);
        Assert.Equal("gemini-3.8-pro", vm.ModelRows[0].ModelName);
        Assert.Equal(2, vm.DonutSlices.Count);
        Assert.Equal(2, vm.TrendPoints.Count);
        Assert.Equal("09时", vm.TrendPoints[0].Label);

        vm.SelectPeriod(1);
        await Task.Delay(50);
        Assert.Equal("7d", vm.ActivePeriodKey);
        await vm.RefreshAsync();
        Assert.Single(vm.TrendPoints);
        Assert.Equal("11-22", vm.TrendPoints[0].Label);

        await vm.ReleaseSnapshotAsync();
    }

    private sealed class CanonicalUsageBridge : IBackendBridge
    {
        public string StateDir => string.Empty;
        public string PipeName => "stub";
        public bool IsOwnedProcessRunning => true;
        public int? OwnedPid => 101;
        public string? PreferredLocale { get; set; } = "zh-Hans";
        public event Action<string>? OnExit { add { } remove { } }
        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JsonObject> SendAsync(string command, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (command == "usage_summary")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["snapshotId"] = "snap-real-1",
                    ["summary"] = new JsonObject
                    {
                        ["totalTokens"] = 264367614,
                        ["promptTokens"] = 10000000,
                        ["cachedTokens"] = 228000000,
                        ["candidatesTokens"] = 26367614,
                        ["requestCount"] = 1234,
                        ["estimatedUsd"] = 57.31,
                    },
                    ["byModel"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["model"] = "gemini-3.8-pro",
                            ["totalTokens"] = 200000000,
                            ["requestCount"] = 900,
                            ["estimatedUsd"] = 45.10,
                            ["priced"] = true,
                        },
                        new JsonObject
                        {
                            ["model"] = "claude-4.5-sonnet",
                            ["totalTokens"] = 64367614,
                            ["requestCount"] = 334,
                            ["estimatedUsd"] = 12.21,
                            ["priced"] = true,
                        },
                    },
                    ["todayHourly"] = new JsonArray
                    {
                        new JsonObject { ["hour"] = 9, ["total_tokens"] = 120000, ["request_count"] = 12 },
                        new JsonObject { ["hour"] = 10, ["total_tokens"] = 240000, ["request_count"] = 24 },
                    },
                    ["dailyTrends"] = new JsonArray
                    {
                        new JsonObject { ["date"] = "2026-11-22", ["total_tokens"] = 264367614, ["request_count"] = 1234 },
                    },
                });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }
}
