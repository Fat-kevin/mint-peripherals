using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain;

/// <summary>
/// RFCOMM 服务 UUID 名称表（纯逻辑，可单测）。标准服务按蓝牙联盟分配号命名，
/// 未知 UUID 回退为短形式。仅作展示辅助，不影响协议判定。
/// </summary>
public static class RfcommServiceNames
{
    /// <summary>标准串口服务 UUID（0x1101）。</summary>
    public static readonly Guid SerialPort = SppUuid(0x1101);

    /// <summary>输出服务 UUID 的可读描述：已知标准服务显示名称，未知显示短 UUID。</summary>
    public static string Describe(Guid serviceId) =>
        KnownNames.TryGetValue(serviceId, out var name)
            ? $"{name} ({serviceId})"
            : $"{ShortForm(serviceId)}（私有）";

    /// <summary>
    /// 取 UUID 的短形式：形如 0000XXXX-0000-1000-8000-00805F9B34FB 的标准分配号返回 XXXX，
    /// 其余返回完整字符串。
    /// </summary>
    public static string ShortForm(Guid uuid)
    {
        const string standardBaseSuffix = "-0000-1000-8000-00805f9b34fb";
        var text = uuid.ToString();
        if (text.StartsWith("0000", StringComparison.OrdinalIgnoreCase)
            && text.EndsWith(standardBaseSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return text.Substring(4, 4).ToUpperInvariant();
        }

        return text;
    }

    private static Guid SppUuid(ushort shortId) =>
        new($"0000{shortId:X4}-0000-1000-8000-00805F9B34FB");

    private static readonly IReadOnlyDictionary<Guid, string> KnownNames = new Dictionary<Guid, string>
    {
        // 本机与 TWS 耳机相关的常见 RFCOMM 服务
        [SppUuid(0x1101)] = "串口 (Serial Port / SPP)",
        [SppUuid(0x1108)] = "耳机 (Headset)",
        [SppUuid(0x110A)] = "音频源 (A2DP Source)",
        [SppUuid(0x110B)] = "音频接收端 (A2DP Sink)",
        [SppUuid(0x110C)] = "AV 遥控目标 (AVRCP Target)",
        [SppUuid(0x110E)] = "AV 遥控控制端 (AVRCP Controller)",
        [SppUuid(0x110F)] = "AV 遥控控制 (AVRCP)",
        [SppUuid(0x1112)] = "耳机音频网关 (Headset Audio Gateway)",
        [SppUuid(0x111E)] = "免提 (Handsfree)",
        [SppUuid(0x111F)] = "免提音频网关 (Handsfree Audio Gateway)",
        [SppUuid(0x1105)] = "OBEX 对象推送 (OBEX Object Push)",
        [SppUuid(0x1106)] = "OBEX 文件传输 (OBEX File Transfer)",
        [SppUuid(0x1200)] = "PnP 信息 (PnP Information)",
    };
}
