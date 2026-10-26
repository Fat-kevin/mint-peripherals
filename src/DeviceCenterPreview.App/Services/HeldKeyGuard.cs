using System.Runtime.InteropServices;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 遥控器语音热键由 sidecar 的 key host 用合成按键实现（默认 Ctrl+Win）。
/// sidecar 被强杀、崩溃或没走完释放路径时，这些修饰键可能留在系统里“按住”，
/// 用户之后每次按键都会变成 Win+Ctrl+X 系统快捷键（讲述人、屏幕键盘、虚拟桌面切换等）。
/// 这里在启动、停引擎、退出时补发一次 key-up 作为兜底；不对任何物理按键做映射。
/// </summary>
internal static class HeldKeyGuard
{
    private const uint KeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static readonly byte[] ModifierVirtualKeys =
    [
        0x11, // VK_CONTROL
        0xA2, // VK_LCONTROL
        0xA3, // VK_RCONTROL
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
        0x10, // VK_SHIFT
        0xA0, // VK_LSHIFT
        0xA1, // VK_RSHIFT
        0x12, // VK_MENU
        0xA4, // VK_LMENU
        0xA5, // VK_RMENU
    ];

    /// <summary>补发修饰键抬起。即使当前没有按键被按住，重复 key-up 也是无害的。</summary>
    public static void ReleaseModifiers(string reason)
    {
        try
        {
            foreach (var virtualKey in ModifierVirtualKeys)
            {
                keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
            }

            AppLog.Warn($"held-key guard released modifier keys ({reason})");
        }
        catch (Exception ex)
        {
            AppLog.Warn("held-key guard failed: " + ex.Message);
        }
    }
}
