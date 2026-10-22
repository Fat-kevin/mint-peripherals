using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>按 RFCOMM 服务 UUID 打开一条双向 SPP 字节流会话。</summary>
public interface ISppConnectionFactory
{
    /// <summary>打开设备的指定 RFCOMM 服务并返回流会话。</summary>
    Task<ISppDeviceSession> OpenAsync(
        HeadsetDevice device,
        Guid serviceId,
        CancellationToken cancellationToken);
}
