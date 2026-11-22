using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GravityBox.Win32;

namespace GravityBox.App.Core;

public sealed class AppConfiguration
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 2;

    [JsonPropertyName("lastPage")]
    public int LastPage { get; set; } = 0;

    [JsonPropertyName("navigationVersion")]
    public int? NavigationVersion { get; set; } = 5;

    [JsonPropertyName("quotaRefreshMinutes")]
    public int QuotaRefreshMinutes { get; set; } = 0;

    [JsonPropertyName("hudEnabled")]
    public bool HudEnabled { get; set; } = true;

    [JsonPropertyName("autoPricingSyncEnabled")]
    public bool AutoPricingSyncEnabled { get; set; } = false;

    [JsonPropertyName("syncAntigravityLifecycle")]
    public bool SyncAntigravityLifecycle { get; set; } = false;

    [JsonPropertyName("autoUpdateEnabled")]
    public bool AutoUpdateEnabled { get; set; } = false;

    [JsonPropertyName("lastCleanedVersion")]
    public string LastCleanedVersion { get; set; } = string.Empty;

    [JsonPropertyName("accountsCollapsed")]
    public bool AccountsCollapsed { get; set; } = false;

    [JsonPropertyName("menuSelectedTab")]
    public int MenuSelectedTab { get; set; } = 0;

    [JsonPropertyName("menuUsagePeriod")]
    public int MenuUsagePeriod { get; set; } = 0;

    [JsonPropertyName("menuTrendMetric")]
    public string MenuTrendMetric { get; set; } = "tokens";

    [JsonPropertyName("applicationChannel")]
    public string ApplicationChannel { get; set; } = "stable";

    /// <summary>
    /// Windows 默认简体中文 (FR-001 / INV-06)。
    /// </summary>
    [JsonPropertyName("appLanguage")]
    public string? AppLanguage { get; set; } = "zh-Hans";

    [JsonPropertyName("launchAtLogin")]
    public bool LaunchAtLogin { get; set; } = false;

    [JsonPropertyName("shortcutGuideDismissed")]
    public bool ShortcutGuideDismissed { get; set; } = false;

    [JsonPropertyName("customAntigravityPath")]
    public string? CustomAntigravityPath { get; set; }

    public void MigrateNavigation()
    {
        if (NavigationVersion == 5)
        {
            return;
        }

        LastPage = NavigationVersion switch
        {
            4 => LastPage switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 3, _ => 0 },
            3 => Math.Clamp(LastPage, 0, 3),
            2 => LastPage switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 3, _ => 0 },
            _ => LastPage switch { 0 => 0, 1 => 1, 2 => 3, 3 => 3, 4 => 3, _ => 0 }
        };
        NavigationVersion = 5;
    }

    public void Normalize()
    {
        Schema = 2;
        ApplicationChannel = "stable";
        MenuSelectedTab = Math.Clamp(MenuSelectedTab, 0, 2);
        MenuUsagePeriod = Math.Clamp(MenuUsagePeriod, 0, 3);
        MenuTrendMetric = MenuTrendMetric is "tokens" or "requests" ? MenuTrendMetric : "tokens";
        if (string.IsNullOrWhiteSpace(AppLanguage))
        {
            AppLanguage = "zh-Hans";
        }
        else
        {
            AppLanguage = AppLanguage.Trim();
        }
        LastCleanedVersion ??= string.Empty;
        MigrateNavigation();
    }
}

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private readonly object _sync = new();
    private JsonObject _retainedDocument = new();

    public string FilePath { get; }
    public string StateDirectory { get; }
    public AppConfiguration Value { get; private set; }

    public ConfigurationStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        StateDirectory = Path.GetFullPath(stateDirectory);
        FilePath = Path.Combine(StateDirectory, "config.json");
        Value = LoadInternal();
    }

    private AppConfiguration LoadInternal()
    {
        Directory.CreateDirectory(StateDirectory);
        try
        {
            SecurityAclHelper.HardenPathAcl(StateDirectory, isDirectory: true);
        }
        catch
        {
            // Non-fatal in constrained test environments
        }

        if (!File.Exists(FilePath))
        {
            var fresh = new AppConfiguration();
            fresh.Normalize();
            return fresh;
        }

        string raw = File.ReadAllText(FilePath, Encoding.UTF8);
        var rootNode = JsonNode.Parse(raw) as JsonObject
            ?? throw new BackendFailureException("config.json root must be a JSON object");

        _retainedDocument = (JsonObject)rootNode.DeepClone();
        var config = JsonSerializer.Deserialize<AppConfiguration>(raw, JsonOptions)
            ?? new AppConfiguration();

        // 若旧版 config.json 未显式包含 appLanguage，则按 FR-001 保留默认简体中文
        if (!rootNode.ContainsKey("appLanguage"))
        {
            config.AppLanguage = "zh-Hans";
        }

        config.Normalize();
        return config;
    }

    public void Update(Action<AppConfiguration> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            mutate(Value);
            SaveLocked();
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        Value.Normalize();
        Directory.CreateDirectory(StateDirectory);

        var serialized = JsonSerializer.SerializeToNode(Value, JsonOptions) as JsonObject ?? new JsonObject();
        var merged = (JsonObject)_retainedDocument.DeepClone();
        foreach (var kv in serialized)
        {
            merged[kv.Key] = kv.Value?.DeepClone();
        }

        string tempPath = Path.Combine(StateDirectory, $".config.{Guid.NewGuid():N}.tmp");
        string json = merged.ToJsonString(JsonOptions) + "\n";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, FilePath, overwrite: true);
        _retainedDocument = merged;

        try
        {
            SecurityAclHelper.HardenPathAcl(FilePath, isDirectory: false);
        }
        catch
        {
            // Non-fatal
        }
    }
}
