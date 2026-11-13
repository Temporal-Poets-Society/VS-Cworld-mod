# Cylinder World (`cylinderworld`)

Turns the world into a planet strip: it wraps east-west across a misty seam ocean, the middle is the equator, and
polar Walls behind ice close the north and south; climate, seasons and spawn follow latitude. Suite rules:
`C:\VS mods\AGENTS.md`. Current state: the card `C:\VS mods\overview\mods\cylinderworld.md`. Open issues:
`tools\Update-Backlog.ps1 -Mod cylinderworld` from `C:\VS mods` (prefix CW). Decisions: `docs\DECISIONS.md`
(CW-Dnn agreed, CW-Pnn proposals).

## Repository
- Trunk `main`, tracking the private `origin` (`andrisberzins-1/VS-Cworld-mod`); no public repo. Published
  versions are tags (`v1.0.2`-`v1.0.4`); old play-test lines are `archive/*` tags. The version is in `modinfo.json`.

## Build
- `dotnet build CylinderWorld.csproj -c Release` gives `release\cylinderworld-<version>.zip` (DLL, PDB, modinfo,
  LICENSE, assets); it fails over the Mod DB's 40,000,000 bytes.
- It replaces only its own version's zip in `release\` (CW-0007); the 1.0.0-1.0.4 zips there (git-ignored, the only
  local copies of the published releases) are left alone.
- It fails if a sync left a `(# Name clash #)` or `(# Edit conflict #)` copy in `src` or `assets`.
- References `VintagestoryLib` as well as the API. Game path: `-p:VintageStoryPath`, `VINTAGE_STORY` or
  `%APPDATA%\Vintagestory`.

## Test
Each server check starts its own temporary local-only server (data in `%TEMP%\cylinder-*`) and fails when a
feature logs `Cylinder World: ... is off:`.
- `tests\SeamChecks\smoke.ps1` after a Release build: seam checks plus `digest.txt`, one SHA-256 per generated
  chunk column (`-ModZip`/`-ModDll` run another build; equal digests show two builds generate the same terrain).
  Its template is `C:\VS mods\Earth\testserver\serverconfig.json`.
- `tests\SeamEdits\smoke.ps1` and `tests\SeamEdits\client.ps1`; `tests\NewWorldChecks\run.ps1`;
  `tests\WallLavaChecks\run.ps1` and `oldworld.ps1` (a 1.0.1 world's saved wall lava; `-OldPackage` the 1.0.1 zip);
  `tests\WorldOptions\run.ps1` (offline: the seam mist option, `polarEquatorDistance`).
- `tests\PolarChecks\run.ps1` (offline; compiles Earth's `src\Core`; `-BaselineDll <old dll>` compares layouts
  4-10). Stale default: `-EarthSource` is the detached worktree `Earth\.claude\worktrees\release-1.0`; pass
  `-EarthSource "C:\VS mods\Earth"`.
- `tests\PolarChecks\smoke.ps1`: an Earth world, new and reloaded. Stale defaults (CW-0004): pass `-EarthZip` and
  `-HydrologyZip` (e.g. `..\Earth\release\earth-<version>.zip`, `..\Hydrology\release\hydrology-<version>.zip`).
  Its run folder holds a junction to the bench's Earth data store: remove `ModData\earth` alone first
  (`cmd /c rmdir`), never the folder recursively.
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
- Earth uses Cylinder World by reflection (`SetSpawn`, the layout, the `Config` switches its preflight reads): keep
  those names stable.
- New worlds write `polarEquatorDistance` (CW-D50), which also moves vanilla's landforms in plain worlds: to compare
  SeamChecks digests with an older build, give the older one that value in its template (CW-D50's verification).
- The zip carries `worldconfig.json` (the `cylinderSeamMist` option, CW-D48): keep its `playStyles` an empty list.
