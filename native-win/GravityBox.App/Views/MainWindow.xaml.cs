using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GravityBox.App.Core;
using GravityBox.App.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GravityBox.App.Views;

public partial class MainWindow : Window
{
    private readonly MainShellViewModel _vm;
    public bool AllowExplicitClose { get; set; }

    public MainWindow(MainShellViewModel viewModel, ThemeService themeService)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _vm;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyWindowTitleBarTheme(this, themeService.CurrentTheme);
        };
        PreviewKeyDown += OnMainWindowPreviewKeyDown;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowExplicitClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.D1:
            case Key.NumPad1:
                _vm.NavigateTo(0);
                e.Handled = true;
                break;
            case Key.D2:
            case Key.NumPad2:
                _vm.NavigateTo(1);
                e.Handled = true;
                break;
            case Key.D3:
            case Key.NumPad3:
                _vm.NavigateTo(2);
                e.Handled = true;
                break;
            case Key.D4:
            case Key.NumPad4:
                _vm.NavigateTo(3);
                e.Handled = true;
                break;
            case Key.D5:
            case Key.NumPad5:
                _vm.NavigateTo(4);
                e.Handled = true;
                break;
            case Key.OemComma:
                _vm.NavigateTo(3);
                e.Handled = true;
                break;
            case Key.R:
                _vm.RefreshCurrentPage();
                e.Handled = true;
                break;
            case Key.Q:
                _vm.RequestExit();
                e.Handled = true;
                break;
        }
    }
}
