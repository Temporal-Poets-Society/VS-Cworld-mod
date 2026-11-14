using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    // SeamParams, part: the east-west seam: the jump lines, the band, its v10 seabed, edge and ramp. (CW-0020: moved unchanged from SeamParams.cs.)
    public partial class SeamParams
    {
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
    }
}
