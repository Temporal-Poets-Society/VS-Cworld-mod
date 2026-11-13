# Cylinder World 0.11.0 verification

Verified 28 September 2026 against Vintage Story 1.22.7, .NET 10.0.8 and bundled Harmony 2.4.2.

- Release build: zero warnings/errors. The installed ZIP is checked against the release ZIP by SHA-256;
  the hash is recorded in `sea-ice-routes/package-sha256.txt`.
- Offline checks: **78,112 passed**, including comparison with the pre-change DLL for default layouts 4–9,
  Earth adapter geometry, polar wildlife boundaries, pressure ridges, opposite normal seasons and local
  fixed-season overrides. Default geometry remains identical.
- Canadian and Siberian routes: verified block by block at summer minimum with a 13-block-wide corridor
  and at least four water blocks of depth. Terrain alone permits both; the previous fixed ice and new winter
  policy did not yield an open route in the search. See `seasonal-sea-ice.md` for scope and CSV routes.
- Real dedicated server: a fresh world and reload of that same world both passed. Tests use the real blocks,
  game accessors and live scheduler. Checks cover generation, ice breaking into saltwater, freeze/thaw,
  snow removal, neighbouring/overhead construction, two-layer legacy-floe conversion, permanent ice,
  saved-chunk catch-up and calendar snapshots. Test chunks are explicitly kept loaded because no player
  is present. The production updater never forces remote chunks to load.
- Polar regression checks: Greenland elevations/glaciers retained; Antarctic ice/cliff materials and heights
  retained around -70/20 across 25 chunk columns; no transparent glacier remnants or boiling water there;
  real patched hot-spring/trader entry points and spawn-event guards verified; northern ridges and snowdrifts
  retained. Refrozen outer sea ice receives weather snow instead of recreating generation-only drifts.
- Client seam-race harness: all three actual game methods patch; exception paths release the locks;
  unrelated packet IDs bypass them; Earth Map and Cylinder World pass in both load orders, including
  three threads making 20,000 calls each without deadlock.
- Graphical client: a separate copied test world reached level finalization with **0.11.0**. The loaded DLL's
  hash matched `bin/Release/mod/CylinderWorld.dll`. Installed ZIP mods were included and normal saves/settings
  were not edited. The copied local login settings were removed when the test client exited.

The full-mod client test later logged an `IndexOutOfRangeException` in
`TCTCache.CalcBlockFaceLight`, called by **ChiseledWorld.Rendering.TreeMushroomDetailWriter** and its tree worker.
This is a separate renderer error, not the `networkproc` dictionary stack reported for seam crossings.
The client remained responsive and later exited via its menu. ChiseledWorld was not modified; the origin of
that renderer error was not investigated beyond the stack trace, so this is not a claim that the entire mod
combination is error-free. A full Carrack voyage and seasonal visual inspection remain manual checks.

Earth source hashes match the pre-change snapshot. `ClientMapRaceFix.cs`, `SeamWrapSystem.cs`,
`EarthPolarSystem.cs` and `EarthPolarTerrain.cs` also match their pre-seasonal copies.

Reproduce with `tests/PolarChecks/run.ps1`, `run.ps1 -Routes`, and `tests/PolarChecks/smoke.ps1`.
The server script uses isolated temporary data and never opens normal saves.
