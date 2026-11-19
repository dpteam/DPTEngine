#
# DPT.Engine - generate the application icon.
# Copyright (C) 2026 DartPower Team. GPLv2.
#
# The icon is generated rather than drawn by hand so it is reproducible: change
# the colours here, run this script, and every executable gets the new icon. A
# checked-in .ico that nobody can regenerate is a file that never gets updated.
#
# Design: the brand blue square, with the three letters DPT knocked out in white,
# and a thin grid line pattern behind them - the engine's own template is a grid
# corridor, and the icon reads at 16x16 because it is three letters on a flat
# field rather than a picture.
#
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\resources\dptengine.ico')
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)

# The light theme's primary, matching Branding.cs. Kept in step by hand because
# this script must run without building the engine first.
$primary = [System.Drawing.Color]::FromArgb(255, 0x00, 0x88, 0xFF)
$accent  = [System.Drawing.Color]::FromArgb(255, 0x00, 0xB4, 0xFF)

function New-IconBitmap([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Background: a vertical gradient, which reads better than flat colour at 256
    # and is indistinguishable from flat at 16.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $primary, $accent, 90.0)
    $g.FillRectangle($brush, $rect)
    $brush.Dispose()

    # Grid lines, only where there is room for them to be visible.
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 255, 255, 255), [Math]::Max(1.0, $size / 128.0))
        $step = [Math]::Max(4, [int]($size / 4))

        for ($i = $step; $i -lt $size; $i += $step) {
            $g.DrawLine($pen, $i, 0, $i, $size)
            $g.DrawLine($pen, 0, $i, $size, $i)
        }

        $pen.Dispose()
    }

    # Letters. Arial Bold is on every Windows version; a webfont would not be.
    $fontSize = $size * 0.40
    $font = New-Object System.Drawing.Font('Arial', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center

    $textRect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $g.DrawString('DPT', $font, [System.Drawing.Brushes]::White, $textRect, $format)

    $format.Dispose()
    $font.Dispose()
    $g.Dispose()

    return $bitmap
}

# An .ico is a directory of PNG-compressed images (Vista and later) or BMP
# images (everything). PNG is used for every size because it keeps the alpha
# channel and is far smaller; XP's shell handles PNG icons in its own way but the
# engine's own executables only need the icon to look right in a modern shell.
$images = @()

foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($size, $stream.ToArray())
    $stream.Dispose()
    $bitmap.Dispose()
}

$directory = Split-Path -Parent $Output
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

$file = [System.IO.File]::Create($Output)
$writer = New-Object System.IO.BinaryWriter($file)

# ICONDIR
$writer.Write([UInt16]0)            # reserved
$writer.Write([UInt16]1)            # type: icon
$writer.Write([UInt16]$images.Count)

# ICONDIRENTRY per image
$offset = 6 + 16 * $images.Count

foreach ($image in $images) {
    $size = $image[0]
    $bytes = $image[1]

    # 256 is stored as 0 in the directory.
    $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([Byte]0)          # palette count
    $writer.Write([Byte]0)          # reserved
    $writer.Write([UInt16]1)        # colour planes
    $writer.Write([UInt16]32)       # bits per pixel
    $writer.Write([UInt32]$bytes.Length)
    $writer.Write([UInt32]$offset)

    $offset += $bytes.Length
}

foreach ($image in $images) {
    $writer.Write([Byte[]]$image[1])
}

$writer.Flush()
$writer.Close()
$file.Close()

Write-Host ("Wrote " + $Output + " (" + (Get-Item $Output).Length + " bytes, " + $images.Count + " sizes)")
