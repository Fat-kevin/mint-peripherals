namespace DeviceCenterPreview.Core.Models;

/// <summary>预览版支持的设备类别。</summary>
public enum DeviceKind
{
    Headset,
    Mouse,
    Remote,
}

/// <summary>充电状态，取值与 VigilWatt 的 DeviceStatus 对齐。</summary>
public enum ChargingState
{
    Unknown,
    Discharging,
    Charging,
    Full,
}

/// <summary>耳机左/右/盒等分量电量。</summary>
public sealed record DeviceComponentBattery(
    string Key,
    string Label,
    int? Percent,
    ChargingState Charging);

/// <summary>设备中心与二级页共用的只读快照。</summary>
public sealed record PeripheralSnapshot(
    DeviceKind Kind,
    string DeviceId,
    string DisplayName,
    bool IsConnected,
    int? BatteryPercent,
    ChargingState Charging,
    IReadOnlyList<DeviceComponentBattery> Components,
    string StatusText,
    bool ControlsAvailable = false,
    string? ControlHint = null,
    string? Firmware = null,
    bool WriteAllowed = true);

/// <summary>设备选择器中的一项。</summary>
public sealed record DeviceOption(string Id, string Name, bool IsConnected);

/// <summary>耳机六态降噪。</summary>
public enum AncMode
{
    Off,
    Transparency,
    Normal,
    Deep,
    Experimental,
    WindNoise,
}

/// <summary>枚举的中文显示名，UI 与模拟服务共用。</summary>
public static class PeripheralDisplayNames
{
    public static string Anc(AncMode mode) => mode switch
    {
        AncMode.Off => "关闭",
        AncMode.Transparency => "通透",
        AncMode.Normal => "普通",
        AncMode.Deep => "深度",
        AncMode.Experimental => "实验性",
        AncMode.WindNoise => "风噪",
        _ => mode.ToString(),
    };
}

/// <summary>耳机七档 EQ。</summary>
public enum EqPreset
{
    Tears,
    Balanced,
    Western,
    TrueTone,
    Gaming,
    Delicate,
    WarmVocal,
}

/// <summary>耳机控制状态。</summary>
public sealed record HeadsetControls(
    AncMode Anc,
    EqPreset Eq,
    bool GameMode,
    bool LowLatency,
    bool DualDevice,
    bool InEarDetection,
    bool WindNoise,
    bool IsKnown = true)
{
    /// <summary>未连接/未读到状态时的中性值；IsKnown=false 时页面不得把它当成真实档位。</summary>
    public static HeadsetControls Default { get; } =
        new(AncMode.Off, EqPreset.Balanced, false, false, false, false, false, IsKnown: false);
}

/// <summary>鼠标按键动作；Id 为设备槽位，空表示只读展示。</summary>
public sealed record ButtonMapping(string Key, string Action, string? Id = null);

/// <summary>鼠标控制状态；读取不到时 Dpi/PollingRateHz 为 null，不伪造数值。</summary>
public sealed record MouseControls(
    string DeviceId,
    int? Dpi,
    int? PollingRateHz,
    IReadOnlyList<ButtonMapping> Buttons,
    int? DpiMin = null,
    int? DpiMax = null,
    int? DpiStep = null,
    IReadOnlyList<int>? SupportedPollingRates = null,
    bool ReadOnly = false,
    IReadOnlyList<int>? DpiStages = null,
    int? DpiStageIndex = null,
    bool OnboardProfilesAvailable = false,
    bool OnboardMode = false,
    string? ProfileName = null,
    int? ProfileFormat = null,
    int? ProfileSize = null,
    bool OnboardProfileWritable = false,
    bool WriteSupported = false,
    string? WriteUnsupportedReason = null);

/// <summary>遥控器按键动作。</summary>
public sealed record KeyMapping(
    string Key,
    string Gesture,
    string Action,
    string? KeyId = null,
    string? GestureId = null,
    string? ActionId = null);

/// <summary>遥控器控制状态。</summary>
public sealed record RemoteControls(
    string DeviceId,
    bool VoiceReady,
    bool FullKeySupport,
    IReadOnlyList<KeyMapping> Keys,
    bool EngineConnected = false,
    string? AudioEndpointId = null,
    string? AudioEndpointName = null,
    int? GainDb = null,
    string? VoiceTool = null,
    string RawInputPhase = "",
    int MatchedDeviceCount = 0,
    long RawEventCount = 0,
    long FiredGestures = 0,
    bool GateActive = false,
    long SemanticEdgeCount = 0,
    string LastButton = "",
    bool LastPressed = false,
    string ActiveButtons = "",
    bool MappingsEnabled = false,
    bool ListenerActive = false);

/// <summary>引擎扫描到的已配对遥控器；Id 只在本进程内用于连接，界面显示 Name/Model。</summary>
public sealed record RemoteDeviceInfo(
    string Id,
    string Token,
    string Name,
    string Model,
    bool Supported)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model) ||
        string.Equals(Model, "unknown", StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Name}（{Model}）";
}

/// <summary>全按键 Helper 的真实状态；Available=false 时 UI 必须禁用开关。</summary>
public sealed record RemoteFullKeyStatus(
    bool Enabled,
    bool Available,
    string? Message);

/// <summary>遥控器引擎暴露的音频端点。</summary>
public sealed record AudioEndpointInfo(string Id, string Name, bool Recommended);

/// <summary>遥控器引擎推送的语义按键边沿，用于“接收测试”。</summary>
public sealed record RemoteButtonEvent(string Button, bool Pressed);
