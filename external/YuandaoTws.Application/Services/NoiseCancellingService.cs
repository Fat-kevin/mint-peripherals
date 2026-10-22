using System.Reactive.Subjects;
using YuandaoTws.Domain.Enums;

namespace YuandaoTws.Application.Services;

/// <summary>ANC 兼容门面，实际命令与回查由统一 SPP 控制服务执行。</summary>
public sealed class NoiseCancellingService : IDisposable
{
    private readonly HeadsetControlService _control;
    private readonly Subject<NoiseCancellingMode> _modeChanged = new();
    private readonly IDisposable _subscription;

    public NoiseCancellingService(HeadsetControlService control)
    {
        _control = control;
        _subscription = control.StateChanged.Subscribe(state =>
        {
            if (state.AncMode is { } mode)
                _modeChanged.OnNext(mode);
        });
    }

    public IObservable<NoiseCancellingMode> ModeChanged => _modeChanged;
    public bool IsAvailable => _control.IsConnected;
    public Task SetModeAsync(NoiseCancellingMode mode, CancellationToken cancellationToken) => _control.SetAncAsync(mode, cancellationToken);
    public Task DetachAsync() => Task.CompletedTask;
    public void Dispose() { _subscription.Dispose(); _modeChanged.Dispose(); }
}
