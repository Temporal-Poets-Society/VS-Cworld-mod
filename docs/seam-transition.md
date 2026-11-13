# Seam transition: how the terrain meets the jump line

Status: 0.11.5. New worlds get layout v10 (see "Layout v10" below: improvements A, B and C are implemented). Worlds
created earlier keep the v9-and-earlier band described in the following sections. Improvements D–F are still
proposals. Decisions referred to as CW-Dnn are in [DECISIONS.md](DECISIONS.md).

## What has to be true

**Runtime edits (0.11.15-playtest.5, CW-D36):** generation makes two separate physical columns agree initially;
it does not share their storage. `SeamBlocks` now mirrors changed cells in the overlapping seam band and journals
them for unloaded counterparts. This applies to existing layouts without moving their jump lines. It leaves old
untouched differences alone instead of choosing a side without evidence. See [the runtime investigation](seam-edits-2026-10-30.md).

A player crossing the jump line at `x = t` (or `x = W − t`) is teleported by the circumference `C = W − 2t`.
The jump is invisible only if everything they can see from the line is identical on both sides. With the server
view radius `R`, that is every column within `R` of either jump line. Beyond that, nothing has to match. The
terrain only has to get from "identical open sea" back to the natural world without visible artefacts.

## The layers, from the coast to the jump line (layout v9 and earlier)

```
natural land ── coast falloff (CoastFalloff, wavy) ── forced deep ocean ── blend (160) ── flat band ── jump line
                │ region maps: OceanMap raised,     │ vanilla terrain      │ chunk pass 1  │ chunk passes 1 + 2
                │ upheaval suppressed               │ at full oceanicity   │               │
x from edge:    H + 2.6·CoastFalloff (max)     H + CoastFalloff  ...  H         H − 160              t
```

Default numbers (view radius 12 chunks, no Earth proportions): `t = 512`, `H = t + R + 64 + 160 = 1120`,
`CoastFalloff = 1024`. With Earth proportions on a 256k × 102.4k map, `t = 25600` and `H = 26208`.

1. **Region maps** (`SeamWorldGen.OnMapRegionGen`, ExecuteOrder 0.6, after vanilla `GenMaps`).
   - `OceanMap = max(vanilla, OceanForce)`: 255 inside `H`, then a smoothstep down to 0 over `CoastFalloff`.
   - From v4 the falloff starts at a wavy distance. Low-frequency coast noise (1/1400) pushes it up to
     `1.6 × CoastFalloff` further in, so the forced ocean can reach `H + 2.6 × CoastFalloff`
     (`SeamParams.OceanForceReach`).
   - `UpheavelMap` is scaled by `1 − force/255`, so no islands rise in the band.
   - Vanilla's `GenTerra` then shifts terrain down by `oceanicity × (MapSizeY/256) × ⅓` blocks: about 85 blocks at
     full force for map heights 256–511. Vanilla thus builds the continental slope itself, with its own landforms,
     beaches and salt water. This is why the coast looks natural (CW-D04).
2. **Blend strip**, `H − 160 ≤ distance from edge < H` (`BlendSeamColumn`, first chunk pass only).
   - The weight is a smoothstep of the distance: 0 at `H − 160`, 1 at `H`.
   - Each column's top becomes `round(SeabedY + (naturalTop − SeabedY) × weight)`. Terrain above is cut away, and
     air, water and plants below are filled with rock (gravel on top underwater).
   - The profile is the natural one, squashed toward the flat seabed.
3. **Flat band**, distance from edge `< H − 160` (`FlattenSeam`, both chunk passes).
   - Rock below `SeabedY` (ores kept, caves and water pockets filled), gravel at `SeabedY`, salt water to sea level,
     air above.
   - Pass 1 (TerrainFeatures) runs before vegetation, so no trees grow there. Pass 2 (Vegetation) removes seaweed
     and anything neighbours spilled in (CW-D05).
4. **Mantle curtain** within 2 blocks of each jump line, below the seabed. Nobody can tunnel across.
5. **Polar columns** in the band belong to the polar worldgen, which sets the shelf floor exactly (`exactFloor`) and
   uses ring-angle noise (CW-D06), so they match too. Across the blend strip that exact floor eases into the
   outside rule (raise natural ground toward the shelf) with the same weight (CW-D22).
6. **Above the water** (client): distance-to-line mist, the jump fog pulse, camera/water reference shift, cloud
   rebase, weather and rain continuity. See the README's code map; these hide what the terrain can't.

From the jump line the flat part extends `H − 160 − t = R + 64` blocks, so the blend is never inside the view radius
of anyone standing at the line.

## Defects found and fixed in 0.11.4

| Problem | Effect | Fix |
|---|---|---|
| `data[idx]` returns the **fluid** when a cell has no solid block | The ground scan stopped at the water surface. In deep water the blend never raised the seabed: a step of up to ~40 blocks at the blend start. Heightmaps recorded water as terrain (447 of 624 underwater columns in the smoke world). Default-layout floes could never be placed (0 of 4,139 expected). | `SolidId` reads the solid layer everywhere ground or air is meant |
| Lowering in the blend kept voids under an overhang | The top solid block can be an overhang with sea under it: a 42-block step at blend weight ≈ 0 | Fill air, water and plants up to the target whether raising or lowering |
| Region cull used `H + CoastFalloff + regionSize` | Regions the coast noise still reached got no forced ocean: a straight, region-aligned step | Cull by `OceanForceReach + regionSize` |
| In the polar shelf zone the band's floor was exact inside the band and only raised outside, with no blend between | A 44-block cliff at the band's inner edge beyond the ice sheet | The exact floor eases into the outside rule across the blend strip |
| Walls and ice built above the original terrain didn't raise `MapChunk.YMax` | Worldgen sunlight treated the chunks they reach into as open sky | `UpdateHeightmaps` raises `YMax` |
| Block entities removed by position (everything above `SeabedY` in the band) | Chests and other structure blocks the blend left in place lost their contents | Remove only block entities whose block was replaced (`RemoveOrphanedBlockEntities`) |

These fixes apply to newly generated chunks and regions in all layouts (CW-D15). Saved chunks are not regenerated, so
an older world can show a seam between old and new terrain where exploration continues.

`tests/SeamChecks/smoke.ps1` now generates real columns in a vanilla world and checks the band and blend profile,
underwater heightmaps, floes, floating water and the far-coast ocean map. The first version of these checks, run
against the 0.11.3 source, found:
- 0 floes;
- water-surface heightmaps in 447 of 624 underwater columns;
- an unforced ocean map (0 where 92 was due);
- a 43-block blend step.

0.11.4 passes them all, with a worst step of 2 blocks in the seam-shaped half of the blend (seed 4244).

## Layout v10 (0.11.5, new worlds)

```
natural land ── coast falloff ── forced deep ocean ── ramp (outward, ≥ 160) ──┤ wavy edge ├── identical part ── jump line
x from edge:                                          E + 6·|height| (≤ 1024)    E = H − 160 − 0..64           t
```

Auto `H = t + R + 64 + 64 + 160` (64 more than v9, for the wavy edge): `t = 512`, `H = 1184` at the default view
radius (Earth proportions on 256k × 102.4k: `H = 26272`). Everything else in the list above (region maps, two chunk
passes, curtain, polar columns, client side) is unchanged.

- **A. Ring-periodic seabed** (`SeamParams.SeamFloorY`). The identical part's top block is `SeabedY` plus broad rises
  and basins (1/360), winding channels (ridged noise, 1/520) and small ripples (1/24), all 3D noise on `RingCoords`
  with z, so x and x ± C match (CW-D06). The amplitude is `(SeaLevel − SeabedY − 4) / 2.5`, at most 14, and the floor
  is clamped to `SeaLevel − 3`. With the default depth of 40 that gives about `SeabedY − 14 … SeabedY + 14`, channels
  to about 25 below. Still gravel on rock, caves filled, the mantle curtain below the floor.
- **B. Wavy edge** (`SeamEdge`). The identical part ends at `H − 160 − 64·n(z)`, with `n` a soft-contrast (tanh) noise
  along z, one field per side. tanh never saturates, so no stretch of the edge is straight. The edge never comes
  closer to the jump line than `IdenticalReach = H − 160 − 64 − t = R + 64`, the same margin v9 had.
- **C. Slope-limited ramp** (`SeamRampWeight`, `SeamGround`). From the edge outward, the ground moves from the band's
  seabed to the natural ground with a smoothstep over `max(160, 6 × |natural − seabed|)` blocks, at most
  `RampMax = min(1024, W/4 − H)`. A smoothstep is steepest at 1.5 × height / width, so the ramp is at most 1 : 4 for
  any height up to 170 blocks, whatever the terrain generator left. The ramp can reach past `H`; the chunk pass
  covers `SeamWorkReach = H − 160 + RampMax` and skips columns once they are natural.
- **Polar shelf columns** (exact floor inside the band, CW-D22) use the relief seabed as their base and the same ramp,
  but the ramp is capped at `H − edge` there, so it still ends by `H` where the outside rule takes over.
- **Rain** treats `IdenticalReach` as the reach of identical terrain (CW-D23).
- **Map predictors** call `SeamGround(x, z, natural)` for the band's final ground in any layout (Wmap still predicts the
  v9 formula; follow-up).

### Verification (0.11.5)

`tests/SeamChecks/smoke.ps1` (vanilla world, seed 4244, Earth proportions, so `H = 26272`) passes all 34 checks:
- **Seabed and edge:** 0 seabed misses before the edge. The edge lies between 26052 and 26102 blocks from the world
  edge over 8 rows 400 apart. The seabed ranges 107–140 around `SeabedY` 125.
- **Mirrored columns:** 0 of 98,863 cells differ, comparing past the jump line, just inside it and at the far end of
  what is in view.
- **Ramp rule:** a recorder mod reads each profile column's natural ground just before Cworld's pass. All 1,126
  generated ramp columns equal `SeamGround(x, z, natural)`.
- **Ramp slope:** the worst 16-block rise is 18 blocks, at 104 blocks into the ramp. It is vanilla's own seafloor: the
  ramp adds at most 3 blocks over 16 beyond what the natural ground and the seabed relief do there. The worst step
  near the edge is 2.
- **Pure rule:** the ramp function is at most 0.250 per block for every height up to 170.
- **Poles:** on the ice sheet 300 blocks from each wall, 0 of 128 columns at the jump line lack a matching surface
  above the sea.

`tests/PolarChecks/run.ps1` with the pre-change DLL passes. Layouts 4–9 give the same `SeamBlendWeight`, `OceanForce`,
`InSeamBand`, polar columns and walls as before, plus the flat seabed and straight edge, at 240 points per layout. The
v10 rules also pass on Earth-sized parameters: seabed identical at x + C and under water, edge in bounds, ramp ≤ 1 : 4.

The vanilla world never leaves land at the band, so the steep-generator case (weakness 4) is covered only by the pure
rule check, not by generated terrain. Nothing has been looked at in a game client yet.

### Earth worlds

Earth Map replaces vanilla's terrain pass and builds its own seam strip (a "gutter": the band plus 512 blocks each
side, fading its real ETOPO seabed down to a constant `GutterSeabedY ≈ SeabedY`). v10 applies on top of it: relief in
the identical part, the wavy edge, and a ramp to Earth's gutter seabed. Antarctica's bridge across the seam and the
Arctic's permanent sea ice within 1,000 blocks of the north wall are polar columns and unchanged, so both poles can
still be crossed on foot.

**Real seabed (CW-D26, 0.11.6, new worlds).** Earth Map 0.14.7+ (decision 0015) with `GutterSeabed` 1 fills its gutter with the seam
meridian's real seabed, blended from one gutter edge to the other (both are the same Earth point), so it repeats one
ring apart. Cworld then saves `EarthSeabed` and uses Earth's column top as the band's floor: no relief, no edge, no
ramp, just the identical part rebuilt on that floor (gravel, salt water, the mantle curtain). `SeamGround` returns the
natural ground, which Wmap takes from Earth. With an older Earth Map the band keeps the v10 relief.

## Remaining weaknesses (layout v9 and earlier; v10 status in brackets)

1. [Fixed in v10 by B.] **The blend boundary is a straight line.** The weight depends on x only, so the flat band ends along a perfectly
   straight north–south line, everywhere on the planet. It is visible underwater and on the map as an unnaturally
   straight, 160-block-wide ramp.
2. [Shape fixed in v10 by A; still gravel with no seaweed.] **The band is perfectly flat gravel.** Hundreds of blocks of featureless seabed on each side, with no seaweed or
   coral (they can't be placed to match on both sides with vanilla's generators).
3. [Open, proposal D.] **The ramp's material is bare rock.** Raising fills with rock (gravel only on an underwater top). Lowering exposes
   cut strata. Where the natural terrain at `H` is far from `SeabedY`, the ramp becomes a 160-block bare-rock slope.
4. [Fixed in v10 by C, up to 170 blocks of height.] **The ramp's steepness is unbounded.** The blend width is fixed at 160. If the natural ground at `H` is 100 blocks
   above `SeabedY` (a terrain mod that ignores the ocean map, or a mountain the forcing didn't fully sink), the
   slope averages 1 : 1.6 and reaches ~1 : 1 in the middle.
5. [Open, proposal F.] **`SeabedY` is a fixed depth** (`SeaLevel − 40`). Vanilla's natural floor under full forcing is typically deeper
   (the landform surface − 85). Most of the blend is therefore a raise, with a rock embankment.
6. [Open.] **The blend sees only the first pass.** Structures that neighbours place later in the strip aren't blended (they
   are no longer deleted either, see the fix above).
7. [Open; v10's ramp runs outward, but the wavy edge is still measured from `H − 160`.] **A user-set `SeamHalfWidth` doesn't include the blend.** An explicit value smaller than `t + R + 64 + 160` puts
   the blend within view of the jump line.

## Improvements

Ordered by value for effort. Anything that changes generated terrain needs a new layout version, so old worlds keep
the terrain their chunks were built with (CW-D07). A, B and C are implemented as layout v10 in 0.11.5 (CW-D25); D, E
and F remain proposals (CW-P01) for a later layout version. As built, v10 differs from the table in three details:
A's relief is up to ±14 blocks with channels (agreed with the user: a real-looking floor rather than ripples); C uses 6 blocks per
block of height, because 4 would let a smoothstep's middle reach 1 : 2.7; and the ramp is capped at 1024 blocks
rather than `CoastFalloff`, which can be as small as 128 on small worlds.

| # | Change | Why | Cost and risk |
|---|---|---|---|
| A (v10) | **Ring-periodic seabed relief.** `SeabedY + relief(ring angle, z)`, ±3–6 blocks of dunes and channels from 3D noise on `RingCoords` (as the Wall face does). | Natural-looking band, still identical at x and x ± C. Removes weakness 2 for the floor shape. | Small. Pure function of the ring angle; the curtain and the protection are unchanged. |
| B (v10) | **Wavy blend boundary.** Offset the blend start by low-frequency noise of (ring angle, z), amplitude ≤ 64, keeping the start ≥ `t + R`. | No straight line (weakness 1). | Small. `H` gets +64 so the flat part stays out of view. |
| C (v10) | **Blend outward, with an adaptive width.** Keep `H` as the edge of the identical zone, and blend outward over `max(160, |natural − seabed| × 4)` blocks, at most `CoastFalloff`. | Slopes stay ≤ 1 : 4 whatever the terrain generator does (weaknesses 4, and 7 partly). The key change for compatibility with terrain mods that ignore the ocean map. | Medium. Columns outside the band get modified, so the blend has to read the natural top consistently per column (it already does). |
| D | **Shift the surface instead of filling.** Move the column's top layers (soil, sand, gravel; about 4 blocks) to the target height, rather than filling with rock or cutting. | Beaches and natural seabed materials on the ramp (weakness 3). | Medium. Needs care with block entities and snow. |
| E | **Matched seam decorations.** Place seaweed and coral in the band from ring-periodic noise, and strip vanilla's own decoration in the band. | The band looks like the sea around it. Already listed as a known gap. | Medium. Vanilla's seaweed generator can't be reused directly. |
| F | **Auto seabed depth.** Pick `SeabedY` at world creation from the median natural floor under full forcing (sample `GenTerra` noise, or the first band chunks). | Smaller blends and fewer embankments (weakness 5). | Medium. It must be decided before any chunk generates, then saved. |

A + B + C (now v10) together give a band that reads as natural open ocean, with no straight lines, and a guaranteed
gentle slope regardless of which terrain generator built the world. C is a prerequisite for using generators such as
Terra Prety safely, see [terrain-mod-compatibility.md](terrain-mod-compatibility.md).
