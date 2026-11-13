using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Getter = System.Func<object, object>;
using SetGetter = System.Func<object, System.Collections.Generic.HashSet<long>>;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CylinderWorld
{
    /// <summary>
    /// Loads the other side of the seam while a player approaches a jump line and sends it to their client before the
    /// jump, so the far side is already drawn when they come out.
    ///  - Where: a disc around the point where they will come out (their line crossing, projected along their heading
    ///    and mirrored), plus a strip further ahead along that heading. Nearest first.
    ///  - When: PrepareDistance blocks before the line, or PrepareSeconds ahead at their current speed (fast ships).
    ///  - Map regions first: a chunk meshed before its region arrives gets a placeholder climate and no ocean data
    ///    (water tint, Sea's wave strength), and the client can't hold weather for it. Each column waits for its region.
    ///  - Kept: whenever a player enters a new chunk, vanilla unloads every chunk it sent them outside their view radius
    ///    (ServerSystemUnloadChunks.SendOutOfRangeChunkUnloads). That threw the pre-sent side away again before the
    ///    jump. A Harmony patch hides the prepared chunks from that check until the player jumps or leaves the zone.
    ///  - Both sides while near the line: at a jump the side left behind becomes the prepared side (kept, not sent
    ///    again), so crossing back and forth reloads nothing. It has to be kept before the teleport: vanilla's teleport
    ///    callback unloads everything out of range at once (EntityPlayer.Onplrteleported → PlayerChunkTransition).
    ///  - Leaving the zone, the kept side is released gradually: still hidden for UnloadOldSideDelay, then a few columns
    ///    every quarter second to vanilla's own unload (all at once, the client stalled for a frame handling it).
    /// </summary>
    public class SeamPreloader
    {
        const string HarmonyId = "cylinderworld.preload";
        const double MaxLead = 512, ZoneMargin = 64;
        const int RequestsPerTick = 16, SendsPerTick = 8;
        /// <summary>A drain releases its columns every DrainBatchTicks × 50 ms: each release runs vanilla's whole out-of-range
        /// scan (and the patch hides and restores every kept chunk around it), too much to do every tick.</summary>
        const int DrainBatchTicks = 5;

        enum Col : byte { Requested, Loaded, Sent }

        sealed class Drain
        {
            public object Client;
            public readonly HashSet<long> Hidden = new(), ColumnSet = new();
            public readonly List<long> Columns = new();
            /// <summary>Columns per 50 ms (released PerTick × DrainBatchTicks at a time).</summary>
            public int Next, PerTick;
            public long ReleaseAt, LastFresh, NextBatch;
        }

        sealed class Approach
        {
            public double LastX = double.NaN, LastZ;
            public long LastMs;
            public double Vx, Vz;
            public readonly Dictionary<long, Col> Window = new();
            /// <summary>Window entries in state Loaded: Send has nothing to do while there are none.</summary>
            public int Loaded;
            public long LastFresh;
            public int Radius;
            public void Set(long key, Col state)
            {
                if (Window.TryGetValue(key, out var old) && old == Col.Loaded) Loaded--;
                Window[key] = state;
                if (state == Col.Loaded) Loaded++;
            }
            public void Remove(long key)
            {
                if (Window.Remove(key, out var old) && old == Col.Loaded) Loaded--;
            }
            public void Clear()
            {
                Window.Clear();
                Loaded = 0;
            }
            /// <summary>For the jump's log line.</summary>
            public int JumpWindow, JumpSent, JumpLeft, JumpDrained;
        }

        /// <summary>Per player: chunk indices (3D) that vanilla's out-of-range unload must leave alone. Main thread only.</summary>
        static readonly Dictionary<string, HashSet<long>> keep = new();
        /// <summary>Per player: the side they jumped away from, unloaded a few columns at a time. Main thread only.</summary>
        static readonly Dictionary<string, Drain> drains = new();
        /// <summary>Keeping the prepared chunks on the client: the patch on vanilla's out-of-range unload. Static for the patch.</summary>
        static GameFeature keepFeature;
        /// <summary>ConnectedClient.Player and .ChunkSent, compiled: the patch reads them at every player's chunk change.</summary>
        static Getter connPlayer;
        static SetGetter connChunkSent;

        readonly ICoreServerAPI sapi;
        readonly CylinderWorldCore core;
        readonly ILogger logger;
        readonly Dictionary<string, Approach> approaches = new();
        readonly ConcurrentQueue<(string uid, long key)> loaded = new();
        readonly Dictionary<string, (int radius, long at)> radii = new();
        // Reused every tick (main thread only)
        readonly List<(long key, int cx, int cz, int prio)> targets = new();
        readonly HashSet<long> current = new();
        readonly List<(long key, int dist)> old = new();
        readonly List<long> dropped = new();
        readonly List<(long key, int dist, int order)> sendOrder = new();
        // Each part switches off alone (one log line) when a game update drops what it reads; the field types are checked
        // here, so a changed one is reported at start instead of failing a cast every tick.
        /// <summary>The client's sent and queued sets: regions first, nothing sent twice, the send throttle. Off: the far
        /// side is still loaded and sent, in plain order, and not kept.</summary>
        readonly GameFeature clientFeature;
        /// <summary>Releasing the side left behind a few columns at a time through vanilla's own unload. Off: all at once.</summary>
        readonly GameFeature drainFeature;
        /// <summary>Vanilla's chunk radius for a client. Off: the player's approved view distance.</summary>
        readonly GameFeature radiusFeature;
        readonly Getter playerClient;
        readonly SetGetter connMapRegionSent, connMapChunkSent, connForceRegions, connForceMapChunks, connForceChunks;
        readonly MethodInfo allowedRadius, unloadOutOfRange;
        readonly object unloadSystem;

        static bool Keeping => keepFeature?.On == true;

        public SeamPreloader(ICoreServerAPI sapi, CylinderWorldCore core, ILogger logger)
        {
            this.sapi = sapi;
            this.core = core;
            this.logger = logger;

            // Anything that throws while a feature binds (a game update's overload, say) switches off that feature alone, with
            // one line, like a missing member: the player tick that crosses the seam must never lose its pre-loader to it.
            clientFeature = new GameFeature("seam pre-sending in order (map regions first)", null, logger);
            Type conn = null;
            try
            {
                conn = clientFeature.Type("Vintagestory.Server.ConnectedClient");
                playerClient = Compile<object>(clientFeature, clientFeature.Field(clientFeature.Type("Vintagestory.Server.ServerPlayer"), "client", conn));
                connPlayer = Compile<object>(clientFeature, clientFeature.Field(conn, "Player", typeof(IServerPlayer)));
                SetGetter Set(string name) => Compile<HashSet<long>>(clientFeature, clientFeature.Field(conn, name, typeof(HashSet<long>)));
                connChunkSent = Set("ChunkSent");
                connMapRegionSent = Set("MapRegionSent");
                connMapChunkSent = Set("MapChunkSent");
                connForceRegions = Set("forceSendMapRegions");
                connForceMapChunks = Set("forceSendMapChunks");
                connForceChunks = Set("forceSendChunks");
            }
            catch (Exception e) { Unbound(clientFeature, e); }
            clientFeature.Apply();

            keepFeature = new GameFeature("keeping the pre-sent seam side on the client", new Harmony(HarmonyId), logger);
            Type unloadType = null;
            MethodInfo unload = null;
            try
            {
                unloadType = keepFeature.Type("Vintagestory.Server.ServerSystemUnloadChunks");
                unload = keepFeature.Method(unloadType, "SendOutOfRangeChunkUnloads", conn == null ? null : new[] { conn });
                keepFeature.Require(clientFeature.On, "client chunk sets (see the line above)");
                keepFeature.Patch(unload, prefix: new HarmonyMethod(typeof(SeamPreloader), nameof(HidePrepared)),
                    finalizer: new HarmonyMethod(typeof(SeamPreloader), nameof(RestorePrepared)));
            }
            catch (Exception e) { Unbound(keepFeature, e); }
            keepFeature.Apply();

            // Vanilla runs it on the chunk thread, but calls its out-of-range unload from the main thread, as we do
            drainFeature = new GameFeature("unloading the seam side left behind gradually", null, logger);
            try
            {
                var systems = drainFeature.Field(sapi.World.GetType(), "Systems", typeof(Array))?.GetValue(sapi.World) as Array;
                unloadSystem = systems?.Cast<object>().FirstOrDefault(sys => unloadType?.IsInstanceOfType(sys) == true);
                drainFeature.Require(unloadSystem != null, "chunk unloading system");
                drainFeature.Require(Keeping, "patch keeping the pre-sent side (see the line above)");
                unloadOutOfRange = unload;
            }
            catch (Exception e) { Unbound(drainFeature, e); }
            drainFeature.Apply();

            radiusFeature = new GameFeature("the exact view radius for seam pre-sending", null, logger);
            try
            {
                allowedRadius = radiusFeature.Method(sapi.World.GetType(), "GetAllowedChunkRadius", conn == null ? null : new[] { conn });
                radiusFeature.Require(allowedRadius?.ReturnType == typeof(int), "int GetAllowedChunkRadius");
                radiusFeature.Require(clientFeature.On, "client access (see the line above)");
            }
            catch (Exception e) { Unbound(radiusFeature, e); }
            radiusFeature.Apply();

            static void Unbound(GameFeature feature, Exception e) => feature.Require(false, "its game members as they were (" + e.GetType().Name + ": " + e.Message + ")");
        }

        /// <summary>A compiled getter for a reflected field (null, and the feature off, if it can't be built).</summary>
        static System.Func<object, T> Compile<T>(GameFeature feature, FieldInfo field)
        {
            if (field == null) return null;
            try
            {
                var o = Expression.Parameter(typeof(object));
                return Expression.Lambda<System.Func<object, T>>(Expression.Convert(Expression.Field(Expression.Convert(o, field.DeclaringType), field), typeof(T)), o).Compile();
            }
            catch (Exception e)
            {
                feature.Require(false, "a readable " + field.Name + " (" + e.Message + ")");
                return null;
            }
        }

        /// <summary>The player's ConnectedClient, or null when the client sets aren't available.</summary>
        object ClientOf(IServerPlayer plr) => Read(playerClient, plr);

        /// <summary>A compiled getter's value (null while the client sets are off). One that throws at runtime (a player
        /// object that isn't the game's own, a changed invariant) switches only this feature off, with one line: these
        /// run inside the player tick that does the crossing, which then carries on without them.</summary>
        T Read<T>(System.Func<object, T> getter, object from) where T : class
        {
            if (from == null || !clientFeature.On) return null;
            try { return getter(from); }
            catch (Exception e) { clientFeature.Fail(e); return null; }
        }

        /// <summary>Every player tick: track the carrier's speed (needed before they reach the zone).</summary>
        public void Track(IServerPlayer plr, Entity carrier)
        {
            var a = Get(plr.PlayerUID);
            long now = sapi.World.ElapsedMilliseconds;
            double x = carrier.Pos.X, z = carrier.Pos.Z;
            if (!double.IsNaN(a.LastX) && now > a.LastMs)
            {
                double dt = (now - a.LastMs) / 1000.0, dx = x - a.LastX, dz = z - a.LastZ;
                // Magelan ships set their position directly, so Pos.Motion isn't their speed; a jump is no speed at all
                if (Math.Abs(dx) < 200 && Math.Abs(dz) < 200 && dt < 2)
                {
                    const double k = 0.3;
                    a.Vx += (dx / dt - a.Vx) * k;
                    a.Vz += (dz / dt - a.Vz) * k;
                }
            }
            a.LastX = x;
            a.LastZ = z;
            a.LastMs = now;
        }

        /// <summary>Near a jump line: grow the prepared window, load it, send what is loaded (regions first).</summary>
        public void Prepare(IServerPlayer plr, Entity carrier, SeamParams p)
        {
            var cfg = core.Config;
            var a = Get(plr.PlayerUID);
            double x = carrier.Pos.X, z = carrier.Pos.Z;
            bool east = x > p.WorldWidth / 2.0;
            double toLine = p.DistToJumpLine(x);
            double toward = east ? a.Vx : -a.Vx;
            double lead = Math.Min(MaxLead, Math.Max(cfg.PrepareDistance, toward * cfg.PrepareSeconds));
            // A little further out once prepared, so hovering at the edge doesn't send and drop the other side over and over
            if (toLine > lead + (a.Window.Count > 0 ? ZoneMargin : 0))
            {
                Forget(plr, true);
                return;
            }

            // Where they will come out: the line crossing (projected along the heading), on the other side
            double outX = p.Mirror(east ? p.WorldWidth - p.JumpInset : p.JumpInset);
            double outZ = toward > 0.5 ? Math.Clamp(z + a.Vz * toLine / toward, 0, p.WorldLength - 1) : z;
            double speed = Math.Sqrt(a.Vx * a.Vx + a.Vz * a.Vz);
            double hx = speed > 0.5 ? a.Vx / speed : (east ? 1 : -1), hz = speed > 0.5 ? a.Vz / speed : 0;

            const int cs = 32;
            int ocx = (int)(outX / cs), ocz = (int)(outZ / cs);
            int maxCx = sapi.WorldManager.MapSizeX / cs, maxCz = sapi.WorldManager.MapSizeZ / cs;
            int r = Math.Max(1, cfg.PreSendDiscRadius), ahead = Math.Max(0, cfg.PreSendAhead);
            if (cfg.PreSendFullView) r = Math.Max(r, ViewRadius(plr, p));
            int span = r + ahead;
            a.Radius = r;
            targets.Clear();
            for (int dx = -span; dx <= span; dx++)
            {
                for (int dz = -span; dz <= span; dz++)
                {
                    double along = dx * hx + dz * hz, across = Math.Abs(dx * hz - dz * hx);
                    if (dx * dx + dz * dz > r * r && !(along >= 0 && along <= span && across <= 3)) continue;
                    int cx = ocx + dx, cz = ocz + dz;
                    if (cx < 0 || cz < 0 || cx >= maxCx || cz >= maxCz) continue;
                    targets.Add((Key(cx, cz), cx, cz, dx * dx + dz * dz));
                }
            }
            targets.Sort((m, n) => m.prio.CompareTo(n.prio));

            // The window keeps what the wobbling projected crossing asked for, within a cap. Old columns are dropped
            // first (far ones always, then as many as the current targets need), or a crossing that moved, such as
            // sailing along the seam, would fill the cap and the real destination would never be prepared.
            string uid = plr.PlayerUID;
            current.Clear();
            foreach (var t in targets) current.Add(t.key);
            int cap = 2 * targets.Count, requested = 0;
            DropStale(plr, a, current, ocx, ocz, span + 4, cap);
            foreach (var t in targets)
            {
                if (requested >= RequestsPerTick) break;
                if (a.Window.ContainsKey(t.key)) continue;
                a.Set(t.key, Col.Requested);
                long key = t.key;
                sapi.WorldManager.LoadChunkColumnPriority(t.cx, t.cz, new ChunkLoadOptions { OnLoaded = () => loaded.Enqueue((uid, key)) });
                requested++;
            }

            KeepFresh(a);
            if (cfg.PreSendChunks && a.Loaded > 0) Send(plr, a, ocx, ocz);
        }

        /// <summary>
        /// Forget prepared columns that aren't current targets: always those further than keepRadius chunks from the
        /// projected exit, and then, nearest the exit last, as many more as needed to fit every current target within
        /// the cap. Stops keeping them on the client too, gradually (Unload).
        /// </summary>
        void DropStale(IServerPlayer plr, Approach a, HashSet<long> current, int ocx, int ocz, int keepRadius, int cap)
        {
            int missing = 0;
            foreach (long key in current) if (!a.Window.ContainsKey(key)) missing++;
            old.Clear();
            foreach (long key in a.Window.Keys)
            {
                if (!current.Contains(key)) old.Add((key, Math.Max(Math.Abs(Cx(key) - ocx), Math.Abs(Cz(key) - ocz))));
            }
            if (old.Count == 0) return;
            old.Sort((m, n) => n.dist.CompareTo(m.dist));
            int over = Math.Max(0, a.Window.Count + missing - cap);
            keep.TryGetValue(plr.PlayerUID, out var kept);
            int chunksY = sapi.WorldManager.MapSizeY / 32;
            dropped.Clear();
            foreach (var (key, dist) in old)
            {
                if (dist <= keepRadius && over <= 0) break;
                a.Remove(key);
                over--;
                if (kept == null) continue;
                for (int cy = 0; cy < chunksY; cy++)
                {
                    long index = sapi.WorldManager.ChunkIndex3D(Cx(key), cy, Cz(key));
                    if (kept.Remove(index)) dropped.Add(index);
                }
            }
            if (dropped.Count > 0) AddDrain(plr, dropped, 0);
        }

        /// <summary>
        /// The player's view radius in chunks (vanilla's allowed radius, plus one: vanilla sends an octagon), but no
        /// further than the seam mist lets anything through (1 %), which with the default mist is 13 chunks.
        /// </summary>
        int ViewRadius(IServerPlayer plr, SeamParams p)
        {
            int view = AllowedRadius(plr) + 1;
            if (p.MistDensity > 0) view = Math.Min(view, (int)Math.Ceiling(Math.Log(100) / p.MistDensity / 32) + 1);
            return view;
        }

        /// <summary>Vanilla's chunk radius for this player (what it sends and keeps), looked up at most once a second.</summary>
        int AllowedRadius(IServerPlayer plr)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (radii.TryGetValue(plr.PlayerUID, out var known) && now - known.at < 1000) return known.radius;
            int radius = (int)Math.Ceiling(plr.WorldData.LastApprovedViewDistance / 32.0);
            object client = radiusFeature.On ? ClientOf(plr) : null;
            if (client != null)
            {
                try { radius = (int)allowedRadius.Invoke(sapi.World, new[] { client }); }
                catch (Exception e) { radiusFeature.Fail(e.InnerException ?? e); }
            }
            radii[plr.PlayerUID] = (radius, now);
            return radius;
        }

        /// <summary>Main thread, before Prepare: columns that finished loading.</summary>
        public void TakeLoaded()
        {
            while (loaded.TryDequeue(out var e))
            {
                if (approaches.TryGetValue(e.uid, out var a) && a.Window.TryGetValue(e.key, out var state) && state == Col.Requested)
                {
                    a.Set(e.key, Col.Loaded);
                }
            }
        }

        void Send(IServerPlayer plr, Approach a, int ocx, int ocz)
        {
            object client = ClientOf(plr);
            var has = Read(connChunkSent, client);
            int chunksY = sapi.WorldManager.MapSizeY / 32, sent = 0;
            // SendChunk only queues; vanilla sends that queue a batch per tick in no particular order. Queue more only once
            // it is nearly empty, so the columns nearest the exit really go first.
            if (Read(connForceChunks, client) is HashSet<long> queued && queued.Count > chunksY * SendsPerTick) return;
            HashSet<long> kept = null;
            // Nearest the exit first; equal distances in window order (as a stable sort would)
            sendOrder.Clear();
            int order = 0;
            foreach (var (k, state) in a.Window) if (state == Col.Loaded) sendOrder.Add((k, Sq(Cx(k) - ocx) + Sq(Cz(k) - ocz), order++));
            sendOrder.Sort((m, n) => m.dist != n.dist ? m.dist.CompareTo(n.dist) : m.order.CompareTo(n.order));
            foreach (var (key, _, _) in sendOrder)
            {
                if (sent >= SendsPerTick) break;
                int cx = Cx(key), cz = Cz(key);
                if (client != null && !RegionReady(client, cx, cz)) continue;
                for (int cy = 0; cy < chunksY; cy++)
                {
                    long index = sapi.WorldManager.ChunkIndex3D(cx, cy, cz);
                    // Per player (vanilla's ResendMapChunk goes to everyone): queues the map chunk too if it wasn't sent.
                    // Not again if the client still has it: it would be replaced and meshed again.
                    if (has == null || !has.Contains(index)) sapi.WorldManager.SendChunk(cx, cy, cz, plr, false);
                    if (Keeping)
                    {
                        if (kept == null && !keep.TryGetValue(plr.PlayerUID, out kept)) keep[plr.PlayerUID] = kept = new HashSet<long>();
                        kept.Add(index);
                    }
                }
                a.Set(key, Col.Sent);
                sent++;
            }
        }

        /// <summary>True once the client has the column's map region; until then asks for it (and the map chunk). True
        /// (plain order) once the client sets are off, also when reading them throws here (Read).</summary>
        bool RegionReady(object client, int cx, int cz)
        {
            if (!clientFeature.On) return true;
            try
            {
                int rs = sapi.WorldManager.RegionSize;
                long region = sapi.WorldManager.MapRegionIndex2D(cx * 32 / rs, cz * 32 / rs);
                if (connMapRegionSent(client).Contains(region)) return true;
                connForceRegions(client).Add(region);
                // Vanilla flushes forced regions only in a tick that also sends forced chunks or map chunks
                long mapChunk = sapi.WorldManager.MapChunkIndex2D(cx, cz);
                if (!connMapChunkSent(client).Contains(mapChunk)) connForceMapChunks(client).Add(mapChunk);
                return false;
            }
            catch (Exception e) { clientFeature.Fail(e); return true; }
        }

        /// <summary>
        /// The server unloads chunk columns that are far from every player after about 12 s. The destination is on
        /// the far side of the world until the jump, so keep marking the prepared columns fresh while approaching.
        /// A loaded or sent column that is gone anyway (unloaded before it was first marked, or by a command) leaves the
        /// window, so it is requested and sent again instead of counting as prepared.
        /// </summary>
        void KeepFresh(Approach a)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (now - a.LastFresh < 2000) return;
            a.LastFresh = now;
            dropped.Clear();
            foreach (var (key, state) in a.Window)
            {
                var mapChunk = sapi.WorldManager.GetMapChunk(Cx(key), Cz(key));
                if (mapChunk != null) mapChunk.MarkFresh();
                else if (state != Col.Requested) dropped.Add(key);
            }
            foreach (long key in dropped) a.Remove(key);
        }

        /// <summary>Left the zone: stop keeping their prepared chunks, gradually (with the whole view radius kept, vanilla
        /// would drop it in one packet at their next chunk change).</summary>
        void Forget(IServerPlayer plr, bool unload)
        {
            if (approaches.TryGetValue(plr.PlayerUID, out var a)) a.Clear();
            if (keep.Remove(plr.PlayerUID, out var kept) && unload && kept.Count > 0) AddDrain(plr, kept, 0);
        }

        /// <summary>
        /// Just before the teleport (vanilla's teleport callback unloads everything out of range at once): the side they
        /// are leaving becomes the prepared side, as far as the pre-send radius, already on the client and kept. What the
        /// client has of it beyond that radius is unloaded gradually. The destination stays kept until the window moves on.
        /// </summary>
        public void BeforeJump(IServerPlayer plr, SeamParams p, double fromX, double fromZ)
        {
            var a = Get(plr.PlayerUID);
            a.JumpWindow = a.Window.Count;
            a.JumpSent = a.JumpLeft = a.JumpDrained = 0;
            if (Read(connChunkSent, ClientOf(plr)) is not HashSet<long> sentSet) return;
            // Delivered, not just queued: every chunk of the column is in vanilla's sent set
            int chunksY = sapi.WorldManager.MapSizeY / 32;
            foreach (var (key, state) in a.Window)
            {
                bool all = state == Col.Sent;
                for (int cy = 0; all && cy < chunksY; cy++) all = sentSet.Contains(sapi.WorldManager.ChunkIndex3D(Cx(key), cy, Cz(key)));
                if (all) a.JumpSent++;
            }
            if (!Keeping) return;

            Indexing(out long mulX, out long mulZ);
            int ocx = (int)(fromX / 32), ocz = (int)(fromZ / 32), ncx = (int)(p.Wrap(fromX) / 32);
            int r = a.Radius > 0 ? a.Radius : ViewRadius(plr, p), reach = AllowedRadius(plr) + 2;
            if (!keep.TryGetValue(plr.PlayerUID, out var kept)) keep[plr.PlayerUID] = kept = new HashSet<long>();
            var beyond = new List<long>();
            var columns = new HashSet<long>();
            foreach (long index in sentSet)
            {
                if (index / mulX / mulZ >= 128) continue;   // other dimensions (vanilla's own test)
                int cx = (int)(index % mulX), cz = (int)(index / mulX % mulZ);
                int dx = cx - ocx, dz = cz - ocz;
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) > reach || Math.Abs(cx - ncx) <= reach) continue;
                if (dx * dx + dz * dz <= r * r)
                {
                    kept.Add(index);
                    if (columns.Add(Key(cx, cz))) a.Set(Key(cx, cz), Col.Sent);
                }
                else beyond.Add(index);
            }
            a.JumpLeft = columns.Count;
            a.JumpDrained = AddDrain(plr, beyond, (long)(core.Config.UnloadOldSideDelay * 1000));
        }

        /// <param name="wrapped">They came out on the other side (else they were put back in front of the line).</param>
        public void OnJumped(IServerPlayer plr, bool wrapped)
        {
            if (!approaches.TryGetValue(plr.PlayerUID, out var a)) return;
            a.LastX = double.NaN;
            if (!wrapped) return;
            logger.Notification("Seam jump {0}: {1} of {2} prepared columns delivered before it (radius {3}); side left behind: {4} columns kept, {5} beyond the radius unloaded gradually",
                plr.PlayerName, a.JumpSent, a.JumpWindow, a.Radius, a.JumpLeft, a.JumpDrained);
        }

        /// <summary>The 3D chunk index layout: index = (cy · mulZ + cz) · mulX + cx (vanilla's WorldMap.ChunkIndex3D).</summary>
        void Indexing(out long mulX, out long mulZ)
        {
            mulX = sapi.WorldManager.ChunkIndex3D(0, 0, 1);
            mulZ = sapi.WorldManager.ChunkIndex3D(0, 1, 0) / mulX;
        }

        /// <summary>Hide these chunks (3D indices) from vanilla's out-of-range unload, then from delayMs on release them
        /// to it a few columns at a time (Unload), over UnloadOldSideSeconds. Returns the number of columns added.</summary>
        int AddDrain(IServerPlayer plr, IEnumerable<long> indices, long delayMs)
        {
            var cfg = core.Config;
            object client = ClientOf(plr);
            if (!Keeping || !drainFeature.On || cfg.UnloadOldSideSeconds <= 0 || client == null) return 0;
            if (!drains.TryGetValue(plr.PlayerUID, out var d) || d.Client != client) drains[plr.PlayerUID] = d = new Drain { Client = client };
            Indexing(out long mulX, out long mulZ);
            int added = 0;
            foreach (long index in indices)
            {
                d.Hidden.Add(index);
                if (d.ColumnSet.Add(Key((int)(index % mulX), (int)(index / mulX % mulZ))))
                {
                    d.Columns.Add(Key((int)(index % mulX), (int)(index / mulX % mulZ)));
                    added++;
                }
            }
            d.ReleaseAt = Math.Max(d.ReleaseAt, sapi.World.ElapsedMilliseconds + delayMs);
            d.PerTick = Math.Max(1, (int)Math.Ceiling((d.Columns.Count - d.Next) / (cfg.UnloadOldSideSeconds * 20)));
            if (d.Next >= d.Columns.Count) drains.Remove(plr.PlayerUID);
            return added;
        }

        /// <summary>Every player tick (main thread): every DrainBatchTicks ticks, release the next columns of each drain to
        /// vanilla's out-of-range unload, as many per second as before. The rest stay hidden from it, on the client and
        /// loaded on the server.</summary>
        public void Unload()
        {
            if (drains.Count == 0) return;
            long now = sapi.World.ElapsedMilliseconds;
            int chunksY = sapi.WorldManager.MapSizeY / 32;
            List<string> done = null;
            foreach (var (uid, d) in drains)
            {
                if (now < d.ReleaseAt || now < d.NextBatch) continue;
                d.NextBatch = now + DrainBatchTicks * 50;
                if (now - d.LastFresh >= 2000)
                {
                    // Or the server unloads them by itself meanwhile and tells the client in one packet
                    d.LastFresh = now;
                    for (int i = d.Next; i < d.Columns.Count; i++) sapi.WorldManager.GetMapChunk(Cx(d.Columns[i]), Cz(d.Columns[i]))?.MarkFresh();
                }
                int released = 0;
                for (; released < d.PerTick * DrainBatchTicks && d.Next < d.Columns.Count; released++, d.Next++)
                {
                    long key = d.Columns[d.Next];
                    d.ColumnSet.Remove(key);
                    for (int cy = 0; cy < chunksY; cy++) d.Hidden.Remove(sapi.WorldManager.ChunkIndex3D(Cx(key), cy, Cz(key)));
                }
                if (released > 0)
                {
                    try { unloadOutOfRange.Invoke(unloadSystem, new[] { d.Client }); }
                    catch (Exception e)
                    {
                        // Off for good, not per drain: what is still hidden goes back to vanilla, which unloads it at once
                        drainFeature.Fail(e.InnerException ?? e);
                        drains.Clear();
                        return;
                    }
                }
                if (d.Next >= d.Columns.Count) (done ??= new List<string>()).Add(uid);
            }
            if (done != null) foreach (string uid in done) drains.Remove(uid);
        }

        public void OnLeave(string playerUid)
        {
            approaches.Remove(playerUid);
            radii.Remove(playerUid);
            keep.Remove(playerUid);
            drains.Remove(playerUid);
        }

        public void Dispose()
        {
            keepFeature?.Unpatch();
            keep.Clear();
            drains.Clear();
        }

        Approach Get(string uid)
        {
            if (!approaches.TryGetValue(uid, out var a)) approaches[uid] = a = new Approach();
            return a;
        }

        static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
        static int Cx(long key) => (int)(key >> 32);
        static int Cz(long key) => (int)(uint)key;
        static int Sq(int v) => v * v;

        // ---------------------------------------------------------------- the patch (server main thread)
        // In vanilla's chunk-transition path for every player: returns at once while the feature is off, and an exception
        // switches it off (one log line) instead of reaching vanilla. Hidden chunks are always put back.

        static void HidePrepared(object[] __args, out List<long> __state)
        {
            __state = null;
            if (!Keeping || keep.Count == 0 && drains.Count == 0 || __args.Length == 0 || __args[0] == null) return;
            List<long> hidden = null;
            try
            {
                if (connPlayer(__args[0]) is not IServerPlayer plr) return;
                var sent = connChunkSent(__args[0]);
                if (keep.TryGetValue(plr.PlayerUID, out var mine)) Hide(sent, mine);
                if (drains.TryGetValue(plr.PlayerUID, out var drain)) Hide(sent, drain.Hidden);
            }
            catch (Exception e) { keepFeature.Fail(e); }
            finally { __state = hidden; }

            void Hide(HashSet<long> sent, HashSet<long> indices)
            {
                foreach (long index in indices)
                {
                    if (sent.Remove(index)) (hidden ??= new List<long>()).Add(index);
                }
            }
        }

        static Exception RestorePrepared(Exception __exception, object[] __args, List<long> __state)
        {
            if (__state != null)
            {
                try
                {
                    var sent = connChunkSent(__args[0]);
                    foreach (long index in __state) sent.Add(index);
                }
                catch (Exception e) { keepFeature?.Fail(e); }
            }
            return __exception;
        }
    }
}
