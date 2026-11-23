param(
    [string]$ReleaseDir = "",
    [string]$CurrentVersion = "",
    [int]$KeepBackups = 2
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $root "release"
}

if ([string]::IsNullOrWhiteSpace($CurrentVersion)) {
    $match = [regex]::Match((Get-Content -LiteralPath (Join-Path $root "src\Zapret.App\Zapret.App.csproj") -Raw), "<Version>([^<]+)</Version>")
    $CurrentVersion = if ($match.Success) { $match.Groups[1].Value } else { "0.0.0" }
}

function ConvertTo-VersionKey([string]$value) {
    $parts = $value -split '\.'
    $numbers = @(0, 0, 0)
    for ($i = 0; $i -lt [Math]::Min(3, $parts.Count); $i++) {
        $number = 0
        [void][int]::TryParse($parts[$i], [ref]$number)
        $numbers[$i] = $number
    }
    return [version]::new($numbers[0], $numbers[1], $numbers[2])
}

if (-not (Test-Path -LiteralPath $ReleaseDir)) {
    Write-Host "[SKIP] release folder not found: $ReleaseDir"
    exit 0
}

$backupRoot = Join-Path $ReleaseDir "backup"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

$pattern = '^ZapretControl-(?<version>\d+\.\d+\.\d+)'
$moved = 0

foreach ($item in Get-ChildItem -LiteralPath $ReleaseDir) {
    if ($item.Name -eq "backup") { continue }
    $match = [regex]::Match($item.Name, $pattern)
    if (-not $match.Success) { continue }

    $version = $match.Groups["version"].Value
    if ($version -eq $CurrentVersion) { continue }

    $versionDir = Join-Path $backupRoot $version
    New-Item -ItemType Directory -Path $versionDir -Force | Out-Null
    $destination = Join-Path $versionDir $item.Name

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }

    Move-Item -LiteralPath $item.FullName -Destination $destination
    Write-Host "[OK] Moved to backup: $($item.Name) -> backup\$version"
    $moved++
}

$oldBackups = Get-ChildItem -LiteralPath $backupRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (ConvertTo-VersionKey $_.Name) -lt (ConvertTo-VersionKey $CurrentVersion) } |
    Sort-Object { ConvertTo-VersionKey $_.Name } -Descending |
    Select-Object -Skip $KeepBackups

foreach ($old in $oldBackups) {
    Remove-Item -LiteralPath $old.FullName -Recurse -Force
    Write-Host "[OK] Removed old backup: backup\$($old.Name)"
}

Write-Host "[OK] Release cleanup: current=$CurrentVersion, moved=$moved, kept backups=$KeepBackups"
