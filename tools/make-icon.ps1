# Generates src/PeriBattery/Assets/app.ico (battery glyph on a dark red rounded square).
# Run with Windows PowerShell: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0

    $bg = New-RoundedRect (2 * $s) (2 * $s) (60 * $s) (60 * $s) (14 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 42, 18, 22))), $bg)
    $g.DrawPath((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 107, 107), [Math]::Max(1.0, 2.5 * $s))), $bg)

    $outline = [System.Drawing.Color]::FromArgb(255, 255, 228, 228)
    $body = New-RoundedRect (12 * $s) (22 * $s) (36 * $s) (20 * $s) (5 * $s)
    $g.DrawPath((New-Object System.Drawing.Pen($outline, [Math]::Max(1.0, 3.2 * $s))), $body)
    $nub = New-RoundedRect (50 * $s) (28 * $s) (4 * $s) (8 * $s) (1.5 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush($outline)), $nub)
    $fill = New-RoundedRect (16 * $s) (26 * $s) (22 * $s) (12 * $s) (2 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 46, 204, 143))), $fill)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$images = $sizes | ForEach-Object { ,(New-IconPng $_) }

$out = Join-Path $PSScriptRoot '..\src\PeriBattery\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose(); $fs.Dispose()
Write-Host "Wrote $out"

# PNG used as the toast notification logo.
$png = Join-Path $PSScriptRoot '..\src\PeriBattery\Assets\app.png'
[System.IO.File]::WriteAllBytes($png, (New-IconPng 96))
Write-Host "Wrote $png"
