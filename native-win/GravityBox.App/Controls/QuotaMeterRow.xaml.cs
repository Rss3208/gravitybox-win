using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace GravityBox.App.Controls;

public partial class QuotaMeterRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(QuotaMeterRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
        nameof(Percentage), typeof(double), typeof(QuotaMeterRow), new PropertyMetadata(0.0, OnPercentageChanged));

    public static readonly DependencyProperty RemainingTextProperty = DependencyProperty.Register(
        nameof(RemainingText), typeof(string), typeof(QuotaMeterRow), new PropertyMetadata("0%"));

    public static readonly DependencyProperty ResetTextProperty = DependencyProperty.Register(
        nameof(ResetText), typeof(string), typeof(QuotaMeterRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(QuotaMeterRow),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public string RemainingText
    {
        get => (string)GetValue(RemainingTextProperty);
        set => SetValue(RemainingTextProperty, value);
    }

    public string ResetText
    {
        get => (string)GetValue(ResetTextProperty);
        set => SetValue(ResetTextProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public QuotaMeterRow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateFillWidth();
    }

    private static void OnPercentageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QuotaMeterRow row)
        {
            row.UpdateFillWidth();
        }
    }

    private void UpdateFillWidth()
    {
        double clamped = Math.Clamp(Percentage, 0.0, 100.0) / 100.0;
        FillBar.Width = Math.Max(0, ActualWidth * clamped);
    }
}
