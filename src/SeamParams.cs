using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    /// <summary>
    /// Marks a <see cref="SeamParams"/> field that is taken from the config (or the world's options) at every start and
    /// shapes no terrain (CW-0020): the mist, the jump fog pulse, the wall build margin. They are saved and sent with the
    /// layout only because they share its packet. A map cache keyed on the saved layout can leave them out (World Map's
    /// chart cache key; listed in docs/REFLECTION-SURFACE.md).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RuntimeSettingAttribute : Attribute { }

    /// <summary>
    /// World shape. East-west (X) wraps; north-south (Z) ends in ice walls.
    ///
    /// X seam: the physical world is x in [0, W). The playable ring is [t, W - t):
    /// crossing a jump line (x = t or x = W - t) shifts x by the circumference C = W - 2t.
    /// Both X edges are covered by an ocean band of half-width H (flattened, identical on
    /// both sides), so at the moment of the jump the player only sees open sea either way.
    ///
    ///   x: 0 ── t ────── H ════ land ════ W-H ────── W-t ── W
    ///      [   ocean band   ]              [   ocean band   ]
    ///
    /// Z walls: an unbreakable ice cliff fills the first WallDepth (+ face roughness) blocks from each Z edge, up to
    /// WallTopY. From v4 a glacier apron rises exponentially from sea level to the wall foot, with pack ice beyond it.
    /// Everything about the wall and apron depends on x only through the ring angle, so it matches across the seam.
    ///
    /// Layout versions (saved per world, older worlds keep theirs):
    ///   v2: warmest at the map center; v3: equator at the map center, spawn/story moved;
    ///   v4: noise wall face, ice shades, glacier apron, pack ice, barren polar zone, wavy seam coast.
    ///   v10: seam band with ring-periodic seabed relief, a wavy edge and a slope-limited ramp (CW-D25).
    /// </summary>
    [ProtoContract]
    public partial class SeamParams
    {
        public const int CurrentVersion = 10;
        [ProtoMember(9)] public int Version;

        /// <summary>W: world width in blocks (MapSizeX).</summary>
        [ProtoMember(1)] public int WorldWidth;
        /// <summary>t: distance from each world edge to its jump line.</summary>
        [ProtoMember(2)] public int JumpInset;
        /// <summary>H: flattened ocean band reaches this far in from each world edge.</summary>
        [ProtoMember(3)] public int SeamHalfWidth;
        /// <summary>Beyond H, forced oceanicity fades out over this many blocks back to vanilla terrain.</summary>
        [ProtoMember(4)] public int CoastFalloff;
        /// <summary>Y level of the flat seabed inside the band.</summary>
        [ProtoMember(5)] public int SeabedY;

        // Runtime-only (taken from config each load, sent to clients)
        [ProtoMember(6), RuntimeSetting] public float MistDensity;
        [ProtoMember(7), RuntimeSetting] public float MistMin;
        [ProtoMember(8), RuntimeSetting] public int MistRange;

        /// <summary>L: world length in blocks (MapSizeZ).</summary>
        [ProtoMember(10)] public int WorldLength;
        /// <summary>Ice wall thickness from each Z edge to its face (before roughness). 0 = no walls.</summary>
        [ProtoMember(11)] public int WallDepth;
        [ProtoMember(12)] public int WallTopY;
        [ProtoMember(13)] public bool PolarClimate;
        [ProtoMember(14)] public float PoleTemperature;
        [ProtoMember(15)] public float EquatorTemperature;
        /// <summary>Runtime-only: no building this far in front of the wall face.</summary>
        [ProtoMember(16), RuntimeSetting] public int WallBuildMargin;

        /// <summary>Exponent on cos(latitude) for the temperature curve; 1.25 fits Earth's yearly zonal means well.</summary>
        [ProtoMember(17)] public float ClimateCurveExponent;
        /// <summary>Where spawn (and the story locations) were moved to; 0 = not moved (map center).</summary>
        [ProtoMember(18)] public int SpawnX;
        [ProtoMember(19)] public int SpawnZ;

        /// <summary>v4: glacier apron in front of the wall face (0 = none).</summary>
        [ProtoMember(23)] public int ApronDepth;
        /// <summary>v4: apron height above sea level at the wall foot; decays exponentially outward.</summary>
        [ProtoMember(24)] public int ApronHeight;
        /// <summary>v4: sea ice floes on the water beyond the apron.</summary>
        [ProtoMember(25)] public int PackIceDepth;
        /// <summary>v4: forests and shrubs thin out to nothing over this many blocks before the apron.</summary>
        [ProtoMember(26)] public int BarrenDepth;
        [ProtoMember(27)] public long NoiseSeed;
        [ProtoMember(28)] public int SeaLevel;
        /// <summary>v5: the continental shelf (raised seabed) continues this far beyond the ice front.</summary>
        [ProtoMember(29)] public int ShelfExtra;
        /// <summary>v8: extra cold (°C, usually ~0) added across the ice sheet on top of the curve and altitude, so the
        /// plateau at the wall averages the configured wall temperature.</summary>
        [ProtoMember(30)] public float SheetExtraCold;
        /// <summary>Extend temperatures beyond vanilla's -20..+40°C map range (see PolarTemperatureSystem). Fixed at world
        /// creation since 1.1.0-rc.3 (<see cref="ExtendTemperatureFrozen"/>), from the config in older saves; off in worlds
        /// Earth generates, where Earth's own extension works from the real climate (Earth also sets it off by reflection).</summary>
        [ProtoMember(31)] public bool ExtendTemperature;

        /// <summary>Earth polar layout, fixed at world creation: automatic in Earth worlds since CW-D37 (PolarIceMode "default"),
        /// opt-in before. Absent in older saves (= false).</summary>
        [ProtoMember(32)] public bool EarthPolarIce;
        /// <summary>Server-only adapter to the active Earth model; never serialized or required by the client.</summary>
        [ProtoIgnore] public EarthPolarModel EarthPolar;
        /// <summary>Runtime setting, Earth worlds only. Old/default layouts keep their original blocks. Not a
        /// <see cref="RuntimeSettingAttribute"/>: it decides the sea ice new chunks get.</summary>
        [ProtoMember(33)] public bool SeasonalSeaIce;
        /// <summary>Calendar snapshot published on the main thread for generation/preview workers.</summary>
        [ProtoIgnore] public double SeaIceYear;
        [ProtoIgnore] public bool SeaIceFixedSeason;
        /// <summary>Both of the above as one snapshot, replaced whole, so a worker never pairs a new date with the old
        /// season rule. Null until the server publishes a date (then the two fields are read).</summary>
        [ProtoIgnore] public SeaIceDate SeaIceSeason;

        /// <summary>Runtime: fog minimum the mist rises to right after a jump (0 = no pulse), and how long it eases out.</summary>
        [ProtoMember(34), RuntimeSetting] public float JumpFogMin;
        [ProtoMember(35), RuntimeSetting] public float JumpFogSeconds;

        /// <summary>
        /// New Earth-mode worlds whose Earth Map fills its seam gutter with the real seabed (Earth's TerrainModel.GutterSeabed,
        /// CW-D26): the band keeps Earth's seabed instead of the v10 relief. Earth's gutter depends only on the position across
        /// it, so it already matches one ring apart; the band still rebuilds its identical part (gravel on that floor, salt
        /// water, the mantle curtain) and needs no ramp. Fixed at world creation, like the layout.
        /// </summary>
        [ProtoMember(36)] public bool EarthSeabed;

        /// <summary>
        /// CW-D31: new EarthSeabed worlds whose Earth Map carries the seam meridian's land across its gutter too (Earth's
        /// TerrainModel.GutterSeabed ≥ 2: St. Lawrence Island on 169.25°W). The band keeps Earth's land at every latitude, as it
        /// keeps Antarctica, instead of flooding it, and the seam worldgen rebuilds its surface in the identical part
        /// (SeamWorldGen.SeamLandColumn). Fixed at world creation.
        /// </summary>
        [ProtoMember(37)] public bool EarthSeamLand;

        /// <summary>
        /// New EarthSeamLand worlds from 1.1: Earth's lakes, salt lakes and rivers in the band's identical part keep their
        /// water (the land rebuild fills it to Earth's water level) instead of being drained to dry pits. Fixed at world
        /// creation, so a world whose band was generated without it never gets a water column beside a pit on the other
        /// side of the jump. ProtoMembers 20-22 were retired before 0.11.4 and must not be reused.
        /// </summary>
        [ProtoMember(38)] public bool EarthSeamWater;

        /// <summary>
        /// From 1.1.0-rc.3 (CW-D47): <see cref="ExtendTemperature"/> is fixed when the world is created, as Earth fixes its own,
        /// because it also changes the climate readings world generation makes (plants, soil, trader camps near the
        /// poles): turning it over in an existing world would give new chunks different ones than their neighbours. False in
        /// older saves, which keep reading the config at every start as before.
        /// </summary>
        [ProtoMember(39)] public bool ExtendTemperatureFrozen;

        /// <summary>
        /// Whether the seam worldgen flattens the band and lays the jump curtain (config <c>FlattenSeabed</c>). Fixed when the
        /// world is created since 1.1.0 (CW-0011, <see cref="FlattenSeabedFrozen"/>), like the layout: switched later, new
        /// band chunks kept natural terrain beside flattened ones, and mirrored edits paired unrelated cells. Older saves keep
        /// reading the config at every start. Without it the band has no identical part (SeamBlocks pairs nothing there).
        /// </summary>
        [ProtoMember(40)] public bool FlattenSeabed;
        /// <summary>True when <see cref="FlattenSeabed"/> was saved with the world (new records since 1.1.0).</summary>
        [ProtoMember(41)] public bool FlattenSeabedFrozen;

        // v8 (Antarctic ice sheet): ApronDepth = ice sheet width (wall face to its edge), ApronHeight = plateau height
        // above sea level. The sheet rises steeply at its edge and is nearly flat toward the wall (Vialov profile).

        /// <summary>v5: seabed drop from just above sea level at the wall foot to the outer end of the shelf.</summary>
        public const int ShelfDrop = 34;
        /// <summary>v6: the flat seam seabed fades into the natural terrain over this many blocks at the band's inner edge.
        /// v10: the shortest ramp; it gets longer where the natural ground is far from the seabed.</summary>
        public const int SeamBlend = 160;
        /// <summary>v10: the band's identical part ends up to this many blocks further out (toward the jump line) than H - SeamBlend.</summary>
        public const int SeamWave = 64;
        /// <summary>v10: ramp width per block of height between the band's seabed and the natural ground (smoothstep peak 1:4).</summary>
        const double RampPerBlock = 6;
        /// <summary>v10: longest ramp, in blocks (keeps the work strip bounded; ≤ 1:4 for heights up to RampMax / 6).</summary>
        const int RampMaxWidth = 1024;
        /// <summary>v6: how far the fractal ice coast can reach past (or fall short of) the front distance, as a fraction of it.</summary>
        const double IceCoastRoughness = 0.28;

        /// <summary>v9: beyond the ice sheet's edge, mountain tops are planed off (and ice-capped) up to this far out.</summary>
        public const int PlaneReach = 320;
        /// <summary>v9: the planing height rises this many blocks per block beyond the sheet's edge.</summary>
        const double PlaneSlope = 0.25;
        /// <summary>v9: thickness of the ice mantle over buried terrain.</summary>
        public const int IceCover = 3;

        public const int CurtainHalfWidth = 2;
        /// <summary>Spawn and story locations stay at least this far from the wall zone (wall + apron).</summary>
        public const int PolarMargin = 1024;
    }
}
