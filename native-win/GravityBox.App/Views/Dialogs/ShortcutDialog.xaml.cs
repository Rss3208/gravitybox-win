using System.Windows;
using GravityBox.App.ViewModels;

namespace GravityBox.App.Views.Dialogs;

public partial class ShortcutDialog : Window
{
    public ShortcutDialog(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await vm.ScanShortcutsAsync();
    }
}
