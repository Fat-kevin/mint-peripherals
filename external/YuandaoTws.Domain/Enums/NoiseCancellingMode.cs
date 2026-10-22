namespace YuandaoTws.Domain.Enums;

/// <summary>耳机 ANC 模式。协议未知值不会被降级为某个默认模式。</summary>
public enum NoiseCancellingMode
{
    Unknown = 0,
    Off = 1,
    Transparency = 2,
    Normal = 3,
    Deep = 4,
    Experimental = 5,
    WindSuppression = 6,

    /// <summary>兼容早期三态 UI 的“降噪”名称，等同于深度降噪。</summary>
    AncOn = Deep,
}
