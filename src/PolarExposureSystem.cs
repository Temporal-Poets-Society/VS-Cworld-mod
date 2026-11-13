using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CylinderWorld
{
    /// <summary>
    /// Winter rule on the ice sheet (layout v8). Vanilla freezing is gentle (slow damage after hours, cancelled by any
    /// fire), so below ColdStart a cold exposure meter fills: faster the colder it is (full rate at ColdFull) and the
    /// nearer the wall (30% at the sheet's edge, 100% at the wall). At the wall in deep winter it's full after
    /// FreezeMinutes; a nearby fire only slows it. When full, frost damage kills. Leaving the sheet or warmer weather
    /// lets it drain. Vanilla's body temperature is pulled down along with it, so the vanilla frost effects show.
    /// Summer on the ice (above ColdStart) is plain vanilla: survivable for days with good gear. In Earth mode the polar
    /// sea counts only where it is frozen under the player, not where the season says it soon will be.
    /// The meter survives leaving the game and server restarts (saved with the world). Time away counts as normal
    /// recovery only if the meter was already draining when they left: logging out on the ice while it rises recovers
    /// nothing, so relogging doesn't reset it.
    /// </summary>
    public class PolarExposureSystem : ModSystem
    {
        ICoreServerAPI sapi;
        CylinderWorldCore core;
        readonly Dictionary<string, float> exposure = new();
        readonly Dictionary<string, int> warnedStage = new();
        /// <summary>Players away from the game: their meter, when they left (UTC ticks) and whether it was rising then.</summary>
        readonly Dictionary<string, (float value, long since, bool rising)> away = new();
        /// <summary>Players whose meter rose in the last tick.</summary>
        readonly HashSet<string> rising = new();

        const float RecoverSeconds = 90f;
        const string SaveKey = "cylinderworld-exposure";
        /// <summary>Leads the saved data (a negative number, so it can't be mistaken for the count of the first format).</summary>
        const int SaveFormat = -2;

        static readonly string[] stageMessages =
        {
            null,
            "The cold bites through your furs.",
            "Your hands are going numb. You should head back.",
            "You are freezing. Turn back now!",
            "Frostbite! You are freezing to death.",
        };

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            api.Event.RegisterGameTickListener(Tick, 1000);
            api.Event.PlayerDeath += (plr, dmg) => { exposure.Remove(plr.PlayerUID); warnedStage.Remove(plr.PlayerUID); away.Remove(plr.PlayerUID); rising.Remove(plr.PlayerUID); };
            api.Event.PlayerLeave += plr =>
            {
                if (exposure.Remove(plr.PlayerUID, out float e)) away[plr.PlayerUID] = (e, DateTime.UtcNow.Ticks, rising.Contains(plr.PlayerUID));
                warnedStage.Remove(plr.PlayerUID);
                rising.Remove(plr.PlayerUID);
            };
            api.Event.PlayerJoin += plr =>
            {
                if (!away.Remove(plr.PlayerUID, out var saved)) return;
                float e = Recovered(saved);
                if (e <= 0) return;
                exposure[plr.PlayerUID] = e;
                // Until the first tick recomputes it, or leaving again before then would count as draining
                if (saved.rising) rising.Add(plr.PlayerUID);
            };
            api.Event.SaveGameLoaded += Load;
            api.Event.GameWorldSave += Save;
        }

        /// <summary>A saved meter after the recovery for the time since it was saved (none if it was rising then).</summary>
        static float Recovered((float value, long since, bool rising) saved)
        {
            if (saved.rising) return saved.value;
            double seconds = Math.Max(0, (DateTime.UtcNow.Ticks - saved.since) / (double)TimeSpan.TicksPerSecond);
            return (float)Math.Max(0, saved.value - seconds / RecoverSeconds);
        }

        void Save()
        {
            // A world where the mod is off keeps no record of it (and one that has a record keeps it as it is): the meter
            // only runs where it is on
            if (core.Params == null) return;
            long now = DateTime.UtcNow.Ticks;
            var all = away.Where(kv => Recovered(kv.Value) > 0).ToList();
            all.AddRange(exposure.Select(kv => new KeyValuePair<string, (float, long, bool)>(kv.Key, (kv.Value, now, rising.Contains(kv.Key)))));
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(SaveFormat);
                w.Write(all.Count);
                foreach (var (uid, (value, since, up)) in all) { w.Write(uid); w.Write(value); w.Write(since); w.Write(up); }
            }
            sapi.WorldManager.SaveGame.StoreData(SaveKey, ms.ToArray());
        }

        /// <summary>Everyone is away after a restart; whoever was online counts as having left when the world was saved.</summary>
        void Load()
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
            if (data == null) return;
            try
            {
                using var r = new BinaryReader(new MemoryStream(data));
                int first = r.ReadInt32();
                // Before the format marker: no rising flag (count first)
                bool flagged = first == SaveFormat;
                for (int n = flagged ? r.ReadInt32() : first; n > 0; n--)
                {
                    string uid = r.ReadString();
                    away[uid] = (r.ReadSingle(), r.ReadInt64(), flagged && r.ReadBoolean());
                }
            }
            catch (Exception e) { Mod.Logger.Warning("Saved cold exposure couldn't be read, starting from none: {0}", e.Message); }
        }

        /// <summary>0..1 cold exposure of a player (for /cyl info).</summary>
        public float GetExposure(string playerUid) => exposure.TryGetValue(playerUid, out float e) ? e : 0;

        void Tick(float dt)
        {
            var p = core.Params;
            var cfg = core.Config;
            if (p == null || cfg == null || !cfg.IceSheetWinterRule || p.Version < 8) return;

            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers.Cast<IServerPlayer>())
            {
                var ep = plr.Entity;
                if (ep == null || !ep.Alive || plr.ConnectionState != EnumClientState.Playing) continue;

                var mode = plr.WorldData.CurrentGameMode;
                bool exempt = mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator;
                double harsh = exempt ? 0 : p.SheetHarshness(ep.Pos.X, ep.Pos.Z);
                // Earth mode: the sea part of the sheet follows the season's ice target, which the live ice catches up with
                // over minutes (SeaIceSystem); a boat or swimmer on water that hasn't frozen yet isn't on the ice
                if (harsh > 0 && p.EarthPolarIce && OpenSea(p, ep)) harsh = 0;

                float e = exposure.TryGetValue(plr.PlayerUID, out float prev) ? prev : 0;
                double rate = 0;
                if (harsh > 0)
                {
                    float temp = sapi.World.BlockAccessor.GetClimateAt(ep.Pos.AsBlockPos, EnumGetClimateMode.NowValues)?.Temperature ?? 0;
                    double cold = Math.Clamp((cfg.ColdStart - temp) / Math.Max(1, cfg.ColdStart - cfg.ColdFull), 0, 1);
                    rate = harsh * cold / Math.Max(1, cfg.FreezeMinutes * 60);
                    if (rate > 0 && NearFire(ep)) rate *= cfg.FireFactor;
                }

                e = rate > 0 ? (float)Math.Min(1, e + rate * dt) : Math.Max(0, e - dt / RecoverSeconds);
                if (rate > 0) rising.Add(plr.PlayerUID);
                else rising.Remove(plr.PlayerUID);
                if (e <= 0)
                {
                    exposure.Remove(plr.PlayerUID);
                    warnedStage.Remove(plr.PlayerUID);
                    continue;
                }
                exposure[plr.PlayerUID] = e;

                // Vanilla's body temperature follows the meter, so its frost overlay and shivering show
                var body = ep.GetBehavior<EntityBehaviorBodyTemperature>();
                if (body != null) body.CurBodyTemperature = Math.Max(31, Math.Min(body.CurBodyTemperature, 37 - 6.5f * e));

                int stage = e >= 1 ? 4 : (int)(e * 4);
                warnedStage.TryGetValue(plr.PlayerUID, out int warned);
                if (rate > 0 && stage > warned && stageMessages[stage] != null)
                {
                    plr.SendMessage(GlobalConstants.GeneralChatGroup, stageMessages[stage], EnumChatType.Notification);
                }
                warnedStage[plr.PlayerUID] = rate > 0 ? Math.Max(warned, stage) : Math.Min(warned, stage);

                if (e >= 1 && rate > 0)
                {
                    ep.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Weather, Type = EnumDamageType.Frost }, cfg.FrostDamagePerSecond * dt);
                }
            }
        }

        /// <summary>Open water at sea level under the entity: no block and a liquid in the cell below the surface (sea ice is
        /// in the fluid layer and isn't liquid; glaciers, floes, the wall and land are blocks).</summary>
        bool OpenSea(SeamParams p, Entity ep)
        {
            var at = new BlockPos((int)Math.Floor(ep.Pos.X), p.SeaLevel - 1, (int)Math.Floor(ep.Pos.Z), ep.Pos.Dimension);
            var ba = sapi.World.BlockAccessor;
            return ba.GetBlock(at, BlockLayersAccess.Solid).Id == 0 && ba.GetBlock(at, BlockLayersAccess.Fluid).IsLiquid();
        }

        static bool NearFire(Entity ep) =>
            (ep.WatchedAttributes.GetTreeAttribute("bodyTemp")?.GetFloat("nearHeatSourceStrength") ?? 0) > 0.1f;
    }
}
