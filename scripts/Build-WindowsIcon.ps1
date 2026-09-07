param(
    [string]$SourcePng = (Join-Path $PSScriptRoot '..\CodexQuotaBar\Assets\carrot.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot '..\CodexQuotaBar\Assets\carrot.ico')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$sourcePath = (Resolve-Path -LiteralPath $SourcePng).Path
$source = [System.Drawing.Bitmap]::new($sourcePath)
$entries = New-Object System.Collections.Generic.List[byte[]]

foreach ($size in $sizes) {
    $canvas = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

    $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
    $drawWidth = [int][Math]::Round($source.Width * $scale)
    $drawHeight = [int][Math]::Round($source.Height * $scale)
    $x = [int][Math]::Floor(($size - $drawWidth) / 2)
    $y = [int][Math]::Floor(($size - $drawHeight) / 2)
    $graphics.DrawImage($source, $x, $y, $drawWidth, $drawHeight)

    $stream = [System.IO.MemoryStream]::new()
    $canvas.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add($stream.ToArray())

    $stream.Dispose()
    $graphics.Dispose()
    $canvas.Dispose()
}

$source.Dispose()

$outputPath = [System.IO.Path]::GetFullPath($OutputIco)
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($outputPath))
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$entries.Count)

$offset = 6 + (16 * $entries.Count)
for ($i = 0; $i -lt $entries.Count; $i++) {
    $size = $sizes[$i]
    $png = $entries[$i]
    $directorySize = $size
    if ($size -eq 256) {
        $directorySize = 0
    }
    $writer.Write([byte]$directorySize)
    $writer.Write([byte]$directorySize)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$png.Length)
    $writer.Write([UInt32]$offset)
    $offset += $png.Length
}

foreach ($png in $entries) {
    $writer.Write($png)
}

$writer.Dispose()
Write-Host "Icon written: $outputPath"
