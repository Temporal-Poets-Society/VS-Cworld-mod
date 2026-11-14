using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    // SeamWorldGen, part: Vanilla's rivulet sources in this mod's Stone-material ice: at generation and, once per column, in older worlds. (CW-0019: moved unchanged from SeamWorldGen.cs.)
    public partial class SeamWorldGen
    {
        /// <summary>
        /// Vanilla's rivulets (GenRivulets, Vegetation 0.9) put a lava or water source wherever a cell has rock on top and
        /// four sides and one open side, and they count this mod's glacier, floe and wall ice as rock (blockmaterial Stone,
        /// CW-D13): sheet edge cliffs, the shelf front under the sea, ice mantles, Earth's Antarctic glacier and Arctic
        /// ridges got lava and springs. PolarRivuletGuard keeps the generator out of the wall chunks; in the other chunk
        /// columns that can hold this ice (and in the wall chunks too while the guard is off, PolarRivuletGuard.GuardOn)
        /// it registers this to run right after the generator (and before vanilla's
        /// sunlight at 0.95). Every source left the way a rivulet leaves one (fresh water or lava with no block, a
        /// Stone-material block on top and on four sides, one open side) that touches this ice gets the block above it.
        /// The source's block update, scheduled for when the chunk is done, then finds a solid block. Rivulets that only
        /// touch natural rock stay. (Vanilla's mountain-side rivers, which carve short channels, are not undone.)
        /// </summary>
        internal void ClearRivuletsInIce(IChunkColumnGenerateRequest request)
        {
            var p = core.Params;
            if (p == null || !idsReady || !p.HasWalls || freshWaterId == 0 && lavaId == 0) return;
            int z0 = request.ChunkZ * GlobalConstants.ChunkSize;
            if (p.EarthPolarIce ? p.EarthPolar == null || !EarthPolarChunk(p, p.EarthPolar, z0)
                : z0 >= p.PolarZoneDepth && z0 + GlobalConstants.ChunkSize <= p.WorldLength - p.PolarZoneDepth) return;
            // generated with this cleanup: the load-time one (CW-D49) has nothing to do here
            request.Chunks[0].MapChunk?.SetModdata(IceRivuletsKey, IceRivuletsDone);
            if (PolarRivuletGuard.GuardOn && PolarRivuletGuard.IsWallColumn(p, request.ChunkZ)) return;   // the generator didn't run there

            var chunks = request.Chunks;
            var blocks = sapi.World.Blocks;
            int top = Math.Min(chunks[0].MapChunk.YMax, sapi.WorldManager.MapSizeY - 2), removed = 0;
            // A source must touch this ice, so it lies in a 32-high chunk that holds some or right next to one: the others
            // (most of a column, whose ice is a sheet of some tens of blocks) are skipped by a look at each chunk's palette
            Span<bool> nearIce = stackalloc bool[chunks.Length];
            for (int cy = 0; cy < chunks.Length; cy++)
                if (HoldsStoneIce(chunks[cy].Data))
                    for (int n = Math.Max(0, cy - 1); n <= Math.Min(chunks.Length - 1, cy + 1); n++) nearIce[n] = true;
            for (int y = 1; y <= top; y++)
            {
                if (!nearIce[y / GlobalConstants.ChunkSize])
                {
                    y = y / GlobalConstants.ChunkSize * GlobalConstants.ChunkSize + GlobalConstants.ChunkSize - 1;
                    continue;
                }
                IChunkBlocks data = chunks[y / GlobalConstants.ChunkSize].Data;
                // a rivulet's cell and all six of its neighbours lie inside the chunk column (vanilla picks 1..30)
                for (int lz = 1; lz < GlobalConstants.ChunkSize - 1; lz++)
                    for (int lx = 1; lx < GlobalConstants.ChunkSize - 1; lx++)
                    {
                        int idx = CellIndex(y, lz, lx);
                        int fluid = data.GetFluid(idx);
                        if (fluid == 0 || fluid != freshWaterId && fluid != lavaId || SolidId(data, idx) != 0) continue;
                        int above = RivuletInIce(chunks, blocks, lx, y, lz);
                        if (above == 0) continue;
                        data[idx] = above;
                        data.SetFluid(idx, 0);
                        removed++;
                    }
            }
            if (removed > 0) Mod.Logger.Debug("Cylinder World: {0} rivulet sources taken out of the polar ice in chunk column {1},{2}", removed, request.ChunkX, request.ChunkZ);
        }

        /// <summary>Map chunk moddata: this column's polar ice holds no rivulet source (generated with the cleanup above, or
        /// cleaned when loaded, CW-D49).</summary>
        const string IceRivuletsKey = "cylinderworld-icerivulets";
        static readonly byte[] IceRivuletsDone = { 1 };
        readonly ConcurrentQueue<Vec2i> oldColumns = new();
        /// <summary>Block ids a saved rivulet source can have (main thread only, set on first use).</summary>
        bool[] savedSource;

        /// <summary>A chunk column where vanilla's rivulets could put sources into this mod's ice (ClearRivuletsInIce's zone).</summary>
        bool InIceZone(SeamParams p, int z0) => p.EarthPolarIce
            ? p.EarthPolar != null && EarthPolarChunk(p, p.EarthPolar, z0)
            : z0 < p.PolarZoneDepth || z0 + GlobalConstants.ChunkSize > p.WorldLength - p.PolarZoneDepth;

        /// <summary>
        /// CW-D49 (CW-0005): a polar column saved before it got the generation-time cleanup (worlds made with Cylinder World
        /// before 1.0.2, whose wall lava the author found at 89.9 N) is queued when it loads, once: the mark on its map chunk
        /// says it was looked at. The work is done on the main thread (CleanOldColumns).
        /// </summary>
        void OnColumnLoaded(Vec2i coord, IWorldChunk[] chunks)
        {
            try
            {
                var p = core.Params;
                if (p == null || !idsReady || !p.HasWalls || freshWaterId == 0 && lavaId == 0) return;
                if (!InIceZone(p, coord.Y * GlobalConstants.ChunkSize)) return;
                var mapChunk = chunks != null && chunks.Length > 0 ? chunks[0]?.MapChunk : null;
                if (mapChunk == null || mapChunk.GetModdata(IceRivuletsKey) != null) return;
                oldColumns.Enqueue(new Vec2i(coord.X, coord.Y));
            }
            catch (Exception e) { Mod.Logger.Warning("Cylinder World: could not queue chunk column {0},{1} for the polar ice cleanup: {2}", coord.X, coord.Y, e.Message); }
        }

        /// <summary>Up to 8 queued columns a call (main thread): rivulet sources in this mod's ice taken out the way the
        /// generation-time cleanup does it, then the column marked. A column unloaded meanwhile is looked at when it loads again.</summary>
        void CleanOldColumns(float dt)
        {
            for (int n = 0; n < 8 && oldColumns.TryDequeue(out var c); n++)
            {
                try
                {
                    var mapChunk = sapi.WorldManager.GetMapChunk(c.X, c.Y);
                    if (mapChunk == null || mapChunk.GetModdata(IceRivuletsKey) != null) continue;
                    var chunks = new IServerChunk[sapi.WorldManager.MapSizeY / GlobalConstants.ChunkSize];
                    bool loaded = true;
                    for (int cy = 0; cy < chunks.Length && loaded; cy++)
                    {
                        chunks[cy] = sapi.WorldManager.GetChunk(c.X, cy, c.Y);
                        if (chunks[cy] == null) loaded = false; else chunks[cy].Unpack();
                    }
                    if (!loaded) continue;
                    // again until a pass finds none: filling one source can leave a neighbour in a pocket of two
                    // (its other open side) rivulet-shaped only now
                    int removed = 0, found;
                    for (int pass = 0; pass < 4 && (found = TakeOutSavedRivulets(chunks, mapChunk, c.X, c.Y)) > 0; pass++) removed += found;
                    mapChunk.SetModdata(IceRivuletsKey, IceRivuletsDone);
                    mapChunk.MarkDirty();
                    if (removed > 0)
                        Mod.Logger.Notification("Cylinder World: {0} rivulet source(s) saved in the polar ice taken out of chunk column {1},{2} (x={3} z={4}); their flows dry up",
                            removed, c.X, c.Y, c.X * GlobalConstants.ChunkSize, c.Y * GlobalConstants.ChunkSize);
                }
                catch (Exception e) { Mod.Logger.Warning("Cylinder World: the polar ice cleanup of chunk column {0},{1} failed: {2}", c.X, c.Y, e.Message); }
            }
        }

        /// <summary>The rivulet sources in this mod's ice of a loaded column (RivuletInIce, as at generation), each given the
        /// block above it through the block accessor, so clients get the change and the flow below gets a neighbour update.</summary>
        int TakeOutSavedRivulets(IServerChunk[] chunks, IMapChunk mapChunk, int chunkX, int chunkZ)
        {
            var blocks = sapi.World.Blocks;
            // A saved rivulet source is a full (level 7) lava or fresh water block: generated as still-7, it turns into a
            // flowing one (lava-e-7 and the like, toward its open side) once a block update reaches it
            if (savedSource == null)
            {
                var ids = new bool[blocks.Count];
                for (int id = 1; id < ids.Length; id++)
                    if (blocks[id] is { LiquidLevel: 7 } b && (b.LiquidCode == "lava" || b.Code?.Domain == "game" && b.Code.Path.StartsWith("water-"))) ids[id] = true;
                savedSource = ids;
            }
            // not the map chunk's YMax: a saved one can be stale; chunks without this ice are skipped by their palette anyway
            int top = sapi.WorldManager.MapSizeY - 2;
            var found = new List<(int lx, int y, int lz, int above)>();
            Span<bool> nearIce = stackalloc bool[chunks.Length];
            for (int cy = 0; cy < chunks.Length; cy++)
                if (HoldsStoneIce(chunks[cy].Data))
                    for (int n = Math.Max(0, cy - 1); n <= Math.Min(chunks.Length - 1, cy + 1); n++) nearIce[n] = true;
            for (int y = 1; y <= top; y++)
            {
                if (!nearIce[y / GlobalConstants.ChunkSize])
                {
                    y = y / GlobalConstants.ChunkSize * GlobalConstants.ChunkSize + GlobalConstants.ChunkSize - 1;
                    continue;
                }
                IChunkBlocks data = chunks[y / GlobalConstants.ChunkSize].Data;
                for (int lz = 1; lz < GlobalConstants.ChunkSize - 1; lz++)
                    for (int lx = 1; lx < GlobalConstants.ChunkSize - 1; lx++)
                    {
                        int idx = CellIndex(y, lz, lx);
                        int fluid = data.GetFluid(idx);
                        if (fluid == 0 || fluid >= savedSource.Length || !savedSource[fluid] || SolidId(data, idx) != 0) continue;
                        int above = RivuletInIce(chunks, blocks, lx, y, lz);
                        if (above != 0) found.Add((lx, y, lz, above));
                    }
            }
            if (found.Count == 0) return 0;
            var ba = sapi.World.BlockAccessor;
            var pos = new BlockPos(0, 0, 0, 0);
            foreach (var (lx, y, lz, above) in found)
            {
                pos.Set(chunkX * GlobalConstants.ChunkSize + lx, y, chunkZ * GlobalConstants.ChunkSize + lz);
                ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
                ba.SetBlock(above, pos);
                ba.TriggerNeighbourBlockUpdate(pos);
            }
            return found.Count;
        }

        /// <summary>
        /// This 32-high chunk holds some of this mod's Stone-material ice: IChunkBlocks.ContainsBlock looks for the id in the
        /// chunk's palette, then (only for an id it has) in the cells' bits, so it is exact and fast. Unlike the cell reads it
        /// takes no lock, so if it fails (a neighbour's worldgen thread growing the chunk's palette meanwhile) the chunk counts
        /// as holding ice and is scanned as before.
        /// </summary>
        bool HoldsStoneIce(IChunkBlocks data)
        {
            try
            {
                foreach (int id in stoneIceIds) if (data.ContainsBlock(id)) return true;
                return false;
            }
            catch (Exception) { return true; }
        }

        static readonly (int dx, int dy, int dz)[] Faces = { (0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0), (0, 1, 0), (0, -1, 0) };

        /// <summary>The block above a rivulet-shaped source at (lx, y, lz) that touches this mod's Stone-material ice, else 0.</summary>
        int RivuletInIce(IServerChunk[] chunks, IList<Block> blocks, int lx, int y, int lz)
        {
            int stone = 0, open = 0, above = 0;
            bool ice = false;
            foreach (var (dx, dy, dz) in Faces)
            {
                int ny = y + dy;
                int id = SolidId(chunks[ny / GlobalConstants.ChunkSize].Data, CellIndex(ny, lz + dz, lx + dx));
                var material = id < blocks.Count ? blocks[id]?.BlockMaterial : null;
                if (material == EnumBlockMaterial.Stone)
                {
                    stone++;
                    if (id < stoneIce.Length && stoneIce[id]) ice = true;
                    if (dy > 0) above = id;
                }
                else if (material == EnumBlockMaterial.Air) open++;
            }
            return stone == 5 && open == 1 && above != 0 && ice ? above : 0;
        }
    }
}
