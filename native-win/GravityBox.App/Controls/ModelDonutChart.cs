using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace GravityBox.App.Controls;

public sealed record DonutSlice(string Label, double Value, Color Color);

public sealed class ModelDonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices),
        typeof(IReadOnlyList<DonutSlice>),
        typeof(ModelDonutChart),
        new FrameworkPropertyMetadata(Array.Empty<DonutSlice>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(ModelDonutChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE2, 0xE4, 0xEC)), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DonutSlice> Slices
    {
        get => (IReadOnlyList<DonutSlice>)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public ModelDonutChart()
    {
        Width = 96;
        Height = 96;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 16) return;

        double thickness = Math.Max(8, size * 0.16);
        double radius = (size - thickness) / 2.0;
        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);

        var trackPen = new Pen(TrackBrush, thickness);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var slices = Slices;
        if (slices == null || slices.Count == 0) return;

        double total = slices.Sum(s => Math.Max(0, s.Value));
        if (total <= 0) return;

        double startDeg = -90.0;
        foreach (var slice in slices)
        {
            if (slice.Value <= 0) continue;
            double sweepDeg = 360.0 * (slice.Value / total);
            if (sweepDeg >= 359.9)
            {
                var fullPen = new Pen(new SolidColorBrush(slice.Color), thickness);
                fullPen.Freeze();
                dc.DrawEllipse(null, fullPen, center, radius, radius);
                break;
            }

            double startRad = startDeg * Math.PI / 180.0;
            double endRad = (startDeg + sweepDeg) * Math.PI / 180.0;
            var p1 = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
            var p2 = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p1, false, false);
                ctx.ArcTo(p2, new Size(radius, radius), 0, sweepDeg > 180.0, SweepDirection.Clockwise, true, true);
            }
            geo.Freeze();

            var pen = new Pen(new SolidColorBrush(slice.Color), thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
            startDeg += sweepDeg;
        }
    }
}
