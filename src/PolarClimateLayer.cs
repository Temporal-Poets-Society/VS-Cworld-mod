using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    /// <summary>
    /// Wraps vanilla's climate map generator and replaces the temperature channel with a single curve:
    /// PoleTemperature at both Z edges, EquatorTemperature at the map center. Vanilla's realistic climate instead
    /// repeats pole/equator bands every 2 × polarEquatorDistance ("zebra"), anchored on the spawn temperature.
    /// Rain and geologic activity are kept from vanilla.
    ///
    /// Climate pixels are packed as (temperature &lt;&lt; 16) | (rain &lt;&lt; 8) | geologicActivity, temperature 0..255
    /// meaning -20..40°C. Colder targets clamp at -20°C here; altitude and seasons still push actual temps lower, and
    /// PolarTemperatureSystem extends live temperatures to the curve. The jitter is added to the clamped value, so where the
    /// curve is below -20 it can only warm a pixel (stored -20..-17°C); the live correction keeps that variation on top of
    /// the curve.
    /// </summary>
    public class PolarClimateLayer : MapLayerBase
    {
        readonly MapLayerBase vanilla;
        readonly SeamParams p;
        readonly double blocksPerPixel;
        readonly float tempMul;
        readonly long seed;

        public PolarClimateLayer(long seed, MapLayerBase vanilla, SeamParams p, double blocksPerPixel, float tempMul) : base(seed)
        {
            this.vanilla = vanilla;
            this.p = p;
            this.blocksPerPixel = blocksPerPixel;
            this.tempMul = tempMul;
            this.seed = seed;
        }

        public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
        {
            int[] result = vanilla.GenLayer(xCoord, zCoord, sizeX, sizeZ);

            for (int z = 0; z < sizeZ; z++)
            {
                int baseTemp = Climate.DescaleTemperature(p.TemperatureAt((zCoord + z) * blocksPerPixel));
                for (int x = 0; x < sizeX; x++)
                {
                    int i = z * sizeX + x;
                    int temp = GameMath.Clamp((int)((baseTemp + Jitter(RingPixel(xCoord + x), zCoord + z)) * tempMul), 0, 255);
                    result[i] = (result[i] & 0xFFFF) | (temp << 16);
                }
            }

            return result;
        }

        /// <summary>
        /// Pixels beyond a jump line take the jitter of their mirror one ring away, so both sides of the jump get the same
        /// temperature (or one could snow while the other rains). Pixels on the ring keep their own.
        /// </summary>
        int RingPixel(int x)
        {
            int ringPixels = (int)Math.Round(p.Circumference / blocksPerPixel);
            if (ringPixels <= 0) return x;
            double bx = x * blocksPerPixel;
            return bx < p.JumpInset ? x + ringPixels : bx >= p.WorldWidth - p.JumpInset ? x - ringPixels : x;
        }

        /// <summary>Per-pixel variation of about ±3°C, like vanilla's noise. Stateless, so it's safe on worldgen threads.</summary>
        int Jitter(int x, int z)
        {
            unchecked
            {
                ulong h = (ulong)seed ^ ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL);
                h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29;
                return (int)(h % 27) - 13;
            }
        }
    }
}
