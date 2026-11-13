using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

// CW-D49 (CW-0005): opens a world whose wall columns were saved with lava sources by an old build (oldworld.ps1 phase 1)
// and checks that this build takes them out when the columns load, marks the columns, and leaves ordinary terrain alone.
public class OldWorldProbe : ModSystem
{
    ICoreServerAPI api;
    bool ended;
    (int x, int z, string name)[] targets;
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
        int width = api.WorldManager.MapSizeX, length = api.WorldManager.MapSizeZ;
        int x = width / 64;
        targets = new[] { (x, 0, "north"), (x, length / 32 - 1, "south"), (x, length / 64, "ordinary-terrain control") };
        int pending = targets.Length;
        foreach (var t in targets)
            api.WorldManager.LoadChunkColumnPriority(t.x, t.z, new ChunkLoadOptions {
                KeepLoaded = true,
                // the cleanup runs on a 250 ms tick after the load: look 3 s later
                OnLoaded = () => { if (--pending == 0) api.Event.RegisterCallback(_ => Check(), 3000); }
            });
    }

    void Check()
    {
        try
        {
            foreach (var t in targets)
            {
                int sources = 0, lava = 0;
                var codes = new System.Collections.Generic.SortedDictionary<string, int>();
                for (int cy = 0; cy < api.WorldManager.MapSizeY / 32; cy++)
                {
                    var chunk = api.WorldManager.GetChunk(t.x, cy, t.z);
                    if (chunk == null) throw new Exception("Target chunk not loaded at " + t.name);
                    chunk.Unpack();
                    for (int i = 0; i < 32 * 32 * 32; i++)
                    {
                        int fluid = chunk.Data.GetFluid(i);
                        if (fluid == 0) continue;
                        var code = api.World.Blocks[fluid].Code;
                        if (!code.Path.StartsWith("lava-")) continue;
                        lava++;
                        codes.TryGetValue(code.Path, out int had); codes[code.Path] = had + 1;
                        if (code.Path.EndsWith("-7"))
                        {
                            sources++;
                            if (t.name != "ordinary-terrain control")
                            {
                                int lx = i % 32, lz = i / 32 % 32, y = cy * 32 + i / 1024;
                                var ba = api.World.BlockAccessor;
                                var at = new Vintagestory.API.MathTools.BlockPos(t.x * 32 + lx, y, t.z * 32 + lz, 0);
                                string n = "";
                                foreach (var (dx, dy, dz) in new[] { (0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0), (0, 1, 0), (0, -1, 0) })
                                    n += " " + ba.GetBlock(at.AddCopy(dx, dy, dz), 1)?.Code?.ToShortString();
                                Mod.Logger.Notification("OLDWORLD left (level " + api.World.Blocks[fluid].LiquidLevel + ", liquid " + api.World.Blocks[fluid].LiquidCode + ") {0} at lx={1} y={2} lz={3}; solid there {4}; faces N E S W up down:{5}", code.Path, lx, y, lz, ba.GetBlock(at, 1)?.Code?.ToShortString(), n);
                            }
                        }   // a rivulet source: still-7 as generated, e-7, w-7... once updated
                    }
                }
                Mod.Logger.Notification("OLDWORLD {0} lava blocks: {1}", t.name, string.Join(", ", System.Linq.Enumerable.Select(codes, kv => kv.Key + " " + kv.Value)));
                bool marked = api.WorldManager.GetMapChunk(t.x, t.z)?.GetModdata("cylinderworld-icerivulets") != null;
                bool wall = t.name != "ordinary-terrain control";
                Mod.Logger.Notification("OLDWORLD {0}: lava sources={1}, lava cells={2}, marked={3}; expected sources {4}, marked {5}",
                    t.name, sources, lava, marked, wall ? "0" : ">0", wall);
                if (wall ? sources != 0 || !marked : sources == 0 || marked) throw new Exception("Unexpected state at " + t.name);
            }
            End("PASS saved wall lava taken out at both poles, ordinary terrain unchanged");
        }
        catch (Exception e) { End("FAIL " + e); }
    }

    void End(string result)
    {
        if (ended) return;
        ended = true;
        Mod.Logger.Notification("OLDWORLD " + result);
        api.Event.RegisterCallback(_ => api.Server.ShutDown(), 100);
    }
}
