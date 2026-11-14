using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CylinderWorld
{
    /// <summary>
    /// The actual "wrap": anything that crosses a jump line gets teleported by ±circumference.
    /// Also preloads (and pre-sends) the destination while a player approaches (see SeamPreloader),
    /// and protects the seam band from building.
    /// </summary>
    public class SeamWrapSystem : ModSystem
    {
        ICoreServerAPI sapi;
        CylinderWorldCore core;

        /// <summary>Entities mid-wrap (id -> ms when the wrap was issued), so we don't wrap them twice.</summary>
        readonly Dictionary<long, long> pending = new();
        /// <summary>An entry older than this is dropped (the teleport callback never came).</summary>
        const long PendingTimeoutMs = 10000;
        /// <summary>After a blocked crossing the entity is left alone this long before the next try, instead of being put
        /// back every few ticks while the player holds forward.</summary>
        const long BlockedBackoffMs = 1000;
        /// <summary>Carriers with players aboard past a jump line whose destination isn't loaded yet (id -> when its columns
        /// were last requested, ms).</summary>
        readonly Dictionary<long, long> waiting = new();
        /// <summary>Per player: when they last jumped (ms), for JumpCooldown.</summary>
        readonly Dictionary<string, long> lastJump = new();
        /// <summary>During the jump cooldown a player is jumped anyway once this far past the line.</summary>
        const double MaxOvershoot = 32;
        /// <summary>Per player: when they were last told that the other side is blocked (ms).</summary>
        readonly Dictionary<string, long> blockedNotice = new();
        const long BlockedNoticeMs = 10000;
        SeamPreloader preloader;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();

            // Built before the listeners that use it, and on its own: pre-loading only makes the jump smoother, so if a game
            // update breaks it beyond what its features catch, crossing still works without it (one warning)
            // (CW-0014: not on a server that has the mod switched off; taken off again in a world that gets no seam)
            if (!CylinderWorldCore.DisabledOn(api))
            {
                try { preloader = new SeamPreloader(api, core, Mod.Logger); }
                catch (Exception e) { Mod.Logger.Warning("Cylinder World: seam pre-loading is off: {0}", e); }
                CylinderWorldCore.WhenWorldHasNoSeam(api, () => { preloader?.Dispose(); preloader = null; });
            }
            api.Event.RegisterGameTickListener(OnPlayerTick, 50);
            api.Event.RegisterGameTickListener(OnEntityTick, 1000);
            api.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreak;
            api.Event.PlayerLeave += plr => { preloader?.OnLeave(plr.PlayerUID); lastJump.Remove(plr.PlayerUID); blockedNotice.Remove(plr.PlayerUID); };
            // Nothing is prepared or kept while dead (the server may unload it meanwhile); start afresh after respawn
            api.Event.PlayerDeath += (plr, dmg) => preloader?.OnLeave(plr.PlayerUID);
            // Every wrap is announced (Jump), so players are told at join that only announced teleports are seam jumps
            SeamEntities.AnnouncesWraps = true;
        }

        public override void Dispose()
        {
            preloader?.Dispose();
            base.Dispose();
        }

        // ---------------------------------------------------------------- players

        void OnPlayerTick(float dt)
        {
            var p = core.Params;
            if (p == null) return;
            preloader?.TakeLoaded();
            preloader?.Unload();

            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers.Cast<IServerPlayer>())
            {
                if (plr.ConnectionState != EnumClientState.Playing) continue;
                EntityPlayer ep = plr.Entity;
                if (ep == null || !ep.Alive) continue;
                // The seam and the walls are overworld geometry; other dimensions reuse the coordinates for other things
                if (ep.Pos.Dimension != 0) { preloader?.OnLeave(plr.PlayerUID); continue; }

                if (core.Config.WallGuard && p.HasWalls && CheckWall(plr, ep, p)) continue;

                // When riding a boat/mount, move the mount; vanilla carries the passengers along.
                Entity carrier = ep.MountedOn?.Entity ?? ep;
                if (IsPending(carrier) || carrier.Teleporting) continue;
                preloader?.Track(plr, carrier);

                // Until the cooldown is over, a player who turns straight back stays in the open sea past the line.
                // The side they just left is still on their client (SeamPreloader keeps both sides near the line).
                bool coolingDown = JumpCoolingDown(plr.PlayerUID);
                if (p.PastJumpLine(carrier.Pos.X))
                {
                    if (coolingDown && p.DistToJumpLine(carrier.Pos.X) < MaxOvershoot) continue;
                    if (!DestinationReady(carrier, p)) continue;
                    // Everyone aboard goes with it, not just this player
                    var riders = PlayersAboard(carrier);
                    if (!Passable(carrier, p))
                    {
                        // Their side stays as it is: nothing to keep or drain for a jump that doesn't happen
                        PutBack(carrier, p, () => { foreach (var rider in riders) preloader?.OnJumped(rider, false); });
                        continue;
                    }
                    // Their side is kept before the teleport, whose callback can unload it (SeamPreloader.BeforeJump)
                    foreach (var rider in riders) preloader?.BeforeJump(rider, p, carrier.Pos.X, carrier.Pos.Z);
                    Jump(carrier, p, () =>
                    {
                        long now = sapi.World.ElapsedMilliseconds;
                        foreach (var rider in riders)
                        {
                            preloader?.OnJumped(rider, true);
                            lastJump[rider.PlayerUID] = now;
                        }
                    });
                }
                else
                {
                    waiting.Remove(carrier.EntityId);
                    if (!coolingDown) preloader?.Prepare(plr, carrier, p);
                }
            }
        }

        /// <summary>
        /// Survival players who end up on top of the wall or behind it (e.g. from a mountain higher than the wall top,
        /// or by falling off the plateau toward the world edge) are put back on the ground in front of it.
        /// </summary>
        bool CheckWall(IServerPlayer plr, EntityPlayer ep, SeamParams p)
        {
            var mode = plr.WorldData.CurrentGameMode;
            if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator || IsPending(ep)) return false;

            if (!IsOnOrBehindWall(p, ep.Pos.Z, ep.Pos.Y)) return false;

            bool north = ep.Pos.Z < p.WorldLength / 2.0;
            double tz = north ? p.WallFaceMax + 4.5 : p.WorldLength - p.WallFaceMax - 4.5;
            int surfaceY = sapi.World.BlockAccessor.GetRainMapHeightAt((int)ep.Pos.X, (int)tz);
            if (surfaceY <= 0) surfaceY = p.ApronDepth > 0 ? p.ApronSurfaceY(ep.Pos.X, tz) : p.SeaLevel;

            SetPending(ep);
            ep.TeleportToDouble(ep.Pos.X, surfaceY + 1.5, tz, () => pending.Remove(ep.EntityId));
            plr.SendMessage(GlobalConstants.GeneralChatGroup, "The Wall cannot be crossed.", EnumChatType.Notification);
            return true;
        }

        // ---------------------------------------------------------------- other entities

        void OnEntityTick(float dt)
        {
            var p = core.Params;
            if (p == null) return;

            // Wrap callbacks that never came (entity despawned mid-teleport) would otherwise stay forever
            long now = sapi.World.ElapsedMilliseconds;
            foreach (var id in pending.Where(kv => now - kv.Value >= PendingTimeoutMs).Select(kv => kv.Key).ToList()) pending.Remove(id);
            // Waits whose carrier is gone (despawned, its player left) aren't ended by the player tick, which renews a live
            // wait's request twice a second
            foreach (var id in waiting.Where(kv => now - kv.Value >= PendingTimeoutMs).Select(kv => kv.Key).ToList()) waiting.Remove(id);

            List<Entity> toWrap = null, strandedOnWall = null;
            foreach (Entity e in sapi.World.LoadedEntities.Values)
            {
                if (e is EntityPlayer || e.Pos.Dimension != 0) continue;
                // Cheap position tests first: nearly every entity is nowhere near the wall or a jump line
                bool onWall = p.HasWalls && e is EntityAgent && IsOnOrBehindWall(p, e.Pos.Z, e.Pos.Y);
                bool past = core.Config.WrapOtherEntities && p.PastJumpLine(e.Pos.X);
                if (!onWall && !past) continue;
                if (!e.Alive || e.Teleporting || IsPending(e)) continue;
                if (e is EntityAgent agent && agent.MountedOn != null) continue;  // passengers travel with their mount
                var mountable = e.GetInterface<IMountable>();

                // Creatures on top of or behind the wall (spawned there before spawning on wall ice was blocked)
                if (onWall && mountable == null)
                {
                    (strandedOnWall ??= new List<Entity>()).Add(e);
                    continue;
                }

                if (!past) continue;
                // A carrier is moved by OnPlayerTick when a player aboard is handled there; one whose players are all
                // still connecting, dead or elsewhere would otherwise drift on past the line
                if (mountable != null && PlayersAboard(e).Any(MovedByPlayerTick)) continue;
                // Nobody prepared the other side for it: load it first, and check it next time
                if (!DestinationLoaded(e, p)) continue;

                (toWrap ??= new List<Entity>()).Add(e);
            }

            if (strandedOnWall != null)
            {
                foreach (var e in strandedOnWall) e.Die(EnumDespawnReason.Removed, null);
                Mod.Logger.Debug("Removed {0} creature(s) from on top of / behind the ice wall", strandedOnWall.Count);
            }
            if (toWrap != null)
            {
                foreach (var e in toWrap) Wrap(e, p);
            }
        }

        /// <summary>
        /// A player (or their carrier) past the line waits for the destination to load, so the crossing is checked against
        /// what is there and lands where they are then. Normally SeamPreloader has loaded it long before; not after a /tp or
        /// respawn past the line, on a mount with PreSendChunks off, or turning back after the overshoot once the far
        /// columns were dropped. The wait has no time limit: crossing into columns that aren't loaded can't be checked
        /// (IsPassable can't see them), and the teleport would land in whatever is there, inside stale sea ice or a
        /// building. Past the line lies the band's copy of the destination, so waiting there only moves the jump a little
        /// further on; the missing columns are requested again twice a second meanwhile.
        /// </summary>
        bool DestinationReady(Entity carrier, SeamParams p)
        {
            long now = sapi.World.ElapsedMilliseconds;
            bool known = waiting.TryGetValue(carrier.EntityId, out long requested);
            // Ask for the missing columns at the first check and twice a second after (each request queues a load)
            bool request = !known || now - requested >= 500;
            if (DestinationLoaded(carrier, p, request))
            {
                waiting.Remove(carrier.EntityId);
                return true;
            }
            if (request) waiting[carrier.EntityId] = now;
            return false;
        }

        /// <summary>Whether every chunk the entity's collision box touches at its destination is loaded; if not, they are
        /// requested (IsPassable can't check what isn't loaded).</summary>
        bool DestinationLoaded(Entity e, SeamParams p, bool request = true)
        {
            const int cs = GlobalConstants.ChunkSize;
            double nx = p.Wrap(e.Pos.X);
            var box = e.CollisionBox;
            int cx1 = (int)Math.Floor((nx + (box?.X1 ?? 0)) / cs), cx2 = (int)Math.Floor((nx + (box?.X2 ?? 0)) / cs);
            int cz1 = (int)Math.Floor((e.Pos.Z + (box?.Z1 ?? 0)) / cs), cz2 = (int)Math.Floor((e.Pos.Z + (box?.Z2 ?? 0)) / cs);
            // Only chunks that can exist: a box reaching above the build limit would otherwise wait forever
            int top = sapi.WorldManager.MapSizeY / cs - 1;
            int cy1 = Math.Clamp((int)Math.Floor((e.Pos.Y + (box?.Y1 ?? 0)) / cs), 0, top);
            int cy2 = Math.Clamp((int)Math.Floor((e.Pos.Y + (box?.Y2 ?? 1)) / cs), cy1, top);
            bool all = true;
            for (int cx = cx1; cx <= cx2; cx++)
            {
                for (int cz = cz1; cz <= cz2; cz++)
                {
                    bool column = true;
                    for (int cy = cy1; cy <= cy2 && column; cy++) column = sapi.WorldManager.GetChunk(cx, cy, cz) != null;
                    if (column) continue;
                    if (request) sapi.WorldManager.LoadChunkColumnPriority(cx, cz);
                    all = false;
                }
            }
            return all;
        }

        static bool IsOnOrBehindWall(SeamParams p, double z, double y)
        {
            double dz = p.DistToZEdge(z);
            return dz < p.WallDepth || (dz < p.WallFaceMax && y > p.WallTopY - 1);
        }

        // ---------------------------------------------------------------- the jump

        /// <summary>An entity without players aboard: across the jump, or back in front of the line when the other side is blocked.</summary>
        void Wrap(Entity e, SeamParams p)
        {
            if (Passable(e, p)) Jump(e, p, null);
            else PutBack(e, p, null);
        }

        /// <summary>Whether the entity fits at its destination on the other side. Both callers check it only once every chunk
        /// the entity's box touches there is loaded (DestinationReady, DestinationLoaded).</summary>
        bool Passable(Entity e, SeamParams p) => IsPassable(p.Wrap(e.Pos.X), e.Pos.Y, e.Pos.Z, e);

        /// <summary>Something solid on the other side (e.g. someone built at the mirror spot): act like a wall. <paramref
        /// name="back"/> runs once the entity is back.</summary>
        void PutBack(Entity e, SeamParams p, Action back)
        {
            var pos = e.Pos;
            double x = pos.X < p.WorldWidth / 2.0 ? p.JumpInset + 0.5 : p.WorldWidth - p.JumpInset - 0.5;
            SetPending(e);
            e.TeleportToDouble(x, pos.Y, pos.Z, () =>
            {
                // Still pending for BlockedBackoffMs after it is back
                pending[e.EntityId] = sapi.World.ElapsedMilliseconds - PendingTimeoutMs + BlockedBackoffMs;
                back?.Invoke();
            });
            NotifyBlocked(e);
        }

        /// <summary>Across the jump by ±circumference. <paramref name="arrived"/> runs in the teleport's callback, once the
        /// entity is there (at once when the destination is loaded).</summary>
        void Jump(Entity e, SeamParams p, Action arrived)
        {
            var pos = e.Pos;
            Vec3d motion = pos.Motion.Clone();
            SetPending(e);
            double nx = p.Wrap(pos.X);
            // Tell the players aboard first, on the same ordered connection as their teleport packet: their client then
            // takes this teleport, and only this one, for a seam jump (SeamSmoothJump)
            SeamEntities.AnnounceWrap(e, Math.Sign(nx - pos.X));
            e.TeleportToDouble(nx, pos.Y, pos.Z, () =>
            {
                // Vanilla zeroes motion on teleport; give drifting boats/items their momentum back.
                e.Pos.Motion.Set(motion);
                // EntityPlayer.Onplrteleported advances this version, but Entity.TeleportToDouble (mounts)
                // does not. Without it an in-flight pre-jump mount input is rejected as a huge move and the
                // server sends its older arrival position back as a correction, even after the jump packet.
                if (e is not EntityPlayer)
                {
                    e.WatchedAttributes.GetIntAndIncrement("positionVersionNumber");
                    e.WatchedAttributes.MarkPathDirty("positionVersionNumber");
                }
                pending.Remove(e.EntityId);
                arrived?.Invoke();
            });
        }

        /// <summary>The online players who travel with this carrier: the player itself, or everyone seated on it. The one
        /// rule for who jumps along, who is told about a blocked crossing, and which carriers OnEntityTick leaves alone.</summary>
        List<IServerPlayer> PlayersAboard(Entity carrier)
        {
            var aboard = new List<IServerPlayer>();
            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
                if (SeamEntities.Aboard(plr, carrier)) aboard.Add(plr);
            return aboard;
        }

        /// <summary>OnPlayerTick handles this player's carrier.</summary>
        static bool MovedByPlayerTick(IServerPlayer plr) =>
            plr.ConnectionState == EnumClientState.Playing && plr.Entity?.Alive == true && plr.Entity.Pos.Dimension == 0;

        /// <summary>Tell the player (or the players aboard), at most every BlockedNoticeMs, why they can't cross.</summary>
        void NotifyBlocked(Entity e)
        {
            long now = sapi.World.ElapsedMilliseconds;
            foreach (var plr in PlayersAboard(e))
            {
                if (blockedNotice.TryGetValue(plr.PlayerUID, out long at) && now - at < BlockedNoticeMs) continue;
                blockedNotice[plr.PlayerUID] = now;
                plr.SendMessage(GlobalConstants.GeneralChatGroup, "Something blocks the way on the other side of the Mist Sea.", EnumChatType.Notification);
            }
        }

        bool JumpCoolingDown(string playerUid)
        {
            if (!lastJump.TryGetValue(playerUid, out long at)) return false;
            if (sapi.World.ElapsedMilliseconds - at < core.Config.JumpCooldown * 1000) return true;
            lastJump.Remove(playerUid);
            return false;
        }

        bool IsPassable(double x, double y, double z, Entity e)
        {
            var ba = sapi.World.BlockAccessor;
            var bpos = new BlockPos((int)x, (int)y, (int)z, e.Pos.Dimension);
            if (ba.GetChunkAtBlockPos(bpos) == null) return true;  // above (or below) the world: nothing there to hit

            // The entity's whole collision box at the destination (boats are wider than one block), shrunk by a contact
            // tolerance so standing on ice or a snow layer doesn't count as overlapping it. Sea ice sits in the fluid
            // layer and collides too, and the two sides' ice can differ while the live sea-ice update catches up.
            const double tol = 0.01;
            var box = e.CollisionBox;
            double x1 = x + (box?.X1 ?? 0) + tol, x2 = Math.Max(x1, x + (box?.X2 ?? 0) - tol);
            double y1 = y + (box?.Y1 ?? 0) + tol, y2 = Math.Max(y1, y + (box?.Y2 ?? 1) - tol);
            double z1 = z + (box?.Z1 ?? 0) + tol, z2 = Math.Max(z1, z + (box?.Z2 ?? 0) - tol);
            for (int bx = (int)Math.Floor(x1); bx <= (int)Math.Floor(x2); bx++)
            {
                for (int by = (int)Math.Floor(y1); by <= (int)Math.Floor(y2); by++)
                {
                    for (int bz = (int)Math.Floor(z1); bz <= (int)Math.Floor(z2); bz++)
                    {
                        bpos.Set(bx, by, bz);
                        if (Overlaps(bpos, BlockLayersAccess.Solid) || Overlaps(bpos, BlockLayersAccess.Fluid)) return false;
                    }
                }
            }
            return true;

            bool Overlaps(BlockPos pos, int layer)
            {
                var block = ba.GetBlock(pos, layer);
                var boxes = block?.GetCollisionBoxes(ba, pos);
                if (boxes == null) return false;
                // A snow layer (up to a quarter block): each seam copy keeps its own snow (CW-D51), so the destination's can
                // stand a layer or two higher than the source's, by vanilla's dither or a copy's catch-up after loading.
                // Landing that deep in snow is what vanilla does to anyone standing where snow builds up; a full snow block
                // still blocks the way.
                if (block.BlockMaterial == EnumBlockMaterial.Snow && ShallowSnow(boxes)) return false;
                foreach (var b in boxes)
                {
                    if (b != null && pos.X + b.X1 < x2 && pos.X + b.X2 > x1 && pos.Y + b.Y1 < y2 && pos.Y + b.Y2 > y1
                        && pos.Z + b.Z1 < z2 && pos.Z + b.Z2 > z1) return true;
                }
                return false;
            }
        }

        /// <summary>Every collision box tops out within a quarter block of the block's floor (vanilla's snow layers: none,
        /// 0.125 or 0.25 high).</summary>
        static bool ShallowSnow(Cuboidf[] boxes)
        {
            foreach (var b in boxes) if (b != null && b.Y2 > 0.25f + 1e-4f) return false;
            return true;
        }

        bool IsPending(Entity e)
        {
            if (!pending.TryGetValue(e.EntityId, out long since)) return false;
            if (sapi.World.ElapsedMilliseconds - since < PendingTimeoutMs) return true;
            pending.Remove(e.EntityId);  // callback never came (e.g. entity despawned); give up waiting
            return false;
        }

        void SetPending(Entity e) => pending[e.EntityId] = sapi.World.ElapsedMilliseconds;

        // ---------------------------------------------------------------- protection

        bool OnCanPlaceOrBreak(IServerPlayer byPlayer, BlockSelection blockSel, out string claimant)
        {
            claimant = null;
            var p = core.Params;
            if (p == null || blockSel?.Position == null || blockSel.Position.dimension != 0) return true;
            if (byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative) return true;

            // Ice walls: no pillaring up the face or digging around it
            if (p.InWallBuildBan(blockSel.Position.Z + 0.5))
            {
                claimant = "the Wall";
                return false;
            }

            if (core.Config.ProtectSeamBand && p.InSeamBand(blockSel.Position.X + 0.5))
            {
                claimant = "the Mist Sea";
                return false;
            }
            return true;
        }
    }
}
