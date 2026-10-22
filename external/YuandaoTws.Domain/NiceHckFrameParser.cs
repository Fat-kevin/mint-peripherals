using System.Text;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain;

/// <summary>
/// NiceHCK / BES 白牌耳机控制协议（经典蓝牙 RFCOMM/SPP）。
/// 来源：NiceHCK 开源控制器（Android + Windows 双实现，见 docs/protocol/nicehck-bes-protocol.md），
/// 帧格式：<c>4E | Len(2, 小端) | 00 | OpCode(2, 小端) | Payload</c>，总包长 = Len + 3。
/// 本类型为纯逻辑（可单测），不涉及任何蓝牙 API。
/// </summary>
public static class NiceHckOp
{
    public const ushort Version = 0x0003;          // 固件版本
    public const ushort Battery = 0x0005;          // 电量（L/R/盒）
    public const ushort HeadphoneStatus = 0x0006;  // 耳机入盒/入耳状态（左/右 Lyq1）
    public const ushort AncQuery = 0x0101;         // 查询降噪
    public const ushort AncSet = 0x0201;           // 设置降噪
    public const ushort TouchControlsQuery = 0x0103;  // 查询触控
    public const ushort TouchControlsSet = 0x0203;    // 设置触控 // TODO: set payload 为对象（zt4/t5），未坐实，Phase 0 后补
    public const ushort AncTouchQuery = 0x010A;       // 查询 ANC 触控
    public const ushort AncTouchSet = 0x020A;         // 设置 ANC 触控 // TODO: set payload 为对象，未坐实，Phase 0 后补
    public const ushort FindEarbuds = 0x03A2;         // 查找耳机（动作型 bool）
    public const ushort ToneVolumeQuery = 0x0102;  // 查询提示音音量
    public const ushort ToneVolumeSet = 0x0202;    // 设置提示音音量
    public const ushort EqQuery = 0x0107;          // 查询 EQ
    public const ushort EqSet = 0x0207;            // 设置 EQ
    public const ushort CodecQuery = 0x0104;       // 查询当前编码
    public const ushort CodecSet = 0x0204;         // 设置编码
    public const ushort GameModeQuery = 0x0108;    // 查询游戏模式
    public const ushort GameModeSet = 0x0208;      // 设置游戏模式
    public const ushort LowLatencyQuery = 0x0106;  // 查询低延迟
    public const ushort LowLatencySet = 0x0206;    // 设置低延迟
    public const ushort DualConnQuery = 0x0105;    // 查询双设备连接
    public const ushort DualConnSet = 0x0205;      // 设置双设备连接
    public const ushort InEarQuery = 0x0109;       // 查询入耳检测
    public const ushort InEarSet = 0x0209;         // 设置入耳检测
    public const ushort WindSuppressionQuery = 0x01E1; // 查询抗风噪
    public const ushort WindSuppressionSet = 0x02E1;   // 设置抗风噪
}

/// <summary>一条 NiceHCK 协议帧（OpCode + Payload）。</summary>
public sealed record NiceHckMessage
{
    public required ushort OpCode { get; init; }

    public required byte[] Payload { get; init; }
}

/// <summary>
/// NiceHCK 协议流式解析器：从字节流中切出完整帧（支持粘包/拆包/脏数据重同步）。
/// 算法移植自开源项目 Rust 版 PacketStreamParser（见 docs/protocol/nicehck-bes-protocol.md §2.1）。
/// </summary>
public sealed class NiceHckFrameParser
{
    public const byte Magic = 0x4E;
    private const int HeaderLength = 6;
    private const int MinPayloadLength = 3;
    private const int MaxPayloadLength = 4096;

    private readonly List<byte> _buffer = [];

    /// <summary>喂入一块字节流，返回其中切出的完整帧（残余部分保留在内部缓冲区）。</summary>
    public IReadOnlyList<NiceHckMessage> Feed(byte[] data)
    {
        _buffer.AddRange(data);
        var messages = new List<NiceHckMessage>();

        while (true)
        {
            var start = _buffer.IndexOf(Magic);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }

            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count < HeaderLength)
            {
                break;
            }

            var payloadLength = _buffer[1] | (_buffer[2] << 8);
            if (payloadLength < MinPayloadLength || payloadLength > MaxPayloadLength)
            {
                // 长度字段非法：丢弃首字节重同步（该字节可能是噪声，而非魔数）。
                _buffer.RemoveAt(0);
                continue;
            }

            var packetLength = payloadLength + 3;
            if (_buffer.Count < packetLength)
            {
                break; // 包不完整，等后续数据。
            }

            var opCode = (ushort)(_buffer[4] | (_buffer[5] << 8));
            var payload = _buffer.Skip(HeaderLength).Take(payloadLength - 3).ToArray();
            _buffer.RemoveRange(0, packetLength);
            messages.Add(new NiceHckMessage { OpCode = opCode, Payload = payload });
        }

        return messages;
    }
}

/// <summary>NiceHCK 协议命令帧构造（字节值对照开源项目 tests/protocol.rs 断言）。</summary>
public static class NiceHckCommands
{
    public const byte Magic = 0x4E;

    /// <summary>构造命令帧：Len = 3 + Params 长度，OpCode 小端。</summary>
    public static byte[] Build(ushort opCode, params byte[] params_)
    {
        var payloadLength = 3 + params_.Length;
        var packet = new byte[3 + payloadLength];
        packet[0] = Magic;
        packet[1] = (byte)(payloadLength & 0xFF);
        packet[2] = (byte)((payloadLength >> 8) & 0xFF);
        packet[3] = 0x00;
        packet[4] = (byte)(opCode & 0xFF);
        packet[5] = (byte)((opCode >> 8) & 0xFF);
        Array.Copy(params_, 0, packet, HeaderLength, params_.Length);
        return packet;
    }

    private const int HeaderLength = 6;

    public static byte[] QueryFirmware() => Build(NiceHckOp.Version);
    public static byte[] QueryBattery() => Build(NiceHckOp.Battery);
    public static byte[] QueryAnc() => Build(NiceHckOp.AncQuery);
    public static byte[] QueryToneVolume() => Build(NiceHckOp.ToneVolumeQuery);
    public static byte[] QueryEq() => Build(NiceHckOp.EqQuery);
    public static byte[] QueryGameMode() => Build(NiceHckOp.GameModeQuery);
    public static byte[] QueryLowLatency() => Build(NiceHckOp.LowLatencyQuery);
    public static byte[] QueryDualConnection() => Build(NiceHckOp.DualConnQuery);
    public static byte[] QueryInEarDetection() => Build(NiceHckOp.InEarQuery);
    public static byte[] QueryWindSuppression() => Build(NiceHckOp.WindSuppressionQuery);

    /// <summary>设置降噪模式（参数 [mode, 0x00]）。</summary>
    public static byte[] SetAnc(byte mode) => Build(NiceHckOp.AncSet, mode, 0x00);

    /// <summary>设置提示音音量（参数 volume）。</summary>
    public static byte[] SetToneVolume(byte volume) => Build(NiceHckOp.ToneVolumeSet, volume);

    public static byte[] SetEq(byte preset) => Build(NiceHckOp.EqSet, preset);
    public static byte[] SetGameMode(bool enabled) => Build(NiceHckOp.GameModeSet, enabled ? (byte)1 : (byte)0);
    public static byte[] SetLowLatency(bool enabled) => Build(NiceHckOp.LowLatencySet, enabled ? (byte)1 : (byte)0);
    public static byte[] SetDualConnection(bool enabled) => Build(NiceHckOp.DualConnSet, enabled ? (byte)1 : (byte)0);
    public static byte[] SetInEarDetection(bool enabled) => Build(NiceHckOp.InEarSet, enabled ? (byte)1 : (byte)0);
    public static byte[] SetWindSuppression(bool enabled) => Build(NiceHckOp.WindSuppressionSet, enabled ? (byte)1 : (byte)0);

    /// <summary>固件 4.8+ 的实验性编码切换命令。</summary>
    public static byte[] SetCodec(byte codec) => Build(NiceHckOp.CodecSet, codec);

    /// <summary>旧固件的 LHDC 开关兼容命令。</summary>
    public static byte[] SetLegacyLhdc(bool enabled) => Build(0x0004, enabled ? (byte)1 : (byte)0);
}

/// <summary>
/// NiceHCK 帧语义解码：把帧翻译成可读摘要（用于校验报告与日志）。
/// 电量 payload = [左, 右, 盒]，盒为 0 表示未知；固件 payload = [子版本, 主版本]。
/// </summary>
public static class NiceHckFrameSemantics
{
    /// <summary>降噪模式值 → 名称（00 关 / 01 通透 / 02 普通 / 03 深度 / 10 试验 / 11 风噪）。</summary>
    public static string AncModeName(byte value) => value switch
    {
        0x00 => "关闭",
        0x01 => "通透",
        0x02 => "普通降噪",
        0x03 => "深度降噪",
        0x10 => "试验性降噪",
        0x11 => "风噪抑制",
        _ => $"未知(0x{value:X2})",
    };

    /// <summary>把一条帧翻译为可读摘要；无法识别时返回 null。</summary>
    public static string? Describe(NiceHckMessage message)
    {
        var payload = message.Payload;
        return message.OpCode switch
        {
            NiceHckOp.Battery when payload.Length >= 3 =>
                $"电量：左 {BatteryText(payload[0])} 右 {BatteryText(payload[1])} 盒 {(payload[2] == 0 ? "未知" : BatteryText(payload[2]))}",
            NiceHckOp.AncQuery when payload.Length >= 1 =>
                $"降噪模式：{AncModeName(payload[0])}",
            NiceHckOp.ToneVolumeQuery when payload.Length >= 1 =>
                $"提示音音量：{payload[0]}",
            NiceHckOp.Version when payload.Length >= 2 =>
                $"固件版本：{payload[1]}.{payload[0]}",
            NiceHckOp.EqQuery when payload.Length >= 1 =>
                $"EQ 模式：0x{payload[0]:X2}",
            NiceHckOp.GameModeQuery or NiceHckOp.LowLatencyQuery
                or NiceHckOp.DualConnQuery or NiceHckOp.InEarQuery
                or NiceHckOp.WindSuppressionQuery when payload.Length >= 1 =>
                $"开关状态：{(payload[0] == 1 ? "开" : "关")}",
            _ => null,
        };
    }

    /// <summary>电量字节解码：bit7 = 充电中标志，低 7 位为电量百分比（实测 E4 &amp; 0x7F = 100）。
    /// 注意：bit7 充电标志仅供诊断摘要；真正的充电状态以 HEADPHONE_STATUS 帧为准，不从电量帧 bit7 推断。</summary>
    private static string BatteryText(byte value)
    {
        var percent = value & 0x7F;
        return (value & 0x80) != 0 ? $"{percent}%(充电中)" : $"{percent}%";
    }

    /// <summary>一帧的完整 hex + 语义摘要（按格式重建：Len = 3 + Payload 长度）。</summary>
    public static string FormatFrame(NiceHckMessage message)
    {
        var len = 3 + message.Payload.Length;
        var sb = new StringBuilder();
        sb.Append("4E ");
        sb.Append((len & 0xFF).ToString("X2")).Append(' ');
        sb.Append(((len >> 8) & 0xFF).ToString("X2")).Append(' ');
        sb.Append("00 ");
        sb.Append((message.OpCode & 0xFF).ToString("X2")).Append(' ');
        sb.Append(((message.OpCode >> 8) & 0xFF).ToString("X2")).Append(' ');
        foreach (var b in message.Payload)
        {
            sb.Append(b.ToString("X2")).Append(' ');
        }

        var hex = sb.ToString().TrimEnd();
        var describe = Describe(message);
        return describe is null ? hex : $"{hex} = {describe}";
    }
}
