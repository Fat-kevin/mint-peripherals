using System.Diagnostics;
using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

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

    private static readonly TimeSpan EntranceStep = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(620);
    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(260);

    private OtherRowViews[] _otherRows = [];
    private readonly List<DispatcherQueueTimer> _numberTimers = [];
    private bool _entrancePlayed;
    private bool? _lastHeadsetConnected;
    private bool? _lastMouseConnected;
    private bool? _lastRemoteConnected;
    private bool _entranceDone = true;

    public DeviceCenterPage()
    {
        InitializeComponent();
        _otherRows = CreateOtherRows();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        foreach (var card in Cards())
        {
            card.RenderTransform = new TranslateTransform();
            card.PointerEntered += Card_PointerEntered;
            card.PointerExited += Card_PointerExited;
        }
    }

    private IEnumerable<Button> Cards() => [HeadsetColumn, MouseColumn, RemoteColumn];

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        Refresh();
        PlayEntrance();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed -= OnServiceChanged;
        StopNumberTimers();
        _entrancePlayed = false;
        _entranceDone = true;
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
        HeaderDot.Fill = StatusBrush(online > 0);
        HeaderSubtitle.Text = (online, otherDevices.Count) switch
        {
            (0, 0) => "没有设备在线 · 检查蓝牙配对或接收器",
            (0, _) => $"{otherDevices.Count} 台其他设备 · 数值全部来自真实链路",
            (3, 0) => "三台在线 · 数值全部来自真实链路",
            (_, 0) => $"{online} 台在线 · 数值全部来自真实链路",
            _ => $"{online} 台在线 · 另有 {otherDevices.Count} 台其他设备",
        };

        // 耳机
        HeadsetName.Text = headset.DisplayName;
        HeadsetMeta.Text = headset.Firmware is { Length: > 0 } hf ? $"SPP · 固件 {hf}" : "SPP 音频通道";
        HeadsetDot.Fill = StatusBrush(headset.IsConnected);
        if (_lastHeadsetConnected is bool lastHeadset && lastHeadset != headset.IsConnected)
        {
            UiMotion.Pop(HeadsetDot, 1.6, 260);
        }
        _lastHeadsetConnected = headset.IsConnected;
        HeadsetFact.Text = headset.ControlsAvailable ? "降噪 / EQ 可写" : "控制通道未接入";
        SetBattery(headset, HeadsetBattery, HeadsetBatterySuffix, HeadsetLeftText, HeadsetRightText, HeadsetCaseText);

        // 鼠标
        MouseName.Text = mouse.DisplayName;
        MouseMeta.Text = mouse.Firmware is { Length: > 0 } mf ? $"HID++ · 固件 {mf}" : "HID++ 指针设备";
        MouseDot.Fill = StatusBrush(mouse.IsConnected);
        if (_lastMouseConnected is bool lastMouse && lastMouse != mouse.IsConnected)
        {
            UiMotion.Pop(MouseDot, 1.6, 260);
        }
        _lastMouseConnected = mouse.IsConnected;
        MouseFact.Text = mouse.ControlsAvailable
            ? "板载配置可写"
            : Shorten(mouse.ControlHint ?? mouse.StatusText);
        SetBattery(mouse, MouseBattery, MouseBatterySuffix, null, null, null);

        // 遥控器
        RemoteName.Text = remote.DisplayName;
        RemoteMeta.Text = remote.Firmware is { Length: > 0 } rf ? $"BLE · 固件 {rf}" : "BLE 语音与按键";
        RemoteDot.Fill = StatusBrush(remote.IsConnected);
        if (_lastRemoteConnected is bool lastRemote && lastRemote != remote.IsConnected)
        {
            UiMotion.Pop(RemoteDot, 1.6, 260);
        }
        _lastRemoteConnected = remote.IsConnected;
        RemoteFact.Text = remote.ControlsAvailable
            ? "语音通道就绪"
            : Shorten(remote.ControlHint ?? remote.StatusText);
        SetBattery(remote, RemoteBattery, RemoteBatterySuffix, null, null, null);

        UpdateOtherDeviceRows(otherDevices);
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        return value.Length <= 18 ? value : value[..17] + "…";
    }

    private void SetBattery(
        PeripheralSnapshot snapshot,
        TextBlock number,
        TextBlock suffix,
        TextBlock? left,
        TextBlock? right,
        TextBlock? caseBox)
    {
        var value = EffectiveBattery(snapshot);
        var previous = number.Text;
        number.Text = value is int pct ? pct.ToString() : "—";
        if (_entranceDone && previous != number.Text && previous.Length > 0)
        {
            UiMotion.Pop(number, 1.04, 220);
        }
        suffix.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        var brush = BatteryBrush(snapshot);
        number.Foreground = brush;
        suffix.Foreground = batterySuffixBrush(snapshot);

        if (left is not null && right is not null && caseBox is not null)
        {
            left.Text = ComponentText(snapshot, "left", "左耳");
            right.Text = ComponentText(snapshot, "right", "右耳");
            caseBox.Text = ComponentText(snapshot, "case", "充电盒");
        }

        Brush batterySuffixBrush(PeripheralSnapshot s) =>
            EffectiveBattery(s) is int p && p <= 15 && s.Charging is not (ChargingState.Charging or ChargingState.Full)
                ? AppTheme.GetBrush(this, "AppDangerBrush")
                : AppTheme.GetBrush(this, "AppTextSecondaryBrush");
    }

    /// <summary>
    /// 耳机没有整机电量字段时，用左右耳真实分量平均值作整机读数；不伪造缺失数据。
    /// </summary>
    private static int? EffectiveBattery(PeripheralSnapshot snapshot)
    {
        if (snapshot.BatteryPercent is int direct)
        {
            return direct;
        }

        var parts = snapshot.Components
            .Where(item => item.Key is "left" or "right" && item.Percent is int)
            .Select(item => item.Percent!.Value)
            .ToArray();
        return parts.Length >= 2 ? (int)Math.Round(parts.Average()) : parts.FirstOrDefault() is var first && parts.Length == 1 ? first : null;
    }

    private static string ComponentText(PeripheralSnapshot snapshot, string key, string label)
    {
        var component = snapshot.Components.FirstOrDefault(item => item.Key == key);
        return component?.Percent is int pct ? $"{label} {pct}%" : $"{label} —";
    }

    private void PlayEntrance()
    {
        if (_entrancePlayed)
        {
            return;
        }

        _entrancePlayed = true;
        if (!AnimationsEnabled())
        {
            return;
        }

        _entranceDone = false;
        var storyboard = new Storyboard();
        var index = 0;
        foreach (var card in Cards())
        {
            var transform = (TranslateTransform)card.RenderTransform;
            card.Opacity = 0;
            transform.Y = 20;

            var begin = TimeSpan.FromMilliseconds(index * EntranceStep.TotalMilliseconds);
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = EntranceDuration,
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, card);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            var move = new DoubleAnimation
            {
                From = 20,
                To = 0,
                Duration = EntranceDuration,
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(move, transform);
            Storyboard.SetTargetProperty(move, "Y");
            storyboard.Children.Add(move);
            index++;
        }

        storyboard.Completed += (_, _) => _entranceDone = true;
        storyboard.Begin();

        AnimateNumber(HeadsetBattery, 540);
        AnimateNumber(MouseBattery, 650);
        AnimateNumber(RemoteBattery, 760);
    }

    private static bool AnimationsEnabled()
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

    private void AnimateNumber(TextBlock block, int delayMs)
    {
        if (block.Text is not { Length: > 0 } text || !int.TryParse(text, out var target) || target < 0 || target > 100)
        {
            return;
        }

        block.Text = "0";
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        var stopwatch = Stopwatch.StartNew();
        const double duration = 760;
        var delayed = false;

        timer.Tick += (_, _) =>
        {
            var elapsed = stopwatch.Elapsed.TotalMilliseconds - delayMs;
            if (elapsed < 0)
            {
                return;
            }

            if (!delayed)
            {
                delayed = true;
            }

            var progress = Math.Clamp(elapsed / duration, 0, 1);
            var eased = 1 - Math.Pow(2, -10 * progress);
            block.Text = Math.Round(target * eased).ToString("0");
            if (progress >= 1)
            {
                block.Text = target.ToString("0");
                timer.Stop();
                _numberTimers.Remove(timer);
            }
        };

        _numberTimers.Add(timer);
        timer.Start();
    }

    private void StopNumberTimers()
    {
        foreach (var timer in _numberTimers)
        {
            timer.Stop();
        }

        _numberTimers.Clear();
    }

    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_entranceDone)
        {
            return;
        }

        AnimateCard(sender as Button, -4);
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_entranceDone)
        {
            return;
        }

        AnimateCard(sender as Button, 0);
    }

    private void AnimateCard(Button? card, double targetY)
    {
        if (card?.RenderTransform is not TranslateTransform transform || !AnimationsEnabled())
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetY,
            Duration = HoverDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Y");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
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

        if (device.Percent is int percent && percent <= 15 &&
            device.Charging is not (ChargingState.Charging or ChargingState.Full))
        {
            return AppTheme.GetBrush(this, "AppDangerBrush");
        }

        return AppTheme.GetBrush(this, "AppTextPrimaryBrush");
    }

    private Brush BatteryBrush(PeripheralSnapshot snapshot)
    {
        if (!snapshot.IsConnected)
        {
            return AppTheme.GetBrush(this, "AppTextTertiaryBrush");
        }

        if (EffectiveBattery(snapshot) is int pct && pct <= 15 &&
            snapshot.Charging is not (ChargingState.Charging or ChargingState.Full))
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
