using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Models;

namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>
/// 基于 WinRT StreamSocket（RFCOMM）的 SPP 流会话。读循环把收到的字节块推给
/// <see cref="DataReceived"/>；写用 DataWriter 串行化。所有 Windows.* 类型仅出现在本类型内部。
/// </summary>
public sealed class SppDeviceSession : ISppDeviceSession
{
    private readonly BluetoothDevice _bluetoothDevice;
    private readonly RfcommDeviceService _service;
    private readonly StreamSocket _socket;
    private readonly DataWriter _writer;
    private readonly ILogger<SppDeviceSession> _logger;
    // RFCOMM 服务可能在连接完成瞬间就推送首帧；保留极短窗口，避免上层订阅时错过状态帧。
    private readonly ReplaySubject<SppDataReceived> _data = new(bufferSize: 4);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readCts = new();
    private readonly Task _readLoop;
    private int _disposed;
    private volatile bool _streamClosed;

    public HeadsetDevice Device { get; }

    public bool IsConnected => !_streamClosed && !_readCts.IsCancellationRequested;

    /// <summary>流意外关闭（对端断开）时触发。</summary>
    public event Action? ConnectionLost;

    public SppDeviceSession(
        BluetoothDevice bluetoothDevice,
        RfcommDeviceService service,
        StreamSocket socket,
        HeadsetDevice device,
        ILogger<SppDeviceSession> logger)
    {
        _bluetoothDevice = bluetoothDevice;
        _service = service;
        _socket = socket;
        // ⚠️ 坑 14：DataWriter 必须会话级复用，不能每次新建再 Dispose——
        // WinRT DataWriter.Dispose() 会关闭底层输出流，第二次写必然抛 ObjectDisposedException。
        _writer = new DataWriter(socket.OutputStream);
        Device = device;
        _logger = logger;
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    public IObservable<SppDataReceived> DataReceived => _data;

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // 复用会话级 DataWriter（写锁保证 StoreAsync 串行）。
            _writer.WriteBytes(data);
            await _writer.StoreAsync().AsTask(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("SPP 已发送 {Length} 字节：{Hex}", data.Length, Convert.ToHexString(data));
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new DataReader(_socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial,
            };
            while (!cancellationToken.IsCancellationRequested)
            {
                // 不要用 .AsTask(cancellationToken) 包 LoadAsync：取消会摧毁 socket 并强制重连。
                // 主动关闭时由 DisposeAsync 先 Dispose socket 使本方法抛异常退出。
                var loaded = await reader.LoadAsync(4096);
                if (loaded == 0)
                {
                    _logger.LogWarning("SPP 流被对端关闭：{Device}", Device.Name);
                    break;
                }

                var buffer = new byte[loaded];
                reader.ReadBytes(buffer);
                try
                {
                    _data.OnNext(new SppDataReceived { Value = buffer });
                }
                catch (Exception ex)
                {
                    // 观察者异常不能被误判为蓝牙链路断开。
                    _logger.LogError(ex, "处理 SPP 数据通知失败：{Device}", Device.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭，属正常。
        }
        catch (ObjectDisposedException)
        {
            // 主动关闭时 socket 被释放，读循环随之退出，属正常。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SPP 读循环中断：{Device}", Device.Name);
        }
        finally
        {
            _streamClosed = true;
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("SPP 会话断开：{Device}", Device.Name);
                try
                {
                    ConnectionLost?.Invoke();
                }
                catch (Exception ex)
                {
                    // 上层重连回调不能让读循环变成未观察的故障任务。
                    _logger.LogDebug(ex, "处理 SPP 断线回调失败：{Device}", Device.Name);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 等待正在进行的写入完成，再释放 writer/socket，避免退出与 StoreAsync 并发。
        await _writeLock.WaitAsync();
        try
        {
            // 先取消标记，再释放 socket 解阻塞在读循环里的 LoadAsync，最后等循环退出。
            _readCts.Cancel();
            _writer.Dispose();
            _socket.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }

        try
        {
            await _readLoop;
        }
        catch
        {
            // 读循环已自行吞掉异常。
        }

        _readCts.Dispose();
        _writeLock.Dispose();
        _data.OnCompleted();
        (_service as IDisposable)?.Dispose();
        _bluetoothDevice.Dispose();
        _logger.LogInformation("SPP 会话已释放：{Device}", Device.Name);
    }
}
