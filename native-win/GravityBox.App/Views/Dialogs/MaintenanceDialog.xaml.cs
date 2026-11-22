using System.Windows;
using GravityBox.App.ViewModels;

namespace GravityBox.App.Views.Dialogs;

public partial class MaintenanceDialog : Window
{
    public MaintenanceDialog(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await vm.ScanMaintenanceAsync();
    }
}
