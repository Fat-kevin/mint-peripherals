using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DeviceCenterPreview.Core.Models;
using Microsoft.Win32.SafeHandles;
using PreviewCharging = DeviceCenterPreview.Core.Models.ChargingState;

namespace DeviceCenterPreview.App.Services.Battery;

/// <summary>
/// 通用 HID 电池读取器（只读）：用 CfgMgr32 枚举 HID 顶层集合，匹配 Generic Device Controls
/// 的 Battery Strength（usage page 0x06 / usage 0x20）输入报文，读 0–100%；Feature 报文里的
/// Battery System Absolute State of Charge（0x85 / 0x65）作为只读兜底。
/// 后台 30–60 秒轮询，失败静默退避（2s / 10s / 60s）；不写设备、不上报，读不到不发 0%。
/// </summary>
internal sealed class GenericHidBatteryWatcher : IDisposable
{
    /// <summary>正常轮询间隔（设计范围 30–60 秒）。</summary>
    private const int PollSeconds = 45;

    /// <summary>整轮枚举/读取失败后的退避：2s → 10s → 60s，成功后回到正常间隔。</summary>
    private static readonly int[] BackoffSeconds = [2, 10, 60];

    /// <summary>单接口 value caps 数量上限，防御异常描述符导致的超大分配。</summary>
    private const int MaxValueCaps = 256;

    private static readonly Guid HidInterfaceGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    private static readonly object LogGate = new();
    private static string _lastLoggedSignature = "";

    private readonly object _gate = new();
    private IReadOnlyList<BatteryDeviceSnapshot> _devices = Array.Empty<BatteryDeviceSnapshot>();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>设备列表有变化（任意线程）；订阅方自行切回 UI 线程。</summary>
    public event EventHandler? Changed;

    /// <summary>最近一轮成功读取到的通用 HID 设备（不可变快照）。</summary>
    public IReadOnlyList<BatteryDeviceSnapshot> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            int delaySeconds;
            try
            {
                var devices = PollOnce(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Publish(devices);
                failures = 0;
                delaySeconds = PollSeconds;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                failures = Math.Min(failures + 1, BackoffSeconds.Length);
                delaySeconds = BackoffSeconds[failures - 1];
                AppLog.Warn($"generic HID battery poll failed; retry in {delaySeconds}s: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(IReadOnlyList<BatteryDeviceSnapshot> devices)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _devices = devices;
        }

        LogIfChanged(devices);
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("generic HID battery Changed handler failed: " + ex.Message);
        }
    }

    /// <summary>整轮扫描：枚举全部 HID 接口，逐接口读取，单个接口失败不影响其它接口。</summary>
    private static IReadOnlyList<BatteryDeviceSnapshot> PollOnce(CancellationToken token)
    {
        var found = new List<BatteryDeviceSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnumerateHidPaths())
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var device = InspectInterface(path);
                if (device is null)
                {
                    continue;
                }

                var key = DedupeKey(device);
                if (seen.Add(key))
                {
                    found.Add(device);
                }
            }
            catch
            {
                // 打开失败/被独占/描述符异常：本轮跳过该接口，下轮重试（不视为设备离线）。
            }
        }

        return found;
    }

    private static string DedupeKey(BatteryDeviceSnapshot device) =>
        device.Id.Length > 0 ? device.Id : device.DisplayName;

    private static IReadOnlyList<string> EnumerateHidPaths()
    {
        var paths = new List<string>();
        try
        {
            var guid = HidInterfaceGuid;
            if (Native.CM_Get_Device_Interface_List_SizeW(
                    out uint size, ref guid, null, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 ||
                size == 0)
            {
                return paths;
            }

            var buffer = new char[size];
            if (Native.CM_Get_Device_Interface_ListW(
                    ref guid, null, buffer, size, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0)
            {
                return paths;
            }

            paths.AddRange(new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            // CfgMgr32 不可用时本轮无结果，由轮询循环的退避兜住。
        }

        return paths;
    }

    private static BatteryDeviceSnapshot? InspectInterface(string path)
    {
        using var handle = Native.CreateFileW(
            path,
            Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        // 键盘/鼠标的受保护 collection 会拒绝只读打开：直接跳过，不请求任何权限升级。
        if (handle.IsInvalid || !Native.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            var caps = NewCaps();
            if (Native.HidP_GetCaps(preparsed, ref caps) != Native.HIDP_STATUS_SUCCESS)
            {
                return null;
            }

            // 数位板/手写屏（0x0D）与电源/UPS（0x84/0x85）不是外设电量，按调研文档一律跳过。
            if (caps.UsagePage is 0x0D or 0x84 or 0x85)
            {
                return null;
            }

            var cap = FindBatteryValueCap(preparsed, caps);
            if (cap is null)
            {
                return null;
            }

            int? percent = TryReadPercent(handle, preparsed, caps);
            if (percent is null)
            {
                // 读不到就不显示（不把 0% 或历史值冒充当前电量）。
                return null;
            }

            var attributes = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            _ = Native.HidD_GetAttributes(handle, ref attributes);

            var product = ReadProduct(handle);
            var name = string.IsNullOrWhiteSpace(product)
                ? $"HID 电池设备 {attributes.VendorID:X4}:{attributes.ProductID:X4}"
                : product;
            var kind = GuessKind(name, caps.UsagePage, caps.Usage);

            return new BatteryDeviceSnapshot(
                kind,
                BuildId(path, attributes.VendorID, attributes.ProductID),
                name,
                percent.Value,
                PreviewCharging.Unknown,
                true,
                Source: "hid-battery");
        }
        finally
        {
            _ = Native.HidD_FreePreparsedData(preparsed);
        }
    }

    private static Native.HIDP_CAPS NewCaps() => new() { Reserved = new ushort[17] };

    private static BatteryValueCap? FindBatteryValueCap(IntPtr preparsed, in Native.HIDP_CAPS caps)
    {
        return FindInReport(preparsed, Native.HIDP_REPORT_TYPE.HidP_Input, caps.NumberInputValueCaps)
            ?? FindInReport(preparsed, Native.HIDP_REPORT_TYPE.HidP_Feature, caps.NumberFeatureValueCaps);
    }

    private static BatteryValueCap? FindInReport(IntPtr preparsed, Native.HIDP_REPORT_TYPE reportType, ushort count)
    {
        if (count == 0)
        {
            return null;
        }

        int usable = Math.Min((int)count, MaxValueCaps);
        int structSize = Marshal.SizeOf<Native.HIDP_VALUE_CAPS>();
        IntPtr buffer = Marshal.AllocHGlobal(structSize * usable);
        try
        {
            ushort length = (ushort)usable;
            if (Native.HidP_GetValueCaps(reportType, buffer, ref length, preparsed) != Native.HIDP_STATUS_SUCCESS)
            {
                return null;
            }

            for (int index = 0; index < length; index++)
            {
                var caps = Marshal.PtrToStructure<Native.HIDP_VALUE_CAPS>(IntPtr.Add(buffer, index * structSize));
                if (caps.IsAlias != 0 || !TryMatchBatteryUsage(caps, out ushort usage))
                {
                    continue;
                }

                return new BatteryValueCap(
                    reportType,
                    caps.ReportID,
                    caps.LinkCollection,
                    caps.UsagePage,
                    usage,
                    caps.LogicalMin,
                    caps.LogicalMax);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryMatchBatteryUsage(in Native.HIDP_VALUE_CAPS caps, out ushort usage)
    {
        // Generic Device Controls / Battery Strength（0x06:0x20），设计要求的首选来源。
        if (caps.UsagePage == 0x06 && InRange(caps, 0x20))
        {
            usage = 0x20;
            return true;
        }

        // Battery System / Absolute State Of Charge（0x85:0x65），仅作为只读兜底。
        if (caps.UsagePage == 0x85 && InRange(caps, 0x65))
        {
            usage = 0x65;
            return true;
        }

        usage = 0;
        return false;
    }

    private static bool InRange(in Native.HIDP_VALUE_CAPS caps, ushort wanted) =>
        caps.UsageMin <= wanted && caps.UsageMax >= wanted;

    private static int? TryReadPercent(
        SafeFileHandle handle,
        IntPtr preparsed,
        in Native.HIDP_CAPS caps)
    {
        // 输入报文优先（Battery Strength 常规就在 input report）；GET_REPORT(Input) 失败时
        // 再只读尝试 Feature 报文，两者都不成功就返回 null。
        var inputCap = FindInReport(preparsed, Native.HIDP_REPORT_TYPE.HidP_Input, caps.NumberInputValueCaps);
        if (inputCap is { } input && caps.InputReportByteLength > 0)
        {
            int? percent = ReadReport(handle, preparsed, input, caps.InputReportByteLength, input: true);
            if (percent is not null)
            {
                return percent;
            }
        }

        var featureCap = FindInReport(preparsed, Native.HIDP_REPORT_TYPE.HidP_Feature, caps.NumberFeatureValueCaps);
        if (featureCap is { } feature && caps.FeatureReportByteLength > 0)
        {
            return ReadReport(handle, preparsed, feature, caps.FeatureReportByteLength, input: false);
        }

        return null;
    }

    private static int? ReadReport(
        SafeFileHandle handle,
        IntPtr preparsed,
        in BatteryValueCap cap,
        int reportLength,
        bool input)
    {
        var report = new byte[reportLength];
        report[0] = cap.ReportId;

        bool ok = input
            ? Native.HidD_GetInputReport(handle, report, (uint)reportLength)
            : Native.HidD_GetFeature(handle, report, (uint)reportLength);
        if (!ok)
        {
            return null;
        }

        int status = Native.HidP_GetUsageValue(
            cap.ReportType,
            cap.UsagePage,
            cap.LinkCollection,
            cap.Usage,
            out uint value,
            preparsed,
            report,
            (uint)reportLength);
        if (status != Native.HIDP_STATUS_SUCCESS)
        {
            return null;
        }

        return ScaleToPercent(value, cap.LogicalMin, cap.LogicalMax);
    }

    /// <summary>
    /// 按 value caps 的 LogicalMin/Max 线性映射到 0–100%。LogicalMax 与 LogicalMin 相同
    /// （无有效范围）时只接受已经在 0–100 内的读数，越界视为不可信。
    /// </summary>
    private static int? ScaleToPercent(uint raw, int logicalMin, int logicalMax)
    {
        if (logicalMax > logicalMin)
        {
            long value = raw;
            if (value < logicalMin || value > logicalMax)
            {
                return null;
            }

            int percent = (int)Math.Round(
                (value - logicalMin) * 100.0 / (logicalMax - logicalMin),
                MidpointRounding.AwayFromZero);
            return Math.Clamp(percent, 0, 100);
        }

        return raw <= 100 ? (int)raw : null;
    }

    private static string ReadProduct(SafeFileHandle handle)
    {
        try
        {
            var buffer = new char[128];
            return Native.HidD_GetProductString(handle, buffer, (uint)(buffer.Length * 2))
                ? new string(buffer).TrimEnd('\0').Trim()
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string BuildId(string path, ushort vendorId, ushort productId)
    {
        // 设备实例路径含端口/序列号等隐私信息，只保留稳定的短哈希做 Id。
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        var suffix = Convert.ToHexString(hash, 0, 4);
        return $"hid:{vendorId:X4}:{productId:X4}:{suffix}";
    }

    private static BatteryDeviceKind GuessKind(string name, ushort topUsagePage, ushort topUsage)
    {
        var lower = name.ToLowerInvariant();
        if (ContainsAny(lower, "keyboard", "键盘"))
        {
            return BatteryDeviceKind.Keyboard;
        }

        if (ContainsAny(lower, "gamepad", "controller", "手柄", "xbox", "dualsense", "dualshock"))
        {
            return BatteryDeviceKind.Gamepad;
        }

        if (ContainsAny(lower, "speaker", "音箱", "扬声器"))
        {
            return BatteryDeviceKind.Speaker;
        }

        if (ContainsAny(lower, "headset", "headphone", "耳机", "earbud", "buds"))
        {
            return BatteryDeviceKind.Headset;
        }

        if (ContainsAny(lower, "mouse", "鼠标"))
        {
            return BatteryDeviceKind.Mouse;
        }

        // 名字看不出来时按 HID 顶层集合补一次（Keypad/Keyboard 0x01:0x06、Mouse 0x01:0x02、Game Pad 0x01:0x05）。
        return topUsagePage switch
        {
            0x01 when topUsage is 0x06 or 0x07 => BatteryDeviceKind.Keyboard,
            0x01 when topUsage is 0x02 => BatteryDeviceKind.Mouse,
            0x01 when topUsage is 0x05 => BatteryDeviceKind.Gamepad,
            _ => BatteryDeviceKind.Other,
        };
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static void LogIfChanged(IReadOnlyList<BatteryDeviceSnapshot> devices)
    {
        var signature = string.Join(
            " | ",
            devices
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => $"{d.Id}:{d.Percent?.ToString() ?? "-"}"));
        lock (LogGate)
        {
            if (signature == _lastLoggedSignature)
            {
                return;
            }

            _lastLoggedSignature = signature;
        }

        if (devices.Count > 0)
        {
            AppLog.Info($"generic HID battery devices: {signature}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private readonly record struct BatteryValueCap(
        Native.HIDP_REPORT_TYPE ReportType,
        byte ReportId,
        ushort LinkCollection,
        ushort UsagePage,
        ushort Usage,
        int LogicalMin,
        int LogicalMax);

    /// <summary>hid.dll / CfgMgr32 / kernel32 的最小只读声明（不引用 VigilWatt 的 internal 原生层）。</summary>
    private static class Native
    {
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        public enum HIDP_REPORT_TYPE
        {
            HidP_Input = 0,
            HidP_Output = 1,
            HidP_Feature = 2,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        /// <summary>HIDP_VALUE_CAPS（hidpi.h，pshpack4，72 字节）——布局必须与 Windows SDK 一致。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            public ushort Reserved2A;
            public ushort Reserved2B;
            public ushort Reserved2C;
            public ushort Reserved2D;
            public ushort Reserved2E;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_SizeW(
            out uint size, ref Guid interfaceClass, string? deviceId, uint flags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_ListW(
            ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint size, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetValueCaps(
            HIDP_REPORT_TYPE reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetUsageValue(
            HIDP_REPORT_TYPE reportType,
            ushort usagePage,
            ushort linkCollection,
            ushort usage,
            out uint usageValue,
            IntPtr preparsedData,
            [Out] byte[] report,
            uint reportLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(
            SafeFileHandle handle, [Out] byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(
            SafeFileHandle handle, [Out] byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetProductString(
            SafeFileHandle handle, [Out] char[] buffer, uint bufferLength);
    }
}
