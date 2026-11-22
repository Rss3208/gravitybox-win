using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace GravityBox.App.Core;

public enum AppThemeMode
{
    Light,
    Dark,
}

public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const int DWMWA_USE_IMMUTABLE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly Func<bool> _isLightThemeReader;
    private bool _listeningSystemEvents;
    private bool _disposed;

    public AppThemeMode CurrentTheme { get; private set; }

    public event Action<AppThemeMode>? ThemeChanged;

    public ThemeService(Func<bool>? isLightThemeReader = null, bool subscribeSystemEvents = true)
    {
        _isLightThemeReader = isLightThemeReader ?? ReadWindowsAppsUseLightTheme;
        CurrentTheme = _isLightThemeReader() ? AppThemeMode.Light : AppThemeMode.Dark;

        if (subscribeSystemEvents)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listeningSystemEvents = true;
        }
    }

    public static bool ReadWindowsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue(AppsUseLightThemeValue) is int dword)
            {
                return dword != 0;
            }
        }
        catch
        {
            // Default to Light when registry unavailable
        }
        return true;
    }

    public void RefreshFromSystem()
    {
        var next = _isLightThemeReader() ? AppThemeMode.Light : AppThemeMode.Dark;
        if (next == CurrentTheme)
        {
            return;
        }

        CurrentTheme = next;
        ApplyToApplication(System.Windows.Application.Current);
        ThemeChanged?.Invoke(CurrentTheme);
    }

    public void ApplyToApplication(System.Windows.Application? app)
    {
        if (app == null)
        {
            return;
        }

        string tokenUri = CurrentTheme == AppThemeMode.Dark
            ? "pack://application:,,,/GravityBox;component/Theme/DesignTokens.Dark.xaml"
            : "pack://application:,,,/GravityBox;component/Theme/DesignTokens.Light.xaml";

        try
        {
            var merged = app.Resources.MergedDictionaries;
            ResourceDictionary? existingTokens = null;
            int existingIndex = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.ToString() ?? string.Empty;
                if (src.Contains("DesignTokens.", StringComparison.OrdinalIgnoreCase))
                {
                    existingTokens = merged[i];
                    existingIndex = i;
                    break;
                }
            }

            var nextDict = new ResourceDictionary { Source = new Uri(tokenUri, UriKind.Absolute) };
            if (existingTokens != null && existingIndex >= 0)
            {
                merged[existingIndex] = nextDict;
            }
            else
            {
                merged.Insert(0, nextDict);
            }
        }
        catch
        {
            // Resource dictionaries may not exist in early unit test harness
        }

        foreach (Window win in app.Windows)
        {
            ApplyWindowTitleBarTheme(win, CurrentTheme);
        }
    }

    public static void ApplyWindowTitleBarTheme(Window window, AppThemeMode theme)
    {
        if (window == null) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = theme == AppThemeMode.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMUTABLE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch
        {
            // Best effort on Windows 10 < 18985
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
        {
            RefreshFromSystem();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_listeningSystemEvents)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listeningSystemEvents = false;
        }
    }
}
