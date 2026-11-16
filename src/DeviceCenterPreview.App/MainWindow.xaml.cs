using DeviceCenterPreview.App.Services;
using Microsoft.UI;
using DeviceCenterPreview.App.Views;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.UI;

namespace DeviceCenterPreview.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _notificationTimer = new();
    private bool _applyingTheme;
    private string _activeTag = "hub";

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
        SetTitleBar(DragRegion);
        AppTheme.ApplyTitleBar(this, Root);
        ApplyWallpaperTint();
        ContentFrame.Navigated += OnContentFrameNavigated;

        EngineVersionText.Text =
            $"v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} · SPP / HID++ / BLE";

        _notificationTimer.Interval = TimeSpan.FromSeconds(7);
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            AppInfoBar.IsOpen = false;
        };
        App.NotificationRaised += OnNotificationRaised;
        App.PeripheralService.Changed += OnServiceChanged;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            App.NotificationRaised -= OnNotificationRaised;
            App.PeripheralService.Changed -= OnServiceChanged;
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
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        try
        {
            // 预览版固定纯色：Mica 在本机会持续合成，纯色更省。
            SystemBackdrop = null;
        }
        catch
        {
            // 资源缺失时保持默认背景，保证窗口仍能打开。
        }

        Root.Loaded += (_, _) =>
        {
            RefreshSidebar();
            SetActive("hub");
        };

        if (ContentFrame.Navigate(typeof(DeviceCenterPage)))
        {
            ContentFrame.BackStack.Clear();
        }
        else
        {
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

    // ---------------------------------------------------------------- 设备栏

    private void OnServiceChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshSidebar);

    private void RefreshSidebar()
    {
        UpdateNav(HeadsetNavName, HeadsetNavStatus, HeadsetNavDot, HeadsetNavBattery, DeviceKind.Headset);
        UpdateNav(MouseNavName, MouseNavStatus, MouseNavDot, MouseNavBattery, DeviceKind.Mouse);
        UpdateNav(RemoteNavName, RemoteNavStatus, RemoteNavDot, RemoteNavBattery, DeviceKind.Remote);
    }

    private void UpdateNav(
        TextBlock name,
        TextBlock status,
        Microsoft.UI.Xaml.Shapes.Ellipse dot,
        TextBlock battery,
        DeviceKind kind)
    {
        var snapshot = App.PeripheralService.GetSnapshot(kind);
        name.Text = snapshot.DisplayName;
        status.Text = snapshot.IsConnected ? "已连接" : "未连接";
        dot.Fill = AppTheme.GetBrush(Root,
            snapshot.IsConnected ? "AppSuccessBrush" : "AppOfflineBrush");
        battery.Text = snapshot.BatteryPercent is int value ? $"{value}%" : "—";
    }

    private void DeviceNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    private void Overview_Click(object sender, RoutedEventArgs e) => NavigateTo("hub");

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateTo("settings");

    private void NavigateTo(string tag)
    {
        var page = tag switch
        {
            "hub" => typeof(DeviceCenterPage),
            "headset" => typeof(HeadsetPage),
            "mouse" => typeof(MousePage),
            "remote" => typeof(RemotePage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };

        if (page is null)
        {
            return;
        }

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
            ContentFrame.BackStack.Clear();
        }

        SetActive(tag);
    }

    private void SetActive(string tag)
    {
        _activeTag = tag;
        var selected = AppTheme.GetBrush(Root, "AppPressedBrush");
        var transparent = new SolidColorBrush(Colors.Transparent);

        StyleDevice(HeadsetNav, HeadsetNavName, tag == "headset", selected, transparent);
        StyleDevice(MouseNav, MouseNavName, tag == "mouse", selected, transparent);
        StyleDevice(RemoteNav, RemoteNavName, tag == "remote", selected, transparent);

        StyleFlat(OverviewNav, OverviewNavText, tag == "hub", selected, transparent);
        StyleFlat(SettingsNav, SettingsNavText, tag == "settings", selected, transparent);

        void StyleDevice(Button button, TextBlock name, bool isActive, Brush activeBrush, Brush idleBrush)
        {
            button.Background = isActive ? activeBrush : idleBrush;
            name.Foreground = AppTheme.GetBrush(Root,
                isActive ? "AppTextPrimaryBrush" : "AppTextPrimaryBrush");
            name.FontWeight = isActive
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.SemiBold;
        }

        void StyleFlat(Button button, TextBlock text, bool isActive, Brush activeBrush, Brush idleBrush)
        {
            button.Background = isActive ? activeBrush : idleBrush;
            text.Foreground = AppTheme.GetBrush(Root,
                isActive ? "AppTextPrimaryBrush" : "AppTextSecondaryBrush");
            text.FontWeight = isActive
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
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

        if (tag is not null)
        {
            SetActive(tag);
        }
    }

    // ---------------------------------------------------------------- 主题 / 通知

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
        RefreshSidebar();
        SetActive(_activeTag);
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
        RefreshSidebar();
        SetActive(_activeTag);
    }

    /// <summary>类 Mica 的静态壁纸染色：只在启动/主题切换/开关切换时重算一次。</summary>
    public void ApplyWallpaperTint() => _ = ApplyWallpaperTintAsync();

    private async Task ApplyWallpaperTintAsync()
    {
        try
        {
            Color? dominant = null;
            if (App.Settings.WallpaperTintEnabled)
            {
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

        // 关闭按钮只隐藏主窗口，保留托盘；真正退出走托盘菜单。
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
}


