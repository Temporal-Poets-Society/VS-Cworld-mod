using System;

namespace CylinderWorld
{
    public partial class SeamParams
    {
        /// <summary>Visible pack ice fades across a wavy margin beyond the mandatory lifeless kilometre.</summary>
        public double EarthIceBlend(double x, double z)
        {
            if (!EarthPolarIce || !HasWalls || z >= EquatorZ) return 0;
            RingCoords(x, out double rx, out double rz);
            double reach = 1800 + 500 * (Noise.Front.Noise(rx, 130, rz) - 0.5);
            return EarthSmooth((reach - DistToWallFace(z)) / 550);
        }

        /// <summary>Distance beyond the wall's foot (WallFaceMax) over which pressure ridges rise to full height.</summary>
        public const double RidgeFootFade = 48;

        /// <summary>Broken pressure ridges, at most eight blocks high, on Arctic ocean ice only.
        /// World coordinates and ring noise keep neighbouring chunks and both sides of the seam continuous.
        /// The wall's foot has no ridges (they would meet the rough face); they fade in beyond it, not in a straight cliff.</summary>
        public int EarthIceRise(double x, double z, EarthPolarModel.Column sample)
        {
            if (!sample.Ocean || sample.Latitude < EarthPolarModel.ArcticIceLatitude || !EarthPolarIce || !HasWalls) return 0;
            double blend = EarthIceBlend(x, z) * EarthSmooth((DistToZEdge(z) - WallFaceMax) / RidgeFootFade);
            if (blend <= 0) return 0;
            RingCoords(x, out double rx, out double rz);
            double ridge = Math.Max(0, 1 - Math.Abs(Noise.Apron.Noise(rx * 0.3, z * 0.3, rz * 0.3) - 0.5) * 6);
            double broken = EarthSmooth((Noise.FaceBig.Noise(rx, z * 1.7, rz) - 0.39) / 0.2);
            double rubble = Math.Max(0, (Noise.FaceSmall.Noise(rx * 0.5, z * 0.5, rz * 0.5) - 0.53) * 4);
            return Math.Clamp((int)Math.Round(blend * (6 * ridge * ridge * broken + 2 * rubble)), 0, 8);
        }

        /// <summary>Wind-scoured bare ice, thin snow and occasional two-block drifts. No change at the spawn border.</summary>
        public int EarthSnowEighths(double x, double z)
        {
            RingCoords(x, out double rx, out double rz);
            // Stretch the pattern north-south into wind-shaped banks; leave roughly half of the ice exposed.
            double n = Noise.Freeboard.Noise(rx * 1.2, z * 0.3, rz * 1.2);
            double banks = Math.Max(0, (n - 0.48) * 26);
            return (int)Math.Clamp(Math.Round(banks * (0.55 + 0.9 * EarthIceBlend(x, z))), 0, 20);
        }

        static double EarthSmooth(double value) => SmoothStep(Math.Clamp(value, 0, 1));
    }
}
