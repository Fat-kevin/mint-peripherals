using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using YuandaoTws.Domain;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Application.Services;

/// <summary>
/// 监听原道独立状态服务的 03 头电量帧。
/// 主控 4E 通道负责控制和百分比；当前实测的 03 帧没有可确认的充电字段，
/// 因此本服务只负责接收状态帧，不把任意字节解释成“充电中”。
/// </summary>
public sealed class YuandaoChargingMonitorService : IDisposable
{
    private readonly HeadsetConnectionService _connection;
    private readonly ILogger<YuandaoChargingMonitorService> _logger;
    private readonly Subject<BatteryInfo> _batteryChanged = new();
    private IDisposable? _dataSubscription;
    private IDisposable? _controlDataSubscription;
    private CancellationTokenSource? _pollCts;
    private readonly object _publishGate = new();
    private BatteryInfo? _lastPublishedBattery;
    private int _disposed;

    public YuandaoChargingMonitorService(
        HeadsetConnectionService connection,
        ILogger<YuandaoChargingMonitorService> logger)
    {
        _connection = connection;
        _logger = logger;
        _connection.StatusSessionChanged += OnStatusSessionChanged;
        _connection.ControlSessionChanged += OnControlSessionChanged;
    }

    public IObservable<BatteryInfo> BatteryChanged => _batteryChanged;

    private void OnStatusSessionChanged(ISppDeviceSession? session)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_publishGate)
        {
            _lastPublishedBattery = null;
        }

        _pollCts?.Cancel();
        _pollCts = null;
        _dataSubscription?.Dispose();
        _dataSubscription = null;

        if (session is null)
        {
            // 清掉上一条状态，避免辅助会话重开期间继续显示旧的“充电中”。
            Publish(BatteryInfo.FromLeftRight(null, null, null));
            return;
        }

        _dataSubscription = SubscribeYuandaoFrames(session);

        // df21 服务通常会主动推送，但部分固件只在打开后收到查询才会再发一次。
        // 查询失败不影响主控通道；轮询仅用于取得状态快照，不代表能够得到充电状态。
        var pollCts = new CancellationTokenSource();
        _pollCts = pollCts;
        _ = PollStatusAsync(session, pollCts);
    }

    private void OnControlSessionChanged(ISppDeviceSession? session, long generation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_publishGate)
        {
            _lastPublishedBattery = null;
        }

        _controlDataSubscription?.Dispose();
        _controlDataSubscription = session is null ? null : SubscribeYuandaoFrames(session);
    }

    private IDisposable SubscribeYuandaoFrames(ISppDeviceSession session)
    {
        var parser = new YuandaoFrameParser();
        return session.DataReceived.Subscribe(chunk =>
        {
            try
            {
                foreach (var message in parser.Feed(chunk.Value))
                {
                    var battery = YuandaoFrameSemantics.TryParseBattery(message);
                    if (battery is not null)
                    {
                        Publish(battery);
                    }
                }
            }
            catch (Exception ex)
            {
                // 辅助状态帧异常不能反向终止 SPP 读循环。
                _logger.LogDebug(ex, "解析原道辅助状态帧失败");
            }
        });
    }

    private async Task PollStatusAsync(ISppDeviceSession session, CancellationTokenSource pollCts)
    {
        try
        {
            while (!pollCts.IsCancellationRequested)
            {
                try
                {
                    await session.WriteAsync(YuandaoCommands.Query(0x03), pollCts.Token);
                    await Task.Delay(TimeSpan.FromSeconds(10), pollCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 辅助服务不稳定时静默退出，不能影响主控连接和界面。
                    _logger.LogDebug(ex, "原道辅助状态轮询结束");
                    return;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_pollCts, pollCts))
            {
                _pollCts = null;
            }

            pollCts.Dispose();
        }
    }

    private void Publish(BatteryInfo battery)
    {
        lock (_publishGate)
        {
            if (Equals(_lastPublishedBattery, battery))
            {
                return;
            }

            _lastPublishedBattery = battery;
        }

        try
        {
            _batteryChanged.OnNext(battery);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布原道辅助电量状态失败");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connection.StatusSessionChanged -= OnStatusSessionChanged;
        _connection.ControlSessionChanged -= OnControlSessionChanged;
        _pollCts?.Cancel();
        _pollCts = null;
        _dataSubscription?.Dispose();
        _dataSubscription = null;
        _controlDataSubscription?.Dispose();
        _controlDataSubscription = null;
        _batteryChanged.Dispose();
    }
}
