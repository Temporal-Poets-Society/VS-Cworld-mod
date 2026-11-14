param(
    [string]$GamePath = '',
    # A serverconfig.json to start from. Default: the game's own defaults with the checks' world (tests\common.ps1)
    [string]$TemplateConfig = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-seam-edits-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$ModZip = '',
    [string]$ModDll = '',
    [switch]$Baseline
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
$GamePath = [IO.Path]::GetFullPath($GamePath)
$DataPath = [IO.Path]::GetFullPath($DataPath)
if (Test-Path -LiteralPath "$DataPath\Saves") { throw 'Use a fresh disposable DataPath.' }
New-Item -ItemType Directory -Path "$DataPath\Mods\seameditsmoke", "$DataPath\ModConfig" -Force | Out-Null
if (!$ModZip) { $ModZip = Join-Path $TestRoot "release\cylinderworld-$(Get-ModVersion).zip" }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
Assert-ZipHoldsDll $ModZip $ModDll
Copy-Item -LiteralPath $ModZip -Destination "$DataPath\Mods\cylinderworld.zip"
Set-Content "$DataPath\Mods\seameditsmoke\modinfo.json" '{"type":"code","modid":"seameditsmoke","name":"Seam Edit Smoke","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0","cylinderworld":"0.11.0"}}'
$config = New-TestServerConfig -GamePath $GamePath -DataPath $DataPath -Template $TemplateConfig
$config.WorldConfig.WorldName = 'Disposable seam edit regression'
$config.WorldConfig.SaveFileLocation = "$DataPath\Saves\seamedits.vcdbs"
$config.WorldConfig.Seed = '4244'
Save-TestServerConfig $config $DataPath
Invoke-GameCompile -GamePath $GamePath -Out "$DataPath\Mods\seameditsmoke\SeamEditSmoke.dll" -Sources @("$PSScriptRoot\SmokeMod.cs") -References @($ModDll)
foreach ($phase in @('new','reload')) {
    if ($Baseline -and $phase -eq 'reload') { break }
    Invoke-TestServer -GamePath $GamePath -DataPath $DataPath -Name $phase -TimeoutSeconds 240
    Copy-Item "$DataPath\Logs\server-main.log" "$DataPath\$phase-server.log"
    $log = Get-Content "$DataPath\$phase-server.log" -Raw
    # A feature that switched itself off (a game update's missing member, a runtime failure) fails the run too
    $log -split '\r?\n' | Where-Object { $_ -match 'SEAMEDIT|Seam edits:|\[Fatal\]|Cylinder World: .* is off:' } | Write-Output
    if (!$Baseline -and ($log -notmatch 'SEAMEDIT RESULT PASS' -or $log -match 'SEAMEDIT FAIL|\[Error\]|\[Fatal\]|Cylinder World: .* is off:')) { throw "$phase regression failed; inspect $DataPath" }
    if ($Baseline -and $log -notmatch 'SEAMEDIT RESULT FAIL') { throw 'Baseline did not reproduce the failure.' }
}
