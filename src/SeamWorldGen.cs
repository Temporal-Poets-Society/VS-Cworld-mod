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
    }
}
