# Generates tinted variants of vanilla's packed glacier ice textures for the wall/glacier blocks.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-ice-textures.ps1
# Reads from the game install, writes to assets\cylinderworld\textures\block\wallice\.
param(
    [string]$GamePath = "$env:APPDATA\Vintagestory",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\assets\cylinderworld\textures\block\wallice")
)
Add-Type -AssemblyName System.Drawing

$src = Join-Path $GamePath "assets\survival\textures\block\liquid\ice"
New-Item -ItemType Directory -Force $OutDir | Out-Null

# name -> (mulR, mulG, mulB, towardWhite)
$shades = [ordered]@{
    light = @(1.0, 1.0, 1.0, 0.28)
    dark  = @(0.80, 0.87, 0.96, 0.0)
    deep  = @(0.62, 0.74, 0.92, 0.0)
}

foreach ($i in 1..4) {
    $file = Join-Path $src "packedglacier$i.png"
    $bmp = [System.Drawing.Bitmap]::FromFile($file)
    foreach ($name in $shades.Keys) {
        $m = $shades[$name]
        $out = New-Object System.Drawing.Bitmap($bmp.Width, $bmp.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            for ($x = 0; $x -lt $bmp.Width; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $r = $c.R * $m[0]; $g = $c.G * $m[1]; $b = $c.B * $m[2]
                $r = $r + (255 - $r) * $m[3]; $g = $g + (255 - $g) * $m[3]; $b = $b + (255 - $b) * $m[3]
                $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, [int][Math]::Min(255, $r), [int][Math]::Min(255, $g), [int][Math]::Min(255, $b)))
            }
        }
        $out.Save((Join-Path $OutDir "$name$i.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $out.Dispose()
    }
    Write-Output ("packedglacier{0}: {1}x{2}, alpha at (0,0) = {3}" -f $i, $bmp.Width, $bmp.Height, $bmp.GetPixel(0, 0).A)
    $bmp.Dispose()
}
Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Output
