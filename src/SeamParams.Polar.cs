using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    // SeamParams, part: the polar zone: walls, apron, pack ice, the v5 shelf and the v8 ice sheet. (CW-0020: moved unchanged from SeamParams.cs.)
    public partial class SeamParams
    {
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
    }
}
