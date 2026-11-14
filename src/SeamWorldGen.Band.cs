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
    // SeamWorldGen, part: The seam band in chunk columns: the column cache, the flatten, Earth's land and curtain in the band, blend and ramp. (CW-0019: moved unchanged from SeamWorldGen.cs.)
    public partial class SeamWorldGen
    {
        // ---------------------------------------------------------------- chunk columns

        /// <summary>
        /// What this mod's passes over one chunk column (TerrainFeatures, Vegetation, NeighbourSunLightFlood) would otherwise
        /// work out again in each pass: Earth's samples of its 32 × 32 columns (Earth's whole terrain model per column) and
        /// the band floor of its columns and of the ring of columns just outside it (a pure function of x and z). Made on
        /// first use and dropped after the last pass (ForgetColumnCache); a column that never gets there takes its entry
        /// with it when its map chunk is gone. Only this column's own passes use it, and they never overlap, so it needs no
        /// lock; a column outside the chunk is sampled directly, never read from its own column's cache.
        /// </summary>
        sealed class ColumnCache
        {
            EarthPolarModel.Column[] earth;
            bool[] earthDone;
            /// <summary>[(lz + 1) * 34 + lx + 1] for lx, lz in -1..32; NoFloor until worked out.</summary>
            int[] floor;
            const int NoFloor = int.MinValue;

            /// <summary>Earth's column at (x0 + lx, z0 + lz), lx and lz in 0..31 (Earth generates columns at integer coordinates).</summary>
            public EarthPolarModel.Column Earth(EarthPolarModel model, int x0, int z0, int lx, int lz)
            {
                earth ??= new EarthPolarModel.Column[32 * 32];
                earthDone ??= new bool[32 * 32];
                int i = lz * 32 + lx;
                if (!earthDone[i])
                {
                    earth[i] = model.Sample(x0 + lx, z0 + lz);
                    earthDone[i] = true;
                }
                return earth[i];
            }

            /// <summary>Earth's column at (x0 + lx, z0 + lz) for lx, lz in -1..32: cached inside the chunk, sampled outside.</summary>
            public EarthPolarModel.Column EarthAround(EarthPolarModel model, int x0, int z0, int lx, int lz)
                => (uint)lx < 32 && (uint)lz < 32 ? Earth(model, x0, z0, lx, lz) : model.Sample(x0 + lx, z0 + lz);

            public bool TryFloor(int lx, int lz, out int y)
            {
                floor ??= NewFloors();
                y = floor[(lz + 1) * 34 + lx + 1];
                return y != NoFloor;
            }

            public int SetFloor(int lx, int lz, int y) => floor[(lz + 1) * 34 + lx + 1] = y;

            static int[] NewFloors()
            {
                var f = new int[34 * 34];
                Array.Fill(f, NoFloor);
                return f;
            }
        }
        readonly ConditionalWeakTable<IMapChunk, ColumnCache> columnCache = new();

        ColumnCache CacheOf(IChunkColumnGenerateRequest request) => columnCache.GetValue(request.Chunks[0].MapChunk, _ => new ColumnCache());

        /// <summary>After this mod's last pass over the chunk column.</summary>
        void ForgetColumnCache(IChunkColumnGenerateRequest request) => columnCache.Remove(request.Chunks[0].MapChunk);

        /// <summary>
        /// Top solid block of the band's identical part at (x0 + lx, z0 + lz), lx and lz in -1..32: SeamFloorY, or with
        /// EarthSeabed Earth's own seabed (EarthSeabedY).
        /// </summary>
        static int BandFloor(SeamParams p, ColumnCache cache, int x0, int z0, int lx, int lz, int seaLevel)
        {
            if (cache.TryFloor(lx, lz, out int y)) return y;
            return cache.SetFloor(lx, lz, p.EarthSeabed ? EarthSeabedY(p, cache, x0, z0, lx, lz, seaLevel) : p.SeamFloorY(x0 + lx + 0.5, z0 + lz + 0.5));
        }

        /// <summary>
        /// PolarOwnsSeabed(x, z) for column (lx, lz) of this chunk, taking Earth's sample from the chunk column's cache (the
        /// same sample at the same integer coordinates; a separate name, as Wmap binds PolarOwnsSeabed by name).
        /// </summary>
        static bool PolarOwnsColumn(SeamParams p, ColumnCache cache, int x0, int z0, int lx, int lz, double x, double z)
        {
            if (!p.HasWalls || !p.EarthPolarIce) return p.PolarOwnsSeabed(x, z);
            return p.DistToZEdge(z) < p.WallFaceMax
                || p.EarthMayKeepSeamColumn(x, z) && SeamParams.EarthKeepsSampled(cache.Earth(p.EarthPolar, x0, z0, lx, lz));
        }

        /// <param name="firstPass">TerrainFeatures pass (true) or Vegetation pass (false). Steps that adapt existing
        /// ground (v6 seam blend, shelf raise outside the seam band) only run in the first pass: in the second the ground
        /// is already adapted, and adapting it again would push it further.</param>
        void OnChunkColumnGen(IChunkColumnGenerateRequest request, bool firstPass)
        {
            var p = RequireIds();
            if (p == null) return;

            if (p.FlattenSeabed && saltWaterId != 0) FlattenSeam(request, p, firstPass);
            if (p.HasWalls && wallIds[0] != 0) BuildPolarZone(request, p, firstPass);
        }

        /// <summary>Remove what neighbours' Vegetation passes spilled into the band's identical part after its own.</summary>
        void ClearSeamSpill(IChunkColumnGenerateRequest request)
        {
            var p = core.Params;
            if (p == null || !idsReady) return;
            if (p.FlattenSeabed && saltWaterId != 0) FlattenSeam(request, p, false, true);
            if (p.HasWalls && wallIds[0] != 0) BuildPolarZone(request, p, false, true);
        }

        /// <param name="spill">Only rewrite the identical part from its floor up, and the curtain under it (ClearSeamSpill).</param>
        void FlattenSeam(IChunkColumnGenerateRequest request, SeamParams p, bool firstPass, bool spill = false)
        {
            const int cs = GlobalConstants.ChunkSize;
            int x0 = request.ChunkX * cs;
            double reach = FlattenReach(p, firstPass);
            if (x0 >= reach && x0 + cs <= p.WorldWidth - reach) return;

            IServerChunk[] chunks = request.Chunks;
            IMapChunk mapChunk = chunks[0].MapChunk;
            int mapSizeY = sapi.WorldManager.MapSizeY;
            int seaLevel = sapi.World.SeaLevel;
            int z0 = request.ChunkZ * cs;
            bool natural = p.Version >= 10;
            var cache = CacheOf(request);

            for (int lx = 0; lx < cs; lx++)
            {
                double wx = x0 + lx + 0.5;
                if (p.DistToEdge(wx) >= reach) continue;
                bool curtain = p.DistToJumpLine(wx) <= SeamParams.CurtainHalfWidth;

                double blend = curtain || natural ? 0 : p.SeamBlendWeight(wx);

                for (int lz = 0; lz < cs; lz++)
                {
                    double wz = z0 + lz + 0.5;
                    // CW-D31: Earth's land carried across its gutter north of Antarctica (St. Lawrence Island) stays land
                    if (p.EarthSeamLand && p.EarthSeamLandColumn(cache.Earth(p.EarthPolar, x0, z0, lx, lz)))
                    {
                        if (curtain || p.DistToEdge(wx) < p.SeamEdge(wx, wz))
                            SeamLandColumn(chunks, mapChunk, p, cache, x0, z0, lx, lz, curtain, spill, mapSizeY, seaLevel);
                        continue;
                    }
                    // the ice wall and its sheet/shelf own these columns (Earth mode: the wall and Antarctic land)
                    if (PolarOwnsColumn(p, cache, x0, z0, lx, lz, wx, wz))
                    {
                        // Earth's land and ice kept at the jump line get the band's mantle curtain under them too (the
                        // default layouts' polar zone lays its own, BuildPolarZone); the wall itself is unbreakable
                        if (curtain && p.EarthPolarIce && p.DistToZEdge(wz) >= p.WallFaceMax) EarthCurtain(chunks, p, cache, x0, z0, lx, lz);
                        continue;
                    }

                    double edge = natural && !curtain ? p.SeamEdge(wx, wz) : 0;
                    if (natural && !curtain && p.DistToEdge(wx) >= edge)
                    {
                        // Earth's seabed is the natural ground there too: nothing to ramp
                        if (firstPass && !p.EarthSeabed) RampSeamColumn(chunks, mapChunk, p, lx, lz, wx, edge, BandFloor(p, cache, x0, z0, lx, lz, seaLevel), mapSizeY, seaLevel);
                        continue;
                    }
                    int floorY = BandFloor(p, cache, x0, z0, lx, lz, seaLevel);
                    if (blend > 0)
                    {
                        if (firstPass) BlendSeamColumn(chunks, mapChunk, lx, lz, floorY, blend, mapSizeY, seaLevel);
                        continue;
                    }

                    // Where a neighbour's floor is lower (v10 relief, Earth's seabed), its water shows this column's side below
                    // the floor: granite there, from one below the lowest neighbour floor, so both laps match (whatever strata,
                    // ores or rivulets were there); the spill pass starts there too. A flat floor (before v10) has no such side.
                    int side = floorY;
                    if (!curtain)
                    {
                        int lowest = floorY;
                        foreach (var (dx, dz) in Neighbours) lowest = Math.Min(lowest, BandFloor(p, cache, x0, z0, lx + dx, lz + dz, seaLevel));
                        if (lowest < floorY) side = Math.Max(1, lowest - 1);
                    }

                    // The curtain from the bottom in the spill pass too: Earth's rock floor (a Vegetation pass after this mod's)
                    // turns every cell up to its top into plain rock, the curtain's mantle included
                    for (int y = spill && !curtain ? side : 1; y < mapSizeY - 1; y++)
                    {
                        IChunkBlocks data = chunks[y / cs].Data;
                        int idx = CellIndex(y, lz, lx);

                        if (y < floorY)
                        {
                            if (curtain) { data[idx] = mantleId; data.SetFluid(idx, 0); }
                            else if (y >= side) { data[idx] = rockId; data.SetFluid(idx, 0); }
                            // Keep ores/rock, but fill caves and water pockets so the gravel on top can't fall
                            else if (TopLayerId(data, idx) == 0 || data.GetFluid(idx) != 0) { data[idx] = rockId; data.SetFluid(idx, 0); }
                        }
                        else if (y == floorY)
                        {
                            data[idx] = gravelId;
                            data.SetFluid(idx, 0);
                        }
                        else if (y < seaLevel)
                        {
                            data.SetBlockAir(idx);
                            data.SetFluid(idx, saltWaterId);
                        }
                        else
                        {
                            if (TopLayerId(data, idx) == 0 && data.GetFluid(idx) == 0) continue;
                            data.SetBlockAir(idx);
                            data.SetFluid(idx, 0);
                        }
                    }

                    int mi = lz * cs + lx;
                    mapChunk.RainHeightMap[mi] = (ushort)(seaLevel - 1);
                    mapChunk.WorldGenTerrainHeightMap[mi] = (ushort)floorY;
                }
            }

            // Drop block entities (shipwreck chests, etc.) whose blocks the flatten or blend replaced
            RemoveOrphanedBlockEntities(chunks);
        }

        /// <summary>
        /// Distance from the world edge within which FlattenSeam can change a column in this pass. The v10 ramp (out to
        /// SeamWorkReach) and the v6-v9 blend run in the first pass only, and never in EarthSeabed worlds; otherwise only
        /// the identical part is written (and the curtain, which small worlds can push past it): up to SeamEdge, which
        /// is below H - SeamBlend (tanh never reaches ±1), from v6 on, and up to H before. Columns sit at half-integer
        /// distances, so none lies exactly on H - SeamBlend.
        /// </summary>
        static double FlattenReach(SeamParams p, bool firstPass)
        {
            if (firstPass && !p.EarthSeabed) return p.SeamWorkReach;
            double identical = p.Version >= 6 ? p.SeamHalfWidth - SeamParams.SeamBlend : p.SeamHalfWidth;
            return Math.Min(p.SeamWorkReach, Math.Max(identical, p.JumpInset + SeamParams.CurtainHalfWidth + 1));
        }

        static readonly (int dx, int dz)[] Neighbours = { (-1, 0), (1, 0), (0, -1), (0, 1) };

        /// <summary>
        /// CW-D31: Earth's land in the band's identical part (St. Lawrence Island across the seam). Earth's height repeats one
        /// ring apart, but vanilla's rock strata, caves, soil and plants don't, so the column is rebuilt from Earth's height, its
        /// neighbours' heights and the ring position only: caves below filled, granite at least 8 blocks deep (vanilla's strata
        /// differ between the two sides) and wherever a slope or the sea beside it can show the column's side, two blocks of soil under grass (sand at the water's edge; bare granite where the warmest month
        /// is below 1 °C, as Earth strips it), tall grass by a hash of the ring position, nothing above. The mantle curtain
        /// under the jump line starts below the granite. (1.0.3: ported from 0.11.15-playtest.2, reading Earth directly.)
        /// Lakes, salt lakes and rivers count as land here (Earth's Ocean is the sea only): before EarthSeamWater their
        /// bed was rebuilt dry; with it (new worlds from 1.1) they keep Earth's water.
        /// </summary>
        void SeamLandColumn(IServerChunk[] chunks, IMapChunk mapChunk, SeamParams p, ColumnCache cache, int x0, int z0, int lx, int lz,
            bool curtain, bool spill, int mapSizeY, int seaLevel)
        {
            const int cs = GlobalConstants.ChunkSize;
            var earth = p.EarthPolar;
            // Earth generates columns at integer coordinates
            var c = cache.Earth(earth, x0, z0, lx, lz);
            int top = Math.Clamp(c.Top, 4, mapSizeY - 3);
            // the lowest ground beside it: everything from there up can be seen from the side
            int open = top;
            foreach (var (dx, dz) in Neighbours) open = Math.Min(open, cache.EarthAround(earth, x0, z0, lx + dx, lz + dz).Top);
            int granite = Math.Max(1, Math.Min(open - 1, top - 8));

            // Earth's climate is only sampled from this latitude on; nearer the equator WarmestMonth reads 0
            bool cold = Math.Abs(c.Latitude) >= EarthPolarModel.ClimateSampleLatitude;
            int surface, under, plant = 0;
            if (top <= seaLevel) surface = under = sandId;
            else if (cold && c.WarmestMonth < 1) surface = under = rockId;
            else
            {
                bool sparse = cold && c.WarmestMonth < 6;
                surface = sparse ? soilSparseId : soilId;
                under = soilBareId;
                long ring = (long)p.Circumference;
                int rx = (int)((((long)(x0 + lx) - p.JumpInset) % ring + ring) % ring);
                int roll = GameMath.MurmurHash3Mod(rx, 7, z0 + lz, 1000);
                if (roll < (sparse ? 120 : 300)) plant = tallGrassIds[roll % tallGrassIds.Length];
            }

            // EarthSeamWater worlds (1.1): Earth's lakes, salt lakes and rivers keep their water, still (rivers like Earth's
            // calm pools), fresh or salt, up to Earth's water level (which repeats one ring apart like its ground), on a
            // gravel bed with nothing growing. Worlds whose band began without it keep the dry bed on both laps.
            int waterTop = -1, waterId = 0;
            if (p.EarthSeamWater && (c.Water == 2 || c.Water == 3) && c.WaterTop > top)
            {
                waterId = c.Water == 3 ? saltWaterId : freshWaterId;
                if (waterId != 0) waterTop = Math.Min(c.WaterTop, mapSizeY - 2);
            }
            if (waterTop > top)
            {
                surface = gravelId;
                plant = 0;
            }

            // the curtain from the bottom in the spill pass too (Earth's rock floor, see FlattenSeam)
            for (int y = spill && !curtain ? granite : 1; y < mapSizeY - 1; y++)
            {
                IChunkBlocks data = chunks[y / cs].Data;
                int idx = CellIndex(y, lz, lx);
                int id, fluid = 0;
                if (y < granite)
                {
                    if (curtain) id = mantleId;
                    // keep ores and rock, fill caves and water pockets
                    else if (TopLayerId(data, idx) == 0 || data.GetFluid(idx) != 0) id = rockId;
                    else continue;
                }
                else if (y < top - 2) id = rockId;
                else if (y < top) id = under;
                else if (y == top) id = surface;
                else if (y <= waterTop) { id = 0; fluid = waterId; }
                else if (y == top + 1) id = plant;
                else id = 0;

                if (id == 0)
                {
                    if (SolidId(data, idx) == 0 && data.GetFluid(idx) == fluid) continue;
                    data.SetBlockAir(idx);
                }
                else data[idx] = id;
                data.SetFluid(idx, fluid);
            }

            int mi = lz * cs + lx;
            int surfaceTop = Math.Max(top, waterTop);
            mapChunk.RainHeightMap[mi] = (ushort)surfaceTop;
            mapChunk.WorldGenTerrainHeightMap[mi] = (ushort)top;
            mapChunk.YMax = Math.Max(mapChunk.YMax, (ushort)(surfaceTop + 1));
        }

        /// <summary>
        /// The mantle curtain under the jump line, below Earth's land or ice that the band keeps (Antarctica): mantle up to
        /// eight blocks under the column's ground (the rock under its ice) and one under its lowest neighbour's, as CW-D31's
        /// land columns have it, so the curtain can't be seen from beside the column and nobody tunnels across the jump.
        /// </summary>
        void EarthCurtain(IServerChunk[] chunks, SeamParams p, ColumnCache cache, int x0, int z0, int lx, int lz)
        {
            const int cs = GlobalConstants.ChunkSize;
            var earth = p.EarthPolar;
            static int Ground(in EarthPolarModel.Column c) => c.Glacier ? Math.Min(c.Top, c.IceBottom - 1) : c.Top;
            int ground = Ground(cache.Earth(earth, x0, z0, lx, lz)), open = ground;
            foreach (var (dx, dz) in Neighbours) open = Math.Min(open, Ground(cache.EarthAround(earth, x0, z0, lx + dx, lz + dz)));
            int below = Math.Min(Math.Max(1, Math.Min(open - 1, ground - 8)), sapi.WorldManager.MapSizeY - 1);
            for (int y = 1; y < below; y++)
            {
                IChunkBlocks data = chunks[y / cs].Data;
                int idx = CellIndex(y, lz, lx);
                if (SolidId(data, idx) == mantleId && data.GetFluid(idx) == 0) continue;
                data[idx] = mantleId;
                data.SetFluid(idx, 0);
            }
        }

        /// <summary>
        /// EarthSeabed worlds: the band's floor is Earth's real seabed in its seam gutter (Earth generates columns at integer
        /// coordinates), kept under water. Read from Earth's model, not the chunk, so caves and other non-periodic carving
        /// can't make the two sides of the jump differ.
        /// </summary>
        static int EarthSeabedY(SeamParams p, ColumnCache cache, int x0, int z0, int lx, int lz, int seaLevel)
        {
            var earth = p.EarthPolar ?? throw new InvalidOperationException("This world's seam band follows Earth's seabed and can't be generated without Earth: Cylinder World's Earth adapter (SeamParams.EarthPolar) isn't bound.");
            return Math.Clamp(cache.EarthAround(earth, x0, z0, lx, lz).Top, 2, seaLevel - 2);
        }

        /// <summary>
        /// v6: at the inner edge of the seam band, move the ground from the flat seam seabed toward the natural terrain
        /// (blend 0 → 1), instead of the flat seabed ending in a hard, chunk-aligned step against natural terrain.
        /// The column ends up solid to exactly the target, so the profile is the lerp of the natural ground top.
        /// </summary>
        void BlendSeamColumn(IServerChunk[] chunks, IMapChunk mapChunk, int lx, int lz, int floorY, double blend, int mapSizeY, int seaLevel)
        {
            int ground = GroundTop(chunks, lx, lz, mapSizeY - 2);
            ShapeColumn(chunks, mapChunk, lx, lz, ground, (int)Math.Round(floorY + (ground - floorY) * blend), mapSizeY, seaLevel);
        }

        /// <summary>
        /// v10: the ramp from the band's identical part out to natural terrain. It starts at the wavy edge and gets longer
        /// the further the natural ground is from the band's seabed, so it stays at most 1 : 4 even where the terrain
        /// generator left land or a deep basin there (SeamParams.SeamRampWeight).
        /// </summary>
        /// <param name="edge">SeamEdge(wx, wz); <paramref name="floor"/> SeamFloorY(wx, wz). The target is SeamGround(wx, wz, ground).</param>
        void RampSeamColumn(IServerChunk[] chunks, IMapChunk mapChunk, SeamParams p, int lx, int lz, double wx, double edge, int floor, int mapSizeY, int seaLevel)
        {
            int ground = GroundTop(chunks, lx, lz, mapSizeY - 2);
            ShapeColumn(chunks, mapChunk, lx, lz, ground, p.SeamRampGround(wx, ground, edge, floor), mapSizeY, seaLevel);
        }

        /// <summary>Make the column solid to exactly `target` (natural top `ground`): fill below it, clear above it.</summary>
        void ShapeColumn(IServerChunk[] chunks, IMapChunk mapChunk, int lx, int lz, int ground, int target, int mapSizeY, int seaLevel)
        {
            const int cs = GlobalConstants.ChunkSize;
            if (target == ground) return;

            for (int y = 1; y < mapSizeY - 1; y++)
            {
                IChunkBlocks data = chunks[y / cs].Data;
                int idx = CellIndex(y, lz, lx);
                int id = SolidId(data, idx);
                bool soft = id == 0 || (id < clearAboveIce.Length && clearAboveIce[id]);

                if (y <= target)
                {
                    // Fill air, water and plants up to the target. When lowering too: the top solid block can be an
                    // overhang with sea or a cave under it, and the deeper seabed would show through as a step.
                    // The flat band fills its caves below the floor the same way.
                    if (soft || data.GetFluid(idx) != 0)
                    {
                        data[idx] = y == target && y < seaLevel ? gravelId : rockId;
                        data.SetFluid(idx, 0);
                    }
                }
                else if (target < ground)
                {
                    // Lower: clear terrain, plants and lake water above the target; sea water below sea level
                    if (id == 0 && data.GetFluid(idx) == (y < seaLevel ? saltWaterId : 0)) continue;
                    data.SetBlockAir(idx);
                    data.SetFluid(idx, y < seaLevel ? saltWaterId : 0);
                }
            }
            UpdateHeightmaps(chunks, mapChunk, lx, lz, mapSizeY);
        }
    }
}
