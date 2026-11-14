param(
    [string]$GamePath = '',
    # An Earth (modid earth, 1.0 or 2.0) checkout: its src\Core is compiled in, its earthdata\ loaded. Default the
    # suite's Earth checkout (C:\VS mods\Earth, on Earth's trunk main); pass an Earth worktree to check a branch.
    [string]$EarthSource = '',
    # The Cylinder World build to check (default this checkout's Release build), and one to compare the default layouts'
    # geometry with (layouts 4-10 must match it exactly)
    [string]$ModDll = '',
    [string]$BaselineDll = '',
    [string]$Output = (Join-Path $env:TEMP 'cylinder-polar-checks'),
    [switch]$Routes
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
if (!$EarthSource) {
    # the suite root: the first folder above this checkout with suite.json (a worktree sits deeper than the main checkout)
    $suite = $TestRoot; while ($suite -and !(Test-Path -LiteralPath (Join-Path $suite 'suite.json'))) { $suite = Split-Path $suite }
    $EarthSource = Join-Path $(if ($suite) { $suite } else { Split-Path $TestRoot }) 'Earth'
}
$earthInfo = Get-Content -LiteralPath (Join-Path $EarthSource 'modinfo.json') -Raw | ConvertFrom-Json
if ($earthInfo.modid -ne 'earth') { throw "$EarthSource is $($earthInfo.modid) $($earthInfo.version), not Earth (modid earth); pass -EarthSource with an Earth 1.0/2.0 checkout." }
if (!(Test-Path -LiteralPath (Join-Path $EarthSource 'earthdata\terrain.bin'))) { throw "$EarthSource has no earthdata\terrain.bin." }
Write-Output "Polar checks against Earth $($earthInfo.version) from $EarthSource"
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Output = [IO.Path]::GetFullPath($Output)
$mod = Join-Path $Output 'mod'
New-Item -ItemType Directory -Path $mod -Force | Out-Null
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ModDll)) "$mod\CylinderWorld.dll"
$sources = @((Get-ChildItem "$EarthSource\src\Core\*.cs").FullName) + @("$PSScriptRoot\Program.cs", "$PSScriptRoot\RouteAudit.cs")
Invoke-GameCompile -GamePath $GamePath -Out "$Output\PolarChecks.dll" -Sources $sources -References @("$mod\CylinderWorld.dll") -Target exe
# Windows PowerShell drops empty arguments to native programs, so "no baseline" is passed as '-'
& dotnet "$Output\PolarChecks.dll" $GamePath $mod $EarthSource $Output $(if($BaselineDll){$BaselineDll}else{'-'}) $(if($Routes){'routes'}else{'checks'})
if ($LASTEXITCODE -ne 0) { throw 'Polar checks failed.' }
