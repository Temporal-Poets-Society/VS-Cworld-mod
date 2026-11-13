# Code review follow-ups (0.11.7, 21 October 2026)

Claude reviewed the whole mod and Codex (adversarial review, three passes) reviewed it too. Verified bugs were fixed in
0.11.7 (see the README). This file lists what was found but **not** addressed, for the next round. Severity is ours
after checking the code, not the reviewer's first rating.

## Round-1 review fixes (branch `claude/review-fixes-cworld`, 24 October 2026, not released yet)

The round-1 review (`C:\VS mods\.reviews\Cworld\round-1.md`, findings C1-1 … C1-25) was fixed on that branch; the
report with each finding's status is `fix-1.md` beside it. Of the items below it closes: unguarded Harmony patches, a
bad `PolarIceMode`, `EquatorTemperature == PoleTemperature`, sea-ice memory, the `PolarTemperatureSystem` comment and
`/cyl map` markers off the ring. Partly: the sea-ice catch-up (columns near players and near their seam mirror go
first; the global change limit is unchanged), a jump into unloaded chunks (other entities now wait for the destination
to load; players still rely on pre-loading) and the weather dictionaries (the server skips a region pair that changes
mid-copy; the client's copy already ran inside a try/catch). Still open: preload work every 50 ms (deferred until the
seam pre-loading rework in progress lands, to avoid conflicting with it).

## Failing smoke checks (resolved in 0.11.8 and 0.11.9)

All three failed with the same numbers on 0.11.6 (`4f0b448`) and 0.11.7 (`00fc591`). Investigated on scratch
dedicated servers with per-column dumps; all three smoke tests now pass (Earth polar new and reload, seam smoke).

| Check | Cause | Fix |
| --- | --- | --- |
| Earth polar smoke, `seam-mirror`: 46/1024 mirrored band columns differ, 22 off Earth's seabed (lat 56°, floor 93..102) | Vanilla aquatic block patches (kelp, seagrass, seaweed) on the band floor, on one side only. A neighbour chunk's Vegetation pass can run after the band chunk's own and spill its patches over the chunk border, after `FlattenSeam` had cleared it. Earth wasn't the cause: Earth 0.14.7 fails with identical numbers, the dumped columns have their gravel floor at Earth's seabed with a plant on one side, and Earth's `TerrainModel` changed only its story-site flat zones in 0.14.8–0.15.2. | Worldgen: `SeamWorldGen.ClearSeamSpill` rewrites the band's identical part from its floor up again in the NeighbourSunLightFlood pass, when every neighbour has finished Vegetation (before vanilla's light flood). Now 0/1024 differ, 0 off Earth's seabed, floor 93..94. New chunks only. |
| Seam smoke, ramp rule: 10 ramp columns off `SeamGround`, 10 without a recorded natural ground (x=26116..26125 z=51200) | The check. Those columns are a vanilla deep-ocean trench: water down to y 1, only the mantle at y 0. The recorder scanned y > 0 and stored nothing, then the check used the generated top as the natural ground. The worldgen's `GroundTop` returns 0 there and ramps from 0, as the rule says. | `SeamSmokeNatural` records 0 when no ground is found, like `GroundTop`. 0 columns off the rule. |
| Seam smoke, polar shelf: worst step 11 (x=26261 z=2936, 73→84) | A vanilla structure (granite rock and cobblestone, y 77..83, with a dry room inside) floats over deep water on a y 28 seabed, 8 columns wide. The shelf rule (outside rule, and the band's ramp strip that eases into it) raised natural ground, structures included, toward the shelf: 83 → 85 there, 28 → 73 beside it, and filled the water under it with rock. | 0.11.8: the check records the natural ground of its rows and fails only on steps beyond what the shelf raise makes of the natural ground's own step, as the ramp's 1 : 4 check does. 0.11.9 (worldgen): ground with sea water under it before the seabed isn't ground for the shelf raise and the seam ramp (`SeamWorldGen.TopGround`), so they start from the real seabed and the structure isn't turned into a rock column; the checks use the same rule. Worst step now 2. |

0.11.9 also runs `ClearSeamSpill` over the default layout's polar band columns (`BuildPolarZone` exact floor), which
had no late re-clear.

0.11.10: in Earth mode that late re-clear (`FlattenSeam`, spill) also reset the band's sea ice and pressure ridges
to open water at the North wall, and `BuildPolarZone` skipped Earth mode in that pass, so the band stayed open until
`SeaIceSystem` refroze it row by row (reported in game). `BuildPolarZone` now reruns `BuildEarthPolarZone` in the
band's chunks after the re-clear. The Earth polar smoke's new `seam-wall-mirror` check (band chunk at the North wall:
no open sea where `SeaIceAt` wants ice, both sides of the jump identical) covers it.

Not Cworld's: with Earth Map 0.15.3 (built 21 October 21:57, not committed at the time), the Earth polar smoke's
Antarctic coast check fails (12 columns near x 133 133 z 90 957 up to 21 blocks under Earth's height). The same
Cworld build passes every polar check with Earth 0.15.2.

## Medium

- **30 October, issue #32:** live cell replication now keeps the two seam copies in step after a mutation, including
  sea-ice changes made through the normal block accessor. The old global sea-ice update budget below is unchanged.
  Camera/physics history and mounted packet corrections are addressed by CW-D35; runtime edit persistence by CW-D36.
  [Evidence and remaining checks](seam-edits-2026-10-30.md).

- **The live sea-ice update is slow to catch up.** `SeaIceSystem.Tick` changes at most 24 cells per 100 ms
  (240/s) and visits 256 cells per tick, shared by all players. That limit is deliberate. A region saved in the
  opposite season (up to 1024 flips per chunk column) can take tens of minutes to catch up, and the two copies of a
  seam column can disagree meanwhile. 0.11.7's landing check now stops anyone landing inside the stale side's ice.
  Idea: handle stale columns near players first, and measure the catch-up time with several players at the poles.
- **0.11.6 chunks keep sea ice inside the wall face.** The 0.11.7 fix covers new chunks only. The live sea-ice
  system skips cells that are already permanent ice, so it never repairs the two-block strip at the waterline. A repair
  would turn permanent sea ice back into wall blocks wherever `SeamParams.IsWallBlock(x, y, z)` holds at sea level − 1
  and − 2. 0.11.6 was never play-tested, so few or no saves may be affected.

## Low

- **A jump into unloaded chunks isn't checked.** `IsPassable` returns true when the destination chunk isn't
  loaded, and the teleport then loads it without a collision check. In worlds created with the mod the band is
  identical and protected, so this only matters when the mod was added to an existing world, or after creative
  building in the band. A fix: hold the wrap (entity pending) until the destination loads, then check.
- **Sea-ice memory.** Each tracked polar chunk column allocates 1024 `EarthPolarModel.Column` samples (about
  57 KB) at once. A compact record (ocean flag, latitude, warmest and coldest month as floats) or lazy allocation
  would cut this to roughly a quarter.
- **Unguarded Harmony patches.** `EarthPolarSystem.Bind` patches `GenHotSprings.genHotspring` and
  `WorldGenStructure.TryGenerate` (`schematicDatas`) without try/catch. If a game update renames them, Earth-mode
  worlds fail to start instead of logging a warning. Today's signatures were checked against VS 1.22.
- **A bad `PolarIceMode` stops the server.** Any value other than `default` or `earth` throws at WorldReady
  (new or recomputed worlds). This might be deliberate, but a warning and fallback to `default` would be friendlier.
- **`EquatorTemperature == PoleTemperature`** makes `AbsLatitudeForTemperature` divide by zero, so the spawn
  latitude is NaN and spawn silently isn't moved. Clamp the config or skip with a warning.
- **Weather dictionaries are read without a lock.** `SeamWeatherSync.Sync` copies `weatherSimByMapRegion` with
  `ToArray()` inside a try/catch, because vanilla's lock is private and vanilla itself enumerates it unlocked. The
  client's `SeamJumpClient.CopyWeather` reads the client dictionary with no lock at all. No crash has been seen.
- **`PolarTemperatureSystem` also adjusts world-generation climate queries.** `OnGetClimate` fires for every mode,
  including `WorldGenValues`. The class comment says worldgen reads the capped map, which holds only for the
  `GetClimateAt(pos, int climate)` overload. Either limit the handler by mode or correct the comment.
- **Preload work every 50 ms.** While a player approaches a jump line, `SeamPreloader.Prepare` rebuilds its target
  list (up to about 840 cells), a HashSet and a LINQ-sorted send list every player tick. That's cheap, but it could
  run every 250 ms.
- **`/cyl map` markers off the ring.** Story or spawn markers west of the jump line round toward zero and are drawn
  in column 0 instead of being left out.
