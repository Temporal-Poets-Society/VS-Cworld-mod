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
    /// <summary>
    /// Worldgen for the seam and the polar walls:
    ///  1. Map regions: force oceanicity near both X edges and suppress upheaval (no islands); thin out forests and
    ///     shrubs toward the walls (barren polar zone).
    ///  2. Chunk columns: flatten the seam band into an identical flat seabed + salt water, with an unbreakable
    ///     mantle "curtain" under each jump line so nobody tunnels across. Runs twice:
    ///     - TerrainFeatures pass: before anyone's vegetation, so no trees/bushes ever grow in the band. (Flattening
    ///       only after vegetation left floating canopies: a neighbour column's trees overhang into an already
    ///       flattened column, then the neighbour gets flattened and only the overhang survives.)
    ///     - Vegetation pass: clears seaweed etc. and anything neighbours' structures spilled in. All neighbours have
    ///       finished TerrainFeatures by then, and vanilla sunlight (GenLight, 0.95) runs after us, so lighting is correct.
    ///     - NeighbourSunLightFlood pass: the band's identical part again, from its floor up (the polar zone's part of
    ///       it too). A neighbour's Vegetation pass can run after ours and spill its block patches (kelp, seagrass,
    ///       seaweed) over the border; by this pass every neighbour has finished Vegetation. The sunlight was computed
    ///       before the spill, for the clear band. The mantle curtain is laid again from the bottom up: Earth's rock floor
    ///       (Vegetation, 0.7) makes everything below its top plain rock.
    ///     Where the seabed is raised or ramped, ground with sea water under it before the seabed (a structure the terrain
    ///     generator left over deep water) doesn't count as ground, or it would become a rock column (TopGround).
    ///  3. Polar zone at both Z edges, in the same two passes (after the flatten, which skips wall/apron columns):
    ///     the unbreakable ice wall, the glacier apron in front of it, and pack ice beyond.
    ///  4. Climate: wraps vanilla's climate map generator (see InstallPolarClimate) and replaces latitude (InstallLatitude).
    ///  5. Moved spawn: forced land there, and the story locations moved along with it.
    /// ExecuteOrder 0.6 = after vanilla GenMaps (0.1), GenStoryStructures (0.2), GenStructures (0.3) and
    /// GenVegetationAndPatches (0.5), so within each init step and pass our handlers run after theirs.
    /// </summary>
    public partial class SeamWorldGen : ModSystem
    {
        ICoreServerAPI sapi;
        CylinderWorldCore core;
        int rockId, gravelId, mantleId, saltWaterId, freshWaterId, lakeIceId, floeId;
        int seasonalSeaIceId, permanentSeaIceId, naturalGlacierId, snowBlockId;
        /// <summary>CW-D31: the surface of Earth's land in the band (SeamLandColumn).</summary>
        int soilId, soilSparseId, soilBareId, sandId;
        int[] tallGrassIds;
        /// <summary>Indexed by snow depth in eighths (1..7); [0] = 0, no snow.</summary>
        int[] snowLayerIds;
        /// <summary>Indexed by SeamParams.WallShade: ice, snow, light, dark, deep.</summary>
        int[] wallIds;
        /// <summary>Indexed by SeamParams.GlacierShade: ice, -, light, dark.</summary>
        int[] glacierIds;
        /// <summary>Block ids that are vegetation or loose cover (cleared above the apron surface).</summary>
        bool[] clearAboveIce;
        /// <summary>By block id: this mod's own ice (glacier, floe, sea ice, wall), the wall's blocks, the glacier's shades,
        /// and the own ice with the Stone material (glacier, floe, wall: CW-D13), which vanilla's rivulets take for rock.</summary>
        bool[] ownIce, wallBlock, glacierBlock, stoneIce;
        /// <summary>The ids stoneIce marks, to look up in a chunk's palette (ClearRivuletsInIce).</summary>
        int[] stoneIceIds;
        /// <summary>The sources vanilla's rivulets place (GlobalConfig waterBlockCode and lavaBlockCode).</summary>
        int lavaId;

        public override double ExecuteOrder() => 0.6;
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            ResolveBlockEntityCleanup();

            api.Event.InitWorldGenerator(InitWorldGen, "standard");
            api.Event.MapRegionGeneration(OnMapRegionGen, "standard");
            api.Event.ChunkColumnGeneration(req => OnChunkColumnGen(req, true), EnumWorldGenPass.TerrainFeatures, "standard");
            api.Event.ChunkColumnGeneration(req => OnChunkColumnGen(req, false), EnumWorldGenPass.Vegetation, "standard");
            api.Event.ChunkColumnGeneration(ClearSeamSpill, EnumWorldGenPass.NeighbourSunLightFlood, "standard");
            // Put full snowdrifts in before local lighting; then remove vanilla's extra blanket afterwards.
            api.Event.ChunkColumnGeneration(FinishEarthSnow, EnumWorldGenPass.Vegetation, "standard");
            api.Event.ChunkColumnGeneration(FinishEarthSnow, EnumWorldGenPass.NeighbourSunLightFlood, "standard");
            api.Event.ChunkColumnGeneration(ForgetColumnCache, EnumWorldGenPass.NeighbourSunLightFlood, "standard");
            // CW-D49: rivulet sources saved in the polar ice before this cleanup existed, taken out once per map chunk
            api.Event.ChunkColumnLoaded += OnColumnLoaded;
            api.Event.RegisterGameTickListener(CleanOldColumns, 250);
        }

        /// <summary>Set once the block ids and tables are in place (once per session: block ids never change in one); a world
        /// with this mod's layout isn't generated without them (Refuse, RequireIds).</summary>
        volatile bool idsReady;

        void InitWorldGen()
        {
            // The block ids first: every chunk pass needs them. Without them a world with Cylinder World's layout isn't
            // generated at all, as vanilla would only log the exception and go on generating chunks that never get the band,
            // curtain and walls. A world without the layout (the mod off for it) generates nothing of this mod anyway.
            try { InitBlockIds(); }
            catch (Exception e) when (core.Params != null) { Refuse("Cylinder World cannot generate this world: its block ids could not be set up (" + e.Message + ").", e); }
            catch (Exception e) { Mod.Logger.Error("Cylinder World: worldgen block ids could not be set up: {0}", e); }
            // A world whose band keeps Earth's lakes and rivers (EarthSeamWater) isn't generated with an Earth whose columns
            // don't say where their water is: its new band chunks would get dry beds beside the water of earlier chunks and
            // of the other lap, the divergence CW-D42 fixed the flag at creation to avoid
            if (core.Params is { EarthSeamWater: true, EarthPolar: { HasWaterTop: false } earth })
                Refuse("Cylinder World cannot generate this world: " + earth.WaterTopMismatch);
            // Each further step on its own: a game update that breaks one (a missing vanilla member fails when its method is
            // compiled, and vanilla's TriggerInitWorldGen then skips the rest of this handler) loses only that step
            Step("planet latitude", InstallLatitude);
            Step("polar climate", InstallPolarClimate);
            Step("story locations moved with spawn", ShiftStoryLocations);  // before ForceLandAtSpawn: it removes stale forced land around the old story spots
            Step("forced land at spawn", ForceLandAtSpawn);
        }

        void Step(string what, Action step)
        {
            try { step(); }
            catch (Exception e) { Mod.Logger.Error("Cylinder World: worldgen setup step '{0}' failed on this game version and is skipped: {1}", what, e); }
        }

        /// <summary>
        /// Stops the server before this world generates a chunk without Cylinder World's layout (the band identical on both
        /// laps, the mantle curtain, the walls): such chunks would be saved so for good. Vanilla only logs an exception from
        /// world generation's set-up and goes on generating, so the server is stopped, as Earth does in the same case; the
        /// exception then ends this set-up. <paramref name="reason"/> is a complete sentence.
        /// </summary>
        void Refuse(string reason, Exception cause = null)
        {
            string message = reason + " The server stops, so that no chunk of this world is generated without its seam band, mantle curtain and polar walls.";
            // (vanilla logs the exception thrown below with its cause)
            Mod.Logger.Error("{0}", message);
            sapi.Server.ShutDown();
            throw new InvalidOperationException(message, cause);
        }

        /// <summary>
        /// The chunk passes' params, null where this mod generates nothing. If the world has the layout but no block ids (its
        /// set-up was refused and the server is stopping), each chunk that still generates says so in the worldgen log
        /// instead of passing silently without the band, curtain and walls.
        /// </summary>
        SeamParams RequireIds()
        {
            var p = core.Params;
            if (p != null && !idsReady) throw new InvalidOperationException("Cylinder World: this chunk is generated without the seam band, mantle curtain and polar walls: the worldgen block ids were not set up (see the error at the start).");
            return p;
        }

        void InitBlockIds()
        {
            // /wgen regen sets world generation up again: the block ids and tables of this session still hold
            if (idsReady) return;
            rockId = BlockId("game:rock-granite");
            gravelId = BlockId("game:gravel-granite");
            mantleId = BlockId("game:mantle");
            saltWaterId = BlockId("game:saltwater-still-7");
            freshWaterId = BlockId("game:water-still-7");
            lakeIceId = BlockId("game:lakeice");
            floeId = BlockId("cylinderworld:glacier-floe");
            seasonalSeaIceId = BlockId("cylinderworld:seaice-seasonal");
            permanentSeaIceId = BlockId("cylinderworld:seaice-permanent");
            naturalGlacierId = BlockId("game:glacierice");
            soilId = BlockId("game:soil-low-normal");
            soilSparseId = BlockId("game:soil-low-sparse");
            soilBareId = BlockId("game:soil-low-none");
            sandId = BlockId("game:sand-granite");
            tallGrassIds = new[] { BlockId("game:tallgrass-short-free"), BlockId("game:tallgrass-mediumshort-free"), BlockId("game:tallgrass-medium-free") };
            snowBlockId = BlockId("game:snowblock");
            snowLayerIds = new int[8];
            for (int i = 1; i < 8; i++) snowLayerIds[i] = BlockId("game:snowlayer-" + i);
            int wallIce = BlockId("cylinderworld:wallice-ice");
            wallIds = new[] { wallIce, BlockId("cylinderworld:wallice-snow"), BlockId("cylinderworld:wallice-light"), BlockId("cylinderworld:wallice-dark"), BlockId("cylinderworld:wallice-deep") };
            for (int i = 0; i < wallIds.Length; i++) if (wallIds[i] == 0) wallIds[i] = wallIce;
            int glacierIce = BlockId("cylinderworld:glacier-ice");
            glacierIds = new[] { glacierIce, glacierIce, BlockId("cylinderworld:glacier-light"), BlockId("cylinderworld:glacier-dark") };
            for (int i = 0; i < glacierIds.Length; i++) if (glacierIds[i] == 0) glacierIds[i] = glacierIce;

            clearAboveIce = new bool[sapi.World.Blocks.Count];
            foreach (var block in sapi.World.Blocks)
            {
                if (block == null || block.Id <= 0 || block.Id >= clearAboveIce.Length) continue;
                var m = block.BlockMaterial;
                clearAboveIce[block.Id] = m == EnumBlockMaterial.Wood || m == EnumBlockMaterial.Leaves || m == EnumBlockMaterial.Plant || m == EnumBlockMaterial.Snow;
            }
            ownIce = new bool[sapi.World.Blocks.Count];
            wallBlock = new bool[sapi.World.Blocks.Count];
            // A missing wall block leaves id 0 in wallIds; IsWall(0) then holds, as Array.IndexOf did before
            foreach (int id in wallIds) { wallBlock[id] = true; if (id != 0) ownIce[id] = true; }
            glacierBlock = new bool[sapi.World.Blocks.Count];
            foreach (int id in glacierIds) if (id != 0) ownIce[id] = glacierBlock[id] = true;
            foreach (int id in new[] { floeId, seasonalSeaIceId, permanentSeaIceId }) if (id != 0) ownIce[id] = true;
            stoneIce = new bool[sapi.World.Blocks.Count];
            foreach (int id in glacierIds) if (id != 0) stoneIce[id] = true;
            foreach (int id in wallIds) if (id != 0) stoneIce[id] = true;
            if (floeId != 0) stoneIce[floeId] = true;
            var stoneIds = new List<int>();
            for (int id = 1; id < stoneIce.Length; id++) if (stoneIce[id]) stoneIds.Add(id);
            stoneIceIds = stoneIds.ToArray();
            lavaId = BlockId("game:lava-still-7");
            idsReady = true;
        }

        int BlockId(string code)
        {
            var block = sapi.World.GetBlock(new AssetLocation(code));
            if (block == null) Mod.Logger.Error("Block {0} not found, seam/wall worldgen will be incomplete", code);
            return block?.Id ?? 0;
        }

        /// <summary>
        /// Vanilla only guarantees land around the map center. Add the moved spawn to the same list (GenMaps.requireLandAt,
        /// in ocean-map pixels). It's rebuilt by vanilla on every start, so this runs every start too.
        /// </summary>
        void ForceLandAtSpawn()
        {
            var p = core.Params;
            var genMaps = sapi.ModLoader.GetModSystem<GenMaps>();
            if (p == null || p.SpawnZ <= 0 || genMaps?.requireLandAt == null || genMaps.noiseSizeOcean <= 0) return;

            int regionSize = sapi.WorldManager.RegionSize;
            int r = 6 * GlobalConstants.ChunkSize;
            int minX = (p.SpawnX - r) * genMaps.noiseSizeOcean / regionSize, maxX = (p.SpawnX + r) * genMaps.noiseSizeOcean / regionSize;
            int minZ = (p.SpawnZ - r) * genMaps.noiseSizeOcean / regionSize, maxZ = (p.SpawnZ + r) * genMaps.noiseSizeOcean / regionSize;
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    genMaps.requireLandAt.Add(new XZ(x, z));
                }
            }
        }

        internal const string StoryShiftKey = "cylinderworld-storyshifted";

        /// <summary>
        /// Vanilla anchors the story chain on the map center (hardcoded, not on the real spawn). Right after it picks the
        /// locations, and before anything is generated or saved, move them all by the same offset as spawn. Vanilla's
        /// forced landforms/climates hold the same CenterPos objects, so they move along. Its forced *land* (ocean map)
        /// was copied as pixel coordinates though, so that is removed at the old spots and added again at the new ones.
        /// Done once per world; the moved locations are then saved by vanilla, and on later starts vanilla sets up the
        /// forced land at the saved (moved) positions by itself.
        /// </summary>
        void ShiftStoryLocations()
        {
            var p = core.Params;
            if (p == null || p.SpawnZ <= 0) return;

            var story = sapi.ModLoader.GetModSystem<GenStoryStructures>();
            if (story?.Structures == null) return;

            var save = sapi.WorldManager.SaveGame;
            var shifted = save.GetData<List<string>>(StoryShiftKey);
            if (shifted != null)
            {
                foreach (var (code, _) in story.Structures)
                {
                    if (!shifted.Contains(code))
                    {
                        Mod.Logger.Warning("Story location {0} was added after the world was created and was not moved with spawn. Use /wgen story setpos {0} if it ended up in a bad place.", code);
                    }
                }
                return;
            }

            // Vanilla has built some of them already (this world started without the shift, e.g. an earlier start failed
            // before this step): moving them now would point the story at places where nothing stands. Leave them all.
            var built = new List<string>();
            var codes = new List<string>();
            foreach (var (code, loc) in story.Structures)
            {
                codes.Add(code);
                if (loc.DidGenerate) built.Add(code);
            }
            if (built.Count > 0)
            {
                Mod.Logger.Warning("Cylinder World: story locations {0} were generated before they could move with spawn; the story chain stays where vanilla put it.", string.Join(", ", built));
                save.StoreData(StoryShiftKey, codes);
                return;
            }

            var genMaps = sapi.ModLoader.GetModSystem<GenMaps>();
            int dx = p.SpawnX - sapi.WorldManager.MapSizeX / 2;
            int dz = p.SpawnZ - sapi.WorldManager.MapSizeZ / 2;
            shifted = new List<string>();
            foreach (var (code, loc) in story.Structures)
            {
                RemoveForcedLand(genMaps, loc.CenterPos.X, loc.CenterPos.Z, loc.LandformRadius);

                loc.CenterPos.X += dx;
                loc.CenterPos.Z += dz;
                var c = loc.Location;
                if (c != null) loc.Location = new Cuboidi(c.X1 + dx, c.Y1, c.Z1 + dz, c.X2 + dx, c.Y2, c.Z2 + dz);

                genMaps?.ForceLandAt(new ForceLandform { CenterPos = loc.CenterPos, Radius = loc.LandformRadius });
                shifted.Add(code);
                Mod.Logger.Notification("Cylinder World: story location {0} moved with spawn to x={1} z={2}", code, loc.CenterPos.X, loc.CenterPos.Z);
            }

            story.StoryStructureInstancesDirty = true;
            save.StoreData(StoryShiftKey, shifted);
        }

        /// <summary>Remove forced-land pixels vanilla added around a story location (its natural shape can grow past the radius, hence the 2×).</summary>
        void RemoveForcedLand(GenMaps genMaps, int x, int z, int radius)
        {
            if (genMaps?.requireLandAt == null || genMaps.noiseSizeOcean <= 0) return;
            int regionSize = sapi.WorldManager.RegionSize;
            int r = 2 * (radius + GlobalConstants.ChunkSize);
            int minX = (x - r) * genMaps.noiseSizeOcean / regionSize, maxX = (x + r) * genMaps.noiseSizeOcean / regionSize;
            int minZ = (z - r) * genMaps.noiseSizeOcean / regionSize, maxZ = (z + r) * genMaps.noiseSizeOcean / regionSize;
            genMaps.requireLandAt.RemoveAll(xz => xz.X >= minX && xz.X <= maxX && xz.Z >= minZ && xz.Z <= maxZ);
        }

        /// <summary>
        /// Latitude (seasons, sun path, day length) by the planet's layout again: vanilla's GenMaps (0.1) set its own over
        /// the one CylinderWorldCore set at WorldReady. A step of its own, apart from the climate map: the client applies the
        /// same latitude whatever the server's climate map is (ApplyClientLatitude), so the server's seasons must not depend
        /// on InstallPolarClimate, which reads GenMaps' fields and fails as a whole if a game update changes one of them.
        /// </summary>
        void InstallLatitude()
        {
            var p = core.Params;
            if (p == null || !p.PolarClimate) return;
            sapi.World.Calendar.OnGetLatitude = p.Latitude;
        }

        /// <summary>
        /// Wrap vanilla's climate map generator so temperature follows our single curve instead of vanilla's repeating
        /// bands. Done at the generator (not after region gen) because vanilla derives the forest, shrub and flower maps
        /// from the climate map in the same step, so vegetation matches the new temperatures.
        /// </summary>
        void InstallPolarClimate()
        {
            var p = core.Params;
            if (p == null || !p.PolarClimate) return;

            var genMaps = sapi.ModLoader.GetModSystem<GenMaps>();
            if (genMaps?.climateGen == null)
            {
                Mod.Logger.Warning("Vanilla GenMaps climate generator not found; polar temperatures not applied.");
                return;
            }
            if (genMaps.climateGen is PolarClimateLayer) return;

            double blocksPerPixel = (double)sapi.WorldManager.RegionSize / genMaps.noiseSizeClimate;
            if (p.Circumference % blocksPerPixel != 0)
                Mod.Logger.Notification("Cylinder World: the ring ({0}) isn't a whole number of {1}-block climate pixels (custom JumpInset), so the small temperature jitter isn't mirrored exactly across the jump.", p.Circumference, blocksPerPixel);
            float tempMul = sapi.WorldManager.SaveGame.WorldConfiguration.GetString("globalTemperature", "1").ToFloat(1);
            genMaps.climateGen = new PolarClimateLayer(sapi.WorldManager.Seed + 7331, genMaps.climateGen, p, blocksPerPixel, tempMul);
        }

        // ---------------------------------------------------------------- map regions

        void OnMapRegionGen(IMapRegion region, int regionX, int regionZ, ITreeAttribute chunkGenParams)
        {
            var p = core.Params;
            if (p == null) return;

            int regionSize = sapi.WorldManager.RegionSize;

            // Barren polar zone: forests and shrubs fade out toward the walls
            if (p.HasWalls && p.BarrenDepth > 0)
            {
                double reachZ = p.PolarZoneDepth + p.BarrenDepth + regionSize;
                double rz0 = (double)regionZ * regionSize;
                if (rz0 < reachZ || rz0 + regionSize > p.WorldLength - reachZ)
                {
                    ModifyByWorldPos(region.ForestMap, regionX, regionZ, regionSize, (x, z, v) => (int)(v * (1 - p.BarrenFactor(z))));
                    // v8: the tundra keeps some low shrubs; only trees disappear completely
                    double shrubKeep = p.Version >= 8 ? 0.35 : 0;
                    ModifyByWorldPos(region.ShrubMap, regionX, regionZ, regionSize, (x, z, v) => (int)(v * (1 - (1 - shrubKeep) * p.BarrenFactor(z))));
                }
            }

            // Seam ocean
            // Must cover OceanForce's whole reach, coast noise included: a region skipped while its neighbour is forced
            // gets a straight, region-aligned step in the terrain. The extra regionSize covers the maps' padding.
            double reach = p.OceanForceReach + regionSize;
            double rx0 = (double)regionX * regionSize;
            if (rx0 > reach && rx0 + regionSize < p.WorldWidth - reach) return;

            // Earth mode: Antarctic land kept in the band keeps Earth's own ocean map (no forced sea over the ice)
            ModifyByWorldPos(region.OceanMap, regionX, regionZ, regionSize, (x, z, v) => p.EarthKeepsSeamColumn(x, z) ? v : Math.Max(v, p.OceanForce(x, z)));
            ModifyByWorldPos(region.UpheavelMap, regionX, regionZ, regionSize, (x, z, v) => p.EarthKeepsSeamColumn(x, z) ? v : (int)(v * (1 - p.OceanForce(x, z) / 255f)));
        }

        /// <summary>Apply fn(worldX, worldZ, value) to every pixel of a padded region map (row-major, z * Size + x).</summary>
        static void ModifyByWorldPos(IntDataMap2D map, int regionX, int regionZ, int regionSize, System.Func<double, double, int, int> fn)
        {
            if (map?.Data == null || map.Data.Length == 0) return;
            int inner = map.InnerSize;
            if (inner <= 0) return;

            double blocksPerPixel = (double)regionSize / inner;
            for (int pz = 0; pz < map.Size; pz++)
            {
                double worldZ = ((long)regionZ * inner + pz - map.TopLeftPadding) * blocksPerPixel;
                for (int px = 0; px < map.Size; px++)
                {
                    double worldX = ((long)regionX * inner + px - map.TopLeftPadding) * blocksPerPixel;
                    int i = pz * map.Size + px;
                    map.Data[i] = fn(worldX, worldZ, map.Data[i]);
                }
            }
        }

        // ---------------------------------------------------------------- chunk columns

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

            if (core.Config.FlattenSeabed && saltWaterId != 0) FlattenSeam(request, p, firstPass);
            if (p.HasWalls && wallIds[0] != 0) BuildPolarZone(request, p, firstPass);
        }

        /// <summary>Remove what neighbours' Vegetation passes spilled into the band's identical part after its own.</summary>
        void ClearSeamSpill(IChunkColumnGenerateRequest request)
        {
            var p = core.Params;
            if (p == null || !idsReady) return;
            if (core.Config.FlattenSeabed && saltWaterId != 0) FlattenSeam(request, p, false, true);
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

        static void UpdateHeightmaps(IServerChunk[] chunks, IMapChunk mapChunk, int lx, int lz, int mapSizeY)
        {
            const int cs = GlobalConstants.ChunkSize;
            int mi = lz * cs + lx;
            bool rainSet = false;
            for (int y = mapSizeY - 2; y > 0; y--)
            {
                IChunkBlocks data = chunks[y / cs].Data;
                int idx = CellIndex(y, lz, lx);
                bool solid = SolidId(data, idx) != 0;
                if (!rainSet && (solid || data.GetFluid(idx) != 0))
                {
                    mapChunk.RainHeightMap[mi] = (ushort)y;
                    // Worldgen sunlight fills every chunk above YMax with full light; raised walls and ice must count
                    mapChunk.YMax = Math.Max(mapChunk.YMax, (ushort)y);
                    rainSet = true;
                }
                if (solid)
                {
                    mapChunk.WorldGenTerrainHeightMap[mi] = (ushort)y;
                    return;
                }
            }
        }

        /// <summary>
        /// Remove block entities whose block was replaced: the block now at their position has no entity class (rock,
        /// gravel, water, air, ice). Surviving structure blocks, such as a chest the seam blend left in place, keep
        /// their contents. Checking the result rather than predicting which cells were rewritten keeps this correct
        /// for every rewrite rule, including ones added later.
        /// </summary>
        void RemoveOrphanedBlockEntities(IServerChunk[] chunks)
        {
            const int cs = GlobalConstants.ChunkSize;
            var blocks = sapi.World.Blocks;
            foreach (var chunk in chunks)
            {
                var bes = chunk.BlockEntities;
                if (bes == null || bes.Count == 0) continue;
                var positions = BlockEntityPositions(chunk, bes);
                if (positions == null) continue;
                foreach (var pos in positions)
                {
                    if (pos == null) continue;
                    int id = SolidId(chunk.Data, CellIndex(pos.Y, pos.Z % cs, pos.X % cs));
                    var block = id > 0 && id < blocks.Count ? blocks[id] : null;
                    if (!string.IsNullOrEmpty(block?.EntityClass)) continue;
                    // Vanilla's own path when worldgen replaces a block entity's block (BlockAccessorWorldGen.SetSolidBlock):
                    // the chunk's removal, under its lock, and the map chunk's saved list of new block entities, or a
                    // block entity placed there later would get OnBlockPlaced again on its next load
                    chunk.RemoveBlockEntity(pos);
                    ForgetNewBlockEntity(chunk.MapChunk, pos);
                }
            }
        }

        /// <summary>
        /// The positions of a chunk's block entities. A neighbour's structure on another worldgen thread can add one to this
        /// chunk meanwhile (BlockAccessorWorldGen writes to any chunk still generating), so they are copied under the
        /// chunk's own lock, which WorldChunk.AddBlockEntity and RemoveBlockEntity take: the writer waits for the copy.
        /// Without the lock (a game update renamed it) the copy is made once without it; if it met a write, null: this pass
        /// leaves the chunk's block entities as they are, and a later pass of the column, if any, looks again.
        /// </summary>
        BlockPos[] BlockEntityPositions(IServerChunk chunk, Dictionary<BlockPos, BlockEntity> bes)
        {
            if (ChunkLock(chunk) is object gate) lock (gate) return Copy(bes);
            try { return Copy(bes); }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException) { return null; }

            static BlockPos[] Copy(Dictionary<BlockPos, BlockEntity> bes)
            {
                var keys = new BlockPos[bes.Count];
                bes.Keys.CopyTo(keys, 0);
                return keys;
            }
        }

        /// <summary>ServerMapChunk.NewBlockEntities: public in VintagestoryLib but not in the API, so it is resolved through
        /// GameFeature; a game update without it only loses this part of the cleanup.</summary>
        GameFeature newBlockEntities;
        FieldInfo newBlockEntitiesField;
        /// <summary>WorldChunk.packUnpackLock: internal in VintagestoryLib, and the API has no locked way to list a chunk's
        /// block entities, so it is resolved through GameFeature; without it the copy is unlocked (BlockEntityPositions).</summary>
        GameFeature chunkLock;
        FieldInfo chunkLockField;

        void ResolveBlockEntityCleanup()
        {
            newBlockEntities = new GameFeature("removal of replaced worldgen block entities from the map chunk's list", null, Mod.Logger);
            newBlockEntitiesField = newBlockEntities.Field(newBlockEntities.Type("Vintagestory.Server.ServerMapChunk"), "NewBlockEntities", typeof(HashSet<BlockPos>));
            newBlockEntities.Apply();
            chunkLock = new GameFeature("the locked copy of a worldgen chunk's block entities (a copy that meets a neighbour's write skips that chunk's cleanup in that pass)", null, Mod.Logger);
            chunkLockField = chunkLock.Field(chunkLock.Type("Vintagestory.Common.WorldChunk"), "packUnpackLock", typeof(object));
            chunkLock.Apply();
        }

        /// <summary>The chunk's own lock object, null when the feature is off or the chunk isn't the game's WorldChunk.</summary>
        object ChunkLock(IServerChunk chunk)
        {
            var field = chunkLockField;
            if (field == null || chunkLock?.On != true || !field.DeclaringType.IsInstanceOfType(chunk)) return null;
            try { return field.GetValue(chunk); }
            catch (Exception e) { chunkLock.Fail(e); return null; }
        }

        void ForgetNewBlockEntity(IMapChunk mapChunk, BlockPos pos)
        {
            var field = newBlockEntitiesField;
            if (field == null || newBlockEntities?.On != true || !field.DeclaringType.IsInstanceOfType(mapChunk)) return;
            try { (field.GetValue(mapChunk) as HashSet<BlockPos>)?.Remove(pos); }
            catch (Exception e) { newBlockEntities.Fail(e); }
        }
    }
}
