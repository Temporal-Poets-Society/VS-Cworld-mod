# Earth polar mode verification — 2026-09-28

Build: Cylinder World 0.10.1, Vintage Story 1.22.7, bundled Harmony 2.4.2, .NET 10.0.8;
Earth Map 0.8.0. Release build: zero warnings and errors.

- 78,076 offline checks passed using Earth's real baked data and projection. These include the optional adapter,
  unchanged default geometry for layout versions 4–9 against the pre-change CylinderWorld.dll, serialization,
  Arctic ocean/land masks, spawn boundaries, seam continuity and bounded pressure ridges/snowdrifts. A 256×256
  sample has ridges up to eight blocks high, a largest east-west step of one block, 35,629 bare-ice columns and
  29,907 snow-covered columns. Of those, 12,227 contain at least a full block of snow.
- A temporary localhost-only dedicated server created a 256000 × 102400 × 384 Earth world with seed 4242.
  Both initial generation and subsequent world reloads passed the actual chunk checks below. Setting the config
  back to `default` did not change the saved Earth mode or the saved wall height.
- The registered individual and group spawn delegates were invoked with the game's polar bear entity type.
  Both deny the northern inner kilometre, Antarctica and the Antarctic Peninsula; both allow outer Arctic floes
  and Greenland to proceed to the game's other spawn checks. The combined game events honor the polar veto.
  Initial worldgen uses `TriggerTrySpawnEntity` too, verified in the installed `GenCreatures` implementation.
- Actual Harmony-patched `GenHotSprings.genHotspring` and `WorldGenStructure.TryGenerate` reject protected polar
  features before accessing terrain or placing entities/claims. The trader prefix allows nonpolar traders and
  unrelated polar ruins. The guard checks feature extents, not just the start coordinate.
- 25 generated chunk columns (25,600 surface positions) around 70°S 20°E were checked, including the shore.
  Existing ice is opaque through the full glacier depth, with no remaining vanilla transparent glacier ice and
  no boiling water in the checked surface band. Earth's solid elevations are preserved within the existing
  snow/decoration tolerance. Live rivulets can flow above coastal water after loading neighbours, so the elevation
  check measures solid terrain rather than mistaking the top of a waterfall for a terrain-height discontinuity.
- The existing client crash-fix harness still patches all three real game methods. With Earth Map also patched,
  both load orders finish the 3-thread × 20,000-call stress check without deadlock. This is a DLL-level test,
  not a graphical client/minimap walkthrough.

| Generated location | Observed terrain |
| --- | --- |
| North, 500 blocks from the nominal wall face | Custom pressure ice reaches y=166 at the inspection point; ocean floor preserved |
| Northern snowdrift chunk, x=128000 z=384 | 738 raised-ice columns, 766 bare columns, 258 snow columns, 13 full-block snow piles |
| Arctic Ocean, 80°N 135°W | Custom outer floes at y=163–164; mixed bare ice and snow |
| Greenland, 75°N 45°W | Original Earth glacier at terrain y=304; snow at y=305 |
| Antarctica, 85°S 20°E | Custom glacier surface at Earth's terrain height y=300; snow at y=301 |
| Antarctic Peninsula, 70°S 65°W | Custom glacier surface at Earth's terrain height y=291; snow at y=292 |
| Southern world boundary | Cylinder World wall through y=353, automatically above Earth's polar terrain |

The 0.10.0 tests exposed the game chunk indexer's fluid fallback; Arctic generation reads the solid layer explicitly.
The 0.10.1 coastal test also caught unconverted ice under low snowy shoreline columns. The conversion now includes
that submerged ice and samples the Earth model at its exact integer generation coordinates.

Reproduction scripts: `tests/PolarChecks/run.ps1` and `tests/PolarChecks/smoke.ps1`. They use temporary output/data
directories and the installed game; neither installs the release into the normal game data. Previews in
`docs/earth-polar-north.png` and `docs/earth-polar-south.png` use the terrain model and the same ice policy.
`docs/arctic-ridges-snow.png` shows the 0.10.1 pressure-ice and snow pattern as a shaded model preview, not a client screenshot.

Earth Map source hashes matched before/after. Cylinder World's existing `ClientMapRaceFix.cs`, `SeamWrapSystem.cs`
and `BlockWallIce.cs` also remain byte-for-byte unchanged. Original source and release were backed up under
`%TEMP%/cylinder-polar-baseline-20260928` before editing.

Limits: Arctic coverage is a fixed climate-based gameplay approximation. Natural spawning is blocked in the
lifeless zones; already-existing creatures are not removed and animals can still walk across a boundary.
The Antarctic report near `/earth tp -70 20` did not reproduce as a generated chunk-height discontinuity. The mixed
transparent/opaque ice was corrected; a graphical inspection is still needed to confirm whether it explains the
reported appearance. Earth Map's own coastal geometry is retained. The in-game minimap/world-map appearance has
not been visually inspected in a graphical client.

Use a fresh development world to inspect 0.10.1 continuously: previously generated chunks, traders and hot springs
are not removed or regenerated. Weather may add snow to the initial wind-shaped pattern during play.
