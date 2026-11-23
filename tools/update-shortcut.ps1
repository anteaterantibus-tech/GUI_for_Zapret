param(
    [string]$ReleaseDir = "",
    [string]$Name = "Zapret Control",
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"

if ($NoDesktopShortcut) {
    Write-Host "[SKIP] Desktop shortcut disabled"
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $root "release"
}

$exe = Get-ChildItem -LiteralPath $ReleaseDir -Filter "ZapretControl-*-Portable.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $exe) {
    # Older builds used a single versioned exe without the Portable suffix.
    $exe = Get-ChildItem -LiteralPath $ReleaseDir -Filter "ZapretControl-*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*-fx.exe" -and $_.Name -notlike "*Setup*" -and $_.Name -notlike "*Portable*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if ($null -eq $exe) {
    Write-Host "[ERROR] release\ZapretControl-<version>-Portable.exe not found, shortcut skipped." -ForegroundColor Red
    exit 1
}

$desktop = [Environment]::GetFolderPath("Desktop")
$link = Join-Path $desktop "$Name.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $exe.FullName
$shortcut.WorkingDirectory = $ReleaseDir
$shortcut.IconLocation = "$($exe.FullName),0"
$shortcut.Description = "Zapret Control"
$shortcut.Save()

Write-Host "[OK] Desktop shortcut: $link -> $($exe.FullName)"
