using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Settings;

namespace DeviceCenterPreview.Core.Services;

/// <summary>
/// 预览版的内存实现：UI 与数据流可跑通，但不访问真实硬件。
/// 后续用 Headset/Mouse/Remote 集成替换同名接口即可。
/// </summary>
public sealed class MockPeripheralService : IPeripheralService
{
    private static readonly DeviceOption[] HeadsetDevices =
    [
        new("headset:yuandao", "原道原点", true),
    ];

    private static readonly DeviceOption[] MouseDevices =
    [
        new("mouse:logitech:mx3s", "罗技 MX Master 3S", true),
        new("mouse:rapoo:vt3", "雷柏 VT3 二代", true),
        new("mouse:inphic:zhen", "英菲克 ZHEN", false),
    ];

    private static readonly DeviceOption[] RemoteDevices =
    [
        new("remote:rc003", "小米 RC003", true),
        new("remote:rc001", "小米 RC001", false),
    ];

    private static readonly Dictionary<string, (int? Battery, ChargingState Charging, string Status)> MouseState =
        new()
        {
            ["mouse:logitech:mx3s"] = (65, ChargingState.Charging, "已连接 · 1000 Hz"),
            ["mouse:rapoo:vt3"] = (45, ChargingState.Discharging, "已连接 · 被动状态帧"),
            ["mouse:inphic:zhen"] = (30, ChargingState.Discharging, "未连接 · 最后已知"),
        };

    private static readonly Dictionary<string, (int? Battery, ChargingState Charging, string Status)> RemoteState =
        new()
        {
            ["remote:rc003"] = (70, ChargingState.Discharging, "已连接 · 语音就绪"),
            ["remote:rc001"] = (55, ChargingState.Discharging, "未连接 · 最后已知"),
        };

    private static readonly IReadOnlyList<KeyMapping> DefaultRemoteKeys =
    [
        new("返回", "单击", "浏览器后退"),
        new("确定", "单击", "回车"),
        new("TV", "长按", "打开电视应用"),
        new("主页", "单击", "Win+D"),
        new("上", "单击", "方向上"),
        new("下", "单击", "方向下"),
        new("左", "单击", "方向左"),
        new("右", "单击", "方向右"),
        new("菜单", "单击", "右键菜单"),
        new("电源", "长按", "锁屏"),
        new("静音", "单击", "系统静音"),
        new("音量+", "单击", "音量加"),
        new("音量-", "单击", "音量减"),
    ];

    private readonly Dictionary<DeviceKind, string> _selected = new()
    {
        [DeviceKind.Headset] = HeadsetDevices[0].Id,
        [DeviceKind.Mouse] = MouseDevices[0].Id,
        [DeviceKind.Remote] = RemoteDevices[0].Id,
    };

    private readonly Dictionary<string, MouseControls> _mouseControls = new();
    private readonly Dictionary<string, RemoteControls> _remoteControls = new();
    private HeadsetControls _headsetControls = HeadsetControls.Default;

    public event EventHandler? Changed;

    public event EventHandler<RemoteButtonEvent>? RemoteButtonReceived
    {
        add { }
        remove { }
    }

    public IReadOnlyList<DeviceOption> GetDevices(DeviceKind kind) => kind switch
    {
        DeviceKind.Headset => HeadsetDevices,
        DeviceKind.Mouse => MouseDevices,
        DeviceKind.Remote => RemoteDevices,
        _ => [],
    };

    public string GetSelectedDeviceId(DeviceKind kind) => _selected[kind];

    public void SelectDevice(DeviceKind kind, string deviceId)
    {
        if (!GetDevices(kind).Any(d => d.Id == deviceId))
        {
            throw new ArgumentException($"Unknown {kind} device: {deviceId}", nameof(deviceId));
        }

        _selected[kind] = deviceId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public PeripheralSnapshot GetSnapshot(DeviceKind kind) => kind switch
    {
        DeviceKind.Headset => GetHeadsetSnapshot(),
        DeviceKind.Mouse => GetMouseSnapshot(),
        DeviceKind.Remote => GetRemoteSnapshot(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public void ApplySettings(AppSettings settings)
    {
    }

    public HeadsetControls GetHeadsetControls() => _headsetControls;

    public Task<WriteResult> UpdateHeadsetControlsAsync(HeadsetControls controls)
    {
        _headsetControls = controls;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(WriteResult.Success);
    }

    public MouseControls GetMouseControls()
    {
        var id = _selected[DeviceKind.Mouse];
        if (!_mouseControls.TryGetValue(id, out var controls))
        {
            controls = new MouseControls(
                id,
                Dpi: 1600,
                PollingRateHz: 1000,
                Buttons:
                [
                    new("左键", "左键点击"),
                    new("右键", "右键点击"),
                    new("中键", "中键点击"),
                    new("侧键 1", "浏览器后退"),
                    new("侧键 2", "浏览器前进"),
                ],
                WriteSupported: true);
            _mouseControls[id] = controls;
        }

        return controls;
    }

    public Task<WriteResult> UpdateMouseControlsAsync(MouseControls controls)
    {
        _mouseControls[controls.DeviceId] = controls;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(WriteResult.Success);
    }

    public Task RefreshMouseAsync() => Task.CompletedTask;

    public RemoteControls GetRemoteControls()
    {
        var id = _selected[DeviceKind.Remote];
        if (!_remoteControls.TryGetValue(id, out var controls))
        {
            controls = new RemoteControls(
                id,
                VoiceReady: true,
                FullKeySupport: false,
                Keys: DefaultRemoteKeys);
            _remoteControls[id] = controls;
        }

        return controls;
    }

    public void UpdateRemoteControls(RemoteControls controls)
    {
        _remoteControls[controls.DeviceId] = controls;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task FindHeadsetAsync(bool enabled) => Task.CompletedTask;

    public Task<IReadOnlyList<RemoteDeviceInfo>> ScanRemotesAsync() =>
        Task.FromResult<IReadOnlyList<RemoteDeviceInfo>>(
        [
            new("mock:rc003", "rc003", "小米蓝牙语音遥控器 2 Pro", "RC003", true),
            new("mock:rc001", "rc001", "小米蓝牙语音遥控器 2", "RC001", true),
        ]);

    public Task<WriteResult> ConnectRemoteAsync(string deviceId) => Task.FromResult(WriteResult.Success);

    public Task<WriteResult> DisconnectRemoteAsync() => Task.FromResult(WriteResult.Success);

    public Task<IReadOnlyList<AudioEndpointInfo>> GetRemoteAudioEndpointsAsync() =>
        Task.FromResult<IReadOnlyList<AudioEndpointInfo>>([]);

    public Task<IReadOnlyList<KeyMapping>> GetRemoteMappingsAsync() =>
        Task.FromResult<IReadOnlyList<KeyMapping>>([]);

    public Task<WriteResult> SetRemoteAudioEndpointAsync(string endpointId) =>
        Task.FromResult(WriteResult.Success);

    public Task<WriteResult> SetRemoteGainAsync(int gainDb) =>
        Task.FromResult(WriteResult.Success);

    public Task<WriteResult> SetRemoteVoiceToolAsync(string tool) =>
        Task.FromResult(WriteResult.Success);

    public Task<string?> GetRemoteVoiceToolAsync() => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> GetRemoteHotkeyAsync() =>
        Task.FromResult<IReadOnlyList<string>>(["right_alt"]);

    public Task<WriteResult> SaveRemoteMappingAsync(string key, string gesture, string actionJson) =>
        Task.FromResult(WriteResult.Success);

    public Task<WriteResult> TestRemoteMappingAsync(string key, string gesture) =>
        Task.FromResult(WriteResult.Success);

    public Task<WriteResult> SetRemoteMappingSuspensionAsync(bool suspended) =>
        Task.FromResult(WriteResult.Success);

    public Task<WriteResult> SetRemoteFullKeyAsync(bool enabled) =>
        Task.FromResult(WriteResult.Fail("模拟服务不提供全按键 Helper"));

    public Task<RemoteFullKeyStatus> GetRemoteFullKeyStatusAsync() =>
        Task.FromResult(new RemoteFullKeyStatus(false, false, "模拟服务不提供全按键 Helper"));

    private PeripheralSnapshot GetHeadsetSnapshot()
    {
        var components = new List<DeviceComponentBattery>
        {
            new("left", "左耳", 82, ChargingState.Charging),
            new("right", "右耳", 80, ChargingState.Discharging),
            new("case", "充电盒", 95, ChargingState.Discharging),
        };

        var main = components
            .Where(c => c.Key is "left" or "right")
            .Select(c => c.Percent)
            .Where(p => p.HasValue)
            .Min();

        var charging = components.Any(c => c.Charging == ChargingState.Charging)
            ? ChargingState.Charging
            : ChargingState.Discharging;

        return new PeripheralSnapshot(
            DeviceKind.Headset,
            HeadsetDevices[0].Id,
            HeadsetDevices[0].Name,
            IsConnected: true,
            BatteryPercent: main,
            charging,
            components,
            $"已连接 · ANC {PeripheralDisplayNames.Anc(_headsetControls.Anc)}",
            ControlsAvailable: true);
    }

    private PeripheralSnapshot GetMouseSnapshot()
    {
        var id = _selected[DeviceKind.Mouse];
        var device = MouseDevices.First(d => d.Id == id);
        var state = MouseState[id];
        return new PeripheralSnapshot(
            DeviceKind.Mouse,
            id,
            device.Name,
            device.IsConnected,
            state.Battery,
            state.Charging,
            [],
            state.Status,
            ControlsAvailable: true);
    }

    private PeripheralSnapshot GetRemoteSnapshot()
    {
        var id = _selected[DeviceKind.Remote];
        var device = RemoteDevices.First(d => d.Id == id);
        var state = RemoteState[id];
        return new PeripheralSnapshot(
            DeviceKind.Remote,
            id,
            device.Name,
            device.IsConnected,
            state.Battery,
            state.Charging,
            [],
            state.Status,
            ControlsAvailable: true);
    }
}
