# Draws WALLNut's icons into src/WallNut.App/Assets. Windows only (uses System.Drawing).
#   pwsh tools/make-icons.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\WallNut.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-Walnut([int]$size, [string]$dot) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0

    # Shell: a slightly egg-shaped walnut with a highlight.
    $shell = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(10*$s, 6*$s), [System.Drawing.PointF]::new(54*$s, 60*$s),
        [System.Drawing.Color]::FromArgb(255, 196, 142, 82), [System.Drawing.Color]::FromArgb(255, 122, 78, 38))
    $g.FillEllipse($shell, 8*$s, 4*$s, 48*$s, 56*$s)
    $edge = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 84, 52, 24), [single](2.2*$s))
    $g.DrawEllipse($edge, 8*$s, 4*$s, 48*$s, 56*$s)

    # Seam down the middle and a few wrinkles.
    $seam = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 84, 52, 24), [single](2.6*$s))
    $g.DrawBezier($seam, 32*$s, 5*$s, 28*$s, 22*$s, 36*$s, 42*$s, 32*$s, 59*$s)
    $wr = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 84, 52, 24), [single](1.6*$s))
    $g.DrawBezier($wr, 18*$s, 18*$s, 22*$s, 24*$s, 18*$s, 32*$s, 21*$s, 40*$s)
    $g.DrawBezier($wr, 46*$s, 18*$s, 42*$s, 26*$s, 46*$s, 34*$s, 43*$s, 42*$s)
    $hi = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
    $g.FillEllipse($hi, 15*$s, 9*$s, 12*$s, 16*$s)

    if ($dot) {
        $c = switch ($dot) { 'amber' { [System.Drawing.Color]::FromArgb(255, 245, 166, 35) }
                             'red'   { [System.Drawing.Color]::FromArgb(255, 220, 38, 38) }
                             'grey'  { [System.Drawing.Color]::FromArgb(255, 140, 140, 140) } }
        $g.FillEllipse([System.Drawing.Brushes]::White, 36*$s, 36*$s, 28*$s, 28*$s)
        $g.FillEllipse([System.Drawing.SolidBrush]::new($c), 39*$s, 39*$s, 22*$s, 22*$s)
    }
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

Save-Png (New-Walnut 256 $null) (Join-Path $out 'wallnut.png')
foreach ($state in @{ ok = $null; warning = 'amber'; problem = 'red'; offline = 'grey' }.GetEnumerator()) {
    Save-Png (New-Walnut 64 $state.Value) (Join-Path $out "tray-$($state.Key).png")
}

# .ico with PNG-compressed 16, 32, 48 and 256 px images (supported since Windows Vista).
$sizes = 16, 32, 48, 256
$images = foreach ($sz in $sizes) {
    $ms = [System.IO.MemoryStream]::new()
    $b = New-Walnut $sz $null; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'wallnut.ico'), $ico.ToArray())
Get-ChildItem $out | Select-Object Name, Length
