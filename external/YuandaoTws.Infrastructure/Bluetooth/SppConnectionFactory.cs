using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Exceptions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>按 RFCOMM 服务 UUID 打开一条双向 SPP 字节流。</summary>
public sealed class SppConnectionFactory : ISppConnectionFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SppConnectionFactory> _logger;

    public SppConnectionFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SppConnectionFactory>();
    }

    public async Task<ISppDeviceSession> OpenAsync(
        HeadsetDevice device,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("打开 {Device} 的 SPP 流：{ServiceId}", device.Name, serviceId);

        var bluetoothDevice = await OpenBluetoothDeviceAsync(device, cancellationToken);
        var servicesResult = await bluetoothDevice.GetRfcommServicesAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);
        if (servicesResult.Error != BluetoothError.Success)
        {
            throw new BluetoothConnectionException($"获取 {device.Name} 的 RFCOMM 服务失败：{servicesResult.Error}");
        }

        var service = servicesResult.Services.FirstOrDefault(s => s.ServiceId.Uuid == serviceId)
            ?? throw new BluetoothConnectionException($"设备 {device.Name} 未暴露 RFCOMM 服务 {serviceId}。");

        // 先请求访问权限。桌面应用下通常直接放行，若被系统拒绝则给出明确指引。
        DeviceAccessStatus access;
        try
        {
            access = await service.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "请求 RFCOMM 访问状态失败，继续尝试打开流");
            access = DeviceAccessStatus.Unspecified;
        }

        if (access is DeviceAccessStatus.DeniedBySystem or DeviceAccessStatus.DeniedByUser)
        {
            throw new BluetoothConnectionException(
                $"打开 {serviceId} 的 SPP 流被系统拒绝（访问状态：{access}）。请确认设备已配对且在范围内；若持续拒绝，需将应用打包为 MSIX 并声明 bluetooth capability。");
        }

        // RFCOMM 无 OpenStreamAsync，标准做法是 StreamSocket 连到服务的通道名（RFC 标准 SPP 模式）。
        var socket = new StreamSocket();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(
                        service.ConnectionHostName,
                        service.ConnectionServiceName,
                        SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                    .AsTask(connectCts.Token);
            }
            catch (Exception ex) when (connectCts.IsCancellationRequested)
            {
                throw new BluetoothConnectionException(
                    $"连接 {device.Name} 的 SPP 服务超时（{ConnectTimeout.TotalSeconds:0}s）。请确认设备已开机且在蓝牙范围内。",
                    ex);
            }
            catch (Exception ex)
            {
                // 加密连接失败时重试一次明文 SPP（部分设备只接受明文）。
                _logger.LogDebug(ex, "加密 SPP 连接失败，尝试明文连接");
                socket.Dispose();
                socket = new StreamSocket();
                await socket.ConnectAsync(
                        service.ConnectionHostName,
                        service.ConnectionServiceName,
                        SocketProtectionLevel.PlainSocket)
                    .AsTask(connectCts.Token);
            }
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new BluetoothConnectionException(
                $"打开 SPP 流失败（0x{ex.HResult:X8}）：{ex.Message}。请确认设备已配对且在范围内；若持续拒绝，需将应用打包为 MSIX 并声明 bluetooth capability。",
                ex);
        }

        return new SppDeviceSession(
            bluetoothDevice,
            service,
            socket,
            device,
            _loggerFactory.CreateLogger<SppDeviceSession>());
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
