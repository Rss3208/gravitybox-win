using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace GravityBox.App.Controls;

public sealed record TrendPoint(string Label, double Value, string? TooltipText = null);

public sealed class UsageTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<TrendPoint>),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(Array.Empty<TrendPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73)), FrameworkPropertyMetadataOptions.AffectsRender));

    private int _hoverIndex = -1;

    public IReadOnlyList<TrendPoint> Points
    {
        get => (IReadOnlyList<TrendPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public UsageTrendChart()
    {
        MinHeight = 110;
        MouseMove += OnChartMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                InvalidateVisual();
            }
        };
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        var pts = Points;
        if (pts == null || pts.Count == 0 || ActualWidth <= 20)
        {
            return;
        }
        double x = e.GetPosition(this).X;
        double padLeft = 8;
        double usableW = Math.Max(1, ActualWidth - 16);
        double step = pts.Count > 1 ? usableW / (pts.Count - 1) : usableW;
        int idx = pts.Count == 1 ? 0 : (int)Math.Round((x - padLeft) / step);
        idx = Math.Clamp(idx, 0, pts.Count - 1);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 16 || h <= 24) return;

        var pts = Points;
        if (pts == null || pts.Count == 0)
        {
            return;
        }

        double padL = 8, padR = 8, padT = 12, padB = 22;
        double plotW = Math.Max(1, w - padL - padR);
        double plotH = Math.Max(1, h - padT - padB);
        double maxVal = Math.Max(1e-6, pts.Max(p => p.Value));

        var coords = new List<Point>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            double x = pts.Count == 1 ? padL + plotW / 2.0 : padL + (plotW * i / (pts.Count - 1));
            double y = padT + plotH * (1.0 - Math.Clamp(pts[i].Value / maxVal, 0.0, 1.0));
            coords.Add(new Point(x, y));
        }

        // Subtle baseline
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)), 1);
        dc.DrawLine(gridPen, new Point(padL, padT + plotH), new Point(padL + plotW, padT + plotH));

        if (coords.Count >= 2)
        {
            var lineGeo = new StreamGeometry();
            var areaGeo = new StreamGeometry();
            using (var lctx = lineGeo.Open())
            using (var actx = areaGeo.Open())
            {
                lctx.BeginFigure(coords[0], false, false);
                actx.BeginFigure(new Point(coords[0].X, padT + plotH), true, true);
                actx.LineTo(coords[0], false, false);

                for (int i = 1; i < coords.Count; i++)
                {
                    var prev = coords[i - 1];
                    var curr = coords[i];
                    double midX = (prev.X + curr.X) / 2.0;
                    var cp1 = new Point(midX, prev.Y);
                    var cp2 = new Point(midX, curr.Y);
                    lctx.BezierTo(cp1, cp2, curr, true, true);
                    actx.BezierTo(cp1, cp2, curr, true, true);
                }

                actx.LineTo(new Point(coords[^1].X, padT + plotH), false, false);
            }
            lineGeo.Freeze();
            areaGeo.Freeze();

            Color lineColor = (LineBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x25, 0x63, 0xEB);
            var fillBrush = new LinearGradientBrush(
                Color.FromArgb(64, lineColor.R, lineColor.G, lineColor.B),
                Color.FromArgb(0, lineColor.R, lineColor.G, lineColor.B),
                90);
            fillBrush.Freeze();
            dc.DrawGeometry(fillBrush, null, areaGeo);

            var pen = new Pen(LineBrush, 2.0) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, lineGeo);
        }
        else if (coords.Count == 1)
        {
            dc.DrawEllipse(LineBrush, null, coords[0], 3.5, 3.5);
        }

        // Hover crosshair & tooltip
        if (_hoverIndex >= 0 && _hoverIndex < coords.Count)
        {
            var pt = coords[_hoverIndex];
            var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)), 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(guidePen, new Point(pt.X, padT), new Point(pt.X, padT + plotH));
            dc.DrawEllipse(LineBrush, new Pen(Brushes.White, 1.5), pt, 4, 4);

            string tip = pts[_hoverIndex].TooltipText ?? $"{pts[_hoverIndex].Label}: {pts[_hoverIndex].Value:0.##}";
            var ft = new FormattedText(
                tip,
                CultureInfo.CurrentUICulture,
                WpfFlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Text"),
                11,
                LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double tx = Math.Clamp(pt.X - ft.Width / 2.0, padL, Math.Max(padL, w - padR - ft.Width));
            dc.DrawText(ft, new Point(tx, 0));
        }

        // First and last x-axis labels
        if (pts.Count > 0)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface("Segoe UI Variable Text");
            var firstFt = new FormattedText(pts[0].Label, CultureInfo.CurrentUICulture, WpfFlowDirection.LeftToRight, typeface, 10, LabelBrush, dpi);
            dc.DrawText(firstFt, new Point(padL, padT + plotH + 4));

            if (pts.Count > 1)
            {
                var lastFt = new FormattedText(pts[^1].Label, CultureInfo.CurrentUICulture, WpfFlowDirection.LeftToRight, typeface, 10, LabelBrush, dpi);
                dc.DrawText(lastFt, new Point(Math.Max(padL, w - padR - lastFt.Width), padT + plotH + 4));
            }
        }
    }
}
