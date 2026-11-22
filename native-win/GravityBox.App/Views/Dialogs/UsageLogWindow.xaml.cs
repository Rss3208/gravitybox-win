using System.Windows;
using GravityBox.App.ViewModels;

namespace GravityBox.App.Views.Dialogs;

public partial class UsageLogWindow : Window
{
    private readonly UsageViewModel _vm;

    public UsageLogWindow(UsageViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await _vm.LoadEventsPageAsync(0);
        Closed += async (_, _) => await _vm.ReleaseSnapshotAsync();
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        await _vm.LoadEventsPageAsync(0);
    }

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentPage > 0)
        {
            await _vm.LoadEventsPageAsync(_vm.CurrentPage - 1);
        }
    }

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        await _vm.LoadEventsPageAsync(_vm.CurrentPage + 1);
    }
}
