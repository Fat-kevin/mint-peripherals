namespace YuandaoTws.Domain.Models;

/// <summary>从 SPP 串口流收到的一个数据块（无包边界，分帧由协议层负责）。</summary>
public sealed record SppDataReceived
{
    /// <summary>本次读到的字节块。</summary>
    public required byte[] Value { get; init; }
}
