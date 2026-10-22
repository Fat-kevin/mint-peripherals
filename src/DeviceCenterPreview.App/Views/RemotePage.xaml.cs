using System.Diagnostics;
using System.Text.Json;
using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeviceCenterPreview.App.Views;

public sealed partial class RemotePage : Page, IThemeAware
{
    private static readonly (string Id, string Label, string Short, double X, double Y)[] KeyPlacements =
    [
        ("power", "电源", "电", 0.376, 0.105),
        ("up", "上", "上", 0.499, 0.186),
        ("left", "左", "左", 0.362, 0.243),
        ("back", "返回", "返", 0.402, 0.383),
        ("home", "主页", "主", 0.402, 0.470),
        ("menu", "菜单", "菜", 0.402, 0.558),
        ("right", "右", "右", 0.638, 0.243),
        ("ok", "确定", "OK", 0.499, 0.243),
        ("down", "下", "下", 0.499, 0.303),
        ("volume_up", "音量+", "+", 0.594, 0.384),
        ("volume_down", "音量-", "−", 0.594, 0.466),
        ("tv", "TV", "TV", 0.594, 0.558),
    ];

    private static readonly (string Id, string Label)[] GestureLabels =
    [
        ("single", "单击"),
        ("double", "双击"),
        ("long", "长按"),
    ];

    private static readonly (string Id, string Label)[] TableKeys =
    [
        ("power", "电源"),
        ("up", "上"),
        ("left", "左"),
        ("ok", "确定"),
        ("right", "右"),
        ("down", "下"),
        ("back", "返回"),
        ("home", "主页"),
        ("menu", "菜单"),
        ("volume_up", "音量+"),
        ("volume_down", "音量-"),
        ("volume_mute", "静音"),
        ("tv", "TV"),
    ];

    private static readonly ActionChoice[] AssignableActions = BuildAssignableActions();

    private static ActionChoice[] BuildAssignableActions()
    {
        return
        [
            new("disabled", "禁用", "{\"type\":\"disabled\"}"),
            Shortcut("enter", "Enter", "enter"),
            Shortcut("escape", "Esc", "escape"),
            Shortcut("space", "空格", "space"),
            Shortcut("tab", "Tab", "tab"),
            Shortcut("backspace", "退格", "backspace"),
            Shortcut("delete", "删除", "delete"),
            Shortcut("up", "↑", "up"),
            Shortcut("down", "↓", "down"),
            Shortcut("left", "←", "left"),
            Shortcut("right", "→", "right"),
            Shortcut("home", "Home", "home"),
            Shortcut("apps", "右键菜单", "apps"),
            Shortcut("f5", "刷新", "f5"),
            Shortcut("control+c", "复制", "control", "c"),
            Shortcut("control+v", "粘贴", "control", "v"),
            Shortcut("control+x", "剪切", "control", "x"),
            Shortcut("control+a", "全选", "control", "a"),
            Shortcut("control+z", "撤销", "control", "z"),
            Shortcut("control+s", "保存", "control", "s"),
            Shortcut("alt+tab", "切换窗口", "alt", "tab"),
            Shortcut("left_windows+d", "显示桌面", "left_windows", "d"),
            Shortcut("control+w", "关闭窗口", "control", "w"),
            Shortcut("left_windows+l", "锁定", "left_windows", "l"),
            Shortcut("left_windows+shift+s", "截图", "left_windows", "shift", "s"),
            Shortcut("volume_mute", "静音", "volume_mute"),
            Shortcut("volume_up", "音量+", "volume_up"),
            Shortcut("volume_down", "音量−", "volume_down"),
            Shortcut("media_play_pause", "播放/暂停", "media_play_pause"),
            Shortcut("media_prev", "上一首", "media_prev"),
            Shortcut("media_next", "下一首", "media_next"),
            new("scroll_up", "滚轮上", "{\"type\":\"scroll\",\"direction\":\"up\",\"steps\":1}"),
            new("scroll_down", "滚轮下", "{\"type\":\"scroll\",\"direction\":\"down\",\"steps\":1}"),
            new("mouse_left", "左键点击", "{\"type\":\"mouse_click\",\"kind\":\"left\"}"),
            new("mouse_right", "右键点击", "{\"type\":\"mouse_click\",\"kind\":\"right\"}"),
            new("mouse_double_left", "双击", "{\"type\":\"mouse_click\",\"kind\":\"double_left\"}"),
            new("mouse_middle", "中键", "{\"type\":\"mouse_click\",\"kind\":\"middle\"}"),
            new("move_up", "鼠标上移", "{\"type\":\"mouse_move\",\"direction\":\"up\",\"distance\":30}"),
            new("move_down", "鼠标下移", "{\"type\":\"mouse_move\",\"direction\":\"down\",\"distance\":30}"),
            new("move_left", "鼠标左移", "{\"type\":\"mouse_move\",\"direction\":\"left\",\"distance\":30}"),
            new("move_right", "鼠标右移", "{\"type\":\"mouse_move\",\"direction\":\"right\",\"distance\":30}"),
        ];
    }

    private static ActionChoice Shortcut(string id, string label, params string[] keys) =>
        new(id, label, JsonSerializer.Serialize(new { type = "shortcut", chord = new { keys } }));

    private readonly Dictionary<(string Key, string Gesture), KeyMapping> _mappingMap = new();
    private readonly Dictionary<string, Button> _hotspots = new();
    private readonly Dictionary<(string Key, string Gesture), Button> _mappingCells = new();
    private readonly Dictionary<string, Border> _mappingRows = new();
    private readonly Dictionary<string, TextBlock> _receiveTexts = new();
    private readonly Dictionary<string, int> _receivedCounts = new(StringComparer.OrdinalIgnoreCase);
    private string _hoveredKeyId = "";
    private readonly Dictionary<string, TextBlock> _gestureActionTexts = new();
    private readonly HashSet<string> _activeButtons = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedKeyId = "ok";
    private string _lastDeviceSignature = "";
    private string _lastMappingSignature = "";
    private string _lastReceiveSignature = "";
    private bool _lastUiEngineConnected;
    private bool _loading;
    private string? _loadedTool;
    private bool _engineConnected;
    private bool _canWrite;
    private bool _fullKeyAvailable;
    private CancellationTokenSource? _fullKeyRetryCts;
    private bool _loadedRemote;
    private bool _receiving;
    private List<RemoteDeviceInfo> _scanDevices = [];
    private DateTimeOffset _testEndsAt;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _statusTimer;

    public RemotePage()
    {
        InitializeComponent();
        BuildHotspots();
        BuildGesturePanel();
        BuildMappingTable();
        BuildActionEditor();
        SelectKey(_selectedKeyId);
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += (_, _) => Refresh();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        App.PeripheralService.RemoteButtonReceived += OnRemoteButtonReceived;
        PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        Refresh();
        _ = LoadRemoteAsync();
        _statusTimer.Start();
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
        App.PeripheralService.RemoteButtonReceived -= OnRemoteButtonReceived;
        _statusTimer.Stop();
        _fullKeyRetryCts?.Cancel();
        _fullKeyRetryCts = null;
        if (_receiving)
        {
            _ = StopReceiveTestAsync("已离开页面，接收测试停止。");
        }
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var snapshot = App.PeripheralService.GetSnapshot(DeviceKind.Remote);
        var controls = App.PeripheralService.GetRemoteControls();

        _loading = true;
        var devices = App.PeripheralService.GetDevices(DeviceKind.Remote);
        // 连接状态变化很频繁；DeviceCombo 只显示设备名，不应因 IsConnected 变化重建 ItemsSource。
        var deviceSignature = string.Join("|", devices.Select(device => $"{device.Id}:{device.Name}"));
        if (deviceSignature != _lastDeviceSignature)
        {
            _lastDeviceSignature = deviceSignature;
            DeviceCombo.ItemsSource = devices;
        }

        var selected = App.PeripheralService.GetSelectedDeviceId(DeviceKind.Remote);
        if (!string.IsNullOrEmpty(selected))
        {
            DeviceCombo.SelectedValue = selected;
        }

        TitleText.Text = $"遥控器 · {snapshot.DisplayName}";
        SubtitleText.Text = snapshot.StatusText;
        RemoteRing.BatteryValue = snapshot.BatteryPercent ?? 0;
        RemoteRing.Charging = snapshot.Charging == ChargingState.Charging;
        RemoteRing.RingBrush = (snapshot.BatteryPercent ?? 0) switch
        {
            <= 15 => ResourceBrush("AppDangerBrush"),
            <= 35 => ResourceBrush("AppWarningBrush"),
            _ => ResourceBrush("AppRemoteBrush"),
        };
        ControlBannerText.Text = snapshot.ControlHint ?? "";
        ControlBanner.Visibility = snapshot.ControlsAvailable && snapshot.WriteAllowed
            ? Visibility.Collapsed
            : Visibility.Visible;

        _engineConnected = controls.EngineConnected;
        _canWrite = snapshot.WriteAllowed;
        AudioCombo.IsEnabled = _engineConnected && _canWrite;
        GainSlider.IsEnabled = _engineConnected && _canWrite;
        ToolCombo.IsEnabled = _engineConnected && _canWrite;
        FullKeySwitch.IsEnabled = _engineConnected && _canWrite && _fullKeyAvailable;
        VoiceTestButton.IsEnabled = _engineConnected;
        ApplyButton.IsEnabled = _engineConnected && _canWrite;
        ConnectButton.IsEnabled = ScanCombo.SelectedItem is RemoteDeviceInfo { Supported: true };
        DisconnectButton.IsEnabled = _engineConnected;
        ScanButton.IsEnabled = true;

        FullKeySwitch.IsOn = _engineConnected && FullKeySwitch.IsOn;
        VoiceStatus.Text = _engineConnected ? "引擎就绪" : "遥控器未连接";
        InputStatus.Text = _engineConnected
            ? $"输入：{controls.RawInputPhase} · 设备 {controls.MatchedDeviceCount} · " +
              $"事件 {controls.RawEventCount} · 语义 {controls.SemanticEdgeCount} · 触发 {controls.FiredGestures}" +
              (string.IsNullOrEmpty(controls.LastButton)
                  ? ""
                  : $" · 最后 {controls.LastButton}{(controls.LastPressed ? "↓" : "↑")}") +
              (controls.MappingsEnabled ? "" : " · 映射未启用") +
              (_fullKeyAvailable ? "" : " · 全按键 Helper 不可用")
            : "输入：遥控器未连接";

        _activeButtons.Clear();
        foreach (var button in controls.ActiveButtons.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            _activeButtons.Add(button.Trim());
        }

        UpdateHotspots();
        UpdateMappingTable();
        UpdateReceiveUi();
        if (!_engineConnected)
        {
            FullKeySwitch.IsOn = false;
            _fullKeyAvailable = false;
            FullKeySwitch.Header = "全按键支持（Helper 状态未知）";
            _loadedRemote = false;
            if (_receiving)
            {
                _ = StopReceiveTestAsync("引擎断开，接收测试停止。");
            }
        }
        else if (!_loadedRemote)
        {
            _ = LoadRemoteAsync();
        }

        if (_lastUiEngineConnected != _engineConnected)
        {
            _lastUiEngineConnected = _engineConnected;
            SelectKey(_selectedKeyId);
        }

        _loading = false;
    }

    private async Task LoadRemoteAsync()
    {
        if (!_engineConnected)
        {
            return;
        }

        _loadedRemote = true;
        try
        {
            var endpoints = await App.PeripheralService.GetRemoteAudioEndpointsAsync();
            var controls = App.PeripheralService.GetRemoteControls();
            _loading = true;
            AudioCombo.ItemsSource = endpoints;
            AudioCombo.SelectedIndex = -1;
            if (!string.IsNullOrWhiteSpace(controls.AudioEndpointId))
            {
                var match = endpoints.FirstOrDefault(endpoint => endpoint.Id == controls.AudioEndpointId);
                if (match is not null)
                {
                    AudioCombo.SelectedItem = match;
                }
            }

            _loading = false;

            var tool = await App.PeripheralService.GetRemoteVoiceToolAsync();
            if (!string.IsNullOrWhiteSpace(tool))
            {
                _loading = true;
                _loadedTool = tool;
                foreach (var item in ToolCombo.Items.OfType<ComboBoxItem>())
                {
                    if (item.Tag is string tag && tag == tool)
                    {
                        ToolCombo.SelectedItem = item;
                        break;
                    }
                }

                _loading = false;
                UpdateToolHint(tool);
            }

            var fullKey = await App.PeripheralService.GetRemoteFullKeyStatusAsync();
            _loading = true;
            _fullKeyAvailable = fullKey.Available;
            FullKeySwitch.IsEnabled = _engineConnected && _canWrite && fullKey.Available;
            FullKeySwitch.IsOn = fullKey.Enabled;
            FullKeySwitch.Header = fullKey.Available
                ? "全按键支持（Helper 已就绪）"
                : "全按键支持（Helper 未就绪）";
            if (!fullKey.Available)
            {
                InputStatus.Text = fullKey.Message ?? "全按键 Helper 不可用。";
                _ = RetryFullKeyAsync();
            }

            _loading = false;

            await RefreshHotkeyAsync();

            var mappings = await App.PeripheralService.GetRemoteMappingsAsync();
            _mappingMap.Clear();
            foreach (var mapping in mappings)
            {
                if (!string.IsNullOrWhiteSpace(mapping.KeyId) &&
                    !string.IsNullOrWhiteSpace(mapping.GestureId))
                {
                    _mappingMap[(mapping.KeyId, mapping.GestureId)] = mapping;
                }
            }

            SelectKey(_selectedKeyId);
            _lastMappingSignature = "";
            UpdateMappingTable();
            MappingSaveStatus.Text = "已从配置文件加载";
        }
        catch (Exception ex)
        {
            AppLog.Warn("remote page load failed: " + ex.Message);
            SaveStatus.Text = $"读取遥控器配置失败：{ex.Message}";
        }
    }

    private async Task RetryFullKeyAsync()
    {
        _fullKeyRetryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fullKeyRetryCts = cts;
        try
        {
            // Helper 由安装器/sidecar 在启动后几秒内拉起；页面可能抢在桥接连接前加载。
            // 只重试状态读取，不重建任何控件，避免触发 WinUI 页面重建问题。
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cts.Token);
                if (cts.IsCancellationRequested || !_engineConnected)
                {
                    return;
                }

                var status = await App.PeripheralService.GetRemoteFullKeyStatusAsync();
                if (!status.Available)
                {
                    continue;
                }

                _loading = true;
                _fullKeyAvailable = true;
                FullKeySwitch.IsEnabled = _engineConnected && _canWrite;
                FullKeySwitch.IsOn = status.Enabled;
                FullKeySwitch.Header = "全按键支持（Helper 已就绪）";
                _loading = false;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载/重新加载。
        }
        catch (Exception ex)
        {
            AppLog.Warn("remote full-key status retry failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_fullKeyRetryCts, cts))
            {
                _fullKeyRetryCts = null;
            }

            cts.Dispose();
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeviceCombo.SelectedValue is not string id)
        {
            return;
        }

        App.PeripheralService.SelectDevice(DeviceKind.Remote, id);
        Refresh();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanStatus.Text = "正在扫描…";
        try
        {
            _scanDevices = (await App.PeripheralService.ScanRemotesAsync()).ToList();
            ScanCombo.ItemsSource = _scanDevices;
            var preferred = _scanDevices.FirstOrDefault(device => device.Supported);
            ScanCombo.SelectedItem = preferred ?? _scanDevices.FirstOrDefault();
            ScanStatus.Text = _scanDevices.Count == 0
                ? "没有扫描到已配对的遥控器；请先在 Windows 蓝牙中配对。"
                : $"找到 {_scanDevices.Count} 个，其中 {_scanDevices.Count(device => device.Supported)} 个受支持。";
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ConnectButton.IsEnabled = ScanCombo.SelectedItem is RemoteDeviceInfo { Supported: true };
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ScanCombo.SelectedItem is not RemoteDeviceInfo device)
        {
            ScanStatus.Text = "请先扫描并选择一个遥控器。";
            return;
        }

        if (!device.Supported)
        {
            ScanStatus.Text = "该设备不在 RC001/RC003 支持范围内，未连接。";
            return;
        }

        ConnectButton.IsEnabled = false;
        ScanStatus.Text = $"正在连接 {device.DisplayName}…";
        try
        {
            var result = await App.PeripheralService.ConnectRemoteAsync(device.Id);
            ScanStatus.Text = result.Ok
                ? $"已请求连接 {device.DisplayName}，等待引擎状态更新。"
                : result.Message ?? "连接失败";
        }
        finally
        {
            ConnectButton.IsEnabled = ScanCombo.SelectedItem is RemoteDeviceInfo { Supported: true };
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        DisconnectButton.IsEnabled = false;
        ScanStatus.Text = "正在断开…";
        try
        {
            var result = await App.PeripheralService.DisconnectRemoteAsync();
            ScanStatus.Text = result.Ok ? "已请求断开。" : result.Message ?? "断开失败";
        }
        finally
        {
            DisconnectButton.IsEnabled = _engineConnected;
        }
    }

    private async void Audio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_engineConnected || AudioCombo.SelectedItem is not AudioEndpointInfo endpoint)
        {
            return;
        }

        var result = await App.PeripheralService.SetRemoteAudioEndpointAsync(endpoint.Id);
        SaveStatus.Text = result.Ok ? $"音频端点：{endpoint.Name}" : result.Message ?? "切换失败";
    }

    private void Gain_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (GainValueText is not null)
        {
            GainValueText.Text = $"{(int)GainSlider.Value} dB";
        }
    }

    private async void Tool_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_engineConnected ||
            ToolCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tool ||
            tool == _loadedTool)
        {
            return;
        }

        var result = await App.PeripheralService.SetRemoteVoiceToolAsync(tool);
        SaveStatus.Text = result.Ok ? "语音工具与按住说话快捷键已保存" : result.Message ?? "设置失败";
        UpdateToolHint(tool);
        if (result.Ok)
        {
            _loadedTool = tool;
            await RefreshHotkeyAsync();
        }
    }

    private async Task RefreshHotkeyAsync()
    {
        var keys = await App.PeripheralService.GetRemoteHotkeyAsync();
        HotkeyText.Text = keys.Count == 0
            ? "当前未设置按住说话快捷键；语音键只会切换音频，不会向输入法注入按键。"
            : $"已保存的按住说话快捷键：{FormatKeys(keys)}";
    }

    private static string FormatKeys(IReadOnlyList<string> keys) => string.Join(" + ", keys.Select(key => key switch
    {
        "right_alt" => "右 Alt",
        "left_alt" => "左 Alt",
        "left_control" => "左 Ctrl",
        "right_control" => "右 Ctrl",
        "left_shift" => "左 Shift",
        "left_windows" => "左 Win",
        "right_windows" => "右 Win",
        _ => key.Replace('_', ' '),
    }));

    private void UpdateToolHint(string tool)
    {
        var (title, body) = tool switch
        {
            "wechat" => ("按住遥控器语音键 = 左 Ctrl + 左 Win",
                "在微信输入法里把麦克风选为 CABLE Output；语音需要联网，按住约半秒以上再说话。"),
            "vokie" => ("按住遥控器语音键 = 右 Alt",
                "在 Vokie 里把麦克风选为 CABLE Output，快捷键保持右 Alt；Vokie 与豆包同时运行时可能抢占右 Alt。"),
            "other" => ("按住遥控器语音键 = 自定义",
                "输入工具麦克风选 CABLE Output；工具里的语音键要与本页设置一致；选“不按键”时只把声音送到 CABLE。"),
            _ => ("按住遥控器语音键 = 右 Alt",
                "在豆包里把麦克风选为 CABLE Output；长按语音键保持右 Alt（出厂默认）。首次使用若弹出 Windows 授权窗口请选“是”。"),
        };
        ToolHintTitle.Text = title;
        ToolHintText.Text = body;
    }

    private async void FullKey_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !_engineConnected || !_canWrite)
        {
            return;
        }

        if (!_fullKeyAvailable)
        {
            _loading = true;
            FullKeySwitch.IsOn = false;
            _loading = false;
            SaveStatus.Text = "RC003 Helper 未就绪；音量键/返回键需要它。请重新运行安装包完成一次授权，或由管理员执行 Helper 的 --install-task。";
            return;
        }

        var result = await App.PeripheralService.SetRemoteFullKeyAsync(FullKeySwitch.IsOn);
        SaveStatus.Text = result.Ok
            ? $"全按键 {(FullKeySwitch.IsOn ? "已开启" : "已关闭")}"
            : result.Message ?? "全按键设置失败";
        if (!result.Ok)
        {
            _loading = true;
            FullKeySwitch.IsOn = false;
            _loading = false;
        }
    }

    private void VoiceTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_engineConnected)
        {
            return;
        }

        SaveStatus.Text = "引擎已就绪；请按住遥控器语音键，音频会路由到所选端点。";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_engineConnected || !_canWrite)
        {
            SaveStatus.Text = _engineConnected ? "遥控器写入已在设置中关闭。" : "遥控器引擎未连接。";
            return;
        }

        var result = await App.PeripheralService.SetRemoteGainAsync((int)GainSlider.Value);
        SaveStatus.Text = result.Ok
            ? $"已应用：增益 {(int)GainSlider.Value} dB"
            : result.Message ?? "增益写入失败";
    }

    private void BuildHotspots()
    {
        const double photoWidth = 200;
        const double photoHeight = 406;
        foreach (var (id, label, _, x, y) in KeyPlacements)
        {
            var button = new Button
            {
                Tag = id,
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            button.Click += Hotspot_Click;
            button.PointerEntered += (_, _) => SetHoverKey(id);
            button.PointerExited += (_, _) => ClearHoverKey(id);
            Canvas.SetLeft(button, x * photoWidth - 15);
            Canvas.SetTop(button, y * photoHeight - 15);
            PhotoCanvas.Children.Add(button);
            _hotspots[id] = button;
        }

        UpdateHotspots();
    }

    private void SetHoverKey(string keyId)
    {
        _hoveredKeyId = keyId;
        UpdateHotspots();
        UpdateRowHighlights();
    }

    private void ClearHoverKey(string keyId)
    {
        if (string.Equals(_hoveredKeyId, keyId, StringComparison.Ordinal))
        {
            _hoveredKeyId = "";
            UpdateHotspots();
            UpdateRowHighlights();
        }
    }

    private void UpdateHotspots()
    {
        foreach (var (id, button) in _hotspots)
        {
            var active = _activeButtons.Contains(id);
            var hovered = string.Equals(id, _hoveredKeyId, StringComparison.Ordinal);
            button.Background = active
                ? ResourceBrush("AppRemoteTintBrush")
                : hovered
                    ? ResourceBrush("AppIonTintBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderBrush = active
                ? ResourceBrush("AppRemoteBrush")
                : hovered
                    ? ResourceBrush("AppIonBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderThickness = hovered || active ? new Thickness(2) : new Thickness(0);
        }
    }

    private void UpdateRowHighlights()
    {
        foreach (var (keyId, row) in _mappingRows)
        {
            row.Background = string.Equals(keyId, _hoveredKeyId, StringComparison.Ordinal)
                ? ResourceBrush("AppIonTintBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void Hotspot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string keyId })
        {
            SelectKey(keyId);
        }
    }

    private void BuildMappingTable()
    {
        foreach (var (keyId, keyLabel) in TableKeys)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            for (var index = 0; index < GestureLabels.Length; index++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            grid.Children.Add(new TextBlock
            {
                Text = keyLabel,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["AppCardLabelStyle"],
            });

            for (var index = 0; index < GestureLabels.Length; index++)
            {
                var (gestureId, _) = GestureLabels[index];
                var cell = new Button
                {
                    Tag = $"{keyId}|{gestureId}",
                    Content = "禁用",
                    Padding = new Thickness(8, 5, 8, 5),
                    FontSize = 12,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                ToolTipService.SetToolTip(cell, $"{keyLabel} · {GestureLabels[index].Label}");
                cell.Click += MappingCell_Click;
                Grid.SetColumn(cell, index + 1);
                grid.Children.Add(cell);
                _mappingCells[(keyId, gestureId)] = cell;
            }

            var receive = new TextBlock
            {
                Text = "—",
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["AppCardFootnoteStyle"],
            };
            Grid.SetColumn(receive, GestureLabels.Length + 1);
            grid.Children.Add(receive);
            _receiveTexts[keyId] = receive;

            var row = new Border
            {
                Padding = new Thickness(6, 3, 6, 3),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = grid,
            };
            row.PointerEntered += (_, _) => SetHoverKey(keyId);
            row.PointerExited += (_, _) => ClearHoverKey(keyId);
            _mappingRows[keyId] = row;
            MappingTablePanel.Children.Add(row);
        }
    }

    private void UpdateMappingTable()
    {
        var signature = string.Join("|", TableKeys.SelectMany(key =>
            GestureLabels.Select(gesture =>
                $"{key.Id}:{gesture.Id}:{(_mappingMap.TryGetValue((key.Id, gesture.Id), out var mapping) ? mapping.ActionId ?? mapping.Action : "disabled")}")));
        if (signature == _lastMappingSignature)
        {
            return;
        }

        _lastMappingSignature = signature;
        var configuredBrush = ResourceBrush("AppSuccessBrush");
        var disabledBrush = ResourceBrush("TextFillColorSecondaryBrush");
        foreach (var (keyId, _) in TableKeys)
        {
            foreach (var (gestureId, _) in GestureLabels)
            {
                if (!_mappingCells.TryGetValue((keyId, gestureId), out var cell))
                {
                    continue;
                }

                _mappingMap.TryGetValue((keyId, gestureId), out var mapping);
                var action = mapping?.Action ?? "禁用";
                var configured = action != "禁用";
                cell.Content = action;
                cell.Foreground = configured ? configuredBrush : disabledBrush;
                ToolTipService.SetToolTip(cell, configured ? action : "未配置（点击设置）");
            }
        }
    }

    private void MappingCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            var parts = tag.Split('|');
            if (parts.Length == 2)
            {
                SelectKey(parts[0]);
                OpenActionEditor(parts[0], parts[1]);
            }
        }
    }

    private void BuildActionEditor()
    {
        _actionEditorTitle = new TextBlock { Style = (Style)Application.Current.Resources["AppSectionTitleStyle"] };
        _actionEditorCurrent = new TextBlock { Style = (Style)Application.Current.Resources["AppCardFootnoteStyle"] };
        ActionEditorPanel.Children.Add(_actionEditorTitle);
        ActionEditorPanel.Children.Add(_actionEditorCurrent);

        var disable = new Button
        {
            Content = "禁用该手势",
            Style = (Style)Application.Current.Resources["AppSecondaryButtonStyle"],
        };
        disable.Click += (_, _) => ApplyChoice("disabled", new ActionChoice("disabled", "禁用", "{\"type\":\"disabled\"}"));
        ActionEditorPanel.Children.Add(disable);

        AddChoiceGrid("基础按键", AssignableActions.Take(14).ToArray());
        AddChoiceGrid("系统与媒体", AssignableActions.Skip(14).Take(17).ToArray());
        AddChoiceGrid("鼠标", AssignableActions.Skip(31).ToArray());

        var close = new Button
        {
            Content = "关闭编辑",
            Style = (Style)Application.Current.Resources["AppSecondaryButtonStyle"],
        };
        close.Click += (_, _) => ActionEditorCard.Visibility = Visibility.Collapsed;
        ActionEditorPanel.Children.Add(close);

        void AddChoiceGrid(string header, ActionChoice[] choices)
        {
            ActionEditorPanel.Children.Add(new TextBlock
            {
                Text = header,
                Style = (Style)Application.Current.Resources["AppCardLabelStyle"],
            });
            var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            const int columns = 3;
            for (var column = 0; column < columns; column++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (var row = 0; row < (choices.Length + columns - 1) / columns; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var index = 0; index < choices.Length; index++)
            {
                var choice = choices[index];
                var button = new Button
                {
                    Content = choice.Label,
                    Padding = new Thickness(6, 4, 6, 4),
                    MinWidth = 0,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                button.Click += (_, _) => ApplyChoice(choice.Id, choice);
                Grid.SetColumn(button, index % columns);
                Grid.SetRow(button, index / columns);
                grid.Children.Add(button);
            }

            ActionEditorPanel.Children.Add(grid);
        }
    }

    private void OpenActionEditor(string keyId, string gestureId)
    {
        if (!_engineConnected)
        {
            SaveStatus.Text = "遥控器引擎未连接";
            return;
        }

        if (!_canWrite)
        {
            SaveStatus.Text = "遥控器写入已在设置中关闭。";
            return;
        }

        _editingKeyId = keyId;
        _editingGesture = gestureId;
        var keyLabel = TableKeys.FirstOrDefault(key => key.Id == keyId).Label ?? keyId;
        var gestureLabel = GestureLabels.FirstOrDefault(item => item.Id == gestureId).Label ?? gestureId;
        _mappingMap.TryGetValue((keyId, gestureId), out var current);
        _actionEditorTitle.Text = $"{keyLabel} · {gestureLabel}";
        _actionEditorCurrent.Text = $"当前：{current?.Action ?? "禁用"}";
        ActionEditorCard.Visibility = Visibility.Visible;
    }

    private async void ApplyChoice(string actionId, ActionChoice choice)
    {
        if (string.IsNullOrEmpty(_editingKeyId))
        {
            return;
        }

        if (!_canWrite)
        {
            SaveStatus.Text = "遥控器写入已在设置中关闭。";
            return;
        }

        var key = _editingKeyId;
        var gesture = _editingGesture;
        var result = await App.PeripheralService.SaveRemoteMappingAsync(key, gesture, choice.Json);
        SaveStatus.Text = result.Ok ? "映射已保存" : result.Message ?? "保存失败";
        MappingSaveStatus.Text = result.Ok
            ? $"已自动保存 · {DateTime.Now:HH:mm:ss}"
            : result.Message ?? "保存失败";
        if (!result.Ok)
        {
            return;
        }

        var keyLabel = TableKeys.FirstOrDefault(item => item.Id == key).Label ?? key;
        var gestureLabel = GestureLabels.FirstOrDefault(item => item.Id == gesture).Label ?? gesture;
        _mappingMap[(key, gesture)] = new KeyMapping(keyLabel, gestureLabel, choice.Label, key, gesture, actionId);
        _lastMappingSignature = "";
        ActionEditorCard.Visibility = Visibility.Collapsed;
        UpdateMappingTable();
        SelectKey(_selectedKeyId);
        UpdateHotspots();
    }

    private TextBlock _actionEditorTitle = null!;
    private TextBlock _actionEditorCurrent = null!;
    private string _editingKeyId = "";
    private string _editingGesture = "";
    private sealed record ActionChoice(string Id, string Label, string Json);

    private void OnRemoteButtonReceived(object? sender, RemoteButtonEvent edge)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_receiving)
            {
                return;
            }

            if (edge.Pressed)
            {
                _receivedCounts[edge.Button] = _receivedCounts.TryGetValue(edge.Button, out var count)
                    ? count + 1
                    : 1;
                _lastReceiveSignature = "";
                var label = TableKeys.FirstOrDefault(key => key.Id == edge.Button).Label ?? edge.Button;
                ReceiveStatus.Text = $"收到：{label} ↓";
            }

            UpdateReceiveUi();
        });
    }

    private async void ReceiveTest_Click(object sender, RoutedEventArgs e)
    {
        if (_receiving)
        {
            await StopReceiveTestAsync("已停止接收测试。");
            return;
        }

        if (!_engineConnected)
        {
            ReceiveStatus.Text = "遥控器引擎未连接，无法测试。";
            return;
        }

        var result = await App.PeripheralService.SetRemoteMappingSuspensionAsync(true);
        if (!result.Ok)
        {
            ReceiveStatus.Text = result.Message ?? "无法暂停映射。";
            return;
        }

        _receivedCounts.Clear();
        _receiving = true;
        _testEndsAt = DateTimeOffset.Now.AddSeconds(10);
        _lastReceiveSignature = "";
        ReceiveStatus.Text = "请按遥控器按键；测试期间不执行映射动作。";
        SaveStatus.Text = "接收测试中：只观察按键信号。";
        UpdateReceiveUi();
    }

    private async Task StopReceiveTestAsync(string message)
    {
        _receiving = false;
        ReceiveStatus.Text = message;
        ReceiveTestButton.Content = "开始接收测试（10 秒）";
        await App.PeripheralService.SetRemoteMappingSuspensionAsync(false);
        UpdateReceiveUi();
    }

    private void UpdateReceiveUi()
    {
        if (_receiving)
        {
            var left = Math.Max(0, (int)Math.Ceiling((_testEndsAt - DateTimeOffset.Now).TotalSeconds));
            if (left <= 0)
            {
                _ = StopReceiveTestAsync($"测试结束：收到 {_receivedCounts.Count} 个按键。");
                return;
            }

            ReceiveTestButton.Content = $"停止接收测试（{left} 秒）";
            var signature = "R|" + left + "|" + _receivedCounts.Count;
            if (signature != _lastReceiveSignature)
            {
                _lastReceiveSignature = signature;
                foreach (var (keyId, _) in TableKeys)
                {
                    if (_receiveTexts.TryGetValue(keyId, out var text))
                    {
                        text.Text = _receivedCounts.TryGetValue(keyId, out var count) ? $"✓ {count}" : "—";
                    }
                }
            }

            return;
        }

        if (_lastReceiveSignature == "R|done")
        {
            return;
        }

        _lastReceiveSignature = "R|done";
        foreach (var (keyId, _) in TableKeys)
        {
            if (_receiveTexts.TryGetValue(keyId, out var text))
            {
                text.Text = _receivedCounts.TryGetValue(keyId, out var count) ? $"✓ {count}" : "—";
            }
        }
    }

    private void BuildGesturePanel()
    {
        foreach (var (gestureId, gestureLabel) in GestureLabels)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = gestureLabel,
                Style = (Style)Application.Current.Resources["AppCardLabelStyle"],
            });
            var actionText = new TextBlock
            {
                Style = (Style)Application.Current.Resources["AppCardTitleStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(actionText, 1);
            grid.Children.Add(actionText);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(6),
                Background = ResourceBrush("AppInputBrush"),
            };
            GesturePanel.Children.Add(row);
            _gestureActionTexts[gestureId] = actionText;
        }
    }

    private void SelectKey(string keyId)
    {
        var known = TableKeys.Any(key => key.Id == keyId);
        _selectedKeyId = known ? keyId : "ok";
        var label = TableKeys.FirstOrDefault(key => key.Id == _selectedKeyId).Label ?? _selectedKeyId;
        UpdateHotspots();
        SelectedKeyTitle.Text = $"{label} · 选择手势";

        foreach (var (gestureId, _) in GestureLabels)
        {
            _mappingMap.TryGetValue((_selectedKeyId, gestureId), out var mapping);
            var action = mapping?.Action ?? "禁用";
            if (_gestureActionTexts.TryGetValue(gestureId, out var text))
            {
                text.Text = action;
            }
        }
    }

    private async void TestGesture_Click(object sender, RoutedEventArgs e)
    {
        if (!_engineConnected || !_canWrite || sender is not Button { Tag: string gesture })
        {
            return;
        }

        var result = await App.PeripheralService.TestRemoteMappingAsync(_selectedKeyId, gesture);
        SaveStatus.Text = result.Ok
            ? $"已发送测试动作：{gesture}"
            : result.Message ?? "测试失败";
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "app.getsayall.remote-mic.windows");
        if (!Directory.Exists(directory))
        {
            SaveStatus.Text = "配置文件夹尚未创建，请先修改一次映射。";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
        {
            UseShellExecute = true,
        });
    }

    public void OnAppThemeChanged() => UpdateMappingTable();

    private Brush ResourceBrush(string key) => AppTheme.GetBrush(this, key);

    private static string FormatBattery(int? percent, ChargingState charging)
    {
        var text = percent is null ? "—" : $"{percent}%";
        return charging == ChargingState.Charging ? $"{text}  ⚡" : text;
    }
}
