using YuandaoTws.Domain.Enums;

namespace YuandaoTws.Domain.Models;

/// <summary>耳机 EQ 预设。协议未知值以 <see cref="Unknown"/> 保留。</summary>
public enum EqualizerPreset
{
    Unknown = 0,
    Blue = 1,
    Balanced = 2,
    Bass = 3,
    Pure = 4,
    Game = 5,
    Fine = 6,
    Vocal = 7,
}

/// <summary>NiceHCK/BES 布尔控制功能。</summary>
public enum HeadsetToggleFeature
{
    GameMode = 1,
    LowLatency = 2,
    DualConnection = 3,
    InEarDetection = 4,
    WindSuppression = 5,
}

/// <summary>耳机状态（对应 APK Lyq1 枚举）。<see cref="Unknown"/> 表示未上报/未知。</summary>
public enum HeadphoneStatus
{
    Unknown = 0,
    InBoxClosed = 1,
    InBoxOpen = 2,
    OutBoxOutside = 3,
    InEar = 4,
}

/// <summary>实验性编码切换指令的目标编码。</summary>
public enum HeadsetCodec
{
    Aac = 0,
    Lhdc = 1,
    Sbc = 2,
}

/// <summary>固件版本，协议载荷顺序为 [子版本, 主版本]。</summary>
public sealed record FirmwareVersion
{
    public required byte Major { get; init; }

    public required byte Minor { get; init; }

    public bool SupportsModernCodecAndExtendedEq => Major > 4 || (Major == 4 && Minor >= 8);

    public override string ToString() => $"{Major}.{Minor}";

    public static FirmwareVersion FromProtocolPayload(byte[] payload) => new()
    {
        Minor = payload[0],
        Major = payload[1],
    };
}

/// <summary>设备报告的一项已知协议状态变更。未识别帧返回 null，而非伪造默认状态。</summary>
public sealed record HeadsetProtocolUpdate
{
    public BatteryInfo? Battery { get; init; }

    public FirmwareVersion? Firmware { get; init; }

    public NoiseCancellingMode? AncMode { get; init; }

    public EqualizerPreset? Equalizer { get; init; }

    public HeadsetToggleFeature? ToggleFeature { get; init; }

    public bool? ToggleValue { get; init; }

    public HeadphoneStatus? LeftEarStatus { get; init; }

    public HeadphoneStatus? RightEarStatus { get; init; }
}

/// <summary>主界面使用的已确认设备状态快照。</summary>
public sealed record HeadsetControlState
{
    public FirmwareVersion? Firmware { get; init; }

    public BatteryInfo? Battery { get; init; }

    public NoiseCancellingMode? AncMode { get; init; }

    public EqualizerPreset? Equalizer { get; init; }

    public bool? GameModeEnabled { get; init; }

    public bool? LowLatencyEnabled { get; init; }

    public bool? DualConnectionEnabled { get; init; }

    public bool? InEarDetectionEnabled { get; init; }

    public bool? WindSuppressionEnabled { get; init; }

    /// <summary>提示音音量（0–255；仅代表最近一次查询/写入的原始值）。</summary>
    public byte? ToneVolume { get; init; }

    /// <summary>仅代表最近成功写出的编码指令；协议没有可靠的编码查询响应。</summary>
    public HeadsetCodec? LastRequestedCodec { get; init; }

    /// <summary>左耳状态（HEADPHONE_STATUS 帧）。</summary>
    public HeadphoneStatus? LeftEarStatus { get; init; }

    /// <summary>右耳状态（HEADPHONE_STATUS 帧）。</summary>
    public HeadphoneStatus? RightEarStatus { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public bool SupportsExtendedEqAndSbc => Firmware?.SupportsModernCodecAndExtendedEq == true;
}
