using System.Windows;
using GravityBox.App.ViewModels;
using GravityBox.App.Views.Dialogs;
using UserControl = System.Windows.Controls.UserControl;

namespace GravityBox.App.Views.Pages;

public partial class UsagePage : UserControl
{
    public UsagePage()
    {
        InitializeComponent();
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UsageViewModel vm) return;
        var win = new UsageLogWindow(vm) { Owner = Window.GetWindow(this) };
        win.Show();
    }
}
