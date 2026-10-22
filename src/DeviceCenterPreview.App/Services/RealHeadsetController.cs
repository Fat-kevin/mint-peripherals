using System.Reactive.Linq;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using YuandaoTws.Application.Services;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Enums;
using YuandaoTws.Domain.Models;
using YuandaoTws.Infrastructure.Bluetooth;
using YuandaoTws.Infrastructure.Protocols;
using NiceAnc = YuandaoTws.Domain.Enums.NoiseCancellingMode;
using NiceEq = YuandaoTws.Domain.Models.EqualizerPreset;
using PreviewAnc = DeviceCenterPreview.Core.Models.AncMode;
using PreviewEq = DeviceCenterPreview.Core.Models.EqPreset;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 耳机真实控制：复用 NICEHCK_OriG-in 的 SPP 连接/命令管线。
/// 只负责扫描、连接、状态归约与命令下发；UI 线程封送由调用方处理。
/// </summary>
public sealed class RealHeadsetController : IDisposable
{
    private readonly BluetoothDeviceScanner _scanner;
    private readonly HeadsetConnectionService _connection;
    private readonly HeadsetControlService _control;
    private readonly DispatcherQueue? _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _deviceSubscription;
    private readonly IDisposable _connectionSubscription;
    private readonly IDisposable _controlSubscription;
    private readonly object _gate = new();
    private HeadsetDevice? _device;
    private HeadsetControlState _state = new();
    private HeadsetConnectionState _connectionState = HeadsetConnectionState.Disconnected;
    private string _status = "正在扫描耳机…";
    private bool _attached;
    private bool _enabled;
    private int _generation;
    private bool _disposed;

    public event EventHandler? Changed;

    public RealHeadsetController(DispatcherQueue? dispatcher = null)
    {
        _dispatcher = dispatcher;
        var protocol = new YuandaoProtocol();
        _scanner = new BluetoothDeviceScanner(NullLogger<BluetoothDeviceScanner>.Instance);
        _connection = new HeadsetConnectionService(
            _scanner,
            new GattConnectionFactory(NullLoggerFactory.Instance),
            new SppConnectionFactory(NullLoggerFactory.Instance),
            protocol,
            NullLogger<HeadsetConnectionService>.Instance);
        _control = new HeadsetControlService(protocol, NullLogger<HeadsetControlService>.Instance);

        // 与原版 NICEHCK ViewModel 一致：扫描/状态事件回到 UI 线程，
        // 避免 WinRT/COM 对象在后台线程创建、释放导致 combase 无效窗口句柄。
        _deviceSubscription = _connection.DevicesDiscovered.Subscribe(
            device => OnUi(() => OnDeviceDiscovered(device)));
        _connectionSubscription = _connection.StateChanged.Subscribe(
            state => OnUi(() => OnConnectionStateChanged(state)));
        _controlSubscription = _control.StateChanged.Subscribe(
            state => OnUi(() => OnControlStateChanged(state)));
        _connection.ControlSessionChanged += OnControlSessionChangedFromAnyThread;
    }

    public HeadsetDevice? Device
    {
        get
        {
            lock (_gate)
            {
                return _device;
            }
        }
    }

    public HeadsetControlState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public HeadsetConnectionState ConnectionState
    {
        get
        {
            lock (_gate)
            {
                return _connectionState;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool ControlsAvailable => Volatile.Read(ref _attached);

    public void Start()
    {
        if (_disposed || _enabled)
        {
            return;
        }

        _enabled = true;
        _generation++;
        AppLog.Info("headset controller start");
        _ = ScanAsync();
    }

    /// <summary>设置里关闭耳机模块时调用；停扫描/断 SPP，但不释放控制器，重新开启可复用。</summary>
    public void Stop()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        _ = StopAsync(++_generation);
    }

    private async Task StopAsync(int generation)
    {
        try
        {
            await _connection.StopScanAsync();
            await _connection.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("headset stop failed: " + ex.Message);
        }
        finally
        {
            if (generation == _generation)
            {
                Volatile.Write(ref _attached, false);
                lock (_gate)
                {
                    _device = null;
                    _connectionState = HeadsetConnectionState.Disconnected;
                    _state = new HeadsetControlState();
                }

                SetStatus("耳机模块已在设置中关闭");
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public async Task<WriteResult> ApplyAsync(HeadsetControls controls)
    {
        if (!ControlsAvailable)
        {
            return WriteResult.Fail("耳机控制通道未就绪。");
        }

        try
        {
            var state = State;
            var currentAnc = ToPreview(state.AncMode);
            if (currentAnc is null || currentAnc != controls.Anc)
            {
                await _control.SetAncAsync(ToNice(controls.Anc), _cts.Token);
            }

            var currentEq = ToPreview(state.Equalizer);
            if (currentEq is null || currentEq != controls.Eq)
            {
                await _control.SetEqualizerAsync(ToNice(controls.Eq), _cts.Token);
            }

            await ApplyToggle(HeadsetToggleFeature.GameMode, state.GameModeEnabled, controls.GameMode);
            await ApplyToggle(HeadsetToggleFeature.LowLatency, state.LowLatencyEnabled, controls.LowLatency);
            await ApplyToggle(HeadsetToggleFeature.DualConnection, state.DualConnectionEnabled, controls.DualDevice);
            await ApplyToggle(HeadsetToggleFeature.InEarDetection, state.InEarDetectionEnabled, controls.InEarDetection);
            await ApplyToggle(HeadsetToggleFeature.WindSuppression, state.WindSuppressionEnabled, controls.WindNoise);
            SetStatus("设置已发送");
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            SetStatus($"写入失败：{ex.Message}");
            return WriteResult.Fail($"耳机写入失败：{ex.Message}");
        }
    }

    public async Task FindAsync(bool enabled)
    {
        if (!ControlsAvailable)
        {
            return;
        }

        try
        {
            await _control.SendFindEarbudsAsync(enabled, _cts.Token);
        }
        catch (Exception ex)
        {
            SetStatus($"查找耳机失败：{ex.Message}");
        }
    }

    private async Task ScanAsync()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        try
        {
            AppLog.Info("headset scan start");
            SetStatus("正在扫描耳机…");
            await _connection.StartScanAsync(_cts.Token);
            AppLog.Info("headset scan started");
        }
        catch (Exception ex)
        {
            AppLog.Error("headset scan failed", ex);
            SetStatus($"扫描失败：{ex.Message}");
        }
    }

    private void OnDeviceDiscovered(HeadsetDevice device)
    {
        if (_disposed || !_enabled || !IsTarget(device.Name))
        {
            return;
        }

        lock (_gate)
        {
            if (_device is not null)
            {
                return;
            }

            _device = device;
        }

        AppLog.Info($"headset discovered name='{device.Name}' address='{device.Address}' le={device.IsLowEnergy} paired={device.IsPaired}");
        _ = ConnectAsync(device);
    }

    private async Task ConnectAsync(HeadsetDevice device)
    {
        try
        {
            AppLog.Info($"headset connect start name='{device.Name}' address='{device.Address}'");
            SetStatus($"正在连接 {device.Name}…");
            await _connection.StopScanAsync();
            await _connection.ConnectAsync(device, _cts.Token);
            AppLog.Info($"headset connect returned name='{device.Name}' state={ConnectionState}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"headset connect failed name='{device.Name}'", ex);
            SetStatus($"连接失败：{DescribeConnectionError(ex)}");
            lock (_gate)
            {
                _device = null;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_disposed && _enabled)
            {
                await ScanAsync();
            }
        }
    }

    private void OnControlSessionChangedFromAnyThread(ISppDeviceSession? session, long generation)
    {
        OnUi(() => OnControlSessionChanged(session, generation));
    }

    private void OnControlSessionChanged(ISppDeviceSession? session, long generation)
    {
        AppLog.Info($"headset control session changed session={(session is null ? "null" : "present")} generation={generation}");
        if (session is null)
        {
            Volatile.Write(ref _attached, false);
            SetStatus("控制通道已断开");
            _ = _control.DetachAsync();
            return;
        }

        _ = AttachAsync(session, generation);
    }

    private async Task AttachAsync(ISppDeviceSession session, long generation)
    {
        try
        {
            await _control.AttachAsync(session, generation, _cts.Token);
            Volatile.Write(ref _attached, true);
            AppLog.Info($"headset control attach ok generation={generation}");
            SetStatus("已连接");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _attached, false);
            AppLog.Error($"headset control attach failed generation={generation}", ex);
            SetStatus($"控制通道初始化失败：{ex.Message}");
        }
    }

    private void OnConnectionStateChanged(HeadsetConnectionState state)
    {
        AppLog.Info($"headset connection state={state}");
        lock (_gate)
        {
            _connectionState = state;
            _status = state switch
            {
                HeadsetConnectionState.Connected => "已连接",
                HeadsetConnectionState.Connecting => "正在连接…",
                HeadsetConnectionState.Reconnecting => "正在重连…",
                _ => "未连接",
            };

            // 断开后保留上一次会话的电量/ANC 会让界面显示过期数据。
            if (state == HeadsetConnectionState.Disconnected)
            {
                _state = new HeadsetControlState();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnControlStateChanged(HeadsetControlState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string status)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => SetStatus(status));
            return;
        }

        lock (_gate)
        {
            _status = status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private async Task ApplyToggle(HeadsetToggleFeature feature, bool? current, bool desired)
    {
        if (current == desired)
        {
            return;
        }

        await _control.SetToggleAsync(feature, desired, _cts.Token);
    }

    private static bool IsTarget(string name) =>
        name.Contains("YUANDAO", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("OriG", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("原道", StringComparison.OrdinalIgnoreCase);

    private static string DescribeConnectionError(Exception ex)
    {
        if (!ex.ToString().Contains("0x80072740", StringComparison.OrdinalIgnoreCase))
        {
            return ex.Message;
        }

        var holder = System.Diagnostics.Process.GetProcessesByName("YuandaoTws.Desktop").Length > 0;
        return holder
            ? "SPP 通道被原道耳机控制台占用。请退出原道耳机控制台，应用会自动重连。"
            : "SPP 通道被其他程序占用。请关闭占用蓝牙串口的程序，应用会自动重连。";
    }

    private static PreviewAnc? ToPreview(NiceAnc? mode) => mode switch
    {
        NiceAnc.Off => PreviewAnc.Off,
        NiceAnc.Transparency => PreviewAnc.Transparency,
        NiceAnc.Normal => PreviewAnc.Normal,
        NiceAnc.Deep => PreviewAnc.Deep,
        NiceAnc.Experimental => PreviewAnc.Experimental,
        NiceAnc.WindSuppression => PreviewAnc.WindNoise,
        _ => null,
    };

    private static NiceAnc ToNice(PreviewAnc mode) => mode switch
    {
        PreviewAnc.Off => NiceAnc.Off,
        PreviewAnc.Transparency => NiceAnc.Transparency,
        PreviewAnc.Normal => NiceAnc.Normal,
        PreviewAnc.Deep => NiceAnc.Deep,
        PreviewAnc.Experimental => NiceAnc.Experimental,
        PreviewAnc.WindNoise => NiceAnc.WindSuppression,
        _ => NiceAnc.Unknown,
    };

    private static PreviewEq? ToPreview(NiceEq? preset) => preset switch
    {
        NiceEq.Blue => PreviewEq.Tears,
        NiceEq.Balanced => PreviewEq.Balanced,
        NiceEq.Bass => PreviewEq.Western,
        NiceEq.Pure => PreviewEq.TrueTone,
        NiceEq.Game => PreviewEq.Gaming,
        NiceEq.Fine => PreviewEq.Delicate,
        NiceEq.Vocal => PreviewEq.WarmVocal,
        _ => null,
    };

    private static NiceEq ToNice(PreviewEq preset) => preset switch
    {
        PreviewEq.Tears => NiceEq.Blue,
        PreviewEq.Balanced => NiceEq.Balanced,
        PreviewEq.Western => NiceEq.Bass,
        PreviewEq.TrueTone => NiceEq.Pure,
        PreviewEq.Gaming => NiceEq.Game,
        PreviewEq.Delicate => NiceEq.Fine,
        PreviewEq.WarmVocal => NiceEq.Vocal,
        _ => NiceEq.Unknown,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _deviceSubscription.Dispose();
        _connectionSubscription.Dispose();
        _controlSubscription.Dispose();
        _connection.ControlSessionChanged -= OnControlSessionChangedFromAnyThread;
        _control.Dispose();
        _connection.Dispose();
        _scanner.Dispose();
        _cts.Dispose();
    }
}
