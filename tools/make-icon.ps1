# Generates server/MyVpn.Server/app.ico (multi-resolution) - a blue rounded tile with a white
# shield and a blue keyhole. Run:  powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$outPath = Join-Path $PSScriptRoot '..\server\MyVpn.Server\app.ico'
$outPath = [System.IO.Path]::GetFullPath($outPath)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-ShieldPath([float]$s) {
    # Normalized shield, inset a little from the tile edges.
    function X([double]$v) { return [float]($s * (0.16 + 0.68 * $v)) }
    function Y([double]$v) { return [float]($s * (0.14 + 0.70 * $v)) }

    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.StartFigure()
    $p.AddLine((X 0.5), (Y 0.0), (X 1.0), (Y 0.22))
    $p.AddLine((X 1.0), (Y 0.22), (X 1.0), (Y 0.52))
    $p.AddBezier((X 1.0), (Y 0.52), (X 0.97), (Y 0.82), (X 0.74), (Y 0.95), (X 0.5), (Y 1.0))
    $p.AddBezier((X 0.5), (Y 1.0), (X 0.26), (Y 0.95), (X 0.03), (Y 0.82), (X 0.0), (Y 0.52))
    $p.AddLine((X 0.0), (Y 0.52), (X 0.0), (Y 0.22))
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded gradient tile
    $radius = [float]($s * 0.22)
    $tile = New-RoundedPath 0 0 $s $s $radius
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 79, 140, 255),
        [System.Drawing.Color]::FromArgb(255, 26, 58, 143),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $tile)

    # White shield
    $shield = New-ShieldPath $s
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPath($white, $shield)

    # Blue keyhole (circle + stem)
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 64, 150))
    $cx = [float]($s * 0.5)
    $cy = [float]($s * 0.44)
    $r = [float]($s * 0.085)
    $g.FillEllipse($blue, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
    $stem = New-Object System.Drawing.Drawing2D.GraphicsPath
    $stem.AddPolygon(@(
        (New-Object System.Drawing.PointF(($cx - $r * 0.7), ($cy + $r * 0.6))),
        (New-Object System.Drawing.PointF(($cx + $r * 0.7), ($cy + $r * 0.6))),
        (New-Object System.Drawing.PointF($cx), ([float]($s * 0.66)))
    ))
    $g.FillPath($blue, $stem)

    $brush.Dispose(); $tile.Dispose(); $shield.Dispose(); $stroke = $null
    $white.Dispose(); $blue.Dispose(); $stem.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$e.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush(); $fs.Close()

Write-Host "Wrote $outPath ($((Get-Item $outPath).Length) bytes, $($entries.Count) sizes)"
