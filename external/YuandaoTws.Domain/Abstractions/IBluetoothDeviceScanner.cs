using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>蓝牙设备扫描器。用于发现附近的 BLE 耳机设备。</summary>
public interface IBluetoothDeviceScanner : IDisposable
{
    /// <summary>扫描期间不断推送发现的设备（可能重复，调用方去重）。</summary>
    IObservable<HeadsetDevice> DevicesDiscovered { get; }

    /// <summary>启动扫描。</summary>
    Task StartScanAsync(CancellationToken cancellationToken);

    /// <summary>停止扫描。</summary>
    Task StopScanAsync();
}
