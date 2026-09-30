# Generates Assets\icon.ico (16-256 px, PNG-compressed entries) and Assets\icon-256.png.
# The icon: a dark rounded tile with a waveform glyph between a left and a right meter bar.
param([string]$OutDir = "$PSScriptRoot\..\src\SoundRadar\Assets")

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Rainbow([double]$t) {
    # Same "Rainbow" scheme as the app: red -> green -> blue -> purple along the bar.
    $pos = [int]([Math]::Min(255, [Math]::Max(0, $t * 255)))
    if ($pos -lt 64) { return [Drawing.Color]::FromArgb(255 - $pos * 4, $pos * 4, 0) }
    if ($pos -lt 128) { $p = $pos - 64; return [Drawing.Color]::FromArgb(0, 255 - $p * 4, $p * 4) }
    if ($pos -lt 192) { $p = $pos - 128; return [Drawing.Color]::FromArgb($p * 4, 0, 255 - $p * 2) }
    return [Drawing.Color]::FromArgb(255, 0, 255)
}

function RoundRect([Drawing.Graphics]$g, [Drawing.Brush]$brush, [double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    if ($r -lt 0.6) { $g.FillRectangle($brush, [single]$x, [single]$y, [single]$w, [single]$h); return }
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = [single](2 * $r)
    $path.AddArc([single]$x, [single]$y, $d, $d, 180, 90)
    $path.AddArc([single]($x + $w - 2 * $r), [single]$y, $d, $d, 270, 90)
    $path.AddArc([single]($x + $w - 2 * $r), [single]($y + $h - 2 * $r), $d, $d, 0, 90)
    $path.AddArc([single]$x, [single]($y + $h - 2 * $r), $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $path.Dispose()
}

function Render([int]$S) {
    $bmp = New-Object Drawing.Bitmap $S, $S, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $S * 0.03)
    $bg = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 34, 34, 38))
    RoundRect $g $bg $m $m ($S - 2 * $m) ($S - 2 * $m) ($S * 0.22)

    # Edge meters: left louder than right, filled bottom-up.
    $barW = [Math]::Max(2, $S * 0.12)
    $top = $S * 0.14; $bottom = $S * 0.86; $len = $bottom - $top
    $segments = if ($S -ge 48) { 7 } elseif ($S -ge 32) { 5 } else { 0 }
    foreach ($bar in @(@{ x = $S * 0.12; fill = 0.86 }, @{ x = $S * 0.88 - $barW; fill = 0.5 })) {
        $track = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60, 255, 255, 255))
        RoundRect $g $track $bar.x $top $barW $len ($barW / 2)
        if ($segments -gt 0) {
            $pitch = $len / $segments; $gap = [Math]::Max(1, $pitch * 0.22)
            $lit = [Math]::Round($bar.fill * $segments)
            for ($i = 0; $i -lt $lit; $i++) {
                $b = New-Object Drawing.SolidBrush (Rainbow ($i / $segments))
                $y = $bottom - ($i + 1) * $pitch + $gap / 2
                RoundRect $g $b $bar.x $y $barW ($pitch - $gap) ($barW / 3)
                $b.Dispose()
            }
        } else {
            $h = $len * $bar.fill
            $brush = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, $bottom + 1)), ([Drawing.PointF]::new(0, $top - 1)), (Rainbow 0), (Rainbow 0.6)
            RoundRect $g $brush $bar.x ($bottom - $h) $barW $h ($barW / 2)
            $brush.Dispose()
        }
        $track.Dispose()
    }

    # Waveform glyph in the middle (echoes the original icon).
    $heights = if ($S -ge 32) { @(0.22, 0.5, 0.34, 0.6, 0.28) } else { @(0.3, 0.6, 0.4) }
    $zoneL = $S * 0.31; $zoneR = $S * 0.69
    $n = $heights.Count
    $slot = ($zoneR - $zoneL) / $n
    $w = [Math]::Max(1, $slot * 0.55)
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(235, 239, 239, 239))
    for ($i = 0; $i -lt $n; $i++) {
        $h = $S * $heights[$i]
        $x = $zoneL + $i * $slot + ($slot - $w) / 2
        RoundRect $g $white $x (($S - $h) / 2) $w $h ($w / 2)
    }
    $white.Dispose(); $bg.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save("$OutDir\icon-256.png", [Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one directory entry per size, then the PNG payloads.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes("$OutDir\icon.ico", $out.ToArray())
"Wrote $OutDir\icon.ico ($($sizes -join ', ') px) and icon-256.png"
