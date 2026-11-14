param(
    [string]$GamePath = '',
    # A serverconfig.json to start from. Default: the game's own defaults with the checks' world (tests\common.ps1, CW-0013)
    [string]$TemplateConfig = '',
    [string]$DataPath = (Join-Path $env:TEMP ('cylinder-newworld-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    # another build to test (e.g. the baseline, which shows the bug): its release zip and the CylinderWorld.dll inside it
    [string]$ModZip = '',
    [string]$ModDll = '',
    # expect the baseline's result: the retry start taken for an existing world (first world only)
    [switch]$Baseline
)
# A new vanilla world whose first start stops at WorldReady (as Earth does when its data can't be had), started again:
# the retry must set it up as a new world (spawn moved, story fitted), and a later start must keep that. Each start also
# renders /cyl map from the server console, and a second /cyl map at once must be refused.
# A second world plays Earth's part with its "still creating" save key (earth-creating, set at StartPre of the new save):
# the retry is new and takes a spawn by SetSpawn (moved out of the plateaus, as no Earth binds); the world then runs with
# the key left set (stuck: must stay an existing world), with it cleared, and once with an unreadable config that says
# "Enabled": false (Cylinder World must stay off and the file must stay as it is).
# Temporary local-only server; never touches the normal game data.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
if (!$GamePath) { $GamePath = Get-TestGamePath }
if (Test-Path -LiteralPath $DataPath) { throw "Use a fresh test data directory: $DataPath" }
New-Item -ItemType Directory -Path "$DataPath\Mods\newworldprobe", "$DataPath\ModConfig" -Force | Out-Null
$DataPath = [IO.Path]::GetFullPath($DataPath)
if (!$ModZip) { $ModZip = Join-Path $TestRoot "release\cylinderworld-$(Get-ModVersion).zip" }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
Assert-ZipHoldsDll $ModZip $ModDll
Copy-Item -LiteralPath $ModZip -Destination "$DataPath\Mods\cylinderworld.zip"
Set-Content -LiteralPath "$DataPath\Mods\newworldprobe\modinfo.json" -Value '{"type":"code","modid":"newworldprobe","name":"New World Probe","version":"1.0.0","side":"Server","dependencies":{"game":"1.22.0","cylinderworld":"1.0.0"}}'
$config = New-TestServerConfig -GamePath $GamePath -DataPath $DataPath -Template $TemplateConfig
$config.MapSizeX = 16384; $config.MapSizeY = 256; $config.MapSizeZ = 8192
$config.WorldConfig.MapSizeY = 256
$config.WorldConfig.WorldConfiguration.worldWidth = '16384'; $config.WorldConfig.WorldConfiguration.worldLength = '8192'
$config.WorldConfig.WorldName = 'Cylinder new world retry'
$config.WorldConfig.Seed = '4245'
Invoke-GameCompile -GamePath $GamePath -Out "$DataPath\Mods\newworldprobe\NewWorldProbe.dll" -Sources @("$PSScriptRoot\ProbeMod.cs") -References @($ModDll)
Write-Output "New world test data: $DataPath"
$cfgPath = "$DataPath\ModConfig\cylinderworld.json"
$maps = @{}
$worlds = @(
    @{ Save = 'newworld.vcdbs'; Passes = @('stop', 'retry', 'reload') },
    @{ Save = 'earthkey.vcdbs'; Passes = @('keystop', 'keyretry', 'stuck', 'cleared', 'badconfig') })
foreach ($world in $worlds) {
    if ($Baseline -and $world.Save -ne 'newworld.vcdbs') { break }
    $config.WorldConfig.SaveFileLocation = "$DataPath\Saves\$($world.Save)"
    Save-TestServerConfig $config $DataPath
    foreach ($pass in $world.Passes) {
        Set-Content -LiteralPath "$DataPath\ModConfig\newworld-pass.txt" -Value $pass
        if ($pass -eq 'badconfig') {
            # "Enabled": false next to a value of the wrong type: the whole file fails to load
            $goodConfig = Get-Content -LiteralPath $cfgPath -Raw
            $bad = $goodConfig | ConvertFrom-Json; $bad.Enabled = $false; $bad.WallDepth = '48m'
            $badConfig = $bad | ConvertTo-Json -Depth 10
            Set-Content -LiteralPath $cfgPath -Value $badConfig
        }
        if (Test-Path -LiteralPath "$DataPath\Logs\server-main.log") { Remove-Item -LiteralPath "$DataPath\Logs\server-main.log" }
        Invoke-TestServer -GamePath $GamePath -DataPath $DataPath -Name $pass -TimeoutSeconds 240 | Out-Null
        Copy-Item -LiteralPath "$DataPath\Logs\server-main.log" -Destination "$DataPath\result-$pass.log"
        $log = Get-Content -LiteralPath "$DataPath\result-$pass.log" -Raw
        $lines = $log -split '\r?\n'
        $lines | Where-Object { $_ -match 'NEWWORLD|Cylinder World( was added|: (spawn|this world|Earth''s mark|the world generator|story|at 100%)|.* is off:)|cylinderworld\.json could not be read|\[Fatal\]' } | ForEach-Object { "[$pass] $_" }
        if ($pass -in @('stop', 'keystop')) {
            if ($log -notmatch 'NEWWORLD stopping the first start' -or $log -notmatch 'Cylinder World: spawn moved') { throw "the first start did not stop at WorldReady as planned; data at $DataPath" }
            if ($pass -eq 'keystop' -and $log -notmatch 'NEWWORLD keystop: earth-creating absent at StartPre, IsNew=True') { throw "keystop did not start a new save; data at $DataPath" }
            continue
        }
        if ($log -match 'NEWWORLD map \S+ sha256 (\S+)') { $maps[$pass] = $Matches[1] }
        if ($Baseline) {
            if ($pass -eq 'retry' -and $log -notmatch 'NEWWORLD FAIL retry') { throw "the baseline did not reproduce the bug; data at $DataPath" }
            if ($pass -eq 'retry') { break }
            continue
        }
        # a game feature that switched itself off (CW-D39) fails every pass
        if ($log -notmatch 'NEWWORLD PASS all' -or $log -match 'NEWWORLD FAIL|Cylinder World was added to an existing world|\[Fatal\]|Cylinder World: .* is off:') { throw "$pass failed; data at $DataPath" }
        $expect = @{
            retry = @('stopped before anything was generated; it is set up as a new world now')
            keyretry = @('stopped before the world ran \(Earth is still creating it\); it is set up as a new world now', 'Cylinder World: spawn set by the world generator', 'the world generator''s spawn z=\d+ is within \d+ blocks of a wall')
            stuck = @('Earth''s mark that it is still creating this world is set, but the world has run with Cylinder World')
            badconfig = @('Cylinder World stays off, as its "Enabled": false says') }[$pass]
        foreach ($e in @($expect)) { if ($e -and $log -notmatch $e) { throw "$pass logged no '$e'; data at $DataPath" } }
        if ($pass -in @('reload', 'stuck', 'cleared', 'badconfig') -and $log -match 'set up as a new world') { throw "$pass was set up as a new world; data at $DataPath" }
        if ($pass -eq 'cleared' -and $log -match 'Earth''s mark') { throw "cleared still saw Earth's key; data at $DataPath" }
        if ($pass -eq 'keyretry') {
            # the story chain is fitted again after the spawn moved out of the plateaus
            $moved = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match 'the world generator''s spawn z=' })
            if (!($lines | Select-Object -Skip ($moved + 1) | Where-Object { $_ -match 'Cylinder World: (at 100% the story|story chain fits)' })) { throw "keyretry: no story fit after the spawn moved; data at $DataPath" }
        }
        if ($pass -eq 'badconfig') {
            if ((Get-Content -LiteralPath $cfgPath -Raw).Trim() -ne $badConfig.Trim()) { throw "badconfig: the unreadable config file was rewritten; data at $DataPath" }
            Set-Content -LiteralPath $cfgPath -Value $goodConfig -NoNewline
        }
    }
}
# (The two pictures may differ in a few pixels near the map centre: on the creating start the story shift removes vanilla's
# forced land around the old story spots there, which vanilla adds again on every later start.)
if (!$Baseline) { Write-Output "NEWWORLD all passes PASS; /cyl map sha256 retry $($maps['retry']), reload $($maps['reload'])" }
