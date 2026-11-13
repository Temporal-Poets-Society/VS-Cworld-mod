# Seam crossing: notes for Sea, Magelan and Earth Map

From a review of a ship crossing the seam (2026-10-02, Cylinder World 0.11.2). Cylinder World now handles the
jump-related fixes it owns (see its README). These items belong to the other mods. Line numbers are from that day.

Already verified there: Sea's waves, wind and storms repeat exactly around the ring. A numeric check found the
same wave height at x and x − circumference to 1.5e-9 blocks. Magelan's ship motion keeps no state that the
jump would break.

## Sea

**30 October follow-up (CW-D35):** the camera-only previous-position shift from 0.11.2 was insufficient: the physics
renderer republishes its own previous position on frames without a physics step. Playtest.5 translates that retained
buffer at the teleport too. Native-handler checks show the next camera sample staying on the new lap, avoiding the
half-ring excursion that can reset both water reference points. The user's water/fog flicker still needs a visual
check with Sea/Wmap; no Sea shader change is made on this evidence alone.

1. **Fallback when a region's ocean map is missing** (`src/SeaSystem.cs:159-160`).
   - `WaveFactorAt` returns 0.2 when the client doesn't have the region yet.
   - After a seam jump, a ship's heave, pitch and roll (Magelan, `EntityShip.cs:305`) then ran at a fifth until the
     region arrived, and snapped back to full in one frame.
   - Cylinder World 0.11.2 now sends the destination's map regions before the jump, so this should rarely happen
     at the seam. It can still happen after `/tp` or on a slow connection.
   - Suggested fix: use the last good value (or the region at x ± circumference) instead of 0.2, or ease the factor
     over about a second.
2. **Camera waves sampled mid-jump** (`FrameStart`, render order 0.05).
   - For a frame, vanilla's camera can interpolate between the old and the new side of the world. It does this when
     its previous position is still from the old side.
   - Cylinder World 0.11.2 now shifts that previous position at the jump, before the camera runs (render order −0.2).
   - Belt and braces: skip the camera-waves sample when the camera moved more than ~1000 blocks since the last frame.

## Magelan

3. **Drawn height after a jump** (`src/Ship/EntityShip.cs:1746` and `:1771`).
   - On a teleport, `renderBaseY = targetPos.Y − fullHeave`, which eases back over about a second.
   - The ship's drawn height is visibly off when she has water aboard (Sinkage).
   - Fix: on a horizontal jump (same Y), keep `renderBaseY`.
4. **Still-water level when the teleport is delayed** (`ServerTick` ~`:1325`, creep ~`:1490`).
   - `TeleportToDouble` moves the ship only once the destination column is loaded. Meanwhile Pos.Y reverts to the
     y at call time, heave included, and the server reads that as a new still-water level. It creeps back at
     0.5 blocks/s.
   - Rare, because Cylinder World preloads the column.
   - Fix: remember Pos.Y in a `TeleportToDouble` override. On completion with unchanged y, keep BaseY.
5. **Compatibility with Cylinder World's camera shift.**
   - Cylinder World shifts `PlayerCamera.prevPos` only if it is still on the old side of the world, so it won't
     double-shift if Magelan also starts doing it in `OnShipJumped`.

## Earth Map

6. **The seam gutter cuts Antarctica.**
   - `TerrainModel.GutterColumn` (`src/Core/TerrainModel.cs:272-286`) ramps every gutter column down to
     `GutterSeabedY`, with no glacier. The seam (165.55°W) runs through the Ross Ice Shelf and the polar plateau, so
     generated worlds get a sea channel about 2,240 blocks wide from about 78.5°S to the south Wall.
   - Wmap draws it, and it was confirmed on a scratch server: the plateau at y≈300 drops into a strait at sea level.
   - The jump itself is seamless; the channel is what you see on the way to it.
   - Done in Earth Map 0.13.2 (`TerrainModel.TryGutterBridge`, saved as `EarthParams.GutterBridges`):
     - South of 60°S, where both gutter edges are land or ice, the gutter blends the two edge columns, with detail of
       its own in the middle.
     - It depends only on the position across the gutter, so the surface matches block for block across the jump.
     - The ocean map follows it.
     - New worlds get it; existing ones opt in once with `UpgradeExistingWorldGutterBridges`.
   - Cylinder World's side is done (0.11.3): in Earth polar mode the seam band keeps Earth's terrain wherever Earth
     has land or ice south of 60°S (`SeamParams.EarthKeepsSeamColumn`). It neither flattens it nor forces ocean
     there. Today the whole Antarctic gutter is sea, so nothing changes until Earth builds the bridge.
   - Chunks already generated keep the channel.
   - Wmap: it draws the Antarctic strait as open water, because it models sea ice only north of 66°
     (`CylinderSurface.cs:108`).
