[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$iconRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$iconSource = [System.Drawing.Bitmap]::new((Join-Path $iconRoot 'assets\inbound-icon.png'))
try {
    $iconSizes = @(16,20,24,32,48,64,128,256)
    $iconFrames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($iconSize in $iconSizes) {
        $iconBitmap = [System.Drawing.Bitmap]::new($iconSize, $iconSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
            try {
                $iconGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $iconGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $iconGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $iconGraphics.DrawImage($iconSource, 0, 0, $iconSize, $iconSize)
            } finally { $iconGraphics.Dispose() }
            $iconStream = [System.IO.MemoryStream]::new()
            try {
                $iconBitmap.Save($iconStream, [System.Drawing.Imaging.ImageFormat]::Png)
                $iconFrames.Add($iconStream.ToArray())
                if ($iconSize -in @(16,32,48,128)) {
                    [System.IO.File]::WriteAllBytes((Join-Path $iconRoot "extensions\chrome\icons\icon$iconSize.png"), $iconStream.ToArray())
                }
            } finally { $iconStream.Dispose() }
        } finally { $iconBitmap.Dispose() }
    }
    $iconWriter = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $iconRoot 'assets\app.ico')))
    try {
        $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]$iconSizes.Count)
        $iconOffset = 6 + 16 * $iconSizes.Count
        for ($iconIndex = 0; $iconIndex -lt $iconSizes.Count; $iconIndex++) {
            $iconDimension = if ($iconSizes[$iconIndex] -eq 256) { 0 } else { $iconSizes[$iconIndex] }
            $iconWriter.Write([byte]$iconDimension); $iconWriter.Write([byte]$iconDimension)
            $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
            $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32)
            $iconWriter.Write([uint32]$iconFrames[$iconIndex].Length); $iconWriter.Write([uint32]$iconOffset)
            $iconOffset += $iconFrames[$iconIndex].Length
        }
        foreach ($iconFrame in $iconFrames) { $iconWriter.Write($iconFrame) }
    } finally { $iconWriter.Dispose() }
    Write-Output "Created ICO frames16–256 and extension PNG icons. Source alpha: $($iconSource.GetPixel(0,0).A)."
} finally { $iconSource.Dispose() }
