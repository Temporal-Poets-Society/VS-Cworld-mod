# CW-D49 (CW-0005): lava already saved in the polar walls of an old world is taken out when its columns load.
# Phase 1 makes the old world with a build that still has the bug (run.ps1 -ExpectWallLava, 1.0.1 by default: its
# fixture puts native lava sources into the wall ice and the world is saved). Phase 2 opens that save with this build's
# Release zip and OldWorldProbe counts the sources in the wall columns and an ordinary-terrain control.
param(
    [string]$OldPackage = (Join-Path $PSScriptRoot '..\..\..\..\..\release\cylinderworld-1.0.1.zip'),
    [string]$PackagePath,
    # the DLL inside -PackagePath (default this checkout's Release build), checked against the zip
    [string]$ModDll = '',
    [string]$GamePath = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-wall-lava-old-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (!(Test-Path -LiteralPath $OldPackage)) { $OldPackage = Join-Path $TestRoot 'release\cylinderworld-1.0.1.zip' }
if (!(Test-Path -LiteralPath $OldPackage)) { throw "No old package with the wall-lava bug: pass -OldPackage (cylinderworld-1.0.1.zip or older)" }
if (!$PackagePath) { $PackagePath = Join-Path $TestRoot "release\cylinderworld-$(Get-ModVersion).zip" }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
& (Join-Path $PSScriptRoot 'run.ps1') -PackagePath ([IO.Path]::GetFullPath($OldPackage)) -ExpectWallLava -GamePath $GamePath -DataPath $DataPath
$DataPath = [IO.Path]::GetFullPath($DataPath)
Write-Output "Phase 1 done (old world saved with wall lava): $DataPath"

Remove-Item -LiteralPath (Join-Path $DataPath "Mods\$(Split-Path $OldPackage -Leaf)"), (Join-Path $DataPath 'Mods\walllavaprobe') -Recurse -Force
Assert-ZipHoldsDll $PackagePath $ModDll
Copy-Item -LiteralPath $PackagePath -Destination "$DataPath\Mods"
New-Item -ItemType Directory -Path "$DataPath\Mods\oldworldprobe" -Force | Out-Null
Set-Content -LiteralPath "$DataPath\Mods\oldworldprobe\modinfo.json" -Value '{"type":"code","modid":"oldworldprobe","name":"Old World Wall Lava Probe","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0"}}'
Invoke-GameCompile -GamePath $GamePath -Out "$DataPath\Mods\oldworldprobe\OldWorldProbe.dll" -Sources @("$PSScriptRoot\OldWorldProbe.cs")

# the same save; a fresh port
$config = Get-Content -LiteralPath "$DataPath\serverconfig.json" -Raw | ConvertFrom-Json
$config.Port = Get-FreePort
Save-TestServerConfig $config $DataPath
Move-Item -LiteralPath "$DataPath\Logs" "$DataPath\Logs-phase1"
Invoke-TestServer -GamePath $GamePath -DataPath $DataPath -Name 'oldworld' -TimeoutSeconds 180
$log = Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
$log -split '\r?\n' | Where-Object { $_ -match 'OLDWORLD|rivulet source|Cylinder World: .* is off:|\[Error\]|\[Fatal\]' } | Write-Output
if ($log -notmatch 'OLDWORLD PASS' -or $log -match 'OLDWORLD FAIL|\[Fatal\]|Cylinder World: .* is off:') {
    throw "Old world wall lava check failed; logs: $DataPath"
}
Remove-Item -LiteralPath $DataPath -Recurse -Force
