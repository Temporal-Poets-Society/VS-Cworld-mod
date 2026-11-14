param(
    [string]$PackagePath,
    [switch]$ExpectWallLava,
    [string]$GamePath = '',
    # A serverconfig.json to start from. Default: the game's own defaults with the checks' world (tests\common.ps1, CW-0013)
    [string]$TemplateConfig = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-wall-lava-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    # the DLL the probe compiles against (it runs with any build's package)
    [string]$ModDll = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (!$PackagePath) { $PackagePath = Join-Path $TestRoot "release\cylinderworld-$(Get-ModVersion).zip" }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
if (Test-Path -LiteralPath $DataPath) { throw "Use a fresh test data directory: $DataPath" }
New-Item -ItemType Directory -Path "$DataPath\Mods\walllavaprobe", "$DataPath\ModConfig" -Force | Out-Null
$DataPath = [IO.Path]::GetFullPath($DataPath)
# -ExpectWallLava runs an older package on purpose (the 1.0.1 world of oldworld.ps1); otherwise the zip is this build
if (!$ExpectWallLava) { Assert-ZipHoldsDll $PackagePath $ModDll }
Copy-Item -LiteralPath $PackagePath -Destination "$DataPath\Mods"
Set-Content -LiteralPath "$DataPath\Mods\walllavaprobe\modinfo.json" -Value '{"type":"code","modid":"walllavaprobe","name":"Wall Lava Probe","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0"}}'
@{ ExpectWallLava = [bool]$ExpectWallLava } | ConvertTo-Json | Set-Content -LiteralPath "$DataPath\ModConfig\wall-lava-probe.json"
$config = New-TestServerConfig -GamePath $GamePath -DataPath $DataPath -Template $TemplateConfig
$config.MapSizeX = 8192; $config.MapSizeY = 256; $config.MapSizeZ = 8192
$config.WorldConfig.MapSizeY = 256
$config.WorldConfig.WorldName = 'Cylinder wall lava regression'
$config.WorldConfig.SaveFileLocation = "$DataPath\Saves\wall-lava.vcdbs"
$config.WorldConfig.Seed = '4244'
Save-TestServerConfig $config $DataPath
Invoke-GameCompile -GamePath $GamePath -Out "$DataPath\Mods\walllavaprobe\WallLavaProbe.dll" -Sources @("$PSScriptRoot\SmokeMod.cs") -References @($ModDll)
Write-Output "Wall lava test data: $DataPath"
Invoke-TestServer -GamePath $GamePath -DataPath $DataPath -Name 'walllava' -TimeoutSeconds 180
$log = Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
$log -split '\r?\n' | Where-Object { $_ -match 'WALLLAVA|Cylinder World: .* is off:|\[Error\]|\[Fatal\]' } | Write-Output
# a game feature that switched itself off (CW-D39) fails the check too
if ($log -notmatch 'WALLLAVA PASS native rivulet regression' -or $log -match 'WALLLAVA FAIL|\[Fatal\]|Cylinder World: .* is off:') {
    throw "Wall lava regression failed; logs: $DataPath"
}
