using System.Windows;
using GravityBox.App.ViewModels;
using GravityBox.App.Views.Dialogs;
using UserControl = System.Windows.Controls.UserControl;

namespace GravityBox.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnLangZhClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) await vm.SetLanguageAsync("zh-Hans");
    }

    private async void OnLangSystemClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) await vm.SetLanguageAsync("system");
    }

    private async void OnLangEnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) await vm.SetLanguageAsync("en");
    }

    private async void OnHudToggleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) await vm.SetHudEnabledAsync(vm.HudEnabled);
    }

    private void OnLaunchAtLoginClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.SetLaunchAtLogin(vm.LaunchAtLogin);
    }

    private void OnLifecycleSyncClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.SetSyncAntigravityLifecycle(vm.SyncAntigravityLifecycle);
    }

    private void OnOpenShortcutsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new ShortcutDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void OnOpenPricingClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new PricingSyncDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void OnOpenMaintenanceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new MaintenanceDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
