using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceCenterPreview.App.Views;

public sealed partial class DeviceCenterPage : Page, IThemeAware
{
    public DeviceCenterPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed -= OnServiceChanged;
    }

    private void OnServiceChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    public void OnAppThemeChanged() => Refresh();

    private void Refresh()
    {
        var headset = App.PeripheralService.GetSnapshot(DeviceKind.Headset);
        var mouse = App.PeripheralService.GetSnapshot(DeviceKind.Mouse);
        var remote = App.PeripheralService.GetSnapshot(DeviceKind.Remote);

        var online = (headset.IsConnected ? 1 : 0) + (mouse.IsConnected ? 1 : 0) +
                     (remote.IsConnected ? 1 : 0);
        HeaderSubtitle.Text = online switch
        {
            0 => "没有设备在线 · 检查蓝牙配对或接收器",
            _ => $"{online} 台设备在线 · 数值全部来自真实链路",
        };

        // 耳机：三分量电量
        HeadsetName.Text = headset.DisplayName;
        HeadsetMeta.Text = headset.Firmware is { Length: > 0 } hf ? $"SPP · 固件 {hf}" : "SPP 音频通道";
        HeadsetStatus.Text = headset.IsConnected ? "已连接" : "未连接";
        HeadsetDot.Fill = StatusBrush(headset.IsConnected);
        HeadsetFact.Text = headset.ControlsAvailable
            ? "降噪、EQ 与快捷开关可写"
            : "控制通道未接入";
        SetComponent(headset, "left", LeftFill, LeftRest, LeftPercent);
        SetComponent(headset, "right", RightFill, RightRest, RightPercent);
        SetComponent(headset, "case", CaseFill, CaseRest, CasePercent);

        // 鼠标
        MouseName.Text = mouse.DisplayName;
        MouseMeta.Text = mouse.Firmware is { Length: > 0 } mf ? $"HID++ · 固件 {mf}" : "HID++ 指针设备";
        MouseStatus.Text = mouse.IsConnected ? "已连接" : "未连接";
        MouseDot.Fill = StatusBrush(mouse.IsConnected);
        MouseFact.Text = mouse.ControlsAvailable
            ? "DPI 与按键可读写"
            : mouse.ControlHint ?? mouse.StatusText;
        SetBattery(mouse, MouseBattery, MouseBatterySuffix, MouseFill, MouseRest);

        // 遥控器
        RemoteName.Text = remote.DisplayName;
        RemoteMeta.Text = remote.Firmware is { Length: > 0 } rf ? $"BLE · 固件 {rf}" : "BLE 语音与按键";
        RemoteStatus.Text = remote.IsConnected ? "已连接" : "未连接";
        RemoteDot.Fill = StatusBrush(remote.IsConnected);
        RemoteFact.Text = remote.ControlsAvailable
            ? "按键映射与语音可写"
            : remote.ControlHint ?? remote.StatusText;
        SetBattery(remote, RemoteBattery, RemoteBatterySuffix, RemoteFill, RemoteRest);
    }

    private void SetComponent(
        PeripheralSnapshot snapshot,
        string key,
        ColumnDefinition fill,
        ColumnDefinition rest,
        TextBlock percent)
    {
        var component = snapshot.Components.FirstOrDefault(item => item.Key == key);
        var value = component?.Percent;
        percent.Text = value is int pct ? $"{pct}%" : "—";
        SetBar(fill, rest, value);
    }

    private static void SetBattery(
        PeripheralSnapshot snapshot,
        TextBlock number,
        TextBlock suffix,
        ColumnDefinition fill,
        ColumnDefinition rest)
    {
        var value = snapshot.BatteryPercent;
        number.Text = value is int pct ? pct.ToString() : "—";
        suffix.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        SetBar(fill, rest, value);
    }

    private static void SetBar(ColumnDefinition fill, ColumnDefinition rest, int? value)
    {
        if (value is int pct)
        {
            fill.Width = new GridLength(Math.Clamp(pct, 0, 100), GridUnitType.Star);
            rest.Width = new GridLength(Math.Clamp(100 - pct, 0, 100), GridUnitType.Star);
            return;
        }

        fill.Width = new GridLength(0, GridUnitType.Star);
        rest.Width = new GridLength(1, GridUnitType.Star);
    }

    private Microsoft.UI.Xaml.Media.Brush StatusBrush(bool connected) =>
        AppTheme.GetBrush(this, connected ? "AppSuccessBrush" : "AppOfflineBrush");

    private void Column_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void Headset_Click(object sender, RoutedEventArgs e) => Navigate("headset");

    private void Mouse_Click(object sender, RoutedEventArgs e) => Navigate("mouse");

    private void Remote_Click(object sender, RoutedEventArgs e) => Navigate("remote");

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Navigate("settings");

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.PeripheralService.RefreshMouseAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Warn("mouse refresh from hub failed: " + ex.Message);
        }
    }

    private void Navigate(string tag)
    {
        var page = tag switch
        {
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

        Frame.Navigate(page);
        Frame.BackStack.Clear();
    }
}

