using System.Security.Cryptography;
using System.Text;
using DeviceCenterPreview.Core.Models;
using Windows.Gaming.Input;
using Windows.System.Power;
using PreviewCharging = DeviceCenterPreview.Core.Models.ChargingState;

namespace DeviceCenterPreview.App.Services.Battery;

/// <summary>
/// Xbox / GIP 手柄电量读取器（只读）：订阅 Windows.Gaming.Input 的 GamepadAdded / GamepadRemoved
/// 事件，按 <see cref="Gamepad.TryGetBatteryReport"/> 的 Remaining/FullCharge 毫瓦时换算百分比，
/// 并带 45 秒兜底重读（事件驱动为主）。全程后台线程，只读、不写设备、不上报。
/// 只在 WinRT 报告存在且数值可信时给百分比，未知一律 null（不把第三方的假 "Full" 当 100%）。
/// </summary>
internal sealed class GamepadBatteryWatcher : IDisposable
{
    /// <summary>兜底重读间隔（设计范围 30–60 秒）；插拔由事件立即触发。</summary>
    private const int PollSeconds = 45;

    // WGI 的 mWh 数值只对微软（045E）与 GameSir GIP（3537）可信；其它第三方手柄常谎报 Full。
    private const ushort MicrosoftVendorId = 0x045E;
    private const ushort GameSirVendorId = 0x3537;

    private readonly object _gate = new();
    private IReadOnlyList<BatteryDeviceSnapshot> _devices = Array.Empty<BatteryDeviceSnapshot>();
    private Timer? _timer;
    private bool _subscribed;
    private bool _disposed;
    private bool _loggedFailure;

    /// <summary>设备列表有变化（任意线程）；订阅方自行切回 UI 线程。</summary>
    public event EventHandler? Changed;

    /// <summary>最近一轮读取到的手柄（不可变快照）。</summary>
    public IReadOnlyList<BatteryDeviceSnapshot> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            // 不在调用线程（通常是 UI 线程）上初始化 WGI：定时器回调在后台线程首次订阅事件并枚举。
            _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(PollSeconds));
        }
    }

    private void TrySubscribe()
    {
        lock (_gate)
        {
            if (_disposed || _subscribed)
            {
                return;
            }

            try
            {
                Gamepad.GamepadAdded += OnGamepadChanged;
                Gamepad.GamepadRemoved += OnGamepadChanged;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                // WGI 不可用（旧系统/被策略禁用）时退回定时读取，不阻塞其它数据源。
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    AppLog.Warn("Windows.Gaming.Input unavailable: " + ex.Message);
                }
            }
        }
    }

    private void OnGamepadChanged(object? sender, Gamepad gamepad)
    {
        Refresh();
    }

    /// <summary>重建整份手柄列表：事件只是触发器，真实来源始终是 Gamepad.Gamepads 当前集合。</summary>
    private void Refresh()
    {
        // 首次在后台线程订阅静态事件（避免 UI 线程初始化 WGI）；失败则只靠定时重读。
        TrySubscribe();

        var devices = new List<BatteryDeviceSnapshot>();
        bool failed = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                foreach (var gamepad in Gamepad.Gamepads)
                {
                    var device = Read(gamepad);
                    if (device is not null)
                    {
                        devices.Add(device);
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    AppLog.Warn("gamepad battery read failed: " + ex.Message);
                }
            }

            if (failed && devices.Count == 0)
            {
                // 读取整轮失败：保留上一轮结果（last-known），不清空列表。
                return;
            }

            devices.Sort(static (left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            _devices = devices;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("gamepad battery Changed handler failed: " + ex.Message);
        }
    }

    private static BatteryDeviceSnapshot? Read(Gamepad gamepad)
    {
        RawGameController? raw = null;
        try
        {
            raw = RawGameController.FromGameController(gamepad);
        }
        catch
        {
            // 拿不到 raw 元数据时仍可给出电量与通用名字。
        }

        ushort vendorId = raw?.HardwareVendorId ?? 0;
        ushort productId = raw?.HardwareProductId ?? 0;
        bool wireless = raw?.IsWireless ?? gamepad.IsWireless;
        var name = raw?.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = vendorId != 0 ? $"游戏手柄 {vendorId:X4}:{productId:X4}" : "游戏手柄";
        }

        bool trusted = vendorId is MicrosoftVendorId or GameSirVendorId;
        var (percent, charging) = ReadBattery(gamepad, trusted);

        return new BatteryDeviceSnapshot(
            BatteryDeviceKind.Gamepad,
            BuildId(raw, gamepad),
            name,
            percent,
            charging,
            true,
            IsAsleep: false,
            IsOffline: false,
            ConnectionType: wireless ? "Xbox 无线" : "USB",
            Components: null,
            Source: "xbox");
    }

    private static (int? Percent, PreviewCharging Charging) ReadBattery(Gamepad gamepad, bool trusted)
    {
        try
        {
            var report = gamepad.TryGetBatteryReport();
            if (report is null)
            {
                return (null, PreviewCharging.Unknown);
            }

            int? full = report.FullChargeCapacityInMilliwattHours;
            int? remaining = report.RemainingCapacityInMilliwattHours;
            int? percent = null;

            if (full is > 0 && remaining is >= 0)
            {
                int value = (int)Math.Round(
                    remaining.Value * 100.0 / full.Value,
                    MidpointRounding.AwayFromZero);
                if (value is >= 0 and <= 100)
                {
                    // 不可信的第三方手柄只在明确充/放电时接受数值，避免把假的 "Full" 当 100%。
                    bool plausible = trusted ||
                                     report.Status is BatteryStatus.Charging or BatteryStatus.Discharging;
                    if (plausible)
                    {
                        percent = value;
                    }
                }
            }

            var charging = report.Status switch
            {
                BatteryStatus.Charging => PreviewCharging.Charging,
                BatteryStatus.Discharging => PreviewCharging.Discharging,
                BatteryStatus.Idle => percent is >= 99 ? PreviewCharging.Full : PreviewCharging.Unknown,
                _ => PreviewCharging.Unknown,
            };

            return (percent, charging);
        }
        catch
        {
            return (null, PreviewCharging.Unknown);
        }
    }

    private static string BuildId(RawGameController? raw, Gamepad gamepad)
    {
        string identity;
        try
        {
            identity = raw?.NonRoamableId ?? "";
        }
        catch
        {
            identity = "";
        }

        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = gamepad.IsWireless ? "wireless:" + gamepad.GetHashCode() : "wired:" + gamepad.GetHashCode();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "xbox:" + Convert.ToHexString(hash, 0, 4);
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();

        lock (_gate)
        {
            _subscribed = false;
        }

        try
        {
            Gamepad.GamepadAdded -= OnGamepadChanged;
            Gamepad.GamepadRemoved -= OnGamepadChanged;
        }
        catch
        {
            // 退出阶段 WGI 已不可用：忽略解绑失败。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }
}
