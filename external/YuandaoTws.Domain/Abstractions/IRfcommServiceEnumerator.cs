using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>
/// RFCOMM 服务枚举：本机已配对经典蓝牙设备 + 某设备的 SDP 服务列表。
/// 实现层负责 WinRT 细节，本接口对上层屏蔽具体类型。
/// </summary>
public interface IRfcommServiceEnumerator
{
    /// <summary>枚举本机已配对的经典蓝牙设备（SPP 探测的目标选择）。</summary>
    Task<IReadOnlyList<HeadsetDevice>> EnumeratePairedDevicesAsync(CancellationToken cancellationToken);

    /// <summary>枚举某经典蓝牙设备暴露的 RFCOMM 服务（SDP，Uncached）。</summary>
    Task<IReadOnlyList<RfcommServiceInfo>> GetServicesAsync(
        HeadsetDevice device,
        CancellationToken cancellationToken);
}
