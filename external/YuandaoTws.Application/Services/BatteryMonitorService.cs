using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Application.Services;

/// <summary>电量状态门面：正式数据来自 <see cref="HeadsetControlService"/> 的 SPP 协议管线。</summary>
public sealed class BatteryMonitorService : IDisposable
{
    private readonly Subject<BatteryInfo> _batteryChanged = new();
    private readonly IDisposable _stateSubscription;
    private BatteryInfo? _latest;

    public BatteryMonitorService(HeadsetControlService control, ILogger<BatteryMonitorService> logger)
    {
        _stateSubscription = control.StateChanged.Subscribe(state =>
        {
            if (state.Battery is not null)
            {
                _latest = state.Battery;
                _batteryChanged.OnNext(state.Battery);
            }
        });
    }

    public IObservable<BatteryInfo> BatteryChanged => _batteryChanged;
    public BatteryInfo? Latest => _latest;
    public bool IsPrivateBatteryAvailable => true;

    /// <summary>保留旧诊断调用兼容性；正式主链路不再使用 GATT 电量。</summary>
    public Task AttachSessionAsync(IGattDeviceSession session, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DetachAsync()
    {
        _latest = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stateSubscription.Dispose();
        _batteryChanged.Dispose();
    }
}
