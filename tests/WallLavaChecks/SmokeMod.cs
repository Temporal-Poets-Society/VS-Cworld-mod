using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using CylinderWorld;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

// A test-only fixture immediately before vanilla GenRivulets, followed by an observation immediately after it.
// The real native generator sees five-sided recesses made from the mod's wall ice and maximum geologic activity.
// This reproduces the material/order bug without relying on a particular world's random cliff shape.
public class WallLavaFixture : ModSystem
{
    internal static readonly ConcurrentDictionary<(int x, int z), int> Results = new();
    internal static readonly Dictionary<(int x, int z), string> Targets = new();
    ICoreServerAPI api;
    int ice;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 0.89;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, () => {
            var p = api.ModLoader.GetModSystem<CylinderWorldCore>().Params;
            int x = p.WorldWidth / 64;
            Targets.Add((x, 0), "north");
            Targets.Add((x, p.WorldLength / 32 - 1), "south");
            Targets.Add((x, p.WorldLength / 64), "ordinary-terrain control");
            ice = api.World.GetBlock(new AssetLocation("cylinderworld:wallice-ice")).Id;
            if (api.World.Blocks[ice].BlockMaterial != EnumBlockMaterial.Stone)
                throw new Exception("Fixture must exercise the wall's Stone material");
        });
        api.Event.ChunkColumnGeneration(Prepare, EnumWorldGenPass.Vegetation, "standard");
    }

    void Prepare(IChunkColumnGenerateRequest request)
    {
        if (!Targets.ContainsKey((request.ChunkX, request.ChunkZ))) return;
        var climate = request.Chunks[0].MapChunk.MapRegion.ClimateMap;
        // Cold climate disables water rivulets; maximum activity makes lava production deterministic and abundant.
        Array.Fill(climate.Data, 255);
        for (int y = 1; y < api.WorldManager.MapSizeY - 1; y++)
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                {
                    var data = request.Chunks[y / 32].Data;
                    int i = ((y % 32) * 32 + z) * 32 + x;
                    if (x % 4 == 0) data.SetBlockAir(i); else data[i] = ice;
                    data.SetFluid(i, 0);
                }
    }
}

public class WallLavaObserve : ModSystem
{
    ICoreServerAPI api;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 0.91;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ChunkColumnGeneration(Observe, EnumWorldGenPass.Vegetation, "standard");
    }

    void Observe(IChunkColumnGenerateRequest request)
    {
        var key = (request.ChunkX, request.ChunkZ);
        if (!WallLavaFixture.Targets.ContainsKey(key)) return;
        int count = 0;
        foreach (var chunk in request.Chunks)
            for (int i = 0; i < 32 * 32 * 32; i++)
            {
                int fluid = chunk.Data.GetFluid(i);
                if (fluid != 0 && api.World.Blocks[fluid].Code.Path.StartsWith("lava-")) count++;
            }
        WallLavaFixture.Results[key] = count;
    }
}

public class WallLavaProbe : ModSystem
{
    public class Config { public bool ExpectWallLava; }
    ICoreServerAPI api;
    bool ended;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 1;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Run(), 100));
        api.Event.RegisterCallback(_ => End("FAIL timeout"), 120000);
    }

    void Run()
    {
        int pending = WallLavaFixture.Targets.Count;
        foreach (var target in WallLavaFixture.Targets.Keys)
            api.WorldManager.LoadChunkColumnPriority(target.x, target.z, new ChunkLoadOptions {
                KeepLoaded = true,
                OnLoaded = () => { if (--pending == 0) api.Event.EnqueueMainThreadTask(Check, "wall-lava-check"); }
            });
    }

    void Check()
    {
        try
        {
            bool baseline = api.LoadModConfig<Config>("wall-lava-probe.json")?.ExpectWallLava == true;
            foreach (var target in WallLavaFixture.Targets)
            {
                if (!WallLavaFixture.Results.TryGetValue(target.Key, out int count)) throw new Exception("Missing native generation observation");
                bool wall = target.Value != "ordinary-terrain control";
                bool wantLava = !wall || baseline;
                Mod.Logger.Notification("WALLLAVA {0}: native lava sources={1}; expected {2}", target.Value, count, wantLava ? ">0" : "0");
                if ((count > 0) != wantLava) throw new Exception("Unexpected lava count at " + target.Value);
            }
            if (!baseline) CheckBoundaries();
            End("PASS native rivulet regression" + (baseline ? " (original bug reproduced)" : " (both poles protected, ordinary terrain unchanged)"));
        }
        catch (Exception e) { End("FAIL " + e); }
    }

    void CheckBoundaries()
    {
        var type = typeof(CylinderWorldCore).Assembly.GetType("CylinderWorld.PolarRivuletGuard", true);
        var method = type.GetMethod("IsWallColumn", BindingFlags.Static | BindingFlags.NonPublic);
        var p = new SeamParams { Version = 10, WallDepth = 48, WorldLength = 8192 };
        bool At(int z) => (bool)method.Invoke(null, new object[] { p, z });
        foreach (bool earth in new[] { false, true })
        {
            p.EarthPolarIce = earth;
            if (!At(0) || !At(2) || !At(253) || !At(255) || At(3) || At(252) || At(128) || At(-1) || At(256))
                throw new Exception("Wall-column boundary mismatch, Earth mode=" + earth);
        }
        p.WallDepth = 0;
        if (At(0) || At(255) || (bool)method.Invoke(null, new object[] { null, 0 }))
            throw new Exception("Disabled wall/world must leave vanilla generation enabled");
        Mod.Logger.Notification("WALLLAVA PASS boundaries, Earth/default modes, disabled walls and inactive worlds");
    }

    void End(string result)
    {
        if (ended) return;
        ended = true;
        Mod.Logger.Notification("WALLLAVA " + result);
        api.Event.RegisterCallback(_ => api.Server.ShutDown(), 100);
    }
}
