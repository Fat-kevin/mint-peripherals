using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain;

/// <summary>标准蓝牙电量帧解析（纯逻辑，可单元测试）。私有协议解析在各品牌适配器内实现。</summary>
public static class BatteryFrameParser
{
    /// <summary>
    /// 解析标准 0x2A19 电量帧：1 字节，0–100%。
    /// 帧非法（长度不足）时返回 null。
    /// </summary>
    public static BatteryInfo? ParseStandard(byte[] frame)
    {
        if (frame.Length < 1)
        {
            return null;
        }

        return BatteryInfo.FromSingleValue(frame[0]);
    }
}
