using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>
/// 一个已连接蓝牙设备的 GATT 会话。实现层负责 WinRT 细节，本接口对上层屏蔽具体类型。
/// </summary>
public interface IGattDeviceSession : IAsyncDisposable
{
    HeadsetDevice Device { get; }

    bool IsConnected { get; }

    /// <summary>连接意外丢失（非主动断开）时触发。</summary>
    event Action? ConnectionLost;

    /// <summary>发现某服务下的全部特征。</summary>
    Task<IReadOnlyList<GattCharacteristicInfo>> DiscoverCharacteristicsAsync(
        Guid serviceUuid,
        CancellationToken cancellationToken);

    /// <summary>枚举设备暴露的全部 GATT 服务及特征（协议探测用）。</summary>
    Task<IReadOnlyList<GattServiceInfo>> EnumerateServicesAsync(CancellationToken cancellationToken);

    /// <summary>读取特征值；读取失败或设备不支持主动读取时返回 null。</summary>
    Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken);

    /// <summary>
    /// 写入特征值。部分特征只支持「无响应写」（Write Without Response），
    /// 用 <paramref name="withResponse"/> 指定写模式。
    /// </summary>
    Task WriteAsync(Guid characteristicUuid, byte[] data, CancellationToken cancellationToken, bool withResponse = true);

    /// <summary>订阅某特征的 Notify 通知。</summary>
    Task SubscribeAsync(Guid characteristicUuid, CancellationToken cancellationToken);

    /// <summary>所有已订阅特征的 Notify 帧流。</summary>
    IObservable<GattNotification> Notifications { get; }
}
