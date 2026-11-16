using DeviceCenterPreview.App.Controls;
using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeviceCenterPreview.App.Views;

public sealed partial class DeviceCenterPage : Page, IThemeAware
{
    // 页面每次导航都会新建；图片解码对象必须跨页面复用，否则反复进入 Hub
    // 会不断 new BitmapImage，最终触发 XAML 内容树崩溃。
    private static readonly Dictionary<string, BitmapImage> ArtworkCache = new(StringComparer.OrdinalIgnoreCase);

    private string? _lastMouseArtwork;
    public DeviceCenterPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        Refresh();
        ResetScrollToTop();
    }

    private void ResetScrollToTop()
    {
        PageScroll.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() => PageScroll.ChangeView(null, 0, null, true));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed -= OnServiceChanged;
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var headset = App.PeripheralService.GetSnapshot(DeviceKind.Headset);
        HeadsetName.Text = headset.IsConnected ? headset.DisplayName : "耳机";
        SetStatus(HeadsetStatusChip, headset.IsConnected);
        HeadsetBattery.Text = FormatPercent(headset.BatteryPercent, headset.Charging);
        HeadsetDetail.Text = headset.Components.Count > 0
            ? string.Join(" · ", headset.Components.Select(c => $"{c.Label} {FormatPercent(c.Percent)}"))
            : headset.ControlHint ?? headset.StatusText;

        var mouse = App.PeripheralService.GetSnapshot(DeviceKind.Mouse);
        var mouseArtwork = DeviceArtwork.ForMouse(mouse.DisplayName);
        if (mouseArtwork != _lastMouseArtwork)
        {
            _lastMouseArtwork = mouseArtwork;
            if (mouseArtwork is not null)
            {
                if (!ArtworkCache.TryGetValue(mouseArtwork, out var image))
                {
                    image = new BitmapImage(new Uri($"ms-appx:///{mouseArtwork}"));
                    ArtworkCache[mouseArtwork] = image;
                }

                HubMouseImage.Source = image;
            }
        }

        HubMouseImage.Visibility = mouseArtwork is not null ? Visibility.Visible : Visibility.Collapsed;
        HubMouseIcon.Visibility = mouseArtwork is not null ? Visibility.Collapsed : Visibility.Visible;
        MouseName.Text = mouse.IsConnected ? mouse.DisplayName : "鼠标";
        SetStatus(MouseStatusChip, mouse.IsConnected);
        MouseBattery.Text = FormatPercent(mouse.BatteryPercent, mouse.Charging);
        var mouseMeta = mouse.StatusText.Replace("已连接", "").Trim(' ', '·');
        MouseDetail.Text = mouse.ControlsAvailable
            ? (string.IsNullOrWhiteSpace(mouseMeta) ? "控制通道已接入" : $"{mouseMeta} · 控制通道已接入")
            : mouse.ControlHint ?? mouse.StatusText;

        var remote = App.PeripheralService.GetSnapshot(DeviceKind.Remote);
        RemoteName.Text = remote.IsConnected ? remote.DisplayName : "遥控器";
        SetStatus(RemoteStatusChip, remote.IsConnected);
        RemoteBattery.Text = FormatPercent(remote.BatteryPercent, remote.Charging);
        var connected = (headset.IsConnected ? 1 : 0) + (mouse.IsConnected ? 1 : 0) + (remote.IsConnected ? 1 : 0);
        ConnectionSummary.Text = $"已连接 {connected} 台设备";
        var remoteMeta = remote.StatusText.Replace("已连接", "").Trim(' ', '·');
        RemoteDetail.Text = remote.ControlsAvailable
            ? (string.IsNullOrWhiteSpace(remoteMeta) ? "语音与按键通道已接入" : $"{remoteMeta} · 语音与按键通道已接入")
            : remote.ControlHint ?? remote.StatusText;
    }

    private static void SetStatus(StatusChip chip, bool connected)
    {
        chip.Kind = connected ? StatusChipKind.Connected : StatusChipKind.Offline;
        chip.Text = connected ? "已连接" : "离线";
    }

    public void OnAppThemeChanged() => Refresh();

    private static string FormatPercent(int? percent, ChargingState charging = ChargingState.Unknown)
    {
        if (percent is null)
        {
            return "—";
        }

        return charging == ChargingState.Charging ? $"{percent}% · 充电中" : $"{percent}%";
    }

    private void HeadsetCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(HeadsetPage));

    private void MouseCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(MousePage));

    private void RemoteCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(RemotePage));

    private async void RefreshMouse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.PeripheralService.RefreshMouseAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("mouse refresh from hub failed: " + ex.Message);
        }
    }

    private void Navigate(Type pageType)
    {
        Frame.Navigate(pageType);
        Frame.BackStack.Clear();
    }
}
