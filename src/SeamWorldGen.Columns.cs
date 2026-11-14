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
    // SeamWorldGen, part: Helpers for one column of a chunk, shared by the band and the polar zone: ground tops and heightmaps. (CW-0019: moved unchanged from SeamWorldGen.cs.)
    public partial class SeamWorldGen
    {
        /// <summary>
        /// The solid block at idx, 0 if there is none. The chunk indexer (data[idx]) returns the fluid when the solid
        /// layer is empty, so with it sea water reads as ground and open water as never empty.
        /// </summary>
        static int SolidId(IChunkBlocks data, int idx) => data.GetBlockId(idx, BlockLayersAccess.Solid);

        /// <summary>
        /// The chunk indexer: the solid block, else the fluid (0 only for a cell with neither). Where code reads this
        /// instead of SolidId it relies on the fluid fallback (sea ice, which is a fluid in seasonal worlds, counts as a
        /// block there), so don't "fix" it to SolidId.
        /// </summary>
        static int TopLayerId(IChunkBlocks data, int idx) => data[idx];

        /// <summary>Index of (lx, y, lz) in its 32³ chunk (chunks[y / 32]).</summary>
        static int CellIndex(int y, int lz, int lx) => ((y % GlobalConstants.ChunkSize) * GlobalConstants.ChunkSize + lz) * GlobalConstants.ChunkSize + lx;

        bool IsOwnIce(int id) => id > 0 && id < ownIce.Length && ownIce[id];

        bool IsWall(int id) => id >= 0 && id < wallBlock.Length && wallBlock[id];

        bool IsGlacier(int id) => id > 0 && id < glacierBlock.Length && glacierBlock[id];

        /// <summary>Highest natural ground in this column: solid, not vegetation or snow, not our own ice (0 if none).</summary>
        int NaturalGroundTop(IServerChunk[] chunks, int lx, int lz, bool seabed = false)
            => TopGround(chunks, lx, lz, sapi.WorldManager.MapSizeY - 2, true, seabed);

        /// <summary>Highest solid, non-vegetation block at or below fromY in this column, skipping ground with sea water
        /// under it (TopGround's seabed rule; 0 if none).</summary>
        int GroundTop(IServerChunk[] chunks, int lx, int lz, int fromY)
            => TopGround(chunks, lx, lz, fromY, false, true);

        /// <summary>A run of ground this thick is the seabed, whatever is under it (ends the seabed search early).</summary>
        const int SolidSeabed = 24;

        /// <param name="seabed">Skip ground with sea water anywhere under it before the seabed (a structure the terrain
        /// generator left over deep water, dry rooms inside it included): the ground the seabed is raised or ramped from
        /// is further down. Dry gaps (rooms, caves under the seabed) don't count.</param>
        int TopGround(IServerChunk[] chunks, int lx, int lz, int fromY, bool notOwnIce, bool seabed)
        {
            const int cs = GlobalConstants.ChunkSize;
            int top = 0, runTop = 0;
            bool sea = false;
            for (int y = Math.Min(fromY, sapi.WorldManager.MapSizeY - 2); y > 0; y--)
            {
                IChunkBlocks data = chunks[y / cs].Data;
                int idx = CellIndex(y, lz, lx);
                int id = SolidId(data, idx);
                if (id != 0 && !(id < clearAboveIce.Length && clearAboveIce[id]) && !(notOwnIce && IsOwnIce(id)))
                {
                    if (!seabed) return y;
                    if (top == 0 || sea) top = y;
                    if (runTop == 0) runTop = y;
                    sea = false;
                    if (runTop - y >= SolidSeabed) return top;
                }
                else
                {
                    runTop = 0;
                    if (top != 0 && data.GetFluid(idx) == saltWaterId) sea = true;
                }
            }
            return sea ? 0 : top;
        }

        /// <summary>The rain and terrain heightmaps of one column after a rewrite, and the map chunk's YMax.</summary>
        /// <param name="notTerrain">By block id: solid blocks the terrain height passes over (Earth mode's polar worldgen:
        /// plants and loose cover, clearAboveIce); null counts every solid block (CW-0021: one function for both).</param>
        static void UpdateHeightmaps(IServerChunk[] chunks, IMapChunk mapChunk, int lx, int lz, int mapSizeY, bool[] notTerrain = null)
        {
            const int cs = GlobalConstants.ChunkSize;
            int mi = lz * cs + lx;
            bool rainSet = false;
            for (int y = mapSizeY - 2; y > 0; y--)
            {
                IChunkBlocks data = chunks[y / cs].Data;
                int idx = CellIndex(y, lz, lx);
                int id = SolidId(data, idx);
                bool solid = id != 0;
                if (!rainSet && (solid || data.GetFluid(idx) != 0))
                {
                    mapChunk.RainHeightMap[mi] = (ushort)y;
                    // Worldgen sunlight fills every chunk above YMax with full light; raised walls and ice must count
                    mapChunk.YMax = Math.Max(mapChunk.YMax, (ushort)y);
                    rainSet = true;
                }
                if (solid && !(notTerrain != null && id < notTerrain.Length && notTerrain[id]))
                {
                    mapChunk.WorldGenTerrainHeightMap[mi] = (ushort)y;
                    return;
                }
            }
        }
    }
}
