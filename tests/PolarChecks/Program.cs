using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using CylinderWorld;
using EarthMap.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

class Program
{
    static int checks;
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string dir in new[] { args[0], Path.Combine(args[0], "Lib"), Path.Combine(args[0], "Mods"), args[1] })
            {
                string path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        try { Run(args); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(string[] args)
    {
        var p = Parameters();
        Check(new CylinderWorldConfig().PolarIceMode == "default" && !p.EarthPolarIce, "Earth mode remains opt-in");
        Check(new ClientMapRaceFix().ShouldLoad(EnumAppSide.Client) && !new ClientMapRaceFix().ShouldLoad(EnumAppSide.Server), "client race fix remains client-only");
        Check(new EarthPolarSystem().ShouldLoad(EnumAppSide.Server) && !new EarthPolarSystem().ShouldLoad(EnumAppSide.Client), "Earth adapter remains server-only");
        if (args.Length > 4 && !string.IsNullOrEmpty(args[4]) && args[4] != "-") CompareDefault(args[4]);
        CheckNaturalBand();
        CheckTemperature();
        CheckWorldgenShortcuts();
        CheckEarthBinder();
        var data = EarthData.LoadFromFolder(args[2], true);
        CheckRealSeam(data);
        var geo = EarthLayout.Build(new LayoutInput {
            WorldWidth = p.WorldWidth,
            CwRingStart = p.JumpInset, CwRingLength = p.Circumference, CwEquatorZ = p.EquatorZ,
            CwPoleDistance = p.PoleDistance, CwBandInner = p.SeamHalfWidth - p.JumpInset
        }, data, out string report);
        Console.WriteLine(report);
        var model = new TerrainModel(data, geo, VerticalScale.ForWorld(384, p.SeaLevel), p.NoiseSeed) { GutterSeabedY = p.SeabedY };
        p.EarthPolar = new EarthPolarModel(model);
        p.EarthPolarIce = true;
        Check(p.EarthPolar.HasWaterTop && p.EarthPolar.WaterTopMismatch == null, "Earth's columns say where their water is (WaterTop), as EarthSeamWater worlds need");
        if(args.Length>5 && args[5]=="routes") { RouteAudit.Run(model,p,args[3]);return; }
        foreach(var point in new[]{(72.0,140.0),(74.0,-95.0),(-65.0,20.0)})
        {
            geo.FromGeo(point.Item1,point.Item2,out double xx,out double zz);
            var c=p.EarthPolar.Sample(xx,zz);
            double minimum=SeaIcePolicy.NorthMinimum+(point.Item1<0?0.5:0);
            double maximum=SeaIcePolicy.NorthMaximum+(point.Item1<0?0.5:0);
            Check(Math.Abs(SeaIcePolicy.OceanTemperature(c,minimum)-c.WarmestMonth)<1e-9,"sea-ice summer matches warmest month");
            Check(Math.Abs(SeaIcePolicy.OceanTemperature(c,maximum)-c.ColdestMonth)<1e-9,"sea-ice winter matches coldest month");
            Check(Math.Abs(SeaIcePolicy.OceanTemperature(c,SeaIcePolicy.NorthMinimum,true)-c.WarmestMonth)<1e-9,"fixed summer applies locally in both hemispheres");
            Check(SeaIcePolicy.At(p,xx,zz,c,maximum)!=SeaIceKind.Open || !c.Ocean,"polar winter ice");
        }
        for(int month=0;month<12;month++)
        {
            double xx=p.WorldWidth/2,zz=p.WallDepth+500;
            Check(SeaIcePolicy.At(p,xx,zz,p.EarthPolar.Sample(xx,zz),month/12.0)==SeaIceKind.Permanent,"Wall approach stays permanent all year");
            geo.FromGeo(75,-45,out xx,out zz);
            Check(SeaIcePolicy.At(p,xx,zz,p.EarthPolar.Sample(xx,zz),month/12.0)==SeaIceKind.Open,"land glaciers never selected for sea-ice updates");
        }
        var random = new Random(821);
        for (int i = 0; i < 1200; i++)
        {
            double x = p.JumpInset + random.NextDouble() * p.Circumference;
            double z = random.NextDouble() * p.WorldLength;
            var natural = model.GetColumn(x, z);
            var adapted = p.EarthPolar.Sample(x, z);
            Check(natural.Top == adapted.Top && natural.IceBottom == adapted.IceBottom && natural.Water == adapted.Water && natural.Lat == adapted.Latitude
                && natural.WaterTop == adapted.WaterTop, "adapter exactly matches Earth terrain");
            Check(!p.HasArcticIce(x, z, adapted) || adapted.Ocean && adapted.Latitude >= 66, "Arctic ice never covers land or lower latitudes");
            Check(p.PlaneCap(x, z) == int.MaxValue && p.GetPolarColumn(x, z).Floor == -1, "Earth terrain is never planed or its seabed raised");
            if (SeaIcePolicy.InPolarOcean(adapted))
            {
                // SeaIceSystem keeps only these fields of a sample
                var compact = new EarthPolarModel.Column { Water = 1, Latitude = adapted.Latitude < 0 ? -90 : 90, WarmestMonth = adapted.WarmestMonth, ColdestMonth = adapted.ColdestMonth };
                for (int month = 0; month < 12; month += 3)
                    Check(SeaIcePolicy.At(p, x, z, adapted, month / 12.0) == SeaIcePolicy.At(p, x, z, compact, month / 12.0)
                        && SeaIcePolicy.At(p, x, z, adapted, month / 12.0, true) == SeaIcePolicy.At(p, x, z, compact, month / 12.0, true), "live sea ice's compact sample gives the same ice");
            }
        }
        foreach (var point in new[] { (75.0,-45.0), (78.2,15.6), (-85.0,20.0), (-70.0,-65.0) })
        {
            geo.FromGeo(point.Item1, point.Item2, out double x, out double z);
            var s = p.EarthPolar.Sample(x, z);
            Check(!p.HasArcticIce(x, z, s), "Greenland, islands and Antarctica are never replaced by Arctic sea ice");
            if (point.Item1 < -60) Check(p.EarthPolar.ForbidPolarPredators(p, z) && p.EarthPolar.ForbidFeatures(p, z)
                && !p.EarthPolar.ForbidNaturalSpawn(p, z), "Antarctica: no Arctic predators, hot springs or traders; other wildlife keeps its rules");
        }
        geo.FromGeo(89, 0, out double px, out double pz);
        Check(p.HasArcticIce(px, pz, p.EarthPolar.Sample(px, pz)), "continuous ice near the North Pole");
        Check(p.EarthPolar.ForbidNaturalSpawn(p, p.WallDepth + 1000) && !p.EarthPolar.ForbidNaturalSpawn(p, p.WallDepth + 1001), "lifeless north border is exactly one kilometre from the wall face");
        Check(p.EarthPolar.ForbidNaturalSpawn(p, p.WorldLength - p.WallDepth - 1000) && !p.EarthPolar.ForbidNaturalSpawn(p, p.WorldLength - p.WallDepth - 1001), "lifeless south border is exactly one kilometre from the wall face");
        Check(p.EarthPolar.ForbidFeatures(p, p.WallDepth + 1000) && !p.EarthPolar.ForbidPolarPredators(p, p.WallDepth + 1000), "north border blocks features; predator rule is southern");
        Check(!p.EarthPolar.ForbidNaturalSpawn(p, p.EquatorZ) && !p.EarthPolar.ForbidPolarPredators(p, p.EquatorZ) && !p.EarthPolar.ForbidFeatures(p, p.EquatorZ), "equatorial wildlife unaffected");
        geo.FromGeo(75, 0, out px, out pz);
        Check(!p.HasArcticIce(px, pz, p.EarthPolar.Sample(px, pz)), "warm Atlantic approach stays open");
        geo.FromGeo(-60, 0, out px, out pz);
        Check(p.EarthPolar.ForbidPolarPredators(p, pz) && !p.EarthPolar.ForbidPolarPredators(p, pz - 1)
            && p.EarthPolar.ForbidFeatures(p, pz) && !p.EarthPolar.ForbidFeatures(p, pz - 1), "Antarctic predator and feature boundary at 60°S");
        Check(new BlockGlacierIce().CanCreatureSpawnOn(null, null, null, null) == false, "custom glacier has no creature spawning");
        // The seam band keeps Antarctic land and ice (Earth's continent, shelves, and any gutter bridge) instead of
        // flattening it into a channel; everywhere else, and in default worlds, ownership is unchanged.
        var keepRandom = new Random(17);
        int kept = 0;
        for (int i = 0; i < 3000; i++)
        {
            double x = p.JumpInset + keepRandom.NextDouble() * p.Circumference, z = keepRandom.NextDouble() * p.WorldLength;
            var s = p.EarthPolar.Sample(Math.Floor(x), Math.Floor(z));
            bool expected = s.Latitude <= -60 && (!s.Ocean || s.Glacier);
            if (expected) kept++;
            Check(p.EarthKeepsSeamColumn(x, z) == expected, "seam band keeps exactly Antarctic land and ice");
            Check(p.PolarOwnsSeabed(x, z) == (expected || p.DistToZEdge(z) < p.WallFaceMax), "polar ownership = the Wall plus Antarctic land");
        }
        Check(kept > 0, "some Antarctic land sampled");
        geo.FromGeo(-80, 20, out double ax, out double az);
        Check(p.PolarOwnsSeabed(ax, az), "inland Antarctica owns its seabed");
        geo.FromGeo(75, -45, out ax, out az);
        Check(!p.PolarOwnsSeabed(ax, az), "Greenland is not polar-owned (the band never reaches it anyway)");
        geo.FromGeo(-85, 0, out _, out az);
        var gutter = p.EarthPolar.Sample(p.JumpInset + 10, az);
        Console.WriteLine($"Antarctic seam gutter at 85°S (old gutter layout): Earth water={gutter.Water}, glacier={gutter.Glacier}, kept by the band={p.EarthKeepsSeamColumn(p.JumpInset + 10.5, az)}");
        Check(EarthPolarSystem.IsTrader("trader","anything") && EarthPolarSystem.IsTrader("", "trader-cold")
            && !EarthPolarSystem.IsTrader("ruin", "ruin-temple"), "trader guard selects only trader camps");
        int highest=0, bare=0, snowy=0, drift=0, maxStep=0;
        var ocean = new EarthPolarModel.Column { Water=1, Latitude=89 };
        for (int ix=0;ix<256;ix++) for (int iz=0;iz<256;iz++)
        {
            double x=p.WorldWidth/2.0+ix,z=p.WallDepth+350+iz;
            int rise=p.EarthIceRise(x,z,ocean), snow=p.EarthSnowEighths(x,z);
            highest=Math.Max(highest,rise);
            maxStep=Math.Max(maxStep,Math.Abs(rise-p.EarthIceRise(x+1,z,ocean)));
            Check(rise>=0 && rise<=8, "Arctic ridges have a bounded height");
            if(snow==0) bare++; else snowy++;
            if(snow>=8) drift++;
        }
        Console.WriteLine($"Arctic 256x256: max ridge={highest}, max step={maxStep}, bare={bare}, snow={snowy}, full drifts={drift}");
        Check(highest>=4 && maxStep<=3 && bare>1000 && snowy>1000 && drift>100, "Arctic has broken hills, exposed ice and snow piles");
        for (int i=0;i<100;i++)
        {
            double x=p.JumpInset-0.5+i*47,z=p.WallDepth+400+i*13;
            Check(p.EarthIceRise(x,z,ocean)==p.EarthIceRise(x+p.Circumference,z,ocean)
                && p.EarthSnowEighths(x,z)==p.EarthSnowEighths(x+p.Circumference,z), "ridges and snow wrap exactly");
            Check(Math.Abs(p.EarthIceBlend(x,p.WallDepth+999.5)-p.EarthIceBlend(x,p.WallDepth+1000.5))<0.01,
                "no visible step at the wildlife boundary");
        }
        Check(p.EarthIceRise(px,pz,new EarthPolarModel.Column{Latitude=89,Water=0})==0
            && p.EarthIceRise(px,p.WorldLength-100,ocean)==0, "pressure ridges never reshape land or Antarctica");
        var restored = SerializerUtil.Deserialize<SeamParams>(SerializerUtil.Serialize(p));
        Check(restored.EarthPolarIce && restored.EarthPolar == null && restored.WallTopY == p.WallTopY, "Earth mode persists without serializing the runtime Earth adapter");
        p.EarthPolarIce = false;
        Check(!p.EarthPolar.ForbidNaturalSpawn(p, p.WorldLength - 1) && !p.EarthPolar.ForbidPolarPredators(p, p.WorldLength - 1)
            && !p.EarthPolar.ForbidFeatures(p, p.WorldLength - 1), "spawn and feature guards leave default worlds alone");
        geo.FromGeo(-80, 20, out double dx80, out double dz80);
        Check(!p.EarthKeepsSeamColumn(dx80, dz80), "default worlds flatten the seam band as before");
        p.EarthPolarIce = true;
        for (int i = 0; i < 80; i++)
        {
            double z = 60 + i * 120;
            var a = p.EarthPolar.Sample(p.JumpInset - 0.5, z);
            var b = p.EarthPolar.Sample(p.JumpInset - 0.5 + p.Circumference, z);
            Check(p.HasArcticIce(p.JumpInset - 0.5, z, a) == p.HasArcticIce(p.JumpInset - 0.5 + p.Circumference, z, b), "Arctic ice wraps across the seam");
        }
        Render(model, p, args[3]);
        p.SeasonalSeaIce=true;
        RenderSeasonal(model,p,args[3]);
        RenderArcticDetail(p,args[3]);
        Console.WriteLine($"PASS {checks} checks; previews in {args[3]}");
    }

    static SeamParams Parameters() => new SeamParams {
        Version = 9, WorldWidth = 256000, WorldLength = 102400, JumpInset = 25600, SeamHalfWidth = 26208,
        CoastFalloff = 512, WallDepth = 48, WallTopY = 283, SeaLevel = 165, SeabedY = 125,
        ApronDepth = 2841, ApronHeight = 70, PackIceDepth = 256, ShelfExtra = 320, BarrenDepth = 5115,
        NoiseSeed = 4242, PolarClimate = true, PoleTemperature = -30, EquatorTemperature = 30,
        ClimateCurveExponent = 1.25f, WallBuildMargin = 32
    };

    static void CompareDefault(string dll)
    {
        var context = new AssemblyLoadContext("CylinderWorldBaseline", true);
        var type = context.LoadFromAssemblyPath(Path.GetFullPath(dll)).GetType("CylinderWorld.SeamParams");
        // v10 too: the worldgen refactors since 1.0.4 must leave the current layout's geometry exactly as it was
        foreach (int version in new[] { 4, 5, 6, 7, 8, 9, 10 })
        {
            var p = Parameters(); p.Version = version;
            var old = Activator.CreateInstance(type);
            foreach (var f in typeof(SeamParams).GetFields(BindingFlags.Public | BindingFlags.Instance))
                type.GetField(f.Name)?.SetValue(old, f.GetValue(p));
            for (int i = 0; i < 240; i++)
            {
                // half across the ring, half across the seam band and its blend on both sides
                double x = i < 120 ? p.JumpInset - 10 + i * p.Circumference / 119
                    : (i % 2 == 0 ? 0 : p.WorldWidth) + (i % 2 == 0 ? 1 : -1) * (p.JumpInset - 64 + (i - 120) * 9.7);
                double z = i % 2 == 0 ? 1 + i * 41 : p.WorldLength - 1 - i * 41;
                // the band rules a v10 layout replaces: old layouts keep the flat seabed and the straight blend
                if (version < 10)
                    Check(p.SeamFloorY(x, z) == p.SeabedY && p.SeamEdge(x, z) == p.SeamHalfWidth - SeamParams.SeamBlend
                        && p.SeamWorkReach == p.SeamHalfWidth, "default band seabed and edge unchanged before v10");
                foreach (var (name, args) in new[] {
                    ("IsIceCovered", new object[] {x,z}), ("GetPolarColumn", new object[] {x,z}),
                    ("PlaneCap", new object[] {x,z}), ("PolarOwnsSeabed", new object[] {x,z}),
                    ("SheetHarshness", new object[] {x,z}), ("BarrenFactor", new object[] {z}),
                    ("WallFaceOffset", new object[] {x,170}), ("IsWallBlock", new object[] {x,170,z}),
                    ("SeamBlendWeight", new object[] {x}), ("OceanForce", new object[] {x,z}), ("InSeamBand", new object[] {x}),
                    ("SeamFloorY", new object[] {x,z}), ("SeamEdge", new object[] {x,z}), ("SeamGround", new object[] {x,z,(int)(z % 211)}),
                    ("WallShade", new object[] {x,170}), ("GlacierShade", new object[] {x,150,z}) })
                {
                    object a = type.GetMethod(name).Invoke(old, args), b = typeof(SeamParams).GetMethod(name).Invoke(p, args);
                    if (name == "GetPolarColumn")
                    {
                        foreach (var f in b.GetType().GetFields()) Check(Equals(a.GetType().GetField(f.Name).GetValue(a), f.GetValue(b)), "default polar column identical to baseline");
                    }
                    else Check(Equals(a,b), "default " + name + " identical to baseline");
                }
            }
        }
        Console.WriteLine("PASS default geometry exactly matches the pre-change DLL for layouts 4–10");
        context.Unload();
    }

    /// <summary>
    /// The worldgen's shortcuts (internal: x-only ring and sheet values per chunk x, the sheet coordinate handed on, the
    /// ramp from an edge it already has) give exactly what the public methods give, for every layout, on the polar
    /// zones, the band and its ramp strip.
    /// </summary>
    static void CheckWorldgenShortcuts()
    {
        const BindingFlags inner = BindingFlags.NonPublic | BindingFlags.Instance;
        var t = typeof(SeamParams);
        var ringXAt = t.GetMethod("RingXAt", inner);
        var columnAt = t.GetMethod("GetPolarColumnAt", inner);
        var capAt = t.GetMethod("PlaneCapAt", inner);
        var shadeAt = t.GetMethods(inner).First(m => m.Name == "GlacierShadeAt" && m.GetParameters()[0].ParameterType.IsByRef);
        var rampGround = t.GetMethod("SeamRampGround", inner);
        var random = new Random(1066);
        int compared = 0, ramps = 0;
        foreach (int version in new[] { 4, 5, 6, 7, 8, 9, 10 })
        {
            var p = Parameters(); p.Version = version;
            for (int i = 0; i < 3000; i++)
            {
                double x = Math.Floor(random.NextDouble() * p.WorldWidth) + 0.5;
                double reach = p.PolarZoneDepth + 64;
                double z = Math.Floor(random.NextDouble() * reach) + 0.5;
                if (i % 2 == 1) z = p.WorldLength - z;
                object ring = ringXAt.Invoke(p, new object[] { x });
                var args = new object[] { ring, z, null };
                var at = (SeamParams.PolarColumn)columnAt.Invoke(p, args);
                var plain = p.GetPolarColumn(x, z);
                Check(at.Floor == plain.Floor && at.ShelfBlend == plain.ShelfBlend && at.IceTop == plain.IceTop && at.IceBottom == plain.IceBottom
                    && at.Floe == plain.Floe, "worldgen polar column = GetPolarColumn");
                Check((int)capAt.Invoke(p, new object[] { ring, z, args[2] }) == p.PlaneCap(x, z), "worldgen planing cap = PlaneCap");
                int y = 100 + i % 120;
                Check((int)shadeAt.Invoke(p, new object[] { ring, y, z }) == p.GlacierShade(x, y, z), "worldgen glacier shade = GlacierShade");
                compared++;
                // the v10 ramp strip on both sides of the world, at any natural height
                if (version >= 10)
                {
                    double rx = p.SeamEdge(0, z) + random.NextDouble() * (p.SeamWorkReach - p.SeamEdge(0, z));
                    double bx = Math.Floor(i % 4 < 2 ? rx : p.WorldWidth - rx) + 0.5;
                    double edge = p.SeamEdge(bx, z);
                    if (p.DistToEdge(bx) < edge || p.DistToEdge(bx) >= p.SeamWorkReach) continue;
                    int natural = 40 + i % 300;
                    Check((int)rampGround.Invoke(p, new object[] { bx, natural, edge, p.SeamFloorY(bx, z) }) == p.SeamGround(bx, z, natural), "worldgen ramp = SeamGround");
                    ramps++;
                }
            }
        }
        Console.WriteLine($"PASS worldgen shortcuts equal the public geometry ({compared} polar columns over layouts 4-10, {ramps} ramp columns)");
    }

    /// <summary>A changed Earth refuses with one message naming every missing member, not a NullReferenceException.</summary>
    static void CheckEarthBinder()
    {
        try
        {
            new EarthPolarModel(new FakeEarth());
            Check(false, "an incompatible Earth is refused");
        }
        catch (InvalidOperationException e)
        {
            Check(e.Message.Contains("not compatible") && e.Message.Contains("BlocksPerDegreeZ (Double, now Single)") && e.Message.Contains("FakeEarth.Data")
                && e.Message.Contains("GetColumn"), "an incompatible Earth is refused with one message naming what is missing: " + e.Message);
            // members of a missing owner (GetColumn's column, Data's HasTerrain and SampleClimate) aren't listed again
            Check(!e.Message.Contains("?.") && !e.Message.Contains("HasTerrain") && !e.Message.Contains("SampleClimate") && !e.Message.Contains(".Top"),
                "the refusal lists only real member names, each once: " + e.Message);
        }
        // An Earth whose column has no WaterTop binds (only EarthSeamWater worlds need it), reads no water level, and has the
        // refusal ready for a world that keeps the band's lakes and rivers (SeamWorldGen refuses it; a new world takes the
        // flag only with it)
        var dry = new EarthPolarModel(new DryEarth());
        Check(!dry.HasWaterTop && dry.WaterTopMismatch?.Contains("not compatible") == true && dry.WaterTopMismatch.Contains("DryColumn.WaterTop (Int32)")
            && dry.Sample(0, 0).WaterTop == -1 && dry.Sample(0, 0).Top == 80, "an Earth without WaterTop binds without it and names it for EarthSeamWater worlds: " + dry.WaterTopMismatch);
    }

    class FakeGeometry { public double EquatorZ = 1000; public float BlocksPerDegreeZ = 10; }
    class FakeEarth { public FakeGeometry Geometry = new(); }
    class DryGeometry { public double EquatorZ = 1000, BlocksPerDegreeZ = 10; }
    public struct DryColumn { public int Top, IceBottom; public byte Water; public double Lat, Lon; }
    public struct DryClimate { public double TMean, TCold, TWarm; }
    class DryData { public bool HasTerrain => true; public DryClimate SampleClimate(double lat, double lon) => new DryClimate { TMean = -20 }; }
    class DryEarth
    {
        public DryGeometry Geometry = new();
        public DryData Data = new();
        public DryColumn GetColumn(double x, double z) => new DryColumn { Top = 80, IceBottom = 999, Water = 2, Lat = (1000 - z) / 10 };
    }

    /// <summary>
    /// Earth 2.0's real seam (decision 0030; GutterSeabed 3, no gutter): the band holds Earth's own terrain, so a new
    /// world takes EarthSeabed and EarthSeamLand, and everything the band keeps or rebuilds (land, ownership, the seabed
    /// it takes as its floor) must be the same at x and x + C across the identical part.
    /// </summary>
    static void CheckRealSeam(EarthData data)
    {
        var p = Parameters(); p.Version = 10;
        var geo = EarthLayout.Build(new LayoutInput {
            WorldWidth = p.WorldWidth, Gutter = false,
            CwRingStart = p.JumpInset, CwRingLength = p.Circumference, CwEquatorZ = p.EquatorZ,
            CwPoleDistance = p.PoleDistance, CwBandInner = p.SeamHalfWidth - p.JumpInset
        }, data, out _);
        var model = new TerrainModel(data, geo, VerticalScale.ForWorld(384, p.SeaLevel), p.NoiseSeed)
            { GutterSeabedY = p.SeabedY, GutterSeabed = TerrainModel.RealSeamVersion, GutterBridges = true };
        p.EarthPolar = new EarthPolarModel(model);
        p.EarthPolarIce = true;
        Check(p.EarthPolar.RealSeam && p.EarthPolar.RealGutterSeabed && p.EarthPolar.GutterLand, "real seam: no gutter, Earth's seabed and land repeat around the ring");
        p.EarthSeabed = p.EarthSeamLand = true;
        int columns = 0, land = 0, kept = 0, lakes = 0;
        var random = new Random(30);
        for (int i = 0; i < 6000; i++)
        {
            // the identical part on the west jump line (both sides of it) and its mirror one ring east
            int x = (int)(p.JumpInset - 64 + random.NextDouble() * (p.IdenticalReach + 64));
            int z = (int)(p.WallFaceMax + random.NextDouble() * (p.WorldLength - 2 * p.WallFaceMax));
            int mx = x + (int)p.Circumference;
            var a = p.EarthPolar.Sample(x, z);
            var b = p.EarthPolar.Sample(mx, z);
            Check(a.Top == b.Top && a.IceBottom == b.IceBottom && a.Water == b.Water && a.WaterTop == b.WaterTop && a.Latitude == b.Latitude
                && a.WarmestMonth == b.WarmestMonth, "real seam: Earth's column (its water level too) repeats one ring apart in the identical band");
            Check(p.EarthSeamLandColumn(a) == p.EarthSeamLandColumn(b) && p.EarthKeepsSeamColumn(x + 0.5, z + 0.5) == p.EarthKeepsSeamColumn(mx + 0.5, z + 0.5)
                && p.PolarOwnsSeabed(x + 0.5, z + 0.5) == p.PolarOwnsSeabed(mx + 0.5, z + 0.5), "real seam: the band keeps the same columns at x and x + C");
            columns++;
            if (!a.Ocean) land++;
            if (p.EarthKeepsSeamColumn(x + 0.5, z + 0.5)) kept++;
            if (a.Water == 2 || a.Water == 3) lakes++;
        }
        Check(land > 0 && kept >= land, "real seam: the identical band holds Earth's land, and keeps it");
        Console.WriteLine($"PASS real seam (Earth 2.0, no gutter): {columns} identical-band columns match one ring apart; {land} land, {kept} kept, {lakes} lake or river");
    }

    /// <summary>v10 band rules on the Earth-sized parameters: the seabed matches across the jump and stays under water,
    /// the wavy edge never comes into view of the jump line, and the ramp is at most 1 : 4.</summary>
    static void CheckNaturalBand()
    {
        var p = Parameters(); p.Version = 10;
        int lo = int.MaxValue, hi = int.MinValue;
        double edgeLo = double.MaxValue, edgeHi = double.MinValue;
        for (int i = 0; i < 4000; i++)
        {
            double x = p.JumpInset - 700 + i * 0.37, z = 7 + i * 25.3;
            int y = p.SeamFloorY(x, z);
            lo = Math.Min(lo, y); hi = Math.Max(hi, y);
            Check(y == p.SeamFloorY(x + p.Circumference, z), "v10 band seabed identical at x and x + C");
            Check(y <= p.SeaLevel - 3, "v10 band seabed stays under water");
            foreach (double side in new[] { x, p.WorldWidth - x })
            {
                double edge = p.SeamEdge(side, z);
                edgeLo = Math.Min(edgeLo, edge); edgeHi = Math.Max(edgeHi, edge);
                Check(edge - p.JumpInset >= p.IdenticalReach && edge <= p.SeamHalfWidth - SeamParams.SeamBlend, "v10 band edge within its bounds");
            }
        }
        double steepest = 0;
        for (int delta = 0; delta <= (int)(p.RampMax / 6); delta += 7)
            for (double u = -2; u < p.RampMax + 2; u++)
            {
                double x = p.SeamEdge(p.JumpInset, 5000) + u;
                steepest = Math.Max(steepest, delta * Math.Abs(p.SeamRampWeight(x + 1, 5000, delta, p.RampMax) - p.SeamRampWeight(x, 5000, delta, p.RampMax)));
            }
        Check(steepest <= 0.25 + 1e-9, "v10 ramp at most 1 : 4");
        Check(hi - lo >= 10 && edgeHi - edgeLo >= 32, "v10 band seabed has relief and its edge is wavy");
        Console.WriteLine($"PASS v10 band: seabed {lo}..{hi} (flat {p.SeabedY}), edge {edgeLo:0}..{edgeHi:0} from the world edge, steepest ramp {steepest:0.000}");
    }

    /// <summary>The live temperature correction lands on the true base temperature on both sides, whatever vanilla's
    /// own clamps make of the stored map value (the server clamps at -20, the client at -50).</summary>
    static void CheckTemperature()
    {
        var p = Parameters();
        double worst = 0;
        foreach (double z in new[] { p.EquatorZ, p.EquatorZ - 0.5 * p.PoleDistance, p.WallDepth + 2000, p.WallDepth + 100, p.WorldLength - p.WallDepth - 40 })
            foreach (int dy in new[] { 0, -64, 70, 118, -150 })
                foreach (bool client in new[] { false, true })
                {
                    int y = p.SeaLevel + dy;
                    int map = Climate.DescaleTemperature(p.TemperatureAt(z));
                    float vanilla = client ? Climate.GetScaledAdjustedTemperatureFloatClient(map, dy) : Climate.GetScaledAdjustedTemperatureFloat(map, dy);
                    double live = vanilla + PolarTemperatureSystem.Correction(p, p.WorldWidth / 2.0, y, z, client);
                    double error = Math.Abs(live - p.TrueBaseTemperature(p.WorldWidth / 2.0, z, y));
                    worst = Math.Max(worst, error);
                    // the map stores whole steps of 1 / 4.25 °C
                    Check(error < 0.25, $"live temperature = true base ({(client ? "client" : "server")}, z {z:0}, {dy:+0;-0} blocks)");
                }
        Console.WriteLine($"PASS live temperature correction on server and client, worst {worst:0.00} °C off the true base");
    }

    static void Render(TerrainModel model, SeamParams p, string output)
    {
        const int w = 1440, h = 120;
        foreach (bool north in new[] { true, false })
        {
            var pixels = new byte[w*h*3];
            for (int row = 0; row < h; row++)
            {
                double lat = north ? 90 - (row + 0.5) * 30 / h : -60 - (row + 0.5) * 30 / h;
                double z = model.Geometry.EquatorZ - lat * model.Geometry.BlocksPerDegreeZ;
                for (int col = 0; col < w; col++)
                {
                    double x = p.JumpInset + (col + 0.5) * p.Circumference / w;
                    var c = model.GetColumn(x,z);
                    var s = p.EarthPolar.Sample(x,z);
                    MapColors.Column(c, p.SeaLevel, 384, out byte r, out byte g, out byte b);
                    if (p.HasArcticIce(x,z,s)) (r,g,b) = (200,226,239);
                    if (p.DistToWallFace(z) <= EarthPolarModel.LifelessBorder) (r,g,b) = (153,178,217);
                    int i = (row*w+col)*3;
                    pixels[i]=r; pixels[i+1]=g; pixels[i+2]=b;
                }
            }
            Png.WriteRgb(Path.Combine(output,north ? "earth-north.png" : "earth-south.png"),w,h,pixels);
        }
    }

    static void RenderArcticDetail(SeamParams p,string output)
    {
        const int w=512,h=512;
        var pixels=new byte[w*h*3];
        var sample=new EarthPolarModel.Column { Water=1,Latitude=89 };
        for(int row=0;row<h;row++) for(int col=0;col<w;col++)
        {
            double x=p.WorldWidth/2.0+col,z=p.WallDepth+200+row;
            int rise=p.EarthIceRise(x,z,sample), snow=p.EarthSnowEighths(x,z);
            double slope=rise+snow/8.0-p.EarthIceRise(x-1,z-1,sample)-p.EarthSnowEighths(x-1,z-1)/8.0;
            double shade=Math.Clamp(0.84+slope*0.12,0.35,1.05);
            var color=snow>0 ? (r:240,g:246,b:250) : (r:135,g:185,b:209);
            int i=(row*w+col)*3;
            pixels[i]=(byte)Math.Clamp(color.r*shade,0,255);
            pixels[i+1]=(byte)Math.Clamp(color.g*shade,0,255);
            pixels[i+2]=(byte)Math.Clamp(color.b*shade,0,255);
        }
        Png.WriteRgb(Path.Combine(output,"arctic-ridges-snow.png"),w,h,pixels);
    }

    static void RenderSeasonal(TerrainModel model,SeamParams p,string output)
    {
        const int w=1440,h=140;
        foreach(bool north in new[]{true,false}) foreach(bool summer in new[]{true,false})
        {
            var pixels=new byte[w*h*3];
            double year=(summer?SeaIcePolicy.NorthMinimum:SeaIcePolicy.NorthMaximum)+(north?0:.5);
            int frozen=0,permanent=0,ocean=0;
            for(int row=0;row<h;row++) for(int col=0;col<w;col++)
            {
                double lat=north?90-(row+.5)*35/h:-55-(row+.5)*35/h;
                double z=model.Geometry.EquatorZ-lat*model.Geometry.BlocksPerDegreeZ;
                double x=p.JumpInset+(col+.5)*p.Circumference/w;
                var c=model.GetColumn(x,z);var s=p.EarthPolar.Sample(x,z);
                MapColors.Column(c,p.SeaLevel,384,out byte r,out byte g,out byte b);
                var kind=SeaIcePolicy.At(p,x,z,s,year);
                if(s.Ocean)ocean++;
                if(kind==SeaIceKind.Seasonal){(r,g,b)=(197,224,242);frozen++;}
                if(kind==SeaIceKind.Permanent){(r,g,b)=(246,248,251);permanent++;}
                if(p.DistToWallFace(z)<=EarthPolarModel.LifelessBorder)(r,g,b)=(153,178,217);
                int i=(row*w+col)*3;pixels[i]=r;pixels[i+1]=g;pixels[i+2]=b;
            }
            string name=$"seasonal-{(north?"north":"south")}-{(summer?"summer":"winter")}";
            Png.WriteRgb(Path.Combine(output,name+".png"),w,h,pixels);
            Console.WriteLine($"{name}: ocean pixels={ocean}, seasonal ice={frozen}, permanent ice={permanent}");
        }
    }

    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL " + message);
        checks++;
    }
}
