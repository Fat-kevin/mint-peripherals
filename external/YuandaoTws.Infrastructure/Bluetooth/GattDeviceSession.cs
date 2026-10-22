using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Domain.Exceptions;
using YuandaoTws.Domain.Models;
using GattProps = YuandaoTws.Domain.Models.GattCharacteristicProperties;
using WinRtGattProps = Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties;

namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>
/// 基于 WinRT 的 GATT 设备会话。所有 Windows.* 类型仅出现在本类型内部，
/// 通过 <see cref="IGattDeviceSession"/> 对上层暴露领域模型。
/// </summary>
public sealed class GattDeviceSession : IGattDeviceSession
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _gattSession;
    private readonly ILogger<GattDeviceSession> _logger;
    private readonly Subject<GattNotification> _notifications = new();
    private readonly HashSet<Guid> _subscribed = new();
    private readonly Dictionary<Guid, GattCharacteristic> _characteristicCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private int _disposed;

    public HeadsetDevice Device { get; }

    public bool IsConnected => _gattSession.SessionStatus == GattSessionStatus.Active;

    public event Action? ConnectionLost;

    public GattDeviceSession(
        BluetoothLEDevice device,
        GattSession gattSession,
        HeadsetDevice deviceInfo,
        ILogger<GattDeviceSession> logger)
    {
        _device = device;
        _gattSession = gattSession;
        Device = deviceInfo;
        _logger = logger;
        _gattSession.SessionStatusChanged += OnSessionStatusChanged;
    }

    public IObservable<GattNotification> Notifications => _notifications;

    public async Task<IReadOnlyList<GattCharacteristicInfo>> DiscoverCharacteristicsAsync(
        Guid serviceUuid,
        CancellationToken cancellationToken)
    {
        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            throw new BluetoothConnectionException($"获取 GATT 服务列表失败：{servicesResult.Status}");
        }

        var service = servicesResult.Services.FirstOrDefault(s => s.Uuid == serviceUuid)
            ?? throw new BluetoothConnectionException($"设备 {Device.Name} 未暴露服务 {serviceUuid}");

        var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (characteristicsResult.Status != GattCommunicationStatus.Success)
        {
            throw new BluetoothConnectionException($"获取服务 {serviceUuid} 的特征列表失败：{characteristicsResult.Status}");
        }

        return characteristicsResult.Characteristics
            .Select(c => new GattCharacteristicInfo
            {
                Uuid = c.Uuid,
                UserDescription = c.UserDescription,
                Properties = MapProperties(c.CharacteristicProperties),
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<GattServiceInfo>> EnumerateServicesAsync(CancellationToken cancellationToken)
    {
        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            throw new BluetoothConnectionException($"获取 GATT 服务列表失败：{servicesResult.Status}");
        }

        var result = new List<GattServiceInfo>();
        foreach (var service in servicesResult.Services)
        {
            var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (characteristicsResult.Status != GattCommunicationStatus.Success)
            {
                _logger.LogWarning("获取服务 {Uuid} 的特征列表失败：{Status}，跳过该服务", service.Uuid, characteristicsResult.Status);
                continue;
            }

            result.Add(new GattServiceInfo
            {
                Uuid = service.Uuid,
                Characteristics = characteristicsResult.Characteristics
                    .Select(c => new GattCharacteristicInfo
                    {
                        Uuid = c.Uuid,
                        UserDescription = c.UserDescription,
                        Properties = MapProperties(c.CharacteristicProperties),
                    })
                    .ToArray(),
            });
        }

        return result;
    }

    public async Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken)
    {
        var characteristic = await GetCharacteristicAsync(characteristicUuid, cancellationToken);
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (result.Status != GattCommunicationStatus.Success)
        {
            _logger.LogWarning("读取特征 {Uuid} 失败：{Status}", characteristicUuid, result.Status);
            return null;
        }

        return BufferToArray(result.Value);
    }

    public async Task WriteAsync(Guid characteristicUuid, byte[] data, CancellationToken cancellationToken, bool withResponse = true)
    {
        var characteristic = await GetCharacteristicAsync(characteristicUuid, cancellationToken);
        using var writer = new DataWriter();
        writer.WriteBytes(data);
        var status = withResponse
            ? await characteristic.WriteValueAsync(writer.DetachBuffer()).AsTask(cancellationToken)
            : await characteristic
                .WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse)
                .AsTask(cancellationToken);
        if (status != GattCommunicationStatus.Success)
        {
            throw new ProtocolException($"写入特征 {characteristicUuid} 失败：{status}");
        }

        _logger.LogDebug("已写入特征 {Uuid}（{Mode}）：{Hex}",
            characteristicUuid, withResponse ? "带响应" : "无响应", Convert.ToHexString(data));
    }

    public async Task SubscribeAsync(Guid characteristicUuid, CancellationToken cancellationToken)
    {
        var characteristic = await GetCharacteristicAsync(characteristicUuid, cancellationToken);
        if (!_subscribed.Add(characteristicUuid))
        {
            return;
        }

        characteristic.ValueChanged += OnCharacteristicValueChanged;
        var status = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken);

        if (status != GattCommunicationStatus.Success)
        {
            _subscribed.Remove(characteristicUuid);
            characteristic.ValueChanged -= OnCharacteristicValueChanged;
            throw new ProtocolException($"订阅特征 {characteristicUuid} 通知失败：{status}");
        }

        _logger.LogInformation("已订阅特征 {Uuid} 的 Notify 通知", characteristicUuid);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var uuid in _subscribed.ToArray())
        {
            if (_characteristicCache.TryGetValue(uuid, out var characteristic))
            {
                characteristic.ValueChanged -= OnCharacteristicValueChanged;
            }
        }

        _subscribed.Clear();
        _gattSession.SessionStatusChanged -= OnSessionStatusChanged;
        _gattSession.MaintainConnection = false;
        _gattSession.Dispose();
        _device.Dispose();
        _cacheLock.Dispose();
        _notifications.OnCompleted();
        _logger.LogInformation("GATT 会话已释放：{DeviceName}", Device.Name);
        return ValueTask.CompletedTask;
    }

    private async Task<GattCharacteristic> GetCharacteristicAsync(Guid characteristicUuid, CancellationToken cancellationToken)
    {
        if (_characteristicCache.TryGetValue(characteristicUuid, out var cached))
        {
            return cached;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_characteristicCache.TryGetValue(characteristicUuid, out cached))
            {
                return cached;
            }

            var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                throw new BluetoothConnectionException($"获取 GATT 服务列表失败：{servicesResult.Status}");
            }

            foreach (var service in servicesResult.Services)
            {
                var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                if (characteristicsResult.Status != GattCommunicationStatus.Success)
                {
                    continue;
                }

                foreach (var characteristic in characteristicsResult.Characteristics)
                {
                    _characteristicCache[characteristic.Uuid] = characteristic;
                }
            }

            if (!_characteristicCache.TryGetValue(characteristicUuid, out var found))
            {
                throw new BluetoothConnectionException($"设备 {Device.Name} 未暴露特征 {characteristicUuid}");
            }

            return found;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        _logger.LogInformation("会话状态变化：{Status}（{Device}）", args.Status, Device.Name);
        if (args.Status != GattSessionStatus.Active)
        {
            _logger.LogWarning("连接丢失：{DeviceName}", Device.Name);
            ConnectionLost?.Invoke();
        }
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var value = BufferToArray(args.CharacteristicValue);
        if (value.Length == 0)
        {
            return;
        }

        _notifications.OnNext(new GattNotification { CharacteristicUuid = sender.Uuid, Value = value });
    }

    private static byte[] BufferToArray(IBuffer? buffer)
    {
        if (buffer is null)
        {
            return [];
        }

        using var reader = DataReader.FromBuffer(buffer);
        var result = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(result);
        return result;
    }

    private static GattProps MapProperties(WinRtGattProps properties)
    {
        var mapped = GattProps.None;
        if (properties.HasFlag(WinRtGattProps.Read))
        {
            mapped |= GattProps.Read;
        }

        if (properties.HasFlag(WinRtGattProps.Write) || properties.HasFlag(WinRtGattProps.WriteWithoutResponse))
        {
            mapped |= GattProps.Write;
        }

        if (properties.HasFlag(WinRtGattProps.Notify))
        {
            mapped |= GattProps.Notify;
        }

        if (properties.HasFlag(WinRtGattProps.Indicate))
        {
            mapped |= GattProps.Indicate;
        }

        return mapped;
    }
}
