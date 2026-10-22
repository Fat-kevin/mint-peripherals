namespace YuandaoTws.Domain.Models;

/// <summary>GATT 特征支持的读写属性（跨平台抽象，屏蔽 WinRT 具体类型）。</summary>
[Flags]
public enum GattCharacteristicProperties
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Notify = 1 << 2,
    Indicate = 1 << 3,
}

/// <summary>一个 GATT 特征的轻量描述。</summary>
public sealed record GattCharacteristicInfo
{
    public required Guid Uuid { get; init; }

    /// <summary>特征人类可读名称（尽力而为，可为空）。</summary>
    public string? UserDescription { get; init; }

    public required GattCharacteristicProperties Properties { get; init; }
}
