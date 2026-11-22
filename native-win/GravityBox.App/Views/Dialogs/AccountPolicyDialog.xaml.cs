using System.Windows;
using GravityBox.App.ViewModels;

namespace GravityBox.App.Views.Dialogs;

public partial class AccountPolicyDialog : Window
{
    private readonly AccountsViewModel _vm;

    public AccountPolicyDialog(AccountsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        EnableSwitch.IsChecked = vm.AutoSwitchEnabled;
        ReserveBox.Text = vm.ReservePercentage.ToString();
        MinCandidateBox.Text = vm.CandidateMinPercentage.ToString();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        int reserve = int.TryParse(ReserveBox.Text, out var r) ? r : _vm.ReservePercentage;
        int minCand = int.TryParse(MinCandidateBox.Text, out var m) ? m : _vm.CandidateMinPercentage;
        await _vm.SavePolicyAsync(EnableSwitch.IsChecked == true, _vm.AutoSwitchStrategy, reserve, minCand);
        Close();
    }
}
