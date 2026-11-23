param(
    [string]$Version = "",
    [string]$Payload = "",
    [string]$OutputName = "",
    [switch]$SkipPublish,
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $match = [regex]::Match((Get-Content -LiteralPath (Join-Path $root "src\Zapret.App\Zapret.App.csproj") -Raw), "<Version>([^<]+)</Version>")
    $Version = if ($match.Success) { $match.Groups[1].Value } else { "1.0.0" }
}

$payload = if (-not [string]::IsNullOrWhiteSpace($Payload)) { $Payload } else { Join-Path $root "release\ZapretControl-$Version.zip" }
if (-not (Test-Path -LiteralPath $payload)) {
    Write-Host "Package zip not found, building it first..."
    & (Join-Path $PSScriptRoot "make-release.ps1") -Version $Version
}

$buildDir = Join-Path $root "setup\build"
$stub = Join-Path $buildDir "ZapretControlSetup.exe"
$output = if (-not [string]::IsNullOrWhiteSpace($OutputName)) {
    Join-Path $root "release\$OutputName"
}
else {
    Join-Path $root "release\ZapretControl-$Version-Setup.exe"
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $buildDir) { Remove-Item -LiteralPath $buildDir -Recurse -Force }
    Write-Host "Publishing installer stub..."
    dotnet publish (Join-Path $root "src\Zapret.Setup\Zapret.Setup.csproj") -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true `
        -p:DebugType=None -p:DebugSymbols=false -o $buildDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if (-not (Test-Path -LiteralPath $stub)) { throw "Installer stub not found: $stub" }
if (-not (Test-Path -LiteralPath $payload)) { throw "Payload not found: $payload" }

Write-Host "Combining stub and payload..."
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }

$out = [System.IO.File]::Create($output)
try {
    $stubStream = [System.IO.File]::OpenRead($stub)
    try { $stubStream.CopyTo($out) } finally { $stubStream.Dispose() }

    $payloadStream = [System.IO.File]::OpenRead($payload)
    try { $payloadStream.CopyTo($out) } finally { $payloadStream.Dispose() }

    $writer = New-Object System.IO.BinaryWriter($out)
    $writer.Write([Int64](Get-Item -LiteralPath $payload).Length)
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("ZCSPAY01"))
    $writer.Flush()
}
finally {
    $out.Dispose()
}

$stubMb = [math]::Round((Get-Item -LiteralPath $stub).Length / 1MB, 1)
$setupMb = [math]::Round((Get-Item -LiteralPath $output).Length / 1MB, 1)

Write-Host ""
Write-Host "[OK] Installer stub: $stub ($stubMb MB)"
Write-Host "[OK] Setup exe:      $output ($setupMb MB)"
Write-Host "     Single file, contains the app and the payload."

if (-not $NoDesktopShortcut) {
    & (Join-Path $PSScriptRoot "update-shortcut.ps1") -ReleaseDir (Join-Path $root "release")
}

& (Join-Path $PSScriptRoot "cleanup-release.ps1") -ReleaseDir (Join-Path $root "release") -CurrentVersion $Version
