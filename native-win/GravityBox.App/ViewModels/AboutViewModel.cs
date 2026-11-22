using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GravityBox.App.Core;

namespace GravityBox.App.ViewModels;

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly ConfigurationStore _config;
    private readonly L10nService _l10n;
    private readonly AppUpdateManager _updateManager;

    [ObservableProperty]
    private string _versionText = "1.1.3";

    [ObservableProperty]
    private string _dataDirectory;

    [ObservableProperty]
    private bool _autoUpdateEnabled;

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _hasUpdateAvailable;

    public AboutViewModel(ConfigurationStore config, L10nService l10n, AppUpdateManager updateManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));

        _versionText = _updateManager.CurrentVersion;
        _dataDirectory = _config.StateDirectory;
        _autoUpdateEnabled = _config.Value.AutoUpdateEnabled;
        _updateStatusText = $"当前版本 {_versionText}";

        _updateManager.StateChanged += () =>
        {
            UpdateStatusText = _updateManager.StatusMessage;
            DownloadProgress = _updateManager.DownloadProgress;
            HasUpdateAvailable = _updateManager.Status == UpdateStatusKind.Available;
        };
    }

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        _config.Update(c => c.AutoUpdateEnabled = value);
    }

    [RelayCommand]
    public async Task CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        await _updateManager.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    public void RevealDataDirectory()
    {
        if (Directory.Exists(DataDirectory))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", DataDirectory) { UseShellExecute = true });
        }
    }
}
