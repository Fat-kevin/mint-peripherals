using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;
using DeviceCenterPreview.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 功 能： 任务栏内嵌电量条：通知区域左侧 2px 的透明 layered 子窗口，
///        逐设备显示 [图标] 百分比，替代旧的任务栏胶囊。
/// 2026/11/27 xliu
/// 结构：隐藏顶层宿主窗口（收 TaskbarCreated / WM_SETTINGCHANGE 广播）
///       + Shell_TrayWnd 子窗口（SetParent → WS_CHILD → UpdateLayeredWindow 三道锁）。
/// 线程：全部在 UI 线程；服务事件经 DispatcherQueue 回投。
/// </summary>
internal sealed class TaskbarBatteryStrip : IDisposable
{
    // 设备最多显示项；其余折成 +N（tooltip 仍然全量）。
    private const int MaxVisibleDevices = 6;

    // 布局（96 DPI 逻辑像素，绘制前统一乘 DPI 缩放）。见 design §2.4。
    private const double TextDipMedium = 13;
    private const double TextDipSmall = 12;
    private const double TextDipLarge = 15;
    private const double IconDip = 16;
    private const double IconTextGapDip = 5;
    private const double DeviceGapDip = 14;
    private const double EdgePadDip = 8;
    private const double StatusGlyphDip = 11;
    private const double StatusGapDip = 3;
    private const double PanelInsetDip = 4;
    private const double PanelRadiusDip = 6;
    private const double TrayGapDip = 2;
    private const double FallbackLeftDip = 235;
    // 任务栏左侧小组件（天气/Widgets）预留宽度：放在 Start/ReBar 左边时避免盖住它。
    private const double WidgetsReserveDip = 170;

    // 窗口类名与 Win32 常量。
    private const string StripClassName = "DeviceCenterPreviewTaskbarBatteryStrip";
    private const string HostClassName = "DeviceCenterPreviewTaskbarStripHost";
    private const string TooltipsClassName = "tooltips_class32";
    private const int GWL_STYLE = -16;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint SW_HIDE = 0;
    private const uint SW_SHOWNOACTIVATE = 4;
    private const uint ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const uint WM_NULL = 0x0000;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint MA_NOACTIVATE = 3;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0;
    private const uint MF_SEPARATOR = 0x0800;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint TTS_ALWAYSTIP = 0x01;
    private const uint TTS_NOPREFIX = 0x02;
    private const uint TTF_IDISHWND = 0x0001;
    private const uint TTF_SUBCLASS = 0x0010;
    private const uint TTM_ADDTOOLW = 0x0400 + 50;
    private const uint TTM_UPDATETIPTEXTW = 0x0400 + 57;
    private const uint TTM_SETMAXTIPWIDTH = 0x0400 + 24;
    private const int TooltipMaxWidth = 600;

    // 任务栏重启重建冷却，避免 Explorer 连续广播时抖动。
    private const long RebuildCooldownTicks = 2500;

    private static readonly WndProcDelegate StaticProc = WndProc;
    private static bool _stripClassRegistered;
    private static bool _hostClassRegistered;
    private static TaskbarBatteryStrip? _current;

    private static readonly string ThemePersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly IPeripheralService _service;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Action _openMainWindow;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly uint _taskbarCreatedMessage;
    private readonly StringFormat _typographic = StringFormat.GenericTypographic;

    private IntPtr _hwnd;
    private IntPtr _host;
    private IntPtr _tooltip;
    private IntPtr _tooltipText;
    private IntPtr _taskbar;
    private string _tooltipContent = "";
    private bool? _tooltipLight;
    private string _signature = "";
    private bool _visible;
    private bool _hiddenForFullscreen;
    private bool _disposed;
    private long _lastRebuildTick;
    private string? _iconFace;
    private string? _textFace;
    private bool _iconFaceResolved;
    private bool _textFaceResolved;

    public TaskbarBatteryStrip(
        IPeripheralService service,
        Func<AppSettings> settingsProvider,
        Action openMainWindow,
        Action openSettings,
        Action exitApplication)
    {
        _service = service;
        _settingsProvider = settingsProvider;
        _openMainWindow = openMainWindow;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("任务栏电量条必须在 UI 线程创建。");
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _current = this;

        EnsureHost();
        _service.Changed += OnServiceChanged;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    /// <summary>设置变化后立即生效：清签名后下一帧按新样式/字号/开关重排。</summary>
    public void ApplySettings() => ScheduleUpdate();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _timer.Stop();
        }
        catch
        {
            // 退出路径上 DispatcherQueue 可能已不可用。
        }

        _service.Changed -= OnServiceChanged;
        DestroyStrip();
        if (_host != IntPtr.Zero && IsWindow(_host))
        {
            DestroyWindow(_host);
        }

        _host = IntPtr.Zero;
        _taskbar = IntPtr.Zero;
        _typographic.Dispose();
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    private void OnServiceChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(Update);

    // ------------------------------------------------------------------ 窗口

    private void EnsureHost()
    {
        if (_host != IntPtr.Zero && IsWindow(_host))
        {
            return;
        }

        RegisterClassOnce(HostClassName, ref _hostClassRegistered);
        _host = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            HostClassName,
            "外设中枢任务栏条",
            WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_host == IntPtr.Zero)
        {
            AppLog.Warn($"taskbar strip host create failed err={Marshal.GetLastWin32Error()}");
        }
    }

    private bool EnsureStripWindow()
    {
        if (_taskbar == IntPtr.Zero)
        {
            return false;
        }

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            if (GetParent(_hwnd) == _taskbar)
            {
                return true;
            }

            DestroyStrip();
        }

        if (_host == IntPtr.Zero)
        {
            EnsureHost();
            if (_host == IntPtr.Zero)
            {
                return false;
            }
        }

        RegisterClassOnce(StripClassName, ref _stripClassRegistered);
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            StripClassName,
            "任务栏电量",
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            AppLog.Warn($"taskbar strip create failed err={Marshal.GetLastWin32Error()}");
            return false;
        }

        // 三道锁：SetParent 到任务栏 → 改 WS_CHILD|WS_CLIPSIBLINGS → layered + UpdateLayeredWindow。
        SetParent(_hwnd, _taskbar);
        var style = GetWindowLongPtrW(_hwnd, GWL_STYLE).ToInt64();
        style &= ~(long)WS_POPUP;
        style |= WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtrW(_hwnd, GWL_STYLE, new IntPtr(style));

        EnsureTooltip();
        return true;
    }

    private void DestroyStrip()
    {
        DestroyTooltip();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        _visible = false;
        _signature = "";
    }

    private void HideStrip()
    {
        if (_visible && _hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            ShowWindow(_hwnd, SW_HIDE);
        }

        _visible = false;
        _signature = "";
    }

    private static void RegisterClassOnce(string className, ref bool registered)
    {
        if (registered)
        {
            return;
        }

        registered = true;
        var wc = new WNDCLASSW
        {
            style = 0x0002,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = className,
        };
        RegisterClassW(ref wc);
    }

    // ------------------------------------------------------------------ tooltip

    private void EnsureTooltip()
    {
        if (_tooltip != IntPtr.Zero && IsWindow(_tooltip))
        {
            return;
        }

        _tooltip = CreateWindowExW(
            WS_EX_TOPMOST,
            TooltipsClassName,
            null,
            WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
            0, 0, 0, 0,
            _host, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_tooltip == IntPtr.Zero)
        {
            AppLog.Warn($"taskbar strip tooltip create failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        SetWindowPos(_tooltip, new IntPtr(-1), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SendMessagePointer(_tooltip, TTM_SETMAXTIPWIDTH, IntPtr.Zero, (IntPtr)TooltipMaxWidth);

        var info = new TOOLINFOW
        {
            cbSize = (uint)Marshal.SizeOf<TOOLINFOW>(),
            uFlags = TTF_IDISHWND | TTF_SUBCLASS,
            hwnd = _host,
            uId = _hwnd,
            lpszText = IntPtr.Zero,
        };
        SendMessageToolInfo(_tooltip, TTM_ADDTOOLW, IntPtr.Zero, ref info);
    }

    private void DestroyTooltip()
    {
        if (_tooltipText != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_tooltipText);
            _tooltipText = IntPtr.Zero;
        }

        if (_tooltip != IntPtr.Zero && IsWindow(_tooltip))
        {
            DestroyWindow(_tooltip);
        }

        _tooltip = IntPtr.Zero;
        _tooltipContent = "";
        _tooltipLight = null;
    }

    private void UpdateTooltip(IReadOnlyList<BatteryDeviceSnapshot> devices, bool light)
    {
        if (_tooltip == IntPtr.Zero || _hwnd == IntPtr.Zero || !IsWindow(_tooltip))
        {
            return;
        }

        if (_tooltipLight != light)
        {
            _tooltipLight = light;
            SetWindowTheme(_tooltip, light ? "Explorer" : "DarkMode_Explorer", null);
        }

        // 文本不变不更新，避免每秒刷 tooltip。
        var text = BuildTooltipText(devices);
        if (string.Equals(text, _tooltipContent, StringComparison.Ordinal))
        {
            return;
        }

        var pointer = Marshal.StringToHGlobalUni(text);
        var info = new TOOLINFOW
        {
            cbSize = (uint)Marshal.SizeOf<TOOLINFOW>(),
            uFlags = TTF_IDISHWND | TTF_SUBCLASS,
            hwnd = _host,
            uId = _hwnd,
            lpszText = pointer,
        };
        SendMessageToolInfo(_tooltip, TTM_UPDATETIPTEXTW, IntPtr.Zero, ref info);
        if (_tooltipText != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_tooltipText);
        }

        _tooltipText = pointer;
        _tooltipContent = text;
    }

    private static string BuildTooltipText(IReadOnlyList<BatteryDeviceSnapshot> devices)
    {
        if (devices.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder(128);
        foreach (var device in devices)
        {
            if (builder.Length > 0)
            {
                builder.Append("\r\n");
            }

            builder.Append(string.IsNullOrWhiteSpace(device.DisplayName) ? "未知设备" : device.DisplayName);
            builder.Append("  ");
            builder.Append(device.Percent is int percent ? $"{percent}%" : "电量未知");

            var extra = new List<string>(2);
            var full = IsFullyCharged(device.Charging, device.Percent);
            if (device.Charging == ChargingState.Charging && !full)
            {
                extra.Add("充电中");
            }
            else if (full)
            {
                extra.Add("已充满");
            }

            if (device.IsAsleep)
            {
                extra.Add("休眠");
            }

            if (!device.IsConnected && !device.IsAsleep)
            {
                extra.Add("未连接");
            }

            if (extra.Count > 0)
            {
                builder.Append(" · ").Append(string.Join(" · ", extra));
            }

            var components = BuildComponentSuffix(device);
            if (components is not null)
            {
                builder.Append("（").Append(components).Append('）');
            }
        }

        return builder.ToString();
    }

    private static string? BuildComponentSuffix(BatteryDeviceSnapshot device)
    {
        if (device.Components is not { Count: > 1 })
        {
            return null;
        }

        var parts = new List<string>(device.Components.Count);
        foreach (var component in device.Components)
        {
            if (component.Percent is not int percent)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(component.Label) ? component.Key : component.Label;
            parts.Add($"{label} {percent}%");
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    // ------------------------------------------------------------------ 主循环

    private void Update()
    {
        if (_disposed)
        {
            return;
        }

        if (_host == IntPtr.Zero)
        {
            EnsureHost();
        }

        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !IsWindow(taskbar))
        {
            _taskbar = IntPtr.Zero;
            HideStrip();
            return;
        }

        _taskbar = taskbar;
        if (!GetWindowRect(taskbar, out var barRect) ||
            barRect.Width <= 8 ||
            barRect.Height <= 8 ||
            barRect.Width < barRect.Height)
        {
            // 竖向任务栏 / 几何异常：v1 不支持，隐藏条（托盘图标仍可用）。
            HideStrip();
            return;
        }

        if (!IsWindowVisible(taskbar))
        {
            HideStrip();
            return;
        }

        if (IsFullscreenForeground())
        {
            if (!_hiddenForFullscreen)
            {
                AppLog.Info("taskbar strip hidden for fullscreen");
                _hiddenForFullscreen = true;
            }

            HideStrip();
            return;
        }

        _hiddenForFullscreen = false;

        var settings = _settingsProvider();
        if (!settings.TaskbarStripEnabled)
        {
            HideStrip();
            return;
        }

        var scale = GetDpiForWindow(taskbar) is uint dpi && dpi >= 96 ? dpi / 96.0 : 1.0;
        var light = IsLightTheme();
        var trayEdge = ResolveTrayEdge(taskbar, barRect, scale);
        var startEdge = ResolveStartEdge(taskbar, barRect);
        var devices = CollectDevices(settings);
        var signature = BuildSignature(barRect, trayEdge, startEdge, scale, light, settings, devices);

        if (string.Equals(signature, _signature, StringComparison.Ordinal) && _visible)
        {
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd) && GetParent(_hwnd) == taskbar)
            {
                // 几何/内容都没变：不建 GDI+ 对象、不重绘、不 SetWindowPos。
                return;
            }

            _visible = false;
        }

        _signature = signature;

        if (devices.Count == 0)
        {
            HideStrip();
            return;
        }

        if (!EnsureStripWindow())
        {
            return;
        }

        Render(devices, settings, barRect, trayEdge, startEdge, scale, light);
        UpdateTooltip(devices, light);
    }

    private List<BatteryDeviceSnapshot> CollectDevices(AppSettings settings)
    {
        IReadOnlyList<BatteryDeviceSnapshot> source;
        try
        {
            source = _service.GetBatteryDevices();
        }
        catch (Exception ex)
        {
            AppLog.Warn("taskbar strip: GetBatteryDevices failed: " + ex.Message);
            return new List<BatteryDeviceSnapshot>();
        }

        var result = new List<BatteryDeviceSnapshot>(source.Count);
        foreach (var device in source)
        {
            if (device.IsOffline)
            {
                continue;
            }

            if (!device.IsConnected && !device.IsAsleep)
            {
                continue;
            }

            if (device.IsAsleep && !settings.TaskbarStripShowSleeping)
            {
                continue;
            }

            if (!IsPrimaryKind(device.Kind) && !settings.ShowOtherBatteryDevices)
            {
                continue;
            }

            result.Add(device);
        }

        result.Sort((left, right) => KindOrder(left.Kind).CompareTo(KindOrder(right.Kind)));
        return result;
    }

    private static string BuildSignature(
        RECT barRect,
        int trayEdge,
        int? startEdge,
        double scale,
        bool light,
        AppSettings settings,
        IReadOnlyList<BatteryDeviceSnapshot> devices)
    {
        var builder = new StringBuilder(192);
        builder.Append(barRect.Left).Append(',')
            .Append(barRect.Top).Append(',')
            .Append(barRect.Right).Append(',')
            .Append(barRect.Bottom).Append('|')
            .Append(trayEdge).Append('|')
            .Append(startEdge?.ToString() ?? "-").Append(settings.TaskbarStripPosition).Append('|')
            .Append((int)Math.Round(scale * 100)).Append(light ? 'L' : 'D').Append('|')
            .Append(settings.TaskbarStripStyle).Append('/')
            .Append(settings.TaskbarStripFontSize).Append('/')
            .Append(settings.TaskbarStripBackground ? '1' : '0')
            .Append(settings.TaskbarStripShowSleeping ? '1' : '0')
            .Append('|');

        foreach (var device in devices)
        {
            builder.Append((int)device.Kind).Append(':')
                .Append(device.Id).Append(':')
                .Append(device.Percent?.ToString() ?? "-").Append(':')
                .Append((int)device.Charging).Append(':')
                .Append(device.IsAsleep ? 'S' : 'a').Append(';');
        }

        return builder.ToString();
    }

    private static int ResolveTrayEdge(IntPtr taskbar, RECT barRect, double scale)
    {
        if (TryGetChildRect(taskbar, "TrayNotifyWnd", barRect, out var trayRect))
        {
            return trayRect.Left;
        }

        if (TryGetChildRect(taskbar, "ClockButton", barRect, out var clockRect))
        {
            return clockRect.Left;
        }

        // 托盘/时钟拿不到（被美化工具替换或任务栏未就绪）：回退旧固定位置（235 DIP）。
        return barRect.Left + (int)Math.Round(FallbackLeftDip * scale);
    }

    /// <summary>
    /// 任务栏左侧的固定任务按钮/开始按钮左缘：Windows 11 居中任务栏时它是任务按钮区的起点；
    /// Start 子窗口在 Win11 上通常存在但不可见（XAML 仍保留 HWND），因此允许不可见。
    /// </summary>
    private static int? ResolveStartEdge(IntPtr taskbar, RECT barRect)
    {
        if (TryGetChildRect(taskbar, "Start", barRect, out var start, requireVisible: false) &&
            start.Left > barRect.Left + 8)
        {
            return start.Left;
        }

        if (TryGetChildRect(taskbar, "ReBarWindow32", barRect, out var rebar, requireVisible: false) &&
            rebar.Left > barRect.Left + 8)
        {
            return rebar.Left;
        }

        return null;
    }

    private static bool TryGetChildRect(
        IntPtr parent,
        string className,
        RECT parentRect,
        out RECT rect,
        bool requireVisible = true)
    {
        rect = default;
        var child = FindWindowExW(parent, IntPtr.Zero, className, null);
        if (child == IntPtr.Zero || (requireVisible && !IsWindowVisible(child)) || !GetWindowRect(child, out rect))
        {
            return false;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        const int tolerance = 2;
        return rect.Left >= parentRect.Left - tolerance &&
               rect.Right <= parentRect.Right + tolerance &&
               rect.Top >= parentRect.Top - tolerance &&
               rect.Bottom <= parentRect.Bottom + tolerance;
    }

    // ------------------------------------------------------------------ 绘制

    private void Render(
        IReadOnlyList<BatteryDeviceSnapshot> devices,
        AppSettings settings,
        RECT barRect,
        int trayEdge,
        int? startEdge,
        double scale,
        bool light)
    {
        var pad = Px(EdgePadDip, scale);
        var iconSize = Px(IconDip, scale);
        var iconGap = Px(IconTextGapDip, scale);
        var deviceGap = Px(DeviceGapDip, scale);
        var statusGap = Px(StatusGapDip, scale);
        var statusWidth = Px(StatusGlyphDip, scale);
        var height = barRect.Height;
        var textSize = (float)(TextDip(settings) * scale);
        var style = settings.TaskbarStripStyle;
        var iconFace = ResolveIconFace();
        var iconsAvailable = iconFace is not null;
        var showIcon = style is not "percent" && iconsAvailable;
        // 没有图标字体时也不能只剩空白：退化为纯文字样式。
        var showText = style is not "icon" || !iconsAvailable;
        var textFace = ResolveTextFace();

        var textFont = new Font(textFace, textSize, FontStyle.Regular, GraphicsUnit.Pixel);
        Font? iconFont = null;
        Font? statusFont = null;
        try
        {
            if (iconsAvailable)
            {
                iconFont = new Font(iconFace!, iconSize, FontStyle.Regular, GraphicsUnit.Pixel);
                statusFont = new Font(iconFace!, (float)statusWidth, FontStyle.Regular, GraphicsUnit.Pixel);
            }

            var items = BuildRenderItems(devices, showIcon);
            var overflow = Math.Max(0, devices.Count - items.Count);

            using var measureBitmap = new Bitmap(1, 1);
            using var measureGraphics = Graphics.FromImage(measureBitmap);

            var textWidths = new int[items.Count];
            var needText = new bool[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                // 仅图标样式下，未知电量仍然画一个 "?"，避免状态完全丢失。
                needText[index] = showText || items[index].Device.Percent is null;
                textWidths[index] = needText[index]
                    ? MeasureText(measureGraphics, items[index].Text, textFont)
                    : 0;
            }

            var initialOverflowWidth = overflow > 0
                ? MeasureText(measureGraphics, $"+{overflow}", textFont)
                : 0;

            var effectiveStatusWidth = iconsAvailable ? statusWidth : 0;
            var effectiveStatusGap = iconsAvailable ? statusGap : 0;
            var totalWidth = pad * 2;
            for (var index = 0; index < items.Count; index++)
            {
                totalWidth += ItemWidth(items[index], textWidths[index], iconSize, iconGap, effectiveStatusWidth, effectiveStatusGap);
                if (index < items.Count - 1)
                {
                    totalWidth += deviceGap;
                }
            }

            if (overflow > 0)
            {
                totalWidth += deviceGap + initialOverflowWidth;
            }

            var minX = barRect.Left + Px(PanelInsetDip, scale);
            var trayMax = trayEdge - Px(TrayGapDip, scale);
            var startMax = startEdge is int startLeft ? startLeft - Px(TrayGapDip, scale) : 0;
            var startMin = barRect.Left + Px(WidgetsReserveDip, scale);
            var position = settings.TaskbarStripPosition;

            var shown = items.Count;
            var placed = false;
            var x = minX;

            // 放不下就少显示几个设备，最少保留 1 个，其余折成 +N；返回最终宽度是否装得下。
            bool FitWidth(int available)
            {
                while (totalWidth > available && shown > 1)
                {
                    shown--;
                    overflow++;
                    totalWidth = pad * 2;
                    for (var index = 0; index < shown; index++)
                    {
                        totalWidth += ItemWidth(items[index], textWidths[index], iconSize, iconGap, effectiveStatusWidth, effectiveStatusGap);
                        if (index < shown - 1)
                        {
                            totalWidth += deviceGap;
                        }
                    }

                    totalWidth += deviceGap + MeasureText(measureGraphics, $"+{overflow}", textFont);
                }

                return totalWidth <= available;
            }

            // 左下角：固定在原胶囊位置（235 DIP），右侧到任务按钮区/托盘的距离为可用宽度。
            if (position is "corner")
            {
                var cornerX = barRect.Left + (int)Math.Round(FallbackLeftDip * scale);
                var limit = startEdge is int cornerStart ? cornerStart - Px(TrayGapDip, scale) : trayMax;
                if (cornerX >= minX && FitWidth(limit - cornerX))
                {
                    x = cornerX;
                    placed = true;
                }
            }

            // 任务栏左侧空位（Start/ReBar 左侧；Windows 11 居中任务栏的固有空白），
            // 放得下就绝不覆盖固定/运行中的任务按钮。
            if (!placed && position is not "tray" && startEdge is not null && startMax - startMin >= pad * 2 &&
                FitWidth(startMax - startMin))
            {
                x = Math.Max(startMin, startMax - totalWidth);
                placed = true;
            }

            if (!placed)
            {
                if (position is "start" or "corner")
                {
                    // 用户明确要求只在左侧/左下角：放不下就隐藏，宁可不显示也不覆盖任务按钮。
                    HideStrip();
                    return;
                }

                if (!FitWidth(trayMax - minX))
                {
                    HideStrip();
                    return;
                }

                x = Math.Max(minX, trayMax - totalWidth);
            }

            if (height <= 8)
            {
                HideStrip();
                return;
            }

            var y = barRect.Top;
            var overflowWidth = overflow > 0
                ? MeasureText(measureGraphics, $"+{overflow}", textFont)
                : 0;

            using var bitmap = new Bitmap(totalWidth, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // alpha=1 的近透明底色让整条都可被鼠标命中（alpha=0 会让点击穿透）。
                graphics.Clear(Color.FromArgb(1, 0, 0, 0));

                var foreground = light ? Hex(0x181818) : Hex(0xF5F5F5);
                var lowColor = light ? Hex(0xC41E28) : Hex(0xFF5A5A);
                var chargingColor = light ? Hex(0x0078D4) : Hex(0x60CDFF);
                var fullColor = light ? Hex(0x107C10) : Hex(0x6CCB5F);

                if (settings.TaskbarStripBackground)
                {
                    var inset = Px(PanelInsetDip, scale);
                    var panel = new Rectangle(
                        inset,
                        inset,
                        Math.Max(1, totalWidth - (inset * 2)),
                        Math.Max(1, height - (inset * 2)));
                    var panelColor = light ? Color.FromArgb(240, 0xF3, 0xF3, 0xF3) : Color.FromArgb(240, 0x20, 0x20, 0x20);
                    using var path = RoundedRect(panel, Px(PanelRadiusDip, scale));
                    using var panelBrush = new SolidBrush(panelColor);
                    graphics.FillPath(panelBrush, path);
                }

                using var centered = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                };
                using var textFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                    Trimming = StringTrimming.None,
                };

                var cursor = pad;
                for (var index = 0; index < shown; index++)
                {
                    var item = items[index];
                    var alpha = item.Asleep ? 120 : 255;
                    var textColor = WithAlpha(item.Low ? lowColor : foreground, alpha);

                    if (showIcon && iconFont is not null)
                    {
                        var iconColor = WithAlpha(DeviceColor(item.Device.Kind, light), alpha);
                        using var iconBrush = new SolidBrush(iconColor);
                        graphics.DrawString(item.Glyph, iconFont, iconBrush,
                            new RectangleF(cursor, 0, iconSize, height), centered);
                        cursor += iconSize + iconGap;
                    }

                    if (needText[index] && textWidths[index] > 0)
                    {
                        using var textBrush = new SolidBrush(textColor);
                        graphics.DrawString(item.Text, textFont, textBrush,
                            new RectangleF(cursor, 0, textWidths[index], height), textFormat);
                        cursor += textWidths[index];
                    }

                    if (statusFont is not null && item.StatusGlyph is not null)
                    {
                        var statusColor = WithAlpha(item.StatusColor(chargingColor, fullColor), alpha);
                        using var statusBrush = new SolidBrush(statusColor);
                        cursor += statusGap;
                        graphics.DrawString(item.StatusGlyph, statusFont, statusBrush,
                            new RectangleF(cursor, 0, statusWidth, height), centered);
                        cursor += statusWidth;
                    }

                    if (index < shown - 1)
                    {
                        cursor += deviceGap;
                    }
                }

                if (shown > 0 && overflow > 0)
                {
                    cursor += deviceGap;
                    using var overflowBrush = new SolidBrush(WithAlpha(foreground, 170));
                    graphics.DrawString($"+{overflow}", textFont, overflowBrush,
                        new RectangleF(cursor, 0, overflowWidth, height), textFormat);
                }
            }

            UpdateLayered(bitmap, x, y);
        }
        finally
        {
            textFont.Dispose();
            iconFont?.Dispose();
            statusFont?.Dispose();
        }
    }

    private static List<RenderItem> BuildRenderItems(IReadOnlyList<BatteryDeviceSnapshot> devices, bool showIcon)
    {
        var count = Math.Min(devices.Count, MaxVisibleDevices);
        var items = new List<RenderItem>(count);
        for (var index = 0; index < count; index++)
        {
            var device = devices[index];
            var text = FormatItemText(device, out var charging, out var full, out var low);
            items.Add(new RenderItem(
                device,
                showIcon ? GlyphFor(device.Kind) : "",
                text,
                charging ? "\uE945" : full ? "\uE73E" : null,
                charging,
                full,
                low,
                device.IsAsleep));
        }

        return items;
    }

    /// <summary>
    /// 任务栏条文本：耳机若有双耳独立电量，显示「左 xx% 右 yy%」；
    /// 只有聚合电量（系统 BLE Battery Service）时仍显示单值，避免把同一数字假装成左右耳。
    /// </summary>
    private static string FormatItemText(
        BatteryDeviceSnapshot device,
        out bool charging,
        out bool full,
        out bool low)
    {
        charging = device.Charging == ChargingState.Charging;
        full = IsFullyCharged(device.Charging, device.Percent);
        low = IsLow(device.Percent, device.Charging);

        if (device.Kind == BatteryDeviceKind.Headset &&
            TryGetEarBattery(device, out var left, out var right, out var leftCharging, out var rightCharging))
        {
            charging = leftCharging || rightCharging;
            full = !charging && left >= 100 && right >= 100;
            low = IsLow(left, leftCharging ? ChargingState.Charging : ChargingState.Unknown) ||
                  IsLow(right, rightCharging ? ChargingState.Charging : ChargingState.Unknown);
            return $"左{left}% 右{right}%";
        }

        return device.Percent is int percent ? $"{percent}%" : "?";
    }

    private static bool TryGetEarBattery(
        BatteryDeviceSnapshot device,
        out int left,
        out int right,
        out bool leftCharging,
        out bool rightCharging)
    {
        left = 0;
        right = 0;
        leftCharging = false;
        rightCharging = false;
        if (device.Components is not { Count: > 0 } components)
        {
            return false;
        }

        var hasLeft = false;
        var hasRight = false;
        foreach (var component in components)
        {
            if (component.Percent is not int value)
            {
                continue;
            }

            if (component.Key is "left" && !hasLeft)
            {
                left = Math.Clamp(value, 0, 100);
                leftCharging = component.Charging == ChargingState.Charging;
                hasLeft = true;
            }
            else if (component.Key is "right" && !hasRight)
            {
                right = Math.Clamp(value, 0, 100);
                rightCharging = component.Charging == ChargingState.Charging;
                hasRight = true;
            }
        }

        return hasLeft && hasRight;
    }

    private static int ItemWidth(
        RenderItem item,
        int textWidth,
        int iconSize,
        int iconGap,
        int statusWidth,
        int statusGap)
    {
        var width = textWidth;
        if (item.Glyph.Length > 0)
        {
            width += iconSize + iconGap;
        }

        if (item.StatusGlyph is not null)
        {
            width += statusGap + statusWidth;
        }

        return width;
    }

    private int MeasureText(Graphics graphics, string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var size = graphics.MeasureString(text, font, new PointF(0, 0), _typographic);
        return (int)Math.Ceiling(size.Width) + 2;
    }

    private void UpdateLayered(Bitmap bitmap, int screenX, int screenY)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            AppLog.Warn("taskbar strip: GetDC failed");
            return;
        }

        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        var selected = false;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                AppLog.Warn("taskbar strip: CreateCompatibleDC failed");
                return;
            }

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };

            dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero)
            {
                AppLog.Warn($"taskbar strip: CreateDIBSection failed err={Marshal.GetLastWin32Error()}");
                return;
            }

            previous = SelectObject(memoryDc, dib);
            selected = true;
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[data.Stride];
                for (var line = 0; line < height; line++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, line * data.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(bits, line * width * 4), width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var destination = new POINT(screenX, screenY);
            if (_taskbar != IntPtr.Zero)
            {
                ScreenToClient(_taskbar, ref destination);
            }

            var size = new SIZE(width, height);
            var source = new POINT(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            if (UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size, memoryDc, ref source,
                    0, ref blend, ULW_ALPHA))
            {
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                _visible = true;
            }
            else
            {
                AppLog.Warn($"taskbar strip: UpdateLayeredWindow failed err={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (memoryDc != IntPtr.Zero)
            {
                if (selected && previous != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previous);
                }

                if (dib != IntPtr.Zero)
                {
                    DeleteObject(dib);
                }

                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        if (bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static int Px(double dip, double scale) => (int)Math.Round(dip * scale);

    private static double TextDip(AppSettings settings) => settings.TaskbarStripFontSize switch
    {
        "small" => TextDipSmall,
        "large" => TextDipLarge,
        _ => TextDipMedium,
    };

    private string? ResolveIconFace()
    {
        if (!_iconFaceResolved)
        {
            _iconFaceResolved = true;
            _iconFace = ResolveFace("Segoe Fluent Icons", "Segoe MDL2 Assets");
            if (_iconFace is null)
            {
                AppLog.Warn("taskbar strip: no icon font available, falling back to text");
            }
        }

        return _iconFace;
    }

    private string ResolveTextFace()
    {
        if (!_textFaceResolved)
        {
            _textFaceResolved = true;
            _textFace = ResolveFace("Segoe UI Variable Text", "Segoe UI") ?? "Segoe UI";
        }

        return _textFace ?? "Segoe UI";
    }

    /// <summary>
    /// 用 GDI 实际创建的字体名做校验：GDI/GDI+ 对不存在的字体会静默替换成别的字形（方框），
    /// 所以必须让 GDI 告诉真正拿到的 face 名。
    /// </summary>
    private static string? ResolveFace(params string[] candidates)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(64);
            foreach (var candidate in candidates)
            {
                var hfont = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0, candidate);
                if (hfont == IntPtr.Zero)
                {
                    continue;
                }

                var previous = SelectObject(screenDc, hfont);
                try
                {
                    buffer.Clear();
                    if (GetTextFaceW(screenDc, buffer.Capacity, buffer) > 0 &&
                        string.Equals(buffer.ToString(), candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
                finally
                {
                    SelectObject(screenDc, previous);
                    DeleteObject(hfont);
                }
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return null;
    }

    private static string GlyphFor(BatteryDeviceKind kind) => kind switch
    {
        BatteryDeviceKind.Headset => "\uE7F6",
        BatteryDeviceKind.Mouse => "\uE962",
        BatteryDeviceKind.Remote => "\uE7F4",
        BatteryDeviceKind.Keyboard => "\uE765",
        BatteryDeviceKind.Gamepad => "\uE7FC",
        BatteryDeviceKind.Speaker => "\uE767",
        _ => "\uE7C4",
    };

    private static Color DeviceColor(BatteryDeviceKind kind, bool light) => kind switch
    {
        BatteryDeviceKind.Headset => Hex(light ? 0x5B4BC4 : 0x8F82E8),
        BatteryDeviceKind.Mouse => Hex(light ? 0xA66B22 : 0xD9A05B),
        BatteryDeviceKind.Remote => Hex(light ? 0x1F6F8C : 0x5FB3D1),
        _ => Hex(light ? 0x6B7380 : 0x9AA3AD),
    };

    private static Color Hex(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static Color WithAlpha(Color color, int alpha) => Color.FromArgb(alpha, color);

    private static bool IsLow(int? percent, ChargingState charging) =>
        percent is int value && value <= 15 && charging is ChargingState.Unknown or ChargingState.Discharging;

    private static bool IsFullyCharged(ChargingState charging, int? percent) =>
        charging == ChargingState.Full ||
        (charging == ChargingState.Charging && percent == 100);

    private static bool IsPrimaryKind(BatteryDeviceKind kind) =>
        kind is BatteryDeviceKind.Headset or BatteryDeviceKind.Mouse or BatteryDeviceKind.Remote;

    private static int KindOrder(BatteryDeviceKind kind) => kind switch
    {
        BatteryDeviceKind.Headset => 0,
        BatteryDeviceKind.Mouse => 1,
        BatteryDeviceKind.Remote => 2,
        BatteryDeviceKind.Keyboard => 3,
        BatteryDeviceKind.Gamepad => 4,
        BatteryDeviceKind.Speaker => 5,
        _ => 6,
    };

    // ------------------------------------------------------------------ 主题 / 全屏

    private static bool IsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                "HKEY_CURRENT_USER\\" + ThemePersonalizeKey,
                "SystemUsesLightTheme",
                0);
            return value is int light && light != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFullscreenForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsIconic(foreground))
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var className = new StringBuilder(64);
        GetClassNameW(foreground, className, className.Capacity);
        if (className.ToString() is "Shell_TrayWnd" or "Progman" or "WorkerW")
        {
            return false;
        }

        if (SHQueryUserNotificationState(out var state) == 0 &&
            state is 2 or 3 or 4 or 7)
        {
            return true;
        }

        var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        return rect.Left <= info.rcMonitor.Left + 2 &&
               rect.Top <= info.rcMonitor.Top + 2 &&
               rect.Right >= info.rcMonitor.Right - 2 &&
               rect.Bottom >= info.rcMonitor.Bottom - 2;
    }

    // ------------------------------------------------------------------ 菜单 / 刷新

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenuW(menu, MF_STRING, (UIntPtr)1, "打开外设中枢");
        AppendMenuW(menu, MF_STRING, (UIntPtr)2, "立即刷新");
        AppendMenuW(menu, MF_STRING, (UIntPtr)3, "任务栏显示设置");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)4, "退出");

        GetCursorPos(out var point);
        var owner = _host != IntPtr.Zero ? _host : _hwnd;
        SetForegroundWindow(owner);
        var command = (uint)TrackPopupMenu(menu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, owner, IntPtr.Zero);
        PostMessageW(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case 1:
                _openMainWindow();
                break;
            case 2:
                RefreshNow();
                break;
            case 3:
                _openSettings();
                break;
            case 4:
                _exitApplication();
                break;
        }
    }

    private async void RefreshNow()
    {
        try
        {
            await _service.RefreshMouseAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("taskbar strip: refresh failed: " + ex.Message);
        }

        if (_disposed)
        {
            return;
        }

        ScheduleUpdate();
    }

    private void OnTaskbarCreated()
    {
        var now = Environment.TickCount64;
        if (now - _lastRebuildTick < RebuildCooldownTicks)
        {
            return;
        }

        _lastRebuildTick = now;
        AppLog.Info("taskbar strip rebuilding after TaskbarCreated");
        // 广播回调里要尽快返回（Explorer 用带超时的广播），重建交给下一帧。
        DestroyStrip();
        _taskbar = IntPtr.Zero;
        ScheduleUpdate();
    }

    private void OnEnvironmentChanged() => ScheduleUpdate();

    /// <summary>清签名并在 UI 队列下一帧重算（避免在系统广播回调里同步重绘）。</summary>
    private void ScheduleUpdate()
    {
        if (_disposed)
        {
            return;
        }

        _signature = "";
        _dispatcher.TryEnqueue(Update);
    }

    // ------------------------------------------------------------------ WndProc

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is null || self._disposed)
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        if (hwnd == self._host && message != 0 && message == self._taskbarCreatedMessage)
        {
            self.OnTaskbarCreated();
            return IntPtr.Zero;
        }

        if (hwnd == self._host)
        {
            switch (message)
            {
                case WM_SETTINGCHANGE:
                {
                    var area = lParam == IntPtr.Zero ? "" : Marshal.PtrToStringUni(lParam) ?? "";
                    if (area.Length == 0 || area == "ImmersiveColorSet")
                    {
                        self.OnEnvironmentChanged();
                    }

                    return IntPtr.Zero;
                }

                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    self.OnEnvironmentChanged();
                    return IntPtr.Zero;
            }
        }

        if (hwnd == self._hwnd)
        {
            switch (message)
            {
                case WM_MOUSEACTIVATE:
                    return (IntPtr)MA_NOACTIVATE;
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    self._openMainWindow();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    self.ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // ------------------------------------------------------------------ 数据结构

    private sealed record RenderItem(
        BatteryDeviceSnapshot Device,
        string Glyph,
        string Text,
        string? StatusGlyph,
        bool Charging,
        bool Full,
        bool Low,
        bool Asleep)
    {
        public Color StatusColor(Color charging, Color full) => Charging ? charging : full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Width, Height;

        public SIZE(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TOOLINFOW
    {
        public uint cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public IntPtr uId;
        public RECT rect;
        public IntPtr hinst;
        public IntPtr lpszText;
        public IntPtr lParam;
        public IntPtr lpReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------ P/Invoke

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageToolInfo(IntPtr hwnd, uint message, IntPtr wParam, ref TOOLINFOW lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessagePointer(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc,
        ref POINT destination, ref SIZE size, IntPtr sourceDc, ref POINT source,
        uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("UxTheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder faceName);
}
