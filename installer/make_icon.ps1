# Draws the Typryt icon ("T" and the orange cursor bar, the same logo as the tray icon) and writes csharp-app\Assets\typryt.ico
# with 16, 32, 48 and 256 pixel PNG images. Run once; the .ico is committed.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\csharp-app\Assets"
New-Item -ItemType Directory -Force $out | Out-Null
$sizes = 16, 32, 48, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [int]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x24, 0x26, 0x2B))), $path)
    $font = New-Object System.Drawing.Font("Segoe UI Semibold", [single]($s * 0.62), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString("T", $font, [System.Drawing.Brushes]::White, [single]($s * 0.12), [single]($s * 0.08))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0xE8, 0x62, 0x0A))), [single]($s * 0.68), [single]($s * 0.2), [single]([Math]::Max(2, $s * 0.1)), [single]($s * 0.6))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($s, $ms.ToArray())
    $bmp.Dispose()
}
$stream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($stream)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $s = $i[0]; $bytes = $i[1]
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$bytes.Length); $w.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($i in $images) { $w.Write($i[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out "typryt.ico"), $stream.ToArray())
"wrote $(Join-Path $out 'typryt.ico')"
