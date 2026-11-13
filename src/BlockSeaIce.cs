using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    /// <summary>Vanilla lake-ice collision, rendering and breaking, but melts/breaks back into saltwater.
    /// Seasonal updates are owned by SeaIceSystem; permanent sea ice never receives melt ticks.</summary>
    public class BlockSeaIce : Vintagestory.GameContent.BlockLakeIce
    {
        static bool warned;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            // Without salt water (renamed in a game update) it keeps vanilla's fresh water instead of failing to load
            var salt = api.World.GetBlock(new AssetLocation("game:saltwater-still-7"));
            if (salt != null) waterBlock = salt.Id;
            else if (!warned)
            {
                warned = true;
                api.Logger.Error("Cylinder World: game:saltwater-still-7 not found; broken sea ice leaves fresh water.");
            }
        }

        public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random random, out object extra)
        {
            extra = null;
            return false;
        }

        public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null) { }

        public override bool CanCreatureSpawnOn(IBlockAccessor accessor, BlockPos pos, EntityProperties type, BaseSpawnConditions sc)
        {
            // The existing world-level veto also covers snow above ice and every Antarctic surface.
            return sc != null && sc.MaxTemp <= 0 && type?.Code != null && BlockGlacierIce.EatsMeat(type)
                && base.CanCreatureSpawnOn(accessor, pos, type, sc);
        }
    }
}
