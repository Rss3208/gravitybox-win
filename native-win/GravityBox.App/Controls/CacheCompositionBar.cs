using System;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace GravityBox.App.Controls;

public sealed class CacheCompositionBar : FrameworkElement
{
    public static readonly DependencyProperty RuntimeBytesProperty = DependencyProperty.Register(
        nameof(RuntimeBytes), typeof(long), typeof(CacheCompositionBar),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LogsBytesProperty = DependencyProperty.Register(
        nameof(LogsBytes), typeof(long), typeof(CacheCompositionBar),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProtectedBytesProperty = DependencyProperty.Register(
        nameof(ProtectedBytes), typeof(long), typeof(CacheCompositionBar),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public long RuntimeBytes { get => (long)GetValue(RuntimeBytesProperty); set => SetValue(RuntimeBytesProperty, value); }
    public long LogsBytes { get => (long)GetValue(LogsBytesProperty); set => SetValue(LogsBytesProperty, value); }
    public long ProtectedBytes { get => (long)GetValue(ProtectedBytesProperty); set => SetValue(ProtectedBytesProperty, value); }

    public CacheCompositionBar()
    {
        Height = 10;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 2) return;

        double r = h / 2.0;
        var trackGeo = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        dc.PushClip(trackGeo);

        var trackBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        dc.DrawRectangle(trackBrush, null, new Rect(0, 0, w, h));

        double total = Math.Max(0, RuntimeBytes) + Math.Max(0, LogsBytes) + Math.Max(0, ProtectedBytes);
        if (total > 0)
        {
            double w1 = w * (Math.Max(0, RuntimeBytes) / total);
            double w2 = w * (Math.Max(0, LogsBytes) / total);
            double w3 = Math.Max(0, w - w1 - w2);

            if (w1 > 0) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), null, new Rect(0, 0, w1, h));
            if (w2 > 0) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), null, new Rect(w1, 0, w2, h));
            if (w3 > 0) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), null, new Rect(w1 + w2, 0, w3, h));
        }

        dc.Pop();
    }
}
