using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CylinderWorld
{
    /// <summary>Updates loaded polar ocean columns in bounded main-thread batches. It never loads remote chunks.
    /// The same date/coverage function is used by worldgen and map previews. Ordinary block-accessor writes notify
    /// the game of fluids, lighting, heightmaps and client updates. Columns near players (and near their seam mirror,
    /// where they may come out after a jump) are visited first, with half the budget; the rest go round a ring.</summary>
    public class SeaIceSystem : ModSystem
    {
        ICoreServerAPI api;
        CylinderWorldCore core;
        int seasonal, permanent, salt, oldFloe;
        long listener;
        readonly ConcurrentQueue<(int x, int z, bool loaded)> events = new();
        /// <summary>The tick's time budget, reused.</summary>
        readonly Stopwatch time = new();
        readonly Dictionary<long, Work> loaded = new();
        readonly Queue<long> ring = new();
        readonly HashSet<long> queued = new();
        readonly Queue<long> near = new();
        readonly HashSet<long> nearSet = new(), nearFresh = new();
        readonly List<long> nearKeep = new();
        long nearRefreshAt;
        /// <summary>Chunks around each player (and around their seam mirror) that are updated first.</summary>
        const int NearRadius = 3;
        /// <summary>A player this close to a jump line gets the other side's columns near them updated first too.</summary>
        const double MirrorLead = 512;
        sealed class Work
        {
            public int X, Z, Cursor;
            /// <summary>Allocated on the first visit: many tracked columns are never visited before they unload.</summary>
            public Cell[] Cells;
        }
        /// <summary>Only what the policy reads from Earth's sample (24 bytes instead of a whole Column).</summary>
        struct Cell
        {
            public double Warmest, Coldest;
            public bool PolarOcean, South, Sampled;
            public EarthPolarModel.Column Sample => new() {
                Water = (byte)(PolarOcean ? 1 : 0), Latitude = !PolarOcean ? 0 : South ? -90 : 90, WarmestMonth = Warmest, ColdestMonth = Coldest };
        }

        public override double ExecuteOrder() => 0.1;
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            api.Event.ChunkColumnLoaded += Loaded;
            api.Event.ChunkColumnUnloaded += Unloaded;
            api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, () => {
                seasonal = BlockId("cylinderworld:seaice-seasonal", "live sea ice is off");
                permanent = BlockId("cylinderworld:seaice-permanent", "live sea ice is off");
                salt = BlockId("game:saltwater-still-7", "live sea ice is off");
                oldFloe = BlockId("cylinderworld:glacier-floe", "0.10 sea-ice floes are not upgraded");
                PublishDate();
            });
            listener = api.Event.RegisterGameTickListener(Tick, 100);
        }

        /// <summary>A block's id, or 0 with one error line when a game update renamed it or an asset failed to load: the part
        /// that needs it switches off (0 = off below) instead of the server failing to start, in every world.</summary>
        int BlockId(string code, string without)
        {
            int id = api.World.GetBlock(new AssetLocation(code))?.Id ?? 0;
            if (id == 0) Mod.Logger.Error("Block {0} not found: {1}.", code, without);
            return id;
        }

        bool Ready => seasonal != 0 && permanent != 0 && salt != 0;

        void Loaded(Vec2i c, IWorldChunk[] chunks) => events.Enqueue((c.X, c.Y, true));
        void Unloaded(Vec3i c) => events.Enqueue((c.X, c.Z, false));

        public void PublishDate()
        {
            var p = core.Params;
            if (p == null) return;
            var calendar = api.World.Calendar;
            double year = calendar.SeasonOverride ?? (calendar.TotalDays / calendar.DaysPerYear % 1);
            bool fixedSeason = calendar.SeasonOverride.HasValue;
            // A new snapshot only when the date moved (CW-0014). While the game runs it moves every tick, so this saves the
            // allocation only while the calendar stands still (paused, a fixed season)
            if (p.SeaIceSeason != null && p.SeaIceYear == year && p.SeaIceFixedSeason == fixedSeason) return;
            p.SeaIceYear = year;
            p.SeaIceFixedSeason = fixedSeason;
            p.SeaIceSeason = new SeaIceDate(year, fixedSeason);
        }

        void Tick(float dt)
        {
            PublishDate();
            var p = core.Params;
            for (int count = 0; count < 1024 && events.TryDequeue(out var e); count++)
            {
                long key = ColumnKey.Of(e.x, e.z);
                if (!e.loaded) { loaded.Remove(key); continue; }
                if (p?.EarthPolar == null || !p.SeasonalSeaIce) continue;
                double lat = p.EarthPolar.Latitude(e.z * 32);
                if (lat < 66 && p.EarthPolar.Latitude(e.z * 32 + 31) > -55) continue;
                if (!loaded.ContainsKey(key))
                {
                    loaded[key] = new Work { X = e.x, Z = e.z };
                    if (queued.Add(key)) ring.Enqueue(key);
                }
            }
            if (p?.EarthPolar == null || !p.SeasonalSeaIce || !Ready) return;
            var season = p.CurrentSeaIceDate;
            time.Restart();
            int cells = 0, changes = 0;
            if (api.World.ElapsedMilliseconds >= nearRefreshAt) RefreshNear(p);
            for (int visits = near.Count; visits > 0 && cells < 128 && changes < 24 && time.Elapsed.TotalMilliseconds < 1.5; visits--)
            {
                long key = near.Dequeue();
                if (!loaded.TryGetValue(key, out var w)) { nearSet.Remove(key); continue; }
                if (!Visit(w, p, season, ref cells, ref changes)) { loaded.Remove(key); nearSet.Remove(key); continue; }
                near.Enqueue(key);
            }
            while (ring.Count > 0 && cells < 256 && changes < 24 && time.Elapsed.TotalMilliseconds < 3)
            {
                long key = ring.Dequeue();
                queued.Remove(key);
                if (!loaded.TryGetValue(key, out var w)) continue;
                if (!Visit(w, p, season, ref cells, ref changes)) { loaded.Remove(key); continue; }
                ring.Enqueue(key);
                queued.Add(key);
            }
        }

        /// <summary>Eight columns of one chunk column. False when its sea-level chunk is gone.</summary>
        bool Visit(Work w, SeamParams p, SeaIceDate season, ref int cells, ref int changes)
        {
            if (api.WorldManager.GetChunk(w.X, (p.SeaLevel - 1) / 32, w.Z) == null) return false;
            w.Cells ??= new Cell[1024];
            // Eight columns per visit keeps catch-up fair across a large view distance.
            for (int n = 0; n < 8 && changes < 24; n++)
            {
                int i = w.Cursor;
                w.Cursor = (w.Cursor + 1) & 1023;
                int x = w.X * 32 + i % 32, z = w.Z * 32 + i / 32;
                ref var c = ref w.Cells[i];
                if (!c.Sampled)
                {
                    var s = p.EarthPolar.Sample(x, z);
                    c = new Cell { Warmest = s.WarmestMonth, Coldest = s.ColdestMonth, PolarOcean = SeaIcePolicy.InPolarOcean(s), South = s.Latitude < 0, Sampled = true };
                }
                if (c.PolarOcean && UpdateColumn(x, z, c.Sample, season.Year, season.FixedSeason)) changes++;
                cells++;
            }
            return true;
        }

        /// <summary>Once a second: the tracked columns within NearRadius of a player, or of their seam mirror. The queue
        /// keeps its rotation (columns still near stay where they are, new ones join at the back): rebuilt in the same
        /// order every second, a queue longer than a second's visits would never get to its tail.</summary>
        void RefreshNear(SeamParams p)
        {
            nearRefreshAt = api.World.ElapsedMilliseconds + 1000;
            nearFresh.Clear();
            if (loaded.Count > 0)
            {
                foreach (var plr in api.World.AllOnlinePlayers)
                {
                    var pos = plr.Entity?.Pos;
                    if (pos == null || pos.Dimension != 0) continue;
                    Add(pos.X, pos.Z);
                    if (p.DistToJumpLine(pos.X) < MirrorLead) Add(pos.X < p.WorldWidth / 2.0 ? pos.X + p.Circumference : pos.X - p.Circumference, pos.Z);
                }
            }
            nearKeep.Clear();
            while (near.Count > 0) { long key = near.Dequeue(); if (nearFresh.Contains(key)) nearKeep.Add(key); }
            foreach (long key in nearKeep) near.Enqueue(key);
            foreach (long key in nearFresh) if (!nearSet.Contains(key)) near.Enqueue(key);
            nearSet.Clear();
            nearSet.UnionWith(nearFresh);

            void Add(double x, double z)
            {
                int cx = (int)Math.Floor(x / 32), cz = (int)Math.Floor(z / 32);
                for (int dx = -NearRadius; dx <= NearRadius; dx++) for (int dz = -NearRadius; dz <= NearRadius; dz++)
                    if (loaded.ContainsKey(ColumnKey.Of(cx + dx, cz + dz))) nearFresh.Add(ColumnKey.Of(cx + dx, cz + dz));
            }
        }

        /// <summary>Shared live update entry, also exercised by the dedicated-server tests.</summary>
        public bool UpdateColumn(int x, int z, EarthPolarModel.Column sample, double year, bool fixedSeason = false)
        {
            // Every loaded copy of the seam band is updated here with its own entity check: not mirrored or journaled
            using var quiet = SeamBlocks.Suppress();
            var p = core.Params;
            if (p?.SeasonalSeaIce != true || p.EarthPolar == null || !Ready || !SeaIcePolicy.InPolarOcean(sample)
                || p.DistToZEdge(z + 0.5) < p.WallDepth) return false;
            var ba = api.World.BlockAccessor;
            var pos = new BlockPos(x, p.SeaLevel - 1, z);
            if (ba.GetChunkAtBlockPos(pos) == null) return false;
            SeaIceKind desired = SeaIcePolicy.At(p, x + 0.5, z + 0.5, sample, year, fixedSeason);
            int solid = ba.GetBlock(pos, BlockLayersAccess.Solid).Id;
            int fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid).Id;
            bool changed = false;

            // Upgrade 0.10.x natural ocean floes at the two known sea-level layers only. Never touch the
            // permanent Wall approach, glaciers, seabed, buildings or ice above/below those layers.
            if (oldFloe != 0 && solid == oldFloe && !SeaIcePolicy.PermanentApproach(p, x + 0.5, z + 0.5))
            {
                for (int y = p.SeaLevel - 1; y >= p.SeaLevel - 2; y--)
                {
                    pos.Y = y;
                    if (ba.GetBlock(pos, BlockLayersAccess.Solid).Id != oldFloe) continue;
                    ba.SetBlock(0, pos, BlockLayersAccess.Solid);
                    ba.SetBlock(salt, pos, BlockLayersAccess.Fluid);
                    changed = true;
                }
                pos.Y = p.SeaLevel - 1; solid = 0; fluid = salt;
            }
            if (solid != 0) return changed;
            if (desired == SeaIceKind.Open)
            {
                if (fluid != seasonal)
                {
                    if (changed) RemoveSnow(ba, pos);
                    return changed; // permanent blocks are never thawed by this system
                }
                ba.SetBlock(salt, pos, BlockLayersAccess.Fluid);
                RemoveSnow(ba, pos);
                return true;
            }
            int ice = desired == SeaIceKind.Permanent ? permanent : seasonal;
            if (fluid == ice || fluid == permanent || fluid != salt && fluid != seasonal) return changed;
            pos.Y++;
            var above = ba.GetBlock(pos, BlockLayersAccess.Solid);
            // The wall's rough face can overhang the sea at its foot: that sea freezes like the rest of the approach
            if (above.Id != 0 && above.BlockMaterial != EnumBlockMaterial.Snow && above is not BlockWallIce || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return changed;
            pos.Y--;
            // Do not place a colliding block into a swimmer or a boat. Revisit after it moves.
            // The game calls the filter unchecked whenever an entity is within 5 blocks, so pass one
            if (api.World.GetIntersectingEntities(pos, new[] { new Cuboidf(0, 0, 0, 1, 1, 1) }, _ => true).Length > 0) return changed;
            ba.SetBlock(ice, pos, BlockLayersAccess.Fluid);
            return true;
        }

        static void RemoveSnow(IBlockAccessor ba, BlockPos sea)
        {
            var pos = sea.Copy();
            for (int i = 0; i < 8; i++)
            {
                pos.Y++;
                if (ba.GetBlock(pos, BlockLayersAccess.Solid).BlockMaterial != EnumBlockMaterial.Snow) break;
                ba.SetBlock(0, pos, BlockLayersAccess.Solid);
            }
        }

        public override void Dispose()
        {
            if (api != null) { api.Event.ChunkColumnLoaded -= Loaded; api.Event.ChunkColumnUnloaded -= Unloaded; api.Event.UnregisterGameTickListener(listener); }
            loaded.Clear(); ring.Clear(); queued.Clear(); near.Clear(); nearSet.Clear(); nearFresh.Clear();
            base.Dispose();
        }
    }
}
