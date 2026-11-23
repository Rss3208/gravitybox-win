using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GravityBox.App.Core;
using GravityBox.App.Services;
using GravityBox.App.ViewModels;
using GravityBox.App.Views;

namespace GravityBox.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private ThemeService? _themeService;
    private BackendBridge? _bridge;
    private LifecycleSyncService? _lifecycle;
    private AppUpdateManager? _updateManager;
    private TrayIconService? _trayIcon;
    private TrayFlyoutWindow? _flyoutWindow;
    private MainWindow? _mainWindow;
    private MainShellViewModel? _shellVm;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GravityBox",
                    "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "app-crash.log"),
                    $"[{DateTime.Now:O}] {args.Exception}\n");
            }
            catch { }
        };

        int exportIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--export-screenshots", StringComparison.OrdinalIgnoreCase));
        if (exportIdx >= 0)
        {
            string outDir = exportIdx + 1 < e.Args.Length
                ? e.Args[exportIdx + 1]
                : Path.Combine(Environment.CurrentDirectory, "assets", "screenshots", "win");
            try
            {
                await DemoScreenshotExporter.ExportAsync(this, outDir);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[export-screenshots] {ex}");
                Shutdown(1);
            }
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\GravityBox-SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        bool minimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        string baseDir = AppContext.BaseDirectory;
        string stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GravityBox");
        string logDir = Path.Combine(stateDir, "logs");
        string i18nDir = Path.Combine(baseDir, "assets", "i18n");
        string iconPath = Path.Combine(baseDir, "assets", "AppIcon.ico");

        var config = new ConfigurationStore(stateDir);
        var l10n = new L10nService(i18nDir);
        l10n.SetLanguage(config.Value.AppLanguage);

        _themeService = new ThemeService();
        _themeService.ApplyToApplication(this);

        _bridge = new BackendBridge(baseDir, stateDir, logDir)
        {
            PreferredLocale = config.Value.AppLanguage,
        };
        _lifecycle = new LifecycleSyncService();
        _lifecycle.AutoExitRequested += () => Dispatcher.Invoke(() => _ = ShutdownAppAsync());
        _updateManager = new AppUpdateManager("1.1.3");

        var overviewVm = new OverviewViewModel(_bridge, config, l10n, _lifecycle);
        var accountsVm = new AccountsViewModel(_bridge, config, l10n, _lifecycle);
        var usageVm = new UsageViewModel(_bridge, config, l10n);
        var settingsVm = new SettingsViewModel(_bridge, config, l10n, _lifecycle);
        var aboutVm = new AboutViewModel(config, l10n, _updateManager, _bridge);

        var flyoutVm = new TrayFlyoutViewModel(_bridge, config, l10n);
        _shellVm = new MainShellViewModel(
            _bridge,
            config,
            l10n,
            _lifecycle,
            new CommunityToolkit.Mvvm.ComponentModel.ObservableObject[]
            {
                overviewVm,
                accountsVm,
                usageVm,
                settingsVm,
                aboutVm,
            });
        _shellVm.ExitApplicationRequested += () => _ = ShutdownAppAsync();
        _shellVm.StatusChanged += (mode, message) =>
        {
            overviewVm.SyncLiveStatus(mode);
            flyoutVm.SyncLiveStatus(mode, message);
        };
        _shellVm.RefreshCurrentPageRequested += () =>
        {
            switch (_shellVm.SelectedPageIndex)
            {
                case 0: _ = overviewVm.RefreshAsync(force: true); break;
                case 1: _ = accountsVm.RefreshAsync(); break;
                case 2: _ = usageVm.RefreshAsync(); break;
                case 3: _ = settingsVm.RefreshAsync(); break;
                case 4: _ = aboutVm.RefreshAsync(); break;
            }
        };

        _mainWindow = new MainWindow(_shellVm, _themeService);
        _flyoutWindow = new TrayFlyoutWindow(flyoutVm);

        flyoutVm.OpenMainWindowRequested += ShowMainWindow;
        flyoutVm.OpenMainPageRequested += pageIdx =>
        {
            _shellVm.NavigateTo(pageIdx);
            ShowMainWindow();
        };
        flyoutVm.OpenAntigravityRequested += () => _ = _shellVm.OpenAntigravityAsync();
        flyoutVm.SafeRelaunchRequested += () => _ = overviewVm.SafeRelaunchAsync();

        _trayIcon = new TrayIconService(l10n, iconPath);
        _trayIcon.LeftClickRequested += () => Dispatcher.InvokeAsync(() => _flyoutWindow.ShowNearCursor());
        _trayIcon.OpenMainWindowRequested += () => Dispatcher.InvokeAsync(ShowMainWindow);
        _trayIcon.OpenAntigravityRequested += () => Dispatcher.InvokeAsync(() => _ = _shellVm.OpenAntigravityAsync());
        _trayIcon.LanguageChangeRequested += lang =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                config.Update(c => c.AppLanguage = lang);
                l10n.SetLanguage(lang);
                _ = _bridge.SendAsync("language_set", new System.Text.Json.Nodes.JsonObject { ["language"] = lang });
            });
        };
        _trayIcon.ExitRequested += () => Dispatcher.InvokeAsync(() => _ = ShutdownAppAsync());

        try
        {
            await _bridge.LaunchAsync();
        }
        catch
        {
            // Surface via status polling banner
        }

        _shellVm.StartPolling();
        _shellVm.RefreshCurrentPage();

        if (!minimized)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    private async Task ShutdownAppAsync()
    {
        if (_mainWindow != null)
        {
            _mainWindow.AllowExplicitClose = true;
            _mainWindow.Close();
        }
        _flyoutWindow?.Close();
        _trayIcon?.Dispose();
        _shellVm?.Dispose();
        _lifecycle?.Dispose();
        _updateManager?.Dispose();
        _themeService?.Dispose();
        if (_bridge != null)
        {
            await _bridge.DisposeAsync();
        }
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown(0);
    }
}
