param(
    [string]$ProcessName = "DeviceCenterPreview.App",
    [string]$OutFile,
    [string]$InvokeName = "",
    [int]$WaitMs = 1200
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinCap2 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
for ($i = 0; $i -lt 30 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 300
    $proc.Refresh()
    $hwnd = $proc.MainWindowHandle
}
if ($hwnd -eq [IntPtr]::Zero) { throw "window handle not found" }

$shell = New-Object -ComObject WScript.Shell
[WinCap2]::ShowWindow($hwnd, 9) | Out-Null
[WinCap2]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30, 0, 0, 0x0001 -bor 0x0040) | Out-Null
$shell.AppActivate($proc.Id) | Out-Null
[WinCap2]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds $WaitMs

if ($InvokeName) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $InvokeName)
    $node = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $node) { throw "UIA node not found: $InvokeName" }
    try {
        $pattern = $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
    } catch {
        Write-Output "invoke-warning: $($_.Exception.Message)"
    }
    Start-Sleep -Milliseconds 1500
}

$rect = New-Object WinCap2+RECT
$hr = [WinCap2]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16)
if ($hr -ne 0) { [WinCap2]::GetWindowRect($hwnd, [ref]$rect) | Out-Null }
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "bad rect $w x $h" }

# 先试 PrintWindow（不受其它窗口遮挡影响），失败再回退到屏幕截图
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$pwOk = [WinCap2]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$blank = $false
if ($pwOk) {
    # 判断是否全黑/全白（PrintWindow 对合成窗口可能无效）
    $c1 = $bmp.GetPixel([int]($w / 2), [int]($h / 2))
    $c2 = $bmp.GetPixel(20, [int]($h / 2))
    $blank = ($c1.R -lt 6 -and $c1.G -lt 6 -and $c1.B -lt 6) -and ($c2.R -lt 6 -and $c2.G -lt 6 -and $c2.B -lt 6)
}
if (-not $pwOk -or $blank) {
    $bmp.Dispose()
    $g.Dispose()
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    Write-Output "printwindow-fallback"
}

$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output "saved $OutFile ($w x $h)"

