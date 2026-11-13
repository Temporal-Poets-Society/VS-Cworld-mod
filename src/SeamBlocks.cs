using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.Server;

namespace CylinderWorld
{
    /// <summary>
    /// The generated seam columns are separate chunks, not views of shared storage. Record runtime changes in
    /// logical ring coordinates and apply them to both physical cells (CW-D36). The journal holds single cells, never
    /// whole chunks (loading an older save never recomputes terrain): a change reaches the other copy at once when it is
    /// loaded, else when it loads, and leaves the journal once both copies have held it across a world save. A copy that
    /// already has a cell's state is not written again.
    /// </summary>
    public class SeamBlocks : ModSystem
    {
        const string HarmonyId = "cylinderworld.seamblocks", SaveKey = "cylinderworld-seam-edits-v1";
        static SeamBlocks instance;
        /// <summary>Every patch and reflected game member below. A game update that drops one switches mirroring off as a
        /// whole (one log line); the journal is still loaded and saved back unchanged.</summary>
        static GameFeature feature;
        FieldInfo accessorMap, requestDimension;
        ICoreServerAPI api;
        CylinderWorldCore core;
        ServerWorldMap map;
        int applying;
        int loading;
        /// <summary>Inside Suppress: writes are neither copied nor journaled, and drop an older cell for their position.</summary>
        int quiet;
        /// <summary>Chunk coordinates of the overworld players, republished by the main thread for RandomTick, which runs
        /// on the block-ticking thread and must not walk the player list there.</summary>
        volatile (int x, int y, int z)[] playerChunks = Array.Empty<(int, int, int)>();
        volatile int tickRange;
        /// <summary>Changes reported off the main thread (vanilla's scheduled block updates call MarkBlockModified on the
        /// chunk thread; a mod may call SetBlock anywhere), and columns published there: the journal, the counters and the
        /// counterpart writes are main-thread only, so these wait for the next OnTick, or a column unload or save before it.</summary>
        readonly ConcurrentQueue<BlockPos> offThread = new();
        readonly ConcurrentQueue<(int cx, int cz)> offThreadColumns = new();
        /// <summary>Containers whose slots changed on the main thread, captured once more at the next tick (AfterSlot).</summary>
        readonly HashSet<BlockPos> recapture = new();
        static bool OnMainThread => Environment.CurrentManagedThreadId == RuntimeEnv.ServerMainThreadId;

        /// <summary>One journaled cell, keyed by its canonical (ring-side) position. The three maps are null when empty:
        /// protobuf writes nothing for an empty or a null map, so the saved form is the same as before.</summary>
        [ProtoContract]
        public sealed class Cell
        {
            [ProtoMember(1)] public int X;
            [ProtoMember(2)] public int Y;
            [ProtoMember(3)] public int Z;
            [ProtoMember(4)] public string Solid;
            [ProtoMember(5)] public string Fluid;
            [ProtoMember(6)] public string EntityClass;
            [ProtoMember(7)] public byte[] Entity;
            [ProtoMember(8)] public Dictionary<int, string> BlockCodes;
            [ProtoMember(9)] public Dictionary<int, string> ItemCodes;
            [ProtoMember(10)] public Dictionary<int, string> Decors;
            /// <summary>Not saved: the physical copies that don't hold this state yet (KeyCopy, TwinCopy). Every cell of
            /// a loaded journal starts with both: whether a chunk was saved with it before the server stopped is unknown.</summary>
            internal byte Missing = KeyCopy | TwinCopy;
            /// <summary>Not saved: the save count when both copies held it (-1: not yet).</summary>
            internal int HeldSince = -1;
        }
        const byte KeyCopy = 1, TwinCopy = 2;
        /// <summary>World saves since the server started (Save).</summary>
        int saves;

        readonly Dictionary<BlockPos, Cell> cells = new();
        readonly Dictionary<(int x, int z), HashSet<BlockPos>> columns = new();
        readonly Dictionary<string, Block> blocksByCode = new();
        readonly Dictionary<string, string> codes = new();
        readonly Dictionary<int, string> codeById = new();
        readonly Dictionary<string, bool> entityKnown = new();
        readonly HashSet<string> warned = new();

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;
            instance = this;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            // Before anything can fail: a journal loaded while mirroring is off is saved back as it was, not dropped.
            api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, Load);
            api.Event.GameWorldSave += Save;
            api.Event.RegisterGameTickListener(OnTick, 100);
            api.Event.ChunkColumnUnloaded += OnColumnUnloaded;

            // All or nothing: half the mutation paths would mirror some edits live and journal none of the others.
            var f = new GameFeature("seam edit mirroring", new Harmony(HarmonyId), Mod.Logger);
            // Every method by its 1.22.7 parameter types, so an overload a game update adds is not taken for it (or makes the
            // lookup throw); anything that throws while binding switches this feature off with one line, like a missing member.
            try
            {
                accessorMap = f.Field(typeof(BlockAccessorBase), "worldmap");
                var request = f.Type("Vintagestory.Server.ChunkColumnLoadRequest");
                requestDimension = f.Field(request, "dimension", typeof(int));
                map = accessorMap?.GetValue(api.World.BlockAccessor) as ServerWorldMap;
                f.Require(map != null, "server world map behind the block accessor");
                HarmonyMethod Fix(string name) => new HarmonyMethod(typeof(SeamBlocks), name);
                f.Patch(f.Method(typeof(BlockAccessorBase), "SetBlockInternal",
                    new[] { typeof(int), typeof(BlockPos), typeof(IWorldChunk), typeof(bool), typeof(bool), typeof(int), typeof(ItemStack) }),
                    postfix: Fix(nameof(AfterSet)));
                f.Patch(f.Method(typeof(BlockAccessorRelaxed), "ExchangeBlock", new[] { typeof(int), typeof(BlockPos) }), postfix: Fix(nameof(AfterExchange)));
                f.Patch(f.Method(typeof(ServerWorldMap), "MarkBlockModified", new[] { typeof(BlockPos), typeof(bool) }), postfix: Fix(nameof(AfterDirty)));
                foreach (string method in new[] { "MarkBlockEntityDirty", "RemoveBlockEntity", "MarkDecorsDirty" })
                    f.Patch(f.Method(typeof(ServerWorldMap), method, new[] { typeof(BlockPos) }), postfix: Fix(nameof(AfterDirty)));
                f.Patch(f.Method(typeof(ServerWorldMap), "SendBlockUpdateBulkMinimal", new[] { typeof(Dictionary<BlockPos, BlockUpdate>) }),
                    postfix: Fix(nameof(AfterBulk)));
                f.Patch(f.Method(typeof(InventoryBase), nameof(InventoryBase.DidModifyItemSlot), new[] { typeof(ItemSlot), typeof(ItemStack) }),
                    postfix: Fix(nameof(AfterSlot)));
                // A replica must not double the rate of a furnace, crop, etc. Keep both instances for normal local
                // reach/claims/inventory handling, but run their registered simulation on only one loaded copy.
                f.Patch(f.Method(typeof(BlockEntity), nameof(BlockEntity.RegisterGameTickListener),
                    new[] { typeof(Action<float>), typeof(int), typeof(int) }), prefix: Fix(nameof(TickListener)));
                f.Patch(f.Method(typeof(BlockEntity), nameof(BlockEntity.RegisterGameTickListener),
                    new[] { typeof(Action<IWorldAccessor, BlockPos, float>), typeof(int), typeof(int) }), prefix: Fix(nameof(PositionTickListener)));
                f.Patch(f.Method(typeof(ServerSystemBlockSimulation), "tryTickBlock", new[] { typeof(Block), typeof(BlockPos) }), prefix: Fix(nameof(RandomTick)));
                // ChunkColumnLoaded fires BEFORE the chunks enter ServerMain.loadedChunks. Replay after the native
                // main-thread publication/BE initialization, before its load-completion callback or pre-send can run.
                f.Patch(f.Method(f.Type("Vintagestory.Server.ServerSystemSupplyChunks"), "mainThreadLoadChunkColumn", request == null ? null : new[] { request }),
                    prefix: Fix(nameof(BeforeColumn)), finalizer: Fix(nameof(AfterColumn)));
            }
            catch (Exception e) { f.Require(false, "its game members as they were (" + e.GetType().Name + ": " + e.Message + ")"); }
            feature = f;
            f.Apply();
        }

        public override void Dispose()
        {
            if (instance == this)
            {
                feature?.Unpatch();
                feature = null;
                instance = null;
            }
            if (api != null) api.Event.ChunkColumnUnloaded -= OnColumnUnloaded;
            base.Dispose();
        }

        /// <summary>Main thread: capture what was changed off it, and publish what RandomTick needs to know about the players.</summary>
        void OnTick(float dt)
        {
            if (feature?.On == true) SafeDrain();
            var list = new List<(int, int, int)>();
            foreach (var player in api.World.AllOnlinePlayers)
            {
                var pos = player.Entity?.Pos;
                if (pos?.Dimension == 0) list.Add(((int)Math.Floor(pos.X / 32), (int)Math.Floor(pos.Y / 32), (int)Math.Floor(pos.Z / 32)));
            }
            playerChunks = list.ToArray();
            tickRange = api.Server.Config.BlockTickChunkRange;
        }

        /// <summary>
        /// Vanilla raises this on the main thread before it takes the column's chunks away (ServerSystemUnloadChunks,
        /// WorldAPI.UnloadChunkColumn). A change queued off the main thread is captured now, while its chunk can still be
        /// read: at the next tick a source that unloaded meanwhile was gone, its copy saved with the change and the other
        /// copy never given it (a container emptied there still held its items on the other side).
        /// </summary>
        void OnColumnUnloaded(Vec3i chunk)
        {
            if (feature?.On == true && OnMainThread && !(offThread.IsEmpty && offThreadColumns.IsEmpty && recapture.Count == 0)) SafeDrain();
        }

        void SafeDrain()
        {
            try
            {
                while (offThreadColumns.TryDequeue(out var c)) FlushColumn(c.cx, c.cz);
                while (offThread.TryDequeue(out var pos)) Capture(pos);
                if (recapture.Count > 0)
                {
                    var again = recapture.ToArray();
                    recapture.Clear();
                    foreach (var pos in again) Capture(pos);
                }
            }
            catch (Exception e) { feature.Fail(e); }
        }

        /// <summary>A patch saw a change in the band: capture it now on the main thread, else at the next tick.</summary>
        void Notify(BlockPos pos)
        {
            if (OnMainThread) Capture(pos);
            else offThread.Enqueue(pos.Copy());
        }

        /// <summary>
        /// For a system that writes every loaded copy itself by a ring-periodic rule (SeaIceSystem): its writes inside
        /// this scope are neither copied to the other side nor journaled. Copied, they froze ice into a boat on the other
        /// copy past that system's own entity check, and every seasonal freeze and thaw became a permanent journal cell
        /// replayed at every load. An older journal cell for a position it writes is dropped (not written anywhere):
        /// replayed after a restart, it put back snow or ice this system had since changed, over open water that its
        /// update then leaves alone. Main thread only (elsewhere writes are queued and captured later anyway).
        /// </summary>
        internal static Quiet Suppress()
        {
            var s = OnMainThread ? instance : null;
            if (s != null) s.quiet++;
            return new Quiet(s);
        }

        internal readonly struct Quiet : IDisposable
        {
            readonly SeamBlocks s;
            internal Quiet(SeamBlocks s) => this.s = s;
            public void Dispose() { if (s != null) s.quiet--; }
        }

        /// <summary>Cheap first test for the hot patches: the cell is in the overworld seam band.</summary>
        bool InBand(BlockPos pos)
        {
            var p = core.Params;
            return p != null && pos != null && pos.dimension == 0 && pos.X >= 0 && pos.X < p.WorldWidth && p.InSeamBand(pos.X + 0.5);
        }

        // Integer block addressing works even in an old/custom save whose circumference is not chunk aligned.
        internal static bool Twin(SeamParams p, BlockPos pos, out BlockPos twin)
        {
            twin = null;
            if (p == null || pos == null || pos.dimension != 0 || pos.X < 0 || pos.X >= p.WorldWidth
                || !p.InSeamBand(pos.X + 0.5)) return false;
            int x = (int)p.Mirror(pos.X);
            if (x < 0 || x >= p.WorldWidth || x == pos.X || !p.InSeamBand(x + 0.5)
                || !IdenticalColumn(p, pos.X, pos.Z) || !IdenticalColumn(p, x, pos.Z)) return false;
            twin = pos.Copy(); twin.X = x;
            return true;
        }

        /// <summary>
        /// Whether worldgen built this column as part of the band's identical part (as FlattenSeam decides), which repeats
        /// around the ring and so matches the other copy. Beyond the band's wavy edge each side has its own ramp and natural
        /// ground: the visitable ramp strip on one side lies opposite the far edge's identical sea, and pairing them copied
        /// that sea's snow and ice over ramp terrain (and journaled the ramp's grass and leaves for good). Earth worlds
        /// likewise: beyond the edge Earth's seabed (EarthSeabed, no ramp) and the land Earth carries across the band
        /// (EarthSeamLand) are left as Earth generated them, with their own plants, snow and strata on each side.
        /// </summary>
        static bool IdenticalColumn(SeamParams p, int x, int z)
        {
            double wx = x + 0.5, d = p.DistToEdge(wx);
            // A pre-v6 band has no blend; inside the edge's lowest point no noise is needed
            if (p.Version < 6 || d - p.JumpInset <= p.IdenticalReach) return true;
            double wz = z + 0.5;
            // The jump curtain, and the polar zone (wall, sheet, Earth's Antarctica), which its own worldgen builds from the
            // ring angle and the seam worldgen leaves alone: the same on both sides at any distance. Not Earth's land north
            // of Antarctica, which PolarOwnsSeabed counts too: the seam worldgen rebuilds that only inside the edge (CW-D31).
            if (p.DistToJumpLine(wx) <= SeamParams.CurtainHalfWidth
                || p.PolarOwnsSeabed(wx, wz) && (p.DistToZEdge(wz) < p.WallFaceMax || !EarthSeamLandAt(p, wx, wz))) return true;
            return p.Version >= 10 ? d < p.SeamEdge(wx, wz) : p.SeamBlendWeight(wx) == 0;
        }

        /// <summary>FlattenSeam's first test for a column: Earth's land north of Antarctica that EarthSeamLand keeps in the
        /// band (Earth generates columns at integer coordinates).</summary>
        static bool EarthSeamLandAt(SeamParams p, double wx, double wz) =>
            p.EarthSeamLand && p.EarthPolar != null && p.EarthSeamLandColumn(p.EarthPolar.Sample(Math.Floor(wx), Math.Floor(wz)));

        BlockPos Key(BlockPos pos, BlockPos twin) => core.Params.PastJumpLine(pos.X) ? twin.Copy() : pos.Copy();

        void Index(BlockPos key)
        {
            Add(key);
            if (Twin(core.Params, key, out var twin)) Add(twin);
            void Add(BlockPos pos)
            {
                var col = (pos.X / 32, pos.Z / 32);
                if (!columns.TryGetValue(col, out var set)) columns[col] = set = new();
                set.Add(key);
            }
        }

        void Forget(BlockPos key)
        {
            cells.Remove(key);
            Remove(key);
            if (Twin(core.Params, key, out var twin)) Remove(twin);
            void Remove(BlockPos pos)
            {
                var col = (pos.X / 32, pos.Z / 32);
                if (columns.TryGetValue(col, out var set) && set.Remove(key) && set.Count == 0) columns.Remove(col);
            }
        }

        /// <summary>Codes repeat across thousands of cells: keep one string per code.</summary>
        string Intern(string code)
        {
            if (code == null) return null;
            if (codes.TryGetValue(code, out var same)) return same;
            return codes[code] = code;
        }

        string CodeOf(Block block)
        {
            if (!codeById.TryGetValue(block.Id, out var code)) codeById[block.Id] = code = Intern(block.Code.ToString());
            return code;
        }

        Dictionary<int, string> Codes(Dictionary<int, AssetLocation> map)
        {
            if (map.Count == 0) return null;
            var result = new Dictionary<int, string>(map.Count);
            foreach (var (k, v) in map) result[k] = Intern(v.ToString());
            return result;
        }

        static Dictionary<int, AssetLocation> Locations(Dictionary<int, string> map)
        {
            var result = new Dictionary<int, AssetLocation>();
            if (map != null) foreach (var (k, v) in map) result[k] = new AssetLocation(v);
            return result;
        }

        void Load()
        {
            if (core.Params == null) return;
            byte[] data = api.WorldManager.SaveGame.GetData(SaveKey);
            if (data == null) return;
            List<Cell> list;
            try { list = SerializerUtil.Deserialize<List<Cell>>(data); }
            catch (Exception e)
            {
                // Keep the unreadable journal where nothing overwrites it, and start an empty one: the counterparts of
                // edits it held may differ until edited again, but the world loads.
                string backup = SaveKey + "-unreadable";
                for (int n = 2; api.WorldManager.SaveGame.GetData(backup) is byte[] old && !old.AsSpan().SequenceEqual(data); n++)
                    backup = SaveKey + "-unreadable-" + n;
                api.WorldManager.SaveGame.StoreData(backup, data);
                Mod.Logger.Error("Seam edits: the saved journal can't be read ({0}); kept as '{1}', starting an empty one.", e.Message, backup);
                return;
            }
            int unpaired = 0;
            foreach (var cell in list)
            {
                if (cell == null) continue;
                // Journaled before pairing stopped at the band's identical part: each copy keeps what its chunks hold
                if (!Twin(core.Params, new BlockPos(cell.X, cell.Y, cell.Z), out _)) { unpaired++; continue; }
                cell.Solid = Intern(cell.Solid);
                cell.Fluid = Intern(cell.Fluid);
                cell.EntityClass = Intern(cell.EntityClass);
                foreach (var map in new[] { cell.BlockCodes, cell.ItemCodes, cell.Decors })
                    if (map != null) foreach (int k in map.Keys.ToArray()) map[k] = Intern(map[k]);
                var key = new BlockPos(cell.X, cell.Y, cell.Z);
                cells[key] = cell;
                Index(key);
            }
            Mod.Logger.Notification("Seam edits: restored {0} changed cells", cells.Count);
            if (unpaired > 0) Mod.Logger.Notification("Seam edits: dropped {0} cells outside the band's identical part (they have no twin)", unpaired);
        }

        void Save()
        {
            if (core.Params == null) return;
            // The shutdown save can run on the thread that stopped the server (singleplayer exit, the console, Ctrl+C).
            // ServerMain.Stop has suspended the server's threads by then, so the queues are drained alone there too:
            // left queued, those changes were saved on their own copy and never captured for the other one.
            if (feature?.On == true && (OnMainThread || api.Server.IsShuttingDown)) SafeDrain();
            // A cell both copies have held since before the previous save is in the chunks that save wrote (dirty
            // chunks are written with the journal; unloaded ones were written when they unloaded): drop it. The journal
            // keeps only what a copy that isn't loaded still lacks.
            List<BlockPos> held = null;
            foreach (var (key, cell) in cells) if (cell.Missing == 0 && cell.HeldSince >= 0 && cell.HeldSince < saves) (held ??= new()).Add(key);
            if (held != null) foreach (var key in held) Forget(key);
            saves++;
            api.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(cells.Values.ToList()));
        }

        // ---------------------------------------------------------------- the patches
        // Each body returns at once while the feature is off and never lets an exception into the game's code: the
        // first one switches mirroring off with one log line (one cell's block entity failing costs only that cell:
        // Capture, TryApply). They run on every block change server-wide, so the band test comes first.

        static void AfterSet(BlockAccessorBase __instance, BlockPos pos)
        {
            var s = instance;
            if (s == null || feature?.On != true) return;
            try
            {
                // Never copy a worldgen accessor's intermediate column into already generated terrain.
                if (s.InBand(pos) && __instance is not BlockAccessorWorldGen && s.accessorMap.GetValue(__instance) == s.map)
                    s.Notify(pos);
            }
            catch (Exception e) { feature.Fail(e); }
        }
        static void AfterExchange(BlockAccessorRelaxed __instance, BlockPos pos) => AfterSet(__instance, pos);
        static void AfterDirty(ServerWorldMap __instance, BlockPos pos)
        {
            var s = instance;
            if (s == null || feature?.On != true) return;
            try
            {
                if (s.map == __instance && s.InBand(pos)) s.Notify(pos);
            }
            catch (Exception e) { feature.Fail(e); }
        }
        static void AfterBulk(ServerWorldMap __instance, Dictionary<BlockPos, BlockUpdate> updates)
        {
            var s = instance;
            if (s == null || feature?.On != true) return;
            try
            {
                if (s.map == __instance) foreach (var pos in updates.Keys) if (s.InBand(pos)) s.Notify(pos);
            }
            catch (Exception e) { feature.Fail(e); }
        }

        /// <summary>
        /// A slot of a container changed (a GUI transfer, a hopper, code). Vanilla's InWorldContainer only marks the chunk
        /// modified, never the block entity dirty, so none of the paths above see it: the other copy would keep the old
        /// items (a duplicate to take) and a reload would replay the journal's older snapshot (items lost). Block-entity
        /// inventories carry the block's position; players' and entities' inventories have none.
        /// This postfix runs at the end of the base method, inside any override: an inventory that changes slots after
        /// calling base (the firepit's InventorySmelting drops and clears its cooking slots, without a notification, when
        /// the pot is taken out) is captured before that. So the position is captured now, leaving the other copy no
        /// window, and once more at the next tick, when the override has finished (an unchanged cell writes nothing).
        /// </summary>
        static void AfterSlot(InventoryBase __instance)
        {
            var s = instance;
            if (s == null || feature?.On != true) return;
            try
            {
                var pos = __instance.Pos;
                if (pos != null && __instance.Api?.Side == EnumAppSide.Server && s.InBand(pos))
                {
                    s.Notify(pos);
                    // Off the main thread the queued capture is already that late; never for our own writes
                    if (OnMainThread && s.applying == 0 && s.loading == 0 && !s.recapture.Contains(pos)) s.recapture.Add(pos.Copy());
                }
            }
            catch (Exception e) { feature.Fail(e); }
        }

        void Capture(BlockPos pos)
        {
            if (applying > 0 || loading > 0 || !Twin(core.Params, pos, out var twin)) return;
            var key = Key(pos, twin);
            // A write inside Suppress: each copy keeps its own, and no older cell is left to overwrite it later
            if (quiet > 0) { if (cells.ContainsKey(key)) Forget(key); return; }
            var ba = api.World.BlockAccessor;
            if (ba.GetChunkAtBlockPos(pos) == null) return;
            var cell = new Cell { X = key.X, Y = key.Y, Z = key.Z,
                Solid = CodeOf(ba.GetBlock(pos, BlockLayersAccess.Solid)),
                Fluid = CodeOf(ba.GetBlock(pos, BlockLayersAccess.Fluid)) };
            var be = ba.GetBlockEntity(pos);
            if (be != null)
            {
                cell.EntityClass = Intern(api.ClassRegistry.GetBlockEntityClass(be.GetType()));
                // Not registered under a name: it can't be created on the other side, so each copy keeps its own, and an
                // older cell for the position leaves the journal (replayed, it put a different block under this entity)
                if (cell.EntityClass == null) { Uncopied(be, pos, "it has no registered class name", null); Forget(key); return; }
                try
                {
                    var tree = new TreeAttribute();
                    be.ToTreeAttributes(tree);
                    tree.SetInt("posx", key.X);
                    cell.Entity = tree.ToBytes();
                    var blocks = new Dictionary<int, AssetLocation>();
                    var items = new Dictionary<int, AssetLocation>();
                    be.OnStoreCollectibleMappings(blocks, items);
                    cell.BlockCodes = Codes(blocks);
                    cell.ItemCodes = Codes(items);
                }
                catch (Exception e)
                {
                    // One block entity that can't be saved (a mod's, left half initialised) costs its own cell, as above,
                    // not mirroring as a whole: switched off, both copies drifted apart and every replica simulated again
                    Uncopied(be, pos, "reading it failed", e);
                    Forget(key);
                    return;
                }
            }
            var chunk = (WorldChunk)ba.GetChunkAtBlockPos(pos);
            var decors = chunk.GetSubDecors(ba, pos);
            if (decors?.Count > 0)
            {
                cell.Decors = new Dictionary<int, string>(decors.Count);
                foreach (var (k, decor) in decors) cell.Decors[k] = CodeOf(decor);
            }
            if (!cells.ContainsKey(key)) Index(key);
            cells[key] = cell;
            // The copy it was read from holds it; the other one now too, if loaded
            cell.Missing = key.X == pos.X ? TwinCopy : KeyCopy;
            if (TryApply(cell, twin)) Held(cell, TwinCopy | KeyCopy);
        }

        /// <summary>A block entity Capture can't copy: logged once per class; each copy keeps its own.</summary>
        void Uncopied(BlockEntity be, BlockPos pos, string why, Exception e)
        {
            if (warned.Add("capture " + be.GetType().FullName))
                Mod.Logger.Warning("Seam edits: block entity {0} (at {1}) is not copied to the other side, {2}; each copy keeps its own (not logged again for this class){3}",
                    be.GetType().FullName, pos, why, e == null ? "" : ": " + e);
        }

        void Held(Cell cell, byte copy)
        {
            cell.Missing &= (byte)~copy;
            if (cell.Missing == 0 && cell.HeldSince < 0) cell.HeldSince = saves;
        }

        static void BeforeColumn(out SeamBlocks __state)
        {
            // Paired with the finalizer through __state, so the count stays right even if the feature fails in between.
            // Vanilla publishes columns on the main thread only; anywhere else the replay waits for the next tick.
            __state = feature?.On == true && OnMainThread ? instance : null;
            if (__state != null) __state.loading++;
        }
        static Exception AfterColumn(Exception __exception, object chunkRequest, SeamBlocks __state)
        {
            var s = __state ?? (OnMainThread ? null : instance);
            if (s == null) return __exception;
            try
            {
                if (__exception == null && feature?.On == true && (int)s.requestDimension.GetValue(chunkRequest) == 0
                    && chunkRequest is IChunkColumnGenerateRequest request)
                {
                    if (__state != null) s.FlushColumn(request.ChunkX, request.ChunkZ);
                    else s.offThreadColumns.Enqueue((request.ChunkX, request.ChunkZ));
                }
            }
            catch (Exception e) { feature?.Fail(e); }
            finally { if (__state != null) s.loading--; }
            return __exception;
        }

        /// <summary>Replay edits when a saved or newly generated counterpart column becomes available: only to the copies
        /// that don't hold them yet (the copy an edit came from may have newer state since, from paths not captured).</summary>
        public void FlushColumn(int cx, int cz)
        {
            if (!columns.TryGetValue((cx, cz), out var keys)) return;
            foreach (var key in keys.ToArray())
            {
                var cell = cells[key];
                if ((cell.Missing & KeyCopy) != 0 && key.X / 32 == cx && TryApply(cell, key)) Held(cell, KeyCopy);
                if ((cell.Missing & TwinCopy) != 0 && Twin(core.Params, key, out var twin) && twin.X / 32 == cx && TryApply(cell, twin))
                    Held(cell, TwinCopy);
            }
        }

        /// <summary>One cell's replay failing (a mod's block entity that throws on load, say) costs that cell, not the
        /// column load or the server: it stays in the journal and is tried again at the next load.</summary>
        bool TryApply(Cell cell, BlockPos pos)
        {
            try { return Apply(cell, pos); }
            catch (Exception e)
            {
                if (warned.Add("replay " + e.GetType().Name + e.Message))
                    Mod.Logger.Warning("Seam edits: could not copy the cell at {0} to the other side (later failures like it are not logged): {1}", pos, e);
                return false;
            }
        }

        /// <summary>The block a journal code names, or null (warned once) when this game or its mods no longer have it:
        /// the journal stores codes, which the game's block remapping never sees.</summary>
        Block Resolve(string code)
        {
            if (code == null) return null;
            if (!blocksByCode.TryGetValue(code, out var block))
            {
                blocksByCode[code] = block = api.World.GetBlock(new AssetLocation(code));
                if (block == null) Warn("block " + code);
            }
            return block;
        }

        bool KnownEntity(string className)
        {
            if (className == null) return false;
            if (!entityKnown.TryGetValue(className, out bool known))
            {
                known = api.ClassRegistry.GetBlockEntity(className) != null;
                // A renamed class can still be known under its legacy name, which only creation resolves
                if (!known) try { known = api.ClassRegistry.CreateBlockEntity(className) != null; } catch (Exception) { }
                entityKnown[className] = known;
                if (!known) Warn("block entity class " + className);
            }
            return known;
        }

        void Warn(string missing)
        {
            if (warned.Add(missing))
                Mod.Logger.Warning("Seam edits: this game has no {0}; journal cells using it are kept but not copied to the other side.", missing);
        }

        /// <returns>True when this copy holds the cell now: written, or already the same (then nothing is written, sent or
        /// marked modified). False when its chunk isn't loaded or the cell names something this game doesn't have.</returns>
        bool Apply(Cell cell, BlockPos pos)
        {
            var ba = (BlockAccessorBase)api.World.BlockAccessor;
            var chunk = (WorldChunk)ba.GetChunkAtBlockPos(pos);
            if (chunk == null) return false; // The durable journal supplies this cell when that column is loaded later.
            var solid = Resolve(cell.Solid);
            var fluid = Resolve(cell.Fluid);
            if (solid == null || fluid == null || cell.Entity != null && !KnownEntity(cell.EntityClass)) return false;
            List<(int key, Block block)> decors = null;
            if (cell.Decors != null)
            {
                foreach (var decor in cell.Decors)
                {
                    var block = Resolve(decor.Value);
                    if (block == null) return false;
                    (decors ??= new()).Add((decor.Key, block));
                }
            }
            chunk.Unpack();
            int index = ((pos.Y & 31) * 32 + (pos.Z & 31)) * 32 + (pos.X & 31);
            int oldSolid = chunk.Data.GetBlockId(index, BlockLayersAccess.Solid), oldFluid = chunk.Data.GetFluid(index);
            bool blocks = oldSolid != solid.Id || oldFluid != fluid.Id;
            if (!blocks && SameEntity(cell, ba.GetBlockEntity(pos)) && SameDecors(chunk, ba, pos, decors)) return true;
            applying++;
            try
            {
                // Copy state, not gameplay callbacks: another placement/break callback could duplicate drops,
                // consume another item, or create another half of a multiblock. Lighting/networking still run.
                if (blocks)
                {
                    chunk.Data[index] = solid.Id;
                    chunk.Data.SetFluid(index, fluid.Id);
                    chunk.Empty = false;
                    ba.UpdateRainHeightMap(api.World.Blocks[oldSolid], solid, pos, chunk.MapChunk);
                    if (solid.RainPermeable) ba.UpdateRainHeightMap(api.World.Blocks[oldFluid], fluid, pos, chunk.MapChunk);
                    if (oldSolid != solid.Id) map.UpdateLighting(oldSolid, solid.Id, pos);
                    if (oldFluid != fluid.Id) map.UpdateLighting(oldFluid, fluid.Id, pos);
                }

                var be = ba.GetBlockEntity(pos);
                if (be != null && api.ClassRegistry.GetBlockEntityClass(be.GetType()) != cell.EntityClass)
                {
                    // Its items are a copy of the other side's, which dropped them itself: removing a hopper or chute
                    // (BlockEntityItemFlow.OnBlockRemoved → DropContents) spawned them a second time
                    EmptyInventories(be);
                    ba.RemoveBlockEntity(pos); be = null;
                }
                if (cell.Entity != null)
                {
                    var tree = new TreeAttribute(); tree.FromBytes(cell.Entity);
                    tree.SetInt("posx", pos.X);
                    bool created = be == null;
                    if (created)
                    {
                        be = api.ClassRegistry.CreateBlockEntity(cell.EntityClass);
                        be.Pos = pos.Copy();
                        be.CreateBehaviors(solid, api.World);
                    }
                    be.Block = solid;
                    be.FromTreeAttributes(tree, api.World);
                    be.OnLoadCollectibleMappings(api.World, Locations(cell.BlockCodes), Locations(cell.ItemCodes), 0, false);
                    if (created)
                    {
                        be.Initialize(api);
                        ba.SpawnBlockEntity(be);
                    }
                    ba.MarkBlockEntityDirty(pos);
                }
                else ba.MarkBlockEntityDirty(pos); // Includes explicit removal with the solid block unchanged.
                // Decoration keys are local to the cell, so copying them does not assume chunk alignment.
                if (decors != null) chunk.Decors ??= new Dictionary<int, Block>();
                if (chunk.Decors != null)
                {
                    lock (chunk.Decors)
                    {
                        foreach (int k in chunk.Decors.Keys.Where(k => DecorBits.Index3dFromIndex(k) == index).ToArray()) chunk.Decors.Remove(k);
                        if (decors != null) foreach (var (k, block) in decors) chunk.SetDecor(block, index, k);
                    }
                    map.MarkDecorsDirty(pos);
                }
                chunk.MarkModified();
                // Vanilla sends these to every client that has this chunk, including Cworld's kept side. Not for a block
                // entity or decoration change alone, which have their own packets (MarkBlockEntityDirty, MarkDecorsDirty):
                // every container click made each client holding the other copy re-mesh its chunk twice.
                if (blocks)
                {
                    map.SendSetBlock(solid.Id, pos.X, pos.InternalY, pos.Z);
                    map.SendSetBlock(-fluid.Id - 1, pos.X, pos.InternalY, pos.Z);
                }
            }
            finally { applying--; }
            return true;
        }

        /// <summary>Empties a replica's inventories (its own and its behaviours') without any slot notification, before Apply
        /// removes it: a block entity that drops its contents when removed drops nothing then.</summary>
        static void EmptyInventories(BlockEntity be)
        {
            if (be is IBlockEntityContainer container) Clear(container.Inventory);
            if (be.Behaviors != null) foreach (var behavior in be.Behaviors) if (behavior is IBlockEntityContainer part) Clear(part.Inventory);
            static void Clear(IInventory inventory)
            {
                if (inventory != null) foreach (var slot in inventory) if (slot != null) slot.Itemstack = null;
            }
        }

        /// <summary>The block entity at the copy already has the cell's state (as saved with the canonical x).</summary>
        bool SameEntity(Cell cell, BlockEntity be)
        {
            if (cell.Entity == null) return be == null;
            if (be == null || api.ClassRegistry.GetBlockEntityClass(be.GetType()) != cell.EntityClass) return false;
            var tree = new TreeAttribute();
            be.ToTreeAttributes(tree);
            tree.SetInt("posx", cell.X);
            return tree.ToBytes().AsSpan().SequenceEqual(cell.Entity);
        }

        static bool SameDecors(WorldChunk chunk, IBlockAccessor ba, BlockPos pos, List<(int key, Block block)> decors)
        {
            var now = chunk.GetSubDecors(ba, pos);
            if ((now?.Count ?? 0) != (decors?.Count ?? 0)) return false;
            if (decors != null) foreach (var (k, block) in decors) if (!now.TryGetValue(k, out var had) || had?.Id != block.Id) return false;
            return true;
        }

        /// <summary>Whether this BE's registered simulation runs: false for the replica past the jump line while its twin
        /// is loaded. Its wrapped listeners outlive a feature that failed, so this never throws and runs them then.</summary>
        static bool Simulate(BlockPos pos)
        {
            var s = instance;
            if (s == null || feature?.On != true) return true;
            try
            {
                if (!s.InBand(pos) || !s.core.Params.PastJumpLine(pos.X) || !Twin(s.core.Params, pos, out var twin)) return true;
                var ba = s.api.World.BlockAccessor;
                // An older save can have an unmatched BE from before replication existed. Keep that instance alive;
                // do not discard its state or freeze it merely because the other physical chunk is loaded.
                return ba.GetBlockEntity(twin)?.GetType() != ba.GetBlockEntity(pos)?.GetType();
            }
            catch (Exception e) { feature.Fail(e); return true; }
        }
        static void TickListener(BlockEntity __instance, ref Action<float> onGameTick)
        {
            if (feature?.On != true || __instance.Api?.Side != EnumAppSide.Server) return;
            var original = onGameTick;
            onGameTick = dt => { if (Simulate(__instance.Pos)) original(dt); };
        }
        static void PositionTickListener(BlockEntity __instance, ref Action<IWorldAccessor, BlockPos, float> onGameTick)
        {
            if (feature?.On != true || __instance.Api?.Side != EnumAppSide.Server) return;
            var original = onGameTick;
            onGameTick = (world, pos, dt) => { if (Simulate(pos)) original(world, pos, dt); };
        }
        /// <summary>Block-ticking thread: reads only the params and the players published by OnTick.</summary>
        static bool RandomTick(BlockPos atPos, ref bool __result)
        {
            var s = instance;
            if (s == null || feature?.On != true) return true;
            try
            {
                if (!s.InBand(atPos) || !s.core.Params.PastJumpLine(atPos.X) || !Twin(s.core.Params, atPos, out var twin)) return true;
                // Random ticks are restricted to a cube around actual players, unlike registered BE listeners.
                // A merely preloaded canonical column may not tick at all. Suppress the replica only when the
                // canonical cell is inside the native tick cube of another player, avoiding a frozen far shore.
                int r = s.tickRange, tx = twin.X / 32, ty = twin.Y / 32, tz = twin.Z / 32;
                foreach (var (x, y, z) in s.playerChunks)
                {
                    if (Math.Abs(x - tx) <= r && Math.Abs(y - ty) <= r && Math.Abs(z - tz) <= r) { __result = false; return false; }
                }
                return true;
            }
            catch (Exception e) { feature.Fail(e); return true; }
        }
    }
}
