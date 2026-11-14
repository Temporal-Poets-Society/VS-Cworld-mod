# Cylinder World (`cylinderworld`)

Turns the world into a planet strip: it wraps east-west across a misty seam ocean, the middle is the equator, and
polar Walls behind ice close the north and south; climate, seasons and spawn follow latitude. Suite rules:
`C:\VS mods\AGENTS.md`. Current state: the card `C:\VS mods\overview\mods\cylinderworld.md`. Open issues:
`tools\Update-Backlog.ps1 -Mod cylinderworld` from `C:\VS mods` (prefix CW). Decisions: `docs\DECISIONS.md`
(CW-Dnn agreed, CW-Pnn proposals).

## Repository
- Trunk `main`, tracking the private `origin` (`andrisberzins-1/VS-Cworld-mod`). The public repo is remote `tps`
  (`Temporal-Poets-Society/VS-Cworld-mod`): clean release snapshots only (one commit per release, no shared
  history), published only on the author's word; never push the private trunk there. Published versions are tags
  (`v1.0.2`-`v1.0.4`, the prerelease `v1.1.0-rc.4`); old play-test lines are `archive/*` tags. The version is in
  `modinfo.json`.

## Build
- `dotnet build CylinderWorld.csproj -c Release` gives `release\cylinderworld-<version>.zip` (DLL, PDB, modinfo,
  LICENSE, assets); it fails over the Mod DB's 40,000,000 bytes.
- It never replaces an existing zip unless `-p:Overwrite=true` (CW-0016: until the version changes, every build has the
  name of the last published one), and `-p:PackageFile=<path>` writes it elsewhere; other versions' zips in `release\`
  (git-ignored, the only local copies of the published 1.0.x releases) are left alone (CW-0007).
- It fails if a sync left a `(# Name clash #)` or `(# Edit conflict #)` copy in `src` or `assets`.
- References `VintagestoryLib` as well as the API. Game path: `-p:VintageStoryPath`, `VINTAGE_STORY` or
  `%APPDATA%\Vintagestory`.

## Test
Each server check starts its own temporary local-only server (data in `%TEMP%\cylinder-*`) and fails when a
feature logs `Cylinder World: ... is off:`. Their server config is the game's own defaults (`--genconfig`) with the
checks' world, 256,000 x 102,400 x 384 (`tests\common.ps1`, CW-0013; `-TemplateConfig` starts from a file instead).
Other servers may run on the machine: each check stops only its own.
- `tests\SeamChecks\smoke.ps1` after a Release build: seam checks plus `digest.txt`, one SHA-256 per generated
  chunk column (`-ModZip`/`-ModDll` run another build; equal digests show two builds generate the same terrain;
  columns 7198/91-97 differ between two runs of one build).
- `tests\Contract\run.ps1` (offline): every member Earth, World Map and Sea read by reflection
  (`docs\REFLECTION-SURFACE.md`).
- `tests\SeamEdits\smoke.ps1` and `tests\SeamEdits\client.ps1`; `tests\NewWorldChecks\run.ps1`;
  `tests\WallLavaChecks\run.ps1` and `oldworld.ps1` (a 1.0.1 world's saved wall lava; `-OldPackage` the 1.0.1 zip);
  `tests\WorldOptions\run.ps1` (offline: the seam mist option, `polarEquatorDistance`).
- `tests\PolarChecks\run.ps1` (offline; compiles the suite's Earth checkout's `src\Core`, or `-EarthSource`;
  `-BaselineDll <old dll>` compares layouts 4-10).
- `tests\PolarChecks\smoke.ps1`: an Earth world, new and reloaded, with the newest Earth and Hydrology zips in their
  release folders or the player's Mods (`-EarthZip`, `-HydrologyZip` to choose). Its run folder holds a junction to
  the bench's Earth data store: remove `ModData\earth` alone first (`cmd /c rmdir`), never the folder recursively.
- In game: a new world (the seam, walls, climate and spawn are set at creation), on the AgentBridge bench; for
  Earth also load the Earth and Hydrology zips and an Earth play style. `/cyl info`, `/cyl goseam`,
  `/cyl gowall north`, `/cyl golat 45`, `/cyl map`. What the client draws at a jump needs a look in game.

## Release
- Bump `version` in `modinfo.json`; add the dated release paragraph at the top of `README.md` (there is no
  changelog file) and the release record in `docs\DECISIONS.md`. Build, run the checks, commit, tag `vX.Y.Z`.
- A new release generates the same worlds as the previous one except labelled fixes (D58): show it with
  SeamChecks' digest. Earth names its minimum Cylinder World version in its own `modinfo.json`.

## Gotchas
- The world layout is saved per world (CW-D07), and `ExtendTemperatureRange` too (CW-D47): existing worlds keep
  their saved behaviour; change it only for new worlds.
- Every Harmony patch and reflected game member goes through the one patch gateway (CW-D39), so a game update
  switches off only the broken feature. Add new ones there.
- `PolarExposureSystem` and the seam journal (`SeamBlocks`) save on `GameWorldSave`: never unsubscribe it in
  `Dispose` (the 1.22 server disposes mods before its shutdown save).
- Earth, World Map and Sea use Cylinder World by reflection: the members they read are listed in
  `docs\REFLECTION-SURFACE.md` and checked by `tests\Contract\run.ps1` (CW-0012). Keep those names, types and kinds,
  give World Map's methods no public overloads, and change the list and the check together.
- New worlds write `polarEquatorDistance` (CW-D50), which also moves vanilla's landforms in plain worlds: to compare
  SeamChecks digests with an older build, give the older one that value in its template (CW-D50's verification).
- The zip carries `worldconfig.json` (the `cylinderSeamMist` option, CW-D48): keep its `playStyles` an empty list.
