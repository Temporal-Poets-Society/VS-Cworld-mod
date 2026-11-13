param(
    [string]$PackagePath,
    [switch]$ExpectWallLava,
    [string]$GamePath = "$env:APPDATA\Vintagestory",
    [string]$TemplateConfig = 'C:\VS mods\Earth\testserver\serverconfig.json',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-wall-lava-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$PackagePath) {
    $version = (Get-Content -LiteralPath "$root\modinfo.json" -Raw | ConvertFrom-Json).version
    $PackagePath = Join-Path $root "release\cylinderworld-$version.zip"
}
if (Test-Path -LiteralPath $DataPath) { throw "Use a fresh test data directory: $DataPath" }
New-Item -ItemType Directory -Path "$DataPath\Mods\walllavaprobe", "$DataPath\ModConfig" -Force | Out-Null
$DataPath = [IO.Path]::GetFullPath($DataPath)
Copy-Item -LiteralPath $PackagePath -Destination "$DataPath\Mods"
Set-Content -LiteralPath "$DataPath\Mods\walllavaprobe\modinfo.json" -Value '{"type":"code","modid":"walllavaprobe","name":"Wall Lava Probe","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0"}}'
@{ ExpectWallLava = [bool]$ExpectWallLava } | ConvertTo-Json | Set-Content -LiteralPath "$DataPath\ModConfig\wall-lava-probe.json"
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
$config.ModPaths = @('Mods', "$DataPath\Mods")
$config.MapSizeX = 8192; $config.MapSizeY = 256; $config.MapSizeZ = 8192
$config.WorldConfig.MapSizeY = 256
$config.WorldConfig.WorldName = 'Cylinder wall lava regression'
$config.WorldConfig.SaveFileLocation = "$DataPath\Saves\wall-lava.vcdbs"
$config.WorldConfig.Seed = '4244'
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$DataPath\serverconfig.json"
$runtime = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$compiler = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$refs = @(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll', 'netstandard.dll') } | ForEach-Object FullName)
$refs += @("$GamePath\VintagestoryAPI.dll", "$GamePath\Mods\VSEssentials.dll", "$GamePath\Lib\protobuf-net.dll", "$root\bin\Release\mod\CylinderWorld.dll")
$argsFile = @('/nologo', '/target:library', '/langversion:12', '/nostdlib+', ('/out:"' + $DataPath + '\Mods\walllavaprobe\WallLavaProbe.dll"'))
$argsFile += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$argsFile += '"' + $PSScriptRoot + '\SmokeMod.cs"'
Set-Content -LiteralPath "$DataPath\probe.rsp" -Value $argsFile
& dotnet "$compiler\Roslyn\bincore\csc.dll" "@$DataPath\probe.rsp"
if ($LASTEXITCODE -ne 0) { throw 'Wall lava probe compilation failed.' }
Write-Output "Wall lava test data: $DataPath"
$proc = Start-Process -FilePath "$GamePath\VintagestoryServer.exe" -ArgumentList @('--dataPath', ('"' + $DataPath + '"')) -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath\console.log" -RedirectStandardError "$DataPath\error.log" -PassThru
try {
    if (!$proc.WaitForExit(180000)) { throw 'Wall lava test timed out.' }
    $log = Get-Content -LiteralPath "$DataPath\Logs\server-main.log" -Raw
    $log -split '\r?\n' | Where-Object { $_ -match 'WALLLAVA|Cylinder World: .* is off:|\[Error\]|\[Fatal\]' } | Write-Output
    # a game feature that switched itself off (CW-D39) fails the check too
    if ($log -notmatch 'WALLLAVA PASS native rivulet regression' -or $log -match 'WALLLAVA FAIL|\[Fatal\]|Cylinder World: .* is off:') {
        throw "Wall lava regression failed; logs: $DataPath"
    }
}
finally {
    if (!$proc.HasExited) { Stop-Process -Id $proc.Id }
}
