param(
    [string]$Source = "",
    [string]$InstallDir = "$env:LOCALAPPDATA\ZapretControl",
    [switch]$Uninstall,
    [switch]$NoDesktopShortcut,
    [switch]$NoZapret
)

$ErrorActionPreference = "Stop"

function Get-ShortcutPaths {
    $desktop = [Environment]::GetFolderPath("Desktop")
    $startMenu = [Environment]::GetFolderPath("Programs")
    return @{
        Desktop = Join-Path $desktop "Zapret Control.lnk"
        StartMenu = Join-Path $startMenu "Zapret Control.lnk"
    }
}

function Remove-Shortcuts {
    $target = Join-Path $InstallDir "ZapretControl.exe"
    $shell = New-Object -ComObject WScript.Shell
    foreach ($p in (Get-ShortcutPaths).Values) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $existing = $shell.CreateShortcut($p)
        $existingTarget = $existing.TargetPath
        if ($existingTarget -and ($existingTarget -eq $target -or $existingTarget.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase))) {
            Remove-Item -LiteralPath $p -Force
        }
        else {
            Write-Host "[OK] Shortcut kept (not ours): $p -> $existingTarget"
        }
    }
}

if ($Uninstall) {
    Remove-Shortcuts
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    Write-Host "[OK] Zapret Control removed from $InstallDir" -ForegroundColor Green
    Write-Host "     The zapret service and your settings were not touched."
    Write-Host "     Settings: $env:APPDATA\ZapretControl"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\dist\ZapretControl.exe")
    )
    $Source = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($Source)) {
        $Source = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "..\dist") -Filter "ZapretControl*.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike "*-fx.exe" } |
            Sort-Object Name -Descending |
            Select-Object -First 1 |
            ForEach-Object { $_.FullName }
    }

    if ([string]::IsNullOrWhiteSpace($Source)) {
        $Source = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "ZapretControl*.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike "*-fx.exe" } |
            Select-Object -First 1 |
            ForEach-Object { $_.FullName }
    }

    if ([string]::IsNullOrWhiteSpace($Source)) {
        $found = Get-ChildItem -Path (Join-Path $PSScriptRoot "..\src\Zapret.App\bin") -Recurse -Filter "ZapretControl.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $Source = $found.FullName }
    }
}

if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path -LiteralPath $Source)) {
    Write-Host "[ERROR] ZapretControl.exe not found." -ForegroundColor Red
    Write-Host "        Put this script next to ZapretControl.exe or pass -Source <path>."
    exit 1
}

$sourceFull = (Resolve-Path -LiteralPath $Source).Path
$sourceDir = Split-Path -Parent $sourceFull

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$targetExe = Join-Path $InstallDir "ZapretControl.exe"

try {
    Copy-Item -LiteralPath $sourceFull -Destination $targetExe -Force
}
catch {
    Write-Host "[ERROR] Cannot copy ZapretControl.exe. Close the running app and try again." -ForegroundColor Red
    Write-Host "        $($_.Exception.Message)"
    exit 1
}

# Detailed manual next to the app: the in-app button opens it.
$pdf = Get-ChildItem -LiteralPath $sourceDir -Filter *.pdf -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pdf) {
    Copy-Item -LiteralPath $pdf.FullName -Destination (Join-Path $InstallDir $pdf.Name) -Force
    Write-Host "[OK] Manual: $($pdf.Name)"
}

# The upstream zapret build, if the package ships one.
if (-not $NoZapret) {
    $zapretSource = @(
        (Join-Path $PSScriptRoot "zapret"),
        (Join-Path $PSScriptRoot "..\src\zapret")
    ) | Where-Object { Test-Path -LiteralPath (Join-Path $_ "bin\winws.exe") } | Select-Object -First 1

    if ($zapretSource) {
        $zapretTarget = Join-Path $InstallDir "zapret"
        if (Test-Path -LiteralPath (Join-Path $zapretTarget "bin\winws.exe")) {
            Write-Host "[OK] Existing zapret build kept: $zapretTarget"
        }
        else {
            Copy-Item -LiteralPath $zapretSource -Destination $zapretTarget -Recurse -Force
            Write-Host "[OK] zapret build: $zapretTarget"
        }
    }
}

$ws = New-Object -ComObject WScript.Shell
$shortcuts = Get-ShortcutPaths

$targets = @($shortcuts.StartMenu)
if (-not $NoDesktopShortcut) { $targets += $shortcuts.Desktop }

foreach ($lnk in $targets) {
    if ($lnk -eq $shortcuts.Desktop -and (Test-Path -LiteralPath $lnk)) {
        $existing = $ws.CreateShortcut($lnk)
        $existingTarget = $existing.TargetPath
        if ($existingTarget -and (Test-Path -LiteralPath $existingTarget) -and
            -not $existingTarget.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "[OK] Existing desktop shortcut kept: $lnk -> $existingTarget"
            continue
        }
    }

    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath = $targetExe
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation = "$targetExe,0"
    $sc.Description = "Zapret Control"
    $sc.Save()
    Write-Host "[OK] Shortcut: $lnk"
}

Write-Host ""
Write-Host "[OK] Installed to $InstallDir" -ForegroundColor Green
Write-Host "     Launch: $targetExe"
Write-Host "     The first launch asks for administrator rights, as required."
Write-Host "     Uninstall: uninstall.cmd"
