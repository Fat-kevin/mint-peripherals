namespace YuandaoTws.Domain.Models;

/// <summary>一个 GATT 服务及其全部特征的描述（协议探测用）。</summary>
public sealed record GattServiceInfo
{
    /// <summary>服务 UUID。</summary>
    public required Guid Uuid { get; init; }

    /// <summary>该服务下全部特征。</summary>
    public required IReadOnlyList<GattCharacteristicInfo> Characteristics { get; init; }
}
