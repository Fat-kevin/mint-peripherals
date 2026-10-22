using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>建立蓝牙 GATT 连接的工厂。Application 层通过它创建会话，不感知具体蓝牙实现。</summary>
public interface IGattConnectionFactory
{
    /// <summary>连接到指定设备并建立 GATT 会话。</summary>
    Task<IGattDeviceSession> ConnectAsync(HeadsetDevice device, CancellationToken cancellationToken);

    /// <summary>与设备建立配对（LE 写入需要配对授权，未配对设备写特征会被 Windows 拒绝）。</summary>
    Task<bool> PairAsync(HeadsetDevice device, CancellationToken cancellationToken);
}
