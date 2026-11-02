# Generates installer wizard images for Inno Setup modern wizard:
#   wizard-large.bmp  164x314  (Welcome / Finish left panel)
#   wizard-small.bmp   55x55  (inner pages top-right mark)
# Design: dark background, orange accent (#DE5C0A), neo-brutalist borders.
Add-Type -AssemblyName System.Drawing

$here = $PSScriptRoot
$accent  = [System.Drawing.Color]::FromArgb(0xDE, 0x5C, 0x0A)
$black   = [System.Drawing.Color]::FromArgb(0x00, 0x00, 0x00)
$bg      = [System.Drawing.Color]::FromArgb(0x12, 0x12, 0x12)
$surface = [System.Drawing.Color]::FromArgb(0x1C, 0x1C, 0x1C)
$shadow  = [System.Drawing.Color]::FromArgb(0x3A, 0x3A, 0x3A)
$white   = [System.Drawing.Color]::White

function Save-Bmp([System.Drawing.Bitmap]$bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    Write-Host "wrote $path"
}

# ── Large image: 164 × 314 ──────────────────────────────────────────────────
$W = 164; $H = 314
$img = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($img)
$g.SmoothingMode    = 'None'
$g.TextRenderingHint = 'SingleBitPerPixelGridFit'

# background
$g.Clear($bg)

# horizontal rule at top
$g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), 0, 0, $W, 4)

# app mark: shadow then body
$ms = 60  # mark size
$mx = ($W - $ms) / 2   # centre horizontally
$my = 40

# hard shadow (+4,+4)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($shadow)), [int]($mx+4), [int]($my+4), $ms, $ms)
# orange body
$g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), [int]$mx, [int]$my, $ms, $ms)
# black 3-px border
$g.DrawRectangle((New-Object System.Drawing.Pen($black, 3)), [int]$mx, [int]$my, $ms-1, $ms-1)

# "T|" inside mark
$mf = New-Object System.Drawing.Font("Consolas", 26, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$tsz = $g.MeasureString("T|", $mf)
$tx = [int]($mx + ($ms - $tsz.Width)  / 2)
$ty = [int]($my + ($ms - $tsz.Height) / 2)
$g.DrawString("T|", $mf, (New-Object System.Drawing.SolidBrush($black)), [single]$tx, [single]$ty)
$mf.Dispose()

# wordmark "Typryt."
$wf = New-Object System.Drawing.Font("Consolas", 20, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$wy = $my + $ms + 20
$wleft = 16
$g.DrawString("Typryt", $wf, (New-Object System.Drawing.SolidBrush($white)), [single]$wleft, [single]$wy)
$dotX = $wleft + [int]$g.MeasureString("Typryt", $wf).Width - 2
$g.DrawString(".", $wf, (New-Object System.Drawing.SolidBrush($accent)), [single]$dotX, [single]$wy)
$wf.Dispose()

# tagline
$tf = New-Object System.Drawing.Font("Consolas", 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString("Type it right.", $tf, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0xD4, 0xD4, 0xD4))), [single]$wleft, [single]($wy + 26))
$tf.Dispose()

# bottom rule
$g.FillRectangle((New-Object System.Drawing.SolidBrush($surface)), 0, $H - 28, $W, 28)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($black)), 0, $H - 28, $W, 3)

$g.Dispose()
Save-Bmp $img "$here\wizard-large.bmp"
$img.Dispose()

# ── Small image: 55 × 55 ────────────────────────────────────────────────────
$S = 55
$simg = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g2 = [System.Drawing.Graphics]::FromImage($simg)
$g2.SmoothingMode    = 'None'
$g2.TextRenderingHint = 'SingleBitPerPixelGridFit'

$g2.Clear($bg)
# shadow
$g2.FillRectangle((New-Object System.Drawing.SolidBrush($shadow)), 4, 4, $S-4, $S-4)
# orange square
$g2.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), 0, 0, $S-4, $S-4)
# border
$g2.DrawRectangle((New-Object System.Drawing.Pen($black, 2)), 1, 1, $S-6, $S-6)
# T| text
$sf = New-Object System.Drawing.Font("Consolas", 18, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$ssz = $g2.MeasureString("T|", $sf)
$g2.DrawString("T|", $sf, (New-Object System.Drawing.SolidBrush($black)), [single](($S-4-$ssz.Width)/2), [single](($S-4-$ssz.Height)/2))
$sf.Dispose()

$g2.Dispose()
Save-Bmp $simg "$here\wizard-small.bmp"
$simg.Dispose()

"done."
