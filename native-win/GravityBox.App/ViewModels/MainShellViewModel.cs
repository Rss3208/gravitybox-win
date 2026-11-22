using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Core;

namespace GravityBox.App.ViewModels;

public sealed partial class MainShellViewModel : ObservableObject, IDisposable
{
    private readonly IBackendBridge _bridge;
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private readonly LifecycleSyncService _lifecycle;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly IReadOnlyList<ObservableObject>? _pages;

    private long _taskRevision = -1;
    private bool _disposed;

    [ObservableProperty]
    private int _selectedPageIndex;

    [ObservableProperty]
    private ObservableObject? _currentPageViewModel;

    [ObservableProperty]
    private string _navOverviewText = "总览";

    [ObservableProperty]
    private string _navAccountsText = "账号";

    [ObservableProperty]
    private string _navUsageText = "用量";

    [ObservableProperty]
    private string _navSettingsText = "设置";

    [ObservableProperty]
    private string _navAboutText = "关于";

    [ObservableProperty]
    private string _openAntigravityText = "打开 Antigravity";

    [ObservableProperty]
    private string _statusMode = "starting";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    [ObservableProperty]
    private bool _hasBanner;

    [ObservableProperty]
    private bool _hasUpdateBadge;

    [ObservableProperty]
    private int _runningTasksCount;

    [ObservableProperty]
    private string _officialExePath = string.Empty;

    [ObservableProperty]
    private string _profileDir = string.Empty;

    public event Action? ExitApplicationRequested;
    public event Action? RefreshCurrentPageRequested;

    public MainShellViewModel(
        IBackendBridge bridge,
        ConfigurationStore config,
        L10nService l10n,
        LifecycleSyncService lifecycle,
        IReadOnlyList<ObservableObject>? pages = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _pages = pages;

        _selectedPageIndex = Math.Clamp(_config.Value.LastPage, 0, 4);
        UpdateCurrentPage();
        RefreshLocalizedLabels();

        _l10n.LanguageChanged += (_, _) => RefreshLocalizedLabels();
    }

    public void RefreshLocalizedLabels()
    {
        NavOverviewText = _l10n.T("nav.overview");
        NavAccountsText = _l10n.T("nav.accounts");
        NavUsageText = _l10n.T("nav.usage");
        NavSettingsText = _l10n.T("nav.settings");
        NavAboutText = _l10n.T("nav.about");
        OpenAntigravityText = _l10n.T("sidebar.open_antigravity");
        if (string.IsNullOrEmpty(StatusMessage))
        {
            StatusMessage = _l10n.T("sidebar.waiting_service");
        }
    }

    partial void OnSelectedPageIndexChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 4);
        if (clamped != value)
        {
            SelectedPageIndex = clamped;
            return;
        }
        _config.Update(c => c.LastPage = clamped);
        UpdateCurrentPage();
        RefreshCurrentPageRequested?.Invoke();
    }

    private void UpdateCurrentPage()
    {
        if (_pages != null && SelectedPageIndex >= 0 && SelectedPageIndex < _pages.Count)
        {
            CurrentPageViewModel = _pages[SelectedPageIndex];
        }
    }

    [RelayCommand]
    public void NavigateTo(object? indexParam)
    {
        if (indexParam is int i)
        {
            SelectedPageIndex = Math.Clamp(i, 0, 4);
        }
        else if (int.TryParse(indexParam?.ToString(), out int parsed))
        {
            SelectedPageIndex = Math.Clamp(parsed, 0, 4);
        }
    }

    [RelayCommand]
    public void DismissBanner()
    {
        HasBanner = false;
        BannerMessage = string.Empty;
    }

    public void ShowBanner(string message)
    {
        BannerMessage = message;
        HasBanner = !string.IsNullOrWhiteSpace(message);
    }

    [RelayCommand]
    public async Task OpenAntigravityAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(OfficialExePath))
            {
                var info = await _bridge.SendAsync("platform_info").ConfigureAwait(false);
                OfficialExePath = info["executablePath"].AsString() ?? string.Empty;
                ProfileDir = info["profileDir"].AsString() ?? string.Empty;
            }
            var res = await _lifecycle.OpenAntigravityAsync(OfficialExePath, ProfileDir).ConfigureAwait(false);
            if (!res.Ok)
            {
                ShowBanner(res.Message);
            }
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message);
        }
    }

    [RelayCommand]
    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new JsonObject
            {
                ["includeTasks"] = true,
                ["includeAccounts"] = false,
            };
            if (_taskRevision >= 0)
            {
                payload["tasksSince"] = _taskRevision;
            }

            var res = await _bridge.SendAsync("status", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            var st = res["status"]?.AsObject();
            string nextMode = st?["mode"].AsString() ?? StatusMode;
            string nextMessage = st?["message"].AsString() ?? nextMode;
            long? nextRev = res["taskRevision"] != null ? res["taskRevision"].AsInt64(-1) : null;
            int nextRunning = (int)res["runningTasks"].AsInt64(0);
            UiDispatch.Run(() =>
            {
                if (st != null)
                {
                    StatusMode = nextMode;
                    StatusMessage = nextMessage;
                }
                if (nextRev.HasValue)
                {
                    _taskRevision = nextRev.Value;
                }
                RunningTasksCount = nextRunning;
            });
        }
        catch
        {
            UiDispatch.Run(() =>
            {
                StatusMode = "disconnected";
                StatusMessage = _l10n.T("status.disconnected");
            });
        }
    }

    [RelayCommand]
    public void RefreshCurrentPage()
    {
        _ = RefreshStatusAsync(_pollCts.Token);
        RefreshCurrentPageRequested?.Invoke();
    }

    [RelayCommand]
    public void RequestExit()
    {
        ExitApplicationRequested?.Invoke();
    }

    public void StartPolling()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await _bridge.SendAsync("platform_info", cancellationToken: _pollCts.Token).ConfigureAwait(false);
                OfficialExePath = info["executablePath"].AsString() ?? string.Empty;
                ProfileDir = info["profileDir"].AsString() ?? string.Empty;
            }
            catch { }

            while (!_pollCts.IsCancellationRequested)
            {
                await RefreshStatusAsync(_pollCts.Token).ConfigureAwait(false);
                await _lifecycle.CheckLifecycleSyncTickAsync(
                    OfficialExePath,
                    _config.Value.SyncAntigravityLifecycle,
                    _pollCts.Token).ConfigureAwait(false);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), _pollCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollCts.Cancel();
        _pollCts.Dispose();
    }
}
