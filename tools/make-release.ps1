param(
    [string]$Version = "",
    [string]$OutputDir = "",
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Join-Path $root "src\Zapret.App\Zapret.App.csproj"
    $match = [regex]::Match((Get-Content -LiteralPath $csproj -Raw), "<Version>([^<]+)</Version>")
    $Version = if ($match.Success) { $match.Groups[1].Value } else { "1.0.0" }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "release"
}

$distDir = Join-Path $root "dist"
# The installer package always uses the plain build. The portable build is a
# separate exe with the "Portable" title and file name.
$appExe = Join-Path $distDir "ZapretControl-$Version.exe"
if (-not (Test-Path -LiteralPath $appExe)) {
    $found = Get-ChildItem -LiteralPath $distDir -Filter "ZapretControl*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*-fx.exe" -and $_.Name -notlike "*Portable*" } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($found) { $appExe = $found.FullName }
}

$pdf = Join-Path $root "docs\Руководство.pdf"
$upstreamVersion = "1.10.4"
$packageName = "ZapretControl-$Version"
$packageDir = Join-Path $OutputDir $packageName
$zipPath = Join-Path $OutputDir "$packageName.zip"

if ($null -eq $appExe -or -not (Test-Path -LiteralPath $appExe)) {
    Write-Host "[ERROR] dist\ZapretControl-<version>.exe not found. Run make-portable.ps1 first." -ForegroundColor Red
    exit 1
}

if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Copy-Item -LiteralPath $appExe -Destination (Join-Path $packageDir "ZapretControl.exe") -Force
if (Test-Path -LiteralPath $pdf) {
    Copy-Item -LiteralPath $pdf -Destination (Join-Path $packageDir "Руководство.pdf") -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination (Join-Path $packageDir "install.ps1") -Force

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

# The package always ships a clean upstream build, never the developer folder
# with custom lists, loaded ipset and backup files.
$zapretDir = Join-Path $packageDir "zapret"
$cacheZip = Join-Path $env:TEMP "zapret-discord-youtube-$upstreamVersion.zip"

if (-not (Test-Path -LiteralPath $cacheZip)) {
    $url = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/$upstreamVersion/zapret-discord-youtube-$upstreamVersion.zip"
    Write-Host "Downloading zapret $upstreamVersion..."
    Invoke-WebRequest -Uri $url -OutFile $cacheZip -UseBasicParsing
}

$extractDir = Join-Path $env:TEMP "zapret-release-$upstreamVersion"
if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
Expand-Archive -LiteralPath $cacheZip -DestinationPath $extractDir -Force
$inner = Get-ChildItem -LiteralPath $extractDir -Directory | Select-Object -First 1
Copy-Item -LiteralPath $inner.FullName -Destination $zapretDir -Recurse -Force

foreach ($volatile in @("test results", "logs")) {
    $p = Join-Path $zapretDir "utils\$volatile"
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
Get-ChildItem -LiteralPath $zapretDir -Recurse -Directory -Filter "lists.backup-*" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
Get-ChildItem -LiteralPath $zapretDir -Recurse -File -Filter "*.bak" -ErrorAction SilentlyContinue |
    Remove-Item -Force

# A clean build ships without the user files the app creates on first run.
foreach ($userFile in @(
    (Join-Path $zapretDir "lists\list-general-user.txt"),
    (Join-Path $zapretDir "lists\list-exclude-user.txt"),
    (Join-Path $zapretDir "lists\ipset-exclude-user.txt"),
    (Join-Path $zapretDir "utils\game_filter.enabled")
)) {
    if (Test-Path -LiteralPath $userFile) { Remove-Item -LiteralPath $userFile -Force }
}

$readme = @"
Zapret Control $Version
========================

Что это
-------
Графическая оболочка для сборки zapret-discord-youtube $upstreamVersion.
Запускает обход Discord, YouTube и других сервисов без консоли и bat-файлов.

Как поставить
-------------
1. Распакуйте этот архив в любую папку (не запускайте из архива).
2. Запустите install.cmd.
   Приложение скопируется в %LOCALAPPDATA%\ZapretControl,
   рядом появится папка zapret со сборкой, на рабочем столе и в меню Пуск будут ярлыки.
3. Запустите Zapret Control с ярлыка и подтвердите запрос администратора.
4. В открывшейся инструкции нажмите «Подобрать», дождитесь результата,
   запустите лучшую стратегию и нажмите «В автозапуск».

Можно и без установки: запустить ZapretControl.exe прямо из этой папки.
Сборка zapret уже лежит рядом в папке zapret.

Подробная инструкция
--------------------
Руководство.pdf рядом с приложением. В приложении есть раздел «Инструкция».

Удаление
--------
Запустите uninstall.cmd. Служба zapret и настройки не удаляются:
их можно убрать в самом приложении и в %APPDATA%\ZapretControl.

Проверка файлов
---------------
ZapretControl.exe     приложение
zapret\               сборка zapret $upstreamVersion (bin, lists, utils, стратегии)
Руководство.pdf       подробное руководство
install.cmd           установка
uninstall.cmd         удаление
install.ps1           скрипт установки

Сборка zapret распространяется по лицензии MIT:
github.com/Flowseal/zapret-discord-youtube
"@
Set-Content -LiteralPath (Join-Path $packageDir "README.txt") -Value $readme -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Write-Host "Compressing $packageName.zip ..."
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
$folderMb = [math]::Round(((Get-ChildItem -LiteralPath $packageDir -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)

# The desktop shortcut must always point to the versioned portable release exe,
# not to the package folder and not to the plain installer build.
$releaseExe = Join-Path $OutputDir "ZapretControl-$Version-Portable.exe"
if (-not (Test-Path -LiteralPath $releaseExe)) {
    $distPortable = Join-Path $root "dist\ZapretControl-$Version-Portable.exe"
    if (Test-Path -LiteralPath $distPortable) {
        Copy-Item -LiteralPath $distPortable -Destination $releaseExe -Force
    }
    else {
        Write-Host "[WARN] Portable exe not found. Run make-portable.ps1 to build it." -ForegroundColor Yellow
    }
}
if (-not $NoDesktopShortcut) {
    & (Join-Path $PSScriptRoot "update-shortcut.ps1") -ReleaseDir $OutputDir
}

& (Join-Path $PSScriptRoot "cleanup-release.ps1") -ReleaseDir $OutputDir -CurrentVersion $Version

Write-Host ""
Write-Host "[OK] Package folder: $packageDir ($folderMb MB)"
Write-Host "[OK] Zip archive:    $zipPath ($zipMb MB)"
Write-Host "     Send the zip; the friend unpacks it and runs install.cmd."
