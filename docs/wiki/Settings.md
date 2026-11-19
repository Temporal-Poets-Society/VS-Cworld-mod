# Settings

Cylinder World has one world option and a config file. The world option is per world; the config file is per server
(or per game, in single player).

## World option (hidden from the creation screen)

Since 1.1.1 Cylinder World has nothing on the world-creation screen. Its one world option is set by an admin with
the game's `/worldconfig` command:

| Key | Values | Default in new worlds | What it does |
| --- | --- | --- | --- |
| `cylinderSeamMist` | `true`, `false` | `false` | Sea mist around the jump line and a short fog after a jump. Off: no mist, and your game is sent the whole view around the arrival point before each jump instead. |

Example: `/worldconfig cylinderSeamMist true`, then restart the world (single player) or the server. A dedicated
server can also set it in its world configuration before the world is made. The change applies
from the next start. Worlds made before 1.1.0 have no such key; they follow the config's `SeamMist` (on by default)
until an admin sets it.

## Config file: `VintagestoryData/ModConfig/cylinderworld.json`

The file is written with its defaults when a world or the server first starts with the mod, and that first world
already has the defaults saved in it: to change layout settings for your very first world, create the file yourself
before that. Edit it while the game or server is stopped. A file that can't be read (a wrong value type, broken JSON) is
left as it is, the game runs on the defaults, and the server log says so: fix the file and restart. A readable
`"Enabled": false` is still obeyed. A misspelled setting name is ignored and dropped when the file is written back.
Values outside their limits are brought back into them (limits are given below where there are any).

**Most settings are saved into a world when it is first started**, so changing them later only affects new worlds. The
ones marked **live** are read at every start and also change existing worlds.

### On or off

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `true` | **Live.** `false` switches the whole mod off on this server, in existing worlds too. |

### The ring and the seam

| Setting | Default | What it does |
| --- | --- | --- |
| `EarthProportions` | `true` | In a world more than twice as wide as long (by at least twice the automatic jump inset), make the ring exactly twice the world's length, as on Earth. Needs `JumpInset` 0 and `PolarWalls` on. |
| `JumpInset` | `0` (automatic) | Distance from each world edge to its jump line, in blocks. Automatic: the server's view distance plus 32, rounded up to 256 (512 by default); with Earth proportions, (width − 2 × length) / 2 rounded down to 256 (25,600 in a 256,000 × 102,400 world). |
| `SeamHalfWidth` | `0` (automatic) | Width of the ocean band at each edge. Automatic makes it wide enough that you never see where the band ends from the jump line. |
| `CoastFalloff` | `1024` | How far past the band the forced ocean fades back to normal terrain. |
| `SeabedDepth` | `40` | How deep the band's seabed lies below sea level, on average (at least 3). |
| `FlattenSeabed` | `true` | Build the band as identical seabed and salt water on both sides. Off: both sides keep natural terrain, and block changes there aren't mirrored. Saved with worlds made with 1.1.0 or later; worlds made with 1.0.x read it at every start. |
| `ProtectSeamBand` | `true` | **Live.** No building or breaking in the band (creative mode is exempt). |
| `WrapOtherEntities` | `true` | **Live.** Also carry drifting boats, dropped items and animals across the seam. |
| `JumpCooldown` | `3` | **Live.** Seconds after a jump before the same player can jump again (0 = off). |
| `SyncWeatherAcrossSeam` | `true` | **Live.** Keep the weather the same on both sides of the seam. |

### Loading the far side before a jump (all live)

| Setting | Default | What it does |
| --- | --- | --- |
| `PrepareDistance` | `96` | Start loading the far side this many blocks before the line. |
| `PrepareSeconds` | `12` | Faster travellers start earlier: this many seconds before they reach the line (at most 512 blocks). |
| `PreSendChunks` | `true` | Send the far side to the player's game before the jump. |
| `PreSendDiscRadius` / `PreSendAhead` | `6` / `8` | Chunks sent around the arrival point, and further ahead along the heading (each at most 16). |
| `PreSendFullView` | `true` | Send at least the player's whole view around the arrival point (as far as the mist lets you see, when it is on). |
| `UnloadOldSideDelay` / `UnloadOldSideSeconds` | `1.5` / `6` | After a jump, how many seconds the side you left stays loaded beyond the pre-sent area, and over how many seconds it is then unloaded (0 = all at once). |

### Seam mist (all live)

| Setting | Default | What it does |
| --- | --- | --- |
| `SeamMist` | `true` | The mist in worlds without the `cylinderSeamMist` world option (worlds made before 1.1.0). The world option wins. |
| `MistDensity` / `MistMin` / `MistRange` | `0.012` / `0.2` / `200` | How thick the mist is around the jump line, and over how many blocks. |
| `JumpFogMin` / `JumpFogSeconds` | `0.6` / `1.5` | The short fog after a jump (0 to 1; 0 = off), only while the arrival area is still loading. |

### Walls and polar ice

| Setting | Default | What it does |
| --- | --- | --- |
| `PolarWalls` | `true` | Ice walls at the north and south ends. |
| `WallDepth` | `48` | Thickness of the Wall from the world edge to its face (at least 8). |
| `WallTopY` | `0` (automatic) | Height of the Wall's top. Automatic: the ice plateau plus `WallAbovePlateau`. |
| `WallAbovePlateau` | `48` | How far the Wall rises above the ice plateau (at least 8). |
| `WallBuildMargin` | `32` | **Live.** No placing or breaking blocks this many blocks in front of the Wall's face. |
| `WallGuard` | `true` | **Live.** Survival players on top of or behind the Wall are put back in front of it. |
| `PolarIceMode` | `default` | `default`: Earth's real polar lands in a world Earth generates, the ice plateaus otherwise. `earth`: the same, with a warning if Earth isn't generating the world. `plateaus`: always the ice plateaus. Any other value counts as `default`, with a warning. |
| `SeasonalSeaIce` | `true` | **Live.** Earth worlds only: the Arctic and Southern Ocean freeze in winter and thaw in summer. Off stops the changes; blocks already changed stay. |
| `IceSheetLatitude` | `85` | Ordinary worlds: the ice sheet covers everything from this latitude to the Wall. |
| `PlateauHeight` | `70` | Height of the ice plateau above sea level. |
| `TundraLatitude` | `76` | Trees fade out from this latitude to the ice sheet. |
| `PackIceDepth` | `256` | How far sea ice floes reach beyond the ice sheet's edge. |
| `ShelfExtra` | `320` | How far the raised seabed under the ice continues beyond its edge. |

### Climate, spawn and story

| Setting | Default | What it does |
| --- | --- | --- |
| `PolarClimate` | `true` | Replace vanilla's temperature bands and latitude with the planet's. |
| `EquatorTemperature` / `PoleTemperature` | `30` / `-30` | Yearly mean temperature (°C) at the equator and at the walls. |
| `ClimateCurveExponent` | `1.25` | How the temperature falls toward the poles (0.3 to 4); 1.25 matches Earth, higher makes the mid-latitudes colder. |
| `ExtendTemperatureRange` | `true` | Let live temperatures go below vanilla's −20 °C. Off in Earth worlds, where Earth extends the real climate itself. Saved with worlds made with 1.1.0 or later; worlds made with 1.0.x read it at every start. |
| `WallMeanTemperature` | `-40` | Yearly mean temperature on the ice plateau at the Wall. |
| `MoveSpawn` | `true` | Move spawn and the story locations to the latitude of the world's starting climate. |
| `SpawnHemisphere` | `north` | `north` or `south`. |
| `FitStoryToWorld` | `true` | Scale down the story locations' distances if the chain wouldn't fit between the walls. |

### The winter rule (all live)

| Setting | Default | What it does |
| --- | --- | --- |
| `IceSheetWinterRule` | `true` | The cold exposure meter on the ice sheet. |
| `ColdStart` / `ColdFull` | `-40` / `-65` | The meter starts filling below this temperature (°C) and fills at full rate at or below the second. |
| `FreezeMinutes` | `4` | Minutes to a full meter at the Wall at full rate. |
| `FireFactor` | `0.5` | Rate multiplier near a fire. |
| `FrostDamagePerSecond` | `0.5` | Damage per second once the meter is full. |

## Vanilla settings that interact

- **World size** (World generation tab): the strip's width and length. See [How it works](How-it-works).
- **Starting climate:** picks the spawn latitude.
- **Polar-Equator distance:** replaced by the planet's own; a new world is given the planet's value so other mods that
  read it agree.
- **World type:** the strip's terrain needs the standard world type. In a superflat world the wrap still works, but
  there is no seam band, wall or climate.
