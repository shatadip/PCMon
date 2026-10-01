# Renders src/PCMon/Assets/pcmon.ico: a blue rounded tile with a white heartbeat line.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot '..\src\PCMon\Assets\pcmon.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 76, 158, 255)), ([System.Drawing.Color]::FromArgb(255, 31, 90, 210)), 45
    $g.FillPath($brush, $path)

    # heartbeat: flat, blip up, deep down, recover, flat
    $pts = @(
        @(0.12, 0.56), @(0.34, 0.56), @(0.42, 0.36), @(0.52, 0.76),
        @(0.60, 0.24), @(0.68, 0.56), @(0.88, 0.56)
    ) | ForEach-Object { New-Object System.Drawing.PointF ($_[0] * $s), ($_[1] * $s) }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.4, $s * 0.085))
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = $sizes | ForEach-Object { , (New-Frame $_) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $frames[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $bw.Write($data) }
$bw.Dispose()
Write-Output "Wrote $out ($((Get-Item $out).Length) bytes)"
