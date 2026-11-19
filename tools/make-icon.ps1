# Draws the application icon and writes it as an .ico (all usual sizes) plus a PNG for the README.
#   tools\make-icon.ps1 [-Preview FILE.png]
# The picture: a "play" triangle cut into three frames on a dark tile. The outer two are white - the frames the
# source delivers - and the one in between, the frame the application adds, is the coloured one.
param(
    [string]$Preview = ''       # also write a sheet with the small sizes enlarged, to judge them
)

Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

function New-IconBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $small = $size -le 24

    # The tile: a rounded square, dark indigo running to almost black.
    $margin = $(if ($small) { 0 } else { 6 * $s })
    $side = $size - 2 * $margin
    $radius = 58 * $s
    $d = 2 * $radius
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($margin, $margin, $d, $d, 180, 90)
    $tile.AddArc($margin + $side - $d, $margin, $d, $d, 270, 90)
    $tile.AddArc($margin + $side - $d, $margin + $side - $d, $d, $d, 0, 90)
    $tile.AddArc($margin, $margin + $side - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $tileRect = New-Object System.Drawing.RectangleF $margin, $margin, $side, $side
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $tileRect,
        ([System.Drawing.Color]::FromArgb(255, 52, 60, 150)), ([System.Drawing.Color]::FromArgb(255, 14, 16, 48)), 55.0
    $g.FillPath($tileBrush, $tile)

    # The triangle, cut into three slices by two gaps. Small sizes get a bolder one with wider gaps.
    $left = $(if ($small) { 58 } else { 70 }) * $s
    $right = $(if ($small) { 210 } else { 202 }) * $s
    $top = $(if ($small) { 44 } else { 54 }) * $s
    $bottom = 256 * $s - $top
    $gap = $(if ($small) { 18 } else { 11 }) * $s
    $middle = ($top + $bottom) / 2
    $slope = ($middle - $top) / ($right - $left)
    $width = ($right - $left - 2 * $gap) / 3
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 244, 246, 255))
    for ($i = 0; $i -lt 3; $i++) {
        $x0 = $left + $i * ($width + $gap)
        $x1 = $x0 + $width
        $points = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF $x0, ($top + ($x0 - $left) * $slope)),
            (New-Object System.Drawing.PointF $x1, ($top + ($x1 - $left) * $slope)),
            (New-Object System.Drawing.PointF $x1, ($bottom - ($x1 - $left) * $slope)),
            (New-Object System.Drawing.PointF $x0, ($bottom - ($x0 - $left) * $slope)))
        if ($i -eq 1) {
            # The frame in between: mint running to sky blue.
            $box = New-Object System.Drawing.RectangleF $x0, $top, $width, ($bottom - $top)
            $accent = New-Object System.Drawing.Drawing2D.LinearGradientBrush $box,
                ([System.Drawing.Color]::FromArgb(255, 70, 248, 176)), ([System.Drawing.Color]::FromArgb(255, 30, 190, 255)), 90.0
            $g.FillPolygon($accent, $points)
            $accent.Dispose()
        }
        else { $g.FillPolygon($white, $points) }
    }
    $white.Dispose(); $tileBrush.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bitmap
}

function Get-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($size in $sizes) { $images[$size] = Get-PngBytes (New-IconBitmap $size) }

# An .ico file: a directory of entries, each pointing at a PNG.
$assets = Join-Path $root 'src\InterpolatIt.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$icoPath = Join-Path $assets 'Interpolate-it.ico'
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))      # 0 stands for 256
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$size].Length); $writer.Write([uint32]$offset)
    $offset += $images[$size].Length
}
foreach ($size in $sizes) { $writer.Write([byte[]]$images[$size]) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
"written: $icoPath ($([math]::Round($out.Length / 1KB)) KB, sizes $($sizes -join ', '))"

$docs = Join-Path $root 'docs'
New-Item -ItemType Directory -Force $docs | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $docs 'icon.png'), $images[256])
"written: $(Join-Path $docs 'icon.png')"

if ($Preview) {
    # 256 as it is, and 48 / 32 / 24 / 16 enlarged without smoothing, on a light and on a dark strip.
    $sheet = New-Object System.Drawing.Bitmap 900, 560
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 243, 243, 243))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))), 0, 280, 900, 280)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    foreach ($row in 0, 1) {
        $y = 12 + 280 * $row
        $g.DrawImage((New-IconBitmap 256), 12, $y, 256, 256)
        $x = 290
        foreach ($size in 48, 32, 24, 16) {
            $scale = [math]::Floor(144 / $size)
            $g.DrawImage((New-IconBitmap $size), $x, $y, $size * $scale, $size * $scale)
            $g.DrawImage((New-IconBitmap $size), $x, $y + 160, $size, $size)      # and at its real size
            $x += 152
        }
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    "preview: $Preview"
}
