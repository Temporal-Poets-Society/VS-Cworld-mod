param(
    # The game folder: like the build, VINTAGE_STORY when set, else the default install
    [string]$GamePath = $(if ($env:VINTAGE_STORY) { $env:VINTAGE_STORY } else { Join-Path $env:APPDATA 'Vintagestory' }),
    # A serverconfig.json to start from. Default: the game's own defaults (--genconfig) with this test's world settings.
    [string]$TemplateConfig = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-seam-edits-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$ModZip = '',
    [string]$ModDll = '',
    [switch]$Baseline
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$GamePath = [IO.Path]::GetFullPath($GamePath)
$DataPath = [IO.Path]::GetFullPath($DataPath)
if (Test-Path -LiteralPath "$DataPath/Saves") { throw 'Use a fresh disposable DataPath.' }
New-Item -ItemType Directory -Path "$DataPath/Mods/seameditsmoke", "$DataPath/ModConfig" -Force | Out-Null
$version = (Get-Content "$root/modinfo.json" -Raw | ConvertFrom-Json).version
if (!$ModZip) { $ModZip = "$root/release/cylinderworld-$version.zip" }
if (!$ModDll) { $ModDll = "$root/bin/Release/mod/CylinderWorld.dll" }
Copy-Item -LiteralPath $ModZip -Destination "$DataPath/Mods/cylinderworld.zip"
Set-Content "$DataPath/Mods/seameditsmoke/modinfo.json" '{"type":"code","modid":"seameditsmoke","name":"Seam Edit Smoke","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0","cylinderworld":"0.11.0"}}'
$generated = !$TemplateConfig
if ($generated) {
    # The game's own defaults, written into a folder of their own (--genconfig also makes a Saves folder there)
    $gen = "$DataPath/genconfig"
    $proc = Start-Process "$GamePath/VintagestoryServer.exe" -ArgumentList @('--dataPath', ('"' + $gen + '"'), '--genconfig') -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath/genconfig-console.log" -RedirectStandardError "$DataPath/genconfig-error.log" -PassThru
    if (!$proc.WaitForExit(120000)) { Stop-Process -Id $proc.Id; throw 'Generating a default server config timed out.' }
    $TemplateConfig = "$gen/serverconfig.json"
}
$config = Get-Content -LiteralPath $TemplateConfig -Raw | ConvertFrom-Json
# A port free for TCP and UDP both (the server binds both on 127.0.0.1): Windows reserves some ranges for one protocol
# only, and the other then fails with SocketException 10013
$port = 0
for ($i = 0; $i -lt 100 -and !$port; $i++) {
    # random candidates below the dynamic range: the OS's own picks come in sequence and can all sit in one excluded range
    $candidate = Get-Random -Minimum 42000 -Maximum 48999
    $tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $candidate)
    try { $tcp.Start(); [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $candidate)).Dispose(); $port = $candidate } catch { } finally { $tcp.Stop() }
}
if (!$port) { throw 'No port is free for both TCP and UDP.' }
$config.Port = $port
$config.Ip = '127.0.0.1'; $config.Upnp = $false; $config.AdvertiseServer = $false
$config.ModPaths = @('Mods', "$DataPath/Mods")
$config.WorldConfig.WorldName = 'Disposable seam edit regression'
$config.WorldConfig.SaveFileLocation = "$DataPath/Saves/seamedits.vcdbs"
$config.WorldConfig.Seed = '4244'
if ($generated) {
    # The world this test was written for: 256000 x 102400 blocks, 384 high, survival
    $config.MapSizeX = 256000; $config.MapSizeY = 384; $config.MapSizeZ = 102400
    $config.WorldConfig.MapSizeY = 384
    $config.WorldConfig.PlayStyle = 'surviveandbuild'; $config.WorldConfig.PlayStyleLangCode = 'surviveandbuild-bands'; $config.WorldConfig.WorldType = 'standard'
    $config.WorldConfig.WorldConfiguration = [pscustomobject]@{ worldWidth = '256000'; worldLength = '102400'; worldEdge = 'blocked'; startingClimate = 'temperate'; gameMode = 'survival' }
}
$config | ConvertTo-Json -Depth 30 | Set-Content "$DataPath/serverconfig.json"
$runtime = (Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler = (Get-ChildItem 'C:/Program Files/dotnet/sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$refs = @(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll','netstandard.dll') } | ForEach-Object FullName)
$refs += @("$GamePath/VintagestoryAPI.dll", "$GamePath/VintagestoryLib.dll", "$GamePath/Mods/VSEssentials.dll", "$GamePath/Lib/0Harmony.dll", "$GamePath/Lib/protobuf-net.dll", $ModDll)
$argsFile = @('/nologo','/target:library','/langversion:12','/nostdlib+',('/out:"' + $DataPath + '/Mods/seameditsmoke/SeamEditSmoke.dll"'))
$argsFile += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$argsFile += '"' + $PSScriptRoot + '/SmokeMod.cs"'
Set-Content "$DataPath/compile.rsp" $argsFile
& dotnet "$compiler/Roslyn/bincore/csc.dll" "@$DataPath/compile.rsp"
if ($LASTEXITCODE -ne 0) { throw 'Smoke mod compilation failed.' }
foreach ($phase in @('new','reload')) {
    if ($Baseline -and $phase -eq 'reload') { break }
    if (Get-Process VintagestoryServer -ErrorAction SilentlyContinue) { throw 'Another dedicated server is running.' }
    $proc = Start-Process "$GamePath/VintagestoryServer.exe" -ArgumentList @('--dataPath', ('"' + $DataPath + '"')) -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath/$phase-console.log" -RedirectStandardError "$DataPath/$phase-error.log" -PassThru
    Write-Output "Started $phase seam edit server pid=$($proc.Id) data=$DataPath"
    if (!$proc.WaitForExit(240000)) { Stop-Process -Id $proc.Id; throw 'Smoke server timed out.' }
    Copy-Item "$DataPath/Logs/server-main.log" "$DataPath/$phase-server.log"
    $log = Get-Content "$DataPath/$phase-server.log" -Raw
    # A feature that switched itself off (a game update's missing member, a runtime failure) fails the run too
    $log -split '\r?\n' | Where-Object { $_ -match 'SEAMEDIT|Seam edits:|\[Fatal\]|Cylinder World: .* is off:' } | Write-Output
    if (!$Baseline -and ($log -notmatch 'SEAMEDIT RESULT PASS' -or $log -match 'SEAMEDIT FAIL|\[Error\]|\[Fatal\]|Cylinder World: .* is off:')) { throw "$phase regression failed; inspect $DataPath" }
    if ($Baseline -and $log -notmatch 'SEAMEDIT RESULT FAIL') { throw 'Baseline did not reproduce the failure.' }
}
