# Offline (CW-0012): the members Earth, World Map and Sea bind by reflection, each looked up as that mod does
# (docs/REFLECTION-SURFACE.md). No game process; compiles Program.cs against the game's DLLs.
param(
    [string]$GamePath = '',
    [string]$ModDll = '',
    [string]$Output = (Join-Path $env:TEMP ('cylinder-contract-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (!$ModDll) { $ModDll = Join-Path $TestRoot 'bin\Release\mod\CylinderWorld.dll' }
$GamePath = [IO.Path]::GetFullPath($GamePath)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ModDll)) "$Output\CylinderWorld.dll"
Invoke-GameCompile -GamePath $GamePath -Out "$Output\ContractChecks.dll" -Sources @("$PSScriptRoot\Program.cs") -Target exe
# The mod is loaded from the copy by path, as the consumers find it at run time (not compiled against)
& dotnet "$Output\ContractChecks.dll" $GamePath "$Output\CylinderWorld.dll" | Tee-Object "$Output\results.log"
if ($LASTEXITCODE -ne 0) { throw "Reflection contract checks failed; output in $Output" }
Remove-Item -LiteralPath $Output -Recurse -Force
