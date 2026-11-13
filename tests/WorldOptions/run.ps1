# Offline checks of the world options (CW-D48 seam mist switch, CW-D50 polarEquatorDistance) against a Release build.
# No game process; compiles Program.cs against the game's DLLs and bin\Release\mod\CylinderWorld.dll.
param(
    [string]$GamePath = $(if ($env:VINTAGE_STORY) { $env:VINTAGE_STORY } else { Join-Path $env:APPDATA 'Vintagestory' }),
    [string]$ModDll = (Join-Path $PSScriptRoot '..\..\bin\Release\mod\CylinderWorld.dll'),
    [string]$Output = (Join-Path $env:TEMP ('cylinder-world-options-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$GamePath = [IO.Path]::GetFullPath($GamePath)
$ModDll = [IO.Path]::GetFullPath($ModDll)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
Copy-Item -LiteralPath $ModDll "$Output\CylinderWorld.dll"
$runtime = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$refs = @(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll', 'netstandard.dll') } | ForEach-Object FullName)
$refs += @("$GamePath\VintagestoryAPI.dll", "$GamePath\VintagestoryLib.dll", "$GamePath\Mods\VSEssentials.dll", "$GamePath\Mods\VSSurvivalMod.dll", "$GamePath\Lib\0Harmony.dll", "$GamePath\Lib\protobuf-net.dll", "$Output\CylinderWorld.dll")
$rsp = @('/nologo', '/target:exe', '/langversion:12', '/nostdlib+', ('/out:"' + $Output + '\WorldOptionsChecks.dll"'))
$rsp += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$rsp += '"' + $PSScriptRoot + '\Program.cs"'
Set-Content "$Output\compile.rsp" $rsp
Set-Content "$Output\WorldOptionsChecks.runtimeconfig.json" '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
& dotnet "$compiler\Roslyn\bincore\csc.dll" "@$Output\compile.rsp"
if ($LASTEXITCODE -ne 0) { throw 'World option check compilation failed.' }
& dotnet "$Output\WorldOptionsChecks.dll" $GamePath $Output ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))) | Tee-Object "$Output\results.log"
if ($LASTEXITCODE -ne 0) { throw "World option checks failed; output in $Output" }
Remove-Item -LiteralPath $Output -Recurse -Force
