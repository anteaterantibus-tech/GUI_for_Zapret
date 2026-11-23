param(
    [string]$Out = "..\src\Zapret.App\app.ico"
)

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = $size / 256.0
    $margin = [float](8 * $scale)
    $rect = New-Object System.Drawing.RectangleF($margin, $margin, [float]($size - 2 * $margin), [float]($size - 2 * $margin))
    $radius = [float](58 * $scale)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.Left, $rect.Top, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Top, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 72, 199, 116),
        [System.Drawing.Color]::FromArgb(255, 35, 132, 74))
    $g.FillPath($brush, $path)

    # subtle inner border
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 255, 255, 255), [float](6 * $scale))
    $g.DrawPath($pen, $path)

    # white "Z" cut by a horizontal gap: clear metaphor for packet splitting,
    # and a real glyph stays readable even at 16 px
    $family = $null
    foreach ($name in @("Segoe UI Black", "Arial Black", "Segoe UI")) {
        try { $family = New-Object System.Drawing.FontFamily($name); break } catch { }
    }
    if ($null -eq $family) { $family = [System.Drawing.FontFamily]::GenericSansSerif }

    $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
    $emSize = [float](212 * $scale)
    $fmt = [System.Drawing.StringFormat]::GenericDefault
    $glyph.AddString("Z", $family, [int][System.Drawing.FontStyle]::Regular, $emSize,
        (New-Object System.Drawing.PointF(0, 0)), $fmt)
    $b = $glyph.GetBounds()
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate([float](($size - $b.Width) / 2 - $b.X), [float](($size - $b.Height) / 2 - $b.Y))
    $glyph.Transform($m)

    $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(45, 8, 30, 15))
    $shadowMatrix = New-Object System.Drawing.Drawing2D.Matrix
    $shadowMatrix.Translate([float](3 * $scale), [float](4 * $scale))
    $shadowGlyph = $glyph.Clone()
    $shadowGlyph.Transform($shadowMatrix)
    $g.FillPath($shadowBrush, $shadowGlyph)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 247, 252, 248))
    $gapHalf = [float](7 * $scale)
    $mid = [float]($size / 2)

    $g.SetClip((New-Object System.Drawing.RectangleF(0, 0, $size, ($mid - $gapHalf))))
    $g.FillPath($white, $glyph)
    $g.SetClip((New-Object System.Drawing.RectangleF(0, ($mid + $gapHalf), $size, ($size - $mid - $gapHalf))))
    $g.FillPath($white, $glyph)
    $g.ResetClip()

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 128, 64, 48, 32, 16)
$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($s, $ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}

$resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$dir = Split-Path -Parent $resolved
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$fs = [System.IO.File]::Create($resolved)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type: icon
$bw.Write([UInt16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $size = $img[0]
    $data = $img[1]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)        # color count
    $bw.Write([byte]0)        # reserved
    $bw.Write([UInt16]1)      # planes
    $bw.Write([UInt16]32)     # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) {
    $bw.Write($img[1])
}
$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output "Wrote $resolved ($((Get-Item $resolved).Length) bytes, $($images.Count) sizes)"

# preview PNG for visual check
$preview = New-IconBitmap 256
$previewPath = Join-Path $dir "..\..\docs\icon-preview.png"
$previewPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($previewPath)
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Output "Preview: $previewPath"
