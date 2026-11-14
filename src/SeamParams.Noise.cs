using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    // SeamParams, part: the ring noise (not serialized). (CW-0020: moved unchanged from SeamParams.cs.)
    public partial class SeamParams
    {
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
