# Compatibility

## Other mods of this suite

| Mod | What they do together |
| --- | --- |
| **Earth** | Earth requires Cylinder World and builds the real Earth on its strip: the real latitudes, the seam in the Bering Strait, Antarctica and the Arctic at the poles with seasonal sea ice, and the walls beyond them. Earth's climate replaces Cylinder World's temperature curve, and Earth turns off Cylinder World's own temperature extension (Earth extends the real climate itself). Earth's land that the seam cuts (St. Lawrence Island, Antarctica) continues across it. Use Earth 2.0.x with Cylinder World 1.1.x. |
| **Sea** | Sea's waves, wind and storms repeat exactly around the ring, so the sea looks and moves the same on both sides of the jump. |
| **World Map** | World Map draws the seam and the polar ice and charts the ring with continuous longitudes. |
| **Hydrology** | No direct link; both work in Earth worlds. |

With Earth's weather on, the seam's own rain handling steps aside and Earth's weather, which is the same on both sides,
takes over.

## Other mods

- **Terrain generators:** Cylinder World is built for the game's standard terrain (and for Earth). Another terrain mod
  that replaces the standard world generation may not get the seam band, walls or climate; this hasn't been tested.
- **Sky and season mods** that read the game's latitude follow the planet, since Cylinder World sets it.
- **Superflat** worlds: the wrap works, but there is no band, wall or planet climate.
- Crossings were checked with one player at a time (on foot, by raft, with other entities). Several players crossing
  together in multiplayer have not been checked.

## Saves

- **Existing worlds keep their layout.** A world's ring, seam, walls, climate and spawn are saved when it is first
  started, and later versions and config changes don't move them.
- **Adding the mod to an existing world:** If you add the mod to an existing world it still switches on: the world wraps and new chunks get the seam sea,
  walls and planet climate, but chunks already generated near the edges keep their terrain and the spawn stays where
  it is. To leave an existing world alone, set `Enabled` to `false`.
- **Removing the mod** from a world: the world no longer wraps and its temperatures and seasons go back to vanilla's.
  The sea band stays as terrain, but the Wall and the mod's own ice and sea-ice blocks become unknown blocks.
- Worlds made with 1.1.0 or 1.1.1 work in 1.1.2 unchanged. Worlds made with 1.0.x keep their layout; they read `FlattenSeabed`,
  `ExtendTemperatureRange` and `SeamMist` from the config.
- A world made by a newer Cylinder World is refused by an older one, rather than generated wrongly.
- A world with Earth's polar layout doesn't start without a matching Earth installed.
- Updating never regenerates or deletes chunks you have already explored.
