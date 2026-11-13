using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    /// <summary>
    /// Extends the live temperature range beyond vanilla's climate map (which stores only -20..+40°C, capped again after
    /// altitude). Every temperature query goes through the OnGetClimate event, where vanilla adds seasons and day/night
    /// to the stored base. Here the base is corrected to the mod's true value: the climate curve, cooled by altitude,
    /// plus the ice sheet's extra cold. Works in either handler order: the base and the live value both get the same
    /// offset. Body heat, animal spawning, crops, snow and the temperature readout all see it, and so do worldgen queries
    /// that go through the event (EnumGetClimateMode.WorldGenValues: forest floors, soil, climate-specific traders).
    /// Worldgen that reads the climate map without the event (block patches, trees, worldgen creatures) still sees the
    /// capped map, where -20 already means barren ground and ice.
    /// </summary>
    public class PolarTemperatureSystem : ModSystem
    {
        CylinderWorldCore core;
        bool client;

        /// <summary>
        /// The bounds of vanilla's temperature conversion (Climate, 1.22.7): the climate map stores -20..40 °C, and after
        /// altitude the server clamps to -20..40 (GetScaledAdjustedTemperatureFloat), the client to -50..40
        /// (GetScaledAdjustedTemperatureFloatClient). Read from those functions, so a game update that moves them is followed.
        /// </summary>
        static readonly double MapMin = Climate.GetScaledAdjustedTemperatureFloat(0, 0), MapMax = Climate.GetScaledAdjustedTemperatureFloat(255, 0),
            ServerFloor = Climate.GetScaledAdjustedTemperatureFloat(0, 100000), ClientFloor = Climate.GetScaledAdjustedTemperatureFloatClient(0, 100000),
            Ceiling = Climate.GetScaledAdjustedTemperatureFloat(255, -100000);

        public override double ExecuteOrder() => 1.0;

        public override void Start(ICoreAPI api)
        {
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            client = api.Side == EnumAppSide.Client;
            api.Event.OnGetClimate += OnGetClimate;
        }

        void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
        {
            var p = core.Params;
            if (p == null || !p.ExtendTemperature || !p.PolarClimate || !p.IsPlanetLayout || pos == null) return;
            Correct(p, climate, pos.X, pos.Y, pos.Z, client);
        }

        /// <summary>The reading this thread corrected last, and the base it left in it (see Correct).</summary>
        [ThreadStatic] static ClimateCondition lastCorrected;
        [ThreadStatic] static float lastBase;

        /// <summary>
        /// Corrects one reading, once. Vanilla reuses readings: GetClimateAt(pos, baseClimate, mode, totalDays) puts the
        /// live temperature back to the base and raises the event again on the same object, up to 480 times in a row in its
        /// catch-up loops (snow accumulation, farmland, berry bushes, fruit trees, transient blocks, harvestable animals),
        /// each loop on one thread with nothing else read in between. Adding the correction every time left a farm at 78°
        /// some 50 °C too cold after a long absence. The object back with the base this correction gave it is that reuse.
        /// (Its value alone can't tell: the map's own jitter of up to 3 °C overlaps corrections that small.)
        /// </summary>
        internal static void Correct(SeamParams p, ClimateCondition climate, double x, double y, double z, bool client)
        {
            if (ReferenceEquals(climate, lastCorrected) && climate.WorldGenTemperature == lastBase) return;
            double extra = Correction(p, x, y, z, client);
            if (Math.Abs(extra) < 0.05) return;

            climate.WorldGenTemperature += (float)extra;
            climate.Temperature += (float)extra;
            lastCorrected = climate;
            lastBase = climate.WorldGenTemperature;
        }

        /// <summary>
        /// What to add to vanilla's base temperature at (x, y, z) to get <see cref="SeamParams.TrueBaseTemperature"/>.
        /// Vanilla stores the map value clamped to -20..40 (Climate.DescaleTemperature), then subtracts altitude and clamps
        /// again: at -20 on the server, at -50 on the client. Map jitter and the world's temperature setting are left out,
        /// as before. The curve is evaluated once.
        /// </summary>
        public static double Correction(SeamParams p, double x, double y, double z, bool client)
        {
            float curve = p.TemperatureAt(z);
            return p.TrueBaseFromCurve(z, y, curve) - VanillaBase(p, curve, y, client);
        }

        /// <summary>Vanilla's base temperature at height y where the climate curve gives <paramref name="curve"/>, without the
        /// map's jitter: the stored map value less altitude, each clamped as that side clamps it.</summary>
        static double VanillaBase(SeamParams p, float curve, double y, bool client)
        {
            double map = Math.Clamp(curve, MapMin, MapMax);
            return Math.Clamp(map - (y - p.SeaLevel) / 6.375, client ? ClientFloor : ServerFloor, Ceiling);
        }
    }
}
