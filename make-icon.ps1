param(
  [string]$OutIco,
  [string]$OutPreview
)

# Generates Resources\AppIcon.ico. The icon is drawn rather than drawn-over, so it lives here as
# code and can be retuned without an image editor:
#
#   .\make-icon.ps1 -OutIco Resources\AppIcon.ico -OutPreview preview.png
#
# The subject is a controller chassis seen face on: modules in a rack, the leftmost one the
# processor, capped green because a green light is what this app is about. The palette is the
# window's own, so the icon and the app look like the same product.

Add-Type -AssemblyName System.Drawing

$ChassisTop    = [System.Drawing.Color]::FromArgb(255, 70, 78, 89)
$ChassisBottom = [System.Drawing.Color]::FromArgb(255, 38, 43, 51)
$ChassisEdge   = [System.Drawing.Color]::FromArgb(255, 110, 120, 135)
$Accent        = [System.Drawing.Color]::FromArgb(255, 61, 163, 255)
$AccentDeep    = [System.Drawing.Color]::FromArgb(255, 22, 96, 173)
$Module        = [System.Drawing.Color]::FromArgb(255, 124, 134, 148)
$ModuleDeep    = [System.Drawing.Color]::FromArgb(255, 76, 84, 95)
$Lit           = [System.Drawing.Color]::FromArgb(255, 74, 214, 94)
$LitDeep       = [System.Drawing.Color]::FromArgb(255, 40, 150, 58)
$CapDim        = [System.Drawing.Color]::FromArgb(255, 52, 59, 68)

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
  $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  if ($r -le 0.4) {
    $path.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h))
    return $path
  }
  $d = $r * 2
  $path.AddArc($x, $y, $d, $d, 180, 90)
  $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  return $path
}

function Render([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)

  # Three modules rather than four: four is closer to a real rack and turns to mush small. Below
  # 24 pixels even three blur together, so those sizes get two chunky ones. Nobody counts modules
  # in a title bar, but everybody sees whether the shape is crisp.
  $modules = if ($size -le 20) { 2 } else { 3 }
  $drawEdge = $size -ge 20

  $margin = [Math]::Max(1.0, $size * 0.05)
  $cx = $margin
  $cy = $margin
  $cw = $size - 2 * $margin
  $ch = $size - 2 * $margin
  $radius = $size * 0.19

  $chassis = New-RoundedPath $cx $cy $cw $ch $radius
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.RectangleF $cx, $cy, $cw, $ch), $ChassisTop, $ChassisBottom, 90)
  $g.FillPath($brush, $chassis)
  $brush.Dispose()

  if ($drawEdge) {
    $pen = New-Object System.Drawing.Pen($ChassisEdge, [float]([Math]::Max(1.0, $size / 56.0)))
    $g.DrawPath($pen, $chassis)
    $pen.Dispose()
  }
  $chassis.Dispose()

  $inset = $size * 0.165
  $ix = $cx + $inset
  $iy = $cy + $inset
  $iw = $cw - 2 * $inset
  $ih = $ch - 2 * $inset

  $gap = $iw * 0.10
  $mw = ($iw - $gap * ($modules - 1)) / $modules
  $mr = $size * 0.045

  for ($i = 0; $i -lt $modules; $i++) {
    $mx = $ix + $i * ($mw + $gap)

    $body = if ($i -eq 0) { $Accent } else { $Module }
    $bodyDeep = if ($i -eq 0) { $AccentDeep } else { $ModuleDeep }

    $slot = New-RoundedPath $mx $iy $mw $ih $mr
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
      (New-Object System.Drawing.RectangleF $mx, $iy, $mw, $ih), $body, $bodyDeep, 90)
    $g.FillPath($fill, $slot)
    $fill.Dispose()
    $slot.Dispose()

    # A status cap across the top of each module. Green on the processor, dark on the rest. A cap
    # survives to 16 pixels where a round lamp turns into one grey pixel.
    $capH = [Math]::Max(1.0, $ih * 0.22)
    $capPath = New-RoundedPath $mx $iy $mw $capH $mr
    if ($i -eq 0) {
      $capFill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF $mx, $iy, $mw, $capH), $Lit, $LitDeep, 90)
      $g.FillPath($capFill, $capPath)
      $capFill.Dispose()
    }
    else {
      $capFill = New-Object System.Drawing.SolidBrush($CapDim)
      $g.FillPath($capFill, $capPath)
      $capFill.Dispose()
    }
    $capPath.Dispose()
  }

  $g.Dispose()
  return $bmp
}

# ---------------------------------------------------------------- the .ico
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) {
  $bmp = Render $s
  $stream = New-Object System.IO.MemoryStream
  $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
  $images += ,@{ Size = $s; Bytes = $stream.ToArray() }
  $stream.Dispose()
  $bmp.Dispose()
}

# ICONDIR, then one 16-byte ICONDIRENTRY per image, then the PNG payloads. Windows has read
# PNG-compressed icon entries since Vista, which keeps this to one encoder and no DIB masks.
$out = New-Object System.IO.MemoryStream
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
$w.Flush()
[System.IO.File]::WriteAllBytes($OutIco, $out.ToArray())
$w.Dispose(); $out.Dispose()
Write-Output "wrote $OutIco ($((Get-Item $OutIco).Length) bytes, $($images.Count) sizes)"

# ---------------------------------------------------------------- a sheet to look at
if ($OutPreview) {
  $shown = @(16, 24, 32, 48, 64, 128)
  $pad = 16
  $width = $pad
  foreach ($s in $shown) { $width += $s + $pad }
  $height = 128 + $pad * 2 + 20

  $sheet = New-Object System.Drawing.Bitmap $width, $height
  $sg = [System.Drawing.Graphics]::FromImage($sheet)
  $sg.Clear([System.Drawing.Color]::FromArgb(255, 30, 30, 30))
  $font = New-Object System.Drawing.Font("Consolas", 9)
  $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 154, 154, 154))

  $x = $pad
  foreach ($s in $shown) {
    $bmp = Render $s
    $y = $pad + (128 - $s)
    $sg.DrawImage($bmp, $x, $y, $s, $s)
    $sg.DrawString("$s", $font, $textBrush, [float]$x, [float]($pad + 130))
    $bmp.Dispose()
    $x += $s + $pad
  }

  $sg.Dispose()
  $sheet.Save($OutPreview, [System.Drawing.Imaging.ImageFormat]::Png)
  $sheet.Dispose()
  Write-Output "wrote $OutPreview"
}
