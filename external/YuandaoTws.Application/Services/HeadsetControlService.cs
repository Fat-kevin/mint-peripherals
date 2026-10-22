using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using YuandaoTws.Domain;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Enums;
using YuandaoTws.Domain.Exceptions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Application.Services;

/// <summary>单一 SPP 协议管线：分帧、状态归约、命令串行化与写后回查。</summary>
public sealed class HeadsetControlService : IDisposable
{
    private static readonly TimeSpan CommandPacing = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly ushort[] InitialRefreshOperations =
    [
        NiceHckOp.Version, NiceHckOp.Battery, NiceHckOp.AncQuery, NiceHckOp.EqQuery,
        NiceHckOp.GameModeQuery, NiceHckOp.LowLatencyQuery, NiceHckOp.DualConnQuery,
        NiceHckOp.InEarQuery, NiceHckOp.WindSuppressionQuery,
    ];
    private readonly IDeviceProtocol _protocol;
    private readonly ILogger<HeadsetControlService> _logger;
    private readonly Subject<HeadsetControlState> _stateChanged = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private IDisposable? _dataSubscription;
    private CancellationTokenSource? _sessionCts;
    private Task? _batteryPollingTask;
    private NiceHckFrameParser? _parser;
    private ISppDeviceSession? _session;
    private TaskCompletionSource<NiceHckMessage>? _pendingResponse;
    private ushort? _pendingOpCode;
    private int _disposed;

    public HeadsetControlService(IDeviceProtocol protocol, ILogger<HeadsetControlService> logger)
    {
        _protocol = protocol;
        _logger = logger;
    }

    public HeadsetControlState State { get; private set; } = new();
    public IObservable<HeadsetControlState> StateChanged => _stateChanged;
    public bool IsConnected => _session?.IsConnected == true;

    public async Task AttachAsync(ISppDeviceSession session, long generation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await DetachAsync();
        _session = session;
        _parser = new NiceHckFrameParser();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _dataSubscription = session.DataReceived.Subscribe(chunk =>
        {
            try
            {
                ProcessChunk(chunk.Value);
            }
            catch (Exception ex)
            {
                // 协议解析或观察者异常不能反向终止 SPP 读循环。
                _logger.LogError(ex, "处理 SPP 控制数据失败");
            }
        });
        await RefreshAsync(_sessionCts.Token);
        await RefreshHeadphoneStatusAsync(_sessionCts.Token);
        StartBatteryPolling(generation, _sessionCts.Token);
    }

    public async Task DetachAsync()
    {
        var sessionCts = _sessionCts;
        sessionCts?.Cancel();
        _sessionCts = null;
        _dataSubscription?.Dispose();
        _dataSubscription = null;
        var pollingTask = _batteryPollingTask;
        _batteryPollingTask = null;
        if (pollingTask is not null)
        {
            try
            {
                await pollingTask;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "等待电量轮询结束失败");
            }
        }

        sessionCts?.Dispose();
        _parser = null;
        _session = null;
        FailPending(new BluetoothConnectionException("SPP 控制会话已失效。"));
        State = new();
        PublishState(State);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        foreach (var opCode in InitialRefreshOperations)
        {
            await QueryAsync(opCode, cancellationToken);
            await Task.Delay(CommandPacing, cancellationToken);
        }
    }

    /// <summary>连接后尽力查询一次 HEADPHONE_STATUS(0x0006)，使「刚开盖/在盒」的耳机立刻上报「在盒 = 充电」。
    /// 该查询非致命：若固件只推送、不响应 0x0006 查询，短超时被吞掉，不影响连接与开机状态同步。</summary>
    private async Task RefreshHeadphoneStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await QueryAsync(NiceHckOp.HeadphoneStatus, cancellationToken, TimeSpan.FromMilliseconds(1500));
        }
        catch (ProtocolException ex)
        {
            _logger.LogDebug(ex, "HEADPHONE_STATUS(0x0006) 查询未响应，充电状态等待设备推送");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("HEADPHONE_STATUS(0x0006) 查询被取消");
        }
    }

    public Task SetAncAsync(NoiseCancellingMode mode, CancellationToken cancellationToken) =>
        SetThenQueryAsync(_protocol.BuildAncCommand(mode), NiceHckOp.AncQuery, cancellationToken);

    public Task SetEqualizerAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        SetThenQueryAsync(_protocol.BuildEqualizerCommand(preset), NiceHckOp.EqQuery, cancellationToken);

    public Task SetToggleAsync(HeadsetToggleFeature feature, bool enabled, CancellationToken cancellationToken) =>
        SetThenQueryAsync(_protocol.BuildToggleCommand(feature, enabled), QueryOp(feature), cancellationToken);

    public async Task SetCodecExperimentalAsync(HeadsetCodec codec, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            await RequireSession().WriteAsync(_protocol.BuildCodecCommand(codec, State.Firmware), cancellationToken);
            State = State with { LastRequestedCodec = codec, UpdatedAt = DateTimeOffset.Now };
            PublishState(State);
        }
        finally { _commandLock.Release(); }
    }

    /// <summary>发送「查找耳机」命令（0x03A2 动作型，无响应帧，fire-and-forget）。
    /// <paramref name="enabled"/> = true 触发鸣响；false 为 best-effort 停止（0x03A2 值 0 的停机语义未坐实）。
    /// 调用方负责在触发前完成 5 秒防呆倒计时；倒计时内取消即不发送，永不鸣响。</summary>
    public async Task SendFindEarbudsAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            await RequireSession().WriteAsync(_protocol.BuildFindEarbudsCommand(enabled), cancellationToken);
        }
        finally { _commandLock.Release(); }
    }

    private async Task SetThenQueryAsync(byte[] setFrame, ushort queryOpCode, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            await RequireSession().WriteAsync(setFrame, cancellationToken);
            await Task.Delay(CommandPacing, cancellationToken);
            await QueryCoreAsync(queryOpCode, cancellationToken);
        }
        finally { _commandLock.Release(); }
    }

    private async Task QueryAsync(ushort opCode, CancellationToken cancellationToken, TimeSpan? queryTimeout = null)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try { await QueryCoreAsync(opCode, cancellationToken, queryTimeout); }
        finally { _commandLock.Release(); }
    }

    private async Task QueryCoreAsync(ushort opCode, CancellationToken cancellationToken, TimeSpan? queryTimeout = null)
    {
        var waiter = new TaskCompletionSource<NiceHckMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingOpCode = opCode;
        _pendingResponse = waiter;
        try
        {
            await RequireSession().WriteAsync(_protocol.BuildQueryCommand(opCode), cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(queryTimeout ?? ResponseTimeout);
            await waiter.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProtocolException($"等待协议响应 0x{opCode:X4} 超时。");
        }
        finally
        {
            _pendingOpCode = null;
            _pendingResponse = null;
        }
    }

    private void ProcessChunk(byte[] data)
    {
        var parser = _parser;
        if (parser is null) return;
        foreach (var message in parser.Feed(data))
        {
            ApplyUpdate(_protocol.TryParse(message));
            if (_pendingOpCode == message.OpCode)
                _pendingResponse?.TrySetResult(message);
        }
    }

    private void ApplyUpdate(HeadsetProtocolUpdate? update)
    {
        if (update is null) return;
        if (update.LeftEarStatus.HasValue || update.RightEarStatus.HasValue)
        {
            _logger.LogInformation("HEADPHONE_STATUS：L={Left}({LCharging}) R={Right}({RCharging})",
                update.LeftEarStatus, update.Battery?.IsLeftEarCharging, update.RightEarStatus, update.Battery?.IsRightEarCharging);
        }

        var state = State;
        var changed = false;
        if (update.Battery is { } battery)
        {
            // HEADPHONE_STATUS 的 Battery 只携带充电标志（percent 全 null）；把它合并进现有电量快照，
            // 避免清掉 0x0005 帧已归约的百分比，同时让充电标志沿 HeadsetControlState 通道驱动 ⚡ 指示。
            var merged = MergeBattery(state.Battery, battery);
            if (!Equals(state.Battery, merged))
            {
                state = state with { Battery = merged };
                changed = true;
            }
        }

        if (update.Firmware is not null && !Equals(state.Firmware, update.Firmware))
        {
            state = state with { Firmware = update.Firmware };
            changed = true;
        }

        if (update.AncMode is not null && state.AncMode != update.AncMode)
        {
            state = state with { AncMode = update.AncMode };
            changed = true;
        }

        if (update.Equalizer is not null && state.Equalizer != update.Equalizer)
        {
            state = state with { Equalizer = update.Equalizer };
            changed = true;
        }

        if (update.ToggleFeature is { } feature && update.ToggleValue is { } value)
        {
            var current = feature switch
            {
                HeadsetToggleFeature.GameMode => state.GameModeEnabled,
                HeadsetToggleFeature.LowLatency => state.LowLatencyEnabled,
                HeadsetToggleFeature.DualConnection => state.DualConnectionEnabled,
                HeadsetToggleFeature.InEarDetection => state.InEarDetectionEnabled,
                HeadsetToggleFeature.WindSuppression => state.WindSuppressionEnabled,
                _ => null,
            };
            if (current != value)
            {
                state = feature switch
                {
                    HeadsetToggleFeature.GameMode => state with { GameModeEnabled = value },
                    HeadsetToggleFeature.LowLatency => state with { LowLatencyEnabled = value },
                    HeadsetToggleFeature.DualConnection => state with { DualConnectionEnabled = value },
                    HeadsetToggleFeature.InEarDetection => state with { InEarDetectionEnabled = value },
                    HeadsetToggleFeature.WindSuppression => state with { WindSuppressionEnabled = value },
                    _ => state,
                };
                changed = true;
            }
        }

        if (update.LeftEarStatus is { } leftStatus && state.LeftEarStatus != leftStatus)
        {
            state = state with { LeftEarStatus = leftStatus };
            changed = true;
        }

        if (update.RightEarStatus is { } rightStatus && state.RightEarStatus != rightStatus)
        {
            state = state with { RightEarStatus = rightStatus };
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        State = state with { UpdatedAt = DateTimeOffset.Now };
        PublishState(State);
    }

    /// <summary>合并电量快照：incoming 的百分比/充电标志优先，缺失（null）时保留 existing。
    /// 使 HEADPHONE_STATUS 携带的充电标志不会覆盖 0x0005 帧已归约的百分比。</summary>
    private static BatteryInfo MergeBattery(BatteryInfo? existing, BatteryInfo incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        return existing with
        {
            LeftEarPercent = incoming.LeftEarPercent ?? existing.LeftEarPercent,
            RightEarPercent = incoming.RightEarPercent ?? existing.RightEarPercent,
            CasePercent = incoming.CasePercent ?? existing.CasePercent,
            IsLeftEarCharging = incoming.IsLeftEarCharging ?? existing.IsLeftEarCharging,
            IsRightEarCharging = incoming.IsRightEarCharging ?? existing.IsRightEarCharging,
            IsCaseCharging = incoming.IsCaseCharging ?? existing.IsCaseCharging,
        };
    }

    private void StartBatteryPolling(long generation, CancellationToken cancellationToken) => _batteryPollingTask = BatteryPollingAsync(generation, cancellationToken);

    private async Task BatteryPollingAsync(long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) await QueryAsync(NiceHckOp.Battery, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "SPP 电量轮询结束（会话 {Generation}）", generation); }
    }

    private static ushort QueryOp(HeadsetToggleFeature feature) => feature switch
    {
        HeadsetToggleFeature.GameMode => NiceHckOp.GameModeQuery,
        HeadsetToggleFeature.LowLatency => NiceHckOp.LowLatencyQuery,
        HeadsetToggleFeature.DualConnection => NiceHckOp.DualConnQuery,
        HeadsetToggleFeature.InEarDetection => NiceHckOp.InEarQuery,
        HeadsetToggleFeature.WindSuppression => NiceHckOp.WindSuppressionQuery,
        _ => throw new ProtocolException($"未知开关功能 {feature}。"),
    };

    private ISppDeviceSession RequireSession() => _session ?? throw new BluetoothConnectionException("尚未建立 SPP 控制会话。");
    private void FailPending(Exception exception) => _pendingResponse?.TrySetException(exception);

    private void PublishState(HeadsetControlState state)
    {
        try
        {
            _stateChanged.OnNext(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布耳机控制状态失败");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = DisposeCoreAsync();
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await DetachAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "释放 SPP 控制服务失败");
        }
        finally
        {
            _commandLock.Dispose();
            _stateChanged.Dispose();
        }
    }
}
