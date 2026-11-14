# What other mods read from Cylinder World by reflection

Earth, World Map and Sea use Cylinder World without a build reference: they find its mod system by type name and read
these members by name. Renaming one, changing its type or kind (field or property), or adding a public overload to a
method World Map binds breaks those mods in their worlds, not at their build. `tests/Contract/run.ps1` checks each
member below by the lookup its consumer uses and names the consumer when one fails (CW-0012). Change this list and the
check together.

## The mod system

| Member | Kind | Read by |
| --- | --- | --- |
| `CylinderWorld.CylinderWorldCore` | type, found among the mod systems by full name | Earth, World Map, Sea |
| `CylinderWorldCore.Params` | public instance property (`SeamParams`, null where the mod is off) | Earth, World Map, Sea |
| `CylinderWorldCore.Config` | public instance property (`CylinderWorldConfig`) | Earth (preflight), World Map |
| `CylinderWorldCore.SetSpawn(int, int)` | public instance method, returns `bool` | Earth (new worlds) |
| save key `cylinderworld-seam` | the world's record (`CylinderWorldCore.SaveKey`) | Earth (its new-world test, `EarthMapSystem.CylinderRecordKey`) |
| `Config.Enabled`, `PolarWalls`, `PolarClimate` | `bool` | Earth (`CylinderCompat.HasEarthConfiguration`) |
| `Config.FlattenSeabed` | `bool` | World Map (worlds saved before 1.1; newer ones have `SeamParams.FlattenSeabed`) |

## `SeamParams`

| Member | Kind | Read by |
| --- | --- | --- |
| `JumpInset`, `SeamHalfWidth`, `SeabedY`, `SpawnX`, `SpawnZ`, `WallDepth`, `WallTopY`, `SeaLevel`, `Version` | number fields | Earth (`CylinderCompat.Get`), World Map; Sea reads `JumpInset` as a field |
| `Circumference`, `EquatorZ`, `PoleDistance`, `WallFaceMax`, `PolarZoneDepth` | number properties | Earth, World Map; Sea reads `Circumference` as a property |
| `SeamWorkReach` | number property (World Map tests for the property) | World Map |
| `PolarClimate`, `EarthPolarIce`, `FlattenSeabed` | `bool` fields | Earth, World Map |
| `IsPlanetLayout`, `HasWalls` | `bool` properties | World Map |
| `ExtendTemperature` | writable `bool` field | Earth (switches it off) |
| `EarthPolar` | field (`EarthPolarModel`, server only) | World Map |
| `IceCover`, `CurtainHalfWidth` | `int` constants | World Map |
| `InSeamBand(double)`, `SeamBlendWeight(double)`, `DistToEdge(double)`, `DistToJumpLine(double)`, `DistToZEdge(double)` | methods returning `bool` / `double` | World Map |
| `SeamGround(double, double, int)`, `SeamFloorY(double, double)`, `PlaneCap(double, double)`, `OceanForce(double, double)`, `EarthSnowEighths(double, double)` | methods returning `int` | World Map |
| `PolarOwnsSeabed(double, double)`, `IsWallBlock(double, int, double)` | methods returning `bool` | World Map |
| `GetPolarColumn(double, double)` | method; its result's fields `Floor` (int), `ShelfBlend` (double), `IceTop`, `IceBottom` (int), `Floe` (bool) | World Map |
| `HasArcticIce(double, double, EarthPolarModel.Column)`, `EarthIceRise(double, double, EarthPolarModel.Column)` | methods returning `bool` / `int` | World Map |
| the `[ProtoMember]` fields | World Map's chart cache key, less `Mist*`, `JumpFog*` and `WallBuildMargin` (runtime settings) | World Map |
| `[RuntimeSetting]` (`RuntimeSettingAttribute`) | on exactly those six runtime fields (CW-0020), for a cache key to read instead of name prefixes | World Map (not yet) |

## `EarthPolarModel`

| Member | Kind | Read by |
| --- | --- | --- |
| `Latitude(double)` | method returning `double` | World Map |
| `Sample(double, double)` | method returning `EarthPolarModel.Column` | World Map |
| `LifelessBorder` | `int` constant | World Map |

## How each one looks them up

- **Earth** (`Earth/src/CylinderCompat.cs`, `EarthMapSystem.cs`): `GetProperty("Params"|"Config", Public | Instance)`;
  numbers and switches by `GetField(name, Public | Instance)`, else `GetProperty`; `SetSpawn` by its parameter types;
  `ExtendTemperature` by `GetField`. A missing member makes Earth refuse an Earth world with a named line.
- **World Map** (`Wmap/src/Integrations/WorldSource.cs`, `CylinderSurface.cs`): fields and properties by
  `GetField(name)`, else `GetProperty(name)`. Methods by name and parameter types from Wmap `c865281` (0.5.3);
  the published 0.5.0 to 0.5.3-rc.3 bind them by name alone (`GetMethod(name)`), which throws once a method has a
  public overload. The check covers both.
- **Sea** (`Sea/src/SeaSystem.Server.cs`): `Params`, then `GetProperty("Circumference")` and `GetField("JumpInset")`;
  without them the wind does not line up at the seam (a warning, no failure).
- Not production: Hydrology's test tools dump `SeamParams`' public fields (`tests/server/wateraudit`).
