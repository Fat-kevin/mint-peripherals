namespace YuandaoTws.Domain;

/// <summary>标准蓝牙 SIG 分配的 UUID 常量。</summary>
public static class StandardUuids
{
    /// <summary>Battery Service：0x180F。</summary>
    public static readonly Guid BatteryService = new("0000180F-0000-1000-8000-00805F9B34FB");

    /// <summary>Battery Level：0x2A19（1 字节，0–100%）。</summary>
    public static readonly Guid BatteryLevel = new("00002A19-0000-1000-8000-00805F9B34FB");

    /// <summary>Device Information Service：0x180A。</summary>
    public static readonly Guid DeviceInformationService = new("0000180A-0000-1000-8000-00805F9B34FB");

    /// <summary>Device Name：0x2A00。</summary>
    public static readonly Guid DeviceName = new("00002A00-0000-1000-8000-00805F9B34FB");
}
