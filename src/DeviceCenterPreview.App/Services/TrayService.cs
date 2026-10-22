using System.Runtime.InteropServices;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 功 能： 系统托盘图标：双击打开主窗口，右键菜单打开/退出；关闭窗口后常驻。
/// 2026/10/19 16:20:00 xliu
/// </summary>
internal sealed class TrayService : IDisposable
{
    private const string ClassName = "DeviceCenterTrayHost";
    private const uint WM_TRAY = 0x8001;
    private const uint WM_NULL = 0;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint NIIF_WARNING = 0x2;
    private const uint NIIF_ERROR = 0x3;
    private const uint MF_STRING = 0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly WndProcDelegate StaticProc = WndProc;
    private static bool _classRegistered;
    private static TrayService? _current;

    private readonly Action _open;
    private readonly Action _exit;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _disposed;

    public TrayService(Action open, Action exit)
    {
        _open = open;
        _exit = exit;
        _current = this;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PeripheralHub.ico");
        if (File.Exists(iconPath))
        {
            // 托盘图标跟随系统 DPI，而不是固定 16px；高 DPI 下固定 16px 会被系统放大成糊图。
            var iconSize = Math.Max(16, GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()));
            _icon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, iconSize, iconSize, LR_LOADFROMFILE);
        }

        RegisterClassOnce();
        _hwnd = CreateWindowExW(0, ClassName, "DeviceCenterTray", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            AppLog.Warn($"tray window create failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        var data = CreateIconData();
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            AppLog.Warn($"tray icon add failed err={Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            var data = CreateIconData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    private NOTIFYICONDATAW CreateIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon != IntPtr.Zero ? _icon : LoadIconW(IntPtr.Zero, (IntPtr)32512),
        szTip = "薄荷外设",
    };

    private static void RegisterClassOnce()
    {
        if (_classRegistered)
        {
            return;
        }

        _classRegistered = true;
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        RegisterClassW(ref wc);
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateIconData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= max ? value : value[..max];
    }

    private void ShowMenu()
    {
        if (!GetCursorPos(out var point))
        {
            point = default;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenuW(menu, MF_STRING, 1, "打开薄荷外设");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, 2, "退出薄荷外设");
        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        if (command == 1)
        {
            _open();
        }
        else if (command == 2)
        {
            _exit();
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is not null && message == WM_TRAY)
        {
            var action = (uint)(lParam.ToInt64() & 0xFFFF);
            if (action is WM_LBUTTONDBLCLK or WM_LBUTTONUP)
            {
                self._open();
                return IntPtr.Zero;
            }

            if (action == WM_RBUTTONUP)
            {
                self.ShowMenu();
                return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type,
        int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
