using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace RemoteMicActivationProbe;

// Hardware characterisation tool, not part of the shipping app.
//
// It connects directly to the paired Xiaomi remote's ATVV service and records
// control notifications (0x08 = remote asks host to open mic, 0x04 = stream
// started) and audio notifications.  It then:
//   1. records a quiet baseline;
//   2. asks the operator to hold the voice key (positive control);
//   3. asks for each ordinary key, one at a time;
//   4. sends MIC_OPEN from the host without any key press.
//
// This separates three possible activation paths:
//   - remote firmware starts the mic only from the voice key;
//   - another physical key starts it;
//   - a host-side ATVV command starts it without a key.
//
// The app/sidecar must be closed: Windows exposes one GATT client at a time
// and the sidecar already owns the remote's ATVV connection.

internal static class Program
{
    private static readonly Guid AtvvServiceUuid = new("ab5e0001-5a21-4f05-bc7d-af01f617b664");
    private static readonly Guid TransmitUuid = new("ab5e0002-5a21-4f05-bc7d-af01f617b664");
    private static readonly Guid AudioUuid = new("ab5e0003-5a21-4f05-bc7d-af01f617b664");
    private static readonly Guid ControlUuid = new("ab5e0004-5a21-4f05-bc7d-af01f617b664");

    private static readonly ConcurrentQueue<CaptureRecord> Captures = new();
    private static readonly ConcurrentQueue<byte> PendingOpenRequests = new();
    private static readonly List<Phase> Phases = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = ParseOptions(args);
        if (options.Help)
        {
            PrintHelp();
            return 0;
        }

        var running = new[] { "DeviceCenterPreview.App", "sayall-engine" }
            .SelectMany(Process.GetProcessesByName)
            .ToArray();
        if (running.Length > 0)
        {
            Console.Error.WriteLine("请先退出外设中枢和 sayall-engine；BLE 同一时间只能有一个 GATT 客户端。");
            foreach (var process in running)
            {
                Console.Error.WriteLine($"  仍在运行: {process.ProcessName} (PID {process.Id})");
            }
            return 3;
        }

        var address = options.Mac is null ? TryReadPairedAddress() : ParseMac(options.Mac);
        if (address is null)
        {
            Console.Error.WriteLine("找不到已配对遥控器的地址。请用 --mac AA:BB:CC:DD:EE:FF 指定。");
            return 3;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        try
        {
            Console.WriteLine($"连接遥控器 {MaskMac(address.Value)} ...");
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address.Value);
            if (device is null)
            {
                Console.Error.WriteLine("连接失败：Windows 没有返回 BluetoothLEDevice。");
                return 3;
            }

            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success)
            {
                Console.Error.WriteLine($"枚举 GATT 服务失败：{services.Status}");
                return 3;
            }

            service = services.Services.FirstOrDefault(item => item.Uuid == AtvvServiceUuid);
            if (service is null)
            {
                Console.Error.WriteLine("没有找到 ATVV 服务 AB5E0001-...；这不是受支持的遥控器。");
                return 3;
            }

            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (characteristics.Status != GattCommunicationStatus.Success)
            {
                Console.Error.WriteLine($"枚举 ATVV 特征值失败：{characteristics.Status}");
                return 3;
            }

            var transmit = Find(characteristics.Characteristics, TransmitUuid, "transmit");
            var audio = Find(characteristics.Characteristics, AudioUuid, "audio");
            var control = Find(characteristics.Characteristics, ControlUuid, "control");
            if (transmit is null || audio is null || control is null)
            {
                return 3;
            }

            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> audioHandler =
                (_, eventArgs) => Capture("audio", eventArgs);
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> controlHandler =
                (_, eventArgs) => Capture("control", eventArgs);
            audio.ValueChanged += audioHandler;
            control.ValueChanged += controlHandler;

            using var responderCancellation = new CancellationTokenSource();
            Task? responder = null;
            try
            {
                var audioSubscribe = await Subscribe(audio);
                var controlSubscribe = await Subscribe(control);
                if (audioSubscribe != GattCommunicationStatus.Success ||
                    controlSubscribe != GattCommunicationStatus.Success)
                {
                    Console.Error.WriteLine(
                        $"订阅失败：audio={audioSubscribe}, control={controlSubscribe}");
                    return 3;
                }

                Console.WriteLine("已订阅 ATVV control/audio。");
                var caps = await RequestCapabilitiesAsync(transmit, cancellation.Token);
                if (caps is null)
                {
                    Console.Error.WriteLine("10 秒内没有收到 ATVV capabilities（0x0B）。");
                    return 3;
                }
                Console.WriteLine(
                    $"ATVV v{caps.Version >> 8}.{caps.Version & 0xFF} codec=0x{caps.Codec:X2}");

                responder = Task.Run(() => RespondToOpenRequestsAsync(
                    transmit,
                    caps,
                    responderCancellation.Token));

                await RunPhaseAsync(
                    "baseline",
                    "不要按任何键，保持遥控器静止",
                    options.BaselineSeconds,
                    cancellation.Token);

                if (!options.HostOpenOnly)
                {
                    await RunPhaseAsync(
                        "voice_key",
                        "请按住语音键（麦克风键）直到倒计时结束，然后松开",
                        options.VoiceSeconds,
                        cancellation.Token);

                    foreach (var key in options.Keys)
                    {
                        await RunPhaseAsync(
                            $"key:{key}",
                            $"请按一次「{key}」；本阶段不要按语音键",
                            options.KeySeconds,
                            cancellation.Token);
                    }
                }

                var hostPhase = new Phase("host_mic_open", "主机直接发 MIC_OPEN，不按遥控器");
                hostPhase.Start = DateTimeOffset.UtcNow;
                Phases.Add(hostPhase);
                var open = caps.Version >= 0x0100
                    ? new byte[] { 0x0C, 0x00 }
                    : new byte[] { 0x0C, 0x00, caps.Codec };
                var openStatus = await WriteAsync(transmit, open);
                Console.WriteLine($"主机 MIC_OPEN 写入结果：{openStatus} ({Convert.ToHexString(open)})");
                var extendStatuses = new List<string>();
                for (var elapsed = 0; elapsed < options.HostOpenSeconds; elapsed++)
                {
                    await Task.Delay(1000, cancellation.Token);
                    var currentStart = Captures
                        .Where(item => item.Channel == "control" &&
                                       item.At >= hostPhase.Start &&
                                       item.Opcode == 0x04)
                        .OrderByDescending(item => item.At)
                        .FirstOrDefault();
                    if (currentStart?.Bytes is not { Length: >= 4 } || caps.Version < 0x0100)
                    {
                        continue;
                    }
                    // The remote only grants a ~5-6 second free audio window;
                    // keep the host-open session alive exactly as the sidecar does.
                    var extend = new byte[] { 0x0E, currentStart.Bytes[3] };
                    var extendStatus = await WriteAsync(transmit, extend);
                    extendStatuses.Add($"{Convert.ToHexString(extend)}:{extendStatus}");
                }
                hostPhase.End = DateTimeOffset.UtcNow;

                // Best-effort close; if the remote unexpectedly streamed, this
                // stops it, and if it did not, the command is a no-op.
                var streamStart = Captures
                    .Where(item => item.Channel == "control" &&
                                   item.At >= hostPhase.Start && item.At <= hostPhase.End &&
                                   item.Opcode == 0x04)
                    .OrderBy(item => item.At)
                    .FirstOrDefault();
                var sessionId = streamStart?.Bytes is { Length: >= 4 } bytes ? bytes[3] : (byte)0;
                var close = caps.Version >= 0x0100
                    ? new byte[] { 0x0D, sessionId }
                    : new byte[] { 0x0D };
                var closeStatus = await WriteAsync(transmit, close);
                Console.WriteLine($"主机 MIC_CLOSE 写入结果：{closeStatus} ({Convert.ToHexString(close)})");

                await Task.Delay(500, cancellation.Token);
                var report = BuildReport(address.Value, caps, openStatus, closeStatus, extendStatuses);
                var output = options.Output ?? DefaultOutputPath();
                var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }
                await File.WriteAllTextAsync(
                    output,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
                PrintSummary(report, output);
                var reportJson = JsonSerializer.SerializeToElement(report);
                var verdict = reportJson.GetProperty("verdict").GetString() ?? string.Empty;
                var audioConclusion =
                    verdict == "voice_key_only" ||
                    verdict.Contains("transmit", StringComparison.Ordinal);
                return audioConclusion ? 0 : 2;
            }
            finally
            {
                responderCancellation.Cancel();
                if (responder is not null)
                {
                    try
                    {
                        await responder;
                    }
                    catch (Exception)
                    {
                        // The remote may be gone; cleanup must not hide the
                        // test result already written to disk.
                    }
                }
                control.ValueChanged -= controlHandler;
                audio.ValueChanged -= audioHandler;
                await Unsubscribe(control);
                await Unsubscribe(audio);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("测试超时或已取消。");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"测试失败：{exception.GetType().Name}: {exception.Message}");
            return 3;
        }
        finally
        {
            (service as IDisposable)?.Dispose();
            (device as IDisposable)?.Dispose();
        }
    }

    private static void Capture(string channel, GattValueChangedEventArgs eventArgs)
    {
        var value = eventArgs.CharacteristicValue;
        using var reader = DataReader.FromBuffer(value);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        var opcode = channel == "control" && bytes.Length > 0 ? bytes[0] : (byte?)null;
        if (opcode == 0x08)
        {
            // The real sidecar answers 0x08 with MIC_OPEN.  Without this the
            // voice-key positive control would never reach 0x04/audio.
            PendingOpenRequests.Enqueue(0);
        }
        // Control frames are short protocol messages.  Audio is never stored:
        // only length/time metadata is needed to prove a stream exists.
        var preview = channel == "control"
            ? Convert.ToHexString(bytes)
            : Convert.ToHexString(bytes.AsSpan(0, Math.Min(4, bytes.Length)));
        Captures.Enqueue(new CaptureRecord(DateTimeOffset.UtcNow, channel, bytes.Length, preview, opcode, bytes));
    }

    private static async Task<Phase> RunPhaseAsync(
        string name,
        string instruction,
        int seconds,
        CancellationToken cancellationToken)
    {
        var phase = new Phase(name, instruction)
        {
            Start = DateTimeOffset.UtcNow,
        };
        Phases.Add(phase);
        Console.WriteLine();
        Console.WriteLine($"[{name}] {instruction}");
        await CountdownAsync(seconds, null, cancellationToken);
        phase.End = DateTimeOffset.UtcNow;
        return phase;
    }

    private static async Task CountdownAsync(
        int seconds,
        string? note,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            Console.WriteLine(note);
        }
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            Console.Write($"\r  {remaining,2}s ...");
            await Task.Delay(1000, cancellationToken);
        }
        Console.WriteLine("\r  完成    ");
    }

    private static async Task RespondToOpenRequestsAsync(
        GattCharacteristic transmit,
        AtvvCapabilities caps,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (PendingOpenRequests.TryDequeue(out _))
            {
                var open = caps.Version >= 0x0100
                    ? new byte[] { 0x0C, 0x00 }
                    : new byte[] { 0x0C, 0x00, caps.Codec };
                var status = await WriteAsync(transmit, open);
                Console.WriteLine($"自动响应 0x08 → MIC_OPEN：{status} ({Convert.ToHexString(open)})");
            }
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<AtvvCapabilities?> RequestCapabilitiesAsync(
        GattCharacteristic transmit,
        CancellationToken cancellationToken)
    {
        var status = await WriteAsync(transmit, new byte[] { 0x0A, 0x01, 0x00, 0x00, 0x03, 0x03 });
        Console.WriteLine($"GET_CAPABILITIES 写入结果：{status}");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var caps = Captures
                .Where(item => item.Channel == "control" && item.Opcode == 0x0B)
                .Select(item => ParseCapabilities(item.Bytes))
                .FirstOrDefault(item => item is not null);
            if (caps is not null)
            {
                return caps;
            }
            await Task.Delay(100, cancellationToken);
        }
        return null;
    }

    private static AtvvCapabilities? ParseCapabilities(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 7 || bytes[0] != 0x0B)
        {
            return null;
        }
        var version = (ushort)((bytes[1] << 8) | bytes[2]);
        var codec = version >= 0x0100 ? ((bytes[3] & 0x02) != 0 ? (byte)0x02 : (byte)0x01) : bytes[4];
        return new AtvvCapabilities(version, codec);
    }

    private static GattCharacteristic? Find(
        IEnumerable<GattCharacteristic> characteristics,
        Guid uuid,
        string label)
    {
        var found = characteristics.FirstOrDefault(item => item.Uuid == uuid);
        if (found is null)
        {
            Console.Error.WriteLine($"缺少 ATVV {label} 特征值 {uuid}。");
        }
        return found;
    }

    private static async Task<GattCommunicationStatus> Subscribe(GattCharacteristic characteristic)
    {
        var properties = characteristic.CharacteristicProperties;
        var value = (properties & GattCharacteristicProperties.Notify) != 0
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        return await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(value);
    }

    private static async Task Unsubscribe(GattCharacteristic characteristic)
    {
        try
        {
            await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
        }
        catch
        {
            // The device may already be gone; cleanup is best effort.
        }
    }

    private static async Task<GattCommunicationStatus> WriteAsync(
        GattCharacteristic characteristic,
        byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        var buffer = writer.DetachBuffer();
        return (characteristic.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
            ? await characteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse)
            : await characteristic.WriteValueAsync(buffer);
    }

    private static object BuildReport(
        ulong address,
        AtvvCapabilities caps,
        GattCommunicationStatus openStatus,
        GattCommunicationStatus closeStatus,
        IReadOnlyList<string> extendStatuses)
    {
        var all = Captures.OrderBy(item => item.At).ToArray();
        var phaseReports = Phases.Select(phase =>
        {
            var events = all.Where(item => item.At >= phase.Start && item.At <= phase.End).ToArray();
            var control = events.Where(item => item.Channel == "control").ToArray();
            var audio = events.Where(item => item.Channel == "audio").ToArray();
            return new
            {
                phase = phase.Name,
                instruction = phase.Instruction,
                startedAt = phase.Start,
                endedAt = phase.End,
                durationMs = (int)(phase.End - phase.Start).TotalMilliseconds,
                microphoneOpenRequests = control.Count(item => item.Opcode == 0x08),
                streamStarts = control.Count(item => item.Opcode == 0x04),
                streamStops = control.Count(item => item.Opcode == 0x00),
                audioPackets = audio.Length,
                audioBytes = audio.Sum(item => item.Length),
                control = control.Select(item => new { at = item.At, hex = item.Hex }).ToArray(),
                audioFirstAt = audio.FirstOrDefault()?.At,
                audioLastAt = audio.LastOrDefault()?.At,
            };
        }).ToArray();

        static bool Requested(int microphoneOpenRequests, int streamStarts) =>
            microphoneOpenRequests > 0 || streamStarts > 0;

        var voice = phaseReports.FirstOrDefault(item => item.phase == "voice_key");
        var otherKeys = phaseReports.Where(item => item.phase.StartsWith("key:", StringComparison.Ordinal)).ToArray();
        var host = phaseReports.FirstOrDefault(item => item.phase == "host_mic_open");
        var voiceRequested = voice is not null &&
                             Requested(voice.microphoneOpenRequests, voice.streamStarts);
        var voiceAudio = voice is not null && voice.audioPackets > 0;
        var otherRequested = otherKeys.Any(item => Requested(item.microphoneOpenRequests, item.streamStarts));
        var otherStream = otherKeys.Any(item => item.streamStarts > 0);
        var otherAudio = otherKeys.Any(item => item.audioPackets > 0);
        var hostRequested = host is not null && Requested(host.microphoneOpenRequests, host.streamStarts);
        var hostStream = host is not null && host.streamStarts > 0;
        var hostAudio = host is not null && host.audioPackets > 0;
        var nonAudioActivation = otherRequested || hostRequested;
        var verdict = (voiceAudio, otherAudio, hostAudio) switch
        {
            // Audio is the real "microphone is transmitting" signal.  A bare
            // 0x04 only means the remote accepted a stream request; it must not
            // hide the voice-key positive control.
            (true, false, false) when nonAudioActivation =>
                "voice_key_transmits_audio_only_but_stream_can_start_without_audio",
            (true, false, false) => "voice_key_only",
            (true, true, true) => "voice_key_and_other_paths_transmit_audio",
            (true, true, false) => "voice_key_and_other_button_transmit_audio",
            (true, false, true) => "voice_key_and_host_protocol_transmit_audio",
            (false, true, true) => "other_button_and_host_protocol_transmit_audio",
            (false, true, false) => "other_button_transmits_microphone_audio",
            (false, false, true) => "host_protocol_transmits_microphone_audio",
            _ when otherStream && hostStream => "other_button_and_host_protocol_start_stream_without_audio",
            _ when otherStream => "other_button_starts_stream_without_audio",
            _ when hostStream => "host_protocol_starts_stream_without_audio",
            _ when otherRequested && hostRequested => "other_button_and_host_protocol_request_microphone_without_audio",
            _ when otherRequested => "other_button_requests_microphone_without_audio",
            _ when hostRequested => "host_protocol_requests_microphone_without_audio",
            _ when voiceRequested => "voice_key_requests_stream_but_no_audio",
            _ => "inconclusive_missing_voice_positive_control",
        };
        var activeOtherKeys = otherKeys
            .Where(item => Requested(item.microphoneOpenRequests, item.streamStarts) || item.audioPackets > 0)
            .Select(item => item.phase)
            .ToArray();
        var evidenceQuality = voiceAudio
            ? "voice_audio_positive_control_present"
            : voiceRequested
                ? "voice_stream_positive_control_present_but_no_audio"
                : "voice_positive_control_missing";
        return new
        {
            schema = 1,
            tool = "RemoteMicActivationProbe",
            createdAt = DateTimeOffset.UtcNow,
            device = MaskMac(address),
            capabilities = new { version = caps.Version, codec = caps.Codec },
            hostMicOpenWrite = openStatus.ToString(),
            hostMicCloseWrite = closeStatus.ToString(),
            hostMicExtendWrites = extendStatuses,
            verdict,
            evidenceQuality,
            interpretation = Describe(verdict),
            otherActivatedKeys = activeOtherKeys,
            phases = phaseReports,
            allControl = all.Where(item => item.Channel == "control")
                .Select(item => new { at = item.At, hex = item.Hex })
                .ToArray(),
        };
    }

    private static string Describe(string verdict) => verdict switch
    {
        "voice_key_only" => "阳性对照成立：只有语音键阶段出现音频；其他按键和主机 MIC_OPEN 都没有音频。",
        "voice_key_transmits_audio_only_but_stream_can_start_without_audio" => "只有语音键阶段出现真实音频；其他按键/主机协议最多只能触发 0x08/0x04，却没有音频。语音键仍是唯一实际传输入口。",
        "voice_key_and_other_paths_transmit_audio" => "语音键、其他按键和主机协议都能产生真实音频；语音键不是唯一入口。",
        "voice_key_and_other_button_transmit_audio" => "语音键和至少一个非语音键都有真实音频；遥控器麦克风不是语音键独占。",
        "voice_key_and_host_protocol_transmit_audio" => "语音键和主机 MIC_OPEN 都能产生真实音频；协议层可绕过语音键。",
        "other_button_transmits_microphone_audio" => "至少一个非语音键阶段出现了真实音频包；遥控器麦克风不是语音键独占。",
        "host_protocol_transmits_microphone_audio" => "未按键时主机主动发 MIC_OPEN 后出现了真实音频包；协议层可绕过语音键启动麦克风传输。",
        "other_button_and_host_protocol_transmit_audio" => "其他按键与主机协议都能产生真实音频；语音键不是唯一入口。",
        "other_button_starts_stream_without_audio" => "某个非语音键让遥控器回了 0x04，但观察窗内没有音频包；只能证明它请求了流，不能证明麦克风在传输。",
        "host_protocol_starts_stream_without_audio" => "主机主动 MIC_OPEN 让遥控器回了 0x04，但没有音频包；只能证明协议能请求流，不能证明麦克风已传输。",
        "other_button_and_host_protocol_start_stream_without_audio" => "其他按键和主机协议都能让遥控器回 0x04，但都没有音频包；不能证明实际麦克风传输。",
        "other_button_requests_microphone_without_audio" => "某个非语音键触发了 0x08（请求开麦），但没有 0x04/音频；需要继续确认固件行为。",
        "host_protocol_requests_microphone_without_audio" => "主机协议触发了开麦请求，但没有 0x04/音频；不能证明实际传输。",
        "other_button_and_host_protocol_request_microphone_without_audio" => "其他按键与主机协议都触发了开麦请求，但没有 0x04/音频。",
        "voice_key_requests_stream_but_no_audio" => "语音键阶段只看到 0x08/0x04，没有音频包；阳性对照不完整，不能下结论。",
        _ => "没有观察到语音键的音频阳性对照，无法下结论；请确认设备已连接、语音键按法正确后重试。",
    };

    private static void PrintSummary(object report, string output)
    {
        var json = JsonSerializer.SerializeToElement(report);
        Console.WriteLine();
        Console.WriteLine("=== 结果 ===");
        Console.WriteLine($"结论: {json.GetProperty("verdict").GetString()}");
        Console.WriteLine($"解释: {json.GetProperty("interpretation").GetString()}");
        Console.WriteLine($"证据质量: {json.GetProperty("evidenceQuality").GetString()}");
        foreach (var phase in json.GetProperty("phases").EnumerateArray())
        {
            Console.WriteLine(
                $"  {phase.GetProperty("phase").GetString(),-18} 0x08={phase.GetProperty("microphoneOpenRequests")} " +
                $"0x04={phase.GetProperty("streamStarts")} audio={phase.GetProperty("audioPackets")}包/" +
                $"{phase.GetProperty("audioBytes")}B");
        }
        Console.WriteLine($"报告: {output}");
    }

    private static ulong? TryReadPairedAddress()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "app.getsayall.remote-mic.windows",
            "settings.json");
        if (!File.Exists(path))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("selected_remote_id", out var id) ||
            id.GetString() is not { } value)
        {
            return null;
        }
        var tail = value.Split('-').LastOrDefault() ?? string.Empty;
        var hex = new string(tail.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
            ? address
            : null;
    }

    private static ulong? ParseMac(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
            ? address
            : null;
    }

    private static string FormatMac(ulong address)
    {
        var hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }

    private static string MaskMac(ulong address)
    {
        var parts = FormatMac(address).Split(':');
        return $"**:**:**:**:{parts[4]}:{parts[5]}";
    }

    private static string DefaultOutputPath() => Path.Combine(
        Environment.CurrentDirectory,
        ".artifacts",
        $"remote-mic-activation-{DateTime.Now:yyyyMMdd-HHmmss}.json");

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    options.Help = true;
                    break;
                case "--mac" when index + 1 < args.Length:
                    options.Mac = args[++index];
                    break;
                case "--keys" when index + 1 < args.Length:
                    options.Keys = args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--out" when index + 1 < args.Length:
                    options.Output = args[++index];
                    break;
                case "--baseline-seconds" when index + 1 < args.Length:
                    options.BaselineSeconds = ParseSeconds(args[++index], 6);
                    break;
                case "--voice-seconds" when index + 1 < args.Length:
                    options.VoiceSeconds = ParseSeconds(args[++index], 6);
                    break;
                case "--key-seconds" when index + 1 < args.Length:
                    options.KeySeconds = ParseSeconds(args[++index], 4);
                    break;
                case "--host-open-seconds" when index + 1 < args.Length:
                    options.HostOpenSeconds = ParseSeconds(args[++index], 10);
                    break;
                case "--host-open-only":
                    options.HostOpenOnly = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[index]}");
            }
        }
        return options;
    }

    private static int ParseSeconds(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 60
            ? seconds
            : fallback;

    private static void PrintHelp()
    {
        Console.WriteLine("""
            RemoteMicActivationProbe — 测试遥控器麦克风是否只能由语音键启动

            用法:
               powershell -ExecutionPolicy Bypass -File tools/RemoteMicActivationProbe/run.ps1 [options]

            也可以先构建，再直接运行:
              dotnet build tools/RemoteMicActivationProbe/RemoteMicActivationProbe.csproj -c Debug -p:Platform=x64
              tools/RemoteMicActivationProbe/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/RemoteMicActivationProbe.exe [options]

            选项:
              --mac <addr>             指定遥控器蓝牙地址（默认从原版 settings.json 读取）
              --keys <a,b,c>           要测试的其他按键（默认 上,下,左,右,确定,返回,主页,菜单,音量+,音量-,静音）
              --baseline-seconds <n>   静默基线秒数（默认 6）
              --voice-seconds <n>      语音键阳性对照秒数（默认 6）
              --key-seconds <n>        每个普通按键观察秒数（默认 4）
              --host-open-seconds <n>  主机 MIC_OPEN 观察秒数（默认 10）
              --host-open-only         只测静默基线和主机 MIC_OPEN，不要求按键
              --out <path>             JSON 报告输出路径
              -h, --help               显示帮助

            运行前必须退出外设中枢和 sayall-engine。
            """);
    }

    private sealed class Options
    {
        public bool Help { get; set; }
        public string? Mac { get; set; }
        public string[] Keys { get; set; } =
            ["上", "下", "左", "右", "确定", "返回", "主页", "菜单", "音量+", "音量-", "静音"];
        public string? Output { get; set; }
        public int BaselineSeconds { get; set; } = 6;
        public int VoiceSeconds { get; set; } = 6;
        public int KeySeconds { get; set; } = 4;
        public int HostOpenSeconds { get; set; } = 10;
        public bool HostOpenOnly { get; set; }
    }

    private sealed class Phase
    {
        public Phase(string name, string instruction)
        {
            Name = name;
            Instruction = instruction;
        }

        public string Name { get; }
        public string Instruction { get; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
    }

    private sealed record CaptureRecord(
        DateTimeOffset At,
        string Channel,
        int Length,
        string Hex,
        byte? Opcode,
        byte[] Bytes);

    private sealed record AtvvCapabilities(ushort Version, byte Codec);
}
