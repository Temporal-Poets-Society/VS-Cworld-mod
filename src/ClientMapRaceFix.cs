using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace CylinderWorld
{
    /// <summary>
    /// Works around a race in the game client: the network thread adds map chunks to the client's dictionary (packet 17,
    /// in SystemNetworkProcess.ProcessInBackground) while the main thread removes them when chunks unload
    /// (SystemUnloadChunks.HandleChunkUnload), with no lock between them. Normally the two rarely meet; a seam jump
    /// unloads one whole area and loads another at the same moment, and after a few jumps the client crashed with "Index
    /// was outside the bounds of the array" in Dictionary.TryInsert. These patches make both writers take the same lock.
    /// Map regions (packet 42, ClientWorldMap.UnloadMapRegion) are a ConcurrentDictionary in 1.22 and need no lock; on a
    /// game version where they are a plain dictionary they are locked the same way.
    /// Readers (GetMapChunk on the main thread, the network thread's chunk loading) still take no lock. What breaks them
    /// is the dictionary growing under them, so it gets room for twice the largest view area up front (CW-D30 keeps both
    /// sides of the seam on the client) and doesn't grow while playing.
    /// Earth up to 2.0.0-rc.2 carried the same fix with its own Harmony id and lock; with both, each patched method takes
    /// both locks, always in the same order (each mod patches all its methods in one go in Start), so they can't deadlock.
    /// From 2.0.0-rc.3 Earth relies on this one (it requires Cylinder World).
    /// </summary>
    public class ClientMapRaceFix : ModSystem
    {
        public const string HarmonyId = "cylinderworld.clientmaprace";

        static readonly object MapLock = new object();
        static GameFeature race;
        /// <summary>Map regions are kept in a plain dictionary on this game version: lock them too.</summary>
        static bool lockRegions;
        Harmony harmony;
        GameFeature room;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void Start(ICoreAPI api)
        {
            harmony = new Harmony(HarmonyId);
            race = new GameFeature("the client map race fix", harmony, Mod.Logger);
            try { Bind(race, Mod.Logger); }
            catch (Exception e) { race.Require(false, e.GetType().Name + ": " + e.Message); }
            race.Apply();
        }

        static void Bind(GameFeature f, ILogger log)
        {
            var net = f.Type("Vintagestory.Client.NoObf.SystemNetworkProcess");
            var packet = f.Type("Packet_Server");
            f.Field(packet, "Id", typeof(int));
            var map = f.Type("Vintagestory.Client.NoObf.ClientWorldMap");
            lockRegions = map != null && RegionsNeedLock(new GameFeature("locking the client's map regions too (where they are a plain dictionary)", null, log), map);
            var finalizer = new HarmonyMethod(typeof(ClientMapRaceFix), nameof(Release));
            var always = new HarmonyMethod(typeof(ClientMapRaceFix), nameof(LockAlways));
            if (packet != null)
                f.Patch(f.Method(net, "ProcessInBackground", new[] { packet }), prefix: new HarmonyMethod(typeof(ClientMapRaceFix), nameof(LockForMapPackets)), finalizer: finalizer);
            f.Patch(f.Method(f.Type("Vintagestory.Client.NoObf.SystemUnloadChunks"), "HandleChunkUnload"), prefix: always, finalizer: finalizer);
            if (lockRegions) f.Patch(f.Method(map, "UnloadMapRegion"), prefix: always, finalizer: finalizer);
        }

        /// <summary>Whether this game keeps the client's map regions in a plain dictionary, which the race fix then locks
        /// too. A check of its own (<paramref name="inspect"/>): a game without the field says so in one line and keeps
        /// the map chunk fix, with the regions unlocked as 1.22 needs.</summary>
        internal static bool RegionsNeedLock(GameFeature inspect, Type map)
        {
            var regions = inspect.Field(map, "MapRegions");
            return inspect.Apply() && !(regions.FieldType.IsGenericType && regions.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>));
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            room = new GameFeature("room in the client's map chunk table (a rare crash after seam jumps)", null, Mod.Logger);
            try { BindRoom(room); }
            catch (Exception e) { room.Require(false, e.GetType().Name + ": " + e.Message); }
            if (room.Apply()) api.Event.LevelFinalize += () => MakeRoom(api);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }

        // ---------------------------------------------------------------- the lock

        // Typed packet: no reflection or argument array for every server packet on the network thread
        static void LockForMapPackets(Packet_Server packet, out bool __state)
        {
            __state = false;
            if (packet == null || !race.On) return;
            int id = packet.Id;
            if (id != 17 && (id != 42 || !lockRegions)) return; // map chunk (map region where that is a plain dictionary)
            Monitor.Enter(MapLock);
            __state = true;
        }

        static void LockAlways(out bool __state)
        {
            __state = false;
            if (!race.On) return;
            Monitor.Enter(MapLock);
            __state = true;
        }

        static Exception Release(Exception __exception, bool __state)
        {
            if (__state) Monitor.Exit(MapLock);
            return __exception;
        }

        // ---------------------------------------------------------------- room for the readers

        /// <summary>Map chunks are columns: a view distance of r chunks holds about (2r + 3)² of them, and near the seam
        /// the client keeps both sides. Beyond this view distance (blocks) the table may still grow.</summary>
        const int MaxRoomViewDistance = 2048;

        static AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientMapChunk>> mapChunks;

        static void BindRoom(GameFeature f)
        {
            f.Field(f.Type("Vintagestory.Client.NoObf.ClientMain"), "WorldMap", typeof(ClientWorldMap));
            f.Field(typeof(ClientWorldMap), "MaxViewDistance", typeof(int));
            var chunks = f.Field(typeof(ClientWorldMap), "MapChunks", typeof(Dictionary<long, ClientMapChunk>));
            if (chunks != null) mapChunks = AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientMapChunk>>(chunks);
        }

        void MakeRoom(ICoreClientAPI api)
        {
            if (!room.On) return;
            try { EnsureRoom(api.World); }
            catch (Exception e) { room.Fail(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void EnsureRoom(IClientWorldAccessor world)
        {
            var map = (world as ClientMain)?.WorldMap;
            if (map == null) return;
            // The server's limit (packet 4, before LevelFinalize): the client never loads more than that
            int r = Math.Clamp(map.MaxViewDistance, 0, MaxRoomViewDistance) / 32 + 1, side = 2 * r + 3;
            // Under the lock: the network thread may be adding a map chunk right now
            lock (MapLock) mapChunks(map).EnsureCapacity(2 * side * side);
        }
    }
}
