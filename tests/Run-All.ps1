# Every check of this mod in one run (CW-0017): the offline checks first, then the server checks, one line each and a
# summary; the exit code is the number of failed checks. Needs a Release build (its zip and DLL), or -ModZip and -ModDll.
#   tests\Run-All.ps1                  offline and server checks
#   tests\Run-All.ps1 -Offline         offline only (seconds)
#   tests\Run-All.ps1 -Earth           also the Earth polar smoke (an Earth world, several minutes)
#   tests\Run-All.ps1 -OldWorld        also the 1.0.1 world's wall lava (needs release\cylinderworld-1.0.1.zip)
# Each check's output goes to <Output>\<check>.log; its data folder stays in <Output>\<check> when it fails.
param(
    [string]$ModZip = '',
    [string]$ModDll = '',
    [string]$Output = (Join-Path $env:TEMP ('cylinder-checks-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [switch]$Offline,
    [switch]$Earth,
    [switch]$OldWorld
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if (!$ModZip) { $ModZip = Join-Path $TestRoot "release\cylinderworld-$(Get-ModVersion).zip" }
if (!$ModDll) { $ModDll = "$TestRoot\bin\Release\mod\CylinderWorld.dll" }
$ModZip = [IO.Path]::GetFullPath($ModZip); $ModDll = [IO.Path]::GetFullPath($ModDll)
foreach ($f in @($ModDll) + $(if ($Offline) { @() } else { @($ModZip) })) { if (!(Test-Path -LiteralPath $f)) { throw "No ${f}: make a Release build first, or pass -ModZip and -ModDll." } }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Output = [IO.Path]::GetFullPath($Output)
$checks = @(
    @{ Name = 'WorldOptions'; Script = 'WorldOptions\run.ps1'; Args = @{ ModDll = $ModDll; Output = "$Output\WorldOptions" } },
    @{ Name = 'Contract'; Script = 'Contract\run.ps1'; Args = @{ ModDll = $ModDll; Output = "$Output\Contract" } },
    @{ Name = 'ClientChecks'; Script = 'SeamEdits\client.ps1'; Args = @{ ModDll = $ModDll; Output = "$Output\ClientChecks" } },
    @{ Name = 'PolarChecks'; Script = 'PolarChecks\run.ps1'; Args = @{ ModDll = $ModDll; Output = "$Output\PolarChecks" } })
if (!$Offline) {
    $checks += @(
        @{ Name = 'SeamEdits'; Script = 'SeamEdits\smoke.ps1'; Args = @{ ModZip = $ModZip; ModDll = $ModDll; DataPath = "$Output\SeamEdits" } },
        @{ Name = 'SeamChecks'; Script = 'SeamChecks\smoke.ps1'; Args = @{ ModZip = $ModZip; ModDll = $ModDll; DataPath = "$Output\SeamChecks" } },
        @{ Name = 'NewWorldChecks'; Script = 'NewWorldChecks\run.ps1'; Args = @{ ModZip = $ModZip; ModDll = $ModDll; DataPath = "$Output\NewWorldChecks" } },
        @{ Name = 'WallLavaChecks'; Script = 'WallLavaChecks\run.ps1'; Args = @{ PackagePath = $ModZip; ModDll = $ModDll; DataPath = "$Output\WallLavaChecks" } })
    if ($OldWorld) { $checks += @{ Name = 'WallLavaOldWorld'; Script = 'WallLavaChecks\oldworld.ps1'; Args = @{ PackagePath = $ModZip; ModDll = $ModDll; DataPath = "$Output\WallLavaOldWorld" } } }
    if ($Earth) { $checks += @{ Name = 'PolarSmoke'; Script = 'PolarChecks\smoke.ps1'; Args = @{ ModZip = $ModZip; ModDll = $ModDll; DataPath = "$Output\PolarSmoke" } } }
}
$failed = 0
foreach ($check in $checks) {
    $start = Get-Date
    $log = "$Output\$($check.Name).log"
    $a = $check.Args
    try {
        & (Join-Path $PSScriptRoot $check.Script) @a *>&1 | Out-File -LiteralPath $log -Encoding utf8
        $result = 'PASS'
        if ($a.DataPath -and (Test-Path -LiteralPath $a.DataPath)) {
            # the terrain digest is the release evidence (D58): kept beside the logs
            if (Test-Path -LiteralPath "$($a.DataPath)\digest.txt") { Copy-Item -LiteralPath "$($a.DataPath)\digest.txt" "$Output\$($check.Name)-digest.txt" }
            # a passing server check's data is disposable; the polar smoke's holds a junction to the Earth store, which
            # goes first on its own (removing the folder with it would follow the link into the store)
            $junction = Join-Path $a.DataPath 'ModData\earth'
            if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
            if (!(Test-Path -LiteralPath $junction)) { Remove-Item -LiteralPath $a.DataPath -Recurse -Force }
        }
    }
    catch { $result = 'FAIL'; $failed++; Add-Content -LiteralPath $log -Value "THREW: $_" }
    Write-Host ('{0,-17} {1}  {2,4} s  {3}' -f $check.Name, $result, [int]((Get-Date) - $start).TotalSeconds, $log)
}
Write-Host "Cylinder World checks: $($checks.Count - $failed) passed, $failed failed (build $ModDll). Logs in $Output"
exit $failed
