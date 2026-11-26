using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceCenterPreview.App.Views;

public sealed partial class DeviceCenterPage : Page, IThemeAware
{
    private const string KeyboardGlyph = "\uE765";
    private const string GamepadGlyph = "\uE7FC";
    private const string SpeakerGlyph = "\uE767";
    private const string HeadsetGlyph = "\uE7F6";
    private const string MouseGlyph = "\uE962";
    private const string RemoteGlyph = "\uE7FC";
    private const string BatteryGlyph = "\uE83F";

    private OtherRowViews[] _otherRows = [];

    public DeviceCenterPage()
    {
        InitializeComponent();
        _otherRows = CreateOtherRows();
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

        var otherDevices = GetOtherBatteryDevices(headset, mouse, remote);
        var online = (headset.IsConnected ? 1 : 0) + (mouse.IsConnected ? 1 : 0) +
                     (remote.IsConnected ? 1 : 0);
        HeaderSubtitle.Text = (online, otherDevices.Count) switch
        {
            (0, 0) => "没有设备在线 · 检查蓝牙配对或接收器",
            (0, _) => $"{otherDevices.Count} 台其他设备 · 数值全部来自真实链路",
            (_, 0) => $"{online} 台设备在线 · 数值全部来自真实链路",
            _ => $"{online} 台设备在线 · 另有 {otherDevices.Count} 台其他设备",
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

        UpdateOtherDeviceRows(otherDevices);
    }

    private OtherRowViews[] CreateOtherRows() =>
    [
        new(OtherRow0, OtherIcon0, OtherName0, OtherPercent0, OtherState0),
        new(OtherRow1, OtherIcon1, OtherName1, OtherPercent1, OtherState1),
        new(OtherRow2, OtherIcon2, OtherName2, OtherPercent2, OtherState2),
        new(OtherRow3, OtherIcon3, OtherName3, OtherPercent3, OtherState3),
        new(OtherRow4, OtherIcon4, OtherName4, OtherPercent4, OtherState4),
        new(OtherRow5, OtherIcon5, OtherName5, OtherPercent5, OtherState5),
    ];

    /// <summary>
    /// 从 GetBatteryDevices() 里剥掉三类受控设备，得到总览「其他设备」。
    /// GetBatteryDevices 前三条是受控设备（可能因未连接而缺席），所以按快照 DeviceId 匹配而不是切片。
    /// </summary>
    private static List<BatteryDeviceSnapshot> GetOtherBatteryDevices(
        PeripheralSnapshot headset,
        PeripheralSnapshot mouse,
        PeripheralSnapshot remote)
    {
        var controlled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddControlledId(controlled, headset);
        AddControlledId(controlled, mouse);
        AddControlledId(controlled, remote);

        var others = new List<BatteryDeviceSnapshot>();
        foreach (var device in App.PeripheralService.GetBatteryDevices())
        {
            if (!controlled.Contains(device.Id))
            {
                others.Add(device);
            }
        }

        return others;
    }

    private static void AddControlledId(HashSet<string> target, PeripheralSnapshot snapshot)
    {
        if (snapshot.IsConnected && !string.IsNullOrWhiteSpace(snapshot.DeviceId))
        {
            target.Add(snapshot.DeviceId);
        }
    }

    /// <summary>固定 6 个预置槽位：只改 Text/Visibility/Foreground，不重建 ItemsSource 或控件。</summary>
    private void UpdateOtherDeviceRows(IReadOnlyList<BatteryDeviceSnapshot> devices)
    {
        OtherDevicesSection.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (devices.Count > _otherRows.Length)
        {
            OtherDevicesCount.Text = $"{devices.Count} 台 · 还有 {devices.Count - _otherRows.Length} 台未显示";
        }
        else
        {
            OtherDevicesCount.Text = devices.Count > 0 ? $"{devices.Count} 台" : "";
        }

        for (int index = 0; index < _otherRows.Length; index++)
        {
            var row = _otherRows[index];
            if (index >= devices.Count)
            {
                row.Root.Visibility = Visibility.Collapsed;
                continue;
            }

            var device = devices[index];
            row.Root.Visibility = Visibility.Visible;
            row.Icon.Glyph = GlyphFor(device.Kind);
            row.Icon.Foreground = IconBrush(device);
            row.Name.Text = device.DisplayName;
            row.Percent.Text = device.Percent is int percent ? $"{percent}%" : "—";
            row.Percent.Foreground = PercentBrush(device);
            row.State.Text = StateText(device);
            row.State.Foreground = StateBrush(device);
        }
    }

    private static string GlyphFor(BatteryDeviceKind kind) => kind switch
    {
        BatteryDeviceKind.Keyboard => KeyboardGlyph,
        BatteryDeviceKind.Gamepad => GamepadGlyph,
        BatteryDeviceKind.Speaker => SpeakerGlyph,
        BatteryDeviceKind.Headset => HeadsetGlyph,
        BatteryDeviceKind.Mouse => MouseGlyph,
        BatteryDeviceKind.Remote => RemoteGlyph,
        _ => BatteryGlyph,
    };

    private static string StateText(BatteryDeviceSnapshot device)
    {
        if (device.IsOffline)
        {
            return "离线";
        }

        if (device.IsAsleep)
        {
            return "休眠 · 上次读数";
        }

        return device.Charging switch
        {
            ChargingState.Charging => "充电中",
            ChargingState.Full => "已充满",
            ChargingState.Discharging => "放电中",
            _ => device.IsConnected ? "在线" : "未连接",
        };
    }

    private Brush IconBrush(BatteryDeviceSnapshot device)
    {
        if (device.IsOffline || device.IsAsleep)
        {
            return AppTheme.GetBrush(this, "AppOfflineBrush");
        }

        return device.Kind switch
        {
            BatteryDeviceKind.Headset => AppTheme.GetBrush(this, "AppHeadsetBrush"),
            BatteryDeviceKind.Mouse => AppTheme.GetBrush(this, "AppMouseBrush"),
            BatteryDeviceKind.Remote => AppTheme.GetBrush(this, "AppRemoteBrush"),
            _ => AppTheme.GetBrush(this, "AppTextTertiaryBrush"),
        };
    }

    private Brush PercentBrush(BatteryDeviceSnapshot device)
    {
        if (device.IsOffline || device.IsAsleep)
        {
            return AppTheme.GetBrush(this, "AppTextTertiaryBrush");
        }

        // 低电 = 颜色 + 文案双编码（状态列写"在线/放电中"），不单靠颜色。
        if (device.Percent is int percent && percent <= 15 &&
            device.Charging is not (ChargingState.Charging or ChargingState.Full))
        {
            return AppTheme.GetBrush(this, "AppDangerBrush");
        }

        return AppTheme.GetBrush(this, "AppTextPrimaryBrush");
    }

    private Brush StateBrush(BatteryDeviceSnapshot device)
    {
        if (device.IsOffline)
        {
            return AppTheme.GetBrush(this, "AppOfflineBrush");
        }

        if (device.IsAsleep)
        {
            return AppTheme.GetBrush(this, "AppTextTertiaryBrush");
        }

        return device.Charging is ChargingState.Charging or ChargingState.Full
            ? AppTheme.GetBrush(this, "AppSuccessBrush")
            : AppTheme.GetBrush(this, "AppTextSecondaryBrush");
    }

    private sealed record OtherRowViews(
        Border Root,
        FontIcon Icon,
        TextBlock Name,
        TextBlock Percent,
        TextBlock State);

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

