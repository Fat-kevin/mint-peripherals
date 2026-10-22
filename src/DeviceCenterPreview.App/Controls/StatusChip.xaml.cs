using DeviceCenterPreview.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceCenterPreview.App.Controls;

public enum StatusChipKind
{
    Live,
    Connected,
    Offline,
    Warning,
    Error,
    Saved,
}

/// <summary>
/// 极简状态标记：彩色圆点 + 同色文字，无底无框（用户否定胶囊形与描边）。
/// 主题画刷按控件当前主题解析，主题切换时同一实例可存活。
/// </summary>
public sealed partial class StatusChip : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusChip),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusChipKind), typeof(StatusChip),
        new PropertyMetadata(StatusChipKind.Connected, OnVisualPropertyChanged));

    public StatusChip()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => UpdateVisual();
        UpdateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppTheme.ThemeChanged -= OnThemeChanged;
        AppTheme.ThemeChanged += OnThemeChanged;
        UpdateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        AppTheme.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateVisual();

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusChipKind Kind
    {
        get => (StatusChipKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusChip)d).UpdateVisual();

    private void UpdateVisual()
    {
        if (ChipText is null)
        {
            return;
        }

        ChipText.Text = Text;
        var colorKey = Kind switch
        {
            StatusChipKind.Live => "AppIonBrush",
            StatusChipKind.Offline => "AppOfflineBrush",
            StatusChipKind.Warning => "AppWarningBrush",
            StatusChipKind.Error => "AppDangerBrush",
            StatusChipKind.Saved => "AppSuccessBrush",
            _ => "AppSuccessBrush",
        };

        var color = AppTheme.GetBrush(this, colorKey);
        ChipText.Foreground = color;
        Dot.Fill = color;
        DotOuter.Fill = color;
        DotOuter.Visibility = Kind == StatusChipKind.Live ? Visibility.Visible : Visibility.Collapsed;
    }
}
