using System;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace CylinderWorld
{
    public partial class SeamWorldGen
    {
        /// <summary>
        /// Earth mode has no raised polar seabed, synthetic Antarctic front or terrain planing. Earth builds the
        /// continent first; we keep its shape, coat its glacier with our non-spawning ice, add low Arctic
        /// sea ice and build the original straight walls. Runs in both existing polar passes, idempotently.
        /// </summary>
        void BuildEarthPolarZone(IChunkColumnGenerateRequest request, SeamParams p)
        {
            var earth = p.EarthPolar ?? throw new InvalidOperationException("This world's polar layout is Earth's and can't be generated without Earth: Cylinder World's Earth adapter (SeamParams.EarthPolar) isn't bound.");
            int x0 = request.ChunkX * GlobalConstants.ChunkSize, z0 = request.ChunkZ * GlobalConstants.ChunkSize;
            bool anyWall = z0 < p.WallFaceMax || z0 + GlobalConstants.ChunkSize > p.WorldLength - p.WallFaceMax;
            if (!EarthPolarChunk(p, earth, z0)) return;
            var chunks = request.Chunks;
            var map = chunks[0].MapChunk;
            var cache = CacheOf(request);
            int height = sapi.WorldManager.MapSizeY;
            int wallTop = Math.Min(p.WallTopY, height - 2);
            int naturalGlacier = naturalGlacierId;

            // Face shape and wall shades depend only on (x, y): compute once per chunk column, if any wall is in it.
            double[,] faceOffset = null;
            int[,] shade = null;
            if (anyWall) WallFace(p, x0, wallTop, out faceOffset, out shade);

            for (int lz = 0; lz < GlobalConstants.ChunkSize; lz++)
            {
                double z = z0 + lz + 0.5;
                double dz = p.DistToZEdge(z);
                bool border = p.DistToWallFace(z) <= EarthPolarModel.LifelessBorder;
                for (int lx = 0; lx < GlobalConstants.ChunkSize; lx++)
                {
                    double x = x0 + lx + 0.5;
                    bool changed = false;
                    if (anyWall && dz < p.WallFaceMax)
                    {
                        for (int y = 1; y < height - 1; y++)
                            WallCell(chunks[y / GlobalConstants.ChunkSize].Data, CellIndex(y, lz, lx), y, lx, wallTop, dz, p.WallDepth, faceOffset, shade, ref changed);
                    }

                    // Between the nominal wall depth and WallFaceMax the face bulges out 0..18 blocks depending on x and y.
                    // The sea in front of a shallow bulge is frozen like the approach beyond it (open water was left
                    // there before), but without pressure ridges, which would rebuild wall blocks they meet as ice.
                    bool wallFoot = dz < p.WallFaceMax;
                    if (dz >= p.WallDepth)
                    {
                        // Earth generates columns at integer coordinates. Matching that sample matters at ice/coast edges.
                        var sample = cache.Earth(earth, x0, z0, lx, lz);
                        if (p.SeasonalSeaIce)
                        {
                            var kind = p.SeaIceAt(x, z, sample);
                            if (kind != SeaIceKind.Open)
                            {
                                changed |= FreezeEarthSea(chunks, lx, lz, p.SeaLevel,
                                    kind == SeaIceKind.Permanent ? permanentSeaIceId : seasonalSeaIceId, true,
                                    SeaIcePolicy.PermanentApproach(p, x, z) ? 2 : 1);
                                if (kind == SeaIceKind.Permanent && !wallFoot)
                                    changed |= BuildEarthPressureIce(chunks, lx, lz, p, sample, x, z, border, height);
                            }
                        }
                        else if (p.HasArcticIce(x, z, sample))
                        {
                            changed |= FreezeEarthSea(chunks, lx, lz, p.SeaLevel, border ? glacierIds[2] : floeId);
                            if (!wallFoot) changed |= BuildEarthPressureIce(chunks, lx, lz, p, sample, x, z, border, height);
                        }

                        bool antarcticIce = sample.Latitude <= EarthPolarModel.AntarcticLatitude && sample.Glacier;
                        if (!wallFoot && (border && !sample.Ocean || antarcticIce))
                        {
                            // Keep the exact surface elevation, including Earth Map's high Antarctic plateau.
                            // Do not turn uncovered Antarctic rock into a different landscape. The spawn-event
                            // guard also protects rock, snow and caves throughout Antarctica.
                            int surface = EarthSurface(chunks, lx, lz, height);
                            if (surface > 0 && (surface >= p.SeaLevel - 1 || antarcticIce))
                            {
                                // The old three-block cap left transparent vanilla ice on the cliff sides and
                                // underneath opaque tops. Use one opaque material throughout the Antarctic glacier;
                                // change only existing ice, keeping snow, bedrock, caves and every elevation intact.
                                int bottom = antarcticIce ? Math.Max(1, sample.IceBottom) : Math.Max(1, surface - 2);
                                var ringX = p.RingXAt(x);
                                for (int y = surface; y >= bottom; y--)
                                {
                                    var blocks = chunks[y / GlobalConstants.ChunkSize].Data;
                                    int i = CellIndex(y, lz, lx);
                                    int id = blocks.GetBlockId(i, BlockLayersAccess.Solid);
                                    if (id == 0 || blocks.GetFluid(i) != 0 || IsWall(id)) continue;
                                    // Glacier blocks in these land and Antarctic columns come only from this loop (sea ice
                                    // and pressure ridges are on ocean columns outside Antarctica), with the shade below
                                    // for their own (x, y, z): already converted in an earlier pass, nothing to recompute
                                    if (IsGlacier(id)) continue;
                                    if (id != naturalGlacier && !IsOwnIce(id) && (!border || surface - y >= 3)) continue;
                                    int ice = glacierIds[p.GlacierShadeAt(ringX, y, z)];
                                    if (id == ice) continue;
                                    blocks[i] = ice;
                                    changed = true;
                                }
                            }
                        }
                    }
                    if (changed) UpdateHeightmaps(chunks, map, lx, lz, height, clearAboveIce);
                }
            }
            // The wall and the border's surface conversion (top layers to glacier ice) can replace structure blocks
            RemoveOrphanedBlockEntities(chunks);
        }

        /// <summary>Earth mode: chunk rows at z0 the polar worldgen works in (the wall, Arctic sea ice, Antarctica).</summary>
        static bool EarthPolarChunk(SeamParams p, EarthPolarModel earth, int z0) =>
            z0 < p.WallFaceMax || z0 + GlobalConstants.ChunkSize > p.WorldLength - p.WallFaceMax
            || earth.Latitude(z0) >= EarthPolarModel.ArcticIceLatitude
            || earth.Latitude(z0 + GlobalConstants.ChunkSize) <= (p.SeasonalSeaIce ? EarthPolarModel.SeasonalSouthLatitude : EarthPolarModel.AntarcticLatitude);

        bool BuildEarthPressureIce(IServerChunk[] chunks, int lx, int lz, SeamParams p,
            EarthPolarModel.Column sample, double x, double z, bool border, int height)
        {
            int sea = p.SeaLevel;
            int baseIndex = CellIndex(sea - 1, lz, lx);
            // The top layer: in seasonal worlds the permanent sea ice under a ridge is a fluid
            if (!IsOwnIce(TopLayerId(chunks[(sea - 1) / GlobalConstants.ChunkSize].Data, baseIndex))) return false;
            int top = Math.Min(height - 5, sea - 1 + p.EarthIceRise(x, z, sample));
            bool changed = false;
            for (int y = sea; y <= top; y++)
            {
                var blocks = chunks[y / GlobalConstants.ChunkSize].Data;
                int i = CellIndex(y, lz, lx);
                int id = blocks.GetBlockId(i, BlockLayersAccess.Solid);
                if (id != 0 && !IsOwnIce(id) && sapi.World.Blocks[id].BlockMaterial != EnumBlockMaterial.Snow) break;
                // Identical light texture on either side of the invisible wildlife boundary.
                int ice = border ? glacierIds[2] : floeId;
                if (id == ice) continue;
                blocks[i] = ice;
                blocks.SetFluid(i, 0);
                changed = true;
            }
            return changed;
        }

        // Runs after vanilla's blanket snow, before neighbouring sunlight is flooded. It is a worldgen operation,
        // never a live cleanup: built terrain, player blocks and later weather accumulation are left alone.
        void FinishEarthSnow(IChunkColumnGenerateRequest request)
        {
            var p = core.Params;
            if (p?.EarthPolar == null || !p.EarthPolarIce || !p.HasWalls || !idsReady) return;
            int x0 = request.ChunkX * GlobalConstants.ChunkSize, z0 = request.ChunkZ * GlobalConstants.ChunkSize;
            if (p.EarthPolar.Latitude(z0) < EarthPolarModel.ArcticIceLatitude) return;
            int height = sapi.WorldManager.MapSizeY;
            var chunks = request.Chunks;
            var cache = CacheOf(request);
            int snow = snowBlockId;
            var layers = snowLayerIds;
            for (int lz = 0; lz < GlobalConstants.ChunkSize; lz++) for (int lx = 0; lx < GlobalConstants.ChunkSize; lx++)
            {
                double x = x0 + lx + 0.5, z = z0 + lz + 0.5;
                if (p.DistToZEdge(z) < p.WallDepth || !cache.Earth(p.EarthPolar, x0, z0, lx, lz).Ocean) continue;
                int top = EarthSurface(chunks, lx, lz, height);
                int idx = CellIndex(top, lz, lx);
                // At the wall's foot the first solid from above can be the wall itself (its top or an overhang)
                int topId = TopLayerId(chunks[top / GlobalConstants.ChunkSize].Data, idx);   // sea ice is a fluid in seasonal worlds
                if (!IsOwnIce(topId) || IsWall(topId)) continue;
                int eighths = p.EarthSnowEighths(x, z);
                bool changed = false;
                for (int dy = 1; dy <= 4 && top + dy < height - 1; dy++)
                {
                    int y = top + dy, i = CellIndex(y, lz, lx);
                    var blocks = chunks[y / GlobalConstants.ChunkSize].Data;
                    int id = blocks.GetBlockId(i, BlockLayersAccess.Solid);
                    if (blocks.GetFluid(i) != 0 || id != 0 && sapi.World.Blocks[id].BlockMaterial != EnumBlockMaterial.Snow) break;
                    int depth = Math.Clamp(eighths - (dy - 1) * 8, 0, 8);
                    int desired = depth == 8 ? snow : layers[depth];
                    if (id == desired) continue;
                    blocks[i] = desired;
                    changed = true;
                }
                if (changed) UpdateHeightmaps(chunks, chunks[0].MapChunk, lx, lz, height, clearAboveIce);
            }
        }

        /// <summary>Surface sea ice replacing only existing sea water/ice. No rock, land or seabed is changed.</summary>
        bool FreezeEarthSea(IServerChunk[] chunks, int lx, int lz, int sea, int ice, bool fluidIce = false, int thickness = 2)
        {
            if (ice == 0) ice = glacierIds[2];
            int above = CellIndex(sea, lz, lx);
            int aboveId = TopLayerId(chunks[sea / GlobalConstants.ChunkSize].Data, above);
            // Sea under an overhang of the wall face freezes too; other solids above mean land or a structure
            if (aboveId != 0 && !(aboveId < clearAboveIce.Length && clearAboveIce[aboveId]) && !IsWall(aboveId)) return false;
            bool changed = false;
            for (int y = sea - 1; y >= sea - thickness && y > 0; y--)
            {
                var blocks = chunks[y / GlobalConstants.ChunkSize].Data;
                int i = CellIndex(y, lz, lx);
                // The chunk indexer returns the fluid when no solid exists. Read the solid layer explicitly.
                int id = blocks.GetBlockId(i, BlockLayersAccess.Solid), fluid = blocks.GetFluid(i);
                // A wall bulge at the waterline stays wall; the sea under it still freezes
                if (IsWall(id)) continue;
                if (!(id == 0 && (fluid == saltWaterId || fluid == lakeIceId || fluid == ice) || id == lakeIceId || IsOwnIce(id))) break;
                if (fluidIce)
                {
                    if (id == 0 && fluid == ice) continue;
                    blocks.SetBlockAir(i);
                    blocks.SetFluid(i, ice);
                    changed = true;
                    continue;
                }
                if (id == ice && fluid == 0) continue;
                blocks[i] = ice;
                blocks.SetFluid(i, 0);
                changed = true;
            }
            return changed;
        }

        int EarthSurface(IServerChunk[] chunks, int lx, int lz, int height)
        {
            for (int y = height - 2; y > 0; y--)
            {
                var blocks = chunks[y / GlobalConstants.ChunkSize].Data;
                int i = CellIndex(y, lz, lx);
                int id = blocks.GetBlockId(i, BlockLayersAccess.Solid);
                if (id == 0 && (blocks.GetFluid(i) == seasonalSeaIceId || blocks.GetFluid(i) == permanentSeaIceId)) return y;
                if (id != 0 && !(id < clearAboveIce.Length && clearAboveIce[id])) return y;
            }
            return 0;
        }
    }
}
