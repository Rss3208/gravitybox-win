using System;
using System.IO;
using System.Text;
using System.Windows;
using GravityBox.App.ViewModels;
using GravityBox.App.Views.Dialogs;
using Microsoft.Win32;
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

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UsageViewModel vm) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出用量统计 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"gravitybox-usage-{vm.ActivePeriodKey}-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            File.WriteAllText(dlg.FileName, vm.ExportCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }
}
