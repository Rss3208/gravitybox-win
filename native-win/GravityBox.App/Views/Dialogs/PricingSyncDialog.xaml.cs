using System.Windows;
using GravityBox.App.ViewModels;

namespace GravityBox.App.Views.Dialogs;

public partial class PricingSyncDialog : Window
{
    public PricingSyncDialog(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
