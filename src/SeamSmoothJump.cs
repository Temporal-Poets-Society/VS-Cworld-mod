using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.Common;

namespace CylinderWorld
{
    /// <summary>
    /// Client patches so that nothing but the place changes at a seam jump (CW-D33, play-test of 26 October):
    ///  - The server wraps the position it last heard from the player and teleports them there. That arrives 50-150 ms
    ///    after the crossing, by when the client has walked or sailed on, so the player was set back (or aside, or up and
    ///    down) by that much: a small jerk. A jump teleport of the player or of what they ride now moves the client's
    ///    own current position by exactly the circumference instead, and tells SeamJumpClient about the jump.
    ///    A teleport is a jump when it moves the player by about one ring; once the server says it announces its wraps
    ///    (SeamEntities.AnnounceWrap), the player's teleport packet is one only if announced, so a command or another
    ///    mod's teleport of about one ring lands exactly where the server puts it (SeamJumpClient still takes the move
    ///    for a jump between the two places: the other creatures, the camera, the clouds and the weather follow).
    ///  - Other players' teleport packets land on the lap nearest the player like everything else (CW-D34), and never
    ///    move what the player rides (a fellow passenger's packet would put the boat back where the server had it).
    ///  - Plants, grass and blocks take a random rotation, offset, size, texture variant or texture tile from a hash of
    ///    their absolute position. The two sides of the line hold the same blocks one ring apart, so they hashed
    ///    differently and the grass turned at the jump. The chunk tesselators, the texture helper they share for cube
    ///    blocks (BleedingTextureHelper) and the block decals now hash x wrapped into the ring (no change inside it).
    /// </summary>
    static class SeamSmoothJump
    {
        static GameFeature teleports, ringHashing;

        /// <summary>The teleport patches are on: jumps are reported by them (SeamJumpClient.Jumped).</summary>
        internal static bool TeleportsOn => teleports?.On == true;

        public static void Patch(Harmony harmony, ILogger logger)
        {
            teleports = new GameFeature("smooth seam jumps (the player keeps their place at the jump)", harmony, logger);
            try { BindTeleports(teleports); }
            catch (Exception e) { teleports.Require(false, e.GetType().Name + ": " + e.Message); }
            teleports.Apply();

            ringHashing = new GameFeature("ring-hashed plant, block texture and decal variants (they stay as they were at a seam jump)", harmony, logger);
            var sites = new List<MethodBase>();
            try { BindRingHashing(ringHashing, sites); }
            catch (Exception e) { ringHashing.Require(false, e.GetType().Name + ": " + e.Message); }
            if (!ringHashing.Apply()) return;
            logger.Debug("Cylinder World: ring-hashing positions in {0}", string.Join(", ", sites.Select(m => m.DeclaringType?.Name + "." + m.Name)));
            // Where vanilla picks a cube block's texture variant and a plant's offset: if these moved elsewhere in an
            // update, those blocks change at the jump again although the feature is on
            foreach (var (type, method) in new[] { ("BleedingTextureHelper", "GetFaceTextureSubId"), ("ChunkTesselator", "TesselateBlock") })
            {
                if (!sites.Any(m => m.DeclaringType?.Name == type && m.Name == method))
                    logger.Warning("Cylinder World: {0}.{1} no longer hashes positions; some blocks may change at a seam jump", type, method);
            }
        }

        static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(SeamSmoothJump), name);

        /// <summary>Everything the teleport patches use, checked before anything is patched: they run in vanilla's packet
        /// handlers, where an exception would crash the client.</summary>
        static void BindTeleports(GameFeature f)
        {
            var net = f.Type("Vintagestory.Client.NoObf.SystemNetworkProcess");
            var packet = f.Type("Packet_EntityPosition");
            foreach (var name in new[] { "EntityId", "X", "Y", "Z", "MotionX", "MotionY", "MotionZ" }) f.Field(packet, name, typeof(long));
            foreach (var name in new[] { "Tick", "PositionVersion", "Yaw", "Pitch", "Roll", "HeadYaw", "HeadPitch", "BodyYaw" }) f.Field(packet, name, typeof(int));
            f.Field(packet, "Teleport", typeof(bool));
            f.Field(net, "bulkPositions", typeof(bool));
            var collectibleNet = f.Type("Vintagestory.Common.CollectibleNet");
            f.Method(collectibleNet, "SerializeDoublePrecise", new[] { typeof(double) });
            f.Method(collectibleNet, "DeserializeDoublePrecise", new[] { typeof(long) });
            f.Method(collectibleNet, "SerializeFloatPrecise", new[] { typeof(float) });
            var prev = f.Field(typeof(EntityBehaviorControlledPhysics), "prevPos", typeof(Vec3d));
            if (prev != null) physicsPrev = AccessTools.FieldRefAccess<EntityBehaviorControlledPhysics, Vec3d>(prev);
            // EntityPlayer moves its mount BEFORE calling Entity's handler. Intercept the override first.
            f.Patch(f.Method(typeof(EntityPlayer), nameof(Entity.OnReceivedServerPacket), new[] { typeof(int), typeof(byte[]) }), prefix: Hook(nameof(TeleportPacket)));
            if (packet != null)
                f.Patch(f.Method(net, "HandleSinglePacket", new[] { packet }), prefix: Hook(nameof(PositionPacket)), postfix: Hook(nameof(AfterPositionPacket)));
        }

        // ---------------------------------------------------------------- the teleport

        sealed class Arrival
        {
            public Vec3d Target;
            public long At;
            public int Version;
            /// <summary>The direction of the jump (±1 circumference).</summary>
            public int Laps;
            /// <summary>Taken for a jump on the server's word (or by a server that doesn't announce its wraps), not only
            /// from an early position packet's distance.</summary>
            public bool Confirmed;
        }
        static readonly ConditionalWeakTable<Entity, Arrival> arrivals = new();
        static AccessTools.FieldRef<EntityBehaviorControlledPhysics, Vec3d> physicsPrev;

        static void Remember(Entity e, Vec3d target, int version, int laps, bool confirmed)
        {
            var a = arrivals.GetOrCreateValue(e);
            a.Target = target.Clone();
            a.At = SeamJumpClient.Capi.ElapsedMilliseconds;
            a.Version = version;
            a.Laps = laps;
            a.Confirmed = confirmed;
        }

        /// <summary>The other packet of a jump already made (the teleport packet and the position packet both carry it).
        /// <paramref name="ordered"/>: the player's teleport packet, which comes after the server's wrap notice. It
        /// confirms a jump that an earlier position packet made from the distance alone, or without a notice overrules
        /// it: then vanilla puts the player where the server says.</summary>
        static bool Repeated(Entity e, Vec3d target, bool ordered)
        {
            if (!arrivals.TryGetValue(e, out var a) || SeamJumpClient.Capi.ElapsedMilliseconds - a.At >= 2000
                || a.Target.SquareDistanceTo(target) >= 0.000001) return false;
            if (a.Confirmed || !ordered) return true;
            if (SeamEntities.TakeNotice(e.EntityId, a.Laps)) return a.Confirmed = true;
            arrivals.Remove(e);
            return false;
        }

        /// <summary>For a teleport of the client's own entity by about one ring: the move by whole circumferences.</summary>
        static bool Shifted(Entity e, double toX, out double d)
        {
            d = 0;
            var p = CylinderWorldCore.ClientShared;
            if (p == null || e.Pos.Dimension != 0 || !SeamEntities.Own(e) || !SeamJumpClient.IsJump(p, toX - e.Pos.X)) return false;
            double x = e.Pos.X + Math.Round((toX - e.Pos.X) / p.Circumference) * p.Circumference;
            d = x - e.Pos.X;
            return true;
        }

        /// <summary>Moves <paramref name="e"/> (the player or what they ride) and the player by <paramref name="d"/>.</summary>
        static void Jump(Entity e, double d, Vec3d target, int version, bool confirmed)
        {
            var player = SeamJumpClient.Capi.World.Player.Entity;
            double fromX = player.Pos.X;
            Translate(e, d);
            Remember(e, target, version, Math.Sign(d), confirmed);
            if (e != player) Translate(player, d);
            SeamJumpClient.Jumped(fromX, d);
        }

        /// <summary>Translate history as well as the live position. Physics publishes prevPos again even in frames
        /// with no physics step; moving only PlayerCamera.prevPos is undone on that frame (CW-D35).</summary>
        internal static void Translate(Entity e, double d)
        {
            e.Pos.X += d;
            if (e.PreviousServerPos != null) e.PreviousServerPos.X += d;
            e.PositionBeforeFalling.X += d;
            var physics = e.GetBehavior<EntityBehaviorControlledPhysics>();
            if (physics != null && physicsPrev != null && physicsPrev(physics) is Vec3d previous) previous.X += d;
            SeamEntities.MoveInterpolation(e, x => x + d);
            if (e is EntityPlayer player && e.Api?.Side == EnumAppSide.Client) player.UpdatePartitioning();
        }

        /// <summary>Packet 1 (EntityPlayer.Onplrteleported): a player, also sent for each player aboard a teleported mount.</summary>
        static bool TeleportPacket(EntityPlayer __instance, int packetid, ref byte[] data)
        {
            if (packetid != 1 || !teleports.On || __instance.Api?.Side != EnumAppSide.Client) return true;
            try { return SeamEntities.Own(__instance) ? OwnTeleport(__instance, data) : OtherTeleport(__instance, ref data); }
            catch (Exception e)
            {
                teleports.Fail(e);
                return true;
            }
        }

        static bool OwnTeleport(EntityPlayer player, byte[] data)
        {
            var to = SerializerUtil.Deserialize<Vec3d>(data);
            Entity carrier = player.MountedOn?.Entity ?? player;
            if (Repeated(carrier, to, true)) return false;
            if (!Shifted(carrier, to.X, out double d)) return true;
            // This packet comes after the server's wrap notice: a server that announces its wraps is believed
            if (SeamEntities.WrapsAnnounced && !SeamEntities.TakeNotice(carrier.EntityId, Math.Sign(d))) return true;
            Jump(carrier, d, to, carrier.WatchedAttributes.GetInt("positionVersionNumber") + 1, true);
            // Packet 1 only relocates, updates partitioning and dirties the arrival block. Keeping the native
            // mounted handler would replace the carrier with the server's old position before our base prefix.
            player.World.BlockAccessor.MarkBlockDirty(player.Pos.AsBlockPos);
            return false;
        }

        /// <summary>Another player's teleport: onto the lap nearest the player (while SeamEntities.LapsOn), and never
        /// moving what the player rides.</summary>
        static bool OtherTeleport(EntityPlayer other, ref byte[] data)
        {
            var to = SerializerUtil.Deserialize<Vec3d>(data);
            if (to.Y >= BlockPos.DimensionBoundary) return true;  // another dimension: no seam there
            double x = SeamEntities.NearestLap(to.X);
            if (x != to.X)
            {
                to.X = x;
                data = SerializerUtil.Serialize(to);
            }
            if (!SeamEntities.Own(other.MountedOn?.Entity)) return true;
            // A fellow passenger: the player's own packet or the mount's position packet moves the mount, by exactly the
            // jump; vanilla would move it again, to where the server had it. Vanilla's packet 1 does no more than this.
            other.Pos.SetPosWithDimension(to);
            other.World.BlockAccessor.MarkBlockDirty(to.AsBlockPos);
            return false;
        }

        /// <summary>Position packets: a teleporting one of what the player rides (Entity.TeleportToDouble) as the player's own;
        /// any other entity's onto the lap nearest the player (SeamEntities, CW-D34; while SeamEntities.LapsOn, which also
        /// moves their interpolation, so the two never end up on different laps).</summary>
        static bool PositionPacket(Packet_EntityPosition packet, bool ___bulkPositions, out EntityPos __state)
        {
            __state = null;
            if (packet == null || !teleports.On) return true;
            try { return Position(packet, ___bulkPositions, out __state); }
            catch (Exception e)
            {
                teleports.Fail(e);
                __state = null;
                return true;
            }
        }

        static bool Position(Packet_EntityPosition packet, bool bulkPositions, out EntityPos state)
        {
            state = null;
            var e = SeamJumpClient.Capi?.World?.GetEntityById(packet.EntityId);
            if (e == null || e.Pos.Dimension != 0) return true;
            bool own = SeamEntities.Own(e);
            if (own && arrivals.TryGetValue(e, out var arrival)
                && packet.PositionVersion < Math.Max(arrival.Version, e.WatchedAttributes.GetInt("positionVersionNumber")))
                return false; // A UDP correction sent before the reliable jump must not move us back afterward.
            // Do not translate for packets which the native tick-order check will discard (or use only to seed
            // the first bulk tick). Otherwise an obsolete packet can jump the client without moving the server.
            int tick = e.Attributes.GetInt("tick");
            if (packet.Tick <= tick || tick == 0 && bulkPositions) return true;
            if (!own)
            {
                // Positions travel as x * 16384 (CollectibleNet.SerializeDoublePrecise) and the circumference is whole
                // blocks, so the move is exact
                double x = packet.X / 16384.0, shift = Math.Round(SeamEntities.NearestLap(x) - x);
                if (shift != 0) packet.X += (long)shift * 16384;
                return true;
            }
            if (!packet.Teleport) return true;
            var target = new Vec3d(CollectibleNet.DeserializeDoublePrecise(packet.X), CollectibleNet.DeserializeDoublePrecise(packet.Y),
                CollectibleNet.DeserializeDoublePrecise(packet.Z));
            if (!Repeated(e, target, false))
            {
                if (!Shifted(e, target.X, out double d)) return true;
                // May overtake the wrap notice (it can come by UDP): a jump by distance, confirmed or overruled by the
                // player's teleport packet, which can't overtake it
                Jump(e, d, target, packet.PositionVersion, !SeamEntities.WrapsAnnounced || SeamEntities.TakeNotice(e.EntityId, Math.Sign(d)));
            }
            state = e.Pos.Copy();
            packet.X = CollectibleNet.SerializeDoublePrecise(e.Pos.X);
            packet.Y = CollectibleNet.SerializeDoublePrecise(e.Pos.InternalY);
            packet.Z = CollectibleNet.SerializeDoublePrecise(e.Pos.Z);
            // OnReceivedServerPos consumes these fields before our postfix (including interpolation snapshots).
            // Keep the local motion and orientation there too, not merely on the live EntityPos afterward.
            packet.MotionX = CollectibleNet.SerializeDoublePrecise(e.Pos.Motion.X);
            packet.MotionY = CollectibleNet.SerializeDoublePrecise(e.Pos.Motion.Y);
            packet.MotionZ = CollectibleNet.SerializeDoublePrecise(e.Pos.Motion.Z);
            packet.Yaw = CollectibleNet.SerializeFloatPrecise(e.Pos.Yaw);
            packet.Pitch = CollectibleNet.SerializeFloatPrecise(e.Pos.Pitch);
            packet.Roll = CollectibleNet.SerializeFloatPrecise(e.Pos.Roll);
            packet.HeadYaw = CollectibleNet.SerializeFloatPrecise(e.Pos.HeadYaw);
            packet.HeadPitch = CollectibleNet.SerializeFloatPrecise(e.Pos.HeadPitch);
            if (e is EntityAgent agent) packet.BodyYaw = CollectibleNet.SerializeFloatPrecise(agent.BodyYawServer);
            return true;
        }

        static void AfterPositionPacket(Packet_EntityPosition packet, EntityPos __state)
        {
            if (__state == null) return;
            try
            {
                // SetFromPacket also replaces motion and angles. A seam translation preserves all of them.
                SeamJumpClient.Capi?.World?.GetEntityById(packet.EntityId)?.Pos.SetFrom(__state);
            }
            catch (Exception e) { teleports.Fail(e); }
        }

        // ---------------------------------------------------------------- position hashes: plants, cube textures and tiles, decals

        static Dictionary<MethodInfo, MethodInfo> hashes = new();

        /// <summary>The position hashes (and the texture tile selector) and every client tesselation method that calls one:
        /// plant rotation and offset, cube block texture variants and tiles, block decals.</summary>
        static void BindRingHashing(GameFeature f, List<MethodBase> sites)
        {
            var map = new Dictionary<MethodInfo, MethodInfo>();
            void Ring(Type type, string name, Type[] args, string ring)
            {
                var m = f.Method(type, name, args);
                if (m != null) map[m] = AccessTools.Method(typeof(SeamSmoothJump), ring);
            }
            var xyz = new[] { typeof(int), typeof(int), typeof(int) };
            Ring(typeof(GameMath), nameof(GameMath.MurmurHash3), xyz, nameof(MurmurHash3));
            Ring(typeof(GameMath), nameof(GameMath.MurmurHash3Mod), new[] { typeof(int), typeof(int), typeof(int), typeof(int) }, nameof(MurmurHash3Mod));
            Ring(typeof(GameMath), nameof(GameMath.oaatHash), xyz, nameof(OaatHash));
            Ring(typeof(GameMath), nameof(GameMath.oaatHashU), xyz, nameof(OaatHashU));
            Ring(typeof(BakedCompositeTexture), nameof(BakedCompositeTexture.GetTiledTexturesSelector),
                new[] { typeof(BakedCompositeTexture[]), typeof(int), typeof(int), typeof(int), typeof(int) }, nameof(TiledTexturesSelector));
            hashes = map;

            // The tesselators, the texture helper they share (cube blocks' variants and tiles) and the block decals, which
            // are drawn over those meshes. Client code only: in singleplayer the server shares this process.
            var tesselator = f.Type("Vintagestory.Client.NoObf.ChunkTesselator");
            if (tesselator == null) return;
            var helpers = new HashSet<string> { "BleedingTextureHelper", "SystemRenderDecals" };
            foreach (var type in AccessTools.GetTypesFromAssembly(tesselator.Assembly)
                .Where(t => t.Namespace == tesselator.Namespace && (t.Name.EndsWith("Tesselator") || helpers.Contains(t.Name))))
            {
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                    if (!PatchProcessor.ReadMethodBody(method).Any(i => i.Value is MethodInfo m && map.ContainsKey(m))) continue;
                    f.Patch(method, transpiler: Hook(nameof(RingHashes)));
                    sites.Add(method);
                }
            }
            f.Require(sites.Count > 0, "a chunk tesselator that hashes positions");
        }

        static IEnumerable<CodeInstruction> RingHashes(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var i in instructions)
            {
                if (i.opcode == OpCodes.Call && i.operand is MethodInfo m && hashes.TryGetValue(m, out var ring)) i.operand = ring;
                yield return i;
            }
        }

        /// <summary>x moved into the ring [JumpInset, JumpInset + Circumference): the same block on both sides of the line.</summary>
        internal static int RingX(int x)
        {
            var p = CylinderWorldCore.ClientShared;
            long c = p == null ? 0 : (long)Math.Round(p.Circumference);
            if (c <= 0) return x;
            long start = (long)p.JumpInset;
            return (int)(start + ((x - start) % c + c) % c);
        }

        static int MurmurHash3(int x, int y, int z) => GameMath.MurmurHash3(RingX(x), y, z);
        static int MurmurHash3Mod(int x, int y, int z, int mod) => GameMath.MurmurHash3Mod(RingX(x), y, z, mod);
        static int OaatHash(int x, int y, int z) => GameMath.oaatHash(RingX(x), y, z);
        static uint OaatHashU(int x, int y, int z) => GameMath.oaatHashU(RingX(x), y, z);
        static int TiledTexturesSelector(BakedCompositeTexture[] tiles, int tileSide, int x, int y, int z) =>
            BakedCompositeTexture.GetTiledTexturesSelector(tiles, tileSide, RingX(x), y, z);
    }
}
