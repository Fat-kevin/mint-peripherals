using DeviceCenterPreview.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceCenterPreview.App.Controls;

/// <summary>
/// 圆形电量仪表盘：原生 Path/Ellipse 弧线 + 中心大号读数。
/// 刷新只改 Path.Data 与文本，符合 15s 刷新稳定性规则。
/// </summary>
public sealed partial class BatteryRing : UserControl
{
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(BatteryRing),
            new PropertyMetadata(72.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty BatteryValueProperty =
        DependencyProperty.Register(nameof(BatteryValue), typeof(double), typeof(BatteryRing),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(BatteryRing),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty ChargingProperty =
        DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryRing),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double BatteryValue
    {
        get => (double)GetValue(BatteryValueProperty);
        set => SetValue(BatteryValueProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public bool Charging
    {
        get => (bool)GetValue(ChargingProperty);
        set => SetValue(ChargingProperty, value);
    }

    public BatteryRing()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisual();
        SizeChanged += (_, _) => UpdateVisual();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BatteryRing ring)
        {
            ring.UpdateVisual();
        }
    }

    private void UpdateVisual()
    {
        var size = Math.Max(16, Size);
        var stroke = Math.Max(3.0, size * 0.055);
        var radius = (size - stroke) / 2.0;
        var center = size / 2.0;

        RingRoot.Width = size;
        RingRoot.Height = size;

        Track.Width = size;
        Track.Height = size;
        Track.StrokeThickness = stroke;

        Arc.StrokeThickness = stroke;
        Arc.Stroke = RingBrush ?? AppTheme.GetBrush(this, "AppSuccessBrush");
        Arc.Data = BuildArc(center, radius, Math.Clamp(BatteryValue, 0, 100));

        Number.FontSize = Math.Max(12.0, size * 0.27);
        PercentSign.FontSize = Math.Max(8.0, size * 0.13);
        PercentSign.Margin = new Thickness(1, size * 0.06, 0, 0);

        var has = BatteryValue > 0;
        Number.Text = has ? $"{(int)Math.Round(BatteryValue)}" : "—";
        PercentSign.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        ChargeMark.FontSize = Math.Max(9.0, size * 0.14);
        ChargeMark.Margin = new Thickness(0, size * 0.62, 0, 0);
        ChargeMark.Visibility = Charging && has ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>12 点钟起点、顺时针、圆头端帽的弧线；100% 时用两段半圆闭合。</summary>
    private static Geometry BuildArc(double center, double radius, double percent)
    {
        if (percent <= 0)
        {
            return new PathGeometry();
        }

        var top = new Windows.Foundation.Point(center, center - radius);
        var figure = new PathFigure { StartPoint = top };

        if (percent >= 99.9)
        {
            var half = new Windows.Foundation.Point(center, center + radius);
            figure.Segments.Add(MakeSegment(half, radius, false));
            figure.Segments.Add(MakeSegment(top, radius, false));
        }
        else
        {
            var angle = 2.0 * Math.PI * percent / 100.0;
            var end = new Windows.Foundation.Point(
                center + radius * Math.Sin(angle),
                center - radius * Math.Cos(angle));
            figure.Segments.Add(MakeSegment(end, radius, angle > Math.PI));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static ArcSegment MakeSegment(Windows.Foundation.Point point, double radius, bool largeArc) => new()
    {
        Point = point,
        Size = new Windows.Foundation.Size(radius, radius),
        IsLargeArc = largeArc,
        SweepDirection = SweepDirection.Clockwise,
    };
}
