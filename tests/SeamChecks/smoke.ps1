param(
    [string]$GamePath = '',
    # A serverconfig.json to start from. Default: the game's own defaults with the checks' world (tests\common.ps1, CW-0013)
    [string]$TemplateConfig = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-seam-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    # another build to test (e.g. a baseline for the terrain digest): its release zip and the CylinderWorld.dll inside it
    [string]$ModZip = '',
    [string]$ModDll = '',
    # chunk columns ('cx cz') whose raw blocks are also written to dump-cx-cz.bin, to see where two digests differ
    [string[]]$DumpColumns = @()
)
# Headless check of the seam-crossing code (see SeamSmoke.cs) on a temporary local-only server. Uses the release zip
# of this folder; never touches the normal game data. The generated terrain's fingerprint (SEAMSMOKE DIGEST lines) is
# written to digest.txt in the data folder: compare two builds' files to show they generate the same terrain.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (Test-Path -LiteralPath $DataPath) { throw "Use a fresh test data directory: $DataPath" }
New-Item -ItemType Directory -Path "$DataPath\Mods\seamsmoke","$DataPath\ModConfig" -Force | Out-Null
$DataPath=[IO.Path]::GetFullPath($DataPath)
if (!$ModZip) { $ModZip = Join-Path $TestRoot "release\cylinderworld-$(Get-ModVersion).zip" }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
Assert-ZipHoldsDll $ModZip $ModDll
Copy-Item -LiteralPath $ModZip -Destination "$DataPath\Mods\cylinderworld.zip"
if ($DumpColumns) { Set-Content -LiteralPath "$DataPath\ModConfig\seamsmoke-dump.txt" -Value $DumpColumns }
Set-Content -LiteralPath "$DataPath\Mods\seamsmoke\modinfo.json" -Value '{"type":"code","modid":"seamsmoke","name":"Seam Smoke Test","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0","cylinderworld":"1.0.0"}}'
$config=New-TestServerConfig -GamePath $GamePath -DataPath $DataPath -Template $TemplateConfig
$config.WorldConfig.WorldName='Cylinder seam smoke test'
$config.WorldConfig.SaveFileLocation="$DataPath\Saves\seam.vcdbs"
$config.WorldConfig.Seed='4244'
Save-TestServerConfig $config $DataPath
Invoke-GameCompile -GamePath $GamePath -Out "$DataPath\Mods\seamsmoke\SeamSmoke.dll" -Sources @("$PSScriptRoot\SeamSmoke.cs") -References @($ModDll)
Invoke-TestServer -GamePath $GamePath -DataPath $DataPath -Name 'seamsmoke' -TimeoutSeconds 480
$log=Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
$lines=$log -split '\r?\n'
$lines | Where-Object { $_ -match 'SEAMSMOKE DIGEST' } | ForEach-Object { $_ -replace '^.*SEAMSMOKE DIGEST ','' } | Set-Content -LiteralPath "$DataPath\digest.txt"
$lines | Where-Object { $_ -match 'SEAMSMOKE DIGEST (ALL|block table)' -or ($_ -match 'SEAMSMOKE|Seam pre-loading|Cylinder World: (the rain|could not|.* is off:)|\[Error\]|\[Fatal\]' -and $_ -notmatch 'SEAMSMOKE DIGEST') } | Write-Output
# a game feature that switched itself off (CW-D39: "Cylinder World: <feature> is off: ...") fails the smoke too, except
# the switch-offs the smoke provokes itself to check them (their reason names "seamsmoke")
$offLines=@($lines | Where-Object { $_ -match 'Cylinder World: .* is off:' -and $_ -notmatch 'seamsmoke' })
if ($log -notmatch 'SEAMSMOKE PASS all seam checks' -or $log -match 'SEAMSMOKE FAIL|\[Fatal\]' -or $offLines.Count -gt 0) { throw "Seam smoke test failed; data at $DataPath" }
