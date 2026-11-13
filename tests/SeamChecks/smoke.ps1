param(
    [string]$GamePath = "$env:APPDATA\Vintagestory",
    [string]$TemplateConfig = 'C:\VS mods\Earth\testserver\serverconfig.json',
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
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
New-Item -ItemType Directory -Path "$DataPath\Mods\seamsmoke","$DataPath\ModConfig" -Force | Out-Null
$DataPath=[IO.Path]::GetFullPath($DataPath)
$version=(Get-Content -LiteralPath (Join-Path $root 'modinfo.json') -Raw | ConvertFrom-Json).version
if (!$ModZip) { $ModZip = Join-Path $root "release\cylinderworld-$version.zip" }
if (!$ModDll) { $ModDll = "$root\bin\Release\mod\CylinderWorld.dll" }
Copy-Item -LiteralPath $ModZip -Destination "$DataPath\Mods\cylinderworld.zip"
if ($DumpColumns) { Set-Content -LiteralPath "$DataPath\ModConfig\seamsmoke-dump.txt" -Value $DumpColumns }
Set-Content -LiteralPath "$DataPath\Mods\seamsmoke\modinfo.json" -Value '{"type":"code","modid":"seamsmoke","name":"Seam Smoke Test","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0","cylinderworld":"1.0.0"}}'
$config=Get-Content -LiteralPath $TemplateConfig -Raw | ConvertFrom-Json
# A port free for TCP and UDP both (the server binds both on 127.0.0.1): Windows reserves some ranges for one protocol
# only, and the other then fails with SocketException 10013
$port=0
for ($i=0; $i -lt 100 -and !$port; $i++) {
    # random candidates below the dynamic range: the OS's own picks come in sequence and can all sit in one excluded range
    $candidate=Get-Random -Minimum 42000 -Maximum 48999
    $tcp=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$candidate)
    try { $tcp.Start(); [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback,$candidate)).Dispose(); $port=$candidate } catch { } finally { $tcp.Stop() }
}
if (!$port) { throw 'No port is free for both TCP and UDP.' }
$config.Port=$port
$config.Ip='127.0.0.1'; $config.Upnp=$false; $config.AdvertiseServer=$false
$config.ModPaths=@('Mods',"$DataPath\Mods")
$config.WorldConfig.WorldName='Cylinder seam smoke test'
$config.WorldConfig.SaveFileLocation="$DataPath\Saves\seam.vcdbs"
$config.WorldConfig.Seed='4244'
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$DataPath\serverconfig.json"
$runtime=(Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler=(Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$refs=@(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll','netstandard.dll') } | ForEach-Object FullName)
$refs+=@("$GamePath\VintagestoryAPI.dll","$GamePath\Mods\VSEssentials.dll","$GamePath\Lib\0Harmony.dll","$GamePath\Lib\protobuf-net.dll",$ModDll)
$argsFile=@('/nologo','/target:library','/langversion:12','/nostdlib+',('/out:"'+$DataPath+'\Mods\seamsmoke\SeamSmoke.dll"'))
$argsFile+=$refs | ForEach-Object { '/reference:"'+$_+'"' }
$argsFile+='"'+$PSScriptRoot+'\SeamSmoke.cs"'
Set-Content -LiteralPath "$DataPath\smoke.rsp" -Value $argsFile
& dotnet "$compiler\Roslyn\bincore\csc.dll" "@$DataPath\smoke.rsp"
if ($LASTEXITCODE -ne 0) { throw 'Seam smoke mod compilation failed.' }
Write-Output "Smoke data: $DataPath"
$proc=Start-Process -FilePath "$GamePath\VintagestoryServer.exe" -ArgumentList @('--dataPath',('"'+$DataPath+'"')) -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath\console.log" -RedirectStandardError "$DataPath\error.log" -PassThru
Write-Output "Started seam smoke test, pid $($proc.Id)"
if (-not $proc.WaitForExit(480000)) { Stop-Process -Id $proc.Id; throw "Seam smoke server timed out; data at $DataPath" }
$log=Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
$lines=$log -split '\r?\n'
$lines | Where-Object { $_ -match 'SEAMSMOKE DIGEST' } | ForEach-Object { $_ -replace '^.*SEAMSMOKE DIGEST ','' } | Set-Content -LiteralPath "$DataPath\digest.txt"
$lines | Where-Object { $_ -match 'SEAMSMOKE DIGEST (ALL|block table)' -or ($_ -match 'SEAMSMOKE|Seam pre-loading|Cylinder World: (the rain|could not|.* is off:)|\[Error\]|\[Fatal\]' -and $_ -notmatch 'SEAMSMOKE DIGEST') } | Write-Output
# a game feature that switched itself off (CW-D39: "Cylinder World: <feature> is off: ...") fails the smoke too, except
# the switch-offs the smoke provokes itself to check them (their reason names "seamsmoke")
$offLines=@($lines | Where-Object { $_ -match 'Cylinder World: .* is off:' -and $_ -notmatch 'seamsmoke' })
if ($log -notmatch 'SEAMSMOKE PASS all seam checks' -or $log -match 'SEAMSMOKE FAIL|\[Fatal\]' -or $offLines.Count -gt 0) { throw "Seam smoke test failed; data at $DataPath" }
