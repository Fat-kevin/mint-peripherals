using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Settings;

namespace DeviceCenterPreview.Core.Services;

/// <summary>预览版的设备数据契约；后续可用真实集成替换 MockPeripheralService。</summary>
public interface IPeripheralService
{
    event EventHandler? Changed;

    /// <summary>遥控器语义按键边沿；在引擎线程触发，订阅方自行切回 UI 线程。</summary>
    event EventHandler<RemoteButtonEvent>? RemoteButtonReceived;

    /// <summary>应用设置变化后立即生效；各实现必须遵守模块开关与写入授权。</summary>
    void ApplySettings(AppSettings settings);

    IReadOnlyList<DeviceOption> GetDevices(DeviceKind kind);

    PeripheralSnapshot GetSnapshot(DeviceKind kind);

    string GetSelectedDeviceId(DeviceKind kind);

    void SelectDevice(DeviceKind kind, string deviceId);

    HeadsetControls GetHeadsetControls();

    Task<WriteResult> UpdateHeadsetControlsAsync(HeadsetControls controls);

    MouseControls GetMouseControls();

    Task<WriteResult> UpdateMouseControlsAsync(MouseControls controls);

    Task RefreshMouseAsync();

    RemoteControls GetRemoteControls();

    void UpdateRemoteControls(RemoteControls controls);

    Task<IReadOnlyList<RemoteDeviceInfo>> ScanRemotesAsync();

    Task<WriteResult> ConnectRemoteAsync(string deviceId);

    Task<WriteResult> DisconnectRemoteAsync();

    Task<IReadOnlyList<AudioEndpointInfo>> GetRemoteAudioEndpointsAsync();

    Task<IReadOnlyList<KeyMapping>> GetRemoteMappingsAsync();

    Task<WriteResult> SetRemoteAudioEndpointAsync(string endpointId);

    Task<WriteResult> SetRemoteGainAsync(int gainDb);

    Task<WriteResult> SetRemoteVoiceToolAsync(string tool);

    Task<string?> GetRemoteVoiceToolAsync();

    Task<IReadOnlyList<string>> GetRemoteHotkeyAsync();

    Task<WriteResult> SaveRemoteMappingAsync(string key, string gesture, string actionJson);

    Task<WriteResult> TestRemoteMappingAsync(string key, string gesture);

    /// <summary>接收测试期间只观察、不注入动作。</summary>
    Task<WriteResult> SetRemoteMappingSuspensionAsync(bool suspended);

    Task<WriteResult> SetRemoteFullKeyAsync(bool enabled);

    Task<RemoteFullKeyStatus> GetRemoteFullKeyStatusAsync();

    Task FindHeadsetAsync(bool enabled);
}
