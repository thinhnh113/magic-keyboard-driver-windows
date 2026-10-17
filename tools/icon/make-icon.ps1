# Renders the MagicKeyboard app icon at all Windows sizes and packs them into a .ico.
# Same visual family as MagicMouse (..\magic_mouse\tools\icon\make-icon.ps1): blue -> violet tile,
# white device, accent highlights. Here the accent marks the media key row and the Fn key.
# Usage: powershell -File tools\icon\make-icon.ps1
param(
    [string]$OutIco = "$PSScriptRoot\..\..\gui\MagicKeyboard\Assets\MagicKeyboard.ico",
    [string]$PreviewPng = "$PSScriptRoot\preview.png"
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Max(0.5, [Math]::Min($r, [Math]::Min($w, $h) / 2))
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Tile: diagonal blue -> violet gradient (identical to MagicMouse)
    $tile = New-RoundRect (8*$s) (8*$s) (240*$s) (240*$s) (56*$s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 56, 132, 255), [System.Drawing.Color]::FromArgb(255, 132, 76, 230))
    $g.FillPath($grad, $tile)

    # Keyboard body: wide rounded slab, white with soft shadow
    $kx = 30*$s; $ky = 70*$s; $kw = 196*$s; $kh = 116*$s; $kr = 20*$s
    $shadow = New-RoundRect ($kx + 3*$s) ($ky + 6*$s) $kw $kh $kr
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 20, 10, 60))), $shadow)
    $body = New-RoundRect $kx $ky $kw $kh $kr
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, $ky), (New-Object System.Drawing.PointF 0, ($ky + $kh)),
        [System.Drawing.Color]::FromArgb(255, 255, 255, 255), [System.Drawing.Color]::FromArgb(255, 226, 230, 242))
    $g.FillPath($bodyBrush, $body)

    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 88, 104, 245))
    $keyFill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 200, 207, 232))

    $ix = $kx + 16*$s; $iw = $kw - 32*$s     # inner key area

    if ($size -ge 48) {
        # Individual keys: media row (accent), two letter rows, bottom row with Fn (accent) + space bar
        $cols = 7; $gap = 6*$s
        $kwid = ($iw - ($cols - 1) * $gap) / $cols
        $rows = @(
            @{ y = 84;  h = 12; accent = $true },
            @{ y = 104; h = 20; accent = $false },
            @{ y = 130; h = 20; accent = $false })
        foreach ($row in $rows) {
            for ($c = 0; $c -lt $cols; $c++) {
                $x = $ix + $c * ($kwid + $gap)
                $brush = if ($row.accent) { $accent } else { $keyFill }
                $g.FillPath($brush, (New-RoundRect $x ($row.y*$s) $kwid ($row.h*$s) (4*$s)))
            }
        }
        $by = 156*$s; $bh = 18*$s
        $g.FillPath($accent, (New-RoundRect $ix $by $kwid $bh (4*$s)))                              # Fn
        $g.FillPath($keyFill, (New-RoundRect ($ix + $kwid + $gap) $by $kwid $bh (4*$s)))             # Ctrl
        $g.FillPath($keyFill, (New-RoundRect ($ix + 2*($kwid + $gap)) $by (3*$kwid + 2*$gap) $bh (4*$s)))  # space
        $g.FillPath($keyFill, (New-RoundRect ($ix + 5*($kwid + $gap)) $by (2*$kwid + $gap) $bh (4*$s)))    # arrows
    } else {
        # Small sizes: rows as bars so the shape stays readable
        $t = [Math]::Max(1.5, 14*$s)
        $g.FillPath($accent,  (New-RoundRect $ix (86*$s)  $iw $t ($t/2)))
        $g.FillPath($keyFill, (New-RoundRect $ix (112*$s) $iw $t ($t/2)))
        $g.FillPath($keyFill, (New-RoundRect $ix (136*$s) $iw $t ($t/2)))
        $g.FillPath($keyFill, (New-RoundRect ($ix + $iw*0.25) (160*$s) ($iw*0.5) $t ($t/2)))
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $b = Render $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $b.Dispose()
}

# ICO container with PNG-compressed entries (supported since Vista)
New-Item -ItemType Directory -Force -Path (Split-Path $OutIco) | Out-Null
$fs = [System.IO.File]::Create($OutIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

# Preview sheet: every size on light and dark backgrounds
$pw = 40; foreach ($sz in $sizes) { $pw += $sz + 24 }
$sheet = New-Object System.Drawing.Bitmap $pw, 600
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(245, 245, 247))
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 36))), 0, 300, $pw, 300)
$x = 20
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $img = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream (, $pngs[$i])))
    $g.DrawImage($img, $x, 150 - $sizes[$i] / 2, $sizes[$i], $sizes[$i])
    $g.DrawImage($img, $x, 450 - $sizes[$i] / 2, $sizes[$i], $sizes[$i])
    $x += $sizes[$i] + 24
}
$g.Dispose()
$sheet.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
"Wrote $OutIco ($((Get-Item $OutIco).Length) bytes) and $PreviewPng"
