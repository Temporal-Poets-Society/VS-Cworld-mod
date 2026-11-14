# Cylinder World code review, 14 November 2026

A fresh review of Cylinder World at `main` `969f301` (1.1.0-rc.4; the commit after the rc.4 tag changes only the
README): structure, correctness, performance, cleanness and maintainability, with a closer look at the wrap seams
(entities, chunks, block updates, creatures' paths and maps across the seam), the Harmony patches, threading, save
data and how Earth, World Map and Sea read this mod by reflection. It follows the earlier reviews
([review-followups.md](review-followups.md), 21 and 24 October) and the two 1.1 review rounds recorded in
[DECISIONS.md](DECISIONS.md) (rc.1 and rc.2). Read-only: no code was changed. Findings are filed as backlog items
CW-0009 to CW-0021 (status `new`).

**Result: no critical and no high finding. 5 medium, 5 low, 3 refactors.** The 1.1 review rounds left this code in good
order; what is here is one real gap in the seam journal at shutdown, one cost that nobody has measured in play, and
upkeep.

## How it was checked

- Read in full: all 26 files in `src` (9,490 lines), `CylinderWorld.csproj`, `modinfo.json`, both `AGENTS.md`, the
  card, `DECISIONS.md` (CW-D01 to CW-D50 and the proposals), `review-followups.md`, the test scripts, and the parts of
  the test sources a finding touches.
- Engine facts against `devtools/vs-ref/decompiled/1.22.7`, named with each finding. Checked and found as the mod
  assumes: the list is under "Seen and judged fine".
- The other mods' side: Earth `main` `fef6166` (`src/CylinderCompat.cs`, `EarthMapSystem.cs`, `Core/TerrainModel*.cs`,
  `Core/FineElevation.cs`), World Map `main` `3d9f941` (`src/Integrations/WorldSource.cs`, `CylinderSurface.cs`), Sea
  `main` (`src/SeaSystem.Server.cs`).
- Runs on this PC, 14 November:
  - `dotnet build CylinderWorld.csproj -c Debug`: 0 warnings, 0 errors (Debug, so no release zip was written).
  - `tests/WorldOptions/run.ps1 -ModDll bin\Debug\mod\CylinderWorld.dll`: **PASS**, 89 checks.
  - `tests/SeamEdits/client.ps1 -ModDll bin\Debug\mod\CylinderWorld.dll`: **177 checks, 0 failures**.
  - `tests/PolarChecks/run.ps1` (Earth 2.0.0-rc.13 from the suite's checkout): **PASS**, 170,433 checks.
  - A scratch benchmark outside the repo (Earth's `src/Core` compiled in as PolarChecks does, the bench world's 1:10
    layout: W 4,040,704, jump inset 16,384, band 17,056): `SeamBlocks.Twin` 0.07 µs in the band's identical part and
    2.2 to 3.6 µs in the strip beyond it (it samples Earth there); Earth's sample alone 1.6 to 3.6 µs; a journal of
    100,000 snow cells takes 41 ms to serialize (3.8 MB) and 46 ms to read back, 500,000 cells 197 ms (19 MB) and 417 ms.
  - A bench server log of today at 1:10 (`work/bench-climate/Logs/Archive/2026-11-14_15_18_41/server-main.log`): the
    Earth binding with its wall-top sampling of the whole ring takes under a second.
- Not run: the server checks (SeamChecks, SeamEdits smoke, NewWorldChecks, WallLavaChecks, PolarChecks smoke). They
  passed for rc.4 on 13 November (the card) and no code changed since; other sessions were using this machine's
  servers. Nothing was looked at in game.

## The earlier reviews' fixes still hold

| Item (review-followups.md) | Where it lives now | Holds? |
| --- | --- | --- |
| Unguarded Harmony patches in the Earth adapter | `GameFeature` for both guards (`EarthPolarSystem.cs:74-83`) | yes |
| A bad `PolarIceMode` stops the server | warning, then `default` (`CylinderWorldCore.cs:232-236`) | yes |
| `EquatorTemperature == PoleTemperature` | `AbsLatitudeForTemperature` answers 0 (`SeamParams.cs:852`) | yes |
| Sea-ice memory per tracked column | 24-byte `Cell`, allocated on the first visit (`SeaIceSystem.cs:36-46, 135`) | yes |
| Sea-ice catch-up | columns near players and their seam mirror first (`SeaIceSystem.cs:157-184`); the global limit is unchanged (CW-P08) | partly, as recorded |
| A jump into unloaded chunks | players wait (`SeamWrapSystem.DestinationReady`, `:212-225`), other entities too (`:187`) | yes |
| Weather dictionaries read without a lock | vanilla's lock on both sides (`SeamWeatherSync.cs:99`, `SeamJumpClient.cs:282`) | yes |
| Climate handler and worldgen queries | one correction per reading (CW-D44, `PolarTemperatureSystem.cs:59-69`); PolarChecks: worst 0.20 °C off | yes |
| `/cyl map` markers off the ring | left out (`WorldPreview.cs:158-167`) | yes |
| Preload work every 50 ms | unchanged, CW-P04 | open, as recorded |

CW-D39's one gateway holds too: every Harmony patch goes through a `GameFeature`, each with a runtime `Fail`. The
only reflection outside it is `SeamJumpTrace`'s three statistics (finding 7).

## Findings, most severe first

Severity: **critical** (data loss or a crash in ordinary play), **high** (a release check is red, or a data race on
a live path), **medium** (a real defect in an edge case, or a cost or upkeep problem that will bite), **low**
(cleanness, small risks), **refactor** (structure; no behaviour change).

### 1. Medium: the shutdown save never drains the queued seam edits

- **Evidence:** `SeamBlocks.Save` captures what is still queued (changes reported off the main thread, and a
  container's second capture) only while `feature?.On == true` (`SeamBlocks.cs:367`). Its comment, and the rc.2
  amendment to CW-D36/CW-D40, say the shutdown save is covered. It is not: the 1.22 server disposes the mods before
  that save. `ServerSystemModHandler.OnBeginShutdown` calls `loader.Dispose()` (`ServerSystemModHandler.cs:96-100`);
  `ServerSystemLoadAndSaveGame`, created after it (`ServerMain.cs:1141, 1151`), raises `GameWorldSave` in its own
  `OnBeginShutdown` (`ServerSystemLoadAndSaveGame.cs:191-208`). `SeamBlocks.Dispose` has by then unpatched the
  feature and set `feature` and `instance` to null (`SeamBlocks.cs:140-147`), so the condition is false and nothing
  is drained. A change queued since the last 100 ms tick (vanilla's scheduled block updates on the chunk thread, a
  mod's `SetBlock` on another thread) or a container's late slot change (the firepit case, `AfterSlot`) is saved in
  its own copy and reaches neither the other copy nor the journal: the two copies differ at that cell until it is
  edited again. The window is one tick, so it is rare, and silent when it happens. `tests/SeamEdits` checks an
  off-thread change before a column unload (`SmokeMod.cs:285-298`), not before a shutdown: its shutdown comes 250 ms
  after the last change, when the tick has drained everything.
- **Fix:** drain in `Dispose`, before the feature is unpatched (same thread as the save that follows, the server's
  threads already suspended by `Suspend(true)`, `ServerMain.cs:1754`, and the chunks still loaded); or keep a flag
  for `Save` that survives `Dispose`, and let `SafeDrain` log instead of calling
  `feature.Fail` on null. Add a SeamEdits check: queue an off-thread change, shut down in the same tick, reload,
  compare the copies.
- **Risk:** low.
- **Item:** CW-0009.

### 2. Medium: natural snow in the band goes through the journal cell by cell, and nobody has measured it

- **Evidence (read from the code; not reproduced in game):**
  - Vanilla's snow accumulation writes through a bulk accessor and commits on the main thread every 100 ms, up to ten
    chunk columns a time (`WeatherSimulationSnowAccum.cs:54, 128-160`); the commit ends in
    `SendBlockUpdateBulkMinimal` (`BlockAccessorBulkMinimalUpdate.cs:134-137`), which `SeamBlocks` patches
    (`SeamBlocks.cs:119, 406-415`). So every snow layer that changes in the band's identical part is captured: a new
    `Cell`, the dictionary entries, and `Apply` on the other copy (`:598-690`), which sends two single-block packets
    per cell to each client holding that copy (`:684-685`) where vanilla sends one batch.
  - Where the other copy is not loaded, the cell stays in the journal until that copy loads and a save has passed.
    The journal has no bound and no figure in the log except the count at load (`:357`). It is serialized whole on
    the main thread at every save (`:375`): measured here at 41 ms and 3.8 MB for 100,000 cells, 197 ms and 19 MB for
    500,000, and it is read back and replayed to both copies after every restart (CW-P05).
  - Vanilla already brings both copies up to date by itself: each map chunk keeps its own `SnowAccum` and catches up
    from the region's snapshots when it loads (`WeatherSimulationSnowAccum.cs:330-378`), and `SeamWeatherSync` keeps
    those snapshots equal on both sides. The mirror then works against it. Vanilla dithers the level by a hash of the
    absolute x (`:437`), so for the same accumulation the two copies want levels up to half a layer apart; each
    copy's next update writes its own level and the mirror copies it over the other. And a copy that catches up after
    the journal's snow was replayed into it reads that snow as its starting level (`:430-433`, where its own
    `SnowAccum` is 0) and adds the whole missed interval on top. The copies stay equal to each other (the last
    writer wins), so the jump is unaffected; the cost is churn, and a snow depth that can step between two values.
  - Where it applies: every snow-bearing surface of the identical part. In Earth 2.0 worlds that is Earth's land on
    the seam (St. Lawrence Island; the bench world's seam runs through the Bering Strait), Arctic sea ice with snow,
    and Antarctica and the wall tops in the band, which pair at any distance (`IdenticalColumn`, `:250-262`); in
    plain worlds the ice sheet and walls where they cross the band. CW-D40 took the seasonal sea ice out of the
    journal for exactly this reason (`Suppress`); snow on the same ice still goes in.
- **Fix:** measure first, on the bench: an Earth world at the seam at 66° N in winter, log the journal's size at every
  save and the time `SafeDrain` and `Save` take. Then choose: leave vanilla's snow accumulation to each copy as the
  sea ice is (a `Suppress`-like scope around that bulk commit, or skipping cells whose only change is the snow cover),
  or keep mirroring it and make it cheap (batch the packets, cap or prune the journal). Either way print the
  journal's size at each save when it is large.
- **Risk:** medium. Leaving snow to each copy can leave the two sides one snow layer apart at a jump (the dither), which
  the crossing check tolerates; the choice is the author's.
- **Correction (re-review of the fix, 14 November):** the crossing check did not tolerate it. `SeamWrapSystem.IsPassable`
  refused a landing whose collision boxes overlap the entity's, and snow layers 2 to 7 collide (0.125 or 0.25 high), so
  with snow left to each copy a crossing on foot over snow could be put back. The fix (CW-D51) lets an entity land in a
  snow layer; snow cells older builds journaled are dropped at load.
- **Item:** CW-0010.

### 3. Medium: `FlattenSeabed` shapes worldgen but is read from the config at every start

- **Evidence:** every other setting that shapes terrain is frozen into `SeamParams` when the world is made (CW-D07),
  and CW-D47 did the same for `ExtendTemperatureRange` because toggling it "gave new chunks different readings than
  their neighbours". `FlattenSeabed` is still read live: `OnChunkColumnGen` and `ClearSeamSpill` flatten the band and
  lay the mantle curtain only `if (core.Config.FlattenSeabed …)` (`SeamWorldGen.cs:528, 537`). Switched off in an
  existing world (or a config file that fell back to defaults the other way), new band chunks keep natural terrain
  beside flattened ones, the two sides of the jump differ, nothing stops tunnelling under the jump line there, and
  `SeamBlocks` still pairs those columns as identical (`IdenticalColumn` has no such test), so the first edit copies
  one side's cell into the other's unrelated terrain. World Map already has to guess: "its saved layout does not
  say" (`Wmap/src/Integrations/WorldSource.cs:48-49`). The config's own comment lists the frozen values and
  `FlattenSeabed` is not among them (`CylinderWorldConfig.cs:3-7`).
  The same shape, smaller: `SeasonalSeaIce` is a documented runtime setting, but switching it off in a world that had
  it leaves the seasonal ice where the last season put it (nothing thaws it: `SeaIceSystem.Tick` returns,
  `BlockSeaIce` takes no melt ticks) while new chunks get solid floes.
- **Fix:** save `FlattenSeabed` with the world as CW-D47 did (a new ProtoMember and a "frozen" marker; worlds without
  the marker keep reading the config), or drop the option: a world without the flatten is not a supported layout. Say
  in the config comment what switching `SeasonalSeaIce` off does, or thaw the seasonal ice once when it is switched off.
- **Risk:** low; one saved field, no terrain change in existing worlds. World Map's cache key changes once.
- **Item:** CW-0011.

### 4. Medium: what Earth, World Map and Sea bind by reflection is not pinned anywhere

- **Evidence:** three mods read about fifty of this mod's members by name, with no build reference:
  - Earth (`Earth/src/CylinderCompat.cs`, `EarthMapSystem.cs:30, 46, 499, 1018`): the type name
    `CylinderWorld.CylinderWorldCore`, `Params`, `Config` with `Enabled`, `PolarWalls`, `PolarClimate`,
    `SetSpawn(int, int)`, the save key `cylinderworld-seam` (Earth's own "new world" test reads it), and on
    `SeamParams` `JumpInset`, `Circumference`, `EquatorZ`, `PoleDistance`, `SeamHalfWidth`, `SeabedY`, `SpawnX`,
    `SpawnZ`, `WallDepth`, `PolarClimate`, `Version`, `EarthPolarIce`, and it writes `ExtendTemperature`.
  - World Map (`Wmap/src/Integrations/CylinderSurface.cs:32-86`, `WorldSource.cs:47-54, 137-143, 326`): 17 methods
    by bare name (`InSeamBand`, `SeamBlendWeight`, `SeamGround`, `SeamFloorY`, `DistToEdge`, `DistToJumpLine`,
    `DistToZEdge`, `PolarOwnsSeabed`, `PlaneCap`, `IsWallBlock`, `GetPolarColumn`, `OceanForce`, `EarthIceRise`,
    `HasArcticIce`, `EarthSnowEighths`, and `Latitude` and `Sample` on `EarthPolar`), the `PolarColumn` fields, the
    constants `IceCover`, `CurtainHalfWidth` and `LifelessBorder`, a dozen fields and properties, `Config.FlattenSeabed`,
    and every field carrying a `ProtoMember` attribute except those named `Mist…` or `JumpFog…` for its cache key.
  - Sea (`Sea/src/SeaSystem.Server.cs:128-133`): `Params`, `Circumference`, `JumpInset`.

  All of them exist today (checked name by name). What guards them is one line in `AGENTS.md` ("keep those names
  stable") and two comments (`SeamParams.cs:689-690`, `SeamWorldGen.cs:510-511`). A bare-name `GetMethod` throws
  `AmbiguousMatchException` the day one of those 17 gets a public overload; a renamed field makes Earth refuse the
  world. The tests call some of these members directly, so a rename breaks their compile, but nothing lists the
  surface or fails with "World Map binds this". World Map's cache key also takes in `WallBuildMargin`, a runtime
  setting that shapes no terrain.
- **Fix:** an offline check (in `tests/WorldOptions` or beside it) that asserts each bound name once, by the same
  lookup the consumer uses (`GetMethod(name)` without parameter types for World Map's), with the consumer named in
  the message; the list in one place in `docs/`, linked from `AGENTS.md`. Cross-mod notes for World Map (bind with
  parameter types; leave `WallBuildMargin` out of the cache key) are below.
- **Risk:** test code only.
- **Item:** CW-0012.

### 5. Medium: three server checks start from an untracked file in Earth's folder

- **Evidence:** `tests/SeamChecks/smoke.ps1:3`, `tests/NewWorldChecks/run.ps1:3` and `tests/WallLavaChecks/run.ps1:5`
  default `-TemplateConfig` to `C:\VS mods\Earth\testserver\serverconfig.json`. That file is ignored by Earth's
  repository (`Earth/.gitignore:17`, `testserver/`), dated 22 October, and exists only on this PC; the suite's rule is
  that test output is deleted when a check is done. SeamChecks carries the release evidence (the terrain digest,
  D58). One cleanup of Earth's ignored folders, or a second machine, and the three checks stop before they start.
  `tests/SeamEdits/smoke.ps1:26` already does it right: it lets the server write a default config (`--genconfig`).
- **Fix:** generate the config in all three as SeamEdits does (one shared helper, finding 9), or keep a small template
  in `tests/`. Check that the digest is unchanged with the generated config (the template may set a view radius or
  world size the digest depends on; if so, set those explicitly in the script).
- **Risk:** test code only; the digest comparison with rc.4 needs one run to confirm.
- **Item:** CW-0013.

### 6. Low: the server patches run in every world, also where the mod is off, and wrap every block entity tick

- **Evidence:** `SeamBlocks`, `SeamEntities`, the preloader, the rivulet guard and the rain fold patch in
  `StartServerSide` or `Start` (`SeamBlocks.cs:89-138`, `SeamEntities.cs:36-52`, `SeamWrapSystem.cs:50`,
  `PolarRivuletGuard.cs:25-35`, `SeamWeatherSync.cs:271-298`), before the world's params exist (WorldReady,
  `CylinderWorldCore.cs:156`), and stay on when the world has none (`"Enabled": false`, or a world too narrow for a
  seam, `:254, 295-300`). Each body returns early, so the result is right, but in such a world every block change
  still runs three postfixes, every inventory slot change one, and every block entity's tick listener, in every
  world, is replaced by a closure that calls `Simulate` first (`SeamBlocks.cs:739-750`): only block entities past a
  jump line can ever be stopped by it. Small things in the same vein: `SeamBlocks.OnTick` builds a list and an array
  of player chunks every 100 ms whether or not anyone is near the band (`:156-163`); `SeaIceSystem.Tick` allocates a
  `SeaIceDate` and a `Stopwatch` every 100 ms in every world (`SeaIceSystem.cs:81-89, 110`).
- **Fix:** skip the patches when the config says `Enabled: false` (known at start), and unpatch at WorldReady when
  the world gets no params; wrap a tick listener only when the block entity's position is in the band (its position
  is known when it registers); publish the player chunks and the sea-ice date only when they changed.
- **Risk:** low. The band test at registration must use the same `InBand` as today.
- **Item:** CW-0014.

### 7. Low: small robustness gaps

- **The world's record is read without a guard.** `SerializerUtil.Deserialize<SeamParams>(data)` at WorldReady
  (`CylinderWorldCore.cs:260`) throws a bare protobuf error for a damaged record; the journal and the exposure data
  both have a guarded load with a message (`SeamBlocks.cs:330-341`, `PolarExposureSystem.cs:109-121`). The world
  should still be refused (recomputing would lay a new layout over the old terrain), but with a sentence that says
  what is damaged and that nothing was changed.
- **The rain fold's user count can go wrong.** `SeamRainPatch.Dispose` decrements the static `users` whether or not
  this instance's `Start` ran (`SeamWeatherSync.cs:300-318`). If mod loading stops before it (another mod throws
  first) the count goes negative and the next world in the session never patches the fold, with no log line.
  Decrement only what this instance counted.
- **Cold exposure ignores the dimension.** `PolarExposureSystem.Tick` (`:133-143`) uses the player's x and z in any
  dimension; the wrap, the preloader and the sea-ice system all check `Pos.Dimension` (`SeamWrapSystem.cs:83`).
- **One reflection outside the gateway.** `SeamJumpTrace` looks up three `RuntimeStats` fields with `AccessTools`
  directly (`SeamJumpTrace.cs:37-40`). It tolerates their absence (-1), so it is harmless; it is the one exception to
  CW-D39 and `AGENTS.md` says there is none.
- **Item:** CW-0015.

### 8. Low: a Release build replaces a published version's zip without a word

- **Evidence:** `PackageMod` copies over `release\cylinderworld-<version>.zip` whenever the version in `modinfo.json`
  is unchanged (`CylinderWorld.csproj:86-108`). `main` is one commit past the `v1.1.0-rc.4` tag with the same version,
  and that zip is the one published as a GitHub prerelease: the next Release build (which the offline checks need,
  finding 9) replaces the local copy of a published file with different bytes. CW-0007 stopped the build deleting
  other versions' zips; Ships' build refuses to overwrite any (`suite.json`).
- **Fix:** refuse to overwrite an existing zip unless asked (`-p:Overwrite=true`), or write to
  `-p:PackageFile=<path>`, as Ships does; or bump the version right after each tag.
- **Item:** CW-0016.

### 9. Low: the test harness has no single entry and repeats itself

- **Evidence:** eight scripts, each run by hand (the card lists them); the block that finds a port free for TCP and
  UDP is pasted into six (`SeamChecks/smoke.ps1:25-35`, `SeamEdits/smoke.ps1:31-41`, `NewWorldChecks/run.ps1:30-40`,
  `PolarChecks/smoke.ps1:68-78`, `WallLavaChecks/run.ps1:21-30`, `oldworld.ps1:38-47`), the compile-against-the-game
  block into three. `PolarChecks/run.ps1:28, 38` takes the mod only from `bin\Release\mod` (the other two offline
  checks have `-ModDll`), so checking a change means a Release build, which rewrites the release zip (finding 8).
  Earth got `tests/Run-All.ps1` this week (EA-0088).
- **Fix:** `tests/common.ps1` (free port, generated server config, compile, run and wait with a timeout) and
  `tests/Run-All.ps1` with one summary and exit code, offline checks first; `-ModDll` for PolarChecks.
- **Item:** CW-0017.

### 10. Low: docs behind the code

- `Cworld/AGENTS.md:10` and the card (`overview/mods/cylinderworld.md:8`) say "no public repo"; the repository has the
  `tps` remote and `suite.json` and the card's own "Published" row name the public repo and its prerelease.
- `AGENTS.md:32-35` still tells the reader to pass `-EarthSource`, `-EarthZip` and `-HydrologyZip` because the
  defaults are stale; CW-0004 and CW-0006 fixed those defaults on 13 November. The card's build line (`:97`) still
  says the Release build "deletes older zips" (fixed by CW-0007).
- Comments: `CylinderWorldConfig.cs:20` gives the automatic band width without the wavy edge's 64 blocks
  (`CylinderWorldCore.cs:539`); `SeamParams.cs:86` calls `EarthPolarIce` "opt-in" (automatic since CW-D37);
  `CylinderWorldCore.cs:100-106` says the mod's own spawn keeps clear of the wall zone plus `PolarMargin`, but
  `ComputeParams` keeps it `PolarMargin` from the wall face (`:613-615`; with the default temperatures the spawn
  latitude never comes near either).
- CW-P08's "about 4 s on the main thread at 1:100" for the wall-top sampling no longer describes it: under a second
  at 1:10 in today's bench log.
- **Item:** CW-0018.

### 11. Refactor: `SeamWorldGen.cs` holds nine jobs in 1,500 lines

- **Evidence:** one partial class, one file: block ids and refusal, spawn land and the story shift, the climate and
  latitude install, map regions, the per-column cache, the band (flatten, ramp, blend, Earth's land and curtain), the
  polar zone (`BuildPolarZone`, 200 lines with eleven locals deciding one column), the rivulet cleanup at generation
  and at load, and the block-entity cleanup with its two reflected members. `EarthPolarWorldGen.cs` already shows the
  way (the same partial class, one concern).
- **Fix:** pure moves into partial files by concern (`SeamWorldGen.Setup.cs`, `.Band.cs`, `.Polar.cs`, `.Rivulets.cs`,
  `.BlockEntities.cs`), one commit, checked by SeamChecks' digest being equal. The split of `BuildPolarZone` into a
  column plan and a writer is CW-P08 and a separate, later step.
- **Item:** CW-0019.

### 12. Refactor: `SeamParams` is the saved layout, the runtime settings and the geometry in one

- **Evidence:** 925 lines plus two partial files; public mutable fields; saved layout (39 ProtoMembers), settings
  taken from the config at each start (`MistDensity`, `MistMin`, `MistRange`, `JumpFogMin`, `JumpFogSeconds`,
  `WallBuildMargin`, `SeasonalSeaIce`), server-only state (`EarthPolar`, the sea-ice date) and some seventy geometry,
  polar and climate functions. Which field is which is in comments ("Runtime-only"), and World Map tells them apart
  by name prefix (finding 4). The runtime settings are also written into the save whenever the record is stored,
  where they mean nothing.
- **Fix:** keep the type, its name, its field names and its ProtoMember numbers (they are the save format and the
  reflection surface). Group the file by concern into partials (`SeamParams.Seam.cs`, `.Polar.cs`, `.Climate.cs`,
  `.Noise.cs`); mark the runtime fields with one attribute or move them into a nested `Runtime` object that the
  packet carries and the save does not, and give World Map that marker to read instead of a name prefix.
- **Item:** CW-0020.

### 13. Refactor: small duplicates

- Chunk column keys are packed the same way twice (`SeamPreload.cs:612-614`, `SeaIceSystem.cs:79`).
- `UpdateHeightmaps` (`SeamWorldGen.cs:1387-1410`) and `UpdateEarthHeightmaps` (`EarthPolarWorldGen.cs:184-202`) differ
  in one rule (whether plants and snow count as terrain); one function with that as a parameter says so.
- "Who is aboard" has two rules: `SeamWrapSystem.PlayersAboard` (`:320-329`, any online player) and
  `SeamEntities.AnnounceWrap` (`:199-201`, playing players only). CW-P09 already asks for one.
- `32` and `GlobalConstants.ChunkSize` are mixed within single files (`SeamPreload.cs`, `EarthPolarWorldGen.cs`).
- `SeaIceSystem.cs` is written without spaces around operators, unlike every other file; reformat it in a commit of
  its own.
- **Item:** CW-0021.

### Seen and judged fine

- **Threading:** vanilla's sent-chunk sets and its out-of-range unload are main-thread only (the awareness system's
  `OnServerTick`, `ServerSystemClientAwareness.cs:23-37`), so `HidePrepared` and `BeforeJump` touch them on the right
  thread. Chunk load callbacks run on the main thread, at once when the column is loaded (`ServerMain.cs:5022`), else
  queued (`ServerSystemSupplyChunks.cs:89-91`). Client position packets are handled in a game tick listener
  (`SystemNetworkProcess.cs:72, 247`), not on the network thread. The map clouds read the spawn position only while
  drawing (`CloudRendererMap.cs:256`). Earth's `FineElevation.BuiltInOnly`, which `/cyl map` sets, is per thread.
  The noise set, the sea-ice date and the column cache are published safely.
- **Weather sync:** region keys are decoded as vanilla packs them (`WeatherSystemBase.cs:235-238`);
  `SerializerUtil.Serialize(data, stream)` returns a copy, so `SameState` compares two arrays.
- **The wrap:** vanilla's teleport does what the mod relies on (motion zeroed before the callback, passengers
  re-seated after it, the position version advanced for players only: `Entity.cs:918-950`,
  `EntityPlayer.cs:1402-1431`). A creature wrapped mid-path keeps its old target, which now lies one ring behind it in
  the direction it was going, so it carries on the same way until the task ends. Interaction and pursuit across the
  line stay a known gap (CW-D34).
- **Block updates:** every synchronized write path reaches a patch: `SetBlockInternal`, `ExchangeBlock`, the relaxed
  bulk accessor through `MarkBlockModified` and `MarkDecorsDirty` (`ServerWorldMap.cs:250-273`), the minimal bulk
  accessor through `SendBlockUpdateBulkMinimal`.
- **Cost of the pairing test:** 0.07 µs in the identical part, 2 to 4 µs beyond it in Earth worlds (measured above).
- **Save data:** a newer layout is refused; the journal and the exposure data load guarded; the journal keeps an
  unreadable copy; nothing unsubscribes `GameWorldSave`. Vanilla already sends the world seed to clients, so
  `NoiseSeed` in the params packet gives nothing away.
- **Reflection of Earth:** every member `EarthPolarModel` and `EarthPolarSystem` bind exists in Earth `main`
  (`Geometry`, `Data`, `GetColumn`, `SampleClimate`, the column's `Top`, `IceBottom`, `Water`, `WaterTop`, `Lat`,
  `Lon`, `GutterSeabed`, `RealSeam`, `Params.Enabled`, `Model`).

## What is good

- **One gateway for everything that can break on a game update** (CW-D39), used without exception for patches, and
  failing one feature at a time with one line; the checks fail on that line, so a broken patch is caught by a run.
- **Layouts are saved and never reinterpreted.** PolarChecks compares layouts 4 to 10 with the 1.0.4 DLL, and the
  seam smoke's digest shows a release generates the same terrain as the last.
- **The decision log is a real record**: what was found, what was chosen, what was rejected and how it was checked,
  down to the numbers.
- **Offline checks against the game's own assemblies** (177 client checks without a client) make the client patches
  testable on a machine that is busy.
- **Refusing instead of generating wrongly**: a world without Earth's model, without block ids, or made by a newer
  layout stops with a sentence a player can act on.

## Cross-mod notes (not changed or filed here)

- **World Map:** bind this mod's methods with their parameter types (a bare `GetMethod(name)` fails on the first
  overload); read `FlattenSeabed` from the saved layout once finding 3 is done; leave `WallBuildMargin` out of the
  chart cache key.
- **Earth:** `modinfo.json` asks for Cylinder World 1.0.4. With 1.0.4 a new Earth world whose first start stops
  (data download) is taken for an existing world on the retry (the bug CW-D41 fixed), so Earth 2.0.0 should ask for
  1.1.0 when both are released.

## Plan, in order

1. **Before 1.1.0:** the shutdown drain with its check (CW-0009); `FlattenSeabed` saved with the world (CW-0011,
   with a note to World Map); the three checks' own server config (CW-0013); the small guards (CW-0015). All small.
2. **Measure, then decide** the snow question on the bench (CW-0010). If the journal stays small in a winter at the
   seam, close it with the numbers; if not, the author chooses between leaving snow to each copy and batching.
3. **Pin the reflection surface** (CW-0012), since three released mods bind it, and tell World Map.
4. **Upkeep, each in its own commit:** the patches only where the mod runs (CW-0014), the build's overwrite
   (CW-0016), the test runner (CW-0017, which makes steps 1 and 3 cheaper if done first), the docs (CW-0018).
5. **Refactors as pure moves, checked by an equal SeamChecks digest and PolarChecks:** `SeamWorldGen` (CW-0019),
   `SeamParams` (CW-0020, after CW-0011 and CW-0012 so the surface is pinned first), the duplicates (CW-0021).
