using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CylinderWorld
{
    /// <summary>
    /// Keeps the weather identical on both sides of the seam. Vanilla simulates weather per 512-block map region, so the
    /// regions right before and after a jump line normally have unrelated weather. When the ring is a whole number of
    /// regions (the default since 0.4), region r at the west seam and region r + ring/512 at the east seam are the same
    /// place. While both are loaded (they are when a player approaches the seam, because the destination is preloaded),
    /// the side a player is on (else the one loaded first) is copied onto the other: patterns, wind, weather events,
    /// random state and snow accumulation. The copy is pushed to nearby players and stored in the region, like vanilla's
    /// own updates. With the rain made continuous around the ring (SeamRainPatch) the two then tick alike, and nothing is
    /// sent while they match: every update restarts a pattern transition on the clients near it.
    /// The clouds and the client's own copy at the moment of the jump are handled client-side (SeamJumpClient).
    /// </summary>
    public class SeamWeatherSync : ModSystem
    {
        ICoreServerAPI sapi;
        CylinderWorldCore core;
        WeatherSystemServer weather;
        readonly Dictionary<long, long> firstSeenMs = new();
        /// <summary>Pairs (by the west region's key) whose copy threw: how many times in a row, and when to try again.</summary>
        readonly Dictionary<long, (int Count, long RetryAt)> failing = new();
        /// <summary>The longest wait before a failing pair is tried again (it doubles from 4 s).</summary>
        const long MaxRetryMs = 60000;
        bool warned;
        GameFeature feature;
        /// <summary>WeatherSimulationRegion.FromBytes (internal): the loader vanilla uses for saved weather. The client's
        /// copy at a jump binds its own (SeamJumpClient.BindWeather).</summary>
        MethodInfo fromBytes;
        /// <summary>Vanilla's lock on its region list (WeatherSystemBase.weatherSimByMapRegionLock), null in a game version
        /// without it; and that lock object.</summary>
        FieldInfo listLock;
        object listGate;
        /// <summary>Reused by every comparison and copy (main thread only).</summary>
        readonly FastMemoryStream ms = new();

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            feature = new GameFeature("weather sync across the seam", null, Mod.Logger);
            fromBytes = feature.Method(typeof(WeatherSimulationRegion), "FromBytes", new[] { typeof(byte[]) });
            feature.Apply();
            // Optional: without it the list is copied unlocked, and a copy torn by another thread is tried again
            var locked = new GameFeature("locking vanilla's weather list for the seam weather sync (it copies the list unlocked instead)", null, Mod.Logger);
            listLock = locked.Field(typeof(WeatherSystemBase), "weatherSimByMapRegionLock");
            if (!locked.Apply()) listLock = null;
            api.Event.RegisterGameTickListener(Sync, 2000);
        }

        void Sync(float dt)
        {
            var p = core.Params;
            if (p == null || !core.Config.SyncWeatherAcrossSeam || !feature.On) return;

            int regionSize = sapi.WorldManager.RegionSize;
            long ring = (long)p.Circumference;
            if (ring % regionSize != 0)
            {
                if (!warned) Mod.Logger.Notification("Weather sync across the seam is off: this world's ring ({0}) isn't a whole number of {1}-block regions (worlds from 0.4+ are).", ring, regionSize);
                warned = true;
                return;
            }
            // Contained: a game update that changes a member SyncPairs binds directly fails when SyncPairs is compiled,
            // which happens at this call; the sync then switches off with one log line instead of throwing every 2 s
            try { SyncPairs(p, regionSize, (int)(ring / regionSize)); }
            catch (Exception e) { feature.Fail(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void SyncPairs(SeamParams p, int regionSize, int shift)
        {
            weather ??= sapi.ModLoader.GetModSystem<WeatherSystemServer>();
            var list = weather?.weatherSimByMapRegion;
            if (list == null) return;
            listGate ??= listLock?.GetValue(weather);

            // The snow-layer and chunk-loading threads add regions to the list (getOrCreateWeatherSimForRegion), under
            // vanilla's lock. Without it (a game version that has none), a copy torn by such an insert can hold a broken
            // entry or a key twice, which ToDictionary throws on: try again next time.
            KeyValuePair<long, WeatherSimulationRegion>[] sims;
            Dictionary<long, WeatherSimulationRegion> byKey;
            try
            {
                if (listGate != null) lock (listGate) sims = list.ToArray();
                else sims = list.ToArray();
                byKey = sims.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            catch (Exception) { return; }

            long now = sapi.World.ElapsedMilliseconds;
            foreach (var key in firstSeenMs.Keys.Where(k => !byKey.ContainsKey(k)).ToList()) firstSeenMs.Remove(key);
            foreach (var key in failing.Keys.Where(k => !byKey.ContainsKey(k)).ToList()) failing.Remove(key);
            foreach (var key in byKey.Keys) if (!firstSeenMs.ContainsKey(key)) firstSeenMs[key] = now;

            foreach (var (key, sim) in sims)
            {
                if (sim == null) continue;
                int regionX = (int)(key & 0xFFFFFFFF), regionZ = (int)(key >> 32);
                // West side of the seam: regions overlapping the seam band
                if ((long)regionX * regionSize >= p.SeamHalfWidth) continue;

                long mirrorKey = weather.MapRegionIndex2D(regionX + shift, regionZ);
                if (!byKey.TryGetValue(mirrorKey, out var mirror) || mirror == null) continue;
                // Unloaded regions stay in the weather list, frozen, until the next save: only match live ones
                if (sapi.WorldManager.GetMapRegion(regionX, regionZ) == null || sapi.WorldManager.GetMapRegion(regionX + shift, regionZ) == null) continue;
                if (failing.TryGetValue(key, out var failed) && now < failed.RetryAt) continue;

                // The weather someone is in wins; with nobody near either side, the one loaded first
                double west = NearestPlayerSq(regionX, regionZ, regionSize), east = NearestPlayerSq(regionX + shift, regionZ, regionSize);
                bool fromWest = west != east ? west < east : firstSeenMs[key] <= firstSeenMs[mirrorKey];
                var (src, dst) = fromWest ? (sim, mirror) : (mirror, sim);

                // The regions only change on this (the main) thread
                try
                {
                    if (!SameState(src, dst))
                    {
                        fromBytes.Invoke(dst, new object[] { src.ToBytes(ms) });
                        dst.sendWeatherUpdatePacket();
                    }
                    failing.Remove(key);
                }
                catch (Exception e)
                {
                    var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                    // A game that changed what the copy calls fails every pair alike: the sync switches off, one log line
                    if (Permanent(cause))
                    {
                        feature.Fail(cause);
                        return;
                    }
                    // A pair in a state the copy can't handle (a region not set up yet): tried again after 4 s, 8 s, ...
                    // up to a minute while it keeps failing, said once
                    int count = failed.Count + 1;
                    failing[key] = (count, now + Math.Min(MaxRetryMs, 2000L << Math.Min(count, 5)));
                    if (count == 1) Mod.Logger.Debug("Weather sync across the seam skipped region {0},{1} (tried again later, less often while it fails): {2}", regionX, regionZ, cause.Message);
                }
            }
        }

        /// <summary>A failure of the call itself rather than of a region's state: a game member gone or changed.</summary>
        static bool Permanent(Exception e) =>
            e is MemberAccessException || e is TypeLoadException || e is TargetParameterCountException || e is TargetException || e is BadImageFormatException;

        /// <summary>
        /// Whether the two regions' saved states (WeatherSimulationRegion.ToBytes) are the same bytes, without serializing a
        /// year of hourly snow snapshots (about 170 KB) for both every 2 s: the rest of the state is serialized (a few hundred
        /// bytes) and compared, then the two snapshot rings value by value, as they serialize (protobuf leaves out the ring's
        /// empty slots). So a pair is copied exactly when the full comparison would have copied it.
        /// </summary>
        internal bool SameState(WeatherSimulationRegion a, WeatherSimulationRegion b)
        {
            if (!SerializerUtil.Serialize(Head(a), ms).AsSpan().SequenceEqual(SerializerUtil.Serialize(Head(b), ms))) return false;
            var x = a.SnowAccumSnapshots.Values;
            var y = b.SnowAccumSnapshots.Values;
            for (int i = 0, j = 0; ; i++, j++)
            {
                while (i < x.Length && x[i] == null) i++;
                while (j < y.Length && y[j] == null) j++;
                if (i == x.Length || j == y.Length) return i == x.Length && j == y.Length;
                if (!Same(x[i], y[j])) return false;
            }
        }

        /// <summary>What vanilla's ToBytes writes (1.22.7), less the snow snapshot ring.</summary>
        static WeatherState Head(WeatherSimulationRegion r) => new()
        {
            NewPattern = r.NewWePattern?.State,
            OldPattern = r.OldWePattern?.State,
            WindPattern = r.CurWindPattern?.State,
            WeatherEvent = r.CurWeatherEvent?.State,
            Weight = r.Weight,
            TransitionDelay = r.TransitionDelay,
            Transitioning = r.Transitioning,
            LastUpdateTotalHours = r.LastUpdateTotalHours,
            LcgCurrentSeed = r.Rand.currentSeed,
            LcgMapGenSeed = r.Rand.mapGenSeed,
            LcgWorldSeed = r.Rand.worldSeed,
            Ringarraycursor = r.SnowAccumSnapshots.EndPosition
        };

        // As protobuf writes them: a single value of zero (either sign) is left out, anything else and every array element
        // goes in bit for bit
        static bool Same(SnowAccumSnapshot a, SnowAccumSnapshot b) =>
            Same(a.TotalHours, b.TotalHours) && a.Checks == b.Checks
            && Same(a.SumTemperatureByRegionCorner, b.SumTemperatureByRegionCorner) && Same(a.SnowAccumulationByRegionCorner, b.SnowAccumulationByRegionCorner);

        static bool Same(double a, double b) => a == 0 && b == 0 || BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

        static bool Same(FloatDataMap3D a, FloatDataMap3D b)
        {
            if (a == null || b == null) return a == b;
            if (a.Width != b.Width || a.Length != b.Length || a.Height != b.Height) return false;
            var u = a.Data ?? Array.Empty<float>();
            var v = b.Data ?? Array.Empty<float>();
            if (u.Length != v.Length) return false;
            for (int i = 0; i < u.Length; i++) if (BitConverter.SingleToInt32Bits(u[i]) != BitConverter.SingleToInt32Bits(v[i])) return false;
            return true;
        }

        double NearestPlayerSq(int regionX, int regionZ, int regionSize)
        {
            double cx = (regionX + 0.5) * regionSize, cz = (regionZ + 0.5) * regionSize, best = double.MaxValue;
            foreach (var plr in sapi.World.AllOnlinePlayers)
            {
                var pos = plr.Entity?.Pos;
                if (pos == null) continue;
                best = Math.Min(best, (pos.X - cx) * (pos.X - cx) + (pos.Z - cz) * (pos.Z - cz));
            }
            return best;
        }
    }

    /// <summary>
    /// Vanilla's rain and snow come from a noise keyed to the absolute x (WeatherSystemBase.GetRainCloudness), so the two
    /// sides of the seam rained independently: rain could start or stop at the jump, and the two copies of a seam region
    /// got different climate, so they picked different weather between syncs. Here x is folded into the ring, and over
    /// the last RainBlend blocks before the east jump line (half the ring in a ring of 4096 or less) the noise blends into
    /// the one just past the west line, so the field is continuous all the way round. Both sides (client particles, server
    /// weather choice) use it.
    ///
    /// Another mod can own the weather: the open sea contracts' weather role (Sea's docs/open-sea-contracts.md, section 4),
    /// an entry in api.ObjectCache["seacontracts:weather"] that says it writes vanilla's weather, which no other mod may
    /// then do. While such an owner answers for its rain on a side and has its patch on vanilla's rain, the fold steps aside
    /// there and vanilla's own method runs for the owner to replace (Earth Map's postfix does, with a rain field that is
    /// already continuous on this ring). An owner that hasn't built its weather yet answers NaN, and one whose patch isn't
    /// there replaces nothing: the fold carries on until both hold. The fold also runs
    /// after other mods' rain prefixes of normal priority, so one that answers the call itself (returns false) skips it.
    /// </summary>
    public class SeamRainPatch : ModSystem
    {
        const string HarmonyId = "cylinderworld.rain";
        const string WeatherRole = "seacontracts:weather";
        const double RainBlend = 2048;
        static readonly object sync = new();
        static int users;
        static GameFeature feature;
        static Func<WeatherSystemBase, double, double, double, float, float> precipNoise;
        /// <summary>WeatherSystemBase.GetRainCloudness, which the fold and a weather owner patch.</summary>
        static MethodInfo rainCloudness;
        /// <summary>Per side: another mod's live weather owner writes vanilla's rain there (see WatchOwner).</summary>
        static volatile bool ownedServer, ownedClient;

        ICoreAPI api;
        long listener;
        /// <summary>The owner's entry and revision last probed, when (Environment.TickCount64), and whether its rain answered
        /// then.</summary>
        object probedEntry;
        long probedRevision, probedAt;
        bool probedLive;
        /// <summary>How often an owner whose rain gave no answer is asked again, with or without a new revision.</summary>
        const long ProbeAgainMs = 10000;
        /// <summary>The entry last said to answer without a rain patch (logged once).</summary>
        object unpatchedLogged;

        /// <summary>This instance's Start counted it among the users (CW-0015): only then does its Dispose count it out. Mod
        /// loading that stops before Start (another mod throws first) left the count one short, and the next world in the
        /// session never patched the fold.</summary>
        bool counted;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            lock (sync)
            {
                counted = true;
                if (users++ == 0)
                {
                    var f = new GameFeature("continuous rain across the seam", new Harmony(HarmonyId), api.Logger);
                    var t = typeof(WeatherSystemBase);
                    var noise = f.Method(t, "getPrecipNoise", new[] { typeof(double), typeof(double), typeof(double), typeof(float) });
                    Func<WeatherSystemBase, double, double, double, float, float> call = null;
                    if (noise != null)
                    {
                        try { call = AccessTools.MethodDelegate<Func<WeatherSystemBase, double, double, double, float, float>>(noise); }
                        catch (Exception) { }
                    }
                    f.Require(noise == null || call != null, "getPrecipNoise(double, double, double, float) returning float");
                    var rain = f.Method(t, nameof(WeatherSystemBase.GetRainCloudness), new[] { typeof(ClimateCondition), typeof(double), typeof(double), typeof(double) });
                    // Last, so another mod's rain prefix of normal priority runs first and can answer the call itself
                    f.Patch(rain, prefix: new HarmonyMethod(typeof(SeamRainPatch), nameof(Fold)) { priority = Priority.Last });
                    precipNoise = call;
                    rainCloudness = rain;
                    feature = f;
                    f.Apply();
                }
            }
            listener = api.Event.RegisterGameTickListener(_ => WatchOwner(), 1000);
        }

        public override void Dispose()
        {
            if (api != null)
            {
                api.Event.UnregisterGameTickListener(listener);
                SetOwned(api.Side, false);
            }
            lock (sync)
            {
                if (counted && --users == 0)
                {
                    feature?.Unpatch();
                    feature = null;
                    precipNoise = null;
                    rainCloudness = null;
                }
            }
            base.Dispose();
        }

        static void SetOwned(EnumAppSide side, bool owned)
        {
            if (side == EnumAppSide.Server) ownedServer = owned;
            else ownedClient = owned;
        }

        /// <summary>
        /// Main thread, each side, once a second: is there a weather owner other than Cylinder World that writes vanilla's
        /// weather, answers for its rain and has its patch on vanilla's rain? Its rain is probed once per change of its
        /// revision (or of the entry), and while it gives no answer every ten seconds; NaN, an infinity or an exception is no
        /// answer (the contracts' rule R7). Fold reads only the result, never api.ObjectCache: it runs on the snow-layer,
        /// chunk-loading and worldgen threads too.
        /// </summary>
        void WatchOwner()
        {
            object entry = null;
            string owner = null;
            bool writes = false;
            Func<long> revision = null;
            System.Func<double, double, double, (double, double)> rain = null;
            lock (api.ObjectCache)
            {
                if (api.ObjectCache.TryGetValue(WeatherRole, out var o) && o is IDictionary<string, object> d)
                {
                    entry = d;
                    owner = d.TryGetValue("owner", out var w) ? w as string : null;
                    writes = d.TryGetValue("writesVanilla", out var v) && v is bool b && b;
                    revision = d.TryGetValue("revision", out var r) ? r as Func<long> : null;
                    rain = d.TryGetValue("rain", out var q) ? q as System.Func<double, double, double, (double, double)> : null;
                }
            }
            bool live = false;
            if (entry != null && writes && rain != null && owner != Mod.Info.ModID)
            {
                // The producer's own functions are called outside the lock
                long rev = 0;
                bool known = false;
                try { if (revision != null) { rev = revision(); known = true; } }
                catch (Exception) { }
                // Also again every ten seconds while it gave no answer: an exception that passed, or an answer that comes
                // without a new revision, keeps the fold on only that long
                long now = Environment.TickCount64;
                if (!known || !ReferenceEquals(entry, probedEntry) || rev != probedRevision || !probedLive && now - probedAt >= ProbeAgainMs)
                {
                    probedEntry = entry;
                    probedRevision = rev;
                    probedAt = now;
                    probedLive = Answers(rain);
                }
                // The owner replaces vanilla's rain with a patch of its own (Earth Map applies it before it publishes a
                // model and removes it when it drops one). Without one (it failed to patch, or was removed while the role
                // stayed), stepping aside would bring vanilla's unfolded rain back at the seam
                live = probedLive && OwnerPatched();
                if (probedLive && !live && !ReferenceEquals(unpatchedLogged, entry))
                {
                    unpatchedLogged = entry;
                    Mod.Logger.Notification("Rain ({0}): {1} answers for the rain (the open sea contracts' weather role) but has no patch on vanilla's rain, so the seam's rain fold carries on.", api.Side, owner ?? "another mod");
                }
            }
            else probedEntry = null;

            if (live == (api.Side == EnumAppSide.Server ? ownedServer : ownedClient)) return;
            SetOwned(api.Side, live);
            if (live) Mod.Logger.Notification("Rain ({0}): {1} writes vanilla's weather here (the open sea contracts' weather role), so the seam's rain fold steps aside.", api.Side, owner ?? "another mod");
            else Mod.Logger.Notification("Rain ({0}): no other mod answers for the rain now; the seam's rain fold is back.", api.Side);
        }

        /// <summary>Another mod (any Harmony id but the fold's) patches vanilla's rain.</summary>
        static bool OwnerPatched()
        {
            var m = rainCloudness;
            var info = m == null ? null : Harmony.GetPatchInfo(m);
            return info != null && info.Owners.Any(o => o != HarmonyId);
        }

        /// <summary>The owner's rain in the middle of this world now, as finite numbers.</summary>
        bool Answers(System.Func<double, double, double, (double, double)> rain)
        {
            try
            {
                var calendar = api.World?.Calendar;
                var ba = api.World?.BlockAccessor;
                if (calendar == null || ba == null) return false;
                var (cloudness, precipitation) = rain(ba.MapSizeX / 2.0, ba.MapSizeZ / 2.0, calendar.TotalHours);
                return double.IsFinite(cloudness) && double.IsFinite(precipitation);
            }
            catch (Exception) { return false; }
        }

        static bool Fold(WeatherSystemBase __instance, ClimateCondition conds, double posX, double posZ, double totalDays, ref float __result)
        {
            var f = feature;
            if (f == null || !f.On) return true;
            try
            {
                var side = __instance.api?.Side ?? EnumAppSide.Server;
                if (side == EnumAppSide.Server ? ownedServer : ownedClient) return true;
                var p = CylinderWorldCore.For(side);
                if (p == null || !(p.Circumference > 0) || precipNoise == null || __instance.OverridePrecipitation.HasValue) return true;
                double c = p.Circumference, u = posX - p.JumpInset;
                // A ring of 4096 blocks or less (test worlds) blends over its east half
                double blend = Math.Min(RainBlend, c / 2);
                u -= c * Math.Floor(u / c);
                double x = p.JumpInset + u, days = totalDays + __instance.RainCloudDaysOffset;
                float wgenRain = conds == null ? 0 : GameMath.Clamp((conds.Rainfall - 0.6f) * 2f, -1f, 1f);
                // The climate map's rainfall isn't periodic, so the two sides of the line would feed different values into
                // the same noise. Within sight of the line (the band's identical part) both use vanilla's neutral 0, fading
                // back to the local value over the blend.
                double flatReach = p.IdenticalReach;
                double s = Math.Clamp((p.DistToJumpLine(posX) - flatReach) / blend, 0, 1);
                wgenRain *= (float)(s * s * (3 - 2 * s));
                float rain = precipNoise(__instance, x, posZ, days, wgenRain);
                if (u > c - blend)
                {
                    double t = (u - (c - blend)) / blend;
                    t = t * t * (3 - 2 * t);
                    rain += (float)((precipNoise(__instance, x - c, posZ, days, wgenRain) - rain) * t);
                }
                __result = rain;
                return false;
            }
            catch (Exception e)
            {
                f.Fail(e);
                return true;
            }
        }
    }
}
