using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GravityBox.App.Core;
using GravityBox.App.ViewModels;
using GravityBox.App.Views;
using Xunit;

namespace GravityBox.Tests.ViewModelTests;

[Trait("Category", "TrayFlyout")]
public sealed class TrayFlyoutTests : IDisposable
{
    private readonly string _tempRoot;

    public TrayFlyoutTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-tray-test-{Guid.NewGuid():N}");
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

    private sealed class StubBridge : IBackendBridge
    {
        public string StateDir => string.Empty;
        public string PipeName => "stub";
        public bool IsOwnedProcessRunning => true;
        public int? OwnedPid => 1234;
        public string? PreferredLocale { get; set; } = "zh-Hans";
        public event Action<string>? OnExit { add { } remove { } }

        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JsonObject> SendAsync(string command, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            JsonObject reply = command switch
            {
                "status" => new JsonObject
                {
                    ["ok"] = true,
                    ["status"] = new JsonObject
                    {
                        ["mode"] = "debug-port-missing",
                        ["message"] = "未开启调试端口",
                        ["activeAccountEmail"] = "demo@example.invalid",
                        ["activeSubscriptionTier"] = "PRO",
                    },
                },
                "dashboard_summary" => new JsonObject
                {
                    ["ok"] = true,
                    ["usage"] = new JsonObject
                    {
                        ["today"] = new JsonObject
                        {
                            ["total_tokens"] = 12345,
                            ["requests"] = 17,
                            ["estimated_usd"] = 0.42,
                        },
                    },
                    ["quota"] = new JsonObject
                    {
                        ["groups"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["displayName"] = "Gemini Models",
                                ["buckets"] = new JsonArray
                                {
                                    new JsonObject { ["remainingFraction"] = 0.8, ["resetTime"] = "3h 20m" },
                                },
                            },
                        },
                    },
                },
                "account_list" => new JsonObject
                {
                    ["ok"] = true,
                    ["accounts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "acc-1",
                            ["email"] = "demo@example.invalid",
                            ["name"] = "Demo",
                            ["subscription_tier"] = "PRO",
                            ["isActive"] = true,
                        },
                    },
                },
                _ => new JsonObject { ["ok"] = true },
            };
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task TrayFlyoutViewModel_RefreshesState_PersistsTabAndPeriod()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot);
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        var vm = new TrayFlyoutViewModel(new StubBridge(), config, l10n);

        await vm.RefreshAsync();

        Assert.True(vm.DebugPortMissing);
        Assert.Equal("demo@example.invalid", vm.ActiveAccountEmail);
        Assert.Equal("PRO", vm.ActiveAccountTier);
        Assert.Equal("12,345", vm.TodayTokensText);
        Assert.Equal("$0.42", vm.TodayCostText);
        Assert.Single(vm.QuotaRows);
        Assert.Single(vm.Accounts);

        vm.SelectTab(2);
        Assert.Equal(2, config.Value.MenuSelectedTab);
    }

    [Fact]
    public void TrayFlyoutPositioner_ClampsAcrossTaskbarEdgesAndHighDpi()
    {
        var monitor = new Rect(0, 0, 1920, 1080);

        // Bottom taskbar (48 DIP)
        var workBottom = new Rect(0, 0, 1920, 1032);
        Assert.Equal(TaskbarEdge.Bottom, TrayFlyoutPositioner.DetectTaskbarEdge(monitor, workBottom));
        var posBottom = TrayFlyoutPositioner.CalculatePosition(monitor, workBottom, new Rect(1880, 1040, 24, 24), 424, 420);
        Assert.Equal(1920 - 424 - 8, posBottom.X);
        Assert.Equal(1032 - 420 - 8, posBottom.Y);

        // Top taskbar
        var workTop = new Rect(0, 48, 1920, 1032);
        Assert.Equal(TaskbarEdge.Top, TrayFlyoutPositioner.DetectTaskbarEdge(monitor, workTop));
        var posTop = TrayFlyoutPositioner.CalculatePosition(monitor, workTop, new Rect(1600, 10, 24, 24), 424, 420);
        Assert.Equal(48 + 8, posTop.Y);

        // Left taskbar
        var workLeft = new Rect(64, 0, 1856, 1080);
        Assert.Equal(TaskbarEdge.Left, TrayFlyoutPositioner.DetectTaskbarEdge(monitor, workLeft));
        var posLeft = TrayFlyoutPositioner.CalculatePosition(monitor, workLeft, new Rect(20, 900, 24, 24), 424, 420);
        Assert.Equal(64 + 8, posLeft.X);
    }
}
