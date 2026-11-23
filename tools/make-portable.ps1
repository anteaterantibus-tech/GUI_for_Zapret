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

$app = Join-Path $root "src\Zapret.App\Zapret.App.csproj"
$dist = Join-Path $root "dist"
$outDir = Join-Path $root "release"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "ZapretControl*" }
if ($running) {
    Write-Host "[ERROR] Zapret Control is running. Close it (tray -> Exit) and run the build again." -ForegroundColor Red
    Write-Host "        Running: " + (($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ", ")
    exit 1
}

# Release rule: only the current version stays in release\, older versions move to
# release\backup\<version>\ and only the two newest backups are kept.
& (Join-Path $PSScriptRoot "cleanup-release.ps1") -ReleaseDir $outDir -CurrentVersion $Version

New-Item -ItemType Directory -Path $dist -Force | Out-Null
Get-ChildItem -LiteralPath $dist -Filter "ZapretControl*.exe" -ErrorAction SilentlyContinue | Remove-Item -Force

# The portable exe is the same binary as the one shipped in the installer
# package; the word Portable in the file name is what makes the app show
# "Zapret Control Portable" in the window title and tray tooltip.
Write-Host "Publishing self-contained build..."
dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$distPlain = Join-Path $dist "ZapretControl-$Version.exe"
Move-Item -LiteralPath (Join-Path $dist "ZapretControl.exe") -Destination $distPlain -Force

$distPortable = Join-Path $dist "ZapretControl-$Version-Portable.exe"
Copy-Item -LiteralPath $distPlain -Destination $distPortable -Force

$portable = Join-Path $outDir "ZapretControl-$Version-Portable.exe"
Copy-Item -LiteralPath $distPortable -Destination $portable -Force

# The in-app PDF button looks for a manual next to the executable.
$pdf = Get-ChildItem -LiteralPath (Join-Path $root "docs") -Filter *.pdf -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pdf) {
    Copy-Item -LiteralPath $pdf.FullName -Destination (Join-Path $dist $pdf.Name) -Force
    Copy-Item -LiteralPath $pdf.FullName -Destination (Join-Path $outDir $pdf.Name) -Force
}

$mb = [math]::Round((Get-Item -LiteralPath $portable).Length / 1MB, 1)
Write-Host "[OK] $portable ($mb MB)"

# A stable desktop shortcut: its name never contains the version, but it always
# points to the freshly built portable release exe.
if (-not $NoDesktopShortcut) {
    & (Join-Path $PSScriptRoot "update-shortcut.ps1") -ReleaseDir $outDir
}

Write-Host "[OK] $distPortable"
Write-Host "[OK] $distPlain"
