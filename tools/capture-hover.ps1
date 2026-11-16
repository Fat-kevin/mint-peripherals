param([string]$OutFile)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HoverCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
}
"@

$proc = Get-Process -Name DeviceCenterPreview.App -ErrorAction Stop | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
$shell = New-Object -ComObject WScript.Shell
[HoverCap]::ShowWindow($hwnd, 9) | Out-Null
[HoverCap]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, 0, 0, 0x0001 -bor 0x0040) | Out-Null
$shell.AppActivate($proc.Id) | Out-Null
[HoverCap]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "耳机工作区")
$node = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($node) {
    $p = $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    try { $p.Invoke() } catch { }
}
Start-Sleep -Milliseconds 1200

$rect = New-Object HoverCap+RECT
$hr = [HoverCap]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16)
if ($hr -ne 0) { throw "rect failed" }

# 选中态「深度」瓦片中心（窗口内坐标）
$wx = $rect.Left + 790
$wy = $rect.Top + 325
[HoverCap]::SetCursorPos($wx, $wy) | Out-Null
Start-Sleep -Milliseconds 700

$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
$cur = New-Object HoverCap+POINT
[HoverCap]::GetCursorPos([ref]$cur) | Out-Null
Write-Output "saved $OutFile cursor=$($cur.X),$($cur.Y) window=$($rect.Left),$($rect.Top) size=$w x $h"


