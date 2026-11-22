using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GravityBox.App.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GravityBox.App.Views;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

public static class TrayFlyoutPositioner
{
    public const double EdgeMarginDip = 8.0;

    public static TaskbarEdge DetectTaskbarEdge(Rect monitorDip, Rect workDip)
    {
        double bottomDiff = Math.Max(0, monitorDip.Bottom - workDip.Bottom);
        double topDiff = Math.Max(0, workDip.Top - monitorDip.Top);
        double leftDiff = Math.Max(0, workDip.Left - monitorDip.Left);
        double rightDiff = Math.Max(0, monitorDip.Right - workDip.Right);

        double max = Math.Max(Math.Max(bottomDiff, topDiff), Math.Max(leftDiff, rightDiff));
        if (max <= 0.5) return TaskbarEdge.Bottom;
        if (Math.Abs(max - bottomDiff) < 0.5) return TaskbarEdge.Bottom;
        if (Math.Abs(max - topDiff) < 0.5) return TaskbarEdge.Top;
        if (Math.Abs(max - leftDiff) < 0.5) return TaskbarEdge.Left;
        return TaskbarEdge.Right;
    }

    public static System.Windows.Point CalculatePosition(
        Rect monitorDip,
        Rect workDip,
        Rect? anchorDip,
        double flyoutWidth,
        double flyoutHeight)
    {
        var edge = DetectTaskbarEdge(monitorDip, workDip);
        double anchorCenterX = anchorDip.HasValue
            ? anchorDip.Value.Left + anchorDip.Value.Width / 2.0
            : workDip.Right - flyoutWidth / 2.0 - EdgeMarginDip;
        double anchorCenterY = anchorDip.HasValue
            ? anchorDip.Value.Top + anchorDip.Value.Height / 2.0
            : workDip.Bottom - flyoutHeight / 2.0 - EdgeMarginDip;

        double minX = workDip.Left + EdgeMarginDip;
        double maxX = Math.Max(minX, workDip.Right - flyoutWidth - EdgeMarginDip);
        double minY = workDip.Top + EdgeMarginDip;
        double maxY = Math.Max(minY, workDip.Bottom - flyoutHeight - EdgeMarginDip);

        return edge switch
        {
            TaskbarEdge.Bottom => new System.Windows.Point(
                Math.Clamp(anchorCenterX - flyoutWidth / 2.0, minX, maxX),
                maxY),
            TaskbarEdge.Top => new System.Windows.Point(
                Math.Clamp(anchorCenterX - flyoutWidth / 2.0, minX, maxX),
                minY),
            TaskbarEdge.Left => new System.Windows.Point(
                minX,
                Math.Clamp(anchorCenterY - flyoutHeight / 2.0, minY, maxY)),
            TaskbarEdge.Right => new System.Windows.Point(
                maxX,
                Math.Clamp(anchorCenterY - flyoutHeight / 2.0, minY, maxY)),
            _ => new System.Windows.Point(maxX, maxY),
        };
    }
}

public partial class TrayFlyoutWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly TrayFlyoutViewModel _vm;

    public TrayFlyoutWindow(TrayFlyoutViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _vm;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            }
        };

        Deactivated += (_, _) => Hide();
        PreviewKeyDown += OnFlyoutPreviewKeyDown;
    }

    public void ShowNearCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        double dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;

        var monitorDip = new Rect(
            screen.Bounds.Left / dpiScale,
            screen.Bounds.Top / dpiScale,
            screen.Bounds.Width / dpiScale,
            screen.Bounds.Height / dpiScale);
        var workDip = new Rect(
            screen.WorkingArea.Left / dpiScale,
            screen.WorkingArea.Top / dpiScale,
            screen.WorkingArea.Width / dpiScale,
            screen.WorkingArea.Height / dpiScale);
        var anchorDip = new Rect(cursor.X / dpiScale - 12, cursor.Y / dpiScale - 12, 24, 24);

        UpdateLayout();
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 420;
        var pos = TrayFlyoutPositioner.CalculatePosition(monitorDip, workDip, anchorDip, w, h);

        Left = pos.X;
        Top = pos.Y;
        Show();
        Activate();
        _ = _vm.RefreshAsync();
    }

    private void OnFlyoutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D1:
                case Key.NumPad1:
                    _vm.SelectTab(0);
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    _vm.SelectTab(1);
                    e.Handled = true;
                    break;
                case Key.D3:
                case Key.NumPad3:
                    _vm.SelectTab(2);
                    e.Handled = true;
                    break;
                case Key.R:
                    _ = _vm.RefreshAsync();
                    e.Handled = true;
                    break;
            }
        }
    }
}
