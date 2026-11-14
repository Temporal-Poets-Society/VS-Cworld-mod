# Offline checks of the client patches against the game's own assemblies (no client or server process).
param(
    [string]$GamePath = '',
    [string]$ModDll = '',
    [string]$Output = (Join-Path $env:TEMP ('cylinder-client-checks-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
$GamePath = [IO.Path]::GetFullPath($GamePath)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ModDll)) "$Output\CylinderWorld.dll"
Invoke-GameCompile -GamePath $GamePath -Out "$Output\ClientChecks.dll" -Sources @("$PSScriptRoot\ClientChecks.cs") -References @("$Output\CylinderWorld.dll") -Target exe
& dotnet "$Output\ClientChecks.dll" $GamePath $Output | Tee-Object "$Output\results.log"
if ($LASTEXITCODE -ne 0) { throw "Client checks failed; output in $Output" }
