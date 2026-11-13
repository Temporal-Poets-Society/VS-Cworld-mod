# Polar wall lava fix - 1 November 2026

The author reported lava streams in the north polar wall (89.9143 N, 11.2095 W).

## Cause and fix

The custom wall ice deliberately uses the Stone material for map rendering. Vanilla's `GenRivulets` runs at order 0.9 in the Vegetation pass, after Cylinder World's wall construction at 0.6, and can carve lava sources into recesses surrounded by that ice. Earlier wall passes clear fluids correctly; the lava is created afterwards.

`PolarRivuletGuard` adds a server-only prefix to `GenRivulets.OnChunkColumnGen`. It skips rivulet generation only in chunk columns intersecting either polar wall's full rough-face footprint. This covers both water and lava rivulets and both Earth/default polar modes. The rest of the world, disabled walls and worlds without Cylinder World retain vanilla generation. The existing Stone material, saved layout, sea ice, client code and cross-mod APIs do not change.

## Verification

The native server regression constructs identical test-only wall-ice recesses at order 0.89 and counts sources at 0.91, around the real vanilla generator. Maximum geologic activity guarantees coverage without depending on a random natural wall shape.

| Package | North wall | South wall | Ordinary-terrain control |
| --- | ---: | ---: | ---: |
| 1.0.1, original bug | 72 | 83 | 64 |
| 1.0.2 | 0 | 0 | 64 |
| 0.11.15-playtest.9 | 0 | 0 | 64 |

Counts are newly generated lava sources before fluid spreading. The fixed runs also check partial/boundary wall columns at both poles, Earth/default mode decisions, disabled walls, inactive worlds and out-of-world columns. Both Release builds pass with 0 warnings/errors. Only the runtime DLL, PDB and manifest differ from each previous package; Earth+ and all other assets are byte-identical.

Run `tests/WallLavaChecks/run.ps1` after building, or pass `-PackagePath <previous ZIP> -ExpectWallLava` to reproduce the original bug. It uses a temporary local-only server and its own save, never a player world. Evidence and package hashes: [suite verification](../../overview/verification/cworld-wall-lava-20261101.json).

## Packages and existing worlds

Public candidate: `release/cylinderworld-1.0.2.zip`. Active world-options candidate: `.claude/worktrees/codex-world-options/release/cylinderworld-0.11.15-playtest.9.zip`. Previous ZIPs are preserved. Play-test .9 was installed at the author's request on 2026-11-01 10:12:02 Europe/Riga. The installed hash matches the tested package; .8 is backed up, settings are unchanged and player saves were not accessed. Public 1.0.2 remains a publishing candidate. See the [installation receipt](<C:/VS mods/overview/verification/cworld-wall-lava-install-20261101.json>). No upload was performed.

This prevents new sources during chunk generation. Lava already saved in previously generated chunks is not automatically removed; existing affected locations need separate cleanup. There is no world-layout migration or save rewrite. No new client visual or full Earth-world integration test is claimed.
