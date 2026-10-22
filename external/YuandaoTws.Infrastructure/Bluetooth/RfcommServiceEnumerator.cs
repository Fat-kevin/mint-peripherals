using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using YuandaoTws.Domain;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Exceptions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>
/// RFCOMM 服务枚举：本机已配对经典蓝牙设备 + 某设备的 SDP 服务列表（Uncached 防缓存）。
/// 经典蓝牙（SPP）与 BLE 无关，只要求设备已配对。
/// </summary>
public sealed class RfcommServiceEnumerator : IRfcommServiceEnumerator
{
    private readonly ILogger<RfcommServiceEnumerator> _logger;

    public RfcommServiceEnumerator(ILogger<RfcommServiceEnumerator> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<HeadsetDevice>> EnumeratePairedDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var infos = await DeviceInformation
            .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))
            .AsTask(cancellationToken);

        var result = new List<HeadsetDevice>();
        foreach (var info in infos)
        {
            BluetoothDevice? device;
            try
            {
                device = await BluetoothDevice.FromIdAsync(info.Id);
            }
            catch (Exception ex)
            {
                // BTHENUM 功能端点（AVRCP 传输、设备标识服务等）不是有效的 BluetoothDevice，跳过。
                _logger.LogDebug("跳过非主蓝牙设备 {Id}：{Message}", info.Id, ex.Message);
                continue;
            }

            if (device is null)
            {
                _logger.LogDebug("经典设备 {Name} 无法打开", info.Name);
                continue;
            }

            var name = string.IsNullOrWhiteSpace(info.Name) ? "(未命名设备)" : info.Name;
            result.Add(new HeadsetDevice
            {
                Name = name,
                DeviceId = info.Id,
                Address = BluetoothAddressFormatter.Format(device.BluetoothAddress),
                IsLowEnergy = false,
                IsPaired = info.Pairing?.IsPaired ?? false,
            });
        }

        _logger.LogInformation("已枚举 {Count} 个已配对经典蓝牙设备", result.Count);
        return result;
    }

    public async Task<IReadOnlyList<RfcommServiceInfo>> GetServicesAsync(
        HeadsetDevice device,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bluetoothDevice = await OpenBluetoothDeviceAsync(device, cancellationToken);
        var result = await bluetoothDevice.GetRfcommServicesAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);
        if (result.Error != BluetoothError.Success)
        {
            throw new BluetoothConnectionException($"获取 {device.Name} 的 RFCOMM 服务失败：{result.Error}");
        }

        var services = result.Services
            .Select(s => new RfcommServiceInfo
            {
                ServiceId = s.ServiceId.Uuid,
                ServiceName = RfcommServiceNames.Describe(s.ServiceId.Uuid),
                ChannelName = s.ConnectionServiceName,
            })
            .ToArray();

        _logger.LogInformation("{Device} 枚举到 {Count} 个 RFCOMM 服务", device.Name, services.Length);
        return services;
    }

    private async Task<BluetoothDevice> OpenBluetoothDeviceAsync(
        HeadsetDevice device,
        CancellationToken cancellationToken)
    {
        BluetoothDevice? bluetoothDevice = null;
        try
        {
            bluetoothDevice = await BluetoothDevice.FromIdAsync(device.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "按 DeviceId 打开经典设备失败：{DeviceId}", device.DeviceId);
        }

        if (bluetoothDevice is null && BluetoothAddressFormatter.TryParse(device.Address, out var mac))
        {
            bluetoothDevice = await BluetoothDevice.FromBluetoothAddressAsync(mac);
        }

        if (bluetoothDevice is null)
        {
            throw new BluetoothConnectionException(
                $"无法打开经典蓝牙设备 {device.Name}（{device.Address}）。请确认设备已配对且在本机蓝牙范围内。");
        }

        return bluetoothDevice;
    }
}
