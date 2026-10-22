using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Settings;

namespace DeviceCenterPreview.Core.Services;

/// <summary>
/// 真实服务初始化失败时的占位实现：只报告失败原因，绝不返回模拟设备或电量。
/// </summary>
public sealed class UnavailablePeripheralService(string reason) : IPeripheralService
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler<RemoteButtonEvent>? RemoteButtonReceived
    {
        add { }
        remove { }
    }

    public void ApplySettings(AppSettings settings)
    {
    }

    public IReadOnlyList<DeviceOption> GetDevices(DeviceKind kind) => [];

    public string GetSelectedDeviceId(DeviceKind kind) => "";

    public void SelectDevice(DeviceKind kind, string deviceId)
    {
    }

    public PeripheralSnapshot GetSnapshot(DeviceKind kind) => new(
        kind,
        "",
        "服务不可用",
        false,
        null,
        ChargingState.Unknown,
        [],
        reason,
        false,
        reason);

    public HeadsetControls GetHeadsetControls() => HeadsetControls.Default;

    public Task<WriteResult> UpdateHeadsetControlsAsync(HeadsetControls controls) =>
        Task.FromResult(WriteResult.Fail(reason));

    public MouseControls GetMouseControls() => new("", null, null, []);

    public Task<WriteResult> UpdateMouseControlsAsync(MouseControls controls) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task RefreshMouseAsync() => Task.CompletedTask;

    public RemoteControls GetRemoteControls() => new("", false, false, []);

    public void UpdateRemoteControls(RemoteControls controls)
    {
    }

    public Task FindHeadsetAsync(bool enabled) => Task.CompletedTask;

    public Task<IReadOnlyList<RemoteDeviceInfo>> ScanRemotesAsync() =>
        Task.FromResult<IReadOnlyList<RemoteDeviceInfo>>([]);

    public Task<WriteResult> ConnectRemoteAsync(string deviceId) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<WriteResult> DisconnectRemoteAsync() =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<IReadOnlyList<AudioEndpointInfo>> GetRemoteAudioEndpointsAsync() =>
        Task.FromResult<IReadOnlyList<AudioEndpointInfo>>([]);

    public Task<IReadOnlyList<KeyMapping>> GetRemoteMappingsAsync() =>
        Task.FromResult<IReadOnlyList<KeyMapping>>([]);

    public Task<WriteResult> SetRemoteAudioEndpointAsync(string endpointId) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<WriteResult> SetRemoteGainAsync(int gainDb) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<WriteResult> SetRemoteVoiceToolAsync(string tool) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<string?> GetRemoteVoiceToolAsync() => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> GetRemoteHotkeyAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<WriteResult> SaveRemoteMappingAsync(string key, string gesture, string actionJson) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<WriteResult> TestRemoteMappingAsync(string key, string gesture) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<WriteResult> SetRemoteMappingSuspensionAsync(bool suspended) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<WriteResult> SetRemoteFullKeyAsync(bool enabled) =>
        Task.FromResult(WriteResult.Fail(reason));

    public Task<RemoteFullKeyStatus> GetRemoteFullKeyStatusAsync() =>
        Task.FromResult(new RemoteFullKeyStatus(false, false, reason));
}
