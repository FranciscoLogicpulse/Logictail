param(
    [string]$Accent = '#D97757',   # signal color (the owner's reference)
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\Logictail.App\Assets')
)
# Draws the Logictail icon: a page of log lines (the "tail" idea from BareTail's icon) with a live-signal
# mark (dot + arc) at the bottom-right. Drawn from scratch; no pixels are taken from any other icon.
# Writes logictail.ico (16-256 px, PNG-compressed entries) and logictail-256.png.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
$ink = Color '#323537'; $paper = Color '#FFFFFF'; $line = Color '#9AA5A9'; $signal = Color $Accent

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 24 # tiny sizes: fewer, thicker strokes so the shape still reads

    # Page.
    $stroke = if ($small) { 18 } else { 12 }
    $page = RoundRect 34 18 168 214 26
    $g.FillPath((New-Object System.Drawing.SolidBrush $paper), $page)
    $g.DrawPath((New-Object System.Drawing.Pen $ink, $stroke), $page)

    # Log lines.
    $lp = New-Object System.Drawing.Pen $line, ($(if ($small) { 22 } else { 15 }))
    $lp.StartCap = 'Round'; $lp.EndCap = 'Round'
    $rows = if ($small) { @(@(70, 82, 166), @(70, 124, 130)) } else { @(@(68, 66, 168), @(68, 100, 150), @(68, 134, 160), @(68, 168, 112)) }
    foreach ($r in $rows) { $g.DrawLine($lp, $r[0], $r[1], $r[2], $r[1]) }

    # Live signal: dot ring + quarter arc, with a paper-colored halo so it reads over the page edge.
    $cx = 176; $cy = 190
    $w = if ($small) { 26 } else { 20 }
    $halo = New-Object System.Drawing.Pen $paper, ($w + 18)
    $halo.StartCap = 'Round'; $halo.EndCap = 'Round'
    $g.DrawArc($halo, $cx - 58, $cy - 58, 116, 116, 270, 90)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $paper), $cx - 34, $cy - 34, 68, 68)
    $ap = New-Object System.Drawing.Pen $signal, $w
    $ap.StartCap = 'Round'; $ap.EndCap = 'Round'
    $g.DrawArc($ap, $cx - 58, $cy - 58, 116, 116, 270, 90)
    $ring = 22
    $g.DrawEllipse((New-Object System.Drawing.Pen $signal, $w), $cx - $ring, $cy - $ring, $ring * 2, $ring * 2)
    $g.Dispose()
    $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) { $b.Save((Join-Path $OutDir 'logictail-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one directory entry per size, then the PNG data.
$ico = New-Object System.IO.MemoryStream
$wr = New-Object System.IO.BinaryWriter $ico
$wr.Write([uint16]0); $wr.Write([uint16]1); $wr.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $wr.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $wr.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $wr.Write([byte]0); $wr.Write([byte]0); $wr.Write([uint16]1); $wr.Write([uint16]32)
    $wr.Write([uint32]$data.Length); $wr.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $wr.Write($data) }
$wr.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'logictail.ico'), $ico.ToArray())
"wrote $(Join-Path $OutDir 'logictail.ico') ($([math]::Round($ico.Length / 1KB)) KB, sizes $($sizes -join ', '))"
