using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 功 能： 预览版任务栏贴片：右下角分层子窗口，只画已连接设备（耳机 L/R、鼠标、遥控器）。
/// 2026/10/19 15:35:00 xliu
/// 参考 VigilWatt TaskbarPillWindow 的三道锁：SetParent / WS_EX_LAYERED / UpdateLayeredWindow。
/// </summary>
internal sealed class PreviewTaskbarPill : IDisposable
{
    private const string ClassName = "DeviceCenterPreviewTaskbarPill";
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const int GWL_STYLE = -16;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint SW_HIDE = 0;
    private const uint SW_SHOWNOACTIVATE = 4;
    private const uint AC_SRC_OVER = 0;
    private const uint AC_SRC_ALPHA = 1;
    private const uint ULW_ALPHA = 2;

    private static readonly WndProcDelegate StaticProc = WndProc;
    private static bool _classRegistered;
    private static PreviewTaskbarPill? _current;

    private readonly IPeripheralService _service;
    private readonly Action _openMainWindow;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private string _signature = "";
    private bool _hiddenForFullscreen;
    private bool _disposed;

    public PreviewTaskbarPill(IPeripheralService service, Action openMainWindow)
    {
        _service = service;
        _openMainWindow = openMainWindow;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("任务栏贴片必须在 UI 线程创建。");
        _current = this;

        EnsureWindow();
        _service.Changed += OnServiceChanged;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _service.Changed -= OnServiceChanged;
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        _taskbar = IntPtr.Zero;
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    private void OnServiceChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(Update);

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            return;
        }

        RegisterClassOnce();
        var instance = GetModuleHandleW(null);
        _hwnd = CreateWindowExW(
            (uint)(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            ClassName,
            "设备电量",
            WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            AppLog.Warn($"taskbar pill create failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        AttachToTaskbar();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    private static void RegisterClassOnce()
    {
        if (_classRegistered)
        {
            return;
        }

        _classRegistered = true;
        var wc = new WNDCLASSW
        {
            style = 0x0002,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = ClassName,
        };
        RegisterClassW(ref wc);
    }

    private void AttachToTaskbar()
    {
        _taskbar = FindWindowW("Shell_TrayWnd", null);
        if (_taskbar == IntPtr.Zero)
        {
            AppLog.Warn("taskbar pill: Shell_TrayWnd not found");
            return;
        }

        SetParent(_hwnd, _taskbar);
        var style = GetWindowLongPtrW(_hwnd, GWL_STYLE).ToInt64();
        style &= ~(long)WS_POPUP;
        style |= WS_CHILD;
        SetWindowLongPtrW(_hwnd, GWL_STYLE, new IntPtr(style));
    }

    private void Update()
    {
        if (_disposed)
        {
            return;
        }

        if (_taskbar == IntPtr.Zero || !IsWindow(_taskbar) || GetParent(_hwnd) != _taskbar)
        {
            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                EnsureWindow();
            }
            else
            {
                AttachToTaskbar();
            }

            if (_taskbar == IntPtr.Zero)
            {
                return;
            }
        }

        if (!GetWindowRect(_taskbar, out var taskbarRect))
        {
            return;
        }

        if (IsFullscreenForeground())
        {
            if (!_hiddenForFullscreen)
            {
                ShowWindow(_hwnd, SW_HIDE);
                _hiddenForFullscreen = true;
                _signature = "";
            }

            return;
        }

        if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            _signature = "";
        }

        var taskbar = new RECT(taskbarRect.Left, taskbarRect.Top, taskbarRect.Right, taskbarRect.Bottom);
        var items = BuildItems();
        var scale = Math.Max(1.0, GetDpiForWindow(_taskbar) / 96.0);
        var signature = string.Join("|", items.Select(i => $"{i.Glyph}{i.Text}{i.Percent}{i.Charging}"))
            + $"|{taskbarRect.Left},{taskbarRect.Top},{taskbarRect.Right},{taskbarRect.Bottom}|{scale:0.0}";

        if (items.Count == 0 || Math.Min(taskbar.Width, taskbar.Height) <= 4)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _signature = signature;
            return;
        }

        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        Render(items, taskbar, scale);
    }

    private List<PillItem> BuildItems()
    {
        var items = new List<PillItem>();

        var headset = _service.GetSnapshot(DeviceKind.Headset);
        if (headset.IsConnected)
        {
            var left = headset.Components.FirstOrDefault(c => c.Key == "left")?.Percent;
            var right = headset.Components.FirstOrDefault(c => c.Key == "right")?.Percent;
            var text = left is null && right is null
                ? Format(headset.BatteryPercent)
                : $"L {Format(left)}  R {Format(right)}";
            var percent = Min(left, right) ?? headset.BatteryPercent;
            items.Add(new PillItem("", text, percent, headset.Charging == ChargingState.Charging));
        }

        var mouse = _service.GetSnapshot(DeviceKind.Mouse);
        if (mouse.IsConnected)
        {
            items.Add(new PillItem("", Format(mouse.BatteryPercent), mouse.BatteryPercent,
                mouse.Charging == ChargingState.Charging));
        }

        var remote = _service.GetSnapshot(DeviceKind.Remote);
        if (remote.IsConnected)
        {
            items.Add(new PillItem("", Format(remote.BatteryPercent), remote.BatteryPercent,
                remote.Charging == ChargingState.Charging));
        }

        return items;
    }

    private void Render(List<PillItem> items, RECT taskbar, double scale)
    {
        var pad = (int)Math.Round(8 * scale);
        var gap = (int)Math.Round(6 * scale);
        var iconWidth = (int)Math.Round(14 * scale);
        var textGap = (int)Math.Round(4 * scale);
        var boltWidth = (int)Math.Round(7 * scale);
        var height = Math.Min((int)Math.Round(35 * scale), taskbar.Height - (int)Math.Round(6 * scale));
        height = Math.Max(height, (int)Math.Round(28 * scale));

        using var probe = new Bitmap(1, 1);
        using var probeGraphics = Graphics.FromImage(probe);
        using var iconFont = CreateIconFont((float)(13 * scale));
        using var textFont = new Font("Segoe UI", (float)(11 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        var textWidths = items.Select(i => probeGraphics.MeasureString(i.Text, textFont).Width).ToArray();

        var width = pad * 2;
        for (var i = 0; i < items.Count; i++)
        {
            width += iconWidth + textGap + (int)Math.Ceiling(textWidths[i]);
            if (items[i].Charging)
            {
                width += boltWidth;
            }

            if (i < items.Count - 1)
            {
                width += gap * 2 + 1;
            }
        }

        width = Math.Min(width, Math.Max((int)(taskbar.Width * 0.55), 80));
        var tray = FindWindowExW(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var anchorTop = taskbar.Bottom;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect))
        {
            anchorTop = trayRect.Top;
        }

        int x;
        int y;
        if (taskbar.Width >= taskbar.Height)
        {
            // 与耳机程序一致：左下角、任务栏左边缘向右约 235 DIP。
            x = taskbar.Left + (int)Math.Round(235 * scale);
            x = Math.Clamp(x, taskbar.Left + 4, Math.Max(taskbar.Left + 4, taskbar.Right - width - 4));
            y = taskbar.Top + (taskbar.Height - height) / 2;
        }
        else
        {
            x = taskbar.Left + (taskbar.Width - width) / 2;
            y = Math.Clamp(anchorTop - height - gap, taskbar.Top + 4, Math.Max(taskbar.Top + 4, taskbar.Bottom - height - 4));
        }

        var dark = !IsLightTheme();
        var background = dark ? Color.FromArgb(236, 30, 30, 30) : Color.FromArgb(246, 250, 250, 250);
        var border = dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);
        var foreground = dark ? Color.FromArgb(255, 245, 245, 245) : Color.FromArgb(255, 24, 24, 24);

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using var path = RoundedRect(new Rectangle(0, 0, width - 1, height - 1), (int)Math.Round(10 * scale));
            using var backgroundBrush = new SolidBrush(background);
            using var borderPen = new Pen(border, 1);
            graphics.FillPath(backgroundBrush, path);
            graphics.DrawPath(borderPen, path);

            var cursor = pad;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var color = BatteryColor(item.Percent, foreground);
                using var iconBrush = new SolidBrush(color);
                using var textBrush = new SolidBrush(color);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                };

                graphics.DrawString(item.Glyph, iconFont, iconBrush,
                    new RectangleF(cursor, 0, iconWidth, height), format);
                cursor += iconWidth + textGap;
                graphics.DrawString(item.Text, textFont, textBrush,
                    new RectangleF(cursor, 0, (int)Math.Ceiling(textWidths[i]) + 2, height), format);
                cursor += (int)Math.Ceiling(textWidths[i]) + 2;

                if (item.Charging)
                {
                    DrawBolt(graphics, cursor, height, scale, Color.FromArgb(255, 10, 132, 255));
                    cursor += boltWidth;
                }

                if (i < items.Count - 1)
                {
                    cursor += gap;
                    using var separatorPen = new Pen(border, 1);
                    graphics.DrawLine(separatorPen, cursor, height / 2 - 7 * (float)scale,
                        cursor, height / 2 + 7 * (float)scale);
                    cursor += 1 + gap;
                }
            }
        }

        UpdateLayered(bitmap, x, y);
    }

    private void UpdateLayered(Bitmap bitmap, int x, int y)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
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

        var dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var previous = SelectObject(memoryDc, dib);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
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

        var destination = new POINT(x, y);
        ScreenToClient(_taskbar, ref destination);
        var size = new SIZE(width, height);
        var source = new POINT(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = (byte)AC_SRC_ALPHA,
        };

        UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size, memoryDc, ref source,
            0, ref blend, ULW_ALPHA);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

        SelectObject(memoryDc, previous);
        DeleteObject(dib);
        DeleteDC(memoryDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static void DrawBolt(Graphics graphics, int x, int height, double scale, Color color)
    {
        var top = height / 2f - 5f * (float)scale;
        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(x + 2f * (float)scale, top),
            new PointF(x + 5f * (float)scale, top),
            new PointF(x + 3f * (float)scale, top + 4f * (float)scale),
            new PointF(x + 6f * (float)scale, top + 4f * (float)scale),
            new PointF(x + 1f * (float)scale, top + 10f * (float)scale),
            new PointF(x + 2.5f * (float)scale, top + 5.5f * (float)scale),
            new PointF(x, top + 5.5f * (float)scale),
        });
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
        path.Dispose();
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Font CreateIconFont(float size)
    {
        try
        {
            return new Font("Segoe Fluent Icons", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }

    private static Color BatteryColor(int? percent, Color fallback) => percent switch
    {
        <= 15 => Color.FromArgb(255, 226, 68, 92),
        <= 35 => Color.FromArgb(255, 250, 170, 23),
        _ => fallback,
    };

    private static bool IsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int light && light != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Format(int? percent) => percent is int value ? $"{value}%" : "--";

    private static int? Min(int? left, int? right) =>
        left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);

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

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is not null)
        {
            if (message is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                self._openMainWindow();
                return IntPtr.Zero;
            }

            if (message is WM_DPICHANGED or WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
            {
                self._signature = "";
                self.Update();
                return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private sealed record PillItem(string Glyph, string Text, int? Percent, bool Charging);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

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

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

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

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc,
        ref POINT destination, ref SIZE size, IntPtr sourceDc, ref POINT source,
        uint colorKey, ref BLENDFUNCTION blend, uint flags);
}
