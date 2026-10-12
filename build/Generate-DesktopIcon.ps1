# Regenerate the desktop icons — the Windows .ico and the macOS .icns — from the same music
# geometry and placement as the sidebar logo.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$iconDirectory = Join-Path $repositoryRoot 'src/LemonNeo.Desktop/Resources/Icons'

function Write-BigEndianUInt32 {
    param([System.IO.BinaryWriter]$writer, [uint32]$value)
    $bytes = [BitConverter]::GetBytes($value)
    [Array]::Reverse($bytes)
    $writer.Write($bytes)
}

function New-IconFrame {
    param([int]$size, $accent, $foreground, $tile, $music)
    $large = [System.Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [System.Drawing.Graphics]::FromImage($large)
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $scaledGraphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $png = [System.IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 16.0, $size / 16.0)
        $graphics.FillPath($accent, $tile)
        $graphics.FillPath($foreground, $music)
        $scaledGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $scaledGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaledGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaledGraphics.DrawImage($large, [System.Drawing.Rectangle]::new(0, 0, $size, $size),
            0, 0, $large.Width, $large.Height, [System.Drawing.GraphicsUnit]::Pixel)
        $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $png.ToArray()
    }
    finally {
        $png.Dispose(); $scaledGraphics.Dispose(); $bitmap.Dispose(); $graphics.Dispose(); $large.Dispose()
    }
}

[xml]$resources = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/LemonNeo.Desktop/Resources/Themes/Icons.axaml') -Raw
$namespaces = [System.Xml.XmlNamespaceManager]::new($resources.NameTable)
$namespaces.AddNamespace('a', 'https://github.com/avaloniaui')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$musicData = $resources.SelectSingleNode('//a:StreamGeometry[@x:Key="Icon.Music"]', $namespaces).InnerText
$placement = $resources.SelectSingleNode('//a:GeometryGroup[@x:Key="Icon.AppMusic"]//a:MatrixTransform', $namespaces).GetAttribute('Matrix')
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$matrixValues = @($placement.Split(',') | ForEach-Object { [single]::Parse($_, $culture) })
$tokens = @([regex]::Matches($musicData, '[MLCZ]|-?(?:\d*\.)?\d+') | ForEach-Object { $_.Value })
$music = [System.Drawing.Drawing2D.GraphicsPath]::new()
$cursor = 0
$currentX = 0.0; $currentY = 0.0
while ($cursor -lt $tokens.Count) {
    $command = $tokens[$cursor++]
    switch ($command) {
        'M' {
            $music.StartFigure()
            $currentX = [single]::Parse($tokens[$cursor++], $culture)
            $currentY = [single]::Parse($tokens[$cursor++], $culture)
        }
        'L' {
            $nextX = [single]::Parse($tokens[$cursor++], $culture)
            $nextY = [single]::Parse($tokens[$cursor++], $culture)
            $music.AddLine($currentX, $currentY, $nextX, $nextY)
            $currentX = $nextX; $currentY = $nextY
        }
        'C' {
            $points = for ($point = 0; $point -lt 6; $point++) { [single]::Parse($tokens[$cursor++], $culture) }
            $music.AddBezier($currentX, $currentY, $points[0], $points[1], $points[2], $points[3], $points[4], $points[5])
            $currentX = $points[4]; $currentY = $points[5]
        }
        'Z' { $music.CloseFigure() }
        default { throw "Unsupported music geometry command: $command" }
    }
}
$placementMatrix = [System.Drawing.Drawing2D.Matrix]::new(
    $matrixValues[0], $matrixValues[1], $matrixValues[2], $matrixValues[3],
    $matrixValues[4] + 4, $matrixValues[5] + 4)
$music.Transform($placementMatrix)
$tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
$tile.AddArc(4, 4, 40, 40, 180, 90)
$tile.AddArc(20, 4, 40, 40, 270, 90)
$tile.AddArc(20, 20, 40, 40, 0, 90)
$tile.AddArc(4, 20, 40, 40, 90, 90)
$tile.CloseFigure()
$accent = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#DCEC7E'))
$foreground = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#172027'))
# The .ico stops at 256 px, the largest size its directory entry can address; the .icns
# carries the same artwork up to 1024 px for Retina displays.
$iconSizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$icnsEntries = @(
    @{ Type = 'icp4'; Size = 16 }
    @{ Type = 'ic11'; Size = 32 }
    @{ Type = 'icp5'; Size = 32 }
    @{ Type = 'ic12'; Size = 64 }
    @{ Type = 'ic07'; Size = 128 }
    @{ Type = 'ic13'; Size = 256 }
    @{ Type = 'ic08'; Size = 256 }
    @{ Type = 'ic14'; Size = 512 }
    @{ Type = 'ic09'; Size = 512 }
    @{ Type = 'ic10'; Size = 1024 }
)
try {
    $frames = @{}
    foreach ($size in $iconSizes + @(512, 1024)) {
        $frames[$size] = New-IconFrame -size $size -accent $accent -foreground $foreground -tile $tile -music $music
    }
    New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null

    $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $iconDirectory 'App.ico')))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$iconSizes.Count)
        $offset = 6 + 16 * $iconSizes.Count
        foreach ($size in $iconSizes) {
            $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$size].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$size].Length
        }
        foreach ($size in $iconSizes) { $writer.Write([byte[]]$frames[$size]) }
    }
    finally { $writer.Dispose() }

    # A PNG-based .icns is a header followed by typed chunks, each carrying one PNG frame.
    $total = 8
    foreach ($entry in $icnsEntries) { $total += 8 + $frames[$entry.Size].Length }
    $encoding = [System.Text.Encoding]::ASCII
    $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $iconDirectory 'App.icns')))
    try {
        $writer.Write($encoding.GetBytes('icns'))
        Write-BigEndianUInt32 -writer $writer -value ([uint32]$total)
        foreach ($entry in $icnsEntries) {
            $data = $frames[$entry.Size]
            $writer.Write($encoding.GetBytes($entry.Type))
            Write-BigEndianUInt32 -writer $writer -value ([uint32]($data.Length + 8))
            $writer.Write([byte[]]$data)
        }
    }
    finally { $writer.Dispose() }
}
finally { $accent.Dispose(); $foreground.Dispose(); $tile.Dispose(); $placementMatrix.Dispose(); $music.Dispose() }
Write-Output "Generated App.ico with $($iconSizes.Count) sizes and App.icns with $($icnsEntries.Count) entries."
