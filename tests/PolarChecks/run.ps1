param(
    [string]$GamePath = "$env:APPDATA\Vintagestory",
    # An Earth (modid earth, 1.0 or 2.0) checkout: its src\Core is compiled in, its earthdata\ loaded. Default the
    # suite's Earth checkout (C:\VS mods\Earth, on Earth's trunk main); pass an Earth worktree to check a branch.
    [string]$EarthSource = '',
    # A Cylinder World DLL to compare the default layouts' geometry with (layouts 4-10 must match it exactly)
    [string]$BaselineDll = '',
    [string]$Output = (Join-Path $env:TEMP 'cylinder-polar-checks'),
    [switch]$Routes
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$EarthSource) {
    # the suite root: the first folder above this checkout with suite.json (a worktree sits deeper than the main checkout)
    $suite = $root; while ($suite -and !(Test-Path -LiteralPath (Join-Path $suite 'suite.json'))) { $suite = Split-Path $suite }
    $EarthSource = Join-Path $(if ($suite) { $suite } else { Split-Path $root }) 'Earth'
}
$earthInfo = Get-Content -LiteralPath (Join-Path $EarthSource 'modinfo.json') -Raw | ConvertFrom-Json
if ($earthInfo.modid -ne 'earth') { throw "$EarthSource is $($earthInfo.modid) $($earthInfo.version), not Earth (modid earth); pass -EarthSource with an Earth 1.0/2.0 checkout." }
if (!(Test-Path -LiteralPath (Join-Path $EarthSource 'earthdata\terrain.bin'))) { throw "$EarthSource has no earthdata\terrain.bin." }
Write-Output "Polar checks against Earth $($earthInfo.version) from $EarthSource"
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Output = [IO.Path]::GetFullPath($Output)
$runtime = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$references = @(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll','netstandard.dll') } | ForEach-Object FullName)
$references += @("$GamePath\VintagestoryAPI.dll", "$GamePath\Lib\protobuf-net.dll", "$GamePath\Lib\protobuf-net.Core.dll", "$GamePath\Lib\0Harmony.dll", "$root\bin\Release\mod\CylinderWorld.dll") | Where-Object { Test-Path -LiteralPath $_ }
$arguments = @('/nologo', '/target:exe', '/langversion:12', '/nostdlib+', ('/out:"' + $Output + '\PolarChecks.dll"'))
$arguments += $references | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += (Get-ChildItem "$EarthSource\src\Core\*.cs").FullName | ForEach-Object { '"' + $_ + '"' }
$arguments += '"' + $PSScriptRoot + '\Program.cs"'
$arguments += '"' + $PSScriptRoot + '\RouteAudit.cs"'
Set-Content -LiteralPath "$Output\compile.rsp" -Value $arguments
Set-Content -LiteralPath "$Output\PolarChecks.runtimeconfig.json" -Value '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
& dotnet "$compiler\Roslyn\bincore\csc.dll" "@$Output\compile.rsp"
if ($LASTEXITCODE -ne 0) { throw 'Polar checks did not compile.' }
# Windows PowerShell drops empty arguments to native programs, so "no baseline" is passed as '-'
& dotnet "$Output\PolarChecks.dll" $GamePath "$root\bin\Release\mod" $EarthSource $Output $(if($BaselineDll){$BaselineDll}else{'-'}) $(if($Routes){'routes'}else{'checks'})
if ($LASTEXITCODE -ne 0) { throw 'Polar checks failed.' }
