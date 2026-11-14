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
    // SeamWorldGen, part: Spawn and the story chain, the planet climate and latitude, map regions. (CW-0019: moved unchanged from SeamWorldGen.cs.)
    public partial class SeamWorldGen
    {
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
    }
}
