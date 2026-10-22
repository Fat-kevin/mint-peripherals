using YuandaoTws.Domain.Enums;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Domain.Abstractions;

/// <summary>
/// 品牌/机型协议适配器。协议层仅处理 RFCOMM 服务、帧编码与帧语义，
/// 不持有 SPP 会话，也不暴露任何 WinRT 类型。
/// </summary>
public interface IDeviceProtocol
{
    string Name { get; }

    /// <summary>正式控制通道的 RFCOMM 服务 UUID。</summary>
    Guid ServiceUuid { get; }

    bool Matches(HeadsetDevice device);

    /// <summary>把一条已分帧的 NiceHCK 消息转换为结构化状态；非状态帧返回 null。</summary>
    HeadsetProtocolUpdate? TryParse(NiceHckMessage message);

    byte[] BuildAncCommand(NoiseCancellingMode mode);

    byte[] BuildEqualizerCommand(EqualizerPreset preset);

    byte[] BuildToggleCommand(HeadsetToggleFeature feature, bool enabled);

    byte[] BuildCodecCommand(HeadsetCodec codec, FirmwareVersion? firmware);

    byte[] BuildCodecQueryCommand();

    byte[] BuildToneVolumeCommand(byte volume);

    byte[] BuildToneVolumeQueryCommand();

    byte[] BuildFindEarbudsCommand(bool enabled);

    byte[] BuildTouchQueryCommand();

    byte[] BuildAncTouchQueryCommand();

    byte[] BuildQueryCommand(ushort opCode);
}
