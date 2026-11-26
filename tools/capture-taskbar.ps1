# 截取任务栏区域（含通知区域）用于验收任务栏内嵌电量条。
# 用法： powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-taskbar.ps1 -OutFile <abs.png>
param(
    [Parameter(Mandatory = $true)][string]$OutFile
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeTaskbar {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@

$taskbar = [NativeTaskbar]::FindWindow("Shell_TrayWnd", $null)
if ($taskbar -eq [IntPtr]::Zero) { throw "taskbar not found" }

$rect = New-Object NativeTaskbar+RECT
if (-not [NativeTaskbar]::GetWindowRect($taskbar, [ref]$rect)) { throw "GetWindowRect failed" }

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $OutFile ($width x $height)"
