using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.App.Core;
using GravityBox.App.ViewModels;
using Xunit;

namespace GravityBox.Tests.ViewModelTests;

[Trait("Category", "Overview")]
public sealed class OverviewTests : IDisposable
{
    private readonly string _tempRoot;

    public OverviewTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-overview-test-{Guid.NewGuid():N}");
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

    private sealed class FakeOverviewBridge : IBackendBridge
    {
        public bool ApplyCalled { get; private set; }
        public string Mode { get; set; } = "connected";
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
            if (command == "shortcut_apply")
            {
                ApplyCalled = true;
                return Task.FromResult(new JsonObject { ["ok"] = true, ["updated"] = new JsonArray("a.lnk") });
            }
            if (command == "shortcut_scan")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["shortcuts"] = new JsonArray
                    {
                        new JsonObject { ["path"] = "Desktop\\Antigravity.lnk", ["hasDebugPort"] = false },
                    },
                });
            }
            if (command == "status")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["status"] = new JsonObject
                    {
                        ["mode"] = Mode,
                        ["activeAccountEmail"] = "demo@example.invalid",
                        ["activeSubscriptionTier"] = "ULTRA",
                    },
                });
            }
            if (command == "dashboard_summary")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["usage"] = new JsonObject
                    {
                        ["today"] = new JsonObject
                        {
                            ["total_tokens"] = 98765,
                            ["requests"] = 42,
                            ["estimated_usd"] = 1.25,
                        },
                    },
                    ["quota"] = new JsonObject
                    {
                        ["groups"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["displayName"] = "Gemini",
                                ["buckets"] = new JsonArray
                                {
                                    new JsonObject { ["window"] = "5h", ["remainingFraction"] = 0.75, ["resetTime"] = "2h" },
                                },
                            },
                        },
                    },
                });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }

    [Fact]
    public async Task OverviewViewModel_LoadsDashboardAndShortcutOnboardingCard()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot);
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        using var lifecycle = new LifecycleSyncService();
        var bridge = new FakeOverviewBridge { Mode = "debug-port-missing" };
        var vm = new OverviewViewModel(bridge, config, l10n, lifecycle);

        await vm.RefreshAsync(force: true);

        Assert.True(vm.DebugPortMissing);
        Assert.True(vm.ShowShortcutOnboardingCard);
        Assert.Equal("demo@example.invalid", vm.HeroAccountEmail);
        Assert.Equal("ULTRA", vm.HeroAccountTier);
        Assert.Equal("98,765", vm.TodayTokensText);
        Assert.Equal("$1.25", vm.TodayCostText);
        Assert.Single(vm.QuotaBars);

        await vm.ApplySeamlessShortcutsAsync();
        Assert.True(bridge.ApplyCalled);
        Assert.False(vm.ShowShortcutOnboardingCard);

        vm.DismissShortcutOnboarding();
        Assert.True(config.Value.ShortcutGuideDismissed);
    }
}
