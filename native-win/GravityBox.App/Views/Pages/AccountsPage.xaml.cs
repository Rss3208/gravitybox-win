using System.Windows;
using GravityBox.App.ViewModels;
using GravityBox.App.Views.Dialogs;
using Microsoft.Win32;
using UserControl = System.Windows.Controls.UserControl;

namespace GravityBox.App.Views.Pages;

public partial class AccountsPage : UserControl
{
    public AccountsPage()
    {
        InitializeComponent();
    }

    private void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm) return;
        var dlg = new AddAccountDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private async void OnImportJsonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的账号 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true && dlg.FileNames.Length > 0)
        {
            await vm.ImportJsonAsync(dlg.FileNames);
        }
    }

    private void OnPolicyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm) return;
        var dlg = new AccountPolicyDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
