# CW-D49 (CW-0005): lava already saved in the polar walls of an old world is taken out when its columns load.
# Phase 1 makes the old world with a build that still has the bug (run.ps1 -ExpectWallLava, 1.0.1 by default: its
# fixture puts native lava sources into the wall ice and the world is saved). Phase 2 opens that save with this build's
# Release zip and OldWorldProbe counts the sources in the wall columns and an ordinary-terrain control.
param(
    [string]$OldPackage = (Join-Path $PSScriptRoot '..\..\..\..\..\release\cylinderworld-1.0.1.zip'),
    [string]$PackagePath,
    [string]$GamePath = "$env:APPDATA\Vintagestory",
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-wall-lava-old-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!(Test-Path -LiteralPath $OldPackage)) { $OldPackage = Join-Path $root 'release\cylinderworld-1.0.1.zip' }
if (!(Test-Path -LiteralPath $OldPackage)) { throw "No old package with the wall-lava bug: pass -OldPackage (cylinderworld-1.0.1.zip or older)" }
if (!$PackagePath) {
    $version = (Get-Content -LiteralPath "$root\modinfo.json" -Raw | ConvertFrom-Json).version
    $PackagePath = Join-Path $root "release\cylinderworld-$version.zip"
}
& (Join-Path $PSScriptRoot 'run.ps1') -PackagePath ([IO.Path]::GetFullPath($OldPackage)) -ExpectWallLava -GamePath $GamePath -DataPath $DataPath
$DataPath = [IO.Path]::GetFullPath($DataPath)
Write-Output "Phase 1 done (old world saved with wall lava): $DataPath"

Remove-Item -LiteralPath (Join-Path $DataPath "Mods\$(Split-Path $OldPackage -Leaf)"), (Join-Path $DataPath 'Mods\walllavaprobe') -Recurse -Force
Copy-Item -LiteralPath $PackagePath -Destination "$DataPath\Mods"
New-Item -ItemType Directory -Path "$DataPath\Mods\oldworldprobe" -Force | Out-Null
Set-Content -LiteralPath "$DataPath\Mods\oldworldprobe\modinfo.json" -Value '{"type":"code","modid":"oldworldprobe","name":"Old World Wall Lava Probe","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0"}}'
$runtime = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$refs = @(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll', 'netstandard.dll') } | ForEach-Object FullName)
$refs += @("$GamePath\VintagestoryAPI.dll", "$GamePath\Lib\protobuf-net.dll")
$argsFile = @('/nologo', '/target:library', '/langversion:12', '/nostdlib+', ('/out:"' + $DataPath + '\Mods\oldworldprobe\OldWorldProbe.dll"'))
$argsFile += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$argsFile += '"' + $PSScriptRoot + '\OldWorldProbe.cs"'
Set-Content -LiteralPath "$DataPath\oldprobe.rsp" -Value $argsFile
& dotnet "$compiler\Roslyn\bincore\csc.dll" "@$DataPath\oldprobe.rsp"
if ($LASTEXITCODE -ne 0) { throw 'Old world probe compilation failed.' }

# the same save; a fresh port, picked as run.ps1 does
$config = Get-Content -LiteralPath "$DataPath\serverconfig.json" -Raw | ConvertFrom-Json
$port = 0
for ($i = 0; $i -lt 100 -and !$port; $i++) {
    $candidate = Get-Random -Minimum 42000 -Maximum 48999
    $tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $candidate)
    try { $tcp.Start(); [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $candidate)).Dispose(); $port = $candidate } catch { } finally { $tcp.Stop() }
}
if (!$port) { throw 'No port is free for both TCP and UDP.' }
$config.Port = $port
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$DataPath\serverconfig.json"
Move-Item -LiteralPath "$DataPath\Logs" "$DataPath\Logs-phase1"
$proc = Start-Process -FilePath "$GamePath\VintagestoryServer.exe" -ArgumentList @('--dataPath', ('"' + $DataPath + '"')) -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath\console2.log" -RedirectStandardError "$DataPath\error2.log" -PassThru
try {
    if (!$proc.WaitForExit(180000)) { throw 'Old world test timed out.' }
    $log = Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
    $log -split '\r?\n' | Where-Object { $_ -match 'OLDWORLD|rivulet source|Cylinder World: .* is off:|\[Error\]|\[Fatal\]' } | Write-Output
    if ($log -notmatch 'OLDWORLD PASS' -or $log -match 'OLDWORLD FAIL|\[Fatal\]|Cylinder World: .* is off:') {
        throw "Old world wall lava check failed; logs: $DataPath"
    }
}
finally {
    if (!$proc.HasExited) { Stop-Process -Id $proc.Id }
}
Remove-Item -LiteralPath $DataPath -Recurse -Force
