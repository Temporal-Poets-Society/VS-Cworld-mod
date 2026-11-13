using System;

namespace CylinderWorld
{
    public enum SeaIceKind { Open, Seasonal, Permanent }

    /// <summary>The sea-ice calendar position (0..1 of the year) and whether it is a fixed season override.</summary>
    public sealed record SeaIceDate(double Year, bool FixedSeason);

    /// <summary>Calendar-driven gameplay sea ice, using Earth's ocean mask and coldest/warmest monthly climate.
    /// The one-month lag approximates ocean heat storage. This is not a historical sea-ice reconstruction.</summary>
    public static class SeaIcePolicy
    {
        public const double NorthMinimum = 8.5 / 12; // September
        public const double NorthMaximum = 2.5 / 12; // March

        public static bool InPolarOcean(EarthPolarModel.Column c) => c.Ocean && (c.Latitude >= EarthPolarModel.ArcticIceLatitude || c.Latitude <= EarthPolarModel.SeasonalSouthLatitude);

        public static bool PermanentApproach(SeamParams p, double x, double z) =>
            p.DistToWallFace(z) <= EarthPolarModel.LifelessBorder || z < p.EquatorZ && p.EarthIceBlend(x, z) > 0;

        public static double OceanTemperature(EarthPolarModel.Column c, double year, bool fixedSeason = false)
        {
            // Like vanilla/Earth climate, an explicit fixed-season override applies locally in both hemispheres.
            double maximum = NorthMaximum + (c.Latitude < 0 && !fixedSeason ? 0.5 : 0);
            double winter = (1 + Math.Cos(2 * Math.PI * (year - maximum))) / 2;
            return c.WarmestMonth + (c.ColdestMonth - c.WarmestMonth) * winter;
        }

        public static SeaIceKind At(SeamParams p, double x, double z, EarthPolarModel.Column c, double year, bool fixedSeason = false)
        {
            if (!p.EarthPolarIce || !p.HasWalls || !InPolarOcean(c)) return SeaIceKind.Open;
            // Preserve the complete existing wavy Wall approach, including its pressure ridges.
            if (PermanentApproach(p, x, z)) return SeaIceKind.Permanent;
            // A smooth moving climate boundary with small irregular floes; no time-of-day oscillation.
            double threshold = -1.8 - 2 * p.SeaIceEdgeNoise(x, z);
            if (c.WarmestMonth <= threshold) return SeaIceKind.Permanent;
            return OceanTemperature(c, year, fixedSeason) <= threshold ? SeaIceKind.Seasonal : SeaIceKind.Open;
        }
    }

    public partial class SeamParams
    {
        public double SeaIceEdgeNoise(double x, double z)
        {
            RingCoords(x, out double rx, out double rz);
            return Noise.Floes.Noise(rx, z, rz);
        }

        /// <summary>The sea-ice date to use anywhere: the snapshot the server publishes, or before its first one the two
        /// loose fields (SeaIceYear and SeaIceFixedSeason stay public: other mods' tests set them).</summary>
        public SeaIceDate CurrentSeaIceDate => SeaIceSeason ?? new SeaIceDate(SeaIceYear, SeaIceFixedSeason);

        public SeaIceKind SeaIceAt(double x, double z, EarthPolarModel.Column sample)
        {
            var season = CurrentSeaIceDate;
            return SeaIcePolicy.At(this, x, z, sample, season.Year, season.FixedSeason);
        }

        public bool HasSeaIce(double x, double z, EarthPolarModel.Column sample) => SeasonalSeaIce
            ? SeaIceAt(x, z, sample) != SeaIceKind.Open : HasArcticIce(x, z, sample);
    }
}
