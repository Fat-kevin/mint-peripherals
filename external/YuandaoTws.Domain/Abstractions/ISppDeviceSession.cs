using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>
/// 一个已打开的双向 SPP 串口流会话。实现层负责 WinRT 细节，
/// 上层只读写字节流、订阅收到的数据块。
/// </summary>
public interface ISppDeviceSession : IAsyncDisposable
{
    HeadsetDevice Device { get; }

    bool IsConnected { get; }

    /// <summary>连接意外丢失（流关闭/对端断开）时触发。</summary>
    event Action? ConnectionLost;

    /// <summary>从串口流收到的数据块序列。</summary>
    IObservable<SppDataReceived> DataReceived { get; }

    /// <summary>向串口流写入一帧字节。</summary>
    Task WriteAsync(byte[] data, CancellationToken cancellationToken);
}
