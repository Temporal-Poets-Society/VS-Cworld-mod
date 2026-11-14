using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace CylinderWorld
{
    /// <summary>
    /// Creatures and other players across the jump line (CW-D34). Every entity exists once, on one lap of the ring, and
    /// vanilla sends a player only the entities near them, so the far side of the line looked empty until the jump, and
    /// what was behind vanished after it: animals appeared and disappeared.
    ///  - Server: the distance in vanilla's entity tracking (PhysicsManager.UpdateTrackedEntityState) is taken around
    ///    the ring, so a player near the line gets the entities on the other side too (the chunks there are already on
    ///    their client: SeamPreload), and those entities are simulated as near a player.
    ///  - Client: every other entity is kept on the lap nearest the player: position and teleport packets are moved by
    ///    whole circumferences before they are applied (SeamSmoothJump), new entities when they spawn, and all of them
    ///    (with the positions their interpolation is between) when the player jumps. One feature switches all of these
    ///    (LapsOn).
    /// Interaction across the line is not mirrored: the server checks reach with the entities' real positions.
    /// Also carries the wrap notices: a server that tells the players aboard just before it wraps them lets the client
    /// tell a seam jump from any other teleport of about one ring (SeamSmoothJump).
    /// </summary>
    public class SeamEntities : ModSystem
    {
        const string HarmonyId = "cylinderworld.seamentities";
        const string NoticeChannel = "cylinderworld-seamjump";
        Harmony harmony;

        public override void StartServerSide(ICoreServerAPI api)
        {
            harmony = new Harmony(HarmonyId);
            // CW-0014: no patch on a server that has the mod switched off, and off again in a world that gets no seam
            if (!CylinderWorldCore.DisabledOn(api))
            {
                var f = new GameFeature("sending entities across the jump line", harmony, Mod.Logger);
                try
                {
                    var method = f.Method(f.Type("Vintagestory.Server.PhysicsManager"), "UpdateTrackedEntityState");
                    f.Require(AccessTools.PropertyGetter(typeof(EntityPos), nameof(EntityPos.X)) != null, "EntityPos.X");
                    f.Patch(method, transpiler: new HarmonyMethod(typeof(SeamEntities), nameof(RingDistance)));
                }
                catch (Exception e) { f.Require(false, e.GetType().Name + ": " + e.Message); }
                tracking = f;
                f.Apply();
                CylinderWorldCore.WhenWorldHasNoSeam(api, () => f.Unpatch());
            }

            serverApi = api;
            noticeChannel = api.Network.RegisterChannel(NoticeChannel).RegisterMessageType<SeamWrapNotice>();
            api.Event.PlayerJoin += plr => { if (AnnouncesWraps) noticeChannel?.SendPacket(new SeamWrapNotice(), plr); };
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            api.Event.OnEntitySpawn += OnEntityArrived;
            api.Event.OnEntityLoaded += OnEntityArrived;
            api.Network.RegisterChannel(NoticeChannel).RegisterMessageType<SeamWrapNotice>()
                .SetMessageHandler<SeamWrapNotice>(n => Notice(n.EntityId, n.Laps));
            laps = new GameFeature("keeping other creatures and players on the lap nearest the player", null, Mod.Logger);
            try { BindInterpolation(laps); }
            catch (Exception e) { laps.Require(false, e.GetType().Name + ": " + e.Message); }
            laps.Apply();
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            // In singleplayer both sides run in one process: each clears only its own statics
            if (harmony != null)
            {
                serverApi = null;
                noticeChannel = null;
                AnnouncesWraps = false;
            }
            else ClearNotices();
            base.Dispose();
        }

        // ---------------------------------------------------------------- server

        static GameFeature tracking;

        /// <summary>
        /// Takes <c>x - clientX</c> around the ring: x is the local vanilla stores from the entity's Pos.X, and this is the
        /// one difference of it and an element of the players' position array. If vanilla changes so that there is no
        /// such difference, or more than one, the feature switches off instead of guessing which axis it is.
        /// </summary>
        static IEnumerable<CodeInstruction> RingDistance(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var getX = AccessTools.PropertyGetter(typeof(EntityPos), nameof(EntityPos.X));
            var xLocals = new HashSet<int>();
            for (int i = 0; i + 1 < list.Count; i++)
                if (list[i].Calls(getX) && list[i + 1].IsStloc()) xLocals.Add(list[i + 1].LocalIndex());
            int found = 0, at = -1;
            if (xLocals.Count == 1)
            {
                int x = xLocals.First();
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[i].opcode != OpCodes.Sub || list[i - 1].opcode != OpCodes.Ldelem_R8) continue;
                    // The subtracted array element is one expression; what is pushed right before it is the minuend
                    int start = ExpressionStart(list, i - 1);
                    if (start > 0 && list[start - 1].IsLdloc() && list[start - 1].LocalIndex() == x)
                    {
                        found++;
                        at = i;
                    }
                }
            }
            if (xLocals.Count != 1 || found != 1)
            {
                tracking?.Off(xLocals.Count != 1 ? "UpdateTrackedEntityState reads the entity's x " + xLocals.Count + " times"
                    : "UpdateTrackedEntityState has " + found + " differences of x and a player's x, not one");
                return list;
            }
            list.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SeamEntities), nameof(RingDx), new[] { typeof(double) })));
            return list;
        }

        /// <summary>Where the straight-line IL that leaves the one value <paramref name="end"/> pushes starts, or -1 (a
        /// branch, a jump target inside it, or an instruction whose stack use isn't simple).</summary>
        static int ExpressionStart(List<CodeInstruction> list, int end)
        {
            int need = 1;
            for (int i = end; i >= 0; i--)
            {
                var op = list[i].opcode;
                int pop = Pops(list[i]), push = Pushes(list[i]);
                if (pop < 0 || push < 0 || op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch) return -1;
                need += pop - push;
                if (need == 0) return i;
                if (need < 0 || list[i].labels.Count > 0) return -1;
            }
            return -1;
        }

        static int Pops(CodeInstruction ci)
        {
            switch (ci.opcode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                case StackBehaviour.Popref_popi_pop1: return 3;
                case StackBehaviour.Varpop when (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo m:
                    return m.GetParameters().Length + (m.IsStatic ? 0 : 1);
                default: return -1;
            }
        }

        static int Pushes(CodeInstruction ci)
        {
            switch (ci.opcode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush when ci.operand is MethodInfo m: return m.ReturnType == typeof(void) ? 0 : 1;
                default: return -1;
            }
        }

        /// <summary>An x difference in the server's entity tracking brought to the nearest lap: within half a
        /// circumference.</summary>
        public static double RingDx(double dx) => LapDx(dx, CylinderWorldCore.ServerShared);

        /// <summary>An x difference within half a circumference of <paramref name="p"/> (dx itself without a seam).</summary>
        static double LapDx(double dx, SeamParams p)
        {
            double c = p?.Circumference ?? 0;
            return c > 0 && Math.Abs(dx) > c / 2 ? dx - c * Math.Round(dx / c) : dx;
        }

        static ICoreServerAPI serverApi;
        static IServerNetworkChannel noticeChannel;

        /// <summary>Whether this server announces its wraps (set by the first AnnounceWrap, or up front by the wrap system);
        /// players who join then are told so at once.</summary>
        internal static volatile bool AnnouncesWraps;

        /// <summary>The one rule for who travels with a carrier (CW-0021, CW-P09): the player is the carrier or sits on it.
        /// The wrap takes every online player it holds along (SeamWrapSystem.PlayersAboard); the notice goes to those whose
        /// client is playing.</summary>
        internal static bool Aboard(IPlayer plr, Entity carrier) => plr?.Entity is { } ep && (ep == carrier || ep.MountedOn?.Entity == carrier);

        /// <summary>
        /// Server, just before a seam wrap teleports <paramref name="carrier"/> by <paramref name="laps"/> circumferences
        /// (±1): tells the players who are it or ride it, so their client takes that teleport, and only that one, for a
        /// seam jump (SeamSmoothJump). Sent before the teleport on the same ordered connection as the player's teleport
        /// packet, which therefore always finds it.
        /// </summary>
        internal static void AnnounceWrap(Entity carrier, int laps)
        {
            var channel = noticeChannel;
            var api = serverApi;
            if (channel == null || api == null || carrier == null || laps == 0) return;
            AnnouncesWraps = true;
            // Those aboard whose client can take a packet now
            var aboard = api.World.AllOnlinePlayers.OfType<IServerPlayer>()
                .Where(p => p.ConnectionState == EnumClientState.Playing && Aboard(p, carrier)).ToArray();
            if (aboard.Length > 0) channel.SendPacket(new SeamWrapNotice { EntityId = carrier.EntityId, Laps = Math.Sign(laps) }, aboard);
        }

        // ---------------------------------------------------------------- client

        static GameFeature laps;

        /// <summary>
        /// Other creatures and players are kept on the lap nearest the player. The one switch for every path that moves
        /// them (their spawn, the player's jump, their position and teleport packets: SeamSmoothJump), so their position
        /// and the positions their interpolation is between are always on one lap. While it is off (a game without the
        /// interpolation members, or a failure) they stay where the server has them, as without the seam; the player's
        /// own smooth jump doesn't depend on it.
        /// </summary>
        internal static bool LapsOn => laps?.On == true;

        /// <summary>The player's own entity or what they ride: they are where the client puts them.</summary>
        internal static bool Own(Entity e)
        {
            var player = SeamJumpClient.Capi?.World?.Player?.Entity;
            return player != null && e != null && (e == player || e == player.MountedOn?.Entity);
        }

        /// <summary>x moved by whole circumferences to the lap nearest the player (x itself without a seam, or while
        /// <see cref="LapsOn"/> is false).</summary>
        internal static double NearestLap(double x)
        {
            var player = SeamJumpClient.Capi?.World?.Player?.Entity;
            return player == null || !LapsOn ? x : player.Pos.X + LapDx(x - player.Pos.X, CylinderWorldCore.ClientShared);
        }

        void OnEntityArrived(Entity e)
        {
            if (!LapsOn || e == null || Own(e) || e.Pos.Dimension != 0) return;
            try { ToNearestLap(e); }
            catch (Exception ex) { laps.Fail(ex); }
        }

        /// <summary>After the player's own jump: every other entity onto the player's new lap.</summary>
        internal static void AfterJump()
        {
            var world = SeamJumpClient.Capi?.World;
            if (!LapsOn || world == null) return;
            try
            {
                foreach (var e in world.LoadedEntities.Values)
                {
                    if (!Own(e) && e.Pos.Dimension == 0) ToNearestLap(e);
                }
            }
            catch (Exception ex) { laps.Fail(ex); }
        }

        /// <summary>Puts an entity on the client on the lap nearest the player, with each position its interpolation is
        /// between (each on its own: a packet that arrived after the player's teleport is already on the new lap).</summary>
        static void ToNearestLap(Entity e)
        {
            e.Pos.X = NearestLap(e.Pos.X);
            if (e.PreviousServerPos != null) e.PreviousServerPos.X = NearestLap(e.PreviousServerPos.X);
            MoveInterpolationFields(e, NearestLap);
        }

        /// <summary>For the player's own jump (SeamSmoothJump.Translate): the x of every position an entity's
        /// interpolation is between (EntityBehaviorInterpolatePosition: the last, the next and the queued ones), changed by
        /// <paramref name="move"/>. Nothing while the lap feature is off; a failure here switches off that feature only, not
        /// the smooth jump.</summary>
        internal static void MoveInterpolation(Entity e, System.Func<double, double> move)
        {
            if (!LapsOn) return;
            try { MoveInterpolationFields(e, move); }
            catch (Exception ex) { laps.Fail(ex); }
        }

        // Separate so that a game without these members only fails the check in BindInterpolation, never a caller's JIT.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void MoveInterpolationFields(Entity e, System.Func<double, double> move)
        {
            var interpolation = e.GetBehavior<EntityBehaviorInterpolatePosition>();
            if (interpolation == null) return;
            interpolation.pL.x = move(interpolation.pL.x);
            interpolation.pN.x = move(interpolation.pN.x);
            if (interpolation.positionQueue.Count == 0) return;
            var queued = interpolation.positionQueue.ToArray();
            interpolation.positionQueue.Clear();
            foreach (var snapshot in queued)
            {
                var moved = snapshot;
                moved.x = move(moved.x);
                interpolation.positionQueue.Enqueue(moved);
            }
        }

        static void BindInterpolation(GameFeature f)
        {
            var t = f.Type("EntityBehaviorInterpolatePosition");
            var snapshot = f.Type("PositionSnapshot");
            f.Field(t, "pL", snapshot);
            f.Field(t, "pN", snapshot);
            f.Field(snapshot, "x", typeof(double));
            var queue = snapshot == null ? null : typeof(Queue<>).MakeGenericType(snapshot);
            f.Field(t, "positionQueue", queue);
        }

        static readonly Dictionary<long, (int Laps, long At)> notices = new();
        /// <summary>A notice is used by the teleport it announces; one left unused this long (the teleport waits for the
        /// destination to load, SeamWrapSystem gives up after 10 s) no longer counts.</summary>
        const long NoticeMs = 10000;

        /// <summary>Client: this server announces its wraps, so a teleport of about one ring without a notice is not a
        /// seam jump. Until a server says so (an older server, or one whose wrap system doesn't announce yet), a jump is
        /// still inferred from the teleport's distance alone.</summary>
        internal static bool WrapsAnnounced { get; private set; }

        /// <summary>A wrap notice (or, with laps 0, the server saying that it sends them). Main thread.</summary>
        internal static void Notice(long entityId, int laps)
        {
            WrapsAnnounced = true;
            if (laps != 0) notices[entityId] = (Math.Sign(laps), SeamJumpClient.Capi?.ElapsedMilliseconds ?? 0);
        }

        /// <summary>Uses up the notice of a wrap of this entity by <paramref name="laps"/> circumferences, if there is one.</summary>
        internal static bool TakeNotice(long entityId, int laps)
        {
            if (!notices.TryGetValue(entityId, out var n)) return false;
            notices.Remove(entityId);
            long now = SeamJumpClient.Capi?.ElapsedMilliseconds ?? 0;
            return n.Laps == Math.Sign(laps) && now - n.At < NoticeMs;
        }

        internal static void ClearNotices()
        {
            notices.Clear();
            WrapsAnnounced = false;
        }
    }

    /// <summary>Server to the players aboard: <see cref="EntityId"/> is about to be wrapped by <see cref="Laps"/>
    /// circumferences (±1). Laps 0: this server announces its wraps.</summary>
    [ProtoContract]
    public sealed class SeamWrapNotice
    {
        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public int Laps;
    }
}
