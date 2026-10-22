using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>
/// BLE 设备扫描器：底层广告监听（覆盖所有广播，含未配对设备）
/// + 系统已知 BLE 设备 + 已配对经典蓝牙设备的 BLE 控制通道探测。
/// </summary>
public sealed class BluetoothDeviceScanner : IBluetoothDeviceScanner
{
    private static readonly string LeIsConnectableProperty = "System.Devices.Aep.Bluetooth.Le.IsConnectable";
    private static readonly string LeAddressProperty = "System.Devices.Aep.Bluetooth.Le.Address";

    private readonly Subject<HeadsetDevice> _devices = new();
    private readonly ILogger<BluetoothDeviceScanner> _logger;
    private readonly object _seenLock = new();
    private readonly HashSet<ulong> _seenAddresses = new();
    private DeviceWatcher? _watcher;
    private BluetoothLEAdvertisementWatcher? _adWatcher;
    private int _disposed;

    public IObservable<HeadsetDevice> DevicesDiscovered => _devices;

    public BluetoothDeviceScanner(ILogger<BluetoothDeviceScanner> logger)
    {
        _logger = logger;
    }

    public async Task StartScanAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        StopScan();

        // 已连接的耳机通常停止广播，DeviceWatcher 只监听广播、扫不到它们。
        // 因此先枚举系统已知的 BLE 设备（含已配对的），再启动广播监听作为补充。
        try
        {
            var known = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector())
                .AsTask(cancellationToken);
            var knownCount = 0;
            foreach (var info in known)
            {
                if (TryCreateDevice(info, out var device))
                {
                    PublishDevice(device);
                    knownCount++;
                }
            }

            _logger.LogInformation("已枚举 {Count} 个已知 BLE 设备（含已配对），继续监听新广播", knownCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举已知 BLE 设备失败，仅依赖广播扫描");
        }

        // TWS 耳机常以「经典蓝牙」作音频连接、BLE 作控制通道，二者共用同一 MAC。
        // 这类耳机的 BLE 身份不会出现在 BLE 广播列表，但可按相同 MAC 直接探测 LE 连接。
        try
        {
            // 只枚举「已配对」的经典蓝牙设备，避免 AVRCP 传输、RFCOMM 等非主设备。
            var classicDevices = await DeviceInformation
                .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))
                .AsTask(cancellationToken);
            var leFound = 0;
            foreach (var info in classicDevices)
            {
                BluetoothDevice? classicDevice;
                try
                {
                    classicDevice = await BluetoothDevice.FromIdAsync(info.Id);
                }
                catch (Exception ex)
                {
                    // BTHENUM 功能端点（AVRCP 传输、设备标识服务等）不是有效的 BluetoothDevice，跳过。
                    _logger.LogDebug("跳过非主蓝牙设备 {Id}：{Message}", info.Id, ex.Message);
                    continue;
                }

                if (classicDevice is null)
                {
                    _logger.LogDebug("经典设备 {Name} 无法打开", info.Name);
                    continue;
                }

                using var classicDeviceLifetime = classicDevice;
                var mac = FormatAddress(classicDevice.BluetoothAddress);
                using var leDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(classicDevice.BluetoothAddress);
                if (leDevice is null)
                {
                    _logger.LogDebug("经典设备 {Name}（{Mac}）当前未暴露 BLE 身份", info.Name, mac);
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(leDevice.Name) ? info.Name : leDevice.Name;
                _logger.LogInformation("经典设备 {Name}（{Mac}）解析出 BLE 控制通道：{LeName}", info.Name, mac, name);
                PublishDevice(new HeadsetDevice
                {
                    Name = name,
                    DeviceId = leDevice.DeviceId,
                    Address = mac,
                    IsLowEnergy = true,
                    IsPaired = true,
                });
                leFound++;
            }

            _logger.LogInformation(
                "已配对经典蓝牙设备 {Count} 个，其中 {Found} 个可解析出 BLE 控制通道",
                classicDevices.Count, leFound);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "探测经典蓝牙设备的 BLE 身份失败");
        }

        var selector = BluetoothLEDevice.GetDeviceSelector();
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added += OnDeviceAdded;
        _watcher.Updated += OnDeviceUpdated;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Stopped += OnWatcherStopped;

        _logger.LogInformation("开始扫描 BLE 设备");
        _watcher.Start();

        // 底层 BLE 广告监听：DeviceWatcher 收不到「未配对设备」的广播（Windows 添加设备能扫到，
        // 我们的 watcher 却空，根因在此），广告监听器能捕获所有广播。
        _adWatcher = new BluetoothLEAdvertisementWatcher
        {
            // 主动扫描：向广播方请求扫描响应，从而拿到完整本地名。
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _adWatcher.Received += OnAdvertisementReceived;
        lock (_seenLock)
        {
            _seenAddresses.Clear();
        }

        _adWatcher.Start();
        _logger.LogInformation("已启动 BLE 广告监听（Active 模式）");
    }

    public Task StopScanAsync()
    {
        StopScan();
        return Task.CompletedTask;
    }

    private void StopScan()
    {
        if (_watcher is not null)
        {
            _watcher.Added -= OnDeviceAdded;
            _watcher.Updated -= OnDeviceUpdated;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnWatcherStopped;
            if (_watcher.Status != DeviceWatcherStatus.Stopped)
            {
                _watcher.Stop();
            }

            _watcher = null;
        }

        if (_adWatcher is not null)
        {
            _adWatcher.Received -= OnAdvertisementReceived;
            if (_adWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _adWatcher.Stop();
            }

            _adWatcher = null;
        }

        _logger.LogInformation("停止扫描 BLE 设备");
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _logger.LogDebug("BLE 枚举器发现设备：{Name}（{Id}）", info.Name, info.Id);
        if (TryCreateDevice(info, out var device))
        {
            PublishDevice(device);
        }
    }

    private async void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            // 跳过无名字的广播，避免列表被无名信标刷屏。
            var advertisedName = args.Advertisement.LocalName;
            if (string.IsNullOrWhiteSpace(advertisedName))
            {
                return;
            }

            lock (_seenLock)
            {
                if (!_seenAddresses.Add(args.BluetoothAddress))
                {
                    return;
                }
            }

            using var leDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
            if (leDevice is null || Volatile.Read(ref _disposed) != 0)
            {
                _logger.LogDebug("广播发现 {Name}（{Mac}）但无法创建 BLE 设备", advertisedName, FormatAddress(args.BluetoothAddress));
                return;
            }

            var name = string.IsNullOrWhiteSpace(leDevice.Name) ? advertisedName : leDevice.Name;
            var mac = FormatAddress(args.BluetoothAddress);
            _logger.LogInformation("BLE 广告发现设备：{Name}（{Mac}）", name, mac);
            PublishDevice(new HeadsetDevice
            {
                Name = name,
                DeviceId = leDevice.DeviceId,
                Address = mac,
                IsLowEnergy = true,
                IsPaired = leDevice.DeviceInformation?.Pairing?.IsPaired ?? false,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理 BLE 广告发现失败");
        }
    }

    private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // 设备属性更新后可能补齐「可连接」标记或名字，尝试重新加入（上层按 DeviceId 去重）。
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(update.Id);
            if (info is not null && Volatile.Read(ref _disposed) == 0 && TryCreateDevice(info, out var device))
            {
                PublishDevice(device);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理设备更新失败：{Id}", update.Id);
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        _logger.LogInformation("BLE 扫描枚举完成");
    }

    private void OnWatcherStopped(DeviceWatcher sender, object args)
    {
        _logger.LogInformation("BLE 扫描已停止");
    }

    private static bool TryCreateDevice(DeviceInformation info, out HeadsetDevice device)
    {
        device = default!;

        var isPaired = info.Pairing?.IsPaired ?? false;

        // 接受条件：
        //  1. 可连接的 LE 设备；或
        //  2. 已配对设备（即使当前未广播）；或
        //  3. 有名字但尚未配对/可连接标记缺失的设备——配对模式下广播的耳机正是这种，
        //     DeviceWatcher 的 Added 事件里「可连接」标记可能尚未填充，不能因此过滤掉。
        var isConnectable = info.Properties.TryGetValue(LeIsConnectableProperty, out var isConnectableObj)
            && isConnectableObj is bool isConnectableFlag
            && isConnectableFlag;
        if (!isConnectable && !isPaired && string.IsNullOrWhiteSpace(info.Name))
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(info.Name) ? "(未命名设备)" : info.Name;
        var address = info.Properties.TryGetValue(LeAddressProperty, out var addressObj) && addressObj is ulong mac
            ? FormatAddress(mac)
            : string.Empty;

        device = new HeadsetDevice
        {
            Name = name,
            DeviceId = info.Id,
            Address = address,
            IsLowEnergy = true,
            IsPaired = isPaired,
        };
        return true;
    }

    private static string FormatAddress(ulong address) => BluetoothAddressFormatter.Format(address);

    private void PublishDevice(HeadsetDevice device)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _devices.OnNext(device);
        }
        catch (Exception ex)
        {
            // 发现通知是可选的观察者回调，不能让一个 UI 订阅者中断 WinRT 扫描事件。
            _logger.LogError(ex, "发布蓝牙设备发现通知失败：{Device}", device.Name);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopScan();
        _devices.OnCompleted();
        _devices.Dispose();
    }
}
