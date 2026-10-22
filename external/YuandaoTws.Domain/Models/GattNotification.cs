namespace YuandaoTws.Domain.Models;

/// <summary>来自某个 GATT 特征的 Notify 通知帧。</summary>
public sealed record GattNotification
{
    public required Guid CharacteristicUuid { get; init; }

    public required byte[] Value { get; init; }
}
