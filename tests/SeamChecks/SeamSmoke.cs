using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CylinderWorld;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

// Test-only mod, compiled and installed solely in a temporary server data directory by smoke.ps1.
// Checks the seam-crossing code that a headless server can reach: the kept pre-sent chunks, the continuous rain,
// the weather sync, the cloud rebase arithmetic, and that every game member the client side relies on exists.
// Then generated terrain in the default layout (no Earth Map): the band's seabed, wavy edge and ramp (layout v10) and
// their heightmaps, mirrored band columns, the ice sheet across the jump line at both walls, pack-ice floes, and the
// coast-noise ocean force in regions beyond the pre-0.11.4 cull distance.
/// <summary>Records the natural ground of the ramp and polar shelf profile columns just before Cylinder World's chunk pass
/// (its systems run at 0.6) rewrites them, so the ramp can be checked against the exact rule and the terrain it started from.</summary>
public class SeamSmokeNatural : ModSystem
{
    public static readonly ConcurrentDictionary<(int x, int z), int> Natural = new();
    public static volatile System.Func<int, int, bool> Wanted;
    ICoreServerAPI api;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 0.59;

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ChunkColumnGeneration(Record, EnumWorldGenPass.TerrainFeatures, "standard");
    }

    /// <summary>Same ground rule as the worldgen's seabed lookup (TopGround): solid, not wood, leaves, plants or snow, not
    /// ground with sea water under it before the seabed (a structure over deep water); 0 if none above y 0 (vanilla's deep ocean trenches
    /// can reach the mantle).</summary>
    void Record(IChunkColumnGenerateRequest request)
    {
        var wanted = Wanted;
        if (wanted == null) return;
        const int cs = 32;
        var blocks = api.World.Blocks;
        for (int lx = 0; lx < cs; lx++)
            for (int lz = 0; lz < cs; lz++)
            {
                int x = request.ChunkX * cs + lx, z = request.ChunkZ * cs + lz;
                if (!wanted(x, z)) continue;
                int ground = 0, run = 0;
                bool sea = false;
                for (int y = api.WorldManager.MapSizeY - 2; y > 0; y--)
                {
                    var data = request.Chunks[y / cs].Data;
                    int idx = ((y % cs) * cs + lz) * cs + lx;
                    int id = data.GetBlockId(idx, BlockLayersAccess.Solid);
                    var m = id == 0 ? EnumBlockMaterial.Air : blocks[id].BlockMaterial;
                    if (id != 0 && m != EnumBlockMaterial.Wood && m != EnumBlockMaterial.Leaves && m != EnumBlockMaterial.Plant && m != EnumBlockMaterial.Snow)
                    {
                        if (ground == 0 || sea) ground = y;
                        if (run == 0) run = y;
                        sea = false;
                        if (run - y >= 24) break;
                    }
                    else
                    {
                        run = 0;
                        int fluid = data.GetFluid(idx);
                        if (ground != 0 && fluid != 0 && blocks[fluid].Code.Path.StartsWith("saltwater")) sea = true;
                    }
                }
                Natural[(x, z)] = sea ? 0 : ground;
            }
    }
}

public class SeamSmoke : ModSystem
{
    ICoreServerAPI api;
    SeamParams p;
    int failures;
    bool finished;
    int sends;
    long mirrorA, mirrorB;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 1;

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Run(), 1000));
        api.Event.RegisterCallback(_ => { if (!finished) End("FAIL timeout"); }, 420000);
    }

    void Check(bool ok, string what)
    {
        if (!ok) failures++;
        Mod.Logger.Notification("SEAMSMOKE {0} {1}", ok ? "PASS" : "FAIL", what);
    }

    void Run()
    {
        try
        {
            p = api.ModLoader.GetModSystem<CylinderWorldCore>().Params ?? throw new Exception("no seam params");
            Check(AccessTools.Field(typeof(CylinderWorldCore), "ServerShared").GetValue(null) == p, "seam geometry is shared with the server's game patches");
            CheckPatches();
            CheckKeep();
            CheckPreloadWindow();
            CheckRain();
            CheckClientMembers();
            CheckClouds();
            StartRainOwner();
        }
        catch (Exception e) { End("FAIL " + e); }
    }

    void CheckPatches()
    {
        var unload = AccessTools.Method(AccessTools.TypeByName("Vintagestory.Server.ServerSystemUnloadChunks"), "SendOutOfRangeChunkUnloads");
        Check(Harmony.GetPatchInfo(unload)?.Owners.Contains("cylinderworld.preload") == true, "pre-sent chunks are protected from vanilla's out-of-range unload");
        var rain = AccessTools.Method(typeof(WeatherSystemBase), nameof(WeatherSystemBase.GetRainCloudness));
        Check(Harmony.GetPatchInfo(rain)?.Owners.Contains("cylinderworld.rain") == true, "rain noise patched");
    }

    /// <summary>The prefix hides exactly the kept chunks from the unload; the finalizer puts them back.</summary>
    void CheckKeep()
    {
        var conn = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
        var plrType = AccessTools.TypeByName("Vintagestory.Server.ServerPlayer");
        var dataType = AccessTools.TypeByName("Vintagestory.Server.ServerWorldPlayerData");
        object data = RuntimeHelpers.GetUninitializedObject(dataType);
        AccessTools.Field(dataType, "PlayerUID").SetValue(data, "seam-test");
        object plr = RuntimeHelpers.GetUninitializedObject(plrType);
        AccessTools.Field(plrType, "worlddata").SetValue(plr, data);
        object client = RuntimeHelpers.GetUninitializedObject(conn);
        AccessTools.Field(conn, "Player").SetValue(client, plr);
        var sent = new HashSet<long> { 1, 2, 3, 100 };
        AccessTools.Field(conn, "ChunkSent").SetValue(client, sent);

        var keep = (IDictionary)AccessTools.Field(typeof(SeamPreloader), "keep").GetValue(null);
        keep["seam-test"] = new HashSet<long> { 2, 3, 999 };
        var hide = AccessTools.Method(typeof(SeamPreloader), "HidePrepared");
        var restore = AccessTools.Method(typeof(SeamPreloader), "RestorePrepared");
        object[] args = { new object[] { client }, null };
        hide.Invoke(null, args);
        bool hidden = !sent.Contains(2) && !sent.Contains(3) && sent.Contains(1) && sent.Contains(100);
        sent.Remove(1); sent.Remove(100);   // what vanilla does to the out-of-range ones
        restore.Invoke(null, new object[] { null, new object[] { client }, args[1] });
        keep.Remove("seam-test");
        Check(hidden && sent.SetEquals(new long[] { 2, 3 }), "kept chunks survive vanilla's unload, the others don't");
    }

    /// <summary>A projected crossing that moved (0 → 12 → 6 chunks along z) must not leave the window full of old columns:
    /// after the drop every current target fits within the cap, and far columns are gone.</summary>
    void CheckPreloadWindow()
    {
        var wrap = api.ModLoader.GetModSystem<SeamWrapSystem>();
        var preloader = AccessTools.Field(typeof(SeamWrapSystem), "preloader").GetValue(wrap);
        var approachType = AccessTools.Inner(typeof(SeamPreloader), "Approach");
        object a = Activator.CreateInstance(approachType, true);
        var window = (IDictionary)AccessTools.Field(approachType, "Window").GetValue(a);
        var colType = AccessTools.Inner(typeof(SeamPreloader), "Col");
        object sent = Enum.Parse(colType, "Sent");
        long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
        HashSet<long> Disc(int ocz)
        {
            var set = new HashSet<long>();
            for (int dx = -7; dx <= 7; dx++) for (int dz = -7; dz <= 7; dz++) if (dx * dx + dz * dz <= 49) set.Add(Key(1000 + dx, ocz + dz));
            return set;
        }
        foreach (int ocz in new[] { 500, 512, 540 }) foreach (long k in Disc(ocz)) window[k] = sent;   // includes one far crossing
        var current = Disc(506);
        int cap = 2 * current.Count;
        // (0.11.14) DropStale takes the player (its UID only here: nothing kept for it, so nothing is drained); a bare one as in CheckKeep
        var plrType = AccessTools.TypeByName("Vintagestory.Server.ServerPlayer");
        var dataType = AccessTools.TypeByName("Vintagestory.Server.ServerWorldPlayerData");
        object data = RuntimeHelpers.GetUninitializedObject(dataType);
        AccessTools.Field(dataType, "PlayerUID").SetValue(data, "seam-test");
        object plr = RuntimeHelpers.GetUninitializedObject(plrType);
        AccessTools.Field(plrType, "worlddata").SetValue(plr, data);
        AccessTools.Method(typeof(SeamPreloader), "DropStale").Invoke(preloader, new object[] { plr, a, current, 1000, 506, 18, cap });
        int missing = current.Count(k => !window.Contains(k));
        bool farGone = !window.Contains(Key(1000, 540));
        Mod.Logger.Notification("SEAMSMOKE preload window: {0} kept + {1} missing targets, cap {2}, far crossing dropped={3}", window.Count, missing, cap, farGone);
        Check(window.Count + missing <= cap && farGone, "a moved crossing frees room in the preload window for every current target");
    }

    void CheckRain()
    {
        var ws = api.ModLoader.GetModSystem<WeatherSystemServer>();
        var noise = AccessTools.Method(typeof(WeatherSystemBase), "getPrecipNoise");
        var conds = new ClimateCondition { Rainfall = 0.7f };
        // The climate maps differ on the two sides; the rain must not
        var dry = new ClimateCondition { Rainfall = 0.3f };
        var wet = new ClimateCondition { Rainfall = 0.95f };
        double c = p.Circumference, j = p.JumpInset, eastLine = p.WorldWidth - j;
        double worst = 0, step = 0, vanilla = 0;
        var rnd = new Random(5);
        for (int i = 0; i < 400; i++)
        {
            double z = rnd.NextDouble() * p.WorldLength, days = rnd.NextDouble() * 400, k = rnd.NextDouble() * 300;
            // the two sides of each jump line are the same place
            worst = Math.Max(worst, Math.Abs(ws.GetRainCloudness(dry, eastLine - k, z, days) - ws.GetRainCloudness(wet, eastLine - k - c, z, days)));
            worst = Math.Max(worst, Math.Abs(ws.GetRainCloudness(wet, j + k, z, days) - ws.GetRainCloudness(dry, j + k + c, z, days)));
            // and the field is continuous across the line itself and where the blend starts
            step = Math.Max(step, Math.Abs(ws.GetRainCloudness(dry, j + 0.25, z, days) - ws.GetRainCloudness(wet, j - 0.25, z, days)));
            step = Math.Max(step, Math.Abs(ws.GetRainCloudness(conds, eastLine - 2048.25, z, days) - ws.GetRainCloudness(conds, eastLine - 2047.75, z, days)));
            // away from the east line nothing changes
            double x = j + 4096 + rnd.NextDouble() * (c - 8192);
            float wgen = GameMath.Clamp((conds.Rainfall - 0.6f) * 2f, -1f, 1f);
            vanilla = Math.Max(vanilla, Math.Abs(ws.GetRainCloudness(conds, x, z, days) - (float)noise.Invoke(ws, new object[] { x, z, days + ws.RainCloudDaysOffset, wgen })));
        }
        Mod.Logger.Notification("SEAMSMOKE rain: mirror diff {0:G3}, step at line/blend {1:G3}, change elsewhere {2:G3}", worst, step, vanilla);
        Check(worst < 1e-6, "rain is identical on both sides of the seam");
        Check(step < 0.02, "rain is continuous across the jump line and the blend edge");
        Check(vanilla < 1e-6, "rain away from the east line is vanilla's");
    }

    /// <summary>
    /// The rain fold and another mod's weather (the open sea contracts' weather role, decision CW-D38): a fake owner that
    /// writes vanilla's weather makes the fold step aside once its rain answers (NaN = not yet) and its own patch is on
    /// vanilla's rain (R2C2), an owner that answers again without a new revision is asked again within ten seconds (R2C3),
    /// an entry under Cylinder World's own name doesn't stop the fold, and once withdrawn the fold is back. The owner is
    /// watched once a second.
    /// </summary>
    void StartRainOwner()
    {
        const string role = "seacontracts:weather";
        var ownerPatch = new Harmony("seamsmoke.rainowner");
        var rainMethod = AccessTools.Method(typeof(WeatherSystemBase), nameof(WeatherSystemBase.GetRainCloudness));
        var ws = api.ModLoader.GetModSystem<WeatherSystemServer>();
        var noise = AccessTools.Method(typeof(WeatherSystemBase), "getPrecipNoise");
        var neutral = new ClimateCondition { Rainfall = 0.6f };   // no climate-map rain in the noise
        var rnd = new Random(7);
        double c = p.Circumference, j = p.JumpInset;
        long revision = 0;
        bool answers = false;
        Dictionary<string, object> Entry(string owner) => new()
        {
            ["owner"] = owner, ["version"] = 1, ["priority"] = 50,
            ["revision"] = new Func<long>(() => revision),
            ["rain"] = new System.Func<double, double, double, (double Cloudness, double Precipitation)>((x, z, h) => answers ? (0.4, 0.0) : (double.NaN, double.NaN)),
            ["writesVanilla"] = true,
        };
        void Publish(object entry) { lock (api.ObjectCache) { if (entry == null) api.ObjectCache.Remove(role); else api.ObjectCache[role] = entry; } }
        // Past the east line: folded, the rain there is the ring's (just inside the west line); vanilla's is its own noise
        (double folded, double vanilla) Measure()
        {
            double folded = 0, vanilla = 0;
            for (int i = 0; i < 40; i++)
            {
                double x = j + rnd.NextDouble() * 300, z = rnd.NextDouble() * p.WorldLength, days = rnd.NextDouble() * 400;
                float beyond = ws.GetRainCloudness(neutral, x + c, z, days);
                folded = Math.Max(folded, Math.Abs(beyond - ws.GetRainCloudness(neutral, x, z, days)));
                vanilla = Math.Max(vanilla, Math.Abs(beyond - (float)noise.Invoke(ws, new object[] { x + c, z, days + ws.RainCloudDaysOffset, 0f })));
            }
            return (folded, vanilla);
        }
        void Then(int ms, Action step) => api.Event.RegisterCallback(_ =>
        {
            try { step(); }
            catch (Exception e) { Publish(null); ownerPatch.UnpatchAll(ownerPatch.Id); End("FAIL " + e); }
        }, ms);

        Publish(Entry("seamsmoke"));
        Then(2500, () =>
        {
            var (f, v) = Measure();
            Mod.Logger.Notification("SEAMSMOKE rain owner not answering yet: fold off by {0:G3}, vanilla off by {1:G3}", f, v);
            Check(f < 1e-6 && v > 1e-3, "the rain fold carries on while the weather owner doesn't answer yet");
            answers = true;
            revision++;
            Then(2500, () =>
            {
                var (f1, v1) = Measure();
                Mod.Logger.Notification("SEAMSMOKE rain owner answering without its rain patch: fold off by {0:G3}, vanilla off by {1:G3}", f1, v1);
                Check(f1 < 1e-6 && v1 > 1e-3, "the rain fold carries on while the weather owner's own rain patch isn't there");
                // The owner's patch on vanilla's rain (this one changes nothing, so vanilla's value shows)
                ownerPatch.Patch(rainMethod, postfix: new HarmonyMethod(typeof(SeamSmoke), nameof(OwnerRain)));
                Then(2500, () =>
                {
                    var (f2, v2) = Measure();
                    Mod.Logger.Notification("SEAMSMOKE rain owner answering: fold off by {0:G3}, vanilla off by {1:G3}", f2, v2);
                    Check(v2 < 1e-6 && f2 > 1e-3, "the rain fold steps aside for a live weather owner");
                    // The owner drops its weather (a new revision, no answer), then answers again without a new revision
                    // (R2C3): asked again within ten seconds
                    answers = false;
                    revision++;
                    Then(2500, () =>
                    {
                        var (f5, v5) = Measure();
                        Check(f5 < 1e-6 && v5 > 1e-3, "the rain fold is back while the weather owner gives no answer");
                        answers = true;
                        Then(12000, () =>
                        {
                            var (f6, v6) = Measure();
                            Mod.Logger.Notification("SEAMSMOKE rain owner answering again, same revision: fold off by {0:G3}, vanilla off by {1:G3}", f6, v6);
                            Check(v6 < 1e-6 && f6 > 1e-3, "a weather owner that answers again without a new revision is asked again within ten seconds");
                            Withdraw();
                        });
                    });
                });
            });
        });

        // An entry under Cylinder World's own name, then none
        void Withdraw()
        {
            Publish(Entry("cylinderworld"));
            Then(2500, () =>
            {
                var (f3, _) = Measure();
                Check(f3 < 1e-6, "Cylinder World's own name in the weather role doesn't stop the fold");
                Publish(null);
                Then(2500, () =>
                {
                    var (f4, v4) = Measure();
                    Mod.Logger.Notification("SEAMSMOKE rain owner withdrawn: fold off by {0:G3}, vanilla off by {1:G3}", f4, v4);
                    Check(f4 < 1e-6 && v4 > 1e-3, "the rain fold is back when the weather owner is withdrawn");
                    ownerPatch.UnpatchAll(ownerPatch.Id);
                    StartWeather();
                });
            });
        }
    }

    /// <summary>The fake weather owner's patch on vanilla's rain: there, changing nothing.</summary>
    static void OwnerRain() { }

    /// <summary>Members the client side reaches by reflection or patches (the same assemblies load here).</summary>
    void CheckClientMembers()
    {
        var clientMain = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain");
        var camera = AccessTools.Field(clientMain, "MainCamera");
        Check(camera != null && AccessTools.Field(camera.FieldType, "prevPos")?.FieldType == typeof(Vec3d), "camera previous position reachable");
        Check(AccessTools.Method(typeof(WeatherSystemClient), "ProcessWeatherUpdate", new[] { typeof(WeatherState) }) != null, "client weather update method found");
        // Vanilla's region loader (internal WeatherSimulationRegion.FromBytes), as each side binds it through its feature:
        // the server's weather sync, and the client's weather copy at a jump with its other members
        var sync = api.ModLoader.GetModSystem<SeamWeatherSync>();
        object syncFeature = AccessTools.Field(typeof(SeamWeatherSync), "feature").GetValue(sync);
        Check(AccessTools.Field(typeof(SeamWeatherSync), "fromBytes").GetValue(sync) != null && AccessTools.Property(syncFeature.GetType(), "On").GetValue(syncFeature) is true,
            "weather region loader bound, the seam weather sync is on");
        var featureType = syncFeature.GetType();
        // (name, harmony, log, then any optional parameters with their defaults: reflection doesn't fill those itself)
        var ctor = featureType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First(c => c.GetParameters().Length >= 3 && c.GetParameters().Skip(3).All(x => x.IsOptional));
        object copy = ctor.Invoke(new object[] { "seamsmoke weather copy", null, Mod.Logger }.Concat(ctor.GetParameters().Skip(3).Select(x => x.DefaultValue)).ToArray());
        AccessTools.Method(typeof(SeamJumpClient), "BindWeather").Invoke(null, new[] { copy });
        Check(AccessTools.Method(featureType, "Apply").Invoke(copy, null) is true, "the client's weather copy at a jump finds the region loader and the region list's lock");
        var u = typeof(Vintagestory.API.Client.DefaultShaderUniforms);
        Check(u.GetField("playerReferencePos")?.FieldType == typeof(Vec3d) && u.GetField("playerReferencePosForFoam")?.FieldType == typeof(Vec3d), "water reference points reachable");
    }

    /// <summary>Patch both cloud renderers with a test id (proves every member was found), then check the rebase keeps
    /// what each tile shows and where it is drawn.</summary>
    void CheckClouds()
    {
        var clouds = AccessTools.TypeByName("CylinderWorld.SeamClouds");
        var h = new Harmony("seamsmoke.clouds");
        AccessTools.Method(clouds, "Patch").Invoke(null, new object[] { h, Mod.Logger });
        foreach (var t in new[] { typeof(FluffyClouds.CloudRendererMap), typeof(CloudRenderer) })
        {
            var render = AccessTools.Method(t, "OnRenderFrame", new[] { typeof(float), typeof(Vintagestory.API.Client.EnumRenderStage) });
            Check(Harmony.GetPatchInfo(render)?.Owners.Contains("seamsmoke.clouds") == true, t.Name + " clouds patched");
        }
        h.UnpatchAll("seamsmoke.clouds");

        var accessMap = (IDictionary)AccessTools.Field(clouds, "access").GetValue(null);
        var rebase = AccessTools.Method(clouds, "Rebase");
        foreach (var t in new[] { typeof(FluffyClouds.CloudRendererMap), typeof(CloudRenderer) })
        {
            object r = RuntimeHelpers.GetUninitializedObject(t);
            AccessTools.Property(t, "CloudTileSize").SetValue(r, 50);
            var states = new[] { "committedState", "mainThreadState", "offThreadState" }.Select(n => AccessTools.Field(t, n)).ToArray();
            double xOld = p.WorldWidth - p.JumpInset + 3.3, jump = -p.Circumference, xNew = xOld + jump;
            int cOld = (int)xOld / 50;
            foreach (var f in states)
            {
                object s = Activator.CreateInstance(f.FieldType);
                AccessTools.Field(f.FieldType, "CenterTilePos").SetValue(s, new Vec3i(cOld, 0, 900));
                AccessTools.Field(f.FieldType, "WindTileOffsetX").SetValue(s, 77);
                f.SetValue(r, s);
            }
            var wind = AccessTools.Field(t, "windOffsetX");
            wind.SetValue(r, 12.5);
            object committed = states[0].GetValue(r);
            int Center() => ((Vec3i)AccessTools.Field(committed.GetType(), "CenterTilePos").GetValue(committed)).X;
            int Wind() => (int)AccessTools.Field(committed.GetType(), "WindTileOffsetX").GetValue(committed);
            double drawnBefore = Center() * 50 - xOld + (double)wind.GetValue(r), keyBefore = Center() + 5 - Wind();

            rebase.Invoke(null, new object[] { r, accessMap[t], jump });

            double drawnAfter = Center() * 50 - xNew + (double)wind.GetValue(r), keyAfter = Center() + 5 - Wind();
            Check(Math.Abs(drawnAfter - drawnBefore) < 1e-6 && keyAfter == keyBefore && Math.Abs(Center() - (int)xNew / 50) <= 1
                && Math.Abs((double)wind.GetValue(r)) < 100, t.Name + " clouds stay put across a jump");
        }
    }

    // ---------------------------------------------------------------- weather sync (needs both sides loaded)

    void StartWeather()
    {
        int rs = api.WorldManager.RegionSize, shift = (int)(p.Circumference / rs);
        int z = (int)p.EquatorZ;
        var ws = api.ModLoader.GetModSystem<WeatherSystemServer>();
        mirrorA = ws.MapRegionIndex2D(0, z / rs);
        mirrorB = ws.MapRegionIndex2D(shift, z / rs);
        var h = new Harmony("seamsmoke.weather");
        h.Patch(AccessTools.Method(typeof(WeatherSimulationRegion), nameof(WeatherSimulationRegion.sendWeatherUpdatePacket)),
            postfix: new HarmonyMethod(typeof(SeamSmoke), nameof(CountSend)));
        instance = this;
        int pending = 2;
        foreach (int x in new[] { 300, 300 + (int)p.Circumference })
        {
            api.WorldManager.LoadChunkColumnPriority(x / 32, z / 32, new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => { if (--pending == 0) api.Event.RegisterCallback(_ => CheckWeather(ws, 0), 5000); } });
        }
    }

    static SeamSmoke instance;
    static void CountSend(WeatherSimulationRegion __instance)
    {
        var s = instance;
        if (s == null) return;
        var ws = s.api.ModLoader.GetModSystem<WeatherSystemServer>();
        long key = ws.MapRegionIndex2D(__instance.regionX, __instance.regionZ);
        if (key == s.mirrorA || key == s.mirrorB) s.sends++;
    }

    void CheckWeather(WeatherSystemServer ws, int round)
    {
        try
        {
            ws.weatherSimByMapRegion.TryGetValue(mirrorA, out var a);
            ws.weatherSimByMapRegion.TryGetValue(mirrorB, out var b);
            bool same = a != null && b != null && a.ToBytes().AsSpan().SequenceEqual(b.ToBytes());
            Mod.Logger.Notification("SEAMSMOKE weather round {0}: both sides loaded={1}, identical={2}, sync sends so far={3}", round, a != null && b != null, same, sends);
            // The sync decides with a comparison that serializes only the small part of the state; it must agree with the
            // full state bytes on the mirror pair and on every pair of loaded regions, both ways
            var sync = api.ModLoader.GetModSystem<SeamWeatherSync>();
            var cheap = AccessTools.Method(typeof(SeamWeatherSync), "SameState");
            WeatherSimulationRegion[] regions;
            lock (AccessTools.Field(typeof(WeatherSystemBase), "weatherSimByMapRegionLock").GetValue(ws)) regions = ws.weatherSimByMapRegion.Values.Where(r => r != null).Take(12).ToArray();
            var pairs = regions.SelectMany(r => regions.Select(s => (r, s))).ToList();
            if (a != null && b != null) pairs.Add((a, b));
            int agree = 0, equal = 0;
            foreach (var (r, s) in pairs)
            {
                bool full = r.ToBytes().AsSpan().SequenceEqual(s.ToBytes());
                if ((bool)cheap.Invoke(sync, new object[] { r, s }) == full) agree++;
                if (full && r != s) equal++;
            }
            Mod.Logger.Notification("SEAMSMOKE weather comparison: {0} of {1} region pairs agree with the full state bytes ({2} different regions equal)", agree, pairs.Count, equal);
            Check(agree == pairs.Count && pairs.Count > regions.Length, "the weather sync's cheap comparison agrees with the full state bytes");
            if (round == 0)
            {
                Check(same, "mirror regions have identical weather after a sync");
                sends = 0;
                api.Event.RegisterCallback(_ => CheckWeather(ws, 1), 12000);
                return;
            }
            Check(same, "mirror regions still identical later");
            Mod.Logger.Notification("SEAMSMOKE weather re-sends in 12 s while identical: {0} (each one restarts a transition on nearby clients)", sends);
            CheckWeatherFailures(sync);
            StartTerrain();
        }
        catch (Exception e) { End("FAIL " + e); }
    }

    static Exception loadFails;
    static bool forceCopy;
    static bool ForceCopy(ref bool __result)
    {
        if (!forceCopy) return true;
        __result = false;
        return false;
    }
    static void FailLoad() { if (loadFails != null) throw loadFails; }

    /// <summary>
    /// A weather pair whose copy keeps throwing (R2C6): tried again less and less often, not every 2 s; a copy that fails
    /// because the game changed switches the weather sync off (one log line); once a copy works the pair is synced again.
    /// The mirror pair is made to look different (SameState answers false) and the region loader throws first; no weather
    /// state changes.
    /// </summary>
    void CheckWeatherFailures(SeamWeatherSync sync)
    {
        int rs = api.WorldManager.RegionSize, shift = (int)(p.Circumference / rs);
        var syncPairs = AccessTools.Method(typeof(SeamWeatherSync), "SyncPairs");
        var failing = (IDictionary)AccessTools.Field(typeof(SeamWeatherSync), "failing").GetValue(sync);
        object feature = AccessTools.Field(typeof(SeamWeatherSync), "feature").GetValue(sync);
        var on = AccessTools.Field(feature.GetType(), "on");
        var h = new Harmony("seamsmoke.weatherfail");
        h.Patch(AccessTools.Method(typeof(SeamWeatherSync), "SameState"), prefix: new HarmonyMethod(typeof(SeamSmoke), nameof(ForceCopy)));
        h.Patch(AccessTools.Method(typeof(WeatherSimulationRegion), "FromBytes"), prefix: new HarmonyMethod(typeof(SeamSmoke), nameof(FailLoad)));
        void Pairs() => syncPairs.Invoke(sync, new object[] { p, rs, shift });
        try
        {
            forceCopy = true;
            loadFails = new NullReferenceException("seamsmoke: a region that isn't set up");
            long now = api.World.ElapsedMilliseconds;
            Pairs();
            var first = failing.Contains(mirrorA) ? ((int Count, long RetryAt))failing[mirrorA] : default;
            Pairs();
            var second = failing.Contains(mirrorA) ? ((int Count, long RetryAt))failing[mirrorA] : default;
            Mod.Logger.Notification("SEAMSMOKE weather copy failing: {0} time(s), tried again in {1} ms; after another sync {2} time(s)", first.Count, first.RetryAt - now, second.Count);
            Check(first.Count == 1 && first.RetryAt - now >= 4000 && second.Count == 1 && AccessTools.Property(feature.GetType(), "On").GetValue(feature) is true,
                "a weather pair whose copy throws is tried again later, not at every sync");

            loadFails = new MissingMethodException("seamsmoke: a changed game");
            failing.Remove(mirrorA);
            Pairs();
            Check(AccessTools.Property(feature.GetType(), "On").GetValue(feature) is false, "a weather copy that fails because the game changed switches the weather sync off");

            loadFails = null;
            on.SetValue(feature, true);
            Pairs();
            Check(!failing.Contains(mirrorA), "a weather pair is synced again once its copy works");
        }
        finally
        {
            forceCopy = false;
            loadFails = null;
            on.SetValue(feature, true);
            h.UnpatchAll(h.Id);
        }
    }

    // ---------------------------------------------------------------- generated terrain (default layout, no Earth Map)

    /// <summary>Generate the columns the terrain checks read, then run them.</summary>
    void StartTerrain()
    {
        var columns = new HashSet<(int, int)>();
        var rampRows = new HashSet<int>(BlendRows());
        var shelfRows = new HashSet<int>(ShelfRows());
        SeamSmokeNatural.Wanted = (x, z) => rampRows.Contains(z) && x >= RampFrom && x < RampTo
            || shelfRows.Contains(z) && x >= ShelfFrom && x < ShelfTo;
        // Seam ramp profile on the west side, three rows, from before the wavy edge to well past the band
        foreach (int z in BlendRows())
            for (int x = RampFrom; x <= RampTo; x += 32) columns.Add((x / 32, z / 32));
        // The wavy edge along a stretch of z
        foreach (int z in EdgeRows())
            for (int x = EdgeFrom; x <= EdgeTo; x += 32) columns.Add((x / 32, z / 32));
        // Walking across the jump line on the ice sheet, next to both walls
        foreach (int z in SheetRows())
            foreach (int x in SheetCrossing()) { columns.Add((x / 32, z / 32)); columns.Add(((x + (int)p.Circumference) / 32, z / 32)); }
        // Pack ice just beyond the north ice sheet's edge, in the flat seam band (always open sea there)
        foreach (var (x, z) in FloeArea()) columns.Add((x / 32, z / 32));
        columns.Add(WallColumn());
        // The polar shelf across the band's inner edge
        foreach (int z in ShelfRows())
            for (int x = p.SeamHalfWidth - SeamParams.SeamBlend - 32; x <= p.SeamHalfWidth + 64; x += 32) columns.Add((x / 32, z / 32));
        // Mirrored flat-band columns on both sides of the west jump line
        foreach (int z in MirrorRows())
            foreach (int x in MirrorXs()) { columns.Add((x / 32, z / 32)); columns.Add(((x + (int)p.Circumference) / 32, z / 32)); }
        // A region beyond the old ocean-force cull distance that the coast noise still reaches
        var coast = FindFarCoast();
        if (coast.HasValue) columns.Add(((int)coast.Value.x / 32, (int)coast.Value.z / 32));
        else Mod.Logger.Notification("SEAMSMOKE terrain: no coast-noise spot beyond the old cull distance in this seed; region check skipped");

        int pending = columns.Count;
        foreach (var (cx, cz) in columns)
        {
            api.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
            {
                KeepLoaded = true,
                OnLoaded = () => { if (--pending == 0) api.Event.RegisterCallback(_ => { Digest(columns); CheckTerrain(coast); }, 1000); }
            });
        }
    }

    /// <summary>
    /// Equivalence fingerprint of the generated terrain, before any check edits a block: a SHA-256 over every block id
    /// and fluid id of every chunk, plus the two height maps and YMax, for each requested chunk column in a fixed order.
    /// Two builds generate the same terrain here when every line matches (same seed, settings and mods; the block table
    /// line shows the ids mean the same blocks).
    /// </summary>
    void Digest(HashSet<(int, int)> columns)
    {
        int chunksHigh = api.WorldManager.MapSizeY / 32;
        var table = string.Join("\n", api.World.Blocks.Select((b, i) => i + " " + b?.Code));
        Mod.Logger.Notification("SEAMSMOKE DIGEST block table {0}", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(table)))[..16]);
        // Differ from run to run of the same build, so hashed as air: vanilla's structures pick loot vessel variants and
        // place tapestries with an unseeded random, and plant patches (seaweed, sea lace, flowers) spill into neighbouring
        // chunks in whatever order the worldgen threads finish them. The band's own plant clearing is checked by the
        // mirrored-cell comparison instead.
        var blocks = api.World.Blocks;
        var unstable = new bool[blocks.Count];
        for (int id = 0; id < blocks.Count; id++)
        {
            var b = blocks[id];
            string path = b?.Code?.Path;
            unstable[id] = path != null && (path.StartsWith("lootvessel") || path.StartsWith("tapestry") || b.BlockMaterial == EnumBlockMaterial.Plant);
        }
        int Stable(int id) => id >= 0 && id < unstable.Length && unstable[id] ? 0 : id;
        using var all = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 32 * 32 * 8];
        int done = 0, missing = 0;
        // optional: "cx cz" lines in ModConfig/seamsmoke-dump.txt write those columns' raw blocks to dump-cx-cz.bin
        string dumpList = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "seamsmoke-dump.txt");
        var dump = System.IO.File.Exists(dumpList) ? new HashSet<string>(System.IO.File.ReadAllLines(dumpList).Select(l => l.Trim())) : new HashSet<string>();
        if (dump.Count > 0) System.IO.File.WriteAllText(System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.DataPath, "blockcodes.txt"), table);
        foreach (var (cx, cz) in columns.OrderBy(c => c.Item1).ThenBy(c => c.Item2))
        {
            using var column = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var map = api.WorldManager.GetMapChunk(cx, cz);
            if (map == null) { missing++; continue; }
            using var raw = dump.Contains(cx + " " + cz) ? System.IO.File.Create(System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.DataPath, "dump-" + cx + "-" + cz + ".bin")) : null;
            for (int cy = 0; cy < chunksHigh; cy++)
            {
                var chunk = api.WorldManager.GetChunk(cx, cy, cz);
                if (chunk == null) { column.AppendData(BitConverter.GetBytes(-1)); continue; }
                chunk.Unpack_ReadOnly();
                var data = chunk.Data;
                for (int i = 0; i < 32 * 32 * 32; i++)
                {
                    // the indexer returns the fluid where there is no solid block: count that as empty, like a skipped plant
                    int fluid = data.GetFluid(i), solid = data[i];
                    BitConverter.TryWriteBytes(buffer.AsSpan(i * 8), solid == fluid ? 0 : Stable(solid));
                    BitConverter.TryWriteBytes(buffer.AsSpan(i * 8 + 4), fluid);
                }
                column.AppendData(buffer);
                raw?.Write(buffer);
            }
            foreach (var h in new[] { map.WorldGenTerrainHeightMap, map.RainHeightMap })
                foreach (ushort v in h) column.AppendData(BitConverter.GetBytes(v));
            column.AppendData(BitConverter.GetBytes(map.YMax));
            string hex = Convert.ToHexString(column.GetHashAndReset());
            all.AppendData(System.Text.Encoding.ASCII.GetBytes(hex));
            Mod.Logger.Notification("SEAMSMOKE DIGEST {0} {1} {2}", cx, cz, hex[..16]);
            done++;
        }
        Mod.Logger.Notification("SEAMSMOKE DIGEST ALL {0} columns, {1} missing: {2}", done, missing, Convert.ToHexString(all.GetHashAndReset())[..16]);
    }

    (int, int) WallColumn() => ((p.JumpInset + 160) / 32, p.WallDepth / 32);

    /// <summary>North rows beyond the ice sheet's edge, inside the raised shelf, where the polar worldgen owns the band.</summary>
    int[] ShelfRows()
    {
        double edge = p.WallDepth + p.SheetEdgeDistance(p.SeamHalfWidth);
        return new[] { 0.3, 0.6, 0.85 }.Select(f => (int)(edge + f * p.ShelfExtra)).ToArray();
    }

    /// <summary>West columns (chunk-aligned) whose mirror at x + C must match: past the jump line, just inside it, and
    /// at the far end of the identical part, which is in view from the line.</summary>
    int[] MirrorXs() => new[] { p.JumpInset - 64, p.JumpInset + 64, (int)(p.JumpInset + p.IdenticalReach) / 32 * 32 - 32 };

    int[] MirrorRows() => new[] { (int)p.EquatorZ }.Concat(ShelfRows()).ToArray();

    int[] BlendRows() => new[] { (int)p.EquatorZ, (int)p.EquatorZ + 1000, (int)p.EquatorZ - 3000 };

    int ShelfFrom => p.SeamHalfWidth - SeamParams.SeamBlend - 16;
    int ShelfTo => p.SeamHalfWidth + 48;
    int RampFrom => p.SeamHalfWidth - SeamParams.SeamBlend - SeamParams.SeamWave - 64;
    int RampTo => p.SeamHalfWidth + 6 * 32;

    /// <summary>Rows 400 apart along z near the equator, to see the wavy edge move.</summary>
    int[] EdgeRows() => Enumerable.Range(0, 8).Select(i => (int)p.EquatorZ + 500 + i * 400).ToArray();
    int EdgeFrom => p.SeamHalfWidth - SeamParams.SeamBlend - SeamParams.SeamWave - 32;
    int EdgeTo => p.SeamHalfWidth - SeamParams.SeamBlend + 16;

    /// <summary>On the ice sheet, 300 blocks from each wall face.</summary>
    int[] SheetRows() => new[] { p.WallDepth + 300, p.WorldLength - p.WallDepth - 300 };
    IEnumerable<int> SheetCrossing() => new[] { p.JumpInset - 32, p.JumpInset };

    IEnumerable<(int x, int z)> FloeArea()
    {
        for (int x = p.JumpInset + 160; x < p.JumpInset + 160 + 4 * 32; x += 32)
        {
            int edge = p.WallDepth + (int)p.SheetEdgeDistance(x + 16);
            for (int z = edge - 32; z < edge + 2 * 32 + p.PackIceDepth / 2; z += 32) yield return (x, z);
        }
    }

    /// <summary>A spot whose region the pre-0.11.4 cull skipped (it starts beyond H + CoastFalloff + regionSize)
    /// but where OceanForce is clearly above 0.</summary>
    (double x, double z, int force)? FindFarCoast()
    {
        int rs = api.WorldManager.RegionSize;
        double oldReach = p.SeamHalfWidth + p.CoastFalloff + rs;
        double firstX = (Math.Floor(oldReach / rs) + 1) * rs + 8;   // first region fully beyond the old reach
        for (double x = firstX; x < p.OceanForceReach; x += 64)
            for (double z = p.WorldLength * 0.3; z < p.WorldLength * 0.7; z += 97)
            {
                int force = p.OceanForce(x, z);
                if (force > 60) return (x, z, force);
            }
        return null;
    }

    void CheckTerrain((double x, double z, int force)? coast)
    {
        try
        {
            var ba = api.World.BlockAccessor;
            int sea = api.World.SeaLevel;

            Check(p.Version == SeamParams.CurrentVersion, "the smoke world has the current layout (v" + p.Version + ")");

            // 1. Seam ramp: the band's identical part follows its seabed up to the (wavy) edge, then a ramp into natural
            //    terrain, steepest at 1 : 4 whatever height it covers
            int worstStep = 0, flatMiss = 0, waterHeightmaps = 0, underwater = 0;
            string worstAt = "", flatAt = "";
            int naturalWorst = 0, floating = 0, worstExcess = int.MinValue, anyRise = 0, ruleMiss = 0, unrecorded = 0, rampColumns = 0;
            string excessAt = "", anyRiseAt = "", ruleAt = "";
            const int RiseRun = 16;
            foreach (int z in BlendRows())
            {
                var tops = new List<int>();
                var naturals = new List<int>();
                var floors = new List<int>();
                for (int x = RampFrom; x < RampTo; x++)
                {
                    int top = GroundTop(ba, x, z);
                    tops.Add(top);
                    floors.Add(p.SeamFloorY(x + 0.5, z + 0.5));
                    if (!SeamSmokeNatural.Natural.TryGetValue((x, z), out int natural)) { unrecorded++; natural = top; }
                    naturals.Add(natural);
                    if (FloatingWater(ba, x, z, sea)) floating++;
                    double u = p.DistToEdge(x + 0.5) - p.SeamEdge(x + 0.5, z + 0.5);   // blocks into the ramp
                    if (u < 0 && top != p.SeamFloorY(x + 0.5, z + 0.5) && flatMiss++ == 0) flatAt = $"x={x} z={z}: {top} vs {p.SeamFloorY(x + 0.5, z + 0.5)}";
                    int prev = tops.Count > 1 ? tops[^2] : -1;
                    // Around the edge, where the ramp has barely started, the profile must be smooth
                    if (prev >= 0 && Math.Abs(top - prev) > worstStep && u > -48 && u < 48)
                    {
                        worstStep = Math.Abs(top - prev);
                        worstAt = $"x={x} z={z} ramp+{u:0} {prev}->{top}";
                    }
                    if (prev >= 0) naturalWorst = Math.Max(naturalWorst, Math.Abs(top - prev));
                    if (u >= 0)
                    {
                        // The ramp is exactly the rule the map predictors use, applied to the terrain it started from
                        rampColumns++;
                        int want = p.SeamGround(x + 0.5, z + 0.5, natural);
                        if (top != want && ruleMiss++ == 0) ruleAt = $"x={x} z={z} ramp+{u:0}: {top}, rule {want} from natural {natural}";
                    }
                    if (tops.Count > RiseRun)
                    {
                        int first = tops.Count - RiseRun - 1;
                        int rise = Math.Abs(top - tops[first]);
                        string at = $"x={x - RiseRun}..{x} z={z} ramp+{u:0} {tops[first]}->{top}";
                        if (rise > anyRise) { anyRise = rise; anyRiseAt = at; }
                        // The ramp adds at most 1 : 4 to what the natural ground and the band's seabed do over the
                        // same run (the rule moves at most 1 per block of either), plus 1 for rounding
                        int naturalVar = 0, floorVar = 0;
                        for (int i = first; i < tops.Count - 1; i++)
                        {
                            naturalVar += Math.Abs(naturals[i + 1] - naturals[i]);
                            floorVar += Math.Abs(floors[i + 1] - floors[i]);
                        }
                        int excess = rise - (int)Math.Ceiling(naturalVar + 1.25 * floorVar) - 1;
                        if (excess > worstExcess) { worstExcess = excess; excessAt = at + $" (natural varies {naturalVar}, seabed {floorVar})"; }
                    }
                    if (top < sea - 2)
                    {
                        underwater++;
                        int hm = ba.GetMapChunk(x / 32, z / 32).WorldGenTerrainHeightMap[(z % 32) * 32 + x % 32];
                        if (hm >= sea - 1) waterHeightmaps++;
                    }
                }
            }
            Mod.Logger.Notification("SEAMSMOKE terrain: ramp worst step {0} near the edge ({1}), {2} anywhere; seabed misses {3} {4}; water-surface heightmaps {5}/{6}",
                worstStep, worstAt, naturalWorst, flatMiss, flatAt, waterHeightmaps, underwater);
            Mod.Logger.Notification("SEAMSMOKE terrain: {0} ramp columns, {1} off the ramp rule {2}; {3} profile columns without a recorded natural ground; worst rise over {4} blocks {5} ({6}); worst rise beyond what the natural terrain and seabed explain {7} blocks ({8})",
                rampColumns, ruleMiss, ruleAt, unrecorded, RiseRun, anyRise, anyRiseAt, worstExcess, excessAt);
            Check(flatMiss == 0, "the band's identical part is at its ring-periodic seabed");
            Check(worstStep <= 3, "no step where the band's seabed starts ramping into natural terrain");
            Check(unrecorded == 0 && rampColumns > 0 && ruleMiss == 0, "the generated ramp follows SeamGround from the natural ground");
            Check(worstExcess <= RiseRun / 4, "the ramp adds no more than 1 : 4 to the natural terrain's slope");
            Check(waterHeightmaps == 0, "underwater heightmaps record the seabed, not the water surface");
            Mod.Logger.Notification("SEAMSMOKE terrain: {0} ramp columns with water floating over air", floating);
            Check(floating == 0, "no water left floating where the ramp lowered the ground");

            // 1a. The ramp rule itself: steepest at 1 : 4 for any height it can cover, and never past the work reach
            double steepest = 0;
            for (int delta = 0; delta <= (int)(p.RampMax / 6); delta += 3)
                for (double u = 0; u < p.RampMax + 2; u++)
                {
                    double x = p.SeamEdge(600.5, 4000.5) + u + 0.5;
                    steepest = Math.Max(steepest, delta * Math.Abs(p.SeamRampWeight(x + 1, 4000.5, delta, p.RampMax) - p.SeamRampWeight(x, 4000.5, delta, p.RampMax)));
                }
            Mod.Logger.Notification("SEAMSMOKE terrain: steepest ramp for heights up to {0}: {1:0.000} per block; work reach {2}", (int)(p.RampMax / 6), steepest, p.SeamWorkReach);
            Check(steepest <= 0.25 + 1e-6 && p.SeamWorkReach >= p.SeamHalfWidth - SeamParams.SeamBlend + p.RampMax, "the ramp rule is at most 1 : 4");

            // 1d. The identical part ends on a wavy line, not a straight one: in every row the generated seabed follows
            //     the band's seabed up to that row's edge, and the edge moves between rows. The seabed itself has relief.
            int edgeMiss = 0;
            double edgeMin = double.MaxValue, edgeMax = double.MinValue;
            int seabedMin = int.MaxValue, seabedMax = int.MinValue;
            foreach (int z in EdgeRows())
            {
                double edge = p.SeamEdge(EdgeFrom + 0.5, z + 0.5);
                edgeMin = Math.Min(edgeMin, edge); edgeMax = Math.Max(edgeMax, edge);
                for (int x = EdgeFrom; x < EdgeTo; x++)
                {
                    if (p.DistToEdge(x + 0.5) >= edge) break;
                    int top = GroundTop(ba, x, z);
                    seabedMin = Math.Min(seabedMin, top); seabedMax = Math.Max(seabedMax, top);
                    if (top != p.SeamFloorY(x + 0.5, z + 0.5)) edgeMiss++;
                }
            }
            Mod.Logger.Notification("SEAMSMOKE terrain: band edge between {0:0} and {1:0} from the world edge over {2} rows, {3} seabed misses before it; seabed {4}..{5}",
                edgeMin, edgeMax, EdgeRows().Length, edgeMiss, seabedMin, seabedMax);
            Check(edgeMiss == 0 && edgeMax - edgeMin >= 24, "the band's identical part ends on a wavy line");
            Check(seabedMax - seabedMin >= 6, "the band's seabed has relief");

            // 1e. Walking across the jump line on the ice sheet next to both walls: a surface above the sea, the same
            //     on both sides of the jump
            int sheetBad = 0, sheetColumns = 0;
            string sheetAt = "";
            foreach (int z in SheetRows())
                foreach (int x0 in SheetCrossing())
                    for (int x = x0; x < x0 + 32; x++)
                    {
                        sheetColumns++;
                        int top = SurfaceTop(ba, x, z), mirror = SurfaceTop(ba, x + (int)p.Circumference, z);
                        if ((top < sea || top != mirror) && sheetBad++ == 0) sheetAt = $"x={x} z={z}: {top} vs {mirror} (sea {sea})";
                    }
            Mod.Logger.Notification("SEAMSMOKE terrain: ice sheet across the jump line at both walls: {0} of {1} columns without a matching surface above the sea {2}", sheetBad, sheetColumns, sheetAt);
            Check(sheetBad == 0, "the ice sheet can be walked across the seam at both poles");

            // 1b. The polar shelf meets the band's inner edge without a cliff. Both the band's ramp strip and the outside
            //     rule raise the natural ground toward the shelf (structures on land included: they are ground there),
            //     so a step is only a cliff beyond what that rule makes of the natural ground's own step.
            int shelfStep = 0, owned = 0, shelfExcess = int.MinValue, shelfUnrecorded = 0;
            string shelfAt = "", shelfExcessAt = "";
            foreach (int z in ShelfRows())
            {
                int prev = -1, prevRaised = -1;
                for (int x = ShelfFrom; x < ShelfTo; x++)
                {
                    if (p.PolarOwnsSeabed(x + 0.5, z + 0.5)) owned++;
                    int top = GroundTop(ba, x, z);
                    if (!SeamSmokeNatural.Natural.TryGetValue((x, z), out int natural)) { shelfUnrecorded++; natural = top; }
                    var col = p.GetPolarColumn(x + 0.5, z + 0.5);
                    int raised = natural < col.Floor ? natural + (int)Math.Round((col.Floor - natural) * col.ShelfBlend) : natural;
                    if (prev >= 0)
                    {
                        int step = Math.Abs(top - prev), excess = step - Math.Abs(raised - prevRaised);
                        if (step > shelfStep) { shelfStep = step; shelfAt = $"x={x} z={z} {prev}->{top}"; }
                        if (excess > shelfExcess) { shelfExcess = excess; shelfExcessAt = $"x={x} z={z} {prev}->{top}, natural raised {prevRaised}->{raised}"; }
                    }
                    prev = top;
                    prevRaised = raised;
                }
            }
            Mod.Logger.Notification("SEAMSMOKE terrain: polar shelf across the band edge: worst step {0} ({1}), worst beyond the raised natural ground {2} ({3}); {4} columns owned by the polar worldgen, {5} without a recorded natural ground",
                shelfStep, shelfAt, shelfExcess, shelfExcessAt, owned, shelfUnrecorded);
            Check(owned > 0 && shelfUnrecorded == 0 && shelfExcess <= 8, "the polar shelf blends across the seam band's inner edge");

            // 1c. The flat band is identical at x and x + C, from the seabed up (wood, caves and structures included), and
            //     below it where a lower neighbour's floor shows its side: from one below the lowest neighbour floor
            int mirrorDiff = 0, mirrorCells = 0, sideCells = 0;
            string mirrorAt = "";
            foreach (int z in MirrorRows())
                foreach (int x0 in MirrorXs())
                for (int x = x0; x < x0 + 32; x++)
                {
                    int floorY = GroundTop(ba, x, z), from = floorY;
                    if (!p.PolarOwnsSeabed(x + 0.5, z + 0.5) && p.DistToJumpLine(x + 0.5) > SeamParams.CurtainHalfWidth)
                    {
                        int lowest = floorY;
                        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }) lowest = Math.Min(lowest, p.SeamFloorY(x + dx + 0.5, z + dz + 0.5));
                        if (lowest < floorY) from = Math.Max(1, lowest - 1);
                        sideCells += floorY - from;
                    }
                    for (int y = from; y < api.WorldManager.MapSizeY - 1; y++)
                    {
                        var a = new BlockPos(x, y, z);
                        var b = new BlockPos(x + (int)p.Circumference, y, z);
                        mirrorCells++;
                        if (ba.GetBlock(a, BlockLayersAccess.Solid).Id == ba.GetBlock(b, BlockLayersAccess.Solid).Id
                            && ba.GetBlock(a, BlockLayersAccess.Fluid).Id == ba.GetBlock(b, BlockLayersAccess.Fluid).Id) continue;
                        if (mirrorDiff++ == 0) mirrorAt = $"x={x} y={y} z={z}: {ba.GetBlock(a).Code} vs {ba.GetBlock(b).Code}";
                    }
                }
            Mod.Logger.Notification("SEAMSMOKE terrain: mirrored band columns differ in {0} of {1} cells ({2} of them below the floor beside a lower neighbour) {3}", mirrorDiff, mirrorCells, sideCells, mirrorAt);
            Check(mirrorDiff == 0, "the seam band is identical on both sides of the jump");

            // 2. Default-layout floes beyond the ice sheet
            int floeId = api.World.GetBlock(new AssetLocation("cylinderworld:glacier-floe")).Id, floes = 0, missing = 0;
            foreach (var (x0, z0) in FloeArea())
                for (int dx = 0; dx < 32; dx++)
                    for (int dz = 0; dz < 32; dz++)
                    {
                        int x = x0 / 32 * 32 + dx, z = z0 / 32 * 32 + dz;
                        var pos = new BlockPos(x, sea - 1, z);
                        int solid = ba.GetBlock(pos, BlockLayersAccess.Solid).Id;
                        if (solid == floeId) { floes++; continue; }
                        if (!p.GetPolarColumn(x + 0.5, z + 0.5).Floe || solid != 0 || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id == 0) continue;
                        pos.Y++;
                        if (ba.GetBlock(pos, BlockLayersAccess.Solid).Id == 0 && ba.GetBlock(pos, BlockLayersAccess.Fluid).Id == 0) missing++;
                    }
            Mod.Logger.Notification("SEAMSMOKE terrain: {0} floes beyond the ice sheet, {1} floe columns left as open water", floes, missing);
            Check(floes > 0 && missing == 0, "pack-ice floes are generated on open water");

            // 3. Raised walls and ice count in YMax, or worldgen sunlight fills the chunks above it with full light
            int lowYMax = 0, polarChunks = 0;
            var polar = new HashSet<(int, int)>(FloeArea().Select(c => (c.x / 32, c.z / 32))) { WallColumn() };
            foreach (var (cx, cz) in polar)
            {
                var mc = ba.GetMapChunk(cx, cz);
                polarChunks++;
                // Terrain, not rain height: vanilla's snow layer goes one above YMax by its own convention
                if (mc.YMax < mc.WorldGenTerrainHeightMap.Max())
                {
                    if (lowYMax < 3) Mod.Logger.Notification("SEAMSMOKE terrain: chunk {0},{1} YMax {2}, highest terrain {3}", cx, cz, mc.YMax, mc.WorldGenTerrainHeightMap.Max());
                    lowYMax++;
                }
            }
            Mod.Logger.Notification("SEAMSMOKE terrain: {0} of {1} polar chunk columns with YMax below their highest terrain", lowYMax, polarChunks);
            Check(lowYMax == 0, "YMax covers raised walls and ice");

            // 4. The ocean map is forced in regions the old cull distance skipped
            if (coast.HasValue)
            {
                var (cxw, czw, force) = coast.Value;
                int rs = api.WorldManager.RegionSize;
                var map = api.WorldManager.GetMapRegion((int)cxw / rs, (int)czw / rs)?.OceanMap;
                double bpp = (double)rs / map.InnerSize;
                int value = map.GetUnpaddedInt((int)(cxw % rs / bpp), (int)(czw % rs / bpp));
                Mod.Logger.Notification("SEAMSMOKE terrain: ocean map {0} at x={1:0} z={2:0} (forced {3})", value, cxw, czw, force);
                Check(value >= force - 40, "coast-noise ocean force reaches regions beyond the old cull distance");
            }

            CheckClimateReuse();
            CheckOpenSea();
            CheckExposureSave();
            CheckWrapCollision();
        }
        catch (Exception e) { End("FAIL " + e); return; }
        End(failures == 0 ? "PASS all seam checks" : "FAIL " + failures + " seam check(s)");
    }

    /// <summary>
    /// Vanilla's catch-up loops (snow accumulation, farmland, berry bushes) reuse one climate reading and raise the climate
    /// event on it again for every hour; the extended temperature must be corrected once, not once per hour. On the ice
    /// sheet (its columns are loaded), 480 hours of reuse.
    /// </summary>
    void CheckClimateReuse()
    {
        var ba = api.World.BlockAccessor;
        var pos = new BlockPos(p.JumpInset, p.SeaLevel + 40, p.WallDepth + 300);
        const EnumGetClimateMode mode = EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly;
        double days = api.World.Calendar.TotalDays;
        var fresh = ba.GetClimateAt(pos, mode, days);
        var reused = ba.GetClimateAt(pos, mode, days);
        for (int i = 1; i <= 480; i++) ba.GetClimateAt(pos, reused, mode, days + i / 24.0);
        var last = ba.GetClimateAt(pos, mode, days + 480 / 24.0);
        Mod.Logger.Notification("SEAMSMOKE climate reuse on the ice sheet: base {0:0.##} fresh, {1:0.##} after 480 reuses; live {2:0.##} reused, {3:0.##} fresh",
            fresh?.WorldGenTemperature, reused?.WorldGenTemperature, reused?.Temperature, last?.Temperature);
        Check(fresh != null && fresh.WorldGenTemperature < -20 && reused.WorldGenTemperature == fresh.WorldGenTemperature
            && Math.Abs(reused.Temperature - last.Temperature) < 1e-3, "a reused climate reading keeps the extended temperature correction once");
    }

    /// <summary>
    /// The cold exposure meter keeps its record only in worlds where Cylinder World is on (R2C5): its save without seam
    /// parameters (the mod off in this world) leaves the save as it is; with them it writes the record.
    /// </summary>
    void CheckExposureSave()
    {
        const string key = "cylinderworld-exposure";
        var exposure = api.ModLoader.GetModSystem<PolarExposureSystem>();
        var save = AccessTools.Method(typeof(PolarExposureSystem), "Save");
        var coreField = AccessTools.Field(typeof(PolarExposureSystem), "core");
        object core = coreField.GetValue(exposure);
        var game = api.WorldManager.SaveGame;
        byte[] mark = { 7, 7, 7 };
        try
        {
            game.StoreData(key, mark);
            coreField.SetValue(exposure, new CylinderWorldCore());   // no seam parameters: the mod is off in this world
            save.Invoke(exposure, null);
            bool untouched = game.GetData(key)?.SequenceEqual(mark) == true;
            coreField.SetValue(exposure, core);
            save.Invoke(exposure, null);
            bool written = game.GetData(key) is { Length: > 3 } record && BitConverter.ToInt32(record, 0) == -2;
            Mod.Logger.Notification("SEAMSMOKE exposure record: mod off left it={0}, mod on wrote it={1}", untouched, written);
            Check(untouched && written, "the cold exposure meter keeps a record only in a world where Cylinder World is on");
        }
        finally { coreField.SetValue(exposure, core); }
    }

    /// <summary>
    /// The cold exposure's open-sea test (Earth mode counts the polar sea only where it is frozen under the player): sea ice
    /// in the fluid layer and a block are not open sea, salt water is. On a flat-band column, put back afterwards.
    /// </summary>
    void CheckOpenSea()
    {
        var openSea = AccessTools.Method(typeof(PolarExposureSystem), "OpenSea");
        var exposure = api.ModLoader.GetModSystem<PolarExposureSystem>();
        var ba = api.World.BlockAccessor;
        int sea = p.SeaLevel, bx = (p.JumpInset + 64) / 32 * 32 + 24, bz = (int)p.EquatorZ / 32 * 32 + 16;
        var cell = new BlockPos(bx, sea - 1, bz);
        int salt = api.World.GetBlock(new AssetLocation("game:saltwater-still-7")).Id;
        var player = new EntityItem();
        player.Pos.SetPos(bx + 0.5, sea + 0.2, bz + 0.5);
        bool Open() => (bool)openSea.Invoke(exposure, new object[] { p, player });
        ba.SetBlock(salt, cell, BlockLayersAccess.Fluid);
        bool water = Open();
        ba.SetBlock(api.World.GetBlock(new AssetLocation("cylinderworld:seaice-seasonal")).Id, cell, BlockLayersAccess.Fluid);
        bool ice = Open();
        ba.SetBlock(salt, cell, BlockLayersAccess.Fluid);
        ba.SetBlock(api.World.GetBlock(new AssetLocation("cylinderworld:glacier-floe")).Id, cell, BlockLayersAccess.Solid);
        bool floe = Open();
        ba.SetBlock(0, cell, BlockLayersAccess.Solid);
        Mod.Logger.Notification("SEAMSMOKE exposure open sea: salt water={0}, sea ice={1}, floe={2}", water, ice, floe);
        Check(water && !ice && !floe, "cold exposure tells open sea from sea ice and floes under the player");
    }

    /// <summary>
    /// The wrap's landing test (SeamWrapSystem.IsPassable) on a flat-band column: sea ice in the fluid layer collides,
    /// standing on ice or on a snow layer doesn't, a block at head height does, and a boat's whole width counts.
    /// Runs last: it builds on the band.
    /// </summary>
    void CheckWrapCollision()
    {
        var passable = AccessTools.Method(typeof(SeamWrapSystem), "IsPassable");
        var wrap = api.ModLoader.GetModSystem<SeamWrapSystem>();
        var ba = api.World.BlockAccessor;
        int sea = p.SeaLevel, bx = (p.JumpInset + 64) / 32 * 32 + 16, bz = (int)p.EquatorZ / 32 * 32 + 16;
        int ice = api.World.GetBlock(new AssetLocation("cylinderworld:seaice-seasonal")).Id;
        int snow = api.World.GetBlock(new AssetLocation("game:snowlayer-1")).Id;
        int rock = api.World.GetBlock(new AssetLocation("game:rock-granite")).Id;
        var player = new EntityItem { CollisionBox = new Cuboidf(-0.3f, 0, -0.3f, 0.3f, 1.8f, 0.3f) };
        var boat = new EntityItem { CollisionBox = new Cuboidf(-1.4f, 0, -1.4f, 1.4f, 1f, 1.4f) };
        bool Pass(Entity e, double x, double y, double z) => (bool)passable.Invoke(wrap, new object[] { x, y, z, e });
        void Set(int id, int x, int y, int z, int layer = BlockLayersAccess.Solid) => ba.SetBlock(id, new BlockPos(x, y, z), layer);

        Set(ice, bx, sea - 1, bz, BlockLayersAccess.Fluid);
        bool onIce = Pass(player, bx + 0.5, sea, bz + 0.5);
        bool inIce = Pass(player, bx + 0.5, sea - 0.5, bz + 0.5);
        Set(snow, bx, sea, bz);
        bool onSnow = Pass(player, bx + 0.5, sea + 0.125, bz + 0.5);
        Set(0, bx, sea, bz);
        Set(rock, bx, sea + 2, bz);
        bool head = Pass(player, bx + 0.5, sea + 0.5, bz + 0.5);
        Set(0, bx, sea + 2, bz);
        Set(api.World.GetBlock(new AssetLocation("game:saltwater-still-7")).Id, bx, sea - 1, bz, BlockLayersAccess.Fluid);
        Set(rock, bx, sea, bz);
        bool boatEdge = Pass(boat, bx - 0.5, sea - 0.4, bz + 0.5);   // centre one block west, the rock under its east side
        bool boatOpen = Pass(boat, bx + 8.5, sea - 0.4, bz + 0.5);   // open water
        Set(0, bx, sea, bz);
        Mod.Logger.Notification("SEAMSMOKE wrap landing: on ice={0}, in ice={1}, on snow={2}, head blocked={3}, boat edge blocked={4}, boat in open water={5}",
            onIce, !inIce, onSnow, !head, !boatEdge, boatOpen);
        Check(onIce && !inIce && onSnow && !head && !boatEdge && boatOpen, "the wrap's landing test sees sea ice, snow layers, head height and the whole box");
    }

    /// <summary>Water above sea level resting on a cell with neither a block nor a fluid.</summary>
    bool FloatingWater(IBlockAccessor ba, int x, int z, int sea)
    {
        var pos = new BlockPos(x, api.WorldManager.MapSizeY - 2, z);
        var below = new BlockPos(x, 0, z);
        for (; pos.Y > sea; pos.Y--)
        {
            if (ba.GetBlock(pos, BlockLayersAccess.Fluid).Id == 0) continue;
            below.Y = pos.Y - 1;
            if (ba.GetBlock(below, BlockLayersAccess.Solid).Id == 0 && ba.GetBlock(below, BlockLayersAccess.Fluid).Id == 0) return true;
        }
        return false;
    }

    /// <summary>Highest block one can stand on: solid, not vegetation or snow cover; the mod's ice counts.</summary>
    int SurfaceTop(IBlockAccessor ba, int x, int z)
    {
        var pos = new BlockPos(x, api.WorldManager.MapSizeY - 2, z);
        for (; pos.Y > 0; pos.Y--)
        {
            var b = ba.GetBlock(pos, BlockLayersAccess.Solid);
            if (b.Id == 0) continue;
            var m = b.BlockMaterial;
            if (m == EnumBlockMaterial.Plant || m == EnumBlockMaterial.Leaves || m == EnumBlockMaterial.Snow) continue;
            return pos.Y;
        }
        return 0;
    }

    /// <summary>Top of the seabed: highest solid, non-vegetation block (solid layer only, so water never counts), skipping
    /// ground with sea water under it before the seabed (a vanilla structure left over deep water), as the worldgen does.</summary>
    int GroundTop(IBlockAccessor ba, int x, int z)
    {
        var pos = new BlockPos(x, api.WorldManager.MapSizeY - 2, z);
        int top = 0, run = 0;
        bool sea = false;
        for (; pos.Y > 0; pos.Y--)
        {
            var b = ba.GetBlock(pos, BlockLayersAccess.Solid);
            var m = b.BlockMaterial;
            // floes and the mod's ice aren't the seabed
            bool ground = b.Id != 0 && b.Code.Domain != "cylinderworld" && m != EnumBlockMaterial.Plant && m != EnumBlockMaterial.Leaves
                && m != EnumBlockMaterial.Wood && m != EnumBlockMaterial.Snow;
            if (ground)
            {
                if (top == 0 || sea) top = pos.Y;
                if (run == 0) run = pos.Y;
                sea = false;
                if (run - pos.Y >= 24) return top;
                continue;
            }
            run = 0;
            if (top != 0 && ba.GetBlock(pos, BlockLayersAccess.Fluid).Code.Path.StartsWith("saltwater")) sea = true;
        }
        return sea ? 0 : top;
    }

    void End(string result)
    {
        if (finished) return;
        finished = true;
        Mod.Logger.Notification("SEAMSMOKE " + result);
        api.Event.RegisterCallback(_ => api.Server.ShutDown(), 500);
    }
}
