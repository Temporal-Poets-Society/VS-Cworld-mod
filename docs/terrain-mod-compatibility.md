# Compatibility with vanilla and other terrain generators

Status: analysis as of 0.11.4 (16 October 2026). Vanilla results are verified by `tests/SeamChecks/smoke.ps1`.
Everything about Terra Prety is **inferred from its public mod page**. Its code has not been inspected or run here.

## Without Earth Map (vanilla terrain)

This is the configuration Cylinder World was designed for (Earth support came later as an opt-in, CW-D11).
`PolarIceMode = default`, a `standard` world type, and any world size that fits the seam.

| Feature | Without Earth | Evidence |
|---|---|---|
| East–west wrap, preloading, weather, rain, clouds | Works | Seam smoke test (vanilla world, seed 4244) |
| Seam band, blend, curtain | Works, after the 0.11.4 fixes | Seam smoke terrain checks: flat band at `SeabedY`, no step at the blend start, seabed heightmaps |
| Coast falloff (forced ocean) | Works, including far coast-noise lobes since 0.11.4 | Seam smoke: ocean map forced beyond the old cull distance |
| Wall, ice sheet, planing, shelf | Works | Polar checks (model); generated in the smoke world |
| Pack-ice floes | Works since 0.11.4. Before, no default-layout floe was ever placed | Seam smoke: 4,139 floes, none missing |
| Planet climate, latitude, seasons | Works | Polar checks |
| Spawn and story moves | Works | Server log on new worlds |
| Seasonal sea ice | Earth mode only, by design (CW-D11) | — |

A world type other than `standard` (vanilla `superflat`, or a mod's own type) gets none of the terrain: no band, no
walls, no climate. The wrap still runs, and the server log warns about this since 0.11.4.

## How Cylinder World hooks into world generation

These are the contact points another terrain mod can collide with.

| Hook | What Cylinder World does | Assumes |
|---|---|---|
| `InitWorldGenerator("standard")`, order 0.6 | Wraps `GenMaps.climateGen` in `PolarClimateLayer`. Sets `Calendar.OnGetLatitude`. Edits `GenMaps.requireLandAt` (spawn) and `GenStoryStructures` locations | Vanilla `GenMaps` exists and owns the climate map. No later mod replaces `climateGen` |
| `MapRegionGeneration("standard")`, order 0.6 | `OceanMap = max(…, OceanForce)`, `UpheavelMap` scaled down near the X edges. Forest and shrub maps thinned toward the walls | The terrain generator reads `OceanMap` and `UpheavelMap` the way vanilla `GenTerra` does. No later handler rewrites them |
| `ChunkColumnGeneration` TerrainFeatures and Vegetation passes, order 0.6 | Flattens the seam band and blends it. Builds the wall, ice sheet, shelf, floes. Removes orphaned block entities | Works on finished blocks, so it is generator-independent. Handlers ordered after 0.6 can still put things into the band |
| `OnGetClimate` (order 1.0) | Corrects the base temperature below vanilla's −20 °C floor | Climate values come from the planet curve |
| Harmony patches | Chunk-unload keep, rain noise, clouds, client map race. Hot-spring and trader guards in Earth mode only | Vanilla internals; none of them touches terrain generation |

The chunk passes are the robust part: they act on blocks, whatever produced them. The region-map step and the climate
wrapper depend on how the other generator works.

## Classes of terrain mods

| Kind of mod | Example | Expected result |
|---|---|---|
| Content-only landform changes (`landforms.json` patches, new landforms) | Many "terrain" packs | **Compatible.** Vanilla `GenTerra` still applies the ocean map. The coast may be steeper or flatter, but the band is forced and flattened |
| Replaces the ocean map generator (`GenMaps.oceanGen`) | Continental World | **Very likely compatible.** Cylinder World's handler runs after `GenMaps` and takes the maximum with its force. Check `requireLandAt`: the replacement may ignore it, and then spawn can land in the sea |
| Rewrites `OceanMap` in its own region handler ordered after 0.6 | — | **Seam coast lost.** Cylinder World's force is overwritten. The band is still flattened, so land meets the flat sea at the blend: up to a ~100-block ramp over 160 blocks |
| Patches `GenTerra` and computes height or oceans itself, ignoring `OceanMap` | Possibly Terra Prety (see below) | **Seam coast lost**, as above. The chunk passes still make the band identical, so the jump stays seamless. The band edge looks artificial |
| Replaces the climate generator | — | If it replaces `climateGen` **after** 0.6, the planet climate is silently lost. If **before**, Cylinder World wraps it and overrides its temperature |
| Different world height or sea level | Terra Prety recommends 448 | Fine: `SeabedY`, the wall and the plateau derive from the world's sea level and height at creation |
| Places structures or decorations later than 0.6 in a pass | Ruins mods | Structures can survive in the band until the Vegetation-pass flatten. Anything placed after that pass stays. The band is protected from building, so players can't remove it |

## Terra Prety

Facts from the [mod page](https://mods.vintagestory.at/terraprety) (7.11.x, for game 1.22.0–1.22.7):

- A code mod using Harmony. Crash reports in its comments show `PatchAll` in `TerraPretyModSystem.StartPre`, and a
  patch on **`GenTerra.generate`**.
- Its own "heightmap system". Continents and oceans come from Voronoi (Worley) noise, and **landcover settings are
  ignored**, so the ocean decision is its own.
- Recommended world height 448, upheaval 0 %, landform scale 300 %. It respects a custom sea level.
- The author calls Fields and Plateaus and Plains and Valleys incompatible with the heightmap system. Watersheds is
  recommended alongside it.

What that probably means for Cylinder World:

1. **The jump itself stays seamless.** The band flatten and the curtain work on finished blocks.
2. **The coast is the open question.** If the patched `GenTerra` no longer turns `OceanMap` into depth, Terra Prety's
   continents run straight into the band. The flat sea then starts at `H − 160` with a steep 160-block bare-rock ramp,
   along a straight north–south line, wherever a continent crosses the seam. Spawn forcing (`requireLandAt`) and the
   story-location forced land are probably ignored too.
3. **Climate.** Nothing on the page suggests it replaces the climate generator. The planet climate should apply.
   Watersheds may have its own climate effects; check it if used.
4. **`/cyl map`** draws the vanilla ocean generator, so it would not show Terra Prety's continents.

### What compatibility would need

In order. Each step is only needed if the previous check shows the problem.

1. **Inspect and test** (no code change). Read Terra Prety's `GenTerra.generate` patch. Does the ocean map still
   reach the height? Then run the seam smoke test with Terra Prety installed: a 448-high world, a 160-block blend
   with a step limit, and `OceanMap` vs terrain depth at `H + 512`.
2. **Generator-independent coast in Cylinder World** (proposal C in [seam-transition.md](seam-transition.md)).
   Blend outward from `H` over an adaptive width, so the slope stays ≤ 1 : 4 whatever height the generator left, and
   shift the surface layers instead of filling with rock (proposal D). This makes the seam acceptable with *any*
   generator, and needs no knowledge of Terra Prety.
3. **Optional adapter**, as for Earth (CW-D11). If Terra Prety exposes its continent/ocean function, a reflection
   adapter (like `EarthPolarModel`) or a Harmony postfix could force ocean near the seam at the source. Its own
   continental shelf would then form naturally. This depends on Terra Prety internals and breaks on its updates, so
   it comes after step 2.
4. **Spawn on land.** If `requireLandAt` is ignored, choose the moved spawn by searching generated land near the target
   latitude, or by asking the adapter, instead of forcing it.
5. **Ordering.** If Terra Prety rewrites region maps in its own handler, Cylinder World's `MapRegionGeneration`
   handler must run after it (raise its order, or register later).

Steps 1 and 2 also cover Continental World, Plains and Valleys and similar mods. Without step 2, any generator that
ignores the ocean map shows the same artificial band edge.
