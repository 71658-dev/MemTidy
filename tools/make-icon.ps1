param([string]$OutIco = "$PSScriptRoot\..\app.ico", [string]$OutPreview = "$PSScriptRoot\..\icon-preview.png")
Add-Type -AssemblyName System.Drawing

# Slate-900 tile, green memory chip (the tray's "normal" colour), white crescent = App Nap.
function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $small = $s -le 24

    # Tile with a Slate-600 hairline so it stays visible on dark taskbars
    $hw = [single]([Math]::Max(1, $s * 0.016)); $i = $hw / 2
    $tile = RoundRect $i $i ($s - $hw) ($s - $hw) ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(15, 23, 42))), $tile)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(71, 85, 105)), $hw), $tile)

    # Chip pins (3 per side, 2 on tiny sizes)
    $green = [System.Drawing.Color]::FromArgb(52, 199, 89)
    $inset = [single]($s * $(if ($small) { 0.27 } else { 0.27 })); $body = [single]($s - 2 * $inset)
    $reach = [single]($s * $(if ($small) { 0.14 } else { 0.18 }))
    $pin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(203, 213, 225)), ([single][Math]::Max(1.2, $s * $(if ($small) { 0.075 } else { 0.045 })))
    $pin.StartCap = 'Round'; $pin.EndCap = 'Round'
    $slots = $(if ($small) { @(0.3, 0.7) } else { @(0.2, 0.5, 0.8) })
    foreach ($f in $slots) {
        $t = [single]($inset + $body * $f)
        $g.DrawLine($pin, $t, $reach, $t, $inset); $g.DrawLine($pin, $t, $s - $inset, $t, $s - $reach)
        $g.DrawLine($pin, $reach, $t, $inset, $t); $g.DrawLine($pin, $s - $inset, $t, $s - $reach, $t)
    }

    # Chip body
    $chip = RoundRect $inset $inset $body $body ($body * 0.2)
    $g.FillPath((New-Object System.Drawing.SolidBrush $green), $chip)

    # Crescent moon on the chip
    if (-not $small) {
        $d = [single]($body * 0.56); $cx = [single]($s / 2 - $d / 2); $cy = $cx
        $g.FillEllipse([System.Drawing.Brushes]::White, $cx, $cy, $d, $d)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $green), [single]($cx + $d * 0.34), [single]($cy - $d * 0.2), $d, $d)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s; $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}

# ICO container with PNG-compressed entries
$out = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = [byte]($(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes($OutIco, $out.ToArray())

# Preview strip: 256, 48, 32, 16 on light and dark backgrounds
$pv = New-Object System.Drawing.Bitmap 760, 300
$g = [System.Drawing.Graphics]::FromImage($pv); $g.InterpolationMode = 'NearestNeighbor'
$g.FillRectangle([System.Drawing.Brushes]::White, 0, 0, 380, 300)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 380, 0, 380, 300)
foreach ($bg in 0, 380) {
    $big = Draw 256; $g.DrawImage($big, $bg + 20, 22, 256, 256)
    $y = 30
    foreach ($s in 48, 32, 16) { $b = Draw $s; $g.DrawImage($b, $bg + 300, $y, $s, $s); $y += $s + 24 }
}
$g.Dispose(); $pv.Save($OutPreview, [System.Drawing.Imaging.ImageFormat]::Png)
"ok: $((Get-Item $OutIco).Length) bytes"
