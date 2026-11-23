param(
    [string]$Exe = "bin\Debug\net9.0-windows\ZapretControl.exe",
    [string]$Out = "..\..\..\docs\screenshot.png",
    [int]$WaitSeconds = 6,
    [string]$Page = "",
    [switch]$NoElevate,
    [int]$ProcessId = 0,
    [string[]]$AppArgs = @()
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public class WinEnum {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<IntPtr> WindowsOf(uint targetPid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Title(IntPtr hwnd) {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@

$started = $false
if ($ProcessId -gt 0) {
    $proc = Get-Process -Id $ProcessId -ErrorAction Stop
} else {
    $appArguments = @()
    if ($Page.Length -gt 0) { $appArguments += "--page=$Page" }
    if ($NoElevate) { $appArguments += "--no-elevate" }
    $appArguments += $AppArgs

    if ($appArguments.Count -gt 0) {
        $proc = Start-Process -FilePath $Exe -ArgumentList $appArguments -PassThru
    } else {
        $proc = Start-Process -FilePath $Exe -PassThru
    }
    $started = $true
}
Start-Sleep -Seconds $WaitSeconds
$proc.Refresh()

if ($proc.HasExited) {
    Write-Output "Process exited with code $($proc.ExitCode)"
    $log = Join-Path $env:TEMP "zapret-control-crash.txt"
    if (Test-Path $log) { Get-Content $log }
    exit 1
}

$windows = [WinEnum]::WindowsOf([uint32]$proc.Id)
Write-Output "PID=$($proc.Id) windows=$($windows.Count)"

$best = [IntPtr]::Zero
$bestArea = 0
foreach ($h in $windows) {
    $r = New-Object WinEnum+RECT
    [void][WinEnum]::GetWindowRect($h, [ref]$r)
    $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
    $title = [WinEnum]::Title($h)
    $visible = [WinEnum]::IsWindowVisible($h)
    Write-Output ("  hwnd={0} visible={1} size={2}x{3} title='{4}'" -f $h, $visible, ($r.Right-$r.Left), ($r.Bottom-$r.Top), $title)
    if ($visible -and $area -gt $bestArea) { $bestArea = $area; $best = $h }
}

if ($best -eq [IntPtr]::Zero) {
    Write-Output "No visible window"
    $proc.Kill()
    exit 1
}

[void][WinEnum]::ShowWindow($best, 5)
[void][WinEnum]::SetForegroundWindow($best)
Start-Sleep -Milliseconds 900

$r = New-Object WinEnum+RECT
[void][WinEnum]::GetWindowRect($best, [ref]$r)
$width = $r.Right - $r.Left
$height = $r.Bottom - $r.Top
Write-Output "Capturing $width x $height"

$bmp = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $graphics.GetHdc()
$ok = [WinEnum]::PrintWindow($best, $hdc, 2)
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()

$resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$dir = Split-Path -Parent $resolved
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$bmp.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Saved $resolved (PrintWindow=$ok)"

if ($started) {
    $proc.Kill()
    Write-Output "Killed"
}
