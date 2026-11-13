param(
    [string]$GamePath = "$env:APPDATA\Vintagestory",
    # Earth 2.0 (modid earth): its release zip; default the newest earth-*.zip in the suite's Earth\release or the
    # player's VintagestoryData\Mods
    [string]$EarthZip = '',
    # Earth 2.0 refuses a world without Hydrology (2.0.0-rc.7 needs 0.6.2-rc.4 or newer); default the newest
    # hydrology-*.zip in the suite's Hydrology\release or the player's Mods
    [string]$HydrologyZip = '',
    # Earth's data store (data files, GLO-90 tiles, ground data). Linked into the test, never copied, written or deleted:
    # Earth downloads nothing when the store is complete, and its download addresses are switched off below.
    [string]$EarthData = "$env:APPDATA\VintagestoryData-agentbridge\ModData\earth",
    # the Earth play style: earth (1:100), earth-200, earth-50, earth-25 or earth-10
    [string]$PlayStyle = 'earth',
    # another Cylinder World build to test (e.g. a baseline): its release zip and the CylinderWorld.dll inside it
    [string]$ModZip = '',
    [string]$ModDll = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-earth-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
# Headless Earth-mode check (see SmokeMod.cs) on a temporary local-only server: a new Earth world, then the same world
# started again. Never touches the normal game data. The data folder holds a junction to the Earth data store: never
# delete that folder recursively (Remove-Item -Recurse would follow the junction into the store); remove the junction
# (ModData\earth) on its own first, with [IO.Directory]::Delete on that path alone.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
# the suite root: the first folder above this checkout with suite.json (a worktree sits deeper than the main checkout)
$suite=$root; while ($suite -and !(Test-Path -LiteralPath (Join-Path $suite 'suite.json'))) { $suite=Split-Path $suite }
if (!$suite) { $suite=Split-Path $root }
# the newest <id>-<version>.zip in the given folders, by version (a pre-release sorts below its release)
function Get-NewestZip([string]$id, [string[]]$dirs) {
    $items = @(foreach ($d in $dirs) { if ($d -and (Test-Path -LiteralPath $d)) { Get-ChildItem -LiteralPath $d -Filter ($id + '-*.zip') -File } })
    $pattern = '^' + [regex]::Escape($id) + '-(\d+)\.(\d+)\.(\d+)(?:-(.+))?\.zip$'
    $best = $null; $bestKey = $null
    foreach ($f in $items) {
        if ($f.Name -notmatch $pattern) { continue }
        $key = '{0:D6}.{1:D6}.{2:D6}' -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
        if ($Matches[4]) { $key += '|0' + (($Matches[4] -split '[.-]' | ForEach-Object { if ($_ -match '^\d+$') { '0' + ([int]$_).ToString('D6') } else { '1' + $_ } }) -join '|') }
        else { $key += '|1' }
        if ($null -eq $bestKey -or [string]::CompareOrdinal($key, $bestKey) -gt 0) { $best = $f.FullName; $bestKey = $key }
    }
    $best
}
$playerMods = Join-Path $env:APPDATA 'VintagestoryData\Mods'
if (!$EarthZip) { $EarthZip = Get-NewestZip earth @((Join-Path $suite 'Earth\release'), $playerMods) }
if (!$HydrologyZip) { $HydrologyZip = Get-NewestZip hydrology @((Join-Path $suite 'Hydrology\release'), $playerMods) }
if (!$EarthZip -or !$HydrologyZip) { throw 'No Earth or Hydrology zip found: pass -EarthZip and -HydrologyZip.' }
Write-Output "Earth: $EarthZip"
Write-Output "Hydrology: $HydrologyZip"
if (Test-Path -LiteralPath $DataPath) { throw "Use a fresh test data directory: $DataPath" }
if (!(Test-Path -LiteralPath "$EarthData\data")) { throw "No Earth data store at $EarthData" }
New-Item -ItemType Directory -Path "$DataPath\Mods\polarsmoke","$DataPath\ModConfig","$DataPath\ModData" -Force | Out-Null
$DataPath=[IO.Path]::GetFullPath($DataPath)
$version=(Get-Content -LiteralPath (Join-Path $root 'modinfo.json') -Raw | ConvertFrom-Json).version
if (!$ModZip) { $ModZip = Join-Path $root "release\cylinderworld-$version.zip" }
if (!$ModDll) { $ModDll = "$root\bin\Release\mod\CylinderWorld.dll" }
Copy-Item -LiteralPath $ModZip -Destination "$DataPath\Mods\cylinderworld.zip"
Copy-Item -LiteralPath $EarthZip -Destination "$DataPath\Mods\earth.zip"
Copy-Item -LiteralPath $HydrologyZip -Destination "$DataPath\Mods"
New-Item -ItemType Junction -Path "$DataPath\ModData\earth" -Target $EarthData | Out-Null
Set-Content -LiteralPath "$DataPath\Mods\polarsmoke\modinfo.json" -Value '{"type":"code","modid":"polarsmoke","name":"Polar Smoke Test","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0","cylinderworld":"1.0.0"}}'
# The play style's own world settings, as the world creation screen would give them (Earth's worldconfig.json)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($EarthZip)
try { $reader=[IO.StreamReader]::new($zip.GetEntry('worldconfig.json').Open()); $styles=($reader.ReadToEnd() | ConvertFrom-Json).playStyles; $reader.Dispose() } finally { $zip.Dispose() }
$style=$styles | Where-Object { $_.code -eq $PlayStyle } | Select-Object -First 1
if (!$style) { throw "Earth has no play style '$PlayStyle'" }
$wc=$style.worldConfig
$mapY=[int]$wc.earthWorldHeight
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
# Earth sets the world's width and length from its size option before the map is built
$config=[ordered]@{ ServerName='Cylinder World Earth polar smoke'; Ip='127.0.0.1'; Port=$port; Upnp=$false; AdvertiseServer=$false; MaxClients=1
    DefaultRoleCode='crplayer'; Roles=@(@{ Code='crplayer'; Name='Test player'; PrivilegeLevel=100; DefaultGameMode=2; Color='Green'; Privileges=@('chat','build','useblock','controlserver') })
    ModPaths=@('Mods',"$DataPath\Mods"); MapSizeX=[int]$wc.earthWorldSize; MapSizeY=$mapY; MapSizeZ=[int]([int]$wc.earthWorldSize*2/5)
    WorldConfig=[ordered]@{ WorldName='Cylinder Earth polar smoke test'; Seed='4242'; SaveFileLocation="$DataPath\Saves\polar.vcdbs"; MapSizeY=$mapY
        WorldType='standard'; PlayStyle=$PlayStyle; WorldConfiguration=$wc } }
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$DataPath\serverconfig.json"
Set-Content -LiteralPath "$DataPath\ModConfig\cylinderworld.json" -Value '{"PolarIceMode":"earth"}'
# No downloads: every address Earth would fetch from points at a closed local port
$off='http://127.0.0.1:9/'
[ordered]@{ ConfigVersion=5; SelfTest=@{ Enabled=$false }; DataRepo=$off; ElevationRepo=$off; ElevationSource=$off; GroundRepo=$off } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$DataPath\ModConfig\earth.json"
$runtime=(Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler=(Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$refs=@(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll','netstandard.dll') } | ForEach-Object FullName)
$refs+=@("$GamePath\VintagestoryAPI.dll","$GamePath\Mods\VSEssentials.dll","$GamePath\Mods\VSSurvivalMod.dll","$GamePath\Lib\0Harmony.dll",$ModDll)
$argsFile=@('/nologo','/target:library','/langversion:12','/nostdlib+',('/out:"'+$DataPath+'\Mods\polarsmoke\PolarSmoke.dll"'))
$argsFile+=$refs | ForEach-Object { '/reference:"'+$_+'"' }
$argsFile+='"'+$PSScriptRoot+'\SmokeMod.cs"'
Set-Content -LiteralPath "$DataPath\smoke.rsp" -Value $argsFile
& dotnet "$compiler\Roslyn\bincore\csc.dll" "@$DataPath\smoke.rsp"
if ($LASTEXITCODE -ne 0) { throw 'Smoke mod compilation failed.' }
Write-Output "Smoke data: $DataPath"
foreach ($pass in @('new','reload'))
{
    if ($pass -eq 'reload') {
        $saved=Get-Content -LiteralPath "$DataPath\ModConfig\cylinderworld.json" -Raw | ConvertFrom-Json
        $saved.PolarIceMode='default'
        $saved | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$DataPath\ModConfig\cylinderworld.json"
    }
    if (Test-Path -LiteralPath "$DataPath\Logs\server-main.log") { Remove-Item -LiteralPath "$DataPath\Logs\server-main.log" }
    $proc=Start-Process -FilePath "$GamePath\VintagestoryServer.exe" -ArgumentList @('--dataPath',('"'+$DataPath+'"')) -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath\console-$pass.log" -RedirectStandardError "$DataPath\error-$pass.log" -PassThru
    Write-Output "Started $pass smoke test, pid $($proc.Id)"
    if (-not $proc.WaitForExit(1200000)) { Stop-Process -Id $proc.Id; throw "Smoke server timed out; data at $DataPath" }
    $log=Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
    Copy-Item -LiteralPath "$DataPath\Logs\server-main.log" -Destination "$DataPath\result-$pass.log"
    $log -split '\r?\n' | Where-Object { $_ -match 'POLARSMOKE (PASS|FAIL|Arctic chunk|bound|map)|Earth polar mode|Cylinder World: (spawn|story|this world|.* is off:)|\[Error\]|\[Fatal\]' } | Write-Output
    # a game feature that switched itself off (CW-D39: "Cylinder World: <feature> is off: ...") fails the smoke too
    if ($log -notmatch 'POLARSMOKE PASS all generated terrain checks' -or $log -match 'POLARSMOKE FAIL|\[Fatal\]|Cylinder World: .* is off:') { throw "Smoke test $pass failed; data at $DataPath" }
    # Earth hands its spawn to Cylinder World only while creating the world; a reload is never set up as new
    if ($pass -eq 'new' -and $log -notmatch 'Cylinder World: spawn set by the world generator') { throw "Earth's spawn was not taken on the new start; data at $DataPath" }
    if ($pass -eq 'reload' -and $log -match 'Cylinder World: spawn set by the world generator|set up as a new world') { throw "the reload was set up as a new world; data at $DataPath" }
}
