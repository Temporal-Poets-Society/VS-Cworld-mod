using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace CylinderWorld
{
    /// <summary>
    /// Vanilla adds rivulets at order 0.9, after the polar wall is built. Our ice uses the Stone material for
    /// map rendering, so vanilla otherwise carves lava sources into it. Keep that generator out of wall columns.
    /// The patch goes through GameFeature: if a game update renames the generator's method, only this guard is lost,
    /// with one log line. Elsewhere in the polar zone (and in the wall rows too while this guard is off) the generator
    /// runs, and the sources it puts into this mod's ice are taken out right after it (SeamWorldGen.ClearRivuletsInIce,
    /// registered here: order 0.92 puts it after the generator and before vanilla's sunlight at 0.95).
    /// </summary>
    public class PolarRivuletGuard : ModSystem
    {
        Harmony harmony;
        /// <summary>The applied guard; the prefix is static, so it finds the feature here (null when not running).</summary>
        static volatile GameFeature feature;

        public override double ExecuteOrder() => 0.92;
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            var worldGen = api.ModLoader.GetModSystem<SeamWorldGen>();
            if (worldGen != null) api.Event.ChunkColumnGeneration(worldGen.ClearRivuletsInIce, EnumWorldGenPass.Vegetation, "standard");
            // CW-0014: no patch on a server that has the mod switched off, and off again in a world that gets no seam (no
            // walls to guard; GuardOn is false then, so the cleanup looks everywhere, as it does without the guard)
            if (CylinderWorldCore.DisabledOn(api)) return;
            CylinderWorldCore.WhenWorldHasNoSeam(api, () => feature?.Unpatch());
            harmony = new Harmony("cylinderworld.polarrivulets");
            var f = new GameFeature("the guard that keeps vanilla's rivulets out of the polar wall", harmony, Mod.Logger);
            f.Patch(f.Method(f.Type("Vintagestory.ServerMods.GenRivulets"), "OnChunkColumnGen", new[] { typeof(IChunkColumnGenerateRequest) }),
                prefix: new HarmonyMethod(typeof(PolarRivuletGuard), nameof(BeforeRivulets)));
            feature = f;
            f.Apply();
        }

        static bool BeforeRivulets(IChunkColumnGenerateRequest request)
        {
            var f = feature;
            if (f == null || !f.On) return true;
            try
            {
                // The server's own params (in singleplayer the client has its own copy), possibly already null while the
                // server still generates during shutdown
                return !IsWallColumn(CylinderWorldCore.ServerShared, request.ChunkZ);
            }
            catch (Exception e)
            {
                f.Fail(e);
                return true;
            }
        }

        /// <summary>
        /// The guard is applied and hasn't failed, and the server's params it reads are there: vanilla's rivulets skipped the
        /// wall rows (IsWallColumn), so the cleanup after them may too. Off (a game update renamed the generator's method, a
        /// prefix call failed, or the params are already gone at shutdown), the generator runs in the wall rows as well and the
        /// cleanup must look there. Read after the generator ran: the guard never comes back on, so a stale "off" only
        /// makes the cleanup look where nothing was put.
        /// </summary>
        internal static bool GuardOn => feature?.On == true && CylinderWorldCore.ServerShared != null;

        internal static bool IsWallColumn(SeamParams p, int chunkZ)
        {
            if (p == null || !p.HasWalls) return false;
            double start = (double)chunkZ * 32, end = start + 32;
            return start < p.WallFaceMax && end > 0
                || start < p.WorldLength && end > p.WorldLength - p.WallFaceMax;
        }

        public override void Dispose()
        {
            feature?.Unpatch();
            feature = null;
            harmony?.UnpatchAll(harmony.Id);
            base.Dispose();
        }
    }
}
