namespace YuandaoTws.Domain.Models;

/// <summary>
/// 电池信息来源。标准 = 0x2A19 单值通道；私有 = 厂商自定义特征（左右耳独立）。
/// </summary>
public enum BatterySource
{
    Unknown = 0,
    Standard = 1,
    Private = 2,
}

/// <summary>
/// 耳机电池信息。各电量字段 0–100，null 表示未知/未上报。
/// </summary>
public sealed record BatteryInfo
{
    public byte? LeftEarPercent { get; init; }

    public byte? RightEarPercent { get; init; }

    /// <summary>充电盒电量（多数私有协议附带，非所有机型上报）。</summary>
    public byte? CasePercent { get; init; }

    /// <summary>左耳是否正在充电。由 HEADPHONE_STATUS(0x0006) 左耳状态 == <see cref="HeadphoneStatus.InBoxClosed"/> 归约而来，
    /// 经 <c>HeadsetControlState.Battery</c> 驱动 ⚡ 指示；主控 0x0005 电量帧未定义 bit7 充电语义。</summary>
    public bool? IsLeftEarCharging { get; init; }

    /// <summary>右耳是否正在充电。由 HEADPHONE_STATUS(0x0006) 右耳状态 == <see cref="HeadphoneStatus.InBoxClosed"/> 归约而来，
    /// 经 <c>HeadsetControlState.Battery</c> 驱动 ⚡ 指示；主控 0x0005 电量帧未定义 bit7 充电语义。</summary>
    public bool? IsRightEarCharging { get; init; }

    /// <summary>充电盒是否正在充电（当前原道协议尚未确认该标志，通常为 null）。</summary>
    public bool? IsCaseCharging { get; init; }

    public BatterySource Source { get; init; } = BatterySource.Private;

    /// <summary>合并电量：优先左耳，其次右耳，最后充电盒；全未知时为 null。</summary>
    public byte? CombinedPercent => LeftEarPercent ?? RightEarPercent ?? CasePercent;

    public bool HasAny => LeftEarPercent.HasValue || RightEarPercent.HasValue || CasePercent.HasValue;

    /// <summary>由标准通道单值构造（左右耳同值，UI 显示为总电量）。</summary>
    public static BatteryInfo FromSingleValue(byte percent) =>
        new()
        {
            LeftEarPercent = percent,
            RightEarPercent = percent,
            Source = BatterySource.Standard,
        };

    /// <summary>由私有通道构造（左右耳独立）。</summary>
    public static BatteryInfo FromLeftRight(
        byte? left,
        byte? right,
        byte? casePercent = null,
        bool? isLeftEarCharging = null,
        bool? isRightEarCharging = null,
        bool? isCaseCharging = null) =>
        new()
        {
            LeftEarPercent = left,
            RightEarPercent = right,
            CasePercent = casePercent,
            IsLeftEarCharging = isLeftEarCharging,
            IsRightEarCharging = isRightEarCharging,
            IsCaseCharging = isCaseCharging,
            Source = BatterySource.Private,
        };
}
