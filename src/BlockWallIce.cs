using System;
using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    /// <summary>
    /// The polar wall's ice. Mining is blocked by requiredMiningTier 99 (+ the build ban near the wall);
    /// this class makes it immune to explosions too, and keeps creatures from spawning on it.
    /// </summary>
    public class BlockWallIce : Block
    {
        /// <summary>Asked by both worldgen creature spawning and the live spawner.</summary>
        public override bool CanCreatureSpawnOn(IBlockAccessor blockAccessor, BlockPos pos, EntityProperties type, BaseSpawnConditions sc) => false;

        public override double GetBlastResistance(IWorldAccessor world, BlockPos pos, Vec3f blastDirectionVector, EnumBlastType blastType) => 1000000;

        public override double ExplosionDropChance(IWorldAccessor world, BlockPos pos, EnumBlastType blastType) => 0;

        public override void OnBlockExploded(IWorldAccessor world, BlockPos pos, BlockPos explosionCenter, EnumBlastType blastType, string ignitedByPlayerUid)
        {
            // Never destroyed.
        }
    }

    /// <summary>
    /// Glacier ice: the ice sheet / shelf in front of the wall, and sea ice floes. Nothing spawns on the sheet or shelf
    /// (a lifeless Antarctic interior). On floes, which always lie beyond the ice sheet, cold-specialist predators may
    /// spawn: spawn conditions topping out at or below freezing (polar bears -9°C, arctic foxes) and a meat diet.
    /// Cold grazers like the muskox (-30..-10°C) and arctic hares belong on the tundra.
    /// </summary>
    public class BlockGlacierIce : Block
    {
        /// <summary>The floe variant (set when loaded, instead of a variant lookup on every spawn probe).</summary>
        bool floe;
        /// <summary>Per block instance, so per world: a singleplayer world with other mods or spawn rules starts empty.</summary>
        readonly ConcurrentDictionary<AssetLocation, bool> eatsMeat = new();

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            floe = Variant?["type"] == "floe";
        }

        public override bool CanCreatureSpawnOn(IBlockAccessor blockAccessor, BlockPos pos, EntityProperties type, BaseSpawnConditions sc)
        {
            if (!floe) return false;
            if (sc == null || sc.MaxTemp > 0 || type?.Code == null) return false;
            if (!eatsMeat.TryGetValue(type.Code, out bool meat)) eatsMeat[type.Code] = meat = EatsMeat(type);
            if (!meat) return false;
            return base.CanCreatureSpawnOn(blockAccessor, pos, type, sc);
        }

        /// <summary>Diet from the entity's "creatureDiet" attribute: a "meat" food tag or the Protein food category.</summary>
        internal static bool EatsMeat(EntityProperties type)
        {
            var diet = type.Attributes?["creatureDiet"];
            if (diet == null || !diet.Exists) return false;

            foreach (var tag in diet["weightedFoodTags"].AsArray() ?? Array.Empty<JsonObject>())
            {
                if (tag["code"].AsString("").Contains("meat", StringComparison.OrdinalIgnoreCase)) return true;
            }
            foreach (var cat in diet["foodCategories"].AsArray<string>() ?? Array.Empty<string>())
            {
                if (string.Equals(cat, "Protein", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
