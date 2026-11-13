using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CylinderWorld
{
    /// <summary>
    /// Client-side sea mist that thickens toward the jump lines. It depends only on distance to the
    /// nearest jump line, which is the same just before and just after a wrap, so the fog doesn't pop.
    /// Hides residual tells: chunks still streaming in, clouds changing, other players vanishing.
    /// Right after a jump it briefly thickens further (JumpFogMin, easing out over JumpFogSeconds) to cover
    /// whatever is still arriving, but only when the arrival area isn't loaded yet: with the destination pre-sent
    /// (SeamPreload) the pulse itself was the visible tell.
    /// </summary>
    public class SeamMistSystem : ModSystem
    {
        const string ModifierKey = "cylinderworld-mist";

        ICoreClientAPI capi;
        CylinderWorldCore core;
        AmbientModifier mist;
        /// <summary>When the last pulse started (client ms); none yet while pulsed is false.</summary>
        long pulseAt;
        bool pulsed;

        /// <summary>Chunk columns around the arrival point that must be loaded for the jump to go without the pulse.
        /// Beyond about this distance the mist at the line already hides the terrain.</summary>
        const int ReadyRadius = 4;

        /// <summary>A jump just happened (SeamJumpClient): thicken now, without waiting for the next tick, if the
        /// arrival area is still missing chunks. Returns how many columns of it were missing (0: no pulse; -1: no player).</summary>
        public int Pulse()
        {
            int missing = ArrivalMissing();
            if (missing <= 0) return missing;
            pulseAt = capi.ElapsedMilliseconds;
            pulsed = true;
            OnTick(0);
            return missing;
        }

        /// <summary>Columns within ReadyRadius of the player that lack the chunk at their height or at sea level.</summary>
        int ArrivalMissing()
        {
            var pos = capi.World.Player?.Entity?.Pos;
            if (pos == null) return -1;
            const int cs = GlobalConstants.ChunkSize;
            int cx = (int)Math.Floor(pos.X / cs), cz = (int)Math.Floor(pos.Z / cs);
            int cy = Math.Clamp((int)Math.Floor(pos.Y / cs), 0, capi.World.BlockAccessor.MapSizeY / cs - 1);
            int seaCy = capi.World.SeaLevel / cs, missing = 0;
            for (int dx = -ReadyRadius; dx <= ReadyRadius; dx++)
                for (int dz = -ReadyRadius; dz <= ReadyRadius; dz++)
                {
                    if (dx * dx + dz * dz > ReadyRadius * ReadyRadius) continue;
                    if (capi.World.BlockAccessor.GetChunk(cx + dx, cy, cz + dz) == null
                        || capi.World.BlockAccessor.GetChunk(cx + dx, seaCy, cz + dz) == null) missing++;
                }
            return missing;
        }

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();

            mist = new AmbientModifier().EnsurePopulated();
            mist.FogDensity.Weight = 0;
            mist.FogMin.Weight = 0;

            api.Event.LevelFinalize += () =>
            {
                if (!capi.Ambient.CurrentModifiers.ContainsKey(ModifierKey)) capi.Ambient.CurrentModifiers.Add(ModifierKey, mist);
            };
            api.Event.RegisterGameTickListener(OnTick, 50);
        }

        void OnTick(float dt)
        {
            var p = core.Params;
            var ent = capi.World.Player?.Entity;
            if (p == null || ent == null || p.MistRange <= 0) return;

            float w = (float)GameMath.Clamp(1 - p.DistToJumpLine(ent.Pos.X) / p.MistRange, 0, 1);
            w = w * w * (3 - 2 * w);

            float pulse = 0;
            long since = capi.ElapsedMilliseconds - pulseAt;
            if (pulsed && p.JumpFogMin > p.MistMin && since >= 0 && since < p.JumpFogSeconds * 1000)
            {
                pulse = 1 - since / (p.JumpFogSeconds * 1000);
                pulse = pulse * pulse * (3 - 2 * pulse);
            }

            mist.FogDensity.Value = p.MistDensity;
            mist.FogDensity.Weight = Math.Max(w, pulse);
            mist.FogMin.Value = p.MistMin + (Math.Max(p.MistMin, p.JumpFogMin) - p.MistMin) * pulse;
            mist.FogMin.Weight = Math.Max(w, pulse);
        }
    }
}
