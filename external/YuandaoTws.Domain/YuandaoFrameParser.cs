using System.Text;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain;

/// <summary>
/// 原道「原点」变体帧（实测推断格式）：<c>03 | Id(1) | 00 | Len(1) | Payload</c>，
/// 总包长 = 4 + Len。实测基线帧（docs/protocol/yuandao-origin.md §3.12）：
/// <c>03 01 00 03 44 18 D8</c>、<c>03 02 00 06 6C A9 CC 75 1E A4</c>、<c>03 03 00 03 64 64 64</c>。
/// 与 NiceHCK 协议（4E 头 6 字节）同族不同变体；id=03 帧 payload [L,R,Case] 为设备状态/电量候选字段。
/// 本类型为纯逻辑（可单测），不涉及任何蓝牙 API。
/// </summary>
public sealed record YuandaoMessage
{
    public required byte Id { get; init; }

    public required byte[] Payload { get; init; }
}

/// <summary>
/// 原道变体帧流式解析器（粘包/拆包/脏数据重同步）。
/// 严格校验防误判：id ≤ 0x20、len ∈ [1,64]、len 与实际载荷字节数精确匹配——
/// 使 NiceHCK 帧（4E 头）等其它数据流不会被误切成原道帧。
/// </summary>
public sealed class YuandaoFrameParser
{
    public const byte Magic = 0x03;
    private const int HeaderLength = 4;
    private const int MinPayloadLength = 1;
    private const int MaxPayloadLength = 64;
    private const byte MaxId = 0x20;

    private readonly List<byte> _buffer = [];

    /// <summary>喂入一块字节流，返回其中切出的完整帧（残余部分保留在内部缓冲区）。</summary>
    public IReadOnlyList<YuandaoMessage> Feed(byte[] data)
    {
        _buffer.AddRange(data);
        var messages = new List<YuandaoMessage>();

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

            // 结构校验：id 上限、保留位必须为 0、len 范围。
            var id = _buffer[1];
            if (id > MaxId || _buffer[2] != 0)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var payloadLength = _buffer[3];
            if (payloadLength < MinPayloadLength || payloadLength > MaxPayloadLength)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var packetLength = HeaderLength + payloadLength;
            if (_buffer.Count < packetLength)
            {
                break; // 包不完整，等后续数据。
            }

            var payload = _buffer.Skip(HeaderLength).Take(payloadLength).ToArray();
            _buffer.RemoveRange(0, packetLength);
            messages.Add(new YuandaoMessage { Id = id, Payload = payload });
        }

        return messages;
    }
}

/// <summary>原道变体帧命令构造（格式推测：03 &lt;id&gt; 00 00 查询，id 即功能码）。</summary>
public static class YuandaoCommands
{
    public const byte Magic = 0x03;

    /// <summary>构造查询帧：03 &lt;id&gt; 00 00（无载荷）。</summary>
    public static byte[] Query(byte id) => [Magic, id, 0x00, 0x00];
}

/// <summary>原道变体帧语义解码（仅使用已被实测确认的字段，不猜测充电状态）。</summary>
public static class YuandaoFrameSemantics
{
    /// <summary>
    /// 解析原道状态服务的 id=03 三字节状态帧。
    /// 当前实测仅能确认三个字节可作为 [左耳、右耳、耳机盒] 的候选电量值，
    /// 且 0xFF 会在耳机离盒时出现。USB 插拔前后帧完全一致，因此不能把 bit7 当作充电标志。
    /// </summary>
    public static BatteryInfo? TryParseBattery(YuandaoMessage message)
    {
        if (message.Id != 0x03 || message.Payload.Length != 3)
        {
            return null;
        }

        var left = DecodePercent(message.Payload[0]);
        var right = DecodePercent(message.Payload[1]);
        var casePercent = DecodePercent(message.Payload[2]);
        return BatteryInfo.FromLeftRight(
            left,
            right,
            casePercent);
    }

    /// <summary>把一条帧翻译为可读摘要；无法识别时返回 null。</summary>
    public static string? Describe(YuandaoMessage message)
    {
        var payload = message.Payload;
        return message.Id switch
        {
            // id=03：3 字节 [L,R,Case]。当前协议没有被确认的充电字段；0xFF 视为未知。
            0x03 when payload.Length == 3 =>
                $"状态电量（未含充电标志）：左 {BatteryText(payload[0])} 右 {BatteryText(payload[1])} 盒 {BatteryText(payload[2])}",
            0x02 => "设备标识（疑似）：" + string.Join(" ", payload.Select(b => b.ToString("X2"))),
            _ => null,
        };
    }

    /// <summary>仅接受 0–100 的直接百分比；其它值（例如耳机离盒时的 0xFF）按未知处理。</summary>
    private static string BatteryText(byte value)
    {
        return DecodePercent(value) is { } percent ? $"{percent}%" : "未知";
    }

    private static byte? DecodePercent(byte value) => value <= 100 ? value : null;

    /// <summary>一帧的完整 hex + 语义摘要。</summary>
    public static string FormatFrame(YuandaoMessage message)
    {
        var sb = new StringBuilder();
        sb.Append("03 ");
        sb.Append(message.Id.ToString("X2")).Append(" 00 ");
        sb.Append(message.Payload.Length.ToString("X2")).Append(' ');
        foreach (var b in message.Payload)
        {
            sb.Append(b.ToString("X2")).Append(' ');
        }

        var hex = sb.ToString().TrimEnd();
        var describe = Describe(message);
        return describe is null ? hex : $"{hex} = {describe}";
    }
}
