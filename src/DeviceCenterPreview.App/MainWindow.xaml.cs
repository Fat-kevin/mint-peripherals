using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Linq;
using Windows.Graphics;
using Windows.UI;

namespace DeviceCenterPreview.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _notificationTimer = new();
    private bool _syncingNav;
    private bool _applyingTheme;

    public MainWindow()
    {
        // 在 XAML 树创建前设置一次初始尺寸，避免运行中 resize。
        try
        {
            AppWindow.Resize(new SizeInt32(1280, 840));
        }
        catch (Exception ex)
        {
            AppLog.Warn("initial resize failed: " + ex.Message);
        }

        InitializeComponent();
        Root.RequestedTheme = AppTheme.Resolve(App.Settings.ThemeMode);
        AppTheme.SyncFrom(Root);
        Root.ActualThemeChanged += OnActualThemeChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTheme.ApplyTitleBar(this, Root);
        ApplyWallpaperTint();
        ContentFrame.Navigated += OnContentFrameNavigated;

        _notificationTimer.Interval = TimeSpan.FromSeconds(7);
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            AppInfoBar.IsOpen = false;
        };
        App.NotificationRaised += OnNotificationRaised;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            App.NotificationRaised -= OnNotificationRaised;
            _notificationTimer.Stop();
        };

        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "PeripheralHub.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch
        {
            // 自定义图标失败时保留系统默认图标。
        }

        // 尺寸只在 InitializeComponent 之前设置一次，之后不再 resize。
        // 之前把 Resize 放在 InitializeComponent 之后，运行约 8 分钟后
        // combase 0x80070578 原生崩溃并黑屏；前置后 10+ 分钟稳定。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        try
        {
            // 预览版固定纯色：Mica 在本机（NVIDIA + 虚拟显示/WARP 回退）空闲时仍持续合成，
            // 实测每 2 秒约 700-800ms CPU；纯色为 0-16ms。正式版再按设备能力做可选材质。
            SystemBackdrop = null;
            // 背景由 XAML 的 AppCanvasBrush 决定，避免被系统默认底色覆盖。
        }
        catch
        {
            // 资源缺失时保持默认背景，保证窗口仍能打开。
        }

        if (ContentFrame.Navigate(typeof(DeviceCenterPage)))
        {
            ContentFrame.BackStack.Clear();
        }
        else
        {
            // 构造阶段偶发导航失败会让窗口只剩标题栏/黑底；等内容树加载完成后再补一次。
            AppLog.Warn("initial navigation failed; retry after Root.Loaded");
            Root.Loaded += (_, _) =>
            {
                if (ContentFrame.CurrentSourcePageType is null &&
                    ContentFrame.Navigate(typeof(DeviceCenterPage)))
                {
                    ContentFrame.BackStack.Clear();
                }
            };
        }
    }

    public void ApplyTheme(string preference)
    {
        var theme = AppTheme.Resolve(preference);
        _applyingTheme = true;
        try
        {
            if (Root.RequestedTheme != theme)
            {
                Root.RequestedTheme = theme;
            }
        }
        finally
        {
            _applyingTheme = false;
        }

        AppTheme.SyncFrom(Root);
        AppTheme.ApplyTitleBar(this, Root);
        ApplyWallpaperTint();
        NotifyThemeChanged();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_applyingTheme)
        {
            return;
        }

        AppTheme.SyncFrom(Root);
        AppTheme.ApplyTitleBar(this, Root);
        ApplyWallpaperTint();
        NotifyThemeChanged();
    }

    /// <summary>
    /// 类 Mica 的静态壁纸染色：设置开关、启动、主题切换时重算一次。
    /// 只改主题字典里共享 Brush 的 Color，无控件重建、无持续合成。
    /// </summary>
    public void ApplyWallpaperTint() => _ = ApplyWallpaperTintAsync();

    private async Task ApplyWallpaperTintAsync()
    {
        try
        {
            Color? dominant = null;
            if (App.Settings.WallpaperTintEnabled)
            {
                // 解码/量化放线程池；结果在服务内缓存，主题切换时瞬时返回。
                dominant = await Task.Run(WallpaperTintService.GetDominantColor);
            }

            DispatcherQueue.TryEnqueue(() => ApplyTintToBrushes(dominant));
        }
        catch (Exception ex)
        {
            AppLog.Warn("wallpaper tint apply failed: " + ex.Message);
        }
    }

    private void ApplyTintToBrushes(Color? dominant)
    {
        try
        {
            // ThemeResource 解析出的 SolidColorBrush 是主题字典里的同一共享实例，
            // 直接改实例 Color 即可让所有引用处（Root + 内层画布）一起变。
            var light = Root.ActualTheme == ElementTheme.Light;
            var canvasBase = light ? WallpaperTintService.CanvasLight : WallpaperTintService.CanvasDark;
            var ratio = light ? WallpaperTintService.CanvasRatioLight : WallpaperTintService.CanvasRatioDark;
            if (Root.Background is SolidColorBrush canvas)
            {
                canvas.Color = dominant is { } wallpaper
                    ? WallpaperTintService.Mix(canvasBase, wallpaper, ratio)
                    : canvasBase;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("wallpaper tint brush update failed: " + ex.Message);
        }
    }

    private void NotifyThemeChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ContentFrame.Content is IThemeAware aware)
            {
                aware.OnAppThemeChanged();
            }
        });
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.IsExiting)
        {
            return;
        }

        // 用户点关闭按钮/Alt+F4 时只隐藏主窗口，保留托盘图标和任务栏胶囊；
        // 真正退出只走托盘菜单的“退出”。
        args.Cancel = true;
        App.HideMainWindow();
    }

    private void OnNotificationRaised(object? sender, AppNotification notification)
    {
        AppInfoBar.Title = notification.Title;
        AppInfoBar.Message = notification.Message;
        AppInfoBar.Severity = notification.Severity switch
        {
            NotificationSeverity.Success => InfoBarSeverity.Success,
            NotificationSeverity.Warning => InfoBarSeverity.Warning,
            NotificationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        AppInfoBar.IsOpen = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        var tag = e.SourcePageType switch
        {
            Type t when t == typeof(DeviceCenterPage) => "hub",
            Type t when t == typeof(HeadsetPage) => "headset",
            Type t when t == typeof(MousePage) => "mouse",
            Type t when t == typeof(RemotePage) => "remote",
            Type t when t == typeof(SettingsPage) => "settings",
            _ => null,
        };
        if (tag is null)
        {
            return;
        }

        var item = Nav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => candidate.Tag is string candidateTag && candidateTag == tag);
        if (item is null || ReferenceEquals(Nav.SelectedItem, item))
        {
            return;
        }

        // 内容帧可能由设备中心卡片直接导航；这里只同步选中项，避免再次 Navigate。
        _syncingNav = true;
        try
        {
            Nav.SelectedItem = item;
        }
        finally
        {
            _syncingNav = false;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNav)
        {
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        Type? page = tag switch
        {
            "hub" => typeof(DeviceCenterPage),
            "headset" => typeof(HeadsetPage),
            "mouse" => typeof(MousePage),
            "remote" => typeof(RemotePage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };

        if (page is not null && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
            ContentFrame.BackStack.Clear();
        }
    }
}
