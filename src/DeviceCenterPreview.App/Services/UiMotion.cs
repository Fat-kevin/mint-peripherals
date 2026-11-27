using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 全应用统一动效。规则：
/// - 只动 Opacity / RenderTransform（CompositeTransform），不触发布局；
/// - 入场用短阶梯 + ExponentialEase(EaseOut)，状态切换用 CubicEase(EaseOut)；
/// - 系统关闭动画时直接到终态；
/// - 页面只在首次 Loaded 播放入场，切页/刷新不重复。
/// </summary>
internal static class UiMotion
{
    public const int MicroMs = 120;
    public const int SmallMs = 180;
    public const int MediumMs = 280;
    public const int LargeMs = 520;
    public const int CountMs = 760;

    public static bool Enabled
    {
        get
        {
            try
            {
                return new UISettings().AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>页面入场：顶层分区按 52ms 阶梯落位，父级不整体淡入，避免“整页糊出来”。</summary>
    public static void PlayEntrance(Panel panel, double distance = 14, int staggerMs = 52, int durationMs = LargeMs)
    {
        if (!Enabled)
        {
            return;
        }

        var index = 0;
        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement element || element.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            PlayElementIn(element, distance, durationMs, index * staggerMs, withScale: true);
            index++;
        }
    }

    /// <summary>单个分区淡入 + 上移；用于工作区切换、详情块出现。</summary>
    public static void PlayElementIn(FrameworkElement element, double distance = 10, int durationMs = MediumMs, int delayMs = 0, bool withScale = false)
    {
        if (!Enabled || element.Visibility == Visibility.Collapsed)
        {
            return;
        }

        var transform = element.RenderTransform as CompositeTransform;
        if (transform is null)
        {
            transform = new CompositeTransform();
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        element.Opacity = 0;
        transform.TranslateY = distance;
        if (withScale)
        {
            transform.ScaleX = 0.985;
            transform.ScaleY = 0.985;
        }

        var storyboard = new Storyboard();
        Add(storyboard, element, "Opacity", 0, 1, durationMs, delayMs, new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 });
        Add(storyboard, transform, "TranslateY", distance, 0, durationMs, delayMs, new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 });
        if (withScale)
        {
            Add(storyboard, transform, "ScaleX", 0.985, 1, durationMs, delayMs, new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 });
            Add(storyboard, transform, "ScaleY", 0.985, 1, durationMs, delayMs, new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 });
        }

        storyboard.Begin();
    }

    /// <summary>把当前元素短暂放大再做一次带轻微过冲的回弹，用于“状态点/选中”反馈。</summary>
    public static void Pop(FrameworkElement element, double scale = 1.08, int durationMs = 220)
    {
        if (!Enabled)
        {
            return;
        }

        var transform = element.RenderTransform as CompositeTransform;
        if (transform is null)
        {
            transform = new CompositeTransform();
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var storyboard = new Storyboard();
        Add(storyboard, transform, "ScaleX", transform.ScaleX, scale, durationMs / 2, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });
        Add(storyboard, transform, "ScaleY", transform.ScaleY, scale, durationMs / 2, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });
        Add(storyboard, transform, "ScaleX", scale, 1, durationMs / 2, durationMs / 2, new CubicEase { EasingMode = EasingMode.EaseOut });
        Add(storyboard, transform, "ScaleY", scale, 1, durationMs / 2, durationMs / 2, new CubicEase { EasingMode = EasingMode.EaseOut });
        storyboard.Begin();
    }

    /// <summary>动画 SolidColorBrush.Color；用于图标栏选中底色，不重建按钮。</summary>
    public static void AnimateColor(SolidColorBrush brush, Windows.UI.Color color, int durationMs = MediumMs)
    {
        if (!Enabled)
        {
            brush.Color = color;
            return;
        }

        var storyboard = new Storyboard();
        var animation = new ColorAnimation
        {
            To = color,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, brush);
        Storyboard.SetTargetProperty(animation, "Color");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>把 Transform.Y 从当前值动画到目标值；用于图标栏选中条。</summary>
    public static void AnimateY(TranslateTransform transform, double target, int durationMs = MediumMs, int delayMs = 0)
    {
        if (!Enabled)
        {
            transform.Y = target;
            return;
        }

        var storyboard = new Storyboard();
        Add(storyboard, transform, "Y", transform.Y, target, durationMs, delayMs, new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 });
        storyboard.Begin();
    }

    private static void Add(Storyboard storyboard, DependencyObject target, string property, double from, double to, int durationMs, int delayMs, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
