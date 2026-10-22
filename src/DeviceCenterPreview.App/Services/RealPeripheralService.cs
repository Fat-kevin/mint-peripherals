using System.Management;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;
using DeviceCenterPreview.Core.Settings;
using Microsoft.UI.Dispatching;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Models;
using VigilWatt.Core.Storage;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using YuandaoTws.Domain.Enums;
using YuandaoTws.Domain.Models;
using PreviewKind = DeviceCenterPreview.Core.Models.DeviceKind;
using PreviewCharging = DeviceCenterPreview.Core.Models.ChargingState;
using VigilKind = VigilWatt.Core.Models.DeviceKind;
using VigilCharging = VigilWatt.Core.Models.ChargingState;
using NiceAnc = YuandaoTws.Domain.Enums.NoiseCancellingMode;
using NiceEq = YuandaoTws.Domain.Models.EqualizerPreset;
using HostSettings = VigilWatt.Core.Settings.AppSettings;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 真实设备服务：复用 VigilWatt.Core 的 BLE/HID/经典蓝牙电量，再补上
/// 配对但未连接的经典蓝牙设备与 USB/HID 接收器，保证名字与连接状态来自系统。
/// 控制协议由耳机 SPP、鼠标 HID++、遥控器 sidecar 分别接入；所有写入先过设置授权。
/// </summary>
public sealed class RealPeripheralService : IPeripheralService, IDisposable
{
    private DeviceManager? _manager;
    private readonly HistoryStore _history;
    private readonly LogitechMouseService _mouse;
    private readonly RemoteEngineClient _remote;
    private RealHeadsetController? _headsetController;
    private readonly Timer _timer;
    private AppSettings _settings;

    // 隔离开关：额外 WinRT 蓝牙枚举和耳机 SPP 控制器会与 DeviceManager 争用 COM/WinRT 对象；
    // 稳定性验证通过前先关闭，只保留 VigilWatt 已验证的 DeviceManager + PnP 名称。
    private static readonly bool EnableHeadsetController = true;
    private static readonly bool EnableExtraBluetoothEnumeration = false;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<PreviewKind, string> _selected = new();
    private readonly Dictionary<PreviewKind, bool> _userSelected = new();
    private List<RealDevice> _devices = [];
    private List<RealDevice> _pnpCache = [];
    private DateTimeOffset _pnpAt;
    private string _lastSnapshotSignature = "";
    private bool _notificationsPrimed;
    private bool _lastHeadsetConnected;
    private bool _lastMouseConnected;
    private bool _lastRemoteConnected;
    private bool _disposed;

    public event EventHandler? Changed;

    public event EventHandler<RemoteButtonEvent>? RemoteButtonReceived;

    public RealPeripheralService(AppSettings settings)
    {
        _settings = settings.Normalize();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceCenterPreview");
        Directory.CreateDirectory(root);

        _history = new HistoryStore(Path.Combine(root, "history.db"));

        // 先把所有服务实例建好，再启动会立刻触发 Changed 的组件；
        // 否则 RefreshAsync 可能在 _mouse/_remote 赋值前运行（历史 NRE）。
        _remote = new RemoteEngineClient();
        _remote.Changed += (_, _) => _ = RefreshAsync();
        _remote.ButtonEventReceived += (_, edge) => RemoteButtonReceived?.Invoke(this, edge);

        _mouse = new LogitechMouseService();
        _mouse.Changed += (_, _) => _ = RefreshAsync();
        _mouse.SetEnabled(_settings.MouseModuleEnabled);

        if (EnableHeadsetController && _settings.HeadsetModuleEnabled)
        {
            _headsetController = new RealHeadsetController(DispatcherQueue.GetForCurrentThread());
            _headsetController.Changed += (_, _) => _ = RefreshAsync();
            _headsetController.Start();
        }

        if (_settings.BatteryMonitoringEnabled)
        {
            _manager = CreateManager();
            _manager.Start();
        }

        if (_settings.RemoteModuleEnabled)
        {
            _remote.Start();
        }

        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        AppLog.Info("real peripheral service started");
    }

    public void ApplySettings(AppSettings settings)
    {
        var normalized = settings.Normalize();
        var thresholdChanged = normalized.LowBatteryThreshold != _settings.LowBatteryThreshold;
        _settings = normalized;

        _mouse.SetEnabled(normalized.MouseModuleEnabled);

        if (normalized.RemoteModuleEnabled)
        {
            _remote.Start();
        }
        else
        {
            _remote.Stop();
        }

        if (EnableHeadsetController)
        {
            if (normalized.HeadsetModuleEnabled)
            {
                if (_headsetController is null)
                {
                    _headsetController = new RealHeadsetController(DispatcherQueue.GetForCurrentThread());
                    _headsetController.Changed += (_, _) => _ = RefreshAsync();
                }

                _headsetController.Start();
            }
            else
            {
                _headsetController?.Stop();
            }
        }

        if (normalized.BatteryMonitoringEnabled)
        {
            if (_manager is null)
            {
                _manager = CreateManager();
                _manager.Start();
            }
            else if (thresholdChanged)
            {
                // 阈值在 DeviceManager 构造时注入；改了阈值就重建，保证提醒按新值触发。
                _manager.Dispose();
                _manager = CreateManager();
                _manager.Start();
            }
        }
        else if (_manager is not null)
        {
            _manager.Dispose();
            _manager = null;
        }

        _ = RefreshAsync();
    }

    public void RestartRemoteEngine()
    {
        _remote.Stop();
        if (_settings.RemoteModuleEnabled)
        {
            _remote.Start();
        }
    }

    private DeviceManager CreateManager()
    {
        var manager = new DeviceManager(
            new HostSettings { LowBatteryThreshold = _settings.LowBatteryThreshold },
            _history);
        manager.DeviceUpdated += status => _ = RefreshAsync();
        manager.DevicesChanged += () => _ = RefreshAsync();
        manager.LowBatteryDetected += OnLowBatteryDetected;
        return manager;
    }

    private void OnLowBatteryDetected(DeviceStatus status)
    {
        if (!_settings.NotifyOnLowBattery || _settings.LowBatteryThreshold <= 0)
        {
            return;
        }

        if (status.BatteryPercent is not int percent || percent > _settings.LowBatteryThreshold)
        {
            return;
        }

        App.Notify(
            "电量偏低",
            $"{status.DisplayName} 剩余 {percent}%（阈值 {_settings.LowBatteryThreshold}%）",
            NotificationSeverity.Warning);
    }

    public IReadOnlyList<DeviceOption> GetDevices(PreviewKind kind)
    {
        lock (_gate)
        {
            return _devices
                .Where(d => d.Kind == kind)
                .Select(d => new DeviceOption(d.Id, d.Name, d.IsConnected))
                .ToList();
        }
    }

    public string GetSelectedDeviceId(PreviewKind kind)
    {
        lock (_gate)
        {
            return _selected.TryGetValue(kind, out var id) ? id : "";
        }
    }

    public void SelectDevice(PreviewKind kind, string deviceId)
    {
        lock (_gate)
        {
            if (_devices.All(d => d.Kind != kind || d.Id != deviceId))
            {
                return;
            }

            _selected[kind] = deviceId;
            _userSelected[kind] = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public PeripheralSnapshot GetSnapshot(PreviewKind kind)
    {
        if (!IsModuleEnabled(kind))
        {
            return new PeripheralSnapshot(
                kind,
                "",
                ModuleLabel(kind),
                false,
                null,
                PreviewCharging.Unknown,
                [],
                "已在设置中关闭",
                false,
                "在“设置 → 模块”中开启后会重新扫描。",
                null,
                false);
        }

        RealDevice? device;
        lock (_gate)
        {
            var selected = _selected.TryGetValue(kind, out var id) ? id : "";
            device = _devices.FirstOrDefault(d => d.Kind == kind && d.Id == selected);
        }

        if (device is null)
        {
            return new PeripheralSnapshot(
                kind,
                "",
                "未发现设备",
                false,
                null,
                PreviewCharging.Unknown,
                [],
                kind switch
                {
                    PreviewKind.Headset => "未发现耳机；请先在 Windows 蓝牙中配对并连接",
                    PreviewKind.Mouse => "未发现鼠标；请唤醒鼠标或检查接收器",
                    PreviewKind.Remote => "未发现遥控器；请先在 Windows 蓝牙中配对并连接",
                    _ => "",
                },
                false,
                Hint(kind),
                null,
                IsWriteEnabled(kind));
        }

        return new PeripheralSnapshot(
            kind,
            device.Id,
            device.Name,
            device.IsConnected,
            device.BatteryPercent,
            device.Charging,
            device.Components ?? [],
            device.Status,
            device.ControlsAvailable,
            device.ControlHint,
            device.Firmware,
            IsWriteEnabled(kind));
    }

    private bool IsModuleEnabled(PreviewKind kind) => kind switch
    {
        PreviewKind.Headset => _settings.HeadsetModuleEnabled,
        PreviewKind.Mouse => _settings.MouseModuleEnabled,
        PreviewKind.Remote => _settings.RemoteModuleEnabled,
        _ => false,
    };

    private bool IsWriteEnabled(PreviewKind kind) => kind switch
    {
        PreviewKind.Headset => _settings.HeadsetWriteEnabled,
        PreviewKind.Mouse => _settings.MouseWriteEnabled,
        PreviewKind.Remote => _settings.RemoteWriteEnabled,
        _ => false,
    };

    private static string ModuleLabel(PreviewKind kind) => kind switch
    {
        PreviewKind.Headset => "耳机",
        PreviewKind.Mouse => "鼠标",
        PreviewKind.Remote => "遥控器",
        _ => "设备",
    };

    public HeadsetControls GetHeadsetControls()
    {
        if (!_settings.HeadsetModuleEnabled || _headsetController is null)
        {
            return HeadsetControls.Default;
        }

        var state = _headsetController.State;
        var known = state.AncMode is not null ||
                    state.Equalizer is not null ||
                    state.GameModeEnabled is not null ||
                    state.LowLatencyEnabled is not null ||
                    state.DualConnectionEnabled is not null ||
                    state.InEarDetectionEnabled is not null ||
                    state.WindSuppressionEnabled is not null;
        if (!known || _headsetController.ConnectionState != HeadsetConnectionState.Connected)
        {
            return HeadsetControls.Default;
        }

        return new HeadsetControls(
            ToPreviewAnc(state.AncMode) ?? AncMode.Off,
            ToPreviewEq(state.Equalizer) ?? EqPreset.Balanced,
            state.GameModeEnabled ?? false,
            state.LowLatencyEnabled ?? false,
            state.DualConnectionEnabled ?? false,
            state.InEarDetectionEnabled ?? false,
            state.WindSuppressionEnabled ?? false,
            IsKnown: true);
    }

    public async Task<WriteResult> UpdateHeadsetControlsAsync(HeadsetControls controls)
    {
        if (!_settings.HeadsetModuleEnabled)
        {
            return WriteResult.Fail("耳机模块已在设置中关闭。");
        }

        if (!_settings.HeadsetWriteEnabled)
        {
            return NotifyWriteFailure("耳机", WriteResult.Fail("耳机写入已在设置中关闭。"));
        }

        if (_headsetController is null)
        {
            return WriteResult.Fail("耳机控制未启动。");
        }

        var result = await _headsetController.ApplyAsync(controls);
        return NotifyWriteFailure("耳机", result);
    }

    public Task FindHeadsetAsync(bool enabled)
    {
        if (!_settings.HeadsetModuleEnabled || !_settings.HeadsetWriteEnabled)
        {
            return Task.CompletedTask;
        }

        return _headsetController?.FindAsync(enabled) ?? Task.CompletedTask;
    }

    public MouseControls GetMouseControls()
    {
        return _mouse.Controls;
    }

    public Task<WriteResult> UpdateMouseControlsAsync(MouseControls controls)
    {
        if (!_settings.MouseModuleEnabled)
        {
            return Task.FromResult(WriteResult.Fail("鼠标模块已在设置中关闭。"));
        }

        if (!_settings.MouseWriteEnabled)
        {
            return Task.FromResult(NotifyWriteFailure("鼠标", WriteResult.Fail("鼠标写入已在设置中关闭。")));
        }

        return ApplyMouseAsync(controls);
    }

    private async Task<WriteResult> ApplyMouseAsync(MouseControls controls)
    {
        var result = await _mouse.ApplyAsync(controls);
        return NotifyWriteFailure("鼠标", result);
    }

    public Task RefreshMouseAsync() => Task.Run(_mouse.Refresh);

    public RemoteControls GetRemoteControls()
    {
        var state = _remote.State;
        var id = state.Connected ? "sayall:" + state.DeviceToken : GetSelectedDeviceId(PreviewKind.Remote);
        return new RemoteControls(
            id,
            state.Connected,
            false,
            [],
            state.Connected,
            string.IsNullOrWhiteSpace(state.AudioEndpointId) ? null : state.AudioEndpointId,
            string.IsNullOrWhiteSpace(state.AudioEndpointName) ? null : state.AudioEndpointName,
            null,
            null,
            state.RawInputPhase,
            state.MatchedDeviceCount,
            state.RawEventCount,
            state.FiredGestures,
            state.GateActive,
            state.SemanticEdgeCount,
            state.LastButton,
            state.LastPressed,
            state.ActiveButtons,
            state.MappingsEnabled,
            state.ListenerActive);
    }

    public void UpdateRemoteControls(RemoteControls controls)
    {
        // 语音/按键映射写入通过 SetRemote*Async 显式调用。
    }

    public Task<IReadOnlyList<AudioEndpointInfo>> GetRemoteAudioEndpointsAsync() =>
        _settings.RemoteModuleEnabled ? _remote.GetAudioEndpointsAsync() : Task.FromResult<IReadOnlyList<AudioEndpointInfo>>([]);

    public Task<IReadOnlyList<KeyMapping>> GetRemoteMappingsAsync() =>
        _settings.RemoteModuleEnabled ? _remote.GetKeyMappingsAsync() : Task.FromResult<IReadOnlyList<KeyMapping>>([]);

    public Task<IReadOnlyList<RemoteDeviceInfo>> ScanRemotesAsync() =>
        _settings.RemoteModuleEnabled
            ? _remote.ScanRemotesAsync()
            : Task.FromResult<IReadOnlyList<RemoteDeviceInfo>>([]);

    public Task<WriteResult> ConnectRemoteAsync(string deviceId) =>
        _settings.RemoteModuleEnabled
            ? _remote.ConnectRemoteAsync(deviceId)
            : Task.FromResult(WriteResult.Fail("遥控器模块已在设置中关闭。"));

    public Task<WriteResult> DisconnectRemoteAsync() =>
        _settings.RemoteModuleEnabled
            ? _remote.DisconnectRemoteAsync()
            : Task.FromResult(WriteResult.Fail("遥控器模块已在设置中关闭。"));

    public Task<WriteResult> SetRemoteAudioEndpointAsync(string endpointId) =>
        RemoteWrite(() => _remote.SelectAudioEndpointAsync(endpointId));

    public Task<WriteResult> SetRemoteGainAsync(int gainDb) =>
        RemoteWrite(() => _remote.SetGainAsync(gainDb));

    public Task<WriteResult> SetRemoteVoiceToolAsync(string tool) =>
        RemoteWrite(() => _remote.SetVoiceToolAsync(tool));

    public Task<string?> GetRemoteVoiceToolAsync() =>
        _settings.RemoteModuleEnabled ? _remote.GetVoiceToolAsync() : Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> GetRemoteHotkeyAsync() =>
        _settings.RemoteModuleEnabled
            ? _remote.GetVoiceHotkeyAsync()
            : Task.FromResult<IReadOnlyList<string>>([]);

    public Task<WriteResult> SaveRemoteMappingAsync(string key, string gesture, string actionJson) =>
        RemoteWrite(() => _remote.SaveMappingAsync(key, gesture, actionJson));

    public Task<WriteResult> TestRemoteMappingAsync(string key, string gesture) =>
        RemoteWrite(() => _remote.TestMappingAsync(key, gesture));

    public Task<WriteResult> SetRemoteMappingSuspensionAsync(bool suspended) =>
        RemoteWrite(() => _remote.SetMappingSuspensionAsync(suspended));

    public Task<WriteResult> SetRemoteFullKeyAsync(bool enabled) =>
        RemoteWrite(() => _remote.SetFullKeyAsync(enabled));

    public Task<RemoteFullKeyStatus> GetRemoteFullKeyStatusAsync() =>
        _settings.RemoteModuleEnabled
            ? _remote.GetFullKeyStatusAsync()
            : Task.FromResult(new RemoteFullKeyStatus(false, false, "遥控器模块已在设置中关闭。"));

    private async Task<WriteResult> RemoteWrite(Func<Task<WriteResult>> action)
    {
        if (!_settings.RemoteModuleEnabled)
        {
            return WriteResult.Fail("遥控器模块已在设置中关闭。");
        }

        if (!_settings.RemoteWriteEnabled)
        {
            return NotifyWriteFailure("遥控器", WriteResult.Fail("遥控器写入已在设置中关闭。"));
        }

        var result = await action();
        return NotifyWriteFailure("遥控器", result);
    }

    private WriteResult NotifyWriteFailure(string target, WriteResult result)
    {
        if (!result.Ok && _settings.NotifyOnWriteFailure)
        {
            App.Notify(
                $"{target}写入失败",
                result.Message ?? "设备没有接受这次写入。",
                NotificationSeverity.Error);
        }

        return result;
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var devices = new List<RealDevice>();

            if (_manager is not null)
            {
                foreach (var status in _manager.Devices)
                {
                    var kind = Classify(status.Name, status.Kind);
                    if (kind is null)
                    {
                        continue;
                    }

                    devices.Add(new RealDevice(
                        kind.Value,
                        status.Id,
                        status.Name,
                        status.IsOnline,
                        status.IsOnline ? status.BatteryPercent : null,
                        status.IsOnline ? ToPreview(status.Charging) : PreviewCharging.Unknown,
                        status.IsOnline ? "已连接" : "未连接",
                        status.Protocol,
                        false,
                        Hint(kind.Value)));
                }
            }

            if (EnableExtraBluetoothEnumeration)
            {
                await AddPairedClassicAsync(devices);
                await AddBleAsync(devices);
            }

            AddPnpDevices(devices);
            AddHeadsetDevice(devices);
            AddMouseDevice(devices);
            AddRemoteDevice(devices);

            if (_mouse.State.Connected)
            {
                devices.RemoveAll(d => d.Kind == PreviewKind.Mouse &&
                    (d.Name.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
                     d.Name.Contains("HID-compliant mouse", StringComparison.OrdinalIgnoreCase)));
            }

            lock (_gate)
            {
                _devices = Deduplicate(devices);
                EnsureSelections();
                CheckConnectionNotifications(_devices);
            }

            LogSnapshotIfChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task AddPairedClassicAsync(List<RealDevice> devices)
    {
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
            foreach (var info in infos)
            {
                var kind = Classify(info.Name, VigilKind.Unknown);
                if (kind is null)
                {
                    continue;
                }

                bool connected = false;
                try
                {
                    using var bt = await BluetoothDevice.FromIdAsync(info.Id);
                    connected = bt?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch
                {
                    // 单个设备查询失败不影响其他设备。
                }

                devices.Add(new RealDevice(
                    kind.Value,
                    "bt:" + info.Id,
                    info.Name,
                    connected,
                    null,
                    PreviewCharging.Unknown,
                    connected ? "已连接" : "已配对，未连接",
                    "经典蓝牙",
                    false,
                    Hint(kind.Value)));
            }
        }
        catch
        {
            // 蓝牙枚举失败时保留其他来源。
        }
    }

    private static async Task AddBleAsync(List<RealDevice> devices)
    {
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
            foreach (var info in infos)
            {
                var kind = Classify(info.Name, VigilKind.Unknown);
                if (kind is null)
                {
                    continue;
                }

                bool connected = false;
                try
                {
                    using var ble = await BluetoothLEDevice.FromIdAsync(info.Id);
                    connected = ble?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch
                {
                    // 单个设备查询失败不影响其他设备。
                }

                devices.Add(new RealDevice(
                    kind.Value,
                    "ble:" + info.Id,
                    info.Name,
                    connected,
                    null,
                    PreviewCharging.Unknown,
                    connected ? "已连接" : "已配对，未连接",
                    "BLE",
                    false,
                    Hint(kind.Value)));
            }
        }
        catch
        {
            // BLE 枚举失败时保留其他来源。
        }
    }

    private void AddPnpDevices(List<RealDevice> devices)
    {
        if (_pnpCache.Count > 0 && DateTimeOffset.Now - _pnpAt < TimeSpan.FromSeconds(30))
        {
            devices.AddRange(_pnpCache);
            return;
        }

        _pnpCache = QueryPnpDevices();
        _pnpAt = DateTimeOffset.Now;
        devices.AddRange(_pnpCache);
    }

    private static List<RealDevice> QueryPnpDevices()
    {
        var found = new List<RealDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Status, PNPClass FROM Win32_PnPEntity");
            using var results = searcher.Get();
            foreach (ManagementObject entity in results)
            {
                using (entity)
                {
                    var name = entity["Name"]?.ToString() ?? "";
                    var id = entity["DeviceID"]?.ToString() ?? "";
                    var pnpClass = entity["PNPClass"]?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    // 音频端点/媒体类只是设备的副产物，名称里还带“耳机”，会把真正的设备条目挤掉。
                    if (pnpClass is "AudioEndpoint" or "MEDIA" or "SoftwareDevice" or "System" ||
                        id.StartsWith("SWD\\MMDEVAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 服务子节点会重复父设备名；只保留产品级节点。
                    if (name.Contains("Avrcp", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("服务", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Bluetooth Device", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Microsoft 蓝牙", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var kind = Classify(name, VigilKind.Unknown);
                    if (kind is null)
                    {
                        continue;
                    }

                    var status = kind switch
                    {
                        PreviewKind.Headset => "系统已识别（未确认连接）",
                        PreviewKind.Mouse => "接收器/设备已识别（未确认连接）",
                        PreviewKind.Remote => "系统已识别（未确认连接）",
                        _ => "系统已识别",
                    };

                    found.Add(new RealDevice(
                        kind.Value,
                        "pnp:" + id,
                        name,
                        false,
                        null,
                        PreviewCharging.Unknown,
                        status,
                        "PnP",
                        false,
                        Hint(kind.Value)));
                }
            }
        }
        catch
        {
            // WMI 不可用时保留其他来源。
        }

        return found;
    }

    private void AddMouseDevice(List<RealDevice> devices)
    {
        if (!_settings.MouseModuleEnabled)
        {
            return;
        }

        var state = _mouse?.State;
        if (state is null || !state.Connected)
        {
            return;
        }

        devices.Add(new RealDevice(
            PreviewKind.Mouse,
            state.Id,
            string.IsNullOrWhiteSpace(state.Name) ? "罗技鼠标" : state.Name,
            true,
            state.BatteryPercent,
            state.Charging,
            state.Status,
            "Logitech HID++",
            state.ControlsAvailable,
            ControlHintFor(PreviewKind.Mouse, state.ControlsAvailable),
            null,
            state.Firmware));
    }

    private void AddRemoteDevice(List<RealDevice> devices)
    {
        if (!_settings.RemoteModuleEnabled)
        {
            return;
        }

        var state = _remote?.State;
        if (state is null || !state.Connected)
        {
            return;
        }

        devices.Add(new RealDevice(
            PreviewKind.Remote,
            "sayall:" + state.DeviceToken,
            string.IsNullOrWhiteSpace(state.Name) ? "遥控器" : state.Name,
            true,
            state.BatteryPercent,
            PreviewCharging.Unknown,
            $"已连接 · 引擎就绪（{state.Model}）",
            "SayAll",
            true,
            ControlHintFor(PreviewKind.Remote, true)));
    }

    private void AddHeadsetDevice(List<RealDevice> devices)
    {
        if (!_settings.HeadsetModuleEnabled || _headsetController is null)
        {
            return;
        }

        var device = _headsetController.Device;
        if (device is null)
        {
            return;
        }

        var state = _headsetController.State;
        var battery = state.Battery;
        var components = BuildHeadsetComponents(battery);
        var main = battery is null
            ? null
            : (int?)(battery.LeftEarPercent ?? battery.RightEarPercent ?? battery.CasePercent);
        var connected = _headsetController.ConnectionState == HeadsetConnectionState.Connected;

        devices.Add(new RealDevice(
            PreviewKind.Headset,
            "nicehck:" + device.Address,
            device.Name,
            connected,
            connected ? main : null,
            connected ? ToPreview(battery) : PreviewCharging.Unknown,
            _headsetController.Status,
            "NiceHCK SPP",
            _headsetController.ControlsAvailable,
            ControlHintFor(PreviewKind.Headset, _headsetController.ControlsAvailable),
            components,
            state.Firmware?.ToString()));
    }

    private string ControlHintFor(PreviewKind kind, bool controlsAvailable)
    {
        if (!controlsAvailable)
        {
            return Hint(kind);
        }

        return IsWriteEnabled(kind)
            ? ""
            : "写入已在设置中关闭；当前只能查看，不能修改设备。";
    }

    private static IReadOnlyList<DeviceComponentBattery> BuildHeadsetComponents(BatteryInfo? battery)
    {
        if (battery is null)
        {
            return [];
        }

        var components = new List<DeviceComponentBattery>();
        if (battery.LeftEarPercent is byte left)
        {
            components.Add(new DeviceComponentBattery(
                "left",
                "左耳",
                left,
                ToChargeState(battery.IsLeftEarCharging)));
        }

        if (battery.RightEarPercent is byte right)
        {
            components.Add(new DeviceComponentBattery(
                "right",
                "右耳",
                right,
                ToChargeState(battery.IsRightEarCharging)));
        }

        if (battery.CasePercent is byte casePercent)
        {
            components.Add(new DeviceComponentBattery(
                "case",
                "充电盒",
                casePercent,
                ToChargeState(battery.IsCaseCharging)));
        }

        return components;
    }

    private static PreviewCharging ToChargeState(bool? charging) => charging switch
    {
        true => PreviewCharging.Charging,
        false => PreviewCharging.Discharging,
        null => PreviewCharging.Unknown,
    };

    private static PreviewCharging ToPreview(BatteryInfo? battery)
    {
        if (battery is null)
        {
            return PreviewCharging.Unknown;
        }

        var flags = new[] { battery.IsLeftEarCharging, battery.IsRightEarCharging, battery.IsCaseCharging };
        if (flags.Any(flag => flag == true))
        {
            return PreviewCharging.Charging;
        }

        return flags.Any(flag => flag == false)
            ? PreviewCharging.Discharging
            : PreviewCharging.Unknown;
    }

    private static AncMode? ToPreviewAnc(NiceAnc? mode) => mode switch
    {
        NiceAnc.Off => AncMode.Off,
        NiceAnc.Transparency => AncMode.Transparency,
        NiceAnc.Normal => AncMode.Normal,
        NiceAnc.Deep => AncMode.Deep,
        NiceAnc.Experimental => AncMode.Experimental,
        NiceAnc.WindSuppression => AncMode.WindNoise,
        _ => null,
    };

    private static EqPreset? ToPreviewEq(NiceEq? preset) => preset switch
    {
        NiceEq.Blue => EqPreset.Tears,
        NiceEq.Balanced => EqPreset.Balanced,
        NiceEq.Bass => EqPreset.Western,
        NiceEq.Pure => EqPreset.TrueTone,
        NiceEq.Game => EqPreset.Gaming,
        NiceEq.Fine => EqPreset.Delicate,
        NiceEq.Vocal => EqPreset.WarmVocal,
        _ => null,
    };

    private static List<RealDevice> Deduplicate(List<RealDevice> devices)
    {
        var result = new List<RealDevice>();
        foreach (var device in devices)
        {
            var existingIndex = result.FindIndex(d =>
                d.Kind == device.Kind &&
                string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                result.Add(device);
                continue;
            }

            // 同一台设备可能同时来自 DeviceManager 与 SPP：必须字段级合并，
            // 否则保留主电量条目会丢掉分量、固件与 ControlsAvailable。
            result[existingIndex] = Merge(result[existingIndex], device);
        }

        return result;
    }

    private static RealDevice Merge(RealDevice existing, RealDevice incoming)
    {
        var primary = incoming.ControlsAvailable ||
                      (incoming.Source == "SayAll" && incoming.IsConnected && !existing.ControlsAvailable) ||
                      (!existing.ControlsAvailable && incoming.IsConnected)
            ? incoming
            : existing;
        var secondary = ReferenceEquals(primary, incoming) ? existing : incoming;
        var controlsAvailable = existing.ControlsAvailable || incoming.ControlsAvailable;

        var hint = controlsAvailable
            ? (primary.ControlsAvailable ? primary.ControlHint : secondary.ControlHint)
            : (primary.ControlHint ?? secondary.ControlHint);

        return new RealDevice(
            primary.Kind,
            primary.Id,
            primary.Name,
            existing.IsConnected || incoming.IsConnected,
            primary.BatteryPercent ?? secondary.BatteryPercent,
            primary.Charging != PreviewCharging.Unknown ? primary.Charging : secondary.Charging,
            primary.IsConnected ? primary.Status : secondary.Status,
            primary.Source,
            controlsAvailable,
            hint,
            primary.Components is { Count: > 0 } ? primary.Components : secondary.Components,
            primary.Firmware ?? secondary.Firmware);
    }

    private void CheckConnectionNotifications(List<RealDevice> devices)
    {
        var headset = devices.FirstOrDefault(d => d.Kind == PreviewKind.Headset);
        var mouse = devices.FirstOrDefault(d => d.Kind == PreviewKind.Mouse);
        var remote = devices.FirstOrDefault(d => d.Kind == PreviewKind.Remote);

        var headsetConnected = headset?.IsConnected == true;
        var mouseConnected = mouse?.IsConnected == true;
        var remoteConnected = remote?.IsConnected == true;

        if (!_notificationsPrimed)
        {
            _notificationsPrimed = true;
            _lastHeadsetConnected = headsetConnected;
            _lastMouseConnected = mouseConnected;
            _lastRemoteConnected = remoteConnected;
            return;
        }

        if (_settings.NotifyOnConnect)
        {
            if (headsetConnected && !_lastHeadsetConnected)
            {
                App.Notify("耳机已连接", headset?.Name ?? "耳机", NotificationSeverity.Success);
            }

            if (mouseConnected && !_lastMouseConnected)
            {
                App.Notify("鼠标已连接", mouse?.Name ?? "鼠标", NotificationSeverity.Success);
            }

            if (remoteConnected && !_lastRemoteConnected)
            {
                App.Notify("遥控器已连接", remote?.Name ?? "遥控器", NotificationSeverity.Success);
            }
        }

        _lastHeadsetConnected = headsetConnected;
        _lastMouseConnected = mouseConnected;
        _lastRemoteConnected = remoteConnected;
    }

    private void LogSnapshotIfChanged()
    {
        string signature;
        lock (_gate)
        {
            signature = string.Join(
                " | ",
                _devices.Select(d =>
                    $"{d.Kind}:{d.Name}:{(d.IsConnected ? "on" : "off")}:{d.BatteryPercent?.ToString() ?? "-"}:{d.Components?.Count ?? 0}:{d.ControlsAvailable}:{d.Firmware ?? "-"}"));
        }

        if (signature == _lastSnapshotSignature)
        {
            return;
        }

        _lastSnapshotSignature = signature;
        AppLog.Info("devices " + signature);
    }

    private void EnsureSelections()
    {
        foreach (var kind in Enum.GetValues<PreviewKind>())
        {
            var options = _devices.Where(d => d.Kind == kind).ToList();
            if (options.Count == 0)
            {
                _selected.Remove(kind);
                _userSelected.Remove(kind);
                continue;
            }

            // 用户手动选择过且设备仍在：保持选择；否则自动选“连接/控件/电量/分量”最好的。
            if (_userSelected.TryGetValue(kind, out var pinned) && pinned &&
                _selected.TryGetValue(kind, out var currentId) &&
                options.Any(d => d.Id == currentId))
            {
                continue;
            }

            _selected[kind] = options
                .OrderByDescending(d => d.IsConnected)
                .ThenByDescending(d => d.ControlsAvailable)
                .ThenByDescending(d => d.BatteryPercent.HasValue)
                .ThenByDescending(d => d.Components?.Count ?? 0)
                .First()
                .Id;
            _userSelected[kind] = false;
        }
    }

    private static PreviewKind? Classify(string name, VigilKind kind)
    {
        if (kind == VigilKind.Headset ||
            ContainsAny(name, "YUANDAO", "OriG", "耳机", "Headset"))
        {
            return PreviewKind.Headset;
        }

        if (ContainsAny(name, "遥控器", "Remote", "MI RC", "RC001", "RC003", "小米蓝牙语音"))
        {
            return PreviewKind.Remote;
        }

        if (kind == VigilKind.Mouse ||
            ContainsAny(name, "LIGHTSPEED", "Mouse", "鼠标", "MX Master", "Rapoo", "雷柏", "Inphic", "英菲克", "Receiver", "接收器"))
        {
            return PreviewKind.Mouse;
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(c => value.Contains(c, StringComparison.OrdinalIgnoreCase));

    private static PreviewCharging ToPreview(VigilCharging charging) => charging switch
    {
        VigilCharging.Charging => PreviewCharging.Charging,
        VigilCharging.Full => PreviewCharging.Full,
        VigilCharging.Discharging => PreviewCharging.Discharging,
        _ => PreviewCharging.Unknown,
    };

    private static string Hint(PreviewKind kind) => kind switch
    {
        PreviewKind.Headset => "耳机已配对；连接后自动建立 SPP 控制通道（ANC/EQ/分量电量）。若未连接，请先在 Windows 蓝牙中连接。",
        PreviewKind.Mouse => "鼠标已识别；仅对已验证的罗技 HID++ 型号开放写入，其余型号保持只读。",
        PreviewKind.Remote => "遥控器已识别；引擎启动后可语音与按键映射。全按键 Helper 当前版本未集成。",
        _ => "",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _headsetController?.Dispose();
        _mouse.Dispose();
        _remote.Dispose();
        _manager?.Dispose();
        _history.Dispose();
        _refreshGate.Dispose();
    }

    private sealed record RealDevice(
        PreviewKind Kind,
        string Id,
        string Name,
        bool IsConnected,
        int? BatteryPercent,
        PreviewCharging Charging,
        string Status,
        string Source,
        bool ControlsAvailable,
        string ControlHint,
        IReadOnlyList<DeviceComponentBattery>? Components = null,
        string? Firmware = null);
}
