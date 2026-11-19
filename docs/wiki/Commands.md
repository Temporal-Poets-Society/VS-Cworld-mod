# Commands

All of Cylinder World's commands start with `/cyl`.

| Command | Who | What it does |
| --- | --- | --- |
| `/cyl` or `/cyl info` | every player | Where you are on the planet: the world's width and ring, where the jump lines are, your X position and **longitude**, how far the nearest jump line is, how far the nearest wall is, your **latitude** and the base temperature there, the temperature now and the yearly average, where spawn was moved, and on the ice sheet your **cold exposure**. In Earth worlds near the poles it also says the sea ice expected here now and whether creatures spawn here. |
| `/cyl goseam [distance]` | players with the `tp` privilege | Teleport onto the sea just before the east jump line, 80 blocks before it unless you give a distance. Good for trying the jump. |
| `/cyl gowall [north/south]` | `tp` | Teleport on top of the north (or south) wall, at your current X. Use creative or spectator mode: the wall guard puts a survival player straight back in front of it. |
| `/cyl golat <degrees>` | `tp` | Teleport to a latitude at your current X: `45` is 45°N, `-30` is 30°S, `0` the equator. You arrive high up; fly down in creative mode. Latitudes very close to the poles land at the edge of the polar zone. |
| `/cyl map` | `tp`, also from the server console | Draw the ring, jump line to jump line (land and sea, temperature, walls, seam, spawn, story locations), to a picture without generating any chunks. It is saved as `VintagestoryData/CylinderWorld/worldmap-<seed>.png`. In Earth worlds it draws Earth's land, sea and ice with the sea ice of the current date; Earth's own `/earth map` shows Earth's terrain without Cylinder World's sea ice and Wall. |

Examples:

```
/cyl
/cyl goseam 200
/cyl golat -60
/cyl gowall south
```

Related game command: `/worldconfig cylinderSeamMist true` switches the seam mist on (see [Settings](Settings)).
