using System.Windows;
using GravityBox.App.ViewModels;

namespace GravityBox.App.Views.Dialogs;

public partial class AddAccountDialog : Window
{
    private readonly AccountsViewModel _vm;

    public AddAccountDialog(AccountsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(token))
        {
            await _vm.AddAccountWithTokenAsync(token);
        }
        Close();
    }
}
