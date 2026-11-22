using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GravityBox.App.Core;
using Xunit;

namespace GravityBox.Tests.CoreTests;

[Trait("Category", "CoreServices")]
public sealed class CoreServicesTests : IDisposable
{
    private readonly string _tempRoot;

    public CoreServicesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-core-test-{Guid.NewGuid():N}", "测试 用户");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            string top = Directory.GetParent(_tempRoot)?.FullName ?? _tempRoot;
            if (Directory.Exists(top))
            {
                Directory.Delete(top, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void ConfigurationStore_FreshInstall_DefaultsToZhHansAndHudEnabled()
    {
        string stateDir = Path.Combine(_tempRoot, "fresh-state");
        var store = new ConfigurationStore(stateDir);

        Assert.Equal("zh-Hans", store.Value.AppLanguage);
        Assert.True(store.Value.HudEnabled);
        Assert.Equal(2, store.Value.Schema);
        Assert.Equal(5, store.Value.NavigationVersion);
        Assert.Equal("stable", store.Value.ApplicationChannel);
    }

    [Fact]
    public void ConfigurationStore_PreservesUnknownFieldsAndMigratesLegacyNavigation()
    {
        string stateDir = Path.Combine(_tempRoot, "legacy-state");
        Directory.CreateDirectory(stateDir);
        string configPath = Path.Combine(stateDir, "config.json");
        File.WriteAllText(configPath, """
        {
          "schema": 2,
          "navigationVersion": 4,
          "lastPage": 4,
          "customBackendFlag": "keep-me",
          "nestedUnknown": { "count": 42 }
        }
        """, Encoding.UTF8);

        var store = new ConfigurationStore(stateDir);
        Assert.Equal("zh-Hans", store.Value.AppLanguage);
        Assert.Equal(5, store.Value.NavigationVersion);
        Assert.Equal(3, store.Value.LastPage);

        store.Update(cfg =>
        {
            cfg.AppLanguage = "en";
            cfg.LaunchAtLogin = true;
        });

        var savedJson = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8))!.AsObject();
        Assert.Equal("en", savedJson["appLanguage"]!.GetValue<string>());
        Assert.True(savedJson["launchAtLogin"]!.GetValue<bool>());
        Assert.Equal("keep-me", savedJson["customBackendFlag"]!.GetValue<string>());
        Assert.Equal(42, savedJson["nestedUnknown"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void L10nService_LoadsRepoPacks_SupportsInterpolationAndHotSwitching()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string i18nDir = Path.Combine(repoRoot, "assets", "i18n");
        Assert.True(Directory.Exists(i18nDir), $"Expected i18n dir at {i18nDir}");

        string systemLocale = "en-US";
        var l10n = new L10nService(i18nDir, () => new[] { systemLocale });

        Assert.Equal("zh-Hans", l10n.ConfiguredLanguage);
        Assert.Equal("zh-Hans", l10n.ResolvedLocale);
        Assert.Equal("总览", l10n.T("nav.overview"));

        var events = new List<(string Configured, string Resolved)>();
        l10n.LanguageChanged += (c, r) => events.Add((c, r));

        l10n.SetLanguage("en");
        Assert.Equal("en", l10n.ResolvedLocale);
        Assert.Equal("Overview", l10n.T("nav.overview"));

        l10n.SetLanguage("system");
        Assert.Equal("system", l10n.ConfiguredLanguage);
        Assert.Equal("en", l10n.ResolvedLocale);

        systemLocale = "zh-CN";
        l10n.SetLanguage("system");
        Assert.Equal("zh-Hans", l10n.ResolvedLocale);
        Assert.Equal("总览", l10n.T("nav.overview"));

        Assert.Equal("nonexistent.key", l10n.T("nonexistent.key"));
        Assert.True(events.Count >= 2);
    }

    [Fact]
    public void ThemeService_SwitchesLightAndDarkAndRaisesEvent()
    {
        bool isLight = true;
        var service = new ThemeService(() => isLight, subscribeSystemEvents: false);
        Assert.Equal(AppThemeMode.Light, service.CurrentTheme);

        AppThemeMode? observed = null;
        service.ThemeChanged += mode => observed = mode;

        isLight = false;
        service.RefreshFromSystem();
        Assert.Equal(AppThemeMode.Dark, service.CurrentTheme);
        Assert.Equal(AppThemeMode.Dark, observed);
    }

    [Fact]
    public void BackendBridge_LogRotation_TrimsToMaxArchives()
    {
        string logDir = Path.Combine(_tempRoot, "logs");
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "backend.log");
        File.WriteAllText(logPath, new string('x', 2048), Encoding.UTF8);

        BackendBridge.RotateLogIfNeeded(logPath, maxBytes: 1024, maxArchives: 2);
        Assert.False(File.Exists(logPath));

        string archiveDir = Path.Combine(logDir, "archive");
        Assert.Single(Directory.GetFiles(archiveDir, "backend-*.log"));
    }

    [Fact]
    public async Task BackendBridge_NamedPipe_InjectsAuthTokenAndParsesResponse()
    {
        string stateDir = Path.Combine(_tempRoot, "pipe-state");
        Directory.CreateDirectory(stateDir);
        string token = "test-ipc-token-123456";
        File.WriteAllText(Path.Combine(stateDir, "ipc.token"), token, Encoding.UTF8);

        string pipeName = BackendBridge.ComputePipeName(stateDir);
        string? receivedToken = null;
        string? receivedCommand = null;

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync();
            var req = JsonNode.Parse(line!)!.AsObject();
            receivedToken = req["authToken"]?.GetValue<string>();
            receivedCommand = req["command"]?.GetValue<string>();

            byte[] reply = Encoding.UTF8.GetBytes("{\"ok\":true,\"pong\":true}\n");
            await server.WriteAsync(reply);
            await server.FlushAsync();
        });

        await using var bridge = new BackendBridge(_tempRoot, stateDir);
        var res = await bridge.SendAsync("ping");

        await serverTask;
        Assert.Equal(token, receivedToken);
        Assert.Equal("ping", receivedCommand);
        Assert.True(res["ok"]!.GetValue<bool>());
        Assert.True(res["pong"]!.GetValue<bool>());
    }
}
