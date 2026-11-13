using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
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
        [ProtoMember(6)] public float MistDensity;
        [ProtoMember(7)] public float MistMin;
        [ProtoMember(8)] public int MistRange;

        /// <summary>L: world length in blocks (MapSizeZ).</summary>
        [ProtoMember(10)] public int WorldLength;
        /// <summary>Ice wall thickness from each Z edge to its face (before roughness). 0 = no walls.</summary>
        [ProtoMember(11)] public int WallDepth;
        [ProtoMember(12)] public int WallTopY;
        [ProtoMember(13)] public bool PolarClimate;
        [ProtoMember(14)] public float PoleTemperature;
        [ProtoMember(15)] public float EquatorTemperature;
        /// <summary>Runtime-only: no building this far in front of the wall face.</summary>
        [ProtoMember(16)] public int WallBuildMargin;

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

        /// <summary>Opt-in Earth polar layout, fixed at world creation. Absent in older saves (= false).</summary>
        [ProtoMember(32)] public bool EarthPolarIce;
        /// <summary>Server-only adapter to the active Earth model; never serialized or required by the client.</summary>
        [ProtoIgnore] public EarthPolarModel EarthPolar;
        /// <summary>Runtime setting, Earth worlds only. Old/default layouts keep their original blocks.</summary>
        [ProtoMember(33)] public bool SeasonalSeaIce;
        /// <summary>Calendar snapshot published on the main thread for generation/preview workers.</summary>
        [ProtoIgnore] public double SeaIceYear;
        [ProtoIgnore] public bool SeaIceFixedSeason;
        /// <summary>Both of the above as one snapshot, replaced whole, so a worker never pairs a new date with the old
        /// season rule. Null until the server publishes a date (then the two fields are read).</summary>
        [ProtoIgnore] public SeaIceDate SeaIceSeason;

        /// <summary>Runtime: fog minimum the mist rises to right after a jump (0 = no pulse), and how long it eases out.</summary>
        [ProtoMember(34)] public float JumpFogMin;
        [ProtoMember(35)] public float JumpFogSeconds;

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

        // ---------------------------------------------------------------- X: seam

        public double Circumference => WorldWidth - 2.0 * JumpInset;

        public double DistToEdge(double x) => Math.Min(x, WorldWidth - x);

        /// <summary>Distance to the nearest jump line. Symmetric across the jump, so anything keyed on it (fog) doesn't pop.</summary>
        public double DistToJumpLine(double x) => Math.Abs(DistToEdge(x) - JumpInset);

        public bool PastJumpLine(double x) => x < JumpInset || x >= WorldWidth - JumpInset;

        public bool InSeamBand(double x) => DistToEdge(x) < SeamHalfWidth;

        /// <summary>v6: 0 in the flat part of the seam band, rising to 1 at its inner edge (the seabed fades into natural terrain).</summary>
        public double SeamBlendWeight(double x)
        {
            if (Version < 6) return 0;
            double t = Math.Clamp((DistToEdge(x) - (SeamHalfWidth - SeamBlend)) / SeamBlend, 0, 1);
            return SmoothStep(t);
        }

        /// <summary>t² (3 - 2t): 0 → 1 with flat ends, for t in 0..1. Only where the expression stood alone: inside a longer
        /// product (k * t * t * (3 - 2t)) the multiplication order differs and so can the last bit.</summary>
        internal static double SmoothStep(double t) => t * t * (3 - 2 * t);

        // ---------------------------------------------------------------- v10: natural seam band
        //
        //   distance from the world edge:
        //   0 ── t (jump) ── identical part: ring-periodic seabed ──┤ wavy edge ├── ramp: ≥ 160, 6 per block of height ── natural
        //                                                     H - 160 - 64 .. H - 160

        /// <summary>
        /// Distance from the world edge at which the band's identical part ends, at (x, z). Before v10 a straight line at
        /// H - SeamBlend. v10: up to SeamWave closer to the jump line, by noise along z (one field per side), so the
        /// ramp doesn't start on a straight north-south line. It only depends on x through the side, so the ramp
        /// coordinate (distance from the edge minus this) still runs straight out.
        /// </summary>
        public double SeamEdge(double x, double z)
        {
            if (Version < 10) return SeamHalfWidth - SeamBlend;
            double side = x < WorldWidth / 2.0 ? 0 : 7919;
            // tanh never saturates, so no stretch of the edge is straight (a clamp would leave some at either limit)
            double n = 0.5 + 0.5 * Math.Tanh((Noise.SeamEdge.Noise(side, 0, z) - 0.5) * 5);
            return SeamHalfWidth - SeamBlend - SeamWave * n;
        }

        /// <summary>Distance from either jump line that the identical part covers everywhere (the edge's lowest point).</summary>
        public double IdenticalReach => SeamHalfWidth - SeamBlend - (Version >= 10 ? SeamWave : 0) - JumpInset;

        /// <summary>
        /// Top solid block of the band's identical part at (x, z). Before v10 the flat SeabedY. v10: broad rises and
        /// basins, winding channels and small ripples from noise on the ring (CW-D06), so x and x ± C get the same
        /// seabed. Scaled to the water depth so it never comes near the surface.
        /// </summary>
        public int SeamFloorY(double x, double z)
        {
            if (Version < 10) return SeabedY;
            RingCoords(x, out double rx, out double rz);
            double amp = Math.Clamp((SeaLevel - SeabedY - 4) / 2.5, 0, 14);
            double swell = Math.Clamp((Noise.SeabedSwell.Noise(rx, z, rz) - 0.5) * 3.2, -1, 1);
            double line = 1 - Math.Abs(2 * Noise.SeabedChannel.Noise(rx, z + 300, rz) - 1);   // 1 along the channels
            double channel = Math.Pow(Math.Clamp((line - 0.86) / 0.14, 0, 1), 1.5);
            double ripple = (Noise.SeabedRipple.Noise(rx, z * 0.4, rz) - 0.5) * 2;
            double y = SeabedY + amp * swell - 0.7 * amp * channel + Math.Min(1.5, amp) * ripple;
            return (int)Math.Clamp(Math.Round(y), 2, SeaLevel - 3);
        }

        /// <summary>v10: the longest ramp (≤ 1:4 up to RampMax / 6 blocks of height), within a quarter of the world width.</summary>
        public double RampMax => Math.Max(SeamBlend, Math.Min(RampMaxWidth, WorldWidth / 4.0 - SeamHalfWidth));

        /// <summary>Distance from the world edge within which the seam worldgen may change columns.</summary>
        public double SeamWorkReach => Version >= 10 ? SeamHalfWidth - SeamBlend + RampMax : SeamHalfWidth;

        /// <summary>
        /// v10: 0 in the identical part, rising to 1 where the ground is natural again. The ramp runs outward from the
        /// wavy edge over SeamBlend blocks, or RampPerBlock blocks per block of height between the band's seabed and the
        /// natural ground (`delta`), whichever is longer, up to `maxWidth`. A smoothstep is steepest at 1.5 × height /
        /// width, so the ramp is at most 1 : 4 whatever height the terrain generator left.
        /// </summary>
        public double SeamRampWeight(double x, double z, double delta, double maxWidth) => RampWeight(DistToEdge(x) - SeamEdge(x, z), delta, maxWidth);

        /// <summary>SeamRampWeight(x, z, delta, maxWidth) with the SeamEdge(x, z) the caller already has.</summary>
        internal double SeamRampWeightAt(double x, double edge, double delta, double maxWidth) => RampWeight(DistToEdge(x) - edge, delta, maxWidth);

        /// <param name="u">Distance beyond the wavy edge: DistToEdge(x) - SeamEdge(x, z).</param>
        static double RampWeight(double u, double delta, double maxWidth)
        {
            if (u <= 0) return 0;
            double width = Math.Clamp(Math.Abs(delta) * RampPerBlock, SeamBlend, Math.Max(SeamBlend, maxWidth));
            double t = Math.Min(1, u / width);
            return SmoothStep(t);
        }

        /// <summary>
        /// SeamGround(x, z, natural) for a column of the v10 ramp strip (not the curtain, not EarthSeabed, inside
        /// SeamWorkReach, at or beyond the wavy edge), from the SeamEdge(x, z) and SeamFloorY(x, z) the worldgen already
        /// has: the same arithmetic without evaluating the edge noise twice more.
        /// </summary>
        internal int SeamRampGround(double x, int natural, double edge, int floor)
        {
            double weight = RampWeight(DistToEdge(x) - edge, natural - floor, RampMax);
            return (int)Math.Round(floor + (natural - floor) * weight);
        }

        /// <summary>
        /// Top solid block the seam worldgen leaves in a column it rewrites, for a natural ground top `natural` there (any
        /// layout). Not for columns the polar worldgen owns (PolarOwnsSeabed). The worldgen uses it for the ramp, and map
        /// predictors can call it to draw the band exactly; it returns `natural` beyond SeamWorkReach. With EarthSeabed it
        /// is `natural` everywhere: the band's seabed is Earth's own (the caller's natural ground is Earth's terrain).
        /// </summary>
        public int SeamGround(double x, double z, int natural)
        {
            double d = DistToEdge(x);
            if (d >= SeamWorkReach || EarthSeabed) return natural;
            int floor = SeamFloorY(x, z);
            if (DistToJumpLine(x) <= CurtainHalfWidth) return floor;
            double weight = Version >= 10
                ? (d < SeamEdge(x, z) ? 0 : SeamRampWeight(x, z, natural - floor, RampMax))
                : SeamBlendWeight(x);
            return (int)Math.Round(floor + (natural - floor) * weight);
        }

        /// <summary>Where an entity at x ends up after crossing a jump line (x itself if not past one).</summary>
        public double Wrap(double x)
        {
            if (x < JumpInset) return x + Circumference;
            if (x >= WorldWidth - JumpInset) return x - Circumference;
            return x;
        }

        /// <summary>The same logical spot on the other side of the world.</summary>
        public double Mirror(double x) => x < WorldWidth / 2.0 ? x + Circumference : x - Circumference;

        /// <summary>Spawn (world center) is 0°, the jump lines are ±180°.</summary>
        public double Longitude(double x) => (x - JumpInset) / Circumference * 360.0 - 180.0;

        /// <summary>Oceanicity (0..255) to force at (x, z). 255 inside the band, smooth falloff outside; v4 makes the falloff's start wavy.</summary>
        public int OceanForce(double x, double z)
        {
            double d = DistToEdge(x) - SeamHalfWidth;
            if (Version >= 4) d -= 1.6 * CoastFalloff * Contrast(Noise.Coast.Noise(x, 0, z), 1.8);
            if (d <= 0) return 255;
            if (d >= CoastFalloff) return 0;
            double f = 1 - d / CoastFalloff;
            return (int)(255 * f * f * (3 - 2 * f));
        }

        /// <summary>Distance from the world edge beyond which OceanForce is always 0: H + CoastFalloff, plus the coast
        /// noise's largest inward push (1.6 × CoastFalloff) from v4.</summary>
        public double OceanForceReach => SeamHalfWidth + CoastFalloff * (Version >= 4 ? 2.6 : 1.0);

        // ---------------------------------------------------------------- Z: walls, apron, pack ice

        public bool HasWalls => WallDepth > 0 && WorldLength > 0;

        public double DistToZEdge(double z) => Math.Min(z, WorldLength - z);

        /// <summary>How far the rough wall face can bulge out beyond WallDepth.</summary>
        public int WallFaceAmplitude => Version >= 4 ? 18 : 7;

        /// <summary>Columns closer than this to a Z edge may contain wall blocks.</summary>
        public int WallFaceMax => WallDepth + WallFaceAmplitude + 1;

        /// <summary>Furthest the ice can reach from the nominal face (v6's fractal coast can overshoot the front distance).</summary>
        public int ApronReach => Version >= 8 ? (int)Math.Ceiling(ApronDepth * 1.12)
                              : Version >= 6 ? (int)Math.Ceiling(ApronDepth * (1 + IceCoastRoughness)) : ApronDepth;

        /// <summary>Wall + glacier apron: columns owned by the wall worldgen (the seam flatten skips them).</summary>
        public int WallZoneDepth => WallFaceMax + ApronReach;

        /// <summary>Wall + apron + pack ice (+ continental shelf in v5): every column the polar worldgen touches.</summary>
        public int PolarZoneDepth => WallZoneDepth + Math.Max(Math.Max(PackIceDepth, Version >= 5 ? ShelfExtra : 0), Version >= 9 ? PlaneReach : 0);

        /// <summary>
        /// Columns whose seabed the polar worldgen sets, so the seam flatten must skip them. Before v9 this was the whole
        /// wall zone, which left a strip (between the end of the v8 shelf and the wall zone's outer bound) where the seam
        /// band wasn't flattened by either.
        /// </summary>
        public bool PolarOwnsSeabed(double x, double z)
        {
            if (!HasWalls) return false;
            if (EarthPolarIce) return DistToZEdge(z) < WallFaceMax || EarthKeepsSeamColumn(x, z);
            if (Version < 9) return InWallZone(z);
            double dz = DistToZEdge(z);
            if (dz < WallFaceMax) return true;
            // The sheet's edge is at most ApronDepth from the wall (SheetEdgeDistance): beyond that and the shelf, no noise
            // is needed for the answer (the band's chunks ask this for every column, at every latitude)
            if (dz - WallDepth >= Math.Max(0, ApronDepth) + ShelfExtra) return false;
            return dz - WallDepth < SheetEdgeDistance(x) + ShelfExtra;
        }

        /// <summary>
        /// Earth mode: Antarctic land and ice south of 60°S (the continent, its ice shelves, and a bridge Earth Map builds
        /// across its seam gutter) keeps Earth's terrain in the seam band instead of being flattened into a sea channel.
        /// Earth's terrain repeats around the ring, so it matches across the jump. Where Earth has open sea the band is
        /// flattened as before. With EarthSeamLand (CW-D31) any land Earth carries across its gutter is kept too, and Earth's
        /// own ocean map wherever it has land within the forced ocean's reach. Server only: clients have no Earth adapter.
        /// </summary>
        public bool EarthKeepsSeamColumn(double x, double z)
        {
            if (!EarthMayKeepSeamColumn(x, z)) return false;
            // Earth generates columns at integer coordinates
            return EarthKeepsSampled(EarthPolar.Sample(Math.Floor(x), Math.Floor(z)));
        }

        /// <summary>EarthKeepsSeamColumn's test before Earth is sampled: false where nothing can be kept (no Earth adapter,
        /// north of 60°S without EarthSeamLand, outside the world).</summary>
        internal bool EarthMayKeepSeamColumn(double x, double z) => EarthPolarIce && EarthPolar != null
            && !(EarthPolar.Latitude(z) > EarthPolarModel.AntarcticLatitude && (!EarthSeamLand || x < 0 || x >= WorldWidth || z < 0 || z >= WorldLength));

        /// <summary>EarthKeepsSeamColumn's answer from Earth's column at (floor x, floor z): land or ice.</summary>
        internal static bool EarthKeepsSampled(in EarthPolarModel.Column c) => !c.Ocean || c.Glacier;

        /// <summary>CW-D31: land north of 60°S that EarthSeamLand keeps in the band (its surface is rebuilt by the seam worldgen;
        /// Antarctica's belongs to the polar worldgen).</summary>
        public bool EarthSeamLandColumn(in EarthPolarModel.Column c) => EarthSeamLand && !c.Ocean && !c.Glacier && c.Latitude > EarthPolarModel.AntarcticLatitude;

        public bool InWallZone(double z) => HasWalls && DistToZEdge(z) < WallZoneDepth;

        public bool InWallBuildBan(double z) => HasWalls && DistToZEdge(z) < WallFaceMax + WallBuildMargin;

        /// <summary>Logical angle around the ring, 0..2π. Anything built from it is identical on both sides of the seam.</summary>
        double RingAngle(double x) => (x - JumpInset) / Circumference * 2 * Math.PI;

        int Cycles(double period) => Math.Max(1, (int)Math.Round(Circumference / period));

        /// <summary>
        /// Maps x onto a circle whose circumference is the ring, so 3D noise sampled at (rx, anything, rz) is seamless
        /// across the seam and has 1 block ≈ 1 noise unit along the ring.
        /// </summary>
        void RingCoords(double x, out double rx, out double rz)
        {
            double u = RingAngle(x), r = Circumference / (2 * Math.PI);
            rx = r * Math.Cos(u);
            rz = r * Math.Sin(u);
        }

        /// <summary>0..WallFaceAmplitude: how far the wall face bulges out at (x, y).</summary>
        public double WallFaceOffset(double x, int y)
        {
            if (Version < 4)
            {
                double u = RingAngle(x);
                double s = 0.55 * Math.Sin(Cycles(37) * u + y * 0.11)
                         + 0.30 * Math.Sin(Cycles(13) * u - y * 0.23 + 1.7)
                         + 0.15 * Math.Sin(y * 0.5 + 0.4);
                return Math.Clamp(0.5 + 0.5 * s, 0, 1) * WallFaceAmplitude;
            }

            // Big buttresses (~150 blocks) plus seracs and ribs, stretched vertically
            RingCoords(x, out double rx, out double rz);
            double big = Contrast(Noise.FaceBig.Noise(rx, y * 0.25, rz), 2.2);
            double small = Contrast(Noise.FaceSmall.Noise(rx, y * 0.45, rz), 2.0);
            return 12 * big + 6 * small;
        }

        static double Contrast(double n, double k) => Math.Clamp((n - 0.5) * k + 0.5, 0, 1);

        public bool IsWallBlock(double x, int y, double z) =>
            HasWalls && y <= WallTopY && DistToZEdge(z) < WallDepth + WallFaceOffset(x, y);

        /// <summary>Wall block shade: 0 = ice, 1 = snow (v2/v3 only), 2 = light, 3 = dark, 4 = deep.</summary>
        public int WallShade(double x, int y)
        {
            if (Version < 4)
            {
                if (y >= WallTopY - 1) return 1;
                int wave = (int)Math.Round(2.5 * Math.Sin(Cycles(61) * RingAngle(x)));
                return ((y + wave) % 11 + 11) % 11 < 2 ? 1 : 0;
            }
            // Irregular strata: much finer vertically than horizontally
            RingCoords(x, out double rx, out double rz);
            double n = Noise.Strata.Noise(rx * 0.15, y, rz * 0.15);
            if (y >= WallTopY - 1 || n > 0.67) return 2;
            if (n < 0.30) return 4;
            if (n < 0.42) return 3;
            return 0;
        }

        /// <summary>Glacier apron surface y at (x, z): sea level far out, rising exponentially toward the wall foot.</summary>
        public int ApronSurfaceY(double x, double z)
        {
            double d = Math.Max(0, DistToZEdge(z) - WallDepth);
            double f = Math.Exp(-4.0 * d / Math.Max(1, ApronDepth));
            RingCoords(x, out double rx, out double rz);
            double ripple = (Noise.Apron.Noise(rx, z * 0.8, rz) - 0.5) * (2 + 6 * f);
            return SeaLevel + (int)Math.Round(ApronHeight * f + Math.Max(0, ripple));
        }

        /// <summary>Glacier (apron) block shade: 0 = ice, 2 = light, 3 = dark.</summary>
        public int GlacierShade(double x, int y, double z)
        {
            RingCoords(x, out double rx, out double rz);
            return GlacierShadeAt(rx, rz, y, z);
        }

        /// <summary>GlacierShade at the ring position (rx, rz) of x (RingX).</summary>
        internal int GlacierShadeAt(in RingX r, int y, double z) => GlacierShadeAt(r.Rx, r.Rz, y, z);

        int GlacierShadeAt(double rx, double rz, int y, double z)
        {
            double n = Noise.Strata.Noise(rx * 0.3 + 1000, y * 0.8, rz * 0.3 + z * 0.05);
            return n > 0.6 ? 2 : n < 0.42 ? 3 : 0;
        }

        /// <summary>
        /// Everything about a polar column that depends on x alone: the ring position and (v8+ default layouts) the ice
        /// sheet's edge distance. The worldgen works it out once per x of a chunk (RingXAt) and passes it to the *At
        /// methods, instead of the cos/sin and 6 noise octaves again for every column and ice cell. Same arithmetic.
        /// </summary>
        internal readonly struct RingX
        {
            public readonly double X, Rx, Rz;
            /// <summary>SheetEdgeDistance(X); NaN where the layout has no sheet (before v8, Earth mode).</summary>
            public readonly double SheetEdge;
            public RingX(double x, double rx, double rz, double sheetEdge) { X = x; Rx = rx; Rz = rz; SheetEdge = sheetEdge; }
        }

        internal RingX RingXAt(double x)
        {
            RingCoords(x, out double rx, out double rz);
            return new RingX(x, rx, rz, Version >= 8 && !EarthPolarIce ? SheetEdgeAt(rx, rz) : double.NaN);
        }

        /// <summary>True where sea ice floes sit on the water beyond the apron (denser near the apron).</summary>
        public bool IsPackIce(double x, double z)
        {
            if (PackIceDepth <= 0) return false;
            double d = DistToZEdge(z) - WallZoneDepth;
            if (d < 0 || d >= PackIceDepth) return false;
            double density = 1 - d / PackIceDepth;
            RingCoords(x, out double rx, out double rz);
            return Noise.Floes.Noise(rx, z, rz) < 0.2 + 0.65 * density;
        }

        // ---------------------------------------------------------------- v5: ice shelf on a continental shelf
        //
        //   side cut (z outward from the wall):
        //
        //   wall ▌▀▀▀▀▄▄▄▄▄▄▄▄▄▄▄▄▄__________ ice front (2..6 above sea, at a noisy distance F)
        //        ▌~~~~~~~~~~~~~~~~~~▐▓▓▓▓▓▓█ ~~~~~~ floes ~~~~~~~~~~~~~~~~~~~~~~~~~~~~ sea level
        //        ▌ grounded ice     ▐▓▓▓▓▓▓█  water under the floating part
        //        ▌███████████▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄__________ seabed rises toward the wall (shelf)

        /// <summary>v5: distance from the nominal wall face to the ice front at x. Bays and tongues, sometimes close to the wall.</summary>
        public double IceFrontDistance(double x)
        {
            RingCoords(x, out double rx, out double rz);
            if (Version >= 6)
            {
                // Fractal (6-octave fBm, domain-warped): bays within bays at every scale instead of smooth waves
                double warp = (Noise.FrontWarp.Noise(rx, 20, rz) - 0.5) * 500;
                double n = Noise.Front6.Noise(rx + warp, 0, rz - warp);
                double big6 = 0.5 + 0.5 * Math.Tanh((n - 0.5) * 3.0);
                return ApronDepth * Math.Clamp(0.06 + 0.94 * big6, 0.05, 1.0);
            }
            // Soft contrast (tanh never saturates flat, unlike a clamp: no straight stretches of ice front)
            double big = 0.5 + 0.5 * Math.Tanh((Noise.Front.Noise(rx, 0, rz) - 0.5) * 3.2);
            double detail = Noise.FrontDetail.Noise(rx, 5, rz) - 0.5;
            return ApronDepth * Math.Clamp(0.1 + 0.85 * big + 0.25 * detail, 0.05, 1.0);
        }

        /// <summary>Is (x, z) under the wall or its ice (v5: up to the noisy front; v4: the whole apron)?</summary>
        public bool IsIceCovered(double x, double z)
        {
            if (!HasWalls) return false;
            double dz = DistToZEdge(z);
            if (EarthPolarIce)
            {
                if (dz < WallFaceMax) return true;
                if (EarthPolar == null) return false; // client: terrain is sent as ordinary chunk blocks
                var sample = EarthPolar.Sample(x, z);
                return sample.Latitude <= -60 && sample.Glacier || HasSeaIce(x, z, sample);
            }
            if (Version < 5) return dz < WallZoneDepth;
            if (dz < WallFaceMax) return true;
            if (Version >= 8) return SheetCoord(x, z, Math.Max(0, dz - WallDepth)) < 1;
            if (Version >= 6) return IceCoastCoord(x, z, Math.Max(0, dz - WallDepth)) < 1;
            return dz - WallDepth < IceFrontDistance(x);
        }

        /// <summary>What the polar worldgen does with one column in front of the wall face.</summary>
        public struct PolarColumn
        {
            /// <summary>Seabed target (top solid block), or -1 to leave the terrain alone.</summary>
            public int Floor;
            /// <summary>1 under the ice, fading to 0 at the outer end of the shelf: how much of the way from the natural
            /// seabed up to Floor to raise it (so the shelf blends into the seabed instead of ending in a step).</summary>
            public double ShelfBlend;
            /// <summary>Glacier ice fills non-solid cells from IceBottom to IceTop; IceTop = -1 for no ice.</summary>
            public int IceTop, IceBottom;
            /// <summary>A sea ice floe at sea level - 1.</summary>
            public bool Floe;
        }

        public PolarColumn GetPolarColumn(double x, double z)
        {
            var col = new PolarColumn { Floor = -1, IceTop = -1, IceBottom = 1 };
            if (EarthPolarIce)
            {
                if (EarthPolar != null) col.Floe = HasArcticIce(x, z, EarthPolar.Sample(x, z));
                return col; // Earth supplies the land, ice sheets and seabed. Never raise or plane them.
            }
            double dz = DistToZEdge(z);

            if (Version < 5)
            {
                if (ApronDepth > 0 && dz < WallZoneDepth) col.IceTop = ApronSurfaceY(x, z);
                else col.Floe = IsPackIce(x, z);
                return col;
            }

            if (Version >= 8) return GetIceSheetColumn(x, z, dz);

            double d = Math.Max(0, dz - WallDepth);
            double front = IceFrontDistance(x);
            double shelfEnd = front + ShelfExtra;

            if (d < shelfEnd)
            {
                double t = d / shelfEnd;
                col.Floor = SeaLevel + 2 - (int)Math.Round(ShelfDrop * t * t * (3 - 2 * t));
                double u = ShelfExtra > 0 ? Math.Clamp((d - front) / ShelfExtra, 0, 1) : 1;
                col.ShelfBlend = 1 - SmoothStep(u);
            }

            RingCoords(x, out double rx, out double rz);
            if (Version >= 6)
            {
                // Ice where the fractal coast coordinate s < 1 (s = 0 at the wall, ~1 at the front): ragged coast, icebergs, inlets
                double s = IceCoastCoord(x, z, d);
                if (s < 1)
                {
                    // Height of the ice edge above the water, -1 (flush) .. 5. v7: smooth 2D field; v6 used whole-block steps
                    // that varied along x only, which showed up as N-S lines running from the wall to the water.
                    double fb = Version >= 7
                        ? 7 * Noise.Freeboard.Noise(rx, z * 0.9, rz) - 1
                        : Math.Floor(7 * Noise.Apron.Noise(rx, 11, rz)) - 1;
                    // Exponential rise normalized to exactly 0 at the front, so the edge height is just the freeboard
                    double f = (Math.Exp(-4.0 * Math.Max(0, s)) - Math.Exp(-4.0)) / (1 - Math.Exp(-4.0));
                    double ripple = (Noise.Apron.Noise(rx, z * 0.8, rz) - 0.5) * 5 * f;
                    int top6 = SeaLevel + (int)Math.Round(fb + (ApronHeight - fb) * f + Math.Max(0, ripple));
                    col.IceTop = Math.Min(Math.Max(top6, SeaLevel - 1), WallTopY);
                    int draft6 = 3 * (col.IceTop - SeaLevel + 1);
                    col.IceBottom = Math.Max(col.Floor + 1, SeaLevel - 1 - draft6);
                }
                else if (PackIceDepth > 0 && d < front * (1 + IceCoastRoughness) + PackIceDepth)
                {
                    double density6 = Math.Clamp(1 - (d - front) / PackIceDepth, 0, 1);
                    col.Floe = Noise.Floes.Noise(rx, z, rz) < 0.1 + 0.7 * density6 * density6;
                }
                return col;
            }
            if (d < front)
            {
                double freeboard = 2 + 4 * Noise.Apron.Noise(rx, 11, rz);
                double f = Math.Exp(-4.0 * d / front);
                double ripple = (Noise.Apron.Noise(rx, z * 0.8, rz) - 0.5) * (1 + 4 * f);
                int top = SeaLevel + (int)Math.Round(freeboard + (ApronHeight - freeboard) * f + Math.Max(0, ripple));
                col.IceTop = Math.Min(top, WallTopY);
                // Floats where the seabed is deep enough: about 3 blocks below the waterline per block above it
                int draft = 3 * (col.IceTop - SeaLevel);
                col.IceBottom = Math.Max(col.Floor + 1, SeaLevel - draft);
            }
            else if (PackIceDepth > 0 && d < front + PackIceDepth)
            {
                double density = 1 - (d - front) / PackIceDepth;
                col.Floe = Noise.Floes.Noise(rx, z, rz) < 0.15 + 0.7 * density * density;
            }
            return col;
        }

        /// <summary>v6: 0 at the wall face, about 1 at the ice front, perturbed by 2D fractal noise (ice where &lt; 1).</summary>
        double IceCoastCoord(double x, double z, double d)
        {
            RingCoords(x, out double rx, out double rz);
            double coast = (Noise.IceCoast.Noise(rx, z, rz) - 0.5) * 2;
            return d / IceFrontDistance(x) - IceCoastRoughness * coast;
        }

        // ---------------------------------------------------------------- v8: Antarctic ice sheet
        //
        //   THE WALL ▌▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔╲___  tundra (land) / ice cliff + floes (sea)
        //            ▌ plateau ~70 above sea, nearly flat          steep edge
        //            ▌████ grounded on land and on a raised seabed ████▀▀▀ floats near the edge over the sea

        /// <summary>v8: distance from the wall face to the ice sheet's edge at x (fractal, 82..100% of the width).</summary>
        public double SheetEdgeDistance(double x)
        {
            RingCoords(x, out double rx, out double rz);
            return SheetEdgeAt(rx, rz);
        }

        /// <summary>SheetEdgeDistance at the ring position (rx, rz) of x.</summary>
        double SheetEdgeAt(double rx, double rz)
        {
            double warp = (Noise.FrontWarp.Noise(rx, 20, rz) - 0.5) * 800;
            double n = Noise.SheetLobes.Noise(rx + warp, 0, rz - warp);
            double big = 0.5 + 0.5 * Math.Tanh((n - 0.5) * 3.0);
            return ApronDepth * (0.84 + 0.16 * big);
        }

        /// <summary>v8: 0 at the wall face, ~1 at the sheet edge (ice where &lt; 1), with a fractal 2D edge.</summary>
        double SheetCoord(double x, double z, double d) => SheetCoord(x, z, d, SheetEdgeDistance(x));

        /// <param name="edge">SheetEdgeDistance(x), when the caller has it already (6 noise octaves each).</param>
        double SheetCoord(double x, double z, double d, double edge)
        {
            RingCoords(x, out double rx, out double rz);
            return SheetCoordAt(rx, rz, z, d, edge);
        }

        double SheetCoordAt(double rx, double rz, double z, double d, double edge)
        {
            double coast = (Noise.SheetCoast.Noise(rx, z, rz) - 0.5) * 2;
            return d / edge - 0.12 * coast;
        }

        PolarColumn GetIceSheetColumn(double x, double z, double dz)
        {
            RingCoords(x, out double rx, out double rz);
            return GetIceSheetColumnAt(rx, rz, SheetEdgeAt(rx, rz), z, dz, out _);
        }

        /// <summary>
        /// GetPolarColumn(r.X, z) for the worldgen, from the x-only values it has per chunk x (RingXAt). Also gives the v8
        /// sheet coordinate of the column (NaN for other layouts), which PlaneCapAt needs for the same column. A name of its
        /// own: Wmap binds GetPolarColumn and PlaneCap by name.
        /// </summary>
        internal PolarColumn GetPolarColumnAt(in RingX r, double z, out double sheetCoord)
        {
            if (EarthPolarIce || Version < 8)
            {
                sheetCoord = double.NaN;
                return GetPolarColumn(r.X, z);
            }
            return GetIceSheetColumnAt(r.Rx, r.Rz, r.SheetEdge, z, DistToZEdge(z), out sheetCoord);
        }

        /// <param name="edge">SheetEdgeDistance at this ring position.</param>
        PolarColumn GetIceSheetColumnAt(double rx, double rz, double edge, double z, double dz, out double s)
        {
            var col = new PolarColumn { Floor = -1, IceTop = -1, IceBottom = 1 };
            double d = Math.Max(0, dz - WallDepth);
            double shelfEnd = edge + ShelfExtra;

            // Seabed rises toward the wall under the whole sheet, so it's grounded there and floats only near its edge
            if (d < shelfEnd)
            {
                double t = d / shelfEnd;
                col.Floor = SeaLevel + 2 - (int)Math.Round(ShelfDrop * t * t * t);
                double u = ShelfExtra > 0 ? Math.Clamp((d - edge) / ShelfExtra, 0, 1) : 1;
                col.ShelfBlend = 1 - SmoothStep(u);
            }

            s = SheetCoordAt(rx, rz, z, d, edge);
            if (s < 1)
            {
                // Vialov profile (ice sheet with Glen's n = 3): h = H (1 - s^(4/3))^(3/8). Flat plateau, steep edge.
                double h = ApronHeight * Math.Pow(Math.Max(0, 1 - Math.Pow(Math.Max(0, s), 4.0 / 3.0)), 3.0 / 8.0);
                // In the outer 15% the profile is scaled down by a 2D field: high ice cliffs in places, low ramps
                // almost down to the water in others
                double cliff = 0.08 + 0.92 * Noise.Freeboard.Noise(rx + 500, z * 0.7, rz - 500);
                double w = Math.Clamp((1 - s) / 0.15, 0, 1);
                h *= cliff + (1 - cliff) * w * w * (3 - 2 * w);
                double edgeHeight = 7 * Noise.Freeboard.Noise(rx, z * 0.9, rz) - 1;           // -1 (flush) .. 5 at the edge
                double sastrugi = (Noise.Apron.Noise(rx, z * 0.8, rz) - 0.5) * 2.4;            // gentle snow dunes
                int top = SeaLevel + (int)Math.Round(Math.Max(h + sastrugi, edgeHeight));
                col.IceTop = Math.Min(Math.Max(top, SeaLevel - 1), WallTopY);
                int draft = 3 * (col.IceTop - SeaLevel + 1);
                col.IceBottom = Math.Max(col.Floor + 1, SeaLevel - 1 - draft);
            }
            else if (PackIceDepth > 0 && d < edge * 1.12 + PackIceDepth)
            {
                double density = Math.Clamp(1 - (d - edge) / PackIceDepth, 0, 1);
                col.Floe = Noise.Floes.Noise(rx, z, rz) < 0.1 + 0.7 * density * density;
            }
            return col;
        }

        /// <summary>
        /// v9: planing height at (x, z), or int.MaxValue for none. Terrain above (cap - IceCover) is removed and the
        /// column gets an ice mantle up to the cap, so no mountain sticks out of the sheet and nothing bare is left to
        /// spawn on. The worldgen uses max(this, the sheet's own ice surface): on the sheet, buried mountains end flush
        /// with the ice. Through the edge it's one continuous ramp (plateau height at the edge, 1 block per 4 up and
        /// out), so mountains there get ice-capped slopes down to the sheet instead of a cut face.
        /// </summary>
        public int PlaneCap(double x, double z)
        {
            if (EarthPolarIce) return int.MaxValue;
            if (Version < 9 || !HasWalls || ApronDepth <= 0) return int.MaxValue;
            double dz = DistToZEdge(z);
            if (dz < WallFaceMax) return WallTopY;
            double d = Math.Max(0, dz - WallDepth);
            double edge = SheetEdgeDistance(x);
            return PlaneCapBeyond((SheetCoord(x, z, d, edge) - 1) * edge);
        }

        /// <summary>PlaneCap(r.X, z), with the sheet coordinate GetPolarColumnAt gave for the same column.</summary>
        internal int PlaneCapAt(in RingX r, double z, double sheetCoord)
        {
            if (EarthPolarIce) return int.MaxValue;
            if (Version < 9 || !HasWalls || ApronDepth <= 0) return int.MaxValue;
            double dz = DistToZEdge(z);
            if (dz < WallFaceMax) return WallTopY;
            return PlaneCapBeyond((sheetCoord - 1) * r.SheetEdge);
        }

        /// <param name="beyond">Blocks past the sheet's edge (&lt; 0 on the sheet).</param>
        int PlaneCapBeyond(double beyond)
        {
            if (beyond >= PlaneReach) return int.MaxValue;
            return SeaLevel + ApronHeight + 1 + (int)Math.Round(PlaneSlope * beyond);
        }

        /// <summary>v8: 0 off the sheet; on it, 0.3 at the edge rising to 1 at the wall (how harsh the winter rule is).</summary>
        public double SheetHarshness(double x, double z)
        {
            if (EarthPolarIce)
            {
                if (!HasWalls || EarthPolar == null || !IsIceCovered(x, z)) return 0;
                double latitude = EarthPolar.Latitude(z);
                double edge = latitude < 0 ? -EarthPolarModel.AntarcticLatitude : EarthPolarModel.ArcticIceLatitude;
                return 0.3 + 0.7 * Math.Clamp((Math.Abs(latitude) - edge) / (90 - edge), 0, 1);
            }
            if (Version < 8 || !HasWalls || ApronDepth <= 0 || !IsIceCovered(x, z)) return 0;
            double f = Math.Clamp(1 - (DistToZEdge(z) - WallDepth) / ApronDepth, 0, 1);
            return 0.3 + 0.7 * f;
        }

        /// <summary>
        /// True yearly-average temperature (°C) at (z, y): the climate curve, cooled by altitude like vanilla
        /// (1 °C per 6.375 blocks above sea level), plus the ice sheet's extra cold. Unlike vanilla's stored value,
        /// it isn't capped at -20 / +40.
        /// </summary>
        public float TrueBaseTemperature(double x, double z, double y) => TrueBaseFromCurve(z, y, TemperatureAt(z));

        /// <summary><see cref="TrueBaseTemperature(double, double, double)"/> with the climate curve at z already known (the
        /// live temperature correction needs the curve too, and it runs on every climate query).</summary>
        internal float TrueBaseFromCurve(double z, double y, float curve)
        {
            double t = curve - (y - SeaLevel) / 6.375;
            if (Version >= 8 && SheetExtraCold != 0 && ApronDepth > 0)
            {
                double f = Math.Clamp(1 - (DistToZEdge(z) - WallDepth) / ApronDepth, 0, 1);
                t += SheetExtraCold * f;
            }
            return (float)t;
        }

        /// <summary>0 far from the walls, 1 at the apron: forests and shrubs are scaled by (1 - this).</summary>
        public double BarrenFactor(double z)
        {
            if (EarthPolarIce) return 0; // Earth Map's climate governs vegetation outside the lifeless border.
            if (BarrenDepth <= 0 || !HasWalls) return 0;
            double d = DistToZEdge(z) - WallZoneDepth;
            return Math.Clamp(1 - d / BarrenDepth, 0, 1);
        }

        // ---------------------------------------------------------------- climate (v3+: equator at map center)

        /// <summary>Low sea ice only; the real Earth land mask protects Greenland and all northern islands.</summary>
        public bool HasArcticIce(double x, double z, EarthPolarModel.Column sample)
        {
            if (!EarthPolarIce || !HasWalls || sample.Latitude < EarthPolarModel.ArcticIceLatitude || !sample.Ocean) return false;
            if (DistToWallFace(z) <= EarthPolarModel.LifelessBorder) return true;
            double coverage = Math.Max(EarthPolarModel.ArcticCoverage(sample), EarthIceBlend(x, z));
            if (coverage <= 0) return false;
            if (coverage >= 1) return true;
            RingCoords(x, out double rx, out double rz);
            return Noise.Floes.Noise(rx, z, rz) < coverage;
        }

        public bool IsPlanetLayout => Version >= 3;

        public double EquatorZ => WorldLength / 2.0;

        /// <summary>Distance from the equator to either wall face (= the pole).</summary>
        public double PoleDistance => EquatorZ - WallDepth;

        /// <summary>Blocks from z to the nearest wall face; negative inside the wall.</summary>
        public double DistToWallFace(double z) => DistToZEdge(z) - WallDepth;

        /// <summary>-1 south pole .. 0 equator .. 1 north pole (north = small z).</summary>
        public double SignedLatitude(double z) => PoleDistance > 0 ? Math.Clamp((EquatorZ - z) / PoleDistance, -1, 1) : 0;

        /// <summary>|latitude| (0 equator .. 1 pole) where the curve gives temperature t.</summary>
        public double AbsLatitudeForTemperature(float t)
        {
            if (EquatorTemperature == PoleTemperature) return 0;   // a flat curve: every latitude has it
            double ratio = Math.Clamp((t - PoleTemperature) / (double)(EquatorTemperature - PoleTemperature), 0, 1);
            return Math.Acos(Math.Pow(ratio, 1.0 / CurveExponent)) / (Math.PI / 2);
        }

        double CurveExponent => ClimateCurveExponent > 0 ? ClimateCurveExponent : 1;

        /// <summary>0 at the Z edges (poles), 1 at the map center. (v2 layout)</summary>
        public double EquatorFactor(double z) => WorldLength > 0 ? Math.Clamp(1 - Math.Abs(2 * z / WorldLength - 1), 0, 1) : 1;

        /// <summary>
        /// Base temperature in °C at world z (before jitter and altitude). Earth-like: cos(latitude)^exponent, so the
        /// tropics are broad and flat and it gets cold fastest between ~40° and 70°.
        /// </summary>
        public float TemperatureAt(double z)
        {
            if (!IsPlanetLayout)
            {
                return (float)(PoleTemperature + (EquatorTemperature - PoleTemperature) * Math.Sin(EquatorFactor(z) * Math.PI / 2));
            }
            double c = Math.Cos(Math.Abs(SignedLatitude(z)) * Math.PI / 2);
            return (float)(PoleTemperature + (EquatorTemperature - PoleTemperature) * Math.Pow(Math.Max(0, c), CurveExponent));
        }

        /// <summary>
        /// Latitude for the calendar (seasons, sun path, day length, hemisphere): -1 south pole .. 1 north pole.
        /// v3+: the real geometric latitude, so the south has reversed seasons and both walls get polar day/night.
        /// v2: derived from temperature and positive everywhere.
        /// </summary>
        public double Latitude(double z) => IsPlanetLayout ? SignedLatitude(z) : 1 - Math.Clamp((TemperatureAt(z) + 20) / 60.0, 0, 1);

        // ---------------------------------------------------------------- noise (not serialized)

        NoiseSet noise;
        /// <summary>Made on first use by whichever worldgen thread gets there first; the others see the finished set (a
        /// plain ??= could publish the reference before the noises in it on a weak-memory CPU).</summary>
        NoiseSet Noise => System.Threading.Volatile.Read(ref noise) ?? CreateNoise();

        NoiseSet CreateNoise()
        {
            var made = new NoiseSet(NoiseSeed);
            return System.Threading.Interlocked.CompareExchange(ref noise, made, null) ?? made;
        }

        /// <summary>Stateless simplex noises; safe to share between worldgen threads.</summary>
        class NoiseSet
        {
            public readonly NormalizedSimplexNoise FaceBig, FaceSmall, Strata, Apron, Floes, Coast, Front, FrontDetail, Front6, FrontWarp, IceCoast, Freeboard, SheetLobes, SheetCoast;
            public readonly NormalizedSimplexNoise SeamEdge, SeabedSwell, SeabedChannel, SeabedRipple;

            public NoiseSet(long seed)
            {
                FaceBig = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 150.0, 0.55, seed + 11);
                FaceSmall = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 22.0, 0.6, seed + 12);
                Strata = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 18.0, 0.5, seed + 13);
                Apron = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 40.0, 0.55, seed + 14);
                Floes = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 26.0, 0.5, seed + 15);
                Coast = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 1400.0, 0.45, seed + 16);
                Front = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 350.0, 0.5, seed + 17);
                FrontDetail = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 45.0, 0.5, seed + 18);
                Front6 = NormalizedSimplexNoise.FromDefaultOctaves(6, 1 / 700.0, 0.58, seed + 19);
                FrontWarp = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 900.0, 0.5, seed + 20);
                IceCoast = NormalizedSimplexNoise.FromDefaultOctaves(5, 1 / 120.0, 0.6, seed + 21);
                Freeboard = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 70.0, 0.5, seed + 22);
                SheetLobes = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 1800.0, 0.5, seed + 23);
                SheetCoast = NormalizedSimplexNoise.FromDefaultOctaves(6, 1 / 260.0, 0.58, seed + 24);
                SeamEdge = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 420.0, 0.5, seed + 25);
                SeabedSwell = NormalizedSimplexNoise.FromDefaultOctaves(4, 1 / 360.0, 0.5, seed + 26);
                SeabedChannel = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 520.0, 0.45, seed + 27);
                SeabedRipple = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 24.0, 0.5, seed + 28);
            }
        }
    }
}
