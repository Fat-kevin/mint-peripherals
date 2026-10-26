using System.Text;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Plugins.Logitech;
using VigilCharging = VigilWatt.Core.Models.ChargingState;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 功 能： 罗技 HID++ 鼠标：复用 VigilWatt 的 NativeHid/HidppIO，读名称/电量/DPI/轮询率并按用户动作写入。
/// 2026/10/19 15:40:00 xliu
/// 只对原项目支持的罗技接收器工作；不猜测其他厂商协议。
/// </summary>
internal sealed class LogitechMouseService : IDisposable
{
    private const ushort LogitechVendorId = 0x046D;
    private const ushort HidppUsagePage = 0xFF00;
    private static readonly byte[] DeviceIndexes = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0xFF];
    private static readonly int[] KnownRates = [125, 250, 500, 1000, 2000, 4000, 8000];

    private readonly object _gate = new();
    private readonly Timer _timer;
    private HidppIO? _io;
    private byte _deviceIndex;
    private byte _featureName;
    private byte _featureUnifiedBattery;
    private byte _featureBatteryStatus;
    private byte _featureDpiExtended;
    private byte _featureDpiLegacy;
    private byte _featureRateExtended;
    private byte _featureRateLegacy;
    private byte _featureOnboardProfiles;
    private byte _featureFirmware;
    private bool _requestsAllowed;
    private bool _enabled = true;
    private bool _writeSupported;
    private string? _writeUnsupportedReason = "尚未读取到鼠标型号";
    private bool _disposed;
    private MouseState _state = new("", "未发现鼠标", false, null, ChargingState.Unknown, "未发现鼠标", false);
    private MouseControls _controls = new("", null, null, []);
    private List<int> _dpiOptions = [];
    private List<int> _rates = [];
    private byte[]? _profile;
    private int _profileSector;
    private int _profileSize;
    private int _profileButtonCount;
    private int _profileFormat;
    private bool _profileOnboardMode;
    private string? _profileName;
    private bool _profileValid;
    private bool _profileWritable;

    private static readonly (string Id, string Label)[] MouseActionOptions =
    [
        ("disabled", "禁用"),
        ("left", "左键"),
        ("right", "右键"),
        ("middle", "中键"),
        ("back", "后退"),
        ("forward", "前进"),
        ("enter", "Enter"),
        ("escape", "Esc"),
        ("space", "空格"),
        ("copy", "Ctrl+C"),
        ("paste", "Ctrl+V"),
        ("volume_up", "音量+"),
        ("volume_down", "音量-"),
        ("mute", "静音"),
        ("play_pause", "播放/暂停"),
        ("media_next", "下一曲"),
        ("media_prev", "上一曲"),
        ("dpi", "DPI 切换"),
    ];

    public event EventHandler? Changed;

    public LogitechMouseService()
    {
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    /// <summary>模块开关：关闭时停掉 HID 会话与轮询；重新开启立即刷新一次。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (!enabled)
        {
            lock (_gate)
            {
                CloseSession();
                _writeSupported = false;
                _writeUnsupportedReason = "鼠标模块已在设置中关闭";
                _state = new MouseState("", "鼠标模块已关闭", false, null, ChargingState.Unknown,
                    "鼠标模块已在设置中关闭", false);
                _controls = new MouseControls("", null, null, []);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Refresh();
    }

    public MouseState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public MouseControls Controls
    {
        get
        {
            lock (_gate)
            {
                return _controls;
            }
        }
    }

    public void Refresh()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                if (!TryOpen())
                {
                    _writeSupported = false;
                    _writeUnsupportedReason = "未发现支持 HID++ 的罗技鼠标";
                    _state = new MouseState("", "未发现鼠标", false, null, ChargingState.Unknown,
                        "未发现支持 HID++ 的罗技鼠标", false);
                    return;
                }

                _requestsAllowed = true;
                var name = ReadName();
                var firmware = ReadFirmware();
                var (battery, charging) = ReadBattery();
                var dpi = ReadDpi();
                _dpiOptions = ReadDpiOptions();
                _rates = ReadPollingOptions();
                var rate = ReadPollingRate();
                ReadOnboardProfile();
                // 板载模式下固件执行的是 profile 里的值（档位 DPI + 轮询间隔），
                // 主机寄存器读数是旧值；显示/滑条必须跟随固件实际生效的配置。
                var displayDpi = dpi;
                var displayRate = rate;
                if (_profileValid && _profileOnboardMode && _profile is not null)
                {
                    var activeStage = Math.Clamp((int)_profile[1], 0, 4);
                    var stageDpi = _profile[3 + activeStage * 2] | (_profile[4 + activeStage * 2] << 8);
                    if (stageDpi > 0)
                    {
                        displayDpi = stageDpi;
                    }

                    var interval = _profile[0];
                    if (interval is >= 1 and <= 8)
                    {
                        displayRate = 1000 / interval;
                    }
                }

                var available = _featureDpiExtended != 0 || _featureDpiLegacy != 0 ||
                                _featureRateExtended != 0 || _featureRateLegacy != 0 ||
                                _profileValid;

                // 接收器自己的槽位也可能回应 name/feature，但没有鼠标级数据时不能当作在线鼠标。
                if (battery is null && string.IsNullOrWhiteSpace(name))
                {
                    CloseSession();
                    _writeSupported = false;
                    _writeUnsupportedReason = "鼠标休眠或未连接";
                    _state = new MouseState("", "未发现鼠标", false, null, ChargingState.Unknown,
                        "鼠标休眠或未连接", false);
                }
                else
                {
                    var stages = ReadStages();
                    var buttons = ReadButtons();
                    var displayName = string.IsNullOrWhiteSpace(name) ? "未知" : name;
                    var verifiedModel = name?.Contains("G304", StringComparison.OrdinalIgnoreCase) == true ||
                                        name?.Contains("G305", StringComparison.OrdinalIgnoreCase) == true;
                    _writeSupported = verifiedModel && _profileValid && _profileWritable;
                    _writeUnsupportedReason = _writeSupported
                        ? null
                        : !verifiedModel
                            ? $"当前型号“{displayName}”未验证写入，保持只读。"
                            : !_profileValid
                                ? "未读取到可校验的板载配置，保持只读。"
                                : "板载配置校验失败，保持只读。";
                    _controls = new MouseControls(
                        $"logi:{_deviceIndex}",
                        displayDpi,
                        displayRate,
                        buttons,
                        _dpiOptions.Count > 0 ? _dpiOptions.Min() : null,
                        _dpiOptions.Count > 0 ? _dpiOptions.Max() : null,
                        StepOf(_dpiOptions),
                        _rates,
                        !available,
                        stages,
                        _profileValid ? _profile![1] : null,
                        _profileValid,
                        _profileOnboardMode,
                        _profileName,
                        _profileValid ? _profileFormat : null,
                        _profileValid ? _profileSize : null,
                        _profileWritable,
                        _writeSupported,
                        _writeUnsupportedReason);
                    _state = new MouseState(
                        $"logi:{_deviceIndex}",
                        string.IsNullOrWhiteSpace(name) ? "罗技鼠标" : name,
                        true,
                        battery,
                        charging,
                        $"已连接 · HID++ · 槽位 {_deviceIndex}" +
                        (_profileValid ? $" · 板载{( _profileOnboardMode ? "开" : "关")}" : ""),
                        available,
                        firmware);
                }

                AppLog.Info($"mouse state connected={_state.Connected} name='{_state.Name}' " +
                            $"battery={battery?.ToString() ?? "-"} dpi={dpi?.ToString() ?? "-"} " +
                            $"rate={rate?.ToString() ?? "-"} available={available}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"mouse refresh failed: {ex.Message}");
                CloseSession();
                _writeSupported = false;
                _writeUnsupportedReason = "鼠标连接失败，稍后重试";
                _state = new MouseState("", "未发现鼠标", false, null, ChargingState.Unknown,
                    "鼠标连接失败，稍后重试", false);
            }
            finally
            {
                _requestsAllowed = false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<WriteResult> ApplyAsync(MouseControls controls) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return WriteResult.Fail("鼠标模块已在设置中关闭。");
            }

            if (_io is null || _deviceIndex == 0)
            {
                return WriteResult.Fail("鼠标未连接，无法写入。");
            }

            if (!_writeSupported)
            {
                return WriteResult.Fail(_writeUnsupportedReason ?? "当前型号未验证写入，保持只读。");
            }

            try
            {
                _requestsAllowed = true;
                // 模式切换先行：DPI/轮询是否直写主机寄存器取决于目标模式。
                // 旧顺序（先写 profile 后切模式）用切换前的模式判断，导致
                // 「关板载 + 调 DPI」一次点击时 DPI 直写被跳过，需要点两次。
                if (controls.OnboardMode != _profileOnboardMode)
                {
                    var modeResult = SetOnboardMode(controls.OnboardMode);
                    if (!modeResult.Ok)
                    {
                        return modeResult;
                    }
                }

                var profileWrite = controls.DpiStages is not null ||
                                   controls.Buttons.Any(button => int.TryParse(button.Id, out _));
                if (profileWrite)
                {
                    var result = WriteProfileControls(controls);
                    if (!result.Ok)
                    {
                        return result;
                    }
                }

                // DPI 直写：主机模式下就是最终值；板载模式下 profile 只决定「下次加载」的
                // 默认档位，必须再发一次 setSensorDpi，鼠标才会立刻切到用户选的档位。
                if (controls.Dpi is int dpi)
                {
                    if (_dpiOptions.Count > 0 && !_dpiOptions.Contains(dpi))
                    {
                        return WriteResult.Fail($"设备不支持{dpi} DPI，请选择列表中的值。");
                    }

                    var result = WriteDpi(dpi);
                    if (!result.Ok)
                    {
                        if (!controls.OnboardMode)
                        {
                            return result;
                        }

                        // 固件拒绝直写：切一次板载模式，让固件重新加载刚写入的 profile（默认档位）。
                        AppLog.Warn("mouse onboard dpi write rejected: " + result.Message + " -> reload profile");
                        var offResult = SetOnboardMode(false);
                        var onResult = offResult.Ok ? SetOnboardMode(true) : offResult;
                        if (!onResult.Ok)
                        {
                            return WriteResult.Fail("板载配置已写入，但设备拒绝切换 DPI：" + result.Message);
                        }
                    }
                    else
                    {
                        AppLog.Info($"mouse dpi applied dpi={dpi} onboard={controls.OnboardMode}");
                    }
                }

                if (controls.PollingRateHz is int rate && !controls.OnboardMode)
                {
                    if (_rates.Count > 0 && !_rates.Contains(rate))
                    {
                        return WriteResult.Fail($"设备不支持{rate} Hz。");
                    }

                    var result = WritePollingRate(rate);
                    if (!result.Ok)
                    {
                        return result;
                    }
                }

                return WriteResult.Success;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"mouse write failed: {ex.Message}");
                return WriteResult.Fail($"写入失败：{ex.Message}");
            }
            finally
            {
                _requestsAllowed = false;
            }
        }
    }).ContinueWith(task =>
    {
        Refresh();
        return task.Result;
    }, TaskScheduler.Default);

    private WriteResult WriteProfileControls(MouseControls controls)
    {
        if (!_profileValid || _profile is null)
        {
            return WriteResult.Fail("鼠标未暴露可写的板载配置。");
        }

        if (!_profileWritable)
        {
            return WriteResult.Fail("板载配置校验失败，已按只读处理；请先在罗技官方软件中启用板载配置。");
        }

        var data = (byte[])_profile.Clone();
        if (controls.DpiStages is { Count: >= 5 } stages)
        {
            for (var index = 0; index < 5; index++)
            {
                var value = stages[index] == 0 ? 0 : Math.Clamp(stages[index], 200, 12000);
                data[3 + index * 2] = (byte)(value & 0xFF);
                data[4 + index * 2] = (byte)(value >> 8);
            }
        }

        var stageIndex = Math.Clamp(controls.DpiStageIndex ?? data[1], 0, 4);
        data[1] = (byte)stageIndex;
        data[2] = (byte)stageIndex;

        if (controls.PollingRateHz is int rate && rate > 0)
        {
            var interval = 1000 / rate;
            if (interval is >= 1 and <= 8)
            {
                data[0] = (byte)interval;
            }
        }

        if (!string.IsNullOrWhiteSpace(controls.ProfileName) && data.Length >= 208)
        {
            var name = Encoding.Unicode.GetBytes(controls.ProfileName.Trim());
            Array.Clear(data, 160, 48);
            Array.Copy(name, 0, data, 160, Math.Min(name.Length, 48));
        }

        foreach (var button in controls.Buttons)
        {
            if (!int.TryParse(button.Id, out var index) || index is < 0 or >= 16)
            {
                continue;
            }

            if (!MouseActionOptions.Any(option => option.Id == button.Action))
            {
                continue;
            }

            var offset = 32 + index * 4;
            if (offset + 4 > data.Length)
            {
                continue;
            }

            var encoded = EncodeButton(button.Action);
            data[offset] = encoded.B0;
            data[offset + 1] = encoded.B1;
            data[offset + 2] = encoded.B2;
            data[offset + 3] = encoded.B3;
        }

        StampCrc(data);
        var result = WriteProfileSector(_profileSector, data);
        if (result.Ok)
        {
            _profile = data;
        }

        return result;
    }

    private WriteResult WriteProfileSector(int sector, byte[] data)
    {
        var existing = ReadProfileSector(sector, data.Length);
        if (existing.Length == data.Length && existing.SequenceEqual(data))
        {
            return WriteResult.Success;
        }

        var hi = (byte)(sector >> 8);
        var lo = (byte)sector;
        var begin = RequestFull(_featureOnboardProfiles, 0x60,
            [hi, lo, 0x00, 0x00, (byte)(data.Length >> 8), (byte)(data.Length & 0xFF)]);
        AppLog.Info($"mouse profile write begin sector={sector} size={data.Length} " +
                    $"response={Convert.ToHexString(begin ?? [])}");

        for (var offset = 0; offset < data.Length - 1; offset += 16)
        {
            var length = Math.Min(16, data.Length - offset);
            var chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);
            var response = RequestFull(_featureOnboardProfiles, 0x70, chunk);
            if (response is null)
            {
                AppLog.Warn($"mouse profile write chunk offset={offset} no response");
            }
        }

        var finish = RequestFull(_featureOnboardProfiles, 0x80);
        AppLog.Info($"mouse profile write finish response={Convert.ToHexString(finish ?? [])}");

        Thread.Sleep(200);
        var readback = ReadProfileSector(sector, data.Length);
        if (readback.Length != data.Length || !readback.SequenceEqual(data))
        {
            return WriteResult.Fail("板载配置回读不一致，写入未生效。");
        }

        return WriteResult.Success;
    }

    private WriteResult SetOnboardMode(bool enabled)
    {
        var response = RequestFull(_featureOnboardProfiles, 0x10, [enabled ? (byte)0x01 : (byte)0x00]);
        AppLog.Info($"mouse onboard mode set={enabled} response={Convert.ToHexString(response ?? [])}");
        if (response is null)
        {
            return WriteResult.Fail("板载模式切换失败。");
        }

        Thread.Sleep(250);
        var check = RequestFull(_featureOnboardProfiles, 0x20);
        var actual = check is { Length: > 2 } && check[2] != 0;
        return actual == enabled
            ? WriteResult.Success
            : WriteResult.Fail($"设备保留了{(actual ? "板载" : "主机")}模式。");
    }

    private bool TryOpen()
    {
        if (_io is not null)
        {
            return true;
        }

        var candidates = HidDeviceScanner.Scan()
            .Where(d => d.VendorId == LogitechVendorId &&
                        d.UsagePage == HidppUsagePage &&
                        d.Usage is 1 or 2 &&
                        d.OutputReportLength >= 7)
            .OrderByDescending(d => d.Usage == 2)
            .ThenByDescending(d => d.OutputReportLength)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        // 一个接收器只试最合适的集合；失败就等下一轮，避免 7 个槽位 × 多个集合把线程占满。
        var info = candidates[0];
        HidDeviceHandle? handle = null;
        try
        {
            handle = info.Open();
            var io = new HidppIO(handle)
            {
                WritesAllowed = () => _requestsAllowed,
            };
            var index = ProbeDeviceIndex(io);
            if (index == 0)
            {
                io.Dispose();
                AppLog.Info($"mouse probe found no device on {info.VidPid} usage=0x{info.UsagePage:X4}/{info.Usage}");
                return false;
            }

            _io = io;
            _deviceIndex = index;
            // ProbeDeviceIndex 结束时把闸门恢复了 false；解析特征前必须重新打开。
            _requestsAllowed = true;
            ResolveFeatures();
            AppLog.Info($"mouse opened {info.VidPid} usage=0x{info.UsagePage:X4}/{info.Usage} " +
                        $"index={index} report=0x{io.ReportId:X2}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"mouse open failed {info.VidPid}: {ex.Message}");
            handle?.Dispose();
        }

        return false;
    }

    private byte ProbeDeviceIndex(HidppIO io)
    {
        var previous = _requestsAllowed;
        _requestsAllowed = true;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        try
        {
            foreach (var index in DeviceIndexes)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }

                var name = io.Request(index, HidppCodec.FeatureFunc(0x00, 0x00),
                    new byte[] { 0x00, 0x05 }, timeout: TimeSpan.FromMilliseconds(300));
                var nameFeature = name is null ? (byte)0 : HidppCodec.ParseFeatureIndex(name);
                AppLog.Info($"mouse probe index={index} nameFeature={nameFeature}");
                if (nameFeature == 0)
                {
                    continue;
                }

                // 接收器自己的槽位也可能有 name；只有 DPI/轮询率特征才能证明是鼠标。
                var dpiExtended = HasFeature(io, index, 0x2202);
                var dpiLegacy = HasFeature(io, index, 0x2201);
                var rateExtended = HasFeature(io, index, 0x8061);
                var rateLegacy = HasFeature(io, index, 0x8060);
                var battery = HasFeature(io, index, 0x1004) || HasFeature(io, index, 0x1000);
                AppLog.Info($"mouse probe index={index} dpiExt={dpiExtended} dpiLegacy={dpiLegacy} " +
                            $"rateExt={rateExtended} rateLegacy={rateLegacy} battery={battery}");
                if (dpiExtended || dpiLegacy || rateExtended || rateLegacy)
                {
                    return index;
                }
            }

            return 0;
        }
        finally
        {
            _requestsAllowed = previous;
        }
    }

    private static bool HasFeature(HidppIO io, byte index, ushort featureId)
    {
        var response = io.Request(index, HidppCodec.FeatureFunc(0x00, 0x00),
            new[] { (byte)(featureId >> 8), (byte)featureId },
            timeout: TimeSpan.FromMilliseconds(300));
        return response is not null && HidppCodec.ParseFeatureIndex(response) != 0;
    }

    private void ResolveFeatures()
    {
        _featureName = ResolveFeature(0x0005);
        _featureUnifiedBattery = ResolveFeature(0x1004);
        _featureBatteryStatus = ResolveFeature(0x1000);
        _featureDpiExtended = ResolveFeature(0x2202);
        _featureDpiLegacy = ResolveFeature(0x2201);
        _featureRateExtended = ResolveFeature(0x8061);
        _featureRateLegacy = ResolveFeature(0x8060);
        _featureOnboardProfiles = ResolveFeature(0x8100);
        _featureFirmware = ResolveFeature(0x0003);
        AppLog.Info($"mouse features index={_deviceIndex} name={_featureName} " +
                    $"battery={_featureUnifiedBattery}/{_featureBatteryStatus} " +
                    $"dpi={_featureDpiExtended}/{_featureDpiLegacy} " +
                    $"rate={_featureRateExtended}/{_featureRateLegacy} " +
                    $"profiles={_featureOnboardProfiles} firmware={_featureFirmware}");
    }

    private void ReadOnboardProfile()
    {
        _profileValid = false;
        _profile = null;
        _profileName = null;
        if (_featureOnboardProfiles == 0)
        {
            return;
        }

        var format = Request(_featureOnboardProfiles, 0x00);
        if (format is null || format.Length < 12 || format[2] != 0x01)
        {
            AppLog.Info($"mouse onboard format unsupported={Convert.ToHexString(format ?? [])}");
            return;
        }

        var profileIndex = format[3];
        var count = format[5];
        var buttons = format[7];
        var sectors = format[8];
        var size = (format[9] << 8) | format[10];
        var mode = RequestFull(_featureOnboardProfiles, 0x20);
        var onboard = mode is { Length: > 2 } && mode[2] != 0;
        AppLog.Info($"mouse onboard format={Convert.ToHexString(format)} " +
                    $"mode={Convert.ToHexString(mode ?? [])}");
        if (profileIndex > 5 || count == 0 || sectors == 0 || size is < 32 or > 4096)
        {
            AppLog.Info($"mouse onboard rejected profile={profileIndex} count={count} " +
                        $"sectors={sectors} size={size}");
            return;
        }

        var header = RequestFull(_featureOnboardProfiles, 0x50, [0x00, 0x00, 0x00, 0x00]);
        if (header is null || header.Length < 6 ||
            header.Skip(2).Take(4).All(value => value is 0x00 or 0xFF))
        {
            header = RequestFull(_featureOnboardProfiles, 0x50, [0x01, 0x00, 0x00, 0x00]);
        }

        if (header is null || header.Length < 5)
        {
            return;
        }

        var sector = (header[2] << 8) | header[3];
        AppLog.Info($"mouse onboard header={Convert.ToHexString(header)} sector={sector} " +
                    $"enabled={header[4]}");
        if (sector is 0 or 0xFFFF)
        {
            return;
        }

        var data = ReadProfileSector(sector, size);
        if (data.Length < size)
        {
            AppLog.Info($"mouse onboard sector short sector={sector} got={data.Length} size={size}");
            return;
        }

        var expectedCrc = (data[^2] << 8) | data[^1];
        var actualCrc = Crc16(data.AsSpan(0, size - 2));
        AppLog.Info($"mouse onboard data size={size} crc=0x{expectedCrc:X4}/0x{actualCrc:X4} " +
                    $"head={Convert.ToHexString(data.AsSpan(0, Math.Min(48, data.Length)))}");
        if (expectedCrc != actualCrc)
        {
            AppLog.Warn($"mouse onboard crc mismatch sector={sector} expected=0x{expectedCrc:X4} " +
                        $"actual=0x{actualCrc:X4}; profile stays read-only");
        }

        _profileWritable = expectedCrc == actualCrc;
        _profile = data;
        _profileSector = sector;
        _profileSize = size;
        _profileButtonCount = buttons;
        _profileFormat = profileIndex;
        _profileOnboardMode = onboard;
        _profileName = ReadProfileName(data);
        _profileValid = true;
        AppLog.Info($"mouse onboard sector={sector} size={size} buttons={buttons} " +
                    $"onboard={onboard} name='{_profileName}' " +
                    $"stages={string.Join(",", ReadStagesFrom(data))} " +
                    $"mode={Convert.ToHexString(mode ?? [])}");
    }

    private byte[] ReadProfileSector(int sector, int size)
    {
        var result = new List<byte>(size);
        var fullChunks = size / 16;
        for (var index = 0; index < fullChunks; index++)
        {
            var offset = index * 16;
            var chunk = RequestFull(_featureOnboardProfiles, 0x50,
                [(byte)(sector >> 8), (byte)sector, (byte)(offset >> 8), (byte)offset]);
            if (chunk is null || chunk.Length < 18)
            {
                return result.ToArray();
            }

            result.AddRange(chunk.Skip(2).Take(16));
        }

        var remaining = size % 16;
        if (remaining > 0)
        {
            var offset = size - 16;
            var chunk = RequestFull(_featureOnboardProfiles, 0x50,
                [(byte)(sector >> 8), (byte)sector, (byte)(offset >> 8), (byte)offset]);
            if (chunk is null || chunk.Length < 18)
            {
                return result.ToArray();
            }

            result.AddRange(chunk.Skip(2 + 16 - remaining).Take(remaining));
        }

        return result.ToArray();
    }

    private int[]? ReadStages() => _profileValid && _profile is not null
        ? ReadStagesFrom(_profile)
        : null;

    private static int[] ReadStagesFrom(byte[] data)
    {
        var stages = new int[5];
        for (var index = 0; index < stages.Length; index++)
        {
            stages[index] = data[3 + index * 2] | (data[4 + index * 2] << 8);
        }

        return stages;
    }

    private static string? ReadProfileName(byte[] data)
    {
        if (data.Length < 208)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(data, 160, 48).TrimEnd('\0', '\uFFFF');
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private IReadOnlyList<ButtonMapping> ReadButtons()
    {
        if (!_profileValid || _profile is null)
        {
            return [];
        }

        var buttons = new List<ButtonMapping>();
        var count = Math.Min(_profileButtonCount, 16);
        for (var index = 0; index < count; index++)
        {
            var offset = 32 + index * 4;
            if (offset + 4 > _profile.Length)
            {
                break;
            }

            var action = DescribeButton(_profile[offset], _profile[offset + 1],
                _profile[offset + 2], _profile[offset + 3]);
            buttons.Add(new ButtonMapping(ButtonLabel(index, _state.Name), action, index.ToString()));
        }

        return buttons;
    }

    private static string ButtonLabel(int index, string displayName)
    {
        // 左/右/中键是通用物理键名；其余按键因型号而异。
        // 只有已确认布局的 G304/G305 使用侧键/DPI 名称，其他型号显示设备槽位，
        // 不把某一款鼠标的按键名套到所有设备上。
        if (index == 0)
        {
            return "左键";
        }

        if (index == 1)
        {
            return "右键";
        }

        if (index == 2)
        {
            return "中键";
        }

        if (displayName.Contains("G304", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("G305", StringComparison.OrdinalIgnoreCase))
        {
            return index switch
            {
                3 => "后退",
                4 => "前进",
                5 => "DPI",
                _ => $"按键 {index + 1}",
            };
        }

        return $"按键 {index + 1}";
    }

    private static string DescribeButton(byte b0, byte b1, byte b2, byte b3)
    {
        var behavior = b0 >> 4;
        if (behavior == 0x8)
        {
            var type = b1;
            var value = (b2 << 8) | b3;
            return type switch
            {
                0x0 => "disabled",
                0x1 => value switch
                {
                    0x0001 => "left",
                    0x0002 => "right",
                    0x0004 => "middle",
                    0x0008 => "back",
                    0x0010 => "forward",
                    0x2000 => "dpi",
                    _ => $"mouse:0x{value:X4}",
                },
                0x2 => (b2, b3) switch
                {
                    (0x00, 0x28) => "enter",
                    (0x00, 0x29) => "escape",
                    (0x00, 0x2C) => "space",
                    (0x01, 0x06) => "copy",
                    (0x01, 0x19) => "paste",
                    _ => $"key:0x{b2:X2}/0x{b3:X2}",
                },
                0x3 => value switch
                {
                    0x00E9 => "volume_up",
                    0x00EA => "volume_down",
                    0x00E2 => "mute",
                    0x00CD => "play_pause",
                    0x00B5 => "media_next",
                    0x00B6 => "media_prev",
                    _ => $"consumer:0x{value:X4}",
                },
                _ => $"send:{type}",
            };
        }

        return behavior switch
        {
            0x0 => "macro",
            0x1 => "macro_stop",
            0x9 => b1 switch
            {
                0x05 => "dpi",
                _ => $"function:0x{b1:X2}",
            },
            _ => $"raw:0x{b0:X2}{b1:X2}{b2:X2}{b3:X2}",
        };
    }

    private static (byte B0, byte B1, byte B2, byte B3) EncodeButton(string action) => action switch
    {
        "disabled" => (0x80, 0x00, 0xFF, 0xFF),
        "left" => (0x80, 0x01, 0x00, 0x01),
        "right" => (0x80, 0x01, 0x00, 0x02),
        "middle" => (0x80, 0x01, 0x00, 0x04),
        "back" => (0x80, 0x01, 0x00, 0x08),
        "forward" => (0x80, 0x01, 0x00, 0x10),
        "enter" => (0x80, 0x02, 0x00, 0x28),
        "escape" => (0x80, 0x02, 0x00, 0x29),
        "space" => (0x80, 0x02, 0x00, 0x2C),
        "copy" => (0x80, 0x02, 0x01, 0x06),
        "paste" => (0x80, 0x02, 0x01, 0x19),
        "volume_up" => (0x80, 0x03, 0x00, 0xE9),
        "volume_down" => (0x80, 0x03, 0x00, 0xEA),
        "mute" => (0x80, 0x03, 0x00, 0xE2),
        "play_pause" => (0x80, 0x03, 0x00, 0xCD),
        "media_next" => (0x80, 0x03, 0x00, 0xB5),
        "media_prev" => (0x80, 0x03, 0x00, 0xB6),
        "dpi" => (0x90, 0x05, 0x00, 0x00),
        _ => (0x80, 0x00, 0xFF, 0xFF),
    };

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    private static void StampCrc(byte[] data)
    {
        var crc = Crc16(data.AsSpan(0, data.Length - 2));
        data[^2] = (byte)(crc >> 8);
        data[^1] = (byte)crc;
    }

    private byte ResolveFeature(ushort featureId)
    {
        var response = Request(0x00, 0x00, new[] { (byte)(featureId >> 8), (byte)featureId });
        return response is null ? (byte)0 : HidppCodec.ParseFeatureIndex(response);
    }

    private string? ReadName()
    {
        if (_featureName == 0)
        {
            return null;
        }

        var header = Request(_featureName, 0x00);
        if (header is null || header.Length < 3)
        {
            return null;
        }

        var length = header[2];
        AppLog.Info($"mouse name header={Convert.ToHexString(header)} length={length}");
        if (length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(length);
        for (var offset = 0; offset < length; offset += 16)
        {
            var response = Request(_featureName, 0x01, new[] { (byte)offset });
            if (response is null || response.Length <= 2)
            {
                break;
            }

            AppLog.Info($"mouse name chunk offset={offset} data={Convert.ToHexString(response)}");
            var count = Math.Min(16, Math.Min(length - offset, response.Length - 2));
            builder.Append(Encoding.UTF8.GetString(response, 2, count));
        }

        return builder.ToString().TrimEnd('\0').Trim();
    }

    private string? ReadFirmware()
    {
        if (_featureFirmware == 0)
        {
            return null;
        }

        var countResponse = Request(_featureFirmware, 0x00);
        if (countResponse is null || countResponse.Length < 3)
        {
            return null;
        }

        var count = Math.Min((int)countResponse[2], 4);
        var parts = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var response = Request(_featureFirmware, 0x01, new[] { (byte)index });
            if (response is null || response.Length < 9)
            {
                continue;
            }

            var code = Encoding.ASCII.GetString(response, 3, 3).Trim();
            var major = response[6];
            var minor = response[7];
            var build = response[8];
            parts.Add($"{code} {major:X2}.{minor:X2}" + (build > 0 ? $".{build:X2}" : ""));
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    private (int? Battery, ChargingState Charging) ReadBattery()
    {
        if (_featureUnifiedBattery != 0)
        {
            var response = Request(_featureUnifiedBattery, 0x01);
            if (response is not null && HidppCodec.TryParseBattery20(response, out var percent, out var charging))
            {
                return (percent, MapCharging(charging));
            }
        }

        if (_featureBatteryStatus != 0)
        {
            var response = Request(_featureBatteryStatus, 0x00);
            if (response is not null && HidppCodec.TryParseBattery20(response, out var percent, out var charging))
            {
                return (percent, MapCharging(charging));
            }
        }

        return (null, ChargingState.Unknown);
    }

    private int? ReadDpi()
    {
        if (_featureDpiExtended != 0)
        {
            var response = Request(_featureDpiExtended, 0x05);
            AppLog.Info($"mouse dpi ext response={Convert.ToHexString(response ?? [])}");
            if (response is not null && response.Length >= 5)
            {
                var dpi = (response[3] << 8) | response[4];
                if (dpi is > 0 and <= 50000)
                {
                    return dpi;
                }
            }
        }

        if (_featureDpiLegacy != 0)
        {
            var response = Request(_featureDpiLegacy, 0x02, new byte[] { 0x00 });
            AppLog.Info($"mouse dpi legacy response={Convert.ToHexString(response ?? [])}");
            if (response is not null && response.Length >= 5)
            {
                var dpi = (response[3] << 8) | response[4];
                if (dpi is > 0 and <= 50000)
                {
                    return dpi;
                }
            }
        }

        return null;
    }

    private List<int> ReadDpiOptions()
    {
        var options = new List<int>();
        if (_featureDpiExtended != 0)
        {
            var bytes = new List<byte>();
            for (var page = 0; page < 32; page++)
            {
                var response = Request(_featureDpiExtended, 0x02, new byte[] { 0x00, 0x00, (byte)page });
                if (response is null || response.Length <= 5)
                {
                    break;
                }

                for (var i = 5; i < response.Length; i++)
                {
                    bytes.Add(response[i]);
                }

                if (bytes.Count >= 2 && bytes[^1] == 0 && bytes[^2] == 0)
                {
                    break;
                }
            }

            ParseDpiList(bytes, options);
        }
        else if (_featureDpiLegacy != 0)
        {
            var response = Request(_featureDpiLegacy, 0x01, new byte[] { 0x00 });
            if (response is not null && response.Length > 3)
            {
                ParseDpiList(response.Skip(3).ToList(), options);
            }
        }

        return options.Distinct().OrderBy(v => v).ToList();
    }

    private static void ParseDpiList(List<byte> bytes, List<int> options)
    {
        for (var i = 0; i + 1 < bytes.Count;)
        {
            var value = (bytes[i] << 8) | bytes[i + 1];
            if (value == 0)
            {
                break;
            }

            if ((value >> 13) == 0b111)
            {
                var step = value & 0x1FFF;
                if (i + 3 >= bytes.Count)
                {
                    break;
                }

                var last = (bytes[i + 2] << 8) | bytes[i + 3];
                var first = options.Count > 0 ? options[^1] : 0;
                if (step > 0 && last > first)
                {
                    for (var dpi = first + step; dpi <= last; dpi += step)
                    {
                        options.Add(dpi);
                    }
                }

                i += 4;
            }
            else
            {
                options.Add(value);
                i += 2;
            }
        }
    }

    private List<int> ReadPollingOptions()
    {
        var rates = new List<int>();
        if (_featureRateExtended != 0)
        {
            var connection = _deviceIndex == 0xFF ? (byte)0x00 : (byte)0x01;
            var response = Request(_featureRateExtended, 0x01, new[] { connection });
            if (response is not null && response.Length >= 4)
            {
                var mask = (response[2] << 8) | response[3];
                for (var i = 0; i < KnownRates.Length && i < 16; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        rates.Add(KnownRates[i]);
                    }
                }
            }
        }
        else if (_featureRateLegacy != 0)
        {
            var response = Request(_featureRateLegacy, 0x00);
            if (response is not null && response.Length >= 3)
            {
                var mask = response[2];
                foreach (var rate in KnownRates)
                {
                    var ms = 1000 / rate;
                    if (ms is >= 1 and <= 8 && (mask & (1 << (ms - 1))) != 0)
                    {
                        rates.Add(rate);
                    }
                }
            }
        }

        return rates.Distinct().OrderBy(v => v).ToList();
    }

    private int? ReadPollingRate()
    {
        if (_featureRateExtended != 0)
        {
            var response = Request(_featureRateExtended, 0x02);
            AppLog.Info($"mouse rate ext response={Convert.ToHexString(response ?? [])}");
            if (response is not null && response.Length >= 3)
            {
                var index = response[2];
                if (index < KnownRates.Length)
                {
                    return KnownRates[index];
                }
            }
        }

        if (_featureRateLegacy != 0)
        {
            var response = Request(_featureRateLegacy, 0x01);
            AppLog.Info($"mouse rate legacy response={Convert.ToHexString(response ?? [])}");
            if (response is not null && response.Length >= 3 && response[2] > 0)
            {
                return 1000 / response[2];
            }
        }

        return null;
    }

    private WriteResult WriteDpi(int dpi)
    {
        var xHi = (byte)(dpi >> 8);
        var xLo = (byte)(dpi & 0xFF);

        if (_featureDpiExtended != 0)
        {
            var current = Request(_featureDpiExtended, 0x05);
            var lod = current is not null && current.Length >= 12 ? current[11] : (byte)0;
            var response = Request(_featureDpiExtended, 0x06,
                new byte[] { 0x00, xHi, xLo, xHi, xLo, lod });
            AppLog.Info($"mouse dpi ext write response={Convert.ToHexString(response ?? [])}");
            Thread.Sleep(120);
            var confirmed = Request(_featureDpiExtended, 0x05);
            var confirmedDpi = confirmed is not null && confirmed.Length >= 5
                ? (confirmed[3] << 8) | confirmed[4]
                : 0;
            return confirmedDpi == dpi
                ? WriteResult.Success
                : WriteResult.Fail($"设备保留了{confirmedDpi} DPI，写入未生效。");
        }

        if (_featureDpiLegacy != 0)
        {
            var response = Request(_featureDpiLegacy, 0x03, new byte[] { 0x00, xHi, xLo });
            AppLog.Info($"mouse dpi legacy write response={Convert.ToHexString(response ?? [])}");
            Thread.Sleep(120);
            var confirmed = Request(_featureDpiLegacy, 0x02, new byte[] { 0x00 });
            var confirmedDpi = confirmed is not null && confirmed.Length >= 5
                ? (confirmed[3] << 8) | confirmed[4]
                : 0;
            return confirmedDpi == dpi
                ? WriteResult.Success
                : WriteResult.Fail($"设备保留了{confirmedDpi} DPI，写入未生效。");
        }

        return WriteResult.Fail("该设备不支持 DPI 写入。");
    }

    private WriteResult WritePollingRate(int rate)
    {
        if (_featureRateExtended != 0)
        {
            var index = Array.IndexOf(KnownRates, rate);
            if (index < 0)
            {
                return WriteResult.Fail($"不支持{rate} Hz。");
            }

            var response = Request(_featureRateExtended, 0x03, new[] { (byte)index });
            AppLog.Info($"mouse rate ext write response={Convert.ToHexString(response ?? [])}");
            Thread.Sleep(200);
            var confirmed = ReadPollingRate();
            return confirmed == rate
                ? WriteResult.Success
                : WriteResult.Fail($"设备保留了{confirmed?.ToString() ?? "未知"} Hz。");
        }

        if (_featureRateLegacy != 0)
        {
            var ms = 1000 / rate;
            if (ms is < 1 or > 8)
            {
                return WriteResult.Fail($"不支持{rate} Hz。");
            }

            var response = Request(_featureRateLegacy, 0x02, new[] { (byte)ms });
            AppLog.Info($"mouse rate legacy write response={Convert.ToHexString(response ?? [])}");
            Thread.Sleep(200);
            var confirmed = ReadPollingRate();
            return confirmed == rate
                ? WriteResult.Success
                : WriteResult.Fail($"设备保留了{confirmed?.ToString() ?? "未知"} Hz。");
        }

        return WriteResult.Fail("该设备不支持轮询率写入。");
    }

    private byte[]? Request(byte feature, byte func, byte[]? args = null) =>
        _io?.Request(_deviceIndex, HidppCodec.FeatureFunc(feature, func),
            args ?? Array.Empty<byte>(), timeout: TimeSpan.FromSeconds(2));

    // 0x8100 的 0x10/0x20/0x50…0x80 是“功能号<<4”，低半字节由 HidppIO 填入 SwId。
    private byte[]? RequestFull(byte feature, byte func, byte[]? args = null) =>
        _io?.Request(_deviceIndex, HidppCodec.FeatureFunc(feature, (byte)(func >> 4)),
            args ?? Array.Empty<byte>(), patchSw: true, timeout: TimeSpan.FromSeconds(2));

    private static int? StepOf(List<int> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            var step = values[i] - values[i - 1];
            if (step > 0)
            {
                return step;
            }
        }

        return null;
    }

    private static ChargingState MapCharging(VigilCharging charging) => charging switch
    {
        VigilCharging.Charging => ChargingState.Charging,
        VigilCharging.Full => ChargingState.Full,
        VigilCharging.Discharging => ChargingState.Discharging,
        _ => ChargingState.Unknown,
    };

    private void CloseSession()
    {
        _io?.Dispose();
        _io = null;
        _deviceIndex = 0;
        _featureName = 0;
        _featureUnifiedBattery = 0;
        _featureBatteryStatus = 0;
        _featureDpiExtended = 0;
        _featureDpiLegacy = 0;
        _featureRateExtended = 0;
        _featureRateLegacy = 0;
        _featureOnboardProfiles = 0;
        _featureFirmware = 0;
        _profile = null;
        _profileValid = false;
        _profileWritable = false;
        _profileName = null;
        _profileOnboardMode = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        lock (_gate)
        {
            CloseSession();
        }
    }
}

internal sealed record MouseState(
    string Id,
    string Name,
    bool Connected,
    int? BatteryPercent,
    ChargingState Charging,
    string Status,
    bool ControlsAvailable,
    string? Firmware = null);

