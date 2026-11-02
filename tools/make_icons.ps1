# Generates the app icon (csharp-app\Assets\typryt.ico) from code: an accent square with a black border and a black "T|".
# No packages needed (System.Drawing). Sizes: 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 (each PNG-encoded inside the .ico).
param([string]$Out = (Join-Path $PSScriptRoot "..\csharp-app\Assets\typryt.ico"))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $border = [Math]::Max(1, [Math]::Round($size * 3 / 32))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xDE, 0x5C, 0x0A))), 0, 0, $size, $size)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Black), $border
    $pen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
    $g.DrawRectangle($pen, 0, 0, $size - 1, $size - 1)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $font = New-Object System.Drawing.Font "Consolas", ([float]($size * 0.5)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = "Center"; $fmt.LineAlignment = "Center"
    $g.DrawString("T|", $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF 0, ($size * 0.04), $size, $size), $fmt)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}
$stream = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write($i.Bytes) }
$w.Close()
"wrote $Out ($($images.Count) sizes)"
