# Seam issue #32: runtime fixes, 30 October 2026

Source: `codex/seam-edits-jerk-2026-10-30`, based on `307bd3c` (playtest.4). Result: **0.11.15-playtest.5**, uncommitted.
Decisions: CW-D35 and CW-D36. No client launch, install, push, player-data access or layout migration.

## Jump and one-frame-later snap

The saved circumference is `WorldWidth - 2 * JumpInset`, with integer inputs. Default insets are multiples of 256;
the existing save's geometry is not recalculated. Fractional player coordinates are retained. Alignment/rounding
does not account for the demonstrated failure.

Engine evidence comes from the coordinator-staged 1.22.7 assemblies, decompiled inside `.local/decompiled/`:

- `PlayerCamera.OnPlayerPhysicsTick` copies the supplied previous position into the camera buffer.
- `EntityBehaviorPlayerPhysics.OnRenderFrame` runs at render order 1, after the camera (0), and calls the physics
  watcher with its own retained `EntityBehaviorControlledPhysics.prevPos` even when its 1/60 s accumulator has not
  produced another physics step. Cworld's old render-order -0.2 camera shift was therefore overwritten with old-lap
  history for the next frame. At high frame rates that step can be absent, explaining intermittence.
- `EntityPlayer.OnReceivedServerPacket(1)` relocates the mount before calling the base `Entity` handler. CW-D33
  patched only the base handler, so mounted movement already took the older server coordinates before the fix ran.
- `SystemNetworkProcess.HandleSinglePacket(Packet_EntityPosition)` calls `EntityPosExtensions.SetFromPacket`, which
  overwrites position, motion and orientation. A late companion teleport could undo the preceding local shift.
  It checks packet ticks, but does not reject an older position version. Reliable teleport packets and UDP correction
  packets can arrive in either order.
- `EntityPlayer.Onplrteleported` advances `positionVersionNumber`; `Entity.TeleportToDouble`, used for mounts, does
  not. `ServerUdpNetwork.HandleMountPosition` rejects old versions before applying coordinates; absent a new version,
  an old-lap mount input instead reaches its distance rejection and produces a correction packet.

`SeamSmoothJump` now intercepts the player override first and translates the local rider/carrier plus physics,
falling and interpolation history. A matching companion packet keeps current local coordinates, motion and angles;
obsolete ticks do not trigger a translation, and obsolete position versions cannot restore the old lap. The server
advances the mount version at wrap completion. Normal current-version corrections still run.

## Block edits and saved counterparts

`SeamWorldGen` produces distinct physical chunks with matching initial surfaces. There is no chunk-storage alias or
client mirror renderer. `SeamPreloader` sends and retains both physical sides and avoids resending chunks the client
already holds. Playtest.4 has no runtime counterpart mutation path, so an edit changes only one server chunk.
Crossing exposes the other's unchanged saved data; repeated crossings do not repair it.

`SeamBlocks` observes the engine's normal mutation paths: `SetBlockInternal`, `ExchangeBlock`, `MarkBlockModified`,
minimal bulk updates, decoration dirty notifications and block-entity dirty notifications. Each snapshot contains
both solid/fluid layers, cell decorations, BE class/attributes and collectible mappings. Logical coordinates are
integer block coordinates; pairing does not require a chunk-aligned circumference. Other dimensions are excluded.

The server writes the counterpart's state without executing placement/break callbacks again. This avoids duplicate
item consumption, drops and multiblock placement. It updates rain heights, lighting, save dirtiness and ordinary
block/BE packets. Native sends target every connection that holds that physical chunk, including the kept side.
Existing BE instances are updated through their normal attribute loader. Registered BE listeners run on one loaded
copy; random ticks suppress a replica only if the canonical cell is also within a player's native tick range.

The durable world-save key `cylinderworld-seam-edits-v1` retains the latest changed-cell state. Unloaded targets are
replayed when they become available, including newly generated targets. Native `ChunkColumnLoaded` fires **before**
publication in `loadedChunks`; replaying there misses the target. The fix instead runs after
`ServerSystemSupplyChunks.mainThreadLoadChunkColumn`, before completion callbacks, suppressing intermediate dirty
notifications during native BE initialization. The load test caught this ordering dependency.

No untouched chunk is replaced or regenerated. Older builds recorded no edit history, so old divergent cells cannot
be safely ordered as "placement" versus "later removal". This build preserves them until a new mutation supplies
authoritative state. Re-place/re-break an affected old cell to reconcile it; do not delete saved chunks to repair it.
BE implementations that store arbitrary linked absolute positions, bypass dirty notifications or register their own
external ticking protocol need a specific adapter; the generic path preserves normal serialized BE state.

## Water/fog flicker (#10)

The native camera resets its water reference after a 20,000-block displacement, and its foam reference after 200
blocks. With old-lap physics history, an interpolated half-ring excursion readily exceeds both thresholds and also
moves the render/culling viewpoint. The offline camera-history test covers this shared failure path. Whether it is
the entire cause of the reported water/fog flicker remains unproven without the user's visual test. No speculative
shader, fog-density or world-ambient change is included.

## Verification

Changed files: `src/SeamSmoothJump.cs` (client transaction/history), `src/SeamWrapSystem.cs` (mount version),
`src/SeamBlocks.cs` (new server replication/journal), `tests/SeamEdits/` (native-handler and server regressions),
`CylinderWorld.csproj` (game-library reference and local-source exclusion), `modinfo.json` (version), plus README,
DECISIONS, seam-transition, review-followups and seam-notes-other-mods documentation. The main checkout is untouched.

All game inputs are copied under `.local/game/`; server configs and disposable saves/logs remain under `.local/`.
`tests/SeamEdits/client.ps1` creates API fakes and executes the native packet/physics-camera methods without starting
a game client, window, renderer or server. `tests/SeamEdits/smoke.ps1` starts one dedicated server, waits for its exit,
then reloads the same disposable save in a second process. It refuses to start while another server is running.

| Check | Playtest.4 baseline | Playtest.5 |
| --- | --- | --- |
| Offline native packet/camera checks, both directions and both packet orders | 45 failures / 65 checks | 65 / 65 pass |
| Dedicated server edit tests | 14 failures / 15 executed checks; missing replica BE prevents dependent inventory checks | 20 / 20 pass on a fresh world |
| Reload, single container drop and previously ungenerated target | Not attempted after baseline failure | 10 / 10 pass |
| Original seam smoke (terrain, landing, preload, weather/cloud continuity) | Pass | Pass; 0 / 98,863 mirrored terrain cells differ |
| Release build | Builds | Zero warnings/errors |

Logs: `.local/client-baseline/results.log`, `.local/client-checks/results.log`,
`.local/edit-baseline/new-server.log`, `.local/edit-release/new-server.log`,
`.local/edit-release/reload-server.log` and `.local/seam-final/Logs/server-main.log`. Servers run and exit sequentially. Native network checks use two simulated
connections with both physical chunks in their sent sets, intercepting the resulting native packets before socket
I/O; these are not a substitute for two real graphical clients.

Package: `release/cylinderworld-0.11.15-playtest.5.zip`, SHA-256
`8e11ffa0f5c1f8872c1f7839733261b5094acc580ef2e852ad0b7cd8661cfd77`.
Test combination: coordinator-staged Vintage Story 1.22.7 and Cworld only, plus test mods. The reported Earth/Sea/Wmap/
Hydrology/AstraTerra/Hansa combination is reserved for the in-game acceptance checks below. No package was installed.

The first persistence fixture used unsupported floating raw rock; vanilla's cave-in behavior removed it before
save, and the journal correctly kept the resulting air. The final fixture uses ordinary plank blocks, so it tests
edit persistence without that unrelated physics event. The initial load test also found and fixed the pre-publication
event ordering described above. Final server runs reject unexpected engine error/fatal lines as well as assertions.

Run from this worktree, with the coordinator-staged inputs:

```powershell
tests/SeamEdits/client.ps1
tests/SeamEdits/smoke.ps1 -DataPath "$PWD/.local/seam-edits-fresh"
tests/SeamChecks/smoke.ps1 -GamePath "$PWD/.local/game" -TemplateConfig "$PWD/.local/serverconfig.json" -DataPath "$PWD/.local/seam-fresh"
```

For a before/after comparison, `client.ps1 -ModDll <baseline DLL> -Output <separate output>` deliberately fails the
same assertions on the old build. `smoke.ps1 -Baseline -ModZip <baseline ZIP> -ModDll <baseline DLL> -DataPath <fresh path>`
requires a failing baseline and does not proceed to reload. Neither command installs anything.

## In-game acceptance still needed

- Cross in both directions at high and ordinary frame rates, walking, sprinting, swimming and flying; turn back
  promptly and repeat under network latency. Watch the frame after the jump as well as the jump frame.
- Repeat in a vanilla boat and a crewed Hansa dev.14 ship; check passenger seat offset, heave, yaw and momentum.
  The headless mount is a native-handler fixture, not a Hansa simulation.
- With mist on/off and occlusion culling on/off, cross beside water and inspect water/foam phase and fog flicker.
  Keep the CW-D32 no-black-frame behavior and the CW-D33/CW-D34 plant/creature behavior.
- In an existing world, place and break blocks across each line, use a chest from either side, and reconnect/reload.
  With two real clients, watch both sides simultaneously and verify inventory transfer/break drops once. Repeat
  a fluid update, a chiseled block and any modded machine used in the build.
