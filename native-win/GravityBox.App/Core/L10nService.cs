using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GravityBox.Win32;

namespace GravityBox.App.Core;

public sealed class L10nService
{
    private static readonly Regex PlaceholderRegex = new(@"\{([A-Za-z0-9_.-]+)\}", RegexOptions.Compiled);

    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, string>> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<string>> _systemLocalesProvider;

    public string I18nDirectory { get; }
    public string ConfiguredLanguage { get; private set; } = "zh-Hans";
    public string ResolvedLocale { get; private set; } = "zh-Hans";

    public event Action<string, string>? LanguageChanged;

    public L10nService(string i18nDirectory, Func<IReadOnlyList<string>>? systemLocalesProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(i18nDirectory);
        I18nDirectory = Path.GetFullPath(i18nDirectory);
        _systemLocalesProvider = systemLocalesProvider ?? SystemLocaleHelper.GetPreferredUiLanguages;
        LoadPacks();
    }

    private void LoadPacks()
    {
        LoadPack("zh-Hans", Path.Combine(I18nDirectory, "zh-Hans.json"));
        LoadPack("en", Path.Combine(I18nDirectory, "en.json"));
    }

    private void LoadPack(string locale, string filePath)
    {
        if (!File.Exists(filePath))
        {
            _packs[locale] = new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }

        string json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("app", out var appEl) && appEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in appEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    flat[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
        }
        _packs[locale] = flat;
    }

    public static string NormalizeLanguageCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "zh-Hans";
        }

        string trimmed = raw.Trim();
        string lower = trimmed.ToLowerInvariant();
        if (lower == "system")
        {
            return "system";
        }
        if (lower is "zh" or "zh-cn" or "zh-hans" or "zh-sg" or "zh-hans-cn" or "zh-hans-sg")
        {
            return "zh-Hans";
        }
        if (lower == "en" || lower.StartsWith("en-"))
        {
            return "en";
        }
        if (lower.StartsWith("zh-"))
        {
            return "zh-Hans";
        }
        return "en";
    }

    public string ResolveLocale(string? configuredLanguage)
    {
        string normalized = NormalizeLanguageCode(configuredLanguage);
        if (normalized != "system")
        {
            return normalized;
        }

        var locales = _systemLocalesProvider();
        foreach (var loc in locales)
        {
            if (string.IsNullOrWhiteSpace(loc)) continue;
            string norm = NormalizeLanguageCode(loc);
            if (norm is "zh-Hans" or "en")
            {
                return norm;
            }
        }

        return "en";
    }

    public void SetLanguage(string? configuredLanguage)
    {
        string normalized = NormalizeLanguageCode(configuredLanguage);
        string resolved = ResolveLocale(normalized);
        bool changed;
        lock (_sync)
        {
            changed = ConfiguredLanguage != normalized || ResolvedLocale != resolved;
            ConfiguredLanguage = normalized;
            ResolvedLocale = resolved;
        }

        if (changed)
        {
            LanguageChanged?.Invoke(normalized, resolved);
        }
    }

    public string T(string key, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        string activeLocale;
        lock (_sync)
        {
            activeLocale = ResolvedLocale;
        }

        string? template = null;
        if (_packs.TryGetValue(activeLocale, out var activePack) && activePack.TryGetValue(key, out var val))
        {
            template = val;
        }
        else if (_packs.TryGetValue("en", out var enPack) && enPack.TryGetValue(key, out var enVal))
        {
            template = enVal;
        }
        else if (_packs.TryGetValue("zh-Hans", out var zhPack) && zhPack.TryGetValue(key, out var zhVal))
        {
            template = zhVal;
        }

        template ??= key;
        if (parameters == null || parameters.Count == 0)
        {
            return template;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            string name = match.Groups[1].Value;
            return parameters.TryGetValue(name, out var paramVal) && paramVal != null
                ? Convert.ToString(paramVal) ?? string.Empty
                : match.Value;
        });
    }

    public string T(string key, params (string Key, object? Value)[] parameters)
    {
        if (parameters.Length == 0)
        {
            return T(key, (IReadOnlyDictionary<string, object?>?)null);
        }
        var dict = new Dictionary<string, object?>(parameters.Length, StringComparer.Ordinal);
        foreach (var (k, v) in parameters)
        {
            dict[k] = v;
        }
        return T(key, dict);
    }
}
