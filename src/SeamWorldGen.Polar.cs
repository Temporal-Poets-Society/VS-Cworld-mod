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
    // SeamWorldGen, part: The polar zone in chunk columns: the wall face, the wall, apron, ice sheet and floes. (CW-0019: moved unchanged from SeamWorldGen.cs.)
    public partial class SeamWorldGen
    {
        /// <summary>Per worldgen thread: the wall face of the last chunk x it was computed for (both passes, and every
        /// wall chunk along z, share it), in buffers reused instead of ~75 KB per wall chunk and pass.</summary>
        [ThreadStatic] static double[,] faceBuffer;
        [ThreadStatic] static int[,] shadeBuffer;
        [ThreadStatic] static SeamParams faceParams;
        [ThreadStatic] static int faceX0, faceTop;

        /// <summary>Face offset and wall shade for x0 + 0..31 and y 1..top (indices [lx, y]; higher y are stale).</summary>
        static void WallFace(SeamParams p, int x0, int top, out double[,] faceOffset, out int[,] shade)
        {
            const int cs = GlobalConstants.ChunkSize;
            if (faceBuffer == null || faceBuffer.GetLength(1) < top + 1)
            {
                faceBuffer = new double[cs, top + 1];
                shadeBuffer = new int[cs, top + 1];
                faceParams = null;
            }
            faceOffset = faceBuffer;
            shade = shadeBuffer;
            if (faceParams == p && faceX0 == x0 && faceTop == top) return;
            for (int lx = 0; lx < cs; lx++)
            {
                double wx = x0 + lx + 0.5;
                for (int y = 1; y <= top; y++)
                {
                    faceOffset[lx, y] = p.WallFaceOffset(wx, y);
                    shade[lx, y] = p.WallShade(wx, y);
                }
            }
            faceParams = p;
            faceX0 = x0;
            faceTop = top;
        }

        /// <summary>
        /// The polar zone at each Z edge: the unbreakable wall (up to WallTopY), then the glacier in front of it (fills
        /// water and valleys with glacier ice, clears vegetation above it; hills may poke through), then ice floes.
        /// v5 adds a continental shelf (seabed rising toward the wall), lets the ice float on water near its noisy
        /// front and end in a low ice cliff, and uses permanent floes. See SeamParams.GetPolarColumn.
        /// </summary>
        /// <param name="spill">Only the seam band's exact-floor columns again, after neighbours' vegetation (ClearSeamSpill).</param>
        void BuildPolarZone(IChunkColumnGenerateRequest request, SeamParams p, bool firstPass, bool spill = false)
        {
            const int cs = GlobalConstants.ChunkSize;
            int x0 = request.ChunkX * cs;
            if (p.EarthPolarIce)
            {
                // Spill pass: FlattenSeam has just reset the band's sea to open water, sea ice and pressure ridges
                // included (0.11.8-0.11.9 left it so, and the live sea-ice system refroze it row by row). Freeze it again.
                if (!spill || x0 < p.SeamWorkReach || x0 + cs > p.WorldWidth - p.SeamWorkReach) BuildEarthPolarZone(request, p);
                return;
            }
            int z0 = request.ChunkZ * cs;
            if (z0 >= p.PolarZoneDepth && z0 + cs <= p.WorldLength - p.PolarZoneDepth) return;
            if (spill && x0 >= p.SeamHalfWidth && x0 + cs <= p.WorldWidth - p.SeamHalfWidth) return;

            IServerChunk[] chunks = request.Chunks;
            IMapChunk mapChunk = chunks[0].MapChunk;
            int mapSizeY = sapi.WorldManager.MapSizeY;
            int top = Math.Min(p.WallTopY, mapSizeY - 2);
            int seaLevel = sapi.World.SeaLevel;

            // Face shape and wall shades depend only on (x, y): compute once per chunk column, if any wall is in it.
            bool anyWall = z0 < p.WallFaceMax || z0 + cs > p.WorldLength - p.WallFaceMax;
            double[,] faceOffset = null;
            int[,] shade = null;
            if (anyWall) WallFace(p, x0, top, out faceOffset, out shade);

            bool v5 = p.Version >= 5;
            bool plane = p.Version >= 9;
            bool natural10 = p.Version >= 10;
            ColumnCache cache = null;
            // The ring position and sheet edge depend on x alone: once per x, not per column and ice cell
            Span<SeamParams.RingX> ring = stackalloc SeamParams.RingX[cs];
            for (int lx = 0; lx < cs; lx++) ring[lx] = p.RingXAt(x0 + lx + 0.5);

            for (int lz = 0; lz < cs; lz++)
            {
                double wz = z0 + lz + 0.5;
                double dz = p.DistToZEdge(wz);
                if (dz >= p.PolarZoneDepth) continue;

                for (int lx = 0; lx < cs; lx++)
                {
                    double wx = x0 + lx + 0.5;
                    bool inBand = p.InSeamBand(wx);
                    // the spill pass only rewrites exact-floor columns, which are all in the band (v5+)
                    if (spill && !(v5 && inBand)) continue;
                    ref readonly var ringX = ref ring[lx];
                    var col = p.GetPolarColumnAt(ringX, wz, out double sheetCoord);
                    int iceTop = Math.Min(col.IceTop, top);
                    int iceBottom = col.IceBottom;
                    int cut = int.MaxValue;   // solid blocks above this are removed (terrain planed under the ice sheet)
                    // In the seam band the seabed must be identical on both sides of the jump, so it's set exactly,
                    // blending from the flat seam seabed. Elsewhere it's only raised, blending from the natural seabed.
                    // Through the band's blend strip (v6+) the exact floor eases into the outside rule, or the two
                    // meet in a cliff at the band's inner edge. Those columns are set in the first pass only: in the
                    // second their ground is already the floor, and recomputing would push it further.
                    // v10: the band's seabed has relief and a wavy edge, and the ramp lengthens with the height it covers,
                    // ending at the band's inner edge at the latest (outside it, the outside rule takes over)
                    double seamBlend = 0, edge = 0;
                    bool exactFloor = false;
                    if (v5 && col.Floor > 0 && inBand)
                    {
                        if (natural10) edge = p.SeamEdge(wx, wz);
                        seamBlend = natural10 ? (p.DistToEdge(wx) >= edge ? 1 : 0) : p.SeamBlendWeight(wx);
                        exactFloor = seamBlend == 0 || firstPass;
                    }
                    if (spill && !exactFloor) continue;
                    int floor = -1;
                    if (col.Floor > 0)
                    {
                        int seamFloor = exactFloor ? BandFloor(p, cache ??= CacheOf(request), x0, z0, lx, lz, seaLevel) : 0;
                        int baseFloor = exactFloor ? seamFloor : GroundTop(chunks, lx, lz, col.Floor);
                        if (exactFloor)
                        {
                            floor = seamFloor + (int)Math.Round((col.Floor - seamFloor) * col.ShelfBlend);
                            if (seamBlend > 0)
                            {
                                // What the outside rule leaves: natural ground (even above the shelf) raised toward the
                                // shelf, then planed like the neighbouring columns outside the band
                                int natural = NaturalGroundTop(chunks, lx, lz, true);
                                int outside = natural < col.Floor ? natural + (int)Math.Round((col.Floor - natural) * col.ShelfBlend) : natural;
                                int outsideCap = plane && dz >= p.WallFaceMax ? p.PlaneCapAt(ringX, wz, sheetCoord) : int.MaxValue;
                                if (outsideCap != int.MaxValue) outside = Math.Min(outside, Math.Min(Math.Max(outsideCap, col.IceTop), top) - SeamParams.IceCover);
                                if (natural10) seamBlend = p.SeamRampWeightAt(wx, edge, outside - floor, p.SeamHalfWidth - edge);
                                floor = (int)Math.Round(floor + (outside - floor) * seamBlend);
                            }
                        }
                        else if (baseFloor < col.Floor) floor = baseFloor + (int)Math.Round((col.Floor - baseFloor) * col.ShelfBlend);
                        if (!exactFloor && floor <= baseFloor) floor = -1;
                        if (!exactFloor && !firstPass && p.Version >= 6) floor = -1;   // already raised in the first pass
                    }

                    int rawCap = plane && firstPass && !exactFloor && dz >= p.WallFaceMax ? p.PlaneCapAt(ringX, wz, sheetCoord) : int.MaxValue;
                    if (rawCap != int.MaxValue)
                    {
                        // Nothing natural sticks out of the sheet: terrain above the cap is planed off, and all ground on
                        // the sheet (and planed tops beyond it) gets an ice mantle; deep on the sheet the cap is its own ice
                        // surface, so buried mountains end flush. First pass only: afterwards the ice is in place, and
                        // loose stones or boulders on it mustn't count as ground.
                        int cap = Math.Min(Math.Max(rawCap, col.IceTop), top);
                        int ground = Math.Max(NaturalGroundTop(chunks, lx, lz), floor);
                        int capped = Math.Min(ground, cap - SeamParams.IceCover);
                        if (ground > capped) cut = capped;
                        if (col.IceTop >= 0 || ground > capped)
                        {
                            int mantleTop = Math.Min(capped + SeamParams.IceCover, cap);
                            if (mantleTop > iceTop)
                            {
                                if (iceTop < 0) iceBottom = capped + 1;
                                iceTop = mantleTop;
                            }
                        }
                    }
                    bool changed = false;
                    // Under the jump line the band's mantle curtain runs under the polar zone's exact floor too
                    bool curtain = exactFloor && p.DistToJumpLine(wx) <= SeamParams.CurtainHalfWidth;

                    for (int y = 1; y < mapSizeY - 1; y++)
                    {
                        IChunkBlocks data = chunks[y / cs].Data;
                        int idx = CellIndex(y, lz, lx);
                        int id = SolidId(data, idx);
                        bool soft = id == 0 || (id < clearAboveIce.Length && clearAboveIce[id]);   // air, water or vegetation
                        bool water = id == 0 && data.GetFluid(idx) != 0;

                        // The wall, and above the plateau nothing may stick out (mountain tops, trees). Before the planing
                        // rule: above the top that rule leaves the same air (the ice it refills ends at the top).
                        if (anyWall && WallCell(data, idx, y, lx, top, dz, p.WallDepth, faceOffset, shade, ref changed)) continue;
                        if (curtain && y < floor)
                        {
                            if (id == mantleId && data.GetFluid(idx) == 0) continue;
                            data[idx] = mantleId;
                            data.SetFluid(idx, 0);
                            changed = true;
                            continue;
                        }
                        if (y > cut && (!soft && !IsOwnIce(id) || water))
                        {
                            // Planed terrain (and lakes on it): removed, then refilled with ice up to the mantle top
                            data.SetBlockAir(idx);
                            data.SetFluid(idx, 0);
                            if (y >= iceBottom && y <= iceTop) data[idx] = glacierIds[p.GlacierShadeAt(ringX, y, wz)];
                            changed = true;
                        }
                        else if (floor > 0 && y <= floor && (soft || data.GetFluid(idx) != 0 || (exactFloor && y == floor)))
                        {
                            // Continental shelf: raise the seabed (never lowers ground that is already higher)
                            data[idx] = y == floor ? gravelId : rockId;
                            data.SetFluid(idx, 0);
                            changed = true;
                        }
                        else if (exactFloor && y > floor)
                        {
                            // Seam band: every cell above the shelf floor is set (glacier, sea or air), so both sides
                            // match, whatever terrain, caves or structures were there (wood and dry caves included)
                            bool ice = iceTop >= 0 && y >= col.IceBottom && y <= iceTop;
                            int wantId = ice ? glacierIds[p.GlacierShadeAt(ringX, y, wz)] : 0;
                            int wantFluid = !ice && y < seaLevel ? saltWaterId : 0;
                            if (id == wantId && data.GetFluid(idx) == wantFluid) continue;
                            if (wantId == 0) data.SetBlockAir(idx); else data[idx] = wantId;
                            data.SetFluid(idx, wantFluid);
                            changed = true;
                        }
                        else if (iceTop >= 0 && y >= iceBottom && y <= iceTop)
                        {
                            // Glacier / ice shelf: fills water, air and vegetation; solid ground stays
                            if (soft || data.GetFluid(idx) != 0)
                            {
                                data[idx] = glacierIds[p.GlacierShadeAt(ringX, y, wz)];
                                data.SetFluid(idx, 0);
                                changed = true;
                            }
                        }
                        else if (iceTop >= 0 && y > iceTop)
                        {
                            // Above the ice surface: remove trees, plants and old snow (hills stay, poking through)
                            if (id != 0 && soft)
                            {
                                data.SetBlockAir(idx);
                                changed = true;
                            }
                            if (y >= seaLevel && data.GetFluid(idx) != 0)
                            {
                                data.SetFluid(idx, 0);
                                changed = true;
                            }
                        }
                    }

                    if (col.Floe) changed |= PlaceFloe(chunks, lx, lz, seaLevel, v5, p.Version);
                    if (changed) UpdateHeightmaps(chunks, mapChunk, lx, lz, mapSizeY);
                }
            }

            // The wall, planing, the seam-band shelf and ice filling over wooden blocks can replace a block entity's block
            RemoveOrphanedBlockEntities(chunks);
        }

        /// <summary>
        /// One cell of a column next to the wall (both polar worldgens): a wall block up to the wall's top wherever the rough
        /// face reaches (its offset and shade at this x and y from WallFace), and nothing at all above the top over the
        /// wall's nominal depth (mountain tops, trees, vanilla's snow). False for the cells outside both, which the caller's
        /// own rules handle.
        /// </summary>
        bool WallCell(IChunkBlocks data, int idx, int y, int lx, int wallTop, double dz, int wallDepth, double[,] faceOffset, int[,] shade, ref bool changed)
        {
            if (y <= wallTop && dz < wallDepth + faceOffset[lx, y])
            {
                data[idx] = wallIds[shade[lx, y]];
                data.SetFluid(idx, 0);
                changed = true;
                return true;
            }
            if (y > wallTop && dz < wallDepth)
            {
                if (TopLayerId(data, idx) == 0 && data.GetFluid(idx) == 0) return true;
                data.SetBlockAir(idx);
                data.SetFluid(idx, 0);
                changed = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// A sea ice floe on open water at sea level - 1. v5: a permanent glacier-ice block (vanilla lake ice melts above
        /// +2°C into fresh water, even at sea). v4 worlds keep the lake ice they were generated with.
        /// </summary>
        bool PlaceFloe(IServerChunk[] chunks, int lx, int lz, int seaLevel, bool v5, int version)
        {
            const int cs = GlobalConstants.ChunkSize;
            int y = seaLevel - 1;
            IChunkBlocks data = chunks[y / cs].Data;
            int idx = CellIndex(y, lz, lx);
            IChunkBlocks dataAbove = chunks[(y + 1) / cs].Data;
            int above = CellIndex(y + 1, lz, lx);

            bool openWater = SolidId(data, idx) == 0 && data.GetFluid(idx) != 0 && data.GetFluid(idx) != lakeIceId
                && SolidId(dataAbove, above) == 0 && dataAbove.GetFluid(above) == 0;
            if (!openWater) return false;

            if (v5)
            {
                // v8: floe blocks (polar bears may spawn on them); v5-v7: plain light glacier ice
                data[idx] = version >= 8 && floeId != 0 ? floeId : glacierIds[2];
                data.SetFluid(idx, 0);
            }
            else if (lakeIceId != 0)
            {
                data.SetFluid(idx, lakeIceId);
            }
            return true;
        }
    }
}
