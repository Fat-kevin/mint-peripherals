namespace YuandaoTws.Domain.Models;

/// <summary>经典蓝牙（BR/EDR）设备暴露的一个 RFCOMM 服务（SDP 记录）。</summary>
public sealed record RfcommServiceInfo
{
    /// <summary>服务 UUID（SerialPort 0x1101 或厂商自定义）。</summary>
    public required Guid ServiceId { get; init; }

    /// <summary>可读名称（由 <see cref="RfcommServiceNames.Describe"/> 生成）。</summary>
    public required string ServiceName { get; init; }

    /// <summary>RFCOMM 通道名（如 "Ch1"），用于建立连接。</summary>
    public string? ChannelName { get; init; }
}
