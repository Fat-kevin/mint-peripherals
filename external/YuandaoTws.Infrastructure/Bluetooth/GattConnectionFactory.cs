using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Exceptions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>建立 GATT 会话的连接工厂。连接握手：打开设备 → 创建会话 → 触发并验证服务发现。</summary>
public sealed class GattConnectionFactory : IGattConnectionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GattConnectionFactory> _logger;

    public GattConnectionFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GattConnectionFactory>();
    }

    public async Task<IGattDeviceSession> ConnectAsync(HeadsetDevice device, CancellationToken cancellationToken)
    {
        _logger.LogInformation("连接设备：{DeviceName}（{Address}）", device.Name, device.Address);

        var bluetoothDevice = await BluetoothLEDevice.FromIdAsync(device.DeviceId);
        if (bluetoothDevice is null)
        {
            throw new BluetoothConnectionException(
                $"无法打开蓝牙设备 {device.Name}。若首次使用，请先在 Windows 设置中与耳机配对。");
        }

        var gattSession = await GattSession.FromDeviceIdAsync(bluetoothDevice.BluetoothDeviceId);
        gattSession.MaintainConnection = true;

        // 触发实际连接并验证设备可达（Uncached 强制穿透缓存）。
        var servicesResult = await bluetoothDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            gattSession.Dispose();
            bluetoothDevice.Dispose();
            throw new BluetoothConnectionException($"连接 {device.Name} 失败：{servicesResult.Status}");
        }

        return new GattDeviceSession(
            bluetoothDevice,
            gattSession,
            device,
            _loggerFactory.CreateLogger<GattDeviceSession>());
    }

    public async Task<bool> PairAsync(HeadsetDevice device, CancellationToken cancellationToken)
    {
        var info = await DeviceInformation.CreateFromIdAsync(device.DeviceId);
        if (info is null)
        {
            throw new BluetoothConnectionException($"无法获取设备 {device.Name} 的信息，请重新扫描后再试。");
        }

        if (info.Pairing.IsPaired)
        {
            _logger.LogInformation("设备 {Name} 已配对，跳过", device.Name);
            return true;
        }

        _logger.LogInformation("开始配对 {Name}（{Address}）…", device.Name, device.Address);
        // Just Works 配对：仅需确认，无需 PIN。配对成功后 Windows 才放行对私有特征的写入。
        // ⚠️ Custom.PairAsync 必须注册 PairingRequested 处理器，否则返回 RequiredHandlerNotRegistered
        //（2026-08-14 实测）。Just Works 场景直接 Accept 即可。
        var customPairing = info.Pairing.Custom;
        void OnPairingRequested(object? sender, DevicePairingRequestedEventArgs args) => args.Accept();
        customPairing.PairingRequested += OnPairingRequested;
        try
        {
            var result = await customPairing.PairAsync(DevicePairingKinds.ConfirmOnly)
                .AsTask(cancellationToken);

            _logger.LogInformation("配对 {Name} 结果：{Status}", device.Name, result.Status);
            return result.Status == DevicePairingResultStatus.Paired;
        }
        finally
        {
            customPairing.PairingRequested -= OnPairingRequested;
        }
    }
}
