# Offline checks of the world options (CW-D48 seam mist switch, CW-D50 polarEquatorDistance) and the settings saved with
# a world (CW-D47, CW-D52). No game process; compiles Program.cs against the game's DLLs and the mod's DLL.
param(
    [string]$GamePath = '',
    [string]$ModDll = '',
    [string]$Output = (Join-Path $env:TEMP ('cylinder-world-options-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
$GamePath = [IO.Path]::GetFullPath($GamePath)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ModDll)) "$Output\CylinderWorld.dll"
Invoke-GameCompile -GamePath $GamePath -Out "$Output\WorldOptionsChecks.dll" -Sources @("$PSScriptRoot\Program.cs") -References @("$Output\CylinderWorld.dll") -Target exe
& dotnet "$Output\WorldOptionsChecks.dll" $GamePath $Output $TestRoot | Tee-Object "$Output\results.log"
if ($LASTEXITCODE -ne 0) { throw "World option checks failed; output in $Output" }
Remove-Item -LiteralPath $Output -Recurse -Force
