using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.App.Core;
using GravityBox.App.ViewModels;
using Xunit;

namespace GravityBox.Tests.ViewModelTests;

[Trait("Category", "Settings")]
public sealed class SettingsTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-settings-test-{Guid.NewGuid():N}");
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

    private sealed class FakeSettingsBridge : IBackendBridge
    {
        public string? LastLanguageSet { get; private set; }
        public bool SmartClearCalled { get; private set; }
        public string? RestoredSnapshotId { get; private set; }
        public bool RevalueApplied { get; private set; }
        public bool RevalueRolledBack { get; private set; }
        public bool ShortcutApplied { get; private set; }
        public bool ShortcutRestored { get; private set; }

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
            switch (command)
            {
                case "language_set":
                    LastLanguageSet = parameters?["language"].AsString();
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                case "maintenance_scan":
                    return Task.FromResult(new JsonObject { ["ok"] = true, ["runtimeBytes"] = 1024, ["logsBytes"] = 2048, ["protectedBytes"] = 4096 });
                case "maintenance_history":
                    return Task.FromResult(new JsonObject
                    {
                        ["ok"] = true,
                        ["snapshots"] = new JsonArray { new JsonObject { ["id"] = "snap-maint-1" } },
                    });
                case "maintenance_smart_clear":
                    SmartClearCalled = true;
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                case "maintenance_restore":
                    RestoredSnapshotId = parameters?["snapshotId"].AsString();
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                case "api_pricing_revalue_preview":
                    return Task.FromResult(new JsonObject
                    {
                        ["ok"] = true,
                        ["preview"] = new JsonObject { ["previewId"] = "rev-1", ["before"] = 1.0, ["after"] = 2.0 },
                    });
                case "api_pricing_revalue":
                    RevalueApplied = true;
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                case "api_pricing_revalue_rollback":
                    RevalueRolledBack = true;
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                case "shortcut_scan":
                    return Task.FromResult(new JsonObject
                    {
                        ["ok"] = true,
                        ["shortcuts"] = new JsonArray
                        {
                            new JsonObject { ["path"] = "Desktop\\Antigravity.lnk", ["location"] = "desktop", ["name"] = "Antigravity", ["hasDebugPort"] = false, ["hasBackup"] = false },
                        },
                    });
                case "shortcut_apply":
                    ShortcutApplied = true;
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                case "shortcut_restore":
                    ShortcutRestored = true;
                    return Task.FromResult(new JsonObject { ["ok"] = true });
                default:
                    return Task.FromResult(new JsonObject { ["ok"] = true });
            }
        }
    }

    [Fact]
    public async Task SettingsViewModel_CoversLanguage_Maintenance_Revalue_AndShortcuts()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot);
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        using var lifecycle = new LifecycleSyncService();
        var bridge = new FakeSettingsBridge();
        var vm = new SettingsViewModel(bridge, config, l10n, lifecycle);

        await vm.SetLanguageAsync("en");
        Assert.Equal("en", bridge.LastLanguageSet);
        Assert.Equal("en", l10n.ResolvedLocale);

        await vm.ScanMaintenanceAsync();
        Assert.True(vm.HasRestorePoint);
        Assert.Equal("snap-maint-1", vm.LatestSnapshotId);
        await vm.RunSmartClearAsync();
        Assert.True(bridge.SmartClearCalled);
        await vm.RestoreLatestSnapshotAsync();
        Assert.Equal("snap-maint-1", bridge.RestoredSnapshotId);

        await vm.PreviewRevalueAsync();
        Assert.Equal("rev-1", vm.RevaluePreviewId);
        await vm.ApplyRevalueAsync();
        Assert.True(bridge.RevalueApplied);
        await vm.RollbackRevalueAsync();
        Assert.True(bridge.RevalueRolledBack);

        await vm.ScanShortcutsAsync();
        Assert.Single(vm.Shortcuts);
        await vm.ApplyAllShortcutsAsync();
        Assert.True(bridge.ShortcutApplied);
        await vm.RestoreAllShortcutsAsync();
        Assert.True(bridge.ShortcutRestored);

        vm.SelectQuotaRefreshMinutes(15);
        Assert.Equal(15, config.Value.QuotaRefreshMinutes);
    }

    [Fact]
    public async Task SettingsViewModel_ParsesCanonicalMaintenanceScanBuckets()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = new ConfigurationStore(_tempRoot);
        var l10n = new L10nService(Path.Combine(repoRoot, "assets", "i18n"));
        using var lifecycle = new LifecycleSyncService();
        var vm = new SettingsViewModel(new CanonicalSettingsBridge(), config, l10n, lifecycle);

        await vm.RefreshAsync();

        Assert.Equal(2_500_000, vm.RuntimeCacheBytes);
        Assert.Equal(1_200_000, vm.LogsBytes);
        Assert.Equal(8_000_000, vm.ProtectedBytes);
        Assert.Contains("2.5 MB", vm.RuntimeCacheSizeText);
        Assert.Single(vm.Shortcuts);
    }

    private sealed class CanonicalSettingsBridge : IBackendBridge
    {
        public string StateDir => string.Empty;
        public string PipeName => "stub";
        public bool IsOwnedProcessRunning => true;
        public int? OwnedPid => 104;
        public string? PreferredLocale { get; set; } = "zh-Hans";
        public event Action<string>? OnExit { add { } remove { } }
        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JsonObject> SendAsync(string command, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<JsonObject>(command switch
            {
                "maintenance_scan" => new JsonObject
                {
                    ["ok"] = true,
                    ["buckets"] = new JsonObject
                    {
                        ["runtimeCache"] = new JsonObject { ["bytes"] = 2_500_000 },
                        ["historyLogs"] = new JsonObject { ["bytes"] = 1_200_000 },
                    },
                    ["protectedBytes"] = 8_000_000,
                },
                "maintenance_history" => new JsonObject { ["ok"] = true, ["snapshots"] = new JsonArray() },
                "pricing_sync_status" => new JsonObject { ["ok"] = true, ["status"] = new JsonObject { ["message"] = "已同步" } },
                "shortcut_scan" => new JsonObject
                {
                    ["ok"] = true,
                    ["shortcuts"] = new JsonArray
                    {
                        new JsonObject { ["path"] = "Desktop\\Antigravity.lnk", ["location"] = "desktop", ["name"] = "Antigravity", ["hasDebugPort"] = true, ["hasBackup"] = true },
                    },
                },
                _ => new JsonObject { ["ok"] = true },
            });
        }
    }
}
