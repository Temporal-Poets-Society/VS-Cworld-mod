using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    // SeamParams, part: latitude and the temperature curve (v3+: equator at the map centre). (CW-0020: moved unchanged from SeamParams.cs.)
    public partial class SeamParams
    {
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
    }
}
