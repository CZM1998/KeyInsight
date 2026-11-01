param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Assets\icon-source.png'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$srcPath = (Resolve-Path $Source).Path
$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$square = $null
try {
    # Normalize alpha (remove stray near-transparent pixels) while finding the content bounds.
    $rect = New-Object System.Drawing.Rectangle 0, 0, $src.Width, $src.Height
    $data = $src.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $src.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)

    $minX = $src.Width
    $minY = $src.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $src.Height; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $src.Width; $x++) {
            $i = $row + ($x * 4)
            if ($bytes[$i + 3] -lt 8) {
                $bytes[$i] = 0; $bytes[$i + 1] = 0; $bytes[$i + 2] = 0; $bytes[$i + 3] = 0
            }
            else {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    $src.UnlockBits($data)
    $data = $null

    if ($maxX -lt 0) { throw 'No opaque pixels found in source image.' }

    $w = $maxX - $minX + 1
    $h = $maxY - $minY + 1
    $pad = [int][Math]::Ceiling([Math]::Max($w, $h) * 0.035)

    $cropX = [Math]::Max(0, $minX - $pad)
    $cropY = [Math]::Max(0, $minY - $pad)
    $cropW = [Math]::Min($src.Width - $cropX, $w + ($pad * 2))
    $cropH = [Math]::Min($src.Height - $cropY, $h + ($pad * 2))

    $side = [Math]::Max($cropW, $cropH)
    $square = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($square)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $dest = New-Object System.Drawing.Rectangle ([int](($side - $cropW) / 2)), ([int](($side - $cropH) / 2)), $cropW, $cropH
        $srcRect = New-Object System.Drawing.Rectangle $cropX, $cropY, $cropW, $cropH
        $g.DrawImage($src, $dest, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $g.Dispose()
    }

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $pngs = New-Object System.Collections.ArrayList
    $png256 = $null

    foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gg = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $gg.Clear([System.Drawing.Color]::Transparent)
            $gg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $gg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $gg.DrawImage($square, 0, 0, $size, $size)
        }
        finally {
            $gg.Dispose()
        }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $arr = $ms.ToArray()
        [void]$pngs.Add($arr)
        if ($size -eq 256) { $png256 = $arr }

        if ($size -eq 256) {
            [System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app256.png'), $arr)
        }
        $ms.Dispose()
        $bmp.Dispose()
    }

    $icoPath = Join-Path $OutDir 'app.ico'
    $fs = [System.IO.File]::Create($icoPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([UInt16]0)
        $bw.Write([UInt16]1)
        $bw.Write([UInt16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $s = $sizes[$i]
            $wByte = if ($s -ge 256) { 0 } else { $s }
            $bw.Write([byte]$wByte)
            $bw.Write([byte]$wByte)
            $bw.Write([byte]0)
            $bw.Write([byte]0)
            $bw.Write([UInt16]1)
            $bw.Write([UInt16]32)
            $png = $pngs[$i]
            $bw.Write([UInt32]$png.Length)
            $bw.Write([UInt32]$offset)
            $offset += $png.Length
        }
        foreach ($png in $pngs) {
            $bw.Write($png)
        }
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }

    Write-Host ('icon: ' + $icoPath)
    Write-Host ('size: ' + (Get-Item $icoPath).Length + ' bytes')
    Write-Host ('source content: ' + $w + 'x' + $h + '  crop: ' + $cropW + 'x' + $cropH)
}
finally {
    if ($square -ne $null) { $square.Dispose() }
    $src.Dispose()
}
