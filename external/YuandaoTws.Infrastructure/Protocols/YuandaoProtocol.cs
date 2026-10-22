using YuandaoTws.Domain;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Enums;
using YuandaoTws.Domain.Exceptions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Infrastructure.Protocols;

/// <summary>
/// 原道 OriG in「原点」协议适配器。
/// 主控制通道已由真机确认：RFCOMM 0000a100 + NiceHCK/BES 4E 帧协议。
/// </summary>
public sealed class YuandaoProtocol : IDeviceProtocol
{
    public string Name => "原道 OriG in";

    public Guid ServiceUuid { get; } = new("0000a100-1000-8000-4e48-434b4354524c");

    public bool Matches(HeadsetDevice device) =>
        device.Name.Contains("YUANDAO", StringComparison.OrdinalIgnoreCase)
        || device.Name.Contains("OriG", StringComparison.OrdinalIgnoreCase);

    public HeadsetProtocolUpdate? TryParse(NiceHckMessage message) => message.OpCode switch
    {
        NiceHckOp.Battery when message.Payload.Length >= 3 => new HeadsetProtocolUpdate
        {
            Battery = BatteryInfo.FromLeftRight(
                DecodePercent(message.Payload[0]),
                DecodePercent(message.Payload[1]),
                DecodeCaseBattery(message.Payload[2])),
        },
        NiceHckOp.HeadphoneStatus when message.Payload.Length >= 4 && message.Payload[0] == 1 && message.Payload[2] == 2 => BuildHeadphoneStatus(message.Payload[1], message.Payload[3]),
        NiceHckOp.Version when message.Payload.Length >= 2 => new HeadsetProtocolUpdate
        {
            Firmware = FirmwareVersion.FromProtocolPayload(message.Payload),
        },
        NiceHckOp.AncQuery when message.Payload.Length >= 1 => new HeadsetProtocolUpdate
        {
            AncMode = DecodeAncMode(message.Payload[0]),
        },
        NiceHckOp.EqQuery when message.Payload.Length >= 1 => new HeadsetProtocolUpdate
        {
            Equalizer = DecodeEqualizer(message.Payload[0]),
        },
        NiceHckOp.GameModeQuery or NiceHckOp.LowLatencyQuery or NiceHckOp.DualConnQuery
            or NiceHckOp.InEarQuery or NiceHckOp.WindSuppressionQuery when message.Payload.Length >= 1 => new HeadsetProtocolUpdate
        {
            ToggleFeature = DecodeToggleFeature(message.OpCode),
            ToggleValue = message.Payload[0] == 1,
        },
        _ => null,
    };

    public byte[] BuildAncCommand(NoiseCancellingMode mode) => NiceHckCommands.SetAnc(mode switch
    {
        NoiseCancellingMode.Off => 0x00,
        NoiseCancellingMode.Transparency => 0x01,
        NoiseCancellingMode.Normal => 0x02,
        NoiseCancellingMode.Deep => 0x03,
        NoiseCancellingMode.Experimental => 0x10,
        NoiseCancellingMode.WindSuppression => 0x11,
        _ => throw new ProtocolException($"无法为未知 ANC 模式 {mode} 构造命令。"),
    });

    public byte[] BuildEqualizerCommand(EqualizerPreset preset) => NiceHckCommands.SetEq(preset switch
    {
        EqualizerPreset.Blue => 0x00,
        EqualizerPreset.Balanced => 0x01,
        EqualizerPreset.Bass => 0x02,
        EqualizerPreset.Pure => 0x03,
        EqualizerPreset.Game => 0x04,
        EqualizerPreset.Fine => 0x05,
        EqualizerPreset.Vocal => 0x06,
        _ => throw new ProtocolException($"无法为未知 EQ 预设 {preset} 构造命令。"),
    });

    public byte[] BuildToggleCommand(HeadsetToggleFeature feature, bool enabled) => feature switch
    {
        HeadsetToggleFeature.GameMode => BuildGameModeToggle(enabled),
        HeadsetToggleFeature.LowLatency => BuildLowLatencyToggle(enabled),
        HeadsetToggleFeature.DualConnection => NiceHckCommands.SetDualConnection(enabled),
        HeadsetToggleFeature.InEarDetection => NiceHckCommands.SetInEarDetection(enabled),
        HeadsetToggleFeature.WindSuppression => NiceHckCommands.SetWindSuppression(enabled),
        _ => throw new ProtocolException($"未知开关功能 {feature}。"),
    };

    // V1/V2 分流骨架：GameMode/LowLatency 在旗舰固件（4.7+，V2）上的 payload 差异待 Phase 0/真机确认。
    // 当前不写死任何未确认的 V2 字节，先建立分流入口并沿用 V1 帧。
    private byte[] BuildGameModeToggle(bool enabled)
    {
        _ = SelectVersion(null);
        return NiceHckCommands.SetGameMode(enabled);
    }

    private byte[] BuildLowLatencyToggle(bool enabled)
    {
        _ = SelectVersion(null);
        return NiceHckCommands.SetLowLatency(enabled);
    }

    /// <summary>固件分流：406 及以下走 V1 帧，407 及以上走旗舰 V2 帧（4.8=Minor8 → V2）。</summary>
    private string SelectVersion(FirmwareVersion? fw) => fw is { } && fw.Major == 4 && fw.Minor >= 7 ? "V2" : "V1";

    public byte[] BuildCodecCommand(HeadsetCodec codec, FirmwareVersion? firmware)
    {
        if (firmware?.SupportsModernCodecAndExtendedEq == true)
        {
            return NiceHckCommands.SetCodec((byte)codec);
        }

        if (codec is HeadsetCodec.Aac or HeadsetCodec.Lhdc)
        {
            return NiceHckCommands.SetLegacyLhdc(codec == HeadsetCodec.Lhdc);
        }

        throw new ProtocolException("固件版本未知或低于 4.8，无法发送 SBC 编码切换命令。");
    }

    public byte[] BuildQueryCommand(ushort opCode) => NiceHckCommands.Build(opCode);

    public byte[] BuildCodecQueryCommand() => NiceHckCommands.Build(NiceHckOp.CodecQuery);

    public byte[] BuildToneVolumeCommand(byte volume) => NiceHckCommands.SetToneVolume(volume);

    public byte[] BuildToneVolumeQueryCommand() => NiceHckCommands.QueryToneVolume();

    // 0x03A2 是「动作型」opcode（jadx 确认 gp2.m 发送 930 = 0x03A2），不适用「set = query + 0x0100」规则，
    // 故不存在对应的 0x04A2 查询。该命令当前无 UI 消费者，仅协议就绪。
    public byte[] BuildFindEarbudsCommand(bool enabled) => NiceHckCommands.Build(NiceHckOp.FindEarbuds, enabled ? (byte)1 : (byte)0);

    public byte[] BuildTouchQueryCommand() => NiceHckCommands.Build(NiceHckOp.TouchControlsQuery);

    public byte[] BuildAncTouchQueryCommand() => NiceHckCommands.Build(NiceHckOp.AncTouchQuery);

    // 两个公开实现都把 0x0005 的三个 payload 字节直接当作百分比，未定义 bit7 充电语义。
    // 充电状态不从主控电量帧推断；超过 100 的值也不展示为伪造的百分比。
    private static byte? DecodeCaseBattery(byte raw) => raw is 0 or > 100 ? null : raw;

    private static byte? DecodePercent(byte raw) => raw <= 100 ? raw : null;

    private static NoiseCancellingMode DecodeAncMode(byte raw) => raw switch
    {
        0x00 => NoiseCancellingMode.Off,
        0x01 => NoiseCancellingMode.Transparency,
        0x02 => NoiseCancellingMode.Normal,
        0x03 => NoiseCancellingMode.Deep,
        0x10 => NoiseCancellingMode.Experimental,
        0x11 => NoiseCancellingMode.WindSuppression,
        _ => NoiseCancellingMode.Unknown,
    };

    private static EqualizerPreset DecodeEqualizer(byte raw) => raw switch
    {
        0x00 => EqualizerPreset.Blue,
        0x01 => EqualizerPreset.Balanced,
        0x02 => EqualizerPreset.Bass,
        0x03 => EqualizerPreset.Pure,
        0x04 => EqualizerPreset.Game,
        0x05 => EqualizerPreset.Fine,
        0x06 => EqualizerPreset.Vocal,
        _ => EqualizerPreset.Unknown,
    };

    private static HeadsetToggleFeature DecodeToggleFeature(ushort opCode) => opCode switch
    {
        NiceHckOp.GameModeQuery => HeadsetToggleFeature.GameMode,
        NiceHckOp.LowLatencyQuery => HeadsetToggleFeature.LowLatency,
        NiceHckOp.DualConnQuery => HeadsetToggleFeature.DualConnection,
        NiceHckOp.InEarQuery => HeadsetToggleFeature.InEarDetection,
        NiceHckOp.WindSuppressionQuery => HeadsetToggleFeature.WindSuppression,
        _ => throw new ProtocolException($"未知开关状态操作码 0x{opCode:X4}。"),
    };

    private static HeadphoneStatus DecodeHeadphoneStatus(byte raw) => raw switch
    {
        0x01 => HeadphoneStatus.InBoxClosed,
        0x02 => HeadphoneStatus.InBoxOpen,
        0x03 => HeadphoneStatus.OutBoxOutside,
        0x04 => HeadphoneStatus.InEar,
        _ => HeadphoneStatus.Unknown,
    };

    /// <summary>HEADPHONE_STATUS(0x0006) 帧：同时归约耳状态与充电标志（每只耳在盒 = 充电中，含盖开）。
    /// Battery 只携带充电标志（percent 全 null），由 HeadsetControlService 合并进现有电量快照，
    /// 使充电状态沿 HeadsetControlState 通道驱动 ⚡ 指示，而不覆盖 0x0005 已归约的百分比。</summary>
    private static HeadsetProtocolUpdate BuildHeadphoneStatus(byte leftRaw, byte rightRaw)
    {
        var left = DecodeHeadphoneStatus(leftRaw);
        var right = DecodeHeadphoneStatus(rightRaw);
        return new HeadsetProtocolUpdate
        {
            LeftEarStatus = left,
            RightEarStatus = right,
            Battery = BatteryInfo.FromLeftRight(null, null, null,
                isLeftEarCharging: left is HeadphoneStatus.InBoxClosed or HeadphoneStatus.InBoxOpen,
                isRightEarCharging: right is HeadphoneStatus.InBoxClosed or HeadphoneStatus.InBoxOpen),
        };
    }
}
