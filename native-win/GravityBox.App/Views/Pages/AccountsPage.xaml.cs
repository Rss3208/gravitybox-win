using System.Windows;
using GravityBox.App.ViewModels;
using GravityBox.App.Views.Dialogs;
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

    private void OnPolicyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm) return;
        var dlg = new AccountPolicyDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
