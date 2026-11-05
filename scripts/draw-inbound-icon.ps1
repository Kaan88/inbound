[CmdletBinding()]
param([string]$Out)
# Draws the original Inbound app icon: a plane descending onto a runway on a rounded navy-to-blue tile.
# Pure vector drawing (no external art), rendered at 1024px; scripts\build-icons.ps1 downsamples it.
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\inbound-icon.png' }
Add-Type -AssemblyName System.Drawing
$S = 1024
$bmp = [System.Drawing.Bitmap]::new($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90)
    $p.CloseFigure(); $p
}

# Tile
$m = 40
$tile = RoundRect $m $m ($S-2*$m) ($S-2*$m) 210
$grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($S, $S),
    [System.Drawing.Color]::FromArgb(255, 11, 37, 69), [System.Drawing.Color]::FromArgb(255, 31, 111, 197))
$g.FillPath($grad, $tile)

# Runway: a perspective strip with centre dashes, lower right.
$runway = [System.Drawing.Drawing2D.GraphicsPath]::new()
$runway.AddPolygon([System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(300, 800), [System.Drawing.PointF]::new(900, 800),
    [System.Drawing.PointF]::new(860, 880), [System.Drawing.PointF]::new(240, 880)))
$g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 74, 163, 240)), $runway)
$dash = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
foreach ($x in 360, 520, 680) { $g.FillRectangle($dash, $x, 832, 90, 16) }

# Plane (top view, nose right) in a 100-unit box, rotated nose-down for the approach.
$pts = @(
    @(95,50), @(90,46.5), @(60,46.5), @(40,12), @(31,12), @(44,46.5), @(22,46.5), @(14,35), @(8,35), @(12,48.5),
    @(12,51.5), @(8,65), @(14,65), @(22,53.5), @(44,53.5), @(31,88), @(40,88), @(60,53.5), @(90,53.5))
$scale = 6.4; $angle = 28 * [Math]::PI / 180; $cx = 470; $cy = 430
$poly = foreach ($p in $pts) {
    $x = ($p[0] - 50) * $scale; $y = ($p[1] - 50) * $scale
    [System.Drawing.PointF]::new($cx + $x*[Math]::Cos($angle) - $y*[Math]::Sin($angle),
                                 $cy + $x*[Math]::Sin($angle) + $y*[Math]::Cos($angle))
}
$shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(60, 0, 0, 0))
$g.FillPolygon($shadow, [System.Drawing.PointF[]]($poly | ForEach-Object { [System.Drawing.PointF]::new($_.X + 14, $_.Y + 18) }))
$g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$poly)

$g.Dispose(); $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Output $Out
