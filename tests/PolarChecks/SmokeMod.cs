using System;
using System.Linq;
using System.Reflection;
using CylinderWorld;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

// Test-only mod, compiled and installed solely in a temporary server data directory by smoke.ps1.
public class PolarSmoke : ModSystem
{
    ICoreServerAPI api;
    SeamParams p;
    (string name, int x, int z)[] places;
    int index;
    int footRetries;
    bool finished;
    bool liveStarted;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 1;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Start(), 1000));
        // Earth 2.0 builds each new chunk from much more data than Earth Map did
        api.Event.RegisterCallback(_ => { if (!finished) End("FAIL timeout"); }, 900000);
    }
    void Start()
    {
        try
        {
            p = api.ModLoader.GetModSystem<CylinderWorldCore>().Params;
            if (p?.EarthPolarIce != true || p.EarthPolar == null) throw new Exception("Earth mode not bound or not retained on reload");
            if(!p.SeasonalSeaIce) throw new Exception("Seasonal sea ice not enabled");
            api.World.Calendar.SetSeasonOverride((float)SeaIcePolicy.NorthMaximum);
            api.ModLoader.GetModSystem<SeaIceSystem>().PublishDate();
            var earth = api.ModLoader.Systems.First(s => s.GetType().FullName == "EarthMap.EarthMapSystem");
            var model = earth.GetType().GetProperty("Model").GetValue(earth);
            var geo = model.GetType().GetField("Geometry").GetValue(model);
            (string,int,int) Geo(string name, double lat, double lon)
            {
                object[] args = { lat,lon,0d,0d };
                geo.GetType().GetMethod("FromGeo").Invoke(geo,args);
                return (name,(int)(double)args[2],(int)(double)args[3]);
            }
            places = new[] {
                ("north-border",p.WorldWidth/2,p.WallDepth+500),
                Geo("north-floes",80,-135), Geo("greenland",75,-45),
                Geo("antarctica",85*-1,20), Geo("peninsula",-70,-65), Geo("coast",-70,20),
                ("south-wall",p.WorldWidth/2,p.WorldLength-10),
                Geo("seasonal-canada",71,-135), Geo("seasonal-siberia",74,140), Geo("seasonal-antarctic",-65,20)
            };
            var coast = places.Single(t => t.name == "coast");
            places = places.Concat(Enumerable.Range(-2,5).SelectMany(dz => Enumerable.Range(-2,5)
                .Where(dx => dx != 0 || dz != 0).Select(dx => ("coast",coast.x+dx*32,coast.z+dz*32)))).ToArray();
            var drift = (from dx in Enumerable.Range(0,16) from dz in Enumerable.Range(0,32)
                let x=p.WorldWidth/2+dx*16 let z=p.WallDepth+200+dz*16
                where p.EarthSnowEighths(x+0.5,z+0.5)>=8
                select ("north-drift",x,z)).First();
            places=places.Append(drift).ToArray();
            // CW-D26: the band at the jump line, inside the ring and one ring further (the hidden side), in the Bering Sea
            int bandZ=(int)(p.EquatorZ-56*(1/p.EarthPolar.Latitude(p.EquatorZ-1)));
            // New checks go last, so the places before them generate in the same order as before (neighbour-dependent features)
            places=places.Append(("wall-foot",p.WorldWidth/2+64,p.WallDepth)).Append(("wall-foot",p.WorldWidth/2+64,p.WallFaceMax))
                .Append(("seam-band",p.JumpInset+40,bandZ)).Append(("seam-mirror",(int)(p.JumpInset+40+p.Circumference),bandZ))
                .Append(("seam-wall",p.JumpInset+40,p.WallFaceMax+40)).Append(("seam-wall-mirror",(int)(p.JumpInset+40+p.Circumference),p.WallFaceMax+40)).ToArray();
            // CW-D31: St. Lawrence Island where the seam meridian crosses it (63.2-63.3°N), on both sides of the jump
            if (p.EarthSeamLand)
            {
                var islandZ = Enumerable.Range((int)(p.EquatorZ-63.6/p.EarthPolar.Latitude(p.EquatorZ-1)), (int)(0.7/p.EarthPolar.Latitude(p.EquatorZ-1)))
                    .Where(z => !p.EarthPolar.Sample(p.JumpInset+40,z).Ocean).ToArray();
                if (islandZ.Length == 0) throw new Exception("no land at the jump line near St. Lawrence Island");
                int iz = islandZ[islandZ.Length/2];
                places=places.Append(("seam-land",p.JumpInset+40,iz)).Append(("seam-land-mirror",(int)(p.JumpInset+40+p.Circumference),iz)).ToArray();
            }
            var manager = api.World.GetType().GetField("ModEventManager")?.GetValue(api.World)
                ?? throw new Exception("Could not access the game's mod spawn events for verification");
            var bear = api.World.GetEntityType(new AssetLocation("game:bear-polar-adult-male"))
                ?? throw new Exception("Polar bear entity type not found");
            var hare = api.World.GetEntityType(new AssetLocation("game:hare-arctic-adult-male"))
                ?? throw new Exception("Arctic hare entity type not found");
            if (!EarthPolarSystem.IsVanillaPolarPredator(bear) || EarthPolarSystem.IsVanillaPolarPredator(hare)
                || EarthPolarSystem.IsVanillaPolarPredator(api.World.GetEntityType(new AssetLocation("game:bear-brown-adult-male"))))
                throw new Exception("Polar predator rule should select the polar bear only, not the arctic hare or brown bear");
            foreach (var place in places.Take(5))
            {
                // Antarctica vetoes the polar bear but not the hare; the lifeless border vetoes both.
                bool hareExpected = !p.EarthPolar.ForbidNaturalSpawn(p, place.z);
                bool hareAllowed = (bool)typeof(EarthPolarSystem).GetMethod("AllowSpawn",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(api.ModLoader.GetModSystem<EarthPolarSystem>(), new object[] { hare, (double)place.z });
                if (hareAllowed != hareExpected) throw new Exception("Spawn guard got the arctic hare wrong at " + place.name);
                bool expected = !EarthPolarSystem.ForbidsSpawn(p, bear, place.z);
                bool vetoed = place.name is "antarctica" or "peninsula" or "north-border";
                if (vetoed == expected) throw new Exception("Polar bear veto missing or misplaced at " + place.name);
                object[] entityArgs = { api.World.BlockAccessor, bear, new Vec3d(place.x,p.SeaLevel,place.z), 0L };
                bool entityAllowed = (bool)manager.GetType().GetMethod("TriggerTrySpawnEntity").Invoke(manager,entityArgs);
                bool groupAllowed = (bool)manager.GetType().GetMethod("TriggerTrySpawnGroupNearOffthread").Invoke(manager,
                    new object[] { api.World.BlockAccessor,bear,new BlockPos(place.x,p.SeaLevel,place.z) });
                // Vanilla/other mods may also deny an otherwise allowed spawn (these chunks are not loaded yet).
                // Check our registered delegates in isolation, then verify the combined event honors our veto.
                foreach (string name in new[] { "OnTrySpawnEntity", "OnTrySpawnGroupNearOffthread" })
                {
                    var hooks = (Delegate)manager.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(manager);
                    var hook = hooks.GetInvocationList().Single(h=>h.Target is EarthPolarSystem);
                    var hookArgs = name == "OnTrySpawnEntity" ? entityArgs : new object[] { api.World.BlockAccessor,bear,new BlockPos(place.x,p.SeaLevel,place.z) };
                    if ((bool)hook.DynamicInvoke(hookArgs) != expected) throw new Exception("Registered spawn guard failed at " + place.name);
                }
                if (!expected && (entityAllowed || groupAllowed)) throw new Exception("Combined spawn events ignored the polar veto at " + place.name);
                Mod.Logger.Notification("POLARSMOKE PASS registered spawn guards {0}: allowed={1}, combined entity={2}, group={3}",place.name,expected,entityAllowed,groupAllowed);
            }
            Mod.Logger.Notification("POLARSMOKE bound; saved Earth mode={0}; wall={1}; fresh={2}", p.EarthPolarIce,p.WallTopY,api.WorldManager.SaveGame.IsNew);
            // CW-D41: Earth's "still creating" key is cleared once Earth has stored its params, and both mods take the world
            // for new only on its creating start
            var save = api.WorldManager.SaveGame;
            if (save.GetData("earth-creating") is { Length: > 0 }) throw new Exception("Earth's earth-creating key is still set at RunGame");
            if (api.ModLoader.GetModSystem<CylinderWorldCore>().IsNewWorld != save.IsNew) throw new Exception("Cylinder World's IsNewWorld differs from the save's IsNew (" + save.IsNew + ")");
            CheckFeatureGuards();
            Next();
        }
        catch (Exception e) { End("FAIL " + e); }
    }
    void Next()
    {
        if (finished) return;
        if (index == places.Length) { BeginLiveCheck(); return; }
        var place = places[index];
        api.WorldManager.LoadChunkColumnPriority(place.x/32,place.z/32,new ChunkLoadOptions {
            KeepLoaded = true,
            OnLoaded = () => api.Event.EnqueueMainThreadTask(Analyze,"polar-smoke")
        });
    }
    void Analyze()
    {
        try
        {
            var place = places[index];
            var sample = p.EarthPolar.Sample(place.x+0.5,place.z+0.5);
            if(!api.WorldManager.SaveGame.IsNew && (place.name.StartsWith("seasonal-") || place.name=="north-floes"))
            {
                var fluid=api.World.BlockAccessor.GetBlock(new BlockPos(place.x,p.SeaLevel-1,place.z),BlockLayersAccess.Fluid).Code.Path;
                string expected=p.SeaIceAt(place.x+.5,place.z+.5,sample) switch {
                    SeaIceKind.Open=>"saltwater-still-7",SeaIceKind.Seasonal=>"seaice-seasonal",_=>"seaice-permanent"
                };
                if(fluid!=expected && (fluid=="seaice-seasonal" || fluid=="saltwater-still-7"))
                {
                    // Saved chunks can be from a different date; they must catch up through the real scheduler.
                    api.Event.RegisterCallback(_=>Analyze(),200);
                    return;
                }
            }
            Block At(int y) => api.World.BlockAccessor.GetBlock(new BlockPos(place.x,y,place.z));
            int top = api.World.BlockAccessor.GetRainMapHeightAt(place.x,place.z);
            string surface = At(top).Code.ToString();
            Mod.Logger.Notification("POLARSMOKE inspect {0}: pos={1},{2} surface={3}@{4}, sea={5}, sea blocks={6}/{7}/{8}, sample=lat {9}, water {10}, ice {11}, wants ice={12}",
                place.name,place.x,place.z,surface,top,p.SeaLevel,At(p.SeaLevel-2).Code,At(p.SeaLevel-1).Code,At(p.SeaLevel).Code,
                sample.Latitude,sample.Water,sample.IceBottom,p.HasArcticIce(place.x+0.5,place.z+0.5,sample));
            var nearby = Enumerable.Range(Math.Max(1,sample.Top-5),Math.Min(12,api.WorldManager.MapSizeY-Math.Max(1,sample.Top-5))).Select(y=>At(y).Code.ToString()).ToArray();
            switch (place.name)
            {
                case "north-border":
                    if (At(p.SeaLevel-1).Code.Path != "seaice-permanent") throw new Exception("missing permanent inner sea ice");
                    if (!p.EarthPolar.ForbidNaturalSpawn(p,place.z)) throw new Exception("north spawn guard inactive");
                    break;
                case "north-floes":
                    if (!At(p.SeaLevel-1).Code.Path.StartsWith("seaice-")) throw new Exception("missing outer sea ice");
                    break;
                case "greenland":
                    if (!nearby.Any(s=>s=="game:glacierice") || Math.Abs(top-sample.Top)>5) throw new Exception("Greenland terrain changed");
                    break;
                case "antarctica":
                case "peninsula":
                    if (sample.Glacier && !nearby.Any(s=>s.StartsWith("cylinderworld:glacier-"))) throw new Exception("Antarctic glacier surface not converted");
                    if (Math.Abs(top-sample.Top)>5) throw new Exception("Antarctic elevation changed");
                    if (!p.EarthPolar.ForbidPolarPredators(p,place.z) || !p.EarthPolar.ForbidFeatures(p,place.z)) throw new Exception("Antarctic predator/feature guard inactive");
                    break;
                case "south-wall":
                    if (!At(p.WallTopY).Code.Path.StartsWith("wallice-")) throw new Exception("wall missing");
                    break;
                case "wall-foot":
                    // The wall face bulges 0..WallFaceAmplitude blocks past WallDepth. In front of it the Arctic sea is
                    // frozen right up to the face, including under overhangs: no open water between wall and ice.
                    int open=0, foot=0, bulge=0, lost=0;
                    int z0=place.z/32*32;
                    for (int dx=0;dx<32;dx++) for (int z=Math.Max(z0,p.WallDepth);z<Math.Min(z0+32,p.WallFaceMax+4);z++)
                    {
                        var pos=new BlockPos(place.x/32*32+dx,p.SeaLevel-1,z);
                        // Where the face bulges out at the waterline the wall stays wall; the sea freezes only outside it
                        for (int y=p.SeaLevel-2;y<=p.SeaLevel-1;y++)
                        {
                            if(!p.IsWallBlock(pos.X+0.5,y,z+0.5)) continue;
                            bulge++;
                            var at=api.World.BlockAccessor.GetBlock(new BlockPos(pos.X,y,z),BlockLayersAccess.Solid);
                            if(at.Code==null || !at.Code.Path.StartsWith("wallice-"))
                            {
                                lost++;
                                if(lost<=5) Mod.Logger.Notification("POLARSMOKE wall block replaced at the waterline: {0},{1},{2} = {3}",pos.X,y,z,at.Code);
                            }
                        }
                        var solid=api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Solid);
                        if(solid.Id!=0) continue;
                        foot++;
                        var fluid=api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Fluid).Code.Path;
                        if(!fluid.StartsWith("seaice-"))
                        {
                            open++;
                            if(open<=5) Mod.Logger.Notification("POLARSMOKE open water at the wall foot: {0},{1} fluid={2}",pos.X,z,fluid);
                        }
                    }
                    // 0.11.11: pressure ridges fade in beyond the foot instead of starting in a straight cliff at WallFaceMax
                    int ridgeCells=0, ridgeMax=0;
                    for (int dx=0;dx<32;dx++) for (int z=Math.Max(z0,p.WallFaceMax);z<Math.Min(z0+32,p.WallFaceMax+8);z++)
                    {
                        int x=place.x/32*32+dx, h=0;
                        for (int y=p.SeaLevel;y<=p.SeaLevel+8;y++)
                        {
                            var code=api.World.BlockAccessor.GetBlock(new BlockPos(x,y,z),BlockLayersAccess.Solid).Code;
                            if(code!=null && code.Domain=="cylinderworld" && !code.Path.StartsWith("wallice-")) h=y-p.SeaLevel+1;
                        }
                        ridgeCells++;
                        ridgeMax=Math.Max(ridgeMax,h);
                    }
                    Mod.Logger.Notification("POLARSMOKE wall foot: ridges within 8 blocks of the foot at most {0} high ({1} columns)",ridgeMax,ridgeCells);
                    if(ridgeMax>1) throw new Exception("pressure ridges start in a cliff at the wall's foot");
                    Mod.Logger.Notification("POLARSMOKE wall foot: {0} wall cells at the waterline, {1} replaced",bulge,lost);
                    if(lost!=0 && api.WorldManager.SaveGame.IsNew) throw new Exception("sea ice replaced wall blocks at the wall foot");
                    if(open!=0 && !api.WorldManager.SaveGame.IsNew && footRetries++<300)
                    {
                        // Chunks generated before 0.11.6 have the open strip; the live sea-ice system closes it after loading.
                        api.Event.RegisterCallback(_=>Analyze(),200);
                        return;
                    }
                    Mod.Logger.Notification("POLARSMOKE wall foot: {0} sea-level cells outside the face, {1} open",foot,open);
                    if(foot==0 || open!=0) throw new Exception("open water between the north wall and its sea ice");
                    break;
                case "seam-mirror":
                {
                    // Earth's real gutter seabed (Earth's TerrainModel.GutterSeabed) is kept as the band's floor, and the two
                    // sides of the jump match. With an Earth Map that has no real gutter seabed the band keeps the v10 relief.
                    var earthType=api.ModLoader.Systems.First(t=>t.GetType().FullName=="EarthMap.EarthMapSystem");
                    var earthModel=earthType.GetType().GetProperty("Model").GetValue(earthType);
                    bool real=earthModel.GetType().GetField("GutterSeabed")?.GetValue(earthModel) is int v && v>=1;
                    if(p.EarthSeabed!=real) throw new Exception($"band seabed mode {p.EarthSeabed}, Earth real gutter seabed {real}");
                    int diff=0, missEarth=0, lo=int.MaxValue, hi=int.MinValue;
                    int Floor(int x,int z) { int y=p.SeaLevel; while(y>1 && api.World.BlockAccessor.GetBlock(new BlockPos(x,y,z),BlockLayersAccess.Solid).Id==0) y--; return y; }
                    int mx0=place.x/32*32, bx0=(int)(mx0-p.Circumference), bz0=place.z/32*32;
                    for(int dz=0;dz<32;dz++) for(int dx=0;dx<32;dx++)
                    {
                        int a=Floor(bx0+dx,bz0+dz), b=Floor(mx0+dx,bz0+dz);
                        int e=Math.Clamp(p.EarthPolar.Sample(bx0+dx,bz0+dz).Top,2,p.SeaLevel-2);
                        bool off=p.EarthSeabed && a!=e;
                        if(a!=b) diff++;
                        lo=Math.Min(lo,a); hi=Math.Max(hi,a);
                        if(off) missEarth++;
                        // the first few bad columns with their top blocks (0.11.7: neighbours' kelp and seaweed on one side)
                        if((a!=b || off) && diff+missEarth<=12)
                            Mod.Logger.Notification("POLARSMOKE band column {0},{1}: floor {2} ({3}), mirror {4} ({5}), Earth {6}",bx0+dx,bz0+dz,
                                a,api.World.BlockAccessor.GetBlock(new BlockPos(bx0+dx,a,bz0+dz),BlockLayersAccess.Solid).Code,
                                b,api.World.BlockAccessor.GetBlock(new BlockPos(mx0+dx,b,bz0+dz),BlockLayersAccess.Solid).Code,e);
                    }
                    Mod.Logger.Notification("POLARSMOKE seam band at lat {0:0.0}: Earth seabed={1}, floor {2}..{3}, mirrored columns differing {4}/1024, off Earth's seabed {5}",
                        p.EarthPolar.Latitude(place.z),p.EarthSeabed,lo,hi,diff,missEarth);
                    if(diff!=0 || missEarth!=0) throw new Exception("seam band seabed check failed");
                    break;
                }
                case "seam-land-mirror":
                {
                    // CW-D31: Earth's land in the band is kept at Earth's height and rebuilt the same on both sides of the jump
                    var earthType=api.ModLoader.Systems.First(t=>t.GetType().FullName=="EarthMap.EarthMapSystem");
                    var earthModel=earthType.GetType().GetProperty("Model").GetValue(earthType);
                    bool carried=earthModel.GetType().GetField("GutterSeabed")?.GetValue(earthModel) is int v && v>=2;
                    if(p.EarthSeamLand!=carried) throw new Exception($"band land mode {p.EarthSeamLand}, Earth carries land {carried}");
                    int land=0, landDiff=0, offEarth=0, plants=0;
                    int mx0=place.x/32*32, bx0=(int)(mx0-p.Circumference), bz0=place.z/32*32;
                    string Cell(int x,int y,int z) { var pos=new BlockPos(x,y,z);
                        return api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Solid).Code+"/"+api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Fluid).Code; }
                    for(int dz=0;dz<32;dz++) for(int dx=0;dx<32;dx++)
                    {
                        int x=bx0+dx, z=bz0+dz;
                        if(p.DistToJumpLine(x+.5)>=p.IdenticalReach) continue;
                        var e=p.EarthPolar.Sample(x,z);
                        if(e.Ocean) continue;
                        land++;
                        int landTop=e.Top;
                        if(api.World.BlockAccessor.GetBlock(new BlockPos(x,landTop,z),BlockLayersAccess.Solid).Id==0
                            || api.World.BlockAccessor.GetBlock(new BlockPos(x,landTop+2,z),BlockLayersAccess.Solid).Id!=0) offEarth++;
                        if(api.World.BlockAccessor.GetBlock(new BlockPos(x,landTop+1,z),BlockLayersAccess.Solid).Code.Path.StartsWith("tallgrass")) plants++;
                        for(int y=landTop-8;y<=landTop+3;y++)
                        {
                            string a=Cell(x,y,z), b=Cell(mx0+dx,y,z);
                            if(a==b) continue;
                            landDiff++;
                            if(landDiff<=8) Mod.Logger.Notification("POLARSMOKE band land differs at {0},{1},{2}: {3} vs {4}",x,y,z,a,b);
                        }
                    }
                    Mod.Logger.Notification("POLARSMOKE seam band land at lat {0:0.00}: {1} land columns, {2} cells differing across the jump, {3} off Earth's height, {4} with tall grass",
                        p.EarthPolar.Latitude(place.z),land,landDiff,offEarth,plants);
                    if(land==0 || landDiff!=0 || offEarth!=0) throw new Exception("seam band land check failed");
                    break;
                }
                case "seam-wall-mirror":
                {
                    // 0.11.9: the band's late spill pass reset its sea at the north wall to open water, which the live
                    // sea-ice system then refroze row by row. The band's sea ice and its ridges are generated, on both sides.
                    int bandOpen=0, wallDiff=0, bandFrozen=0;
                    int mx0=place.x/32*32, bx0=(int)(mx0-p.Circumference), bz0=place.z/32*32;
                    string Cell(int x,int y,int z) { var pos=new BlockPos(x,y,z);
                        return api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Solid).Code+"/"+api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Fluid).Code; }
                    for(int dz=0;dz<32;dz++) for(int dx=0;dx<32;dx++)
                    {
                        int x=bx0+dx, z=bz0+dz;
                        foreach(int sx in new[] { x, mx0+dx })
                        {
                            var pos=new BlockPos(sx,p.SeaLevel-1,z);
                            if(api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Solid).Id!=0) continue;
                            if(p.SeaIceAt(sx+.5,z+.5,p.EarthPolar.Sample(sx,z))==SeaIceKind.Open) continue;
                            if(api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Fluid).Code.Path.StartsWith("seaice-")) { bandFrozen++; continue; }
                            bandOpen++;
                            if(bandOpen<=5) Mod.Logger.Notification("POLARSMOKE open water in the band at the north wall: {0},{1}",sx,z);
                        }
                        if(p.DistToJumpLine(x+.5)>=p.IdenticalReach) continue;
                        for(int y=p.SeaLevel-2;y<=p.SeaLevel+8;y++)
                        {
                            string a=Cell(x,y,z), b=Cell(mx0+dx,y,z);
                            if(a==b) continue;
                            wallDiff++;
                            if(wallDiff<=5) Mod.Logger.Notification("POLARSMOKE band at the north wall differs at {0},{1},{2}: {3} vs {4}",x,y,z,a,b);
                        }
                    }
                    if(bandOpen!=0 && !api.WorldManager.SaveGame.IsNew && footRetries++<300)
                    {
                        // Chunks generated with 0.11.8-0.11.9 have the open band; the live sea-ice system closes it after loading
                        api.Event.RegisterCallback(_=>Analyze(),200);
                        return;
                    }
                    Mod.Logger.Notification("POLARSMOKE band at the north wall: {0} frozen sea cells, {1} open, {2} cells differing across the jump",bandFrozen,bandOpen,wallDiff);
                    if(bandFrozen==0 || bandOpen!=0 || wallDiff!=0 && api.WorldManager.SaveGame.IsNew) throw new Exception("seam band at the north wall: open water or sides differ");
                    break;
                }
                case "coast":
                    int bad=0, maxDelta=0, glacier=0, transparent=0, boiling=0;
                    for (int dz=0;dz<32;dz++) for (int dx=0;dx<32;dx++)
                    {
                        int x=place.x/32*32+dx,z=place.z/32*32+dz;
                        var original=p.EarthPolar.Sample(x,z);
                        int actual=api.World.BlockAccessor.GetRainMapHeightAt(x,z);
                        // Live rivulets/waterfalls can flow above the sea after neighbouring chunks load.
                        // Check the solid terrain, not the top of that flowing water column.
                        while(actual>0 && api.World.BlockAccessor.GetBlock(new BlockPos(x,actual,z),BlockLayersAccess.Solid).Id==0) actual--;
                        actual=Math.Max(p.SeaLevel-1,actual);
                        int delta=actual-Math.Max(p.SeaLevel-1,original.Top);
                        maxDelta=Math.Max(maxDelta,Math.Abs(delta));
                        if(Math.Abs(delta)>5)
                        {
                            bad++;
                            Mod.Logger.Notification("POLARSMOKE coast delta at {0},{1}: expected={2}, rain={3}, surface={4}, seaSolid={5}, seaFluid={6}",x,z,
                                Math.Max(p.SeaLevel-1,original.Top),actual,api.World.BlockAccessor.GetBlock(new BlockPos(x,actual,z)).Code,
                                api.World.BlockAccessor.GetBlock(new BlockPos(x,p.SeaLevel-1,z),BlockLayersAccess.Solid).Code,
                                api.World.BlockAccessor.GetBlock(new BlockPos(x,p.SeaLevel-1,z),BlockLayersAccess.Fluid).Code);
                        }
                        if(original.Glacier) glacier++;
                        for(int y=Math.Max(1,original.IceBottom);y<=original.Top;y++)
                            if(api.World.BlockAccessor.GetBlock(new BlockPos(x,y,z)).Code.Path=="glacierice") transparent++;
                        for(int y=Math.Max(1,actual-4);y<=actual;y++)
                            if(api.World.BlockAccessor.GetBlock(new BlockPos(x,y,z),BlockLayersAccess.Fluid).Code.Path.StartsWith("boilingwater")) boiling++;
                    }
                    Mod.Logger.Notification("POLARSMOKE coast column {0},{1}: glacier={2}, unexpected={3}, maxDelta={4}, transparent={5}, boiling={6}",place.x/32,place.z/32,glacier,bad,maxDelta,transparent,boiling);
                    if(bad!=0 || transparent!=0 || boiling!=0) throw new Exception("Antarctic coast verification failed");
                    break;
            }
            if(place.name.StartsWith("seasonal-")) CheckSeaIce(place.x,place.z,sample);
            // Natural snow is a generation-only detail. An outer floe that thawed before saving refreezes
            // as bare ice, then receives normal weather snow; permanent Wall ridges retain their snow.
            if(place.name.StartsWith("north-") && (api.WorldManager.SaveGame.IsNew || place.name!="north-floes"))
                CheckNorthChunk(place.x/32*32,place.z/32*32);
            Mod.Logger.Notification("POLARSMOKE PASS {0}: surface {1} y={2}, Earth top={3}, iceBottom={4}",place.name,surface,top,sample.Top,sample.IceBottom);
            index++;
            api.Event.RegisterCallback(_=>Next(),50);
        }
        catch (Exception e) { End("FAIL " + e); }
    }
    void CheckFeatureGuards()
    {
        var spring=AccessTools.Method(typeof(GenHotSprings),"genHotspring");
        var trader=AccessTools.Method(typeof(WorldGenStructure),"TryGenerate");
        foreach(var method in new[]{spring,trader})
            if(Harmony.GetPatchInfo(method)?.Owners.Contains("cylinderworld.earthpolar")!=true)
                throw new Exception("Missing worldgen guard: "+method.Name);
        // Hot springs: blocked in the lifeless kilometre at either Wall; elsewhere in Antarctica they are Earth's (it marks
        // only real geothermal sites), so the guard lets them through (CW-D46)
        var springGuard=typeof(EarthPolarSystem).GetMethod("HotSpringGuard",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(int z in new[]{p.WallDepth+500,p.WorldLength-p.WallDepth-500})
            if((bool)springGuard.Invoke(null,new object[]{z,new Vec3i(0,0,0),new HotSpringGenData{horRadius=12}}))
                throw new Exception("Hot spring guard let a spring into the lifeless border at z="+z);
        if(!(bool)springGuard.Invoke(null,new object[]{p.WorldLength-10000,new Vec3i(0,0,0),new HotSpringGenData{horRadius=12}}))
            throw new Exception("Hot spring guard still blocks Antarctica outside the lifeless border (Earth decides there)");
        foreach(int z in new[]{p.WallDepth+500,p.WorldLength-10000})
        {
            // Uninitialized original routines cannot run with these null accessors. Returning cleanly proves
            // the installed prefix stops generation before touching terrain, entities or land claims.
            if(z==p.WallDepth+500) spring.Invoke(new GenHotSprings(),new object[]{p.WorldWidth/2,z,new Vec3i(0,0,0),new HotSpringGenData{horRadius=12}});
            var camp=new WorldGenStructure{Group="trader",Code="trader-cold"};
            var result=trader.Invoke(camp,new object[]{null,null,new BlockPos(p.WorldWidth/2,p.SeaLevel,z),0,0,0,0,0,null});
            if(!Equals(result,false)) throw new Exception("Trader guard did not reject polar camp");
        }
        // Check scoped pass-through without actually placing a nonpolar feature.
        var prefix=typeof(EarthPolarSystem).GetMethod("TraderGuard",BindingFlags.Static|BindingFlags.NonPublic);
        if(!(bool)prefix.Invoke(null,new object[]{new WorldGenStructure{Group="trader",Code="trader-cold"},new BlockPos(100,p.SeaLevel,(int)p.EquatorZ),null,false})
            || !(bool)prefix.Invoke(null,new object[]{new WorldGenStructure{Group="ruin",Code="ruin-temple"},new BlockPos(100,p.SeaLevel,p.WorldLength-100),null,false}))
            throw new Exception("Nonpolar trader or unrelated ruin incorrectly blocked");
        Mod.Logger.Notification("POLARSMOKE PASS real patched hot-spring and trader generation; nonpolar traders and ruins pass through");
    }
    void CheckNorthChunk(int x0,int z0)
    {
        int raised=0,bare=0,snow=0,drifts=0,structures=0;
        for(int lz=0;lz<32;lz++) for(int lx=0;lx<32;lx++)
        {
            int x=x0+lx,z=z0+lz;
            var s=p.EarthPolar.Sample(x,z);
            if(!p.HasSeaIce(x+0.5,z+0.5,s)) continue;
            // Ruins may place gravel/cobblestone over the ocean after the polar pass. Those features are
            // deliberately preserved; a missing ice block in otherwise bare sea must still fail below.
            var solid=api.World.BlockAccessor.GetBlock(new BlockPos(x,p.SeaLevel-1,z),BlockLayersAccess.Solid);
            if(solid.Id!=0 && solid.Code.Domain!="cylinderworld") {structures++;continue;}
            int ice=p.SeaLevel-1+p.EarthIceRise(x+0.5,z+0.5,s);
            string code=api.World.BlockAccessor.GetBlock(new BlockPos(x,ice,z)).Code.Path;
            if(!code.StartsWith("glacier-") && !code.StartsWith("seaice-"))
                throw new Exception($"Generated ridge does not match world-coordinate height: {x},{ice},{z} actual={code} sea={api.World.BlockAccessor.GetBlock(new BlockPos(x,p.SeaLevel-1,z)).Code} rain={api.World.BlockAccessor.GetRainMapHeightAt(x,z)}");
            int eighths=p.EarthSnowEighths(x+0.5,z+0.5), expectedTop=ice+(eighths+7)/8;
            if(api.World.BlockAccessor.GetRainMapHeightAt(x,z)!=expectedTop) throw new Exception("Arctic snow heightmap mismatch");
            if(ice>=p.SeaLevel) raised++;
            if(eighths==0) bare++;else snow++;
            if(eighths>=8) drifts++;
        }
        if(bare+snow<500) throw new Exception("Too little natural Arctic ice verified");
        Mod.Logger.Notification("POLARSMOKE Arctic chunk {0},{1}: raised={2}, bare={3}, snow={4}, drifts={5}, structure cells={6}",x0,z0,raised,bare,snow,drifts,structures);
    }
    void CheckSeaIce(int x,int z,EarthPolarModel.Column sample)
    {
        var sys=api.ModLoader.GetModSystem<SeaIceSystem>();
        var ba=api.World.BlockAccessor;
        var pos=new BlockPos(x,p.SeaLevel-1,z);
        int Id(string code)=>api.World.GetBlock(new AssetLocation(code)).Id;
        int salt=Id("game:saltwater-still-7"),seasonal=Id("cylinderworld:seaice-seasonal"),permanent=Id("cylinderworld:seaice-permanent");
        double summer=SeaIcePolicy.NorthMinimum+(sample.Latitude<0?.5:0), winter=summer+.5;
        var calendar=api.World.Calendar;var originalOverride=calendar.SeasonOverride;
        calendar.SetSeasonOverride(null);sys.PublishDate();
        if(p.SeaIceFixedSeason || Math.Abs(p.SeaIceYear-calendar.TotalDays/calendar.DaysPerYear%1)>0.000001)
            throw new Exception("Ordinary calendar date not used for sea ice");
        calendar.SetSeasonOverride(originalOverride);sys.PublishDate();
        if(!sample.Ocean || SeaIcePolicy.At(p,x+.5,z+.5,sample,summer)!=SeaIceKind.Open
            || SeaIcePolicy.At(p,x+.5,z+.5,sample,winter)!=SeaIceKind.Seasonal) throw new Exception("Test point is not a seasonal ocean");
        int expected=p.SeaIceAt(x+.5,z+.5,sample)==SeaIceKind.Open?salt:seasonal;
        if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Id!=expected || ba.GetBlock(pos,BlockLayersAccess.Solid).Id!=0)
            throw new Exception("Worldgen differs from live season policy");
        // Real block classes/assets and saltwater restoration, without mocked game accessors.
        foreach(int id in new[]{seasonal,permanent})
        {
            var block=api.World.Blocks[id];
            if(block is not BlockSeaIce || block.ShouldReceiveServerGameTicks(api.World,pos,new Random(1),out _))
                throw new Exception("Wrong sea ice class or vanilla temperature ticks still enabled");
            ba.SetBlock(id,pos,BlockLayersAccess.Fluid);
            block.OnBlockBroken(api.World,pos,null);
            if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Id!=salt) throw new Exception("Breaking ice did not restore saltwater");
        }
        // A freeze/thaw cycle clears only snow over the thawing cell, preserving neighbouring construction.
        var above=pos.UpCopy();var neighbour=above.EastCopy();
        int oldNeighbour=ba.GetBlock(neighbour,BlockLayersAccess.Solid).Id;
        int rock=Id("game:rock-granite"),snow=Id("game:snowblock");
        ba.SetBlock(0,above,BlockLayersAccess.Solid);
        ba.SetBlock(rock,neighbour,BlockLayersAccess.Solid);
        sys.UpdateColumn(x,z,sample,winter);
        if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Id!=seasonal) throw new Exception("Winter did not freeze saltwater");
        ba.SetBlock(snow,above,BlockLayersAccess.Solid);
        sys.UpdateColumn(x,z,sample,summer);
        if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Id!=salt || ba.GetBlock(above,BlockLayersAccess.Solid).Id!=0
            || ba.GetBlock(neighbour,BlockLayersAccess.Solid).Id!=rock) throw new Exception("Thaw or snow cleanup damaged terrain");
        // A roof/building directly over water prevents new ice.
        ba.SetBlock(rock,above,BlockLayersAccess.Solid);sys.UpdateColumn(x,z,sample,winter);
        if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Id!=salt) throw new Exception("Froze inside covered player construction");
        ba.SetBlock(0,above,BlockLayersAccess.Solid);ba.SetBlock(oldNeighbour,neighbour,BlockLayersAccess.Solid);
        // Existing 0.10.x two-block floes thaw all the way through instead of leaving an underwater barrier.
        var below=pos.DownCopy();int floe=Id("cylinderworld:glacier-floe");
        foreach(var cell in new[]{pos,below}) {ba.SetBlock(0,cell,BlockLayersAccess.Fluid);ba.SetBlock(floe,cell,BlockLayersAccess.Solid);}
        ba.SetBlock(snow,above,BlockLayersAccess.Solid);sys.UpdateColumn(x,z,sample,summer);
        foreach(var cell in new[]{pos,below})
            if(ba.GetBlock(cell,BlockLayersAccess.Solid).Id!=0 || ba.GetBlock(cell,BlockLayersAccess.Fluid).Id!=salt)
                throw new Exception("Legacy two-layer floe did not migrate");
        if(ba.GetBlock(above,BlockLayersAccess.Solid).Id!=0) throw new Exception("Legacy floe left floating snow");
        ba.SetBlock(permanent,pos,BlockLayersAccess.Fluid);sys.UpdateColumn(x,z,sample,summer);
        if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Id!=permanent) throw new Exception("Permanent block seasonally melted");
        ba.SetBlock(salt,pos,BlockLayersAccess.Fluid);sys.UpdateColumn(x,z,sample,p.SeaIceYear,p.SeaIceFixedSeason);
        Mod.Logger.Notification("POLARSMOKE PASS seasonal lifecycle lat={0}: generation, break, winter freeze, summer thaw, snow, construction, legacy migration, permanent ice",sample.Latitude);
    }
    void BeginLiveCheck()
    {
        if(liveStarted)return;
        liveStarted=true;
        var place=places.Single(v=>v.name=="seasonal-canada");
        void Ready()
        {
                var pos=new BlockPos(place.x,p.SeaLevel-1,place.z);
                var ba=api.World.BlockAccessor;
                int seasonal=api.World.GetBlock(new AssetLocation("cylinderworld:seaice-seasonal")).Id;
                ba.SetBlock(seasonal,pos,BlockLayersAccess.Fluid);
                api.World.Calendar.SetSeasonOverride((float)SeaIcePolicy.NorthMinimum);
                long deadline=api.World.ElapsedMilliseconds+90000;
                long logAt=0;
                void Poll(float _)
                {
                    if(finished)return;
                    if(api.World.ElapsedMilliseconds>=logAt)
                    {
                        var system=api.ModLoader.GetModSystem<SeaIceSystem>();
                        var work=(System.Collections.IDictionary)typeof(SeaIceSystem).GetField("loaded",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(system);
                        long key=((long)(place.x/32)<<32)|(uint)(place.z/32);
                        object task=work[key];
                        int cursor=task==null?-1:(int)task.GetType().GetField("Cursor").GetValue(task);
                        Mod.Logger.Notification("POLARSMOKE live waiting: block={0}, date={1}, columns={2}, loaded={3}, cursor={4}, target={5}",ba.GetBlock(pos).Code,p.SeaIceYear,work.Count,ba.GetChunkAtBlockPos(pos)!=null,cursor,p.SeaIceAt(place.x+.5,place.z+.5,p.EarthPolar.Sample(place.x,place.z)));
                        logAt=api.World.ElapsedMilliseconds+10000;
                    }
                    if(ba.GetBlock(pos,BlockLayersAccess.Fluid).Code.Path=="saltwater-still-7")
                    {
                        // Save a winter state so the next pass tests actual persisted ice and a fresh server load.
                        api.World.Calendar.SetSeasonOverride((float)SeaIcePolicy.NorthMaximum);
                        api.ModLoader.GetModSystem<SeaIceSystem>().PublishDate();
                        api.ModLoader.GetModSystem<SeaIceSystem>().UpdateColumn(place.x,place.z,p.EarthPolar.Sample(place.x,place.z),p.SeaIceYear);
                        Mod.Logger.Notification("POLARSMOKE PASS live scheduled calendar thaw on loaded chunks");
                        CheckMap();return;
                    }
                    if(api.World.ElapsedMilliseconds>deadline){End("FAIL live scheduler did not thaw loaded ice");return;}
                    api.Event.RegisterCallback(Poll,200);
                }
                Poll(0);
        }
        if(api.WorldManager.GetChunk(place.x/32,(p.SeaLevel-1)/32,place.z/32)!=null) Ready();
        else api.WorldManager.LoadChunkColumnPriority(place.x/32,place.z/32,new ChunkLoadOptions {
            KeepLoaded = true,
            OnLoaded=()=>api.Event.EnqueueMainThreadTask(Ready,"seasonal-live-check")
        });
    }
    /// <summary>
    /// /cyl map from the server console in the new world: Earth's sea, land and ice, sampled on its own thread with
    /// Earth's built-in heights. A build whose /cyl can't be run from the console (1.0.4) is reported and passed over.
    /// </summary>
    void CheckMap()
    {
        if(!api.WorldManager.SaveGame.IsNew) { End("PASS all generated terrain checks"); return; }
        string png=System.IO.Path.Combine(api.GetOrCreateDataPath("CylinderWorld"),"worldmap-"+api.WorldManager.SaveGame.Seed+".png");
        if(System.IO.File.Exists(png)) System.IO.File.Delete(png);
        var console=new TextCommandCallingArgs { Caller=new Caller { Type=EnumCallerType.Console, CallerPrivileges=new[]{"*"}, CallerRole="admin", FromChatGroupId=Vintagestory.API.Config.GlobalConstants.ConsoleGroup } };
        long started=api.World.ElapsedMilliseconds, deadline=started+600000;
        bool accepted=false;
        api.ChatCommands.ExecuteUnparsed("/cyl map",console,r=>{
            accepted=r.Status!=EnumCommandStatus.Error;
            Mod.Logger.Notification("POLARSMOKE map command: {0} {1}",r.Status,r.StatusMessage);
        });
        if(!accepted) { Mod.Logger.Notification("POLARSMOKE map skipped: /cyl map can't run from the console in this build"); End("PASS all generated terrain checks"); return; }
        void Poll(float _)
        {
            if(finished)return;
            bool done=false;
            try { using(var f=System.IO.File.Open(png,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.None)) done=f.Length>0; }
            catch(System.IO.IOException) { }
            if(done)
            {
                Mod.Logger.Notification("POLARSMOKE PASS map rendered in {0:0.0} s",(api.World.ElapsedMilliseconds-started)/1000.0);
                End("PASS all generated terrain checks");return;
            }
            if(api.World.ElapsedMilliseconds>deadline){End("FAIL /cyl map wrote no picture in 10 minutes");return;}
            api.Event.RegisterCallback(Poll,500);
        }
        api.Event.RegisterCallback(Poll,500);
    }
    void End(string result)
    {
        if (finished) return;
        finished=true;
        Mod.Logger.Notification("POLARSMOKE " + result);
        api.Server.ShutDown();
    }
}
