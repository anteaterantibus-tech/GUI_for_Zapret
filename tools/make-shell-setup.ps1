param(
    [string]$Version = "",
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $match = [regex]::Match((Get-Content -LiteralPath (Join-Path $root "src\Zapret.App\Zapret.App.csproj") -Raw), "<Version>([^<]+)</Version>")
    $Version = if ($match.Success) { $match.Groups[1].Value } else { "1.0.0" }
}

$distExe = Join-Path $root "dist\ZapretControl-$Version.exe"
if (-not (Test-Path -LiteralPath $distExe)) {
    Write-Host "[ERROR] dist\ZapretControl-$Version.exe not found. Run make-portable.ps1 first." -ForegroundColor Red
    exit 1
}

$outDir = Join-Path $root "release"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$packageName = "ZapretControl-$Version-Shell"
$packageDir = Join-Path $outDir $packageName
$zipPath = Join-Path $outDir "$packageName.zip"

if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Copy-Item -LiteralPath $distExe -Destination (Join-Path $packageDir "ZapretControl.exe") -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination (Join-Path $packageDir "install.ps1") -Force

$pdf = Join-Path $root "docs\Руководство.pdf"
if (Test-Path -LiteralPath $pdf) {
    Copy-Item -LiteralPath $pdf -Destination (Join-Path $packageDir "Руководство.pdf") -Force
}

$installCmd = @"
@echo off
chcp 65001 >nul
title Zapret Control installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
"@
Set-Content -LiteralPath (Join-Path $packageDir "install.cmd") -Value $installCmd -Encoding ASCII

$uninstallCmd = @"
@echo off
chcp 65001 >nul
title Zapret Control uninstaller
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall
echo.
pause
"@
Set-Content -LiteralPath (Join-Path $packageDir "uninstall.cmd") -Value $uninstallCmd -Encoding ASCII

$readme = @"
Zapret Control $Version (только оболочка)
========================================

Это установщик только графической оболочки. Стратегии и сборка zapret в него не входят.

Zapret Control не является средством обхода. Это оболочка для проекта
zapret-discord-youtube (движок zapret от bol-van): своего механизма обхода,
VPN или прокси в приложении нет.

Установка
---------
1. Запустите install.cmd или ZapretControl-$Version-Setup-Shell.exe.
2. Приложение скопируется в %LOCALAPPDATA%\ZapretControl, появятся ярлыки.
3. Скачайте zapret-discord-youtube:
   https://github.com/Flowseal/zapret-discord-youtube/releases/latest
4. Распакуйте сборку в папку без кириллицы в пути.
5. Запустите Zapret Control и укажите папку сборки в «Настройках».

Если папки zapret нет, приложение при первом запуске само предложит скачать
последнюю сборку в %LOCALAPPDATA%\ZapretControl\zapret.

Можно и без установки: ZapretControl.exe работает как portable.

Удаление
--------
Запустите uninstall.cmd. Служба zapret и настройки в %APPDATA%\ZapretControl
не удаляются.
"@
Set-Content -LiteralPath (Join-Path $packageDir "README.txt") -Value $readme -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Write-Host "Compressing $packageName.zip ..."
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "[OK] Shell package: $packageDir"
Write-Host "[OK] Shell zip:     $zipPath"

$setupArgs = @{
    Version    = $Version
    Payload    = $zipPath
    OutputName = "ZapretControl-$Version-Setup-Shell.exe"
}
if ($NoDesktopShortcut) { $setupArgs.NoDesktopShortcut = $true }

& (Join-Path $PSScriptRoot "make-setup.ps1") @setupArgs
