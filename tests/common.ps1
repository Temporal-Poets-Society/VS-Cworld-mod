# Shared by the test scripts (CW-0013, CW-0017). Dot-source it: . (Join-Path $PSScriptRoot '..\common.ps1')

# The game folder: like the build, VINTAGE_STORY when set, else the default install
function Get-TestGamePath { if ($env:VINTAGE_STORY) { $env:VINTAGE_STORY } else { Join-Path $env:APPDATA 'Vintagestory' } }

# This checkout's folder (the parent of tests\) and its modinfo version
$TestRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Get-ModVersion { (Get-Content -LiteralPath (Join-Path $TestRoot 'modinfo.json') -Raw | ConvertFrom-Json).version }

# A port free for TCP and UDP both (the server binds both on 127.0.0.1): Windows reserves some ranges for one protocol
# only, and the other then fails with SocketException 10013
function Get-FreePort {
    for ($i = 0; $i -lt 100; $i++) {
        # random candidates below the dynamic range: the OS's own picks come in sequence and can all sit in one excluded range
        $candidate = Get-Random -Minimum 42000 -Maximum 48999
        $tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $candidate)
        try { $tcp.Start(); [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $candidate)).Dispose(); return $candidate } catch { } finally { $tcp.Stop() }
    }
    throw 'No port is free for both TCP and UDP.'
}

<#
The server config a check starts from, local-only (127.0.0.1, a free port, no UPnP, not advertised, ModPaths with
<DataPath>\Mods). With -Template, that file. Without, the game's own defaults (--genconfig, written into
<DataPath>\genconfig) with the world these checks were written for: 256,000 x 102,400 blocks, 384 high, the
survival play style, a standard world, blocked edges, temperate start. That is the world Earth's untracked
testserver\serverconfig.json set, which the checks used to start from (CW-0013); the rest of that file was the game's
defaults. Each script then sets its own world name, seed, save file and any other size.
#>
function New-TestServerConfig {
    param([string]$GamePath, [string]$DataPath, [string]$Template = '')
    if (!$Template) {
        $gen = Join-Path $DataPath 'genconfig'
        $proc = Start-Process "$GamePath\VintagestoryServer.exe" -ArgumentList @('--dataPath', ('"' + $gen + '"'), '--genconfig') -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath\genconfig-console.log" -RedirectStandardError "$DataPath\genconfig-error.log" -PassThru
        if (!$proc.WaitForExit(120000)) { Stop-Process -Id $proc.Id; throw 'Generating a default server config timed out.' }
        $Template = Join-Path $gen 'serverconfig.json'
        if (!(Test-Path -LiteralPath $Template)) { throw "The server wrote no default config; see $DataPath\genconfig-console.log" }
        $config = Get-Content -LiteralPath $Template -Raw | ConvertFrom-Json
        $config.MapSizeX = 256000; $config.MapSizeY = 384; $config.MapSizeZ = 102400
        $config.WorldConfig.MapSizeY = 384
        $config.WorldConfig.PlayStyle = 'surviveandbuild'; $config.WorldConfig.PlayStyleLangCode = 'surviveandbuild-bands'; $config.WorldConfig.WorldType = 'standard'
        $config.WorldConfig.WorldConfiguration = [pscustomobject]@{ worldWidth = '256000'; worldLength = '102400'; worldEdge = 'blocked'; startingClimate = 'temperate'; gameMode = 'survival' }
    }
    else { $config = Get-Content -LiteralPath $Template -Raw | ConvertFrom-Json }
    $config.Port = Get-FreePort
    $config.Ip = '127.0.0.1'; $config.Upnp = $false; $config.AdvertiseServer = $false
    $config.ModPaths = @('Mods', "$DataPath\Mods")
    return $config
}

<#
The zip a server check installs must hold the DLL its probe compiles against (re-review of CW-0016/CW-0017): a Release
build refreshes bin\Release but keeps an existing zip of the same version, so without this a check could run an older
zip and report on the newer build, silently for the terrain digest. Byte-equal or it throws.
#>
function Assert-ZipHoldsDll([string]$ModZip, [string]$ModDll) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ModZip))
    try {
        $entry = $zip.GetEntry('CylinderWorld.dll')
        if (!$entry) { throw "$ModZip holds no CylinderWorld.dll" }
        $buffer = New-Object IO.MemoryStream
        $stream = $entry.Open(); try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
    $inZip = $buffer.ToArray()
    $built = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ModDll))
    if ($inZip.Length -ne $built.Length -or [Convert]::ToBase64String($inZip) -ne [Convert]::ToBase64String($built)) {
        throw "$ModZip does not hold $ModDll (a stale zip?): rebuild with -p:Overwrite=true, or pass the zip and DLL of one build."
    }
}

function Save-TestServerConfig([object]$Config, [string]$DataPath) {
    $Config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$DataPath\serverconfig.json"
}

<#
Compiles C# sources against the game's assemblies with the SDK's compiler, no project. The references are the .NET
runtime's, the game's API, engine, essentials and survival assemblies, Harmony and protobuf-net, and -References (the
mod's DLL, a probe's own). An exe also gets the runtimeconfig to run it with dotnet.
#>
function Invoke-GameCompile {
    param([string]$GamePath, [string]$Out, [string[]]$Sources, [string[]]$References = @(), [ValidateSet('library', 'exe')][string]$Target = 'library')
    $runtime = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
    $compiler = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -Filter '8.*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
    if (!$runtime -or !$compiler) { throw 'Needs the .NET 10 runtime and an 8.x SDK under C:\Program Files\dotnet.' }
    $refs = @(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' | Where-Object { $_.Name -like 'System.*' -and $_.Name -notlike '*.Native.dll' -or $_.Name -in @('mscorlib.dll', 'netstandard.dll') } | ForEach-Object FullName)
    $refs += @("$GamePath\VintagestoryAPI.dll", "$GamePath\VintagestoryLib.dll", "$GamePath\Mods\VSEssentials.dll", "$GamePath\Mods\VSSurvivalMod.dll",
        "$GamePath\Lib\0Harmony.dll", "$GamePath\Lib\protobuf-net.dll", "$GamePath\Lib\protobuf-net.Core.dll") | Where-Object { Test-Path -LiteralPath $_ }
    $refs += $References
    $rsp = @('/nologo', "/target:$Target", '/langversion:12', '/nostdlib+', ('/out:"' + $Out + '"'))
    $rsp += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
    $rsp += $Sources | ForEach-Object { '"' + $_ + '"' }
    $rspFile = [IO.Path]::ChangeExtension($Out, '.rsp')
    Set-Content -LiteralPath $rspFile -Value $rsp
    & dotnet "$compiler\Roslyn\bincore\csc.dll" "@$rspFile"
    if ($LASTEXITCODE -ne 0) { throw "Compiling $(Split-Path $Out -Leaf) failed." }
    if ($Target -eq 'exe') {
        Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Out, '.runtimeconfig.json')) -Value '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
    }
}

<#
Starts a server on DataPath and waits for it to stop by itself (the probe mods shut it down). Other servers may run on
this machine (other checks, other mods' tests): each has its own data folder and port. A server that runs past
-TimeoutSeconds is stopped (only this one, by its pid) and the check fails. Console output goes to <Name>-console.log.
#>
function Invoke-TestServer {
    param([string]$GamePath, [string]$DataPath, [string]$Name, [int]$TimeoutSeconds)
    $proc = Start-Process -FilePath "$GamePath\VintagestoryServer.exe" -ArgumentList @('--dataPath', ('"' + $DataPath + '"')) -WorkingDirectory $GamePath -WindowStyle Hidden -RedirectStandardOutput "$DataPath\$Name-console.log" -RedirectStandardError "$DataPath\$Name-error.log" -PassThru
    Write-Output "Started $Name server, pid $($proc.Id), data $DataPath"
    if (!$proc.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue
        throw "$Name server timed out after $TimeoutSeconds s; data at $DataPath"
    }
}
