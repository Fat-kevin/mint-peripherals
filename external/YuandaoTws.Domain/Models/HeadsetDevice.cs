namespace YuandaoTws.Domain.Models;

/// <summary>一台可连接的蓝牙耳机设备。</summary>
public sealed record HeadsetDevice
{
    /// <summary>设备显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>Windows 设备实例 ID，供 <c>BluetoothLEDevice.FromIdAsync</c> 连接使用。</summary>
    public required string DeviceId { get; init; }

    /// <summary>蓝牙地址（MAC），用于展示与去重。</summary>
    public required string Address { get; init; }

    /// <summary>是否为 BLE（低功耗）设备。TWS 耳机的控制通道依赖 BLE。</summary>
    public required bool IsLowEnergy { get; init; }

    /// <summary>是否已与本机配对。</summary>
    public bool IsPaired { get; init; }
}
