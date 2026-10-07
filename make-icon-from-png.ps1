param(
  [string]$Source = "Resources\AppIcon-source.png",
  [string]$OutIco = "Resources\AppIcon.ico",
  [string]$OutPreview
)

# Generates Resources\AppIcon.ico from a picture rather than from drawing code. Used since 0.8.0,
# when the user chose a ready-made image (Resources\AppIcon-source.png, an Icons8 94 px PNG) over
# the drawn chassis that make-icon.ps1 produces.
#
#   .\make-icon-from-png.ps1 -OutPreview preview.png
#
# Every size is resampled from the one source, centred on a transparent square. Sizes above the
# source's own are enlarged and so come out softer; a bigger source image fixes that.

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

function Render([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

  $scale = [Math]::Min($size / $src.Width, $size / $src.Height)
  $w = $src.Width * $scale
  $h = $src.Height * $scale
  $attributes = New-Object System.Drawing.Imaging.ImageAttributes
  $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)   # no dark fringe at the edges
  $dest = New-Object System.Drawing.Rectangle ([int][Math]::Round(($size - $w) / 2)), ([int][Math]::Round(($size - $h) / 2)), ([int][Math]::Round($w)), ([int][Math]::Round($h))
  $g.DrawImage($src, $dest, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
  $g.Dispose()
  return $bmp
}

# ---------------------------------------------------------------- the .ico
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$images = @()
foreach ($s in $sizes) {
  $bmp = Render $s
  $stream = New-Object System.IO.MemoryStream
  $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
  $images += [pscustomobject]@{ Size = $s; Bytes = $stream.ToArray() }
  $bmp.Dispose()
}

# PNG-compressed icon entries since Vista, the same layout make-icon.ps1 writes.
$out = [System.IO.File]::Create((Join-Path (Get-Location) $OutIco))
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0)
$w.Write([UInt16]1)
$w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
  $dim = if ($image.Size -ge 256) { 0 } else { $image.Size }
  $w.Write([Byte]$dim)
  $w.Write([Byte]$dim)
  $w.Write([Byte]0)
  $w.Write([Byte]0)
  $w.Write([UInt16]1)
  $w.Write([UInt16]32)
  $w.Write([UInt32]$image.Bytes.Length)
  $w.Write([UInt32]$offset)
  $offset += $image.Bytes.Length
}
foreach ($image in $images) { $w.Write($image.Bytes) }
$w.Close()
Write-Output "wrote $OutIco ($((Get-Item $OutIco).Length) bytes, $($images.Count) sizes) from $Source ($($src.Width)x$($src.Height))"

# ---------------------------------------------------------------- preview sheet
if ($OutPreview) {
  $pad = 12
  $sheetW = ($sizes | Measure-Object -Sum).Sum + $pad * ($sizes.Count + 1)
  $sheet = [System.Drawing.Bitmap]::new([int]$sheetW, [int](256 + 2 * $pad))
  $g = [System.Drawing.Graphics]::FromImage($sheet)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 30, 30, 30))
  $x = $pad
  foreach ($s in $sizes) {
    $bmp = Render $s
    $g.DrawImage($bmp, $x, $pad + (256 - $s), $s, $s)
    $bmp.Dispose()
    $x += $s + $pad
  }
  $g.Dispose()
  $sheet.Save($OutPreview, [System.Drawing.Imaging.ImageFormat]::Png)
  $sheet.Dispose()
}
$src.Dispose()
