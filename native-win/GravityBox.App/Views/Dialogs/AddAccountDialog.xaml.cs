using System;
using System.Windows;
using GravityBox.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace GravityBox.App.Views.Dialogs;

public partial class AddAccountDialog : Window
{
    private readonly AccountsViewModel _vm;

    public AddAccountDialog(AccountsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        if (_vm.HasDetectedLiveAccount)
        {
            SaveCurrentSubLabel.Text = $"检测到当前登录：{_vm.DetectedLiveEmail}";
        }
        ExportBtn.IsEnabled = _vm.HasAccounts;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnSaveCurrentClick(object sender, RoutedEventArgs e)
    {
        SaveCurrentBtn.IsEnabled = false;
        FeedbackLabel.Text = "正在读取当前 Antigravity 登录账号…";
        try
        {
            await _vm.SaveCurrentAccountAsync();
            if (!string.IsNullOrWhiteSpace(_vm.ErrorBanner))
            {
                FeedbackLabel.Text = _vm.ErrorBanner;
                return;
            }
            Close();
        }
        finally
        {
            SaveCurrentBtn.IsEnabled = true;
        }
    }

    private async void OnImportJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择账号 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0)
        {
            return;
        }
        FeedbackLabel.Text = "正在导入账号文件…";
        await _vm.ImportJsonAsync(dlg.FileNames);
        if (!string.IsNullOrWhiteSpace(_vm.ErrorBanner))
        {
            FeedbackLabel.Text = _vm.ErrorBanner;
            return;
        }
        Close();
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "导出文件将包含可用于刷新凭据的账号敏感信息，请勿上传至公开位置。确认继续导出？",
            "安全确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出账号备份",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"gravitybox-accounts-backup-{DateTime.Now:yyyyMMdd}.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog(this) != true) return;

        await _vm.ExportBackupAsync(dlg.FileName, confirmed: true);
        FeedbackLabel.Text = string.IsNullOrWhiteSpace(_vm.ErrorBanner) ? "备份已安全导出" : _vm.ErrorBanner;
    }

    private async void OnOAuthClick(object sender, RoutedEventArgs e)
    {
        OAuthBtn.IsEnabled = false;
        FeedbackLabel.Text = "正在启动 Google OAuth 登录会话…";
        try
        {
            var (ok, msg) = await _vm.LoginWithGoogleOAuthAsync(status => Dispatcher.Invoke(() => FeedbackLabel.Text = status));
            FeedbackLabel.Text = msg;
            if (ok) Close();
        }
        finally
        {
            OAuthBtn.IsEnabled = true;
        }
    }

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            FeedbackLabel.Text = "请输入有效的 Refresh Token";
            return;
        }
        FeedbackLabel.Text = "正在验证并保存 Refresh Token…";
        await _vm.AddAccountWithTokenAsync(token);
        if (!string.IsNullOrWhiteSpace(_vm.ErrorBanner))
        {
            FeedbackLabel.Text = _vm.ErrorBanner;
            return;
        }
        Close();
    }
}
