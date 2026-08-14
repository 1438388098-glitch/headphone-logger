# Generate HeadphoneLogger.ico (headphone icon, 256x256, matches IconFactory drawing)
# Usage: powershell -ExecutionPolicy Bypass -File make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path (Join-Path $outDir 'src') 'HeadphoneLogger\HeadphoneLogger.ico'
$size = 256
$s = $size / 32.0

$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

function FillRoundRect($g, $brush, $x, $y, $w, $h, $r) {
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
  $path.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
  $path.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
  $path.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
  $path.CloseFigure()
  $g.FillPath($brush, $path)
  $path.Dispose()
}

# rounded-square background (indigo)
$bg = New-Object System.Drawing.Drawing2D.GraphicsPath
$bg.AddArc(1 * $s, 1 * $s, 8 * $s, 8 * $s, 180, 90)
$bg.AddArc(23 * $s, 1 * $s, 8 * $s, 8 * $s, 270, 90)
$bg.AddArc(23 * $s, 23 * $s, 8 * $s, 8 * $s, 0, 90)
$bg.AddArc(1 * $s, 23 * $s, 8 * $s, 8 * $s, 90, 90)
$bg.CloseFigure()
$bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 63, 81, 140))
$g.FillPath($bgBrush, $bg)
$bgBrush.Dispose()
$bg.Dispose()

# headband arc
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White, [float](4 * $s))
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawArc($pen, 8 * $s, 6 * $s, 16 * $s, 20 * $s, 180, 180)
$pen.Dispose()

# earcups
$cup = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
FillRoundRect $g $cup (5 * $s) (17 * $s) (8 * $s) (10 * $s) (3 * $s)
FillRoundRect $g $cup (19 * $s) (17 * $s) (8 * $s) (10 * $s) (3 * $s)
$cup.Dispose()
$g.Dispose()

# save as .ico (GetHicon keeps alpha)
$hicon = $bmp.GetHicon()
try {
  $icon = [System.Drawing.Icon]::FromHandle($hicon)
  try {
    $fs = [System.IO.File]::Create($outPath)
    try { $icon.Save($fs) } finally { $fs.Dispose() }
  } finally { $icon.Dispose() }
} finally { $bmp.Dispose() }

Write-Output "generated: $outPath"
