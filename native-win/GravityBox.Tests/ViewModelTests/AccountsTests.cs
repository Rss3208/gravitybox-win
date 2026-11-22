using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.App.Core;
using GravityBox.App.ViewModels;
using Xunit;

namespace GravityBox.Tests.ViewModelTests;

[Trait("Category", "Accounts")]
public sealed class AccountsTests : IDisposable
{
    private readonly string _tempRoot;

    public AccountsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-accounts-test-{Guid.NewGuid():N}");
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

    private sealed class FakeAccountsBridge : IBackendBridge
    {
        public bool HasRunningTask { get; set; }
        public bool SwitchCalled { get; private set; }
        public bool RollbackCalled { get; private set; }
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
            if (command == "account_list")
            {
                return Task.FromResult(new JsonObject
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
                            ["standbyDeferred"] = false,
                        },
                        new JsonObject
                        {
                            ["id"] = "acc-2",
                            ["email"] = "standby@example.invalid",
                            ["name"] = "Standby",
                            ["subscription_tier"] = "FREE",
                            ["isActive"] = false,
                            ["standbyDeferred"] = true,
                        },
                    },
                    ["recovery"] = new JsonObject { ["available"] = true },
                });
            }
            if (command == "auto_switch_get")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["config"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["strategy"] = "priority",
                        ["reserve_percentage"] = 20,
                        ["candidate_min_percentage"] = 30,
                    },
                });
            }
            if (command == "account_switch_preflight")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["appRunning"] = false,
                    ["hasRunningTask"] = HasRunningTask,
                    ["safe"] = !HasRunningTask,
                    ["taskReason"] = HasRunningTask ? "推理进行中" : "",
                });
            }
            if (command == "account_switch")
            {
                SwitchCalled = true;
                return Task.FromResult(new JsonObject { ["ok"] = true });
            }
            if (command == "account_switch_rollback")
            {
                RollbackCalled = true;
                return Task.FromResult(new JsonObject { ["ok"] = true });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }

    [Fact]
    public async Task AccountsViewModel_HandlesStandbyDeferred_PreflightBlock_SwitchAndRollback()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot);
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        using var lifecycle = new LifecycleSyncService();
        var bridge = new FakeAccountsBridge();
        var vm = new AccountsViewModel(bridge, config, l10n, lifecycle);

        await vm.RefreshAsync();
        Assert.Equal(2, vm.Accounts.Count);
        Assert.True(vm.Accounts[1].StandbyDeferred);
        Assert.Equal("预计已重置", vm.Accounts[1].StandbyBadgeText);
        Assert.True(vm.HasRollbackAvailable);

        bridge.HasRunningTask = true;
        await vm.SwitchAccountAsync("acc-2");
        Assert.False(bridge.SwitchCalled);
        Assert.Contains("推理进行中", vm.ErrorBanner);

        bridge.HasRunningTask = false;
        await vm.SwitchAccountAsync("acc-2");
        Assert.True(bridge.SwitchCalled);

        await vm.RollbackSwitchAsync();
        Assert.True(bridge.RollbackCalled);
    }
}
