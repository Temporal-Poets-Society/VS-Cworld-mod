using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace CylinderWorld
{
    /// <summary>
    /// The client side of a seam jump. A jump moves the player by exactly ±circumference between two places that look
    /// the same, but several things vanilla draws are keyed to the absolute position. At that moment this:
    ///  - shifts the water shader's reference points by the jump (the camera would reset them: it does after any move
    ///    over 20000 blocks), so foam, glint and the water texture continue; and the camera's previous position, so no
    ///    frame is interpolated halfway round the world;
    ///  - copies the client's weather of the regions just left onto the ones arrived in: the server keeps both sides
    ///    identical, but the client only hears about the far side once it is there;
    ///  - shows every loaded chunk until vanilla's occlusion culling has caught up with the new place (ShowAllChunks);
    ///  - puts every other entity on the new lap (SeamEntities), starts the mist pulse (SeamMistSystem) and logs what the
    ///    jump cost (SeamJumpTrace);
    ///  - and keeps the clouds (SeamClouds) and weather transitions (KeepTransition) from restarting.
    /// A jump is reported by SeamSmoothJump, which makes it (Jumped); one it leaves to vanilla (a teleport of about one
    /// ring the server didn't announce as a wrap), or any while its patches are off, is inferred from the player's x
    /// changing by about one ring between frames. SeamSmoothJump also keeps the player where the client has them at the
    /// jump and the plants as they were.
    /// </summary>
    public class SeamJumpClient : ModSystem, IRenderer
    {
        const string HarmonyId = "cylinderworld.seamclient";

        /// <summary>The client API while a world is joined, for the static client patches (SeamSmoothJump, SeamClouds,
        /// SeamEntities, KeepTransition).</summary>
        internal static ICoreClientAPI Capi;
        ICoreClientAPI capi;
        CylinderWorldCore core;
        Harmony harmony;
        GameFeature camera, chunkView, weatherCopy;
        static GameFeature transitions;
        double lastX = double.NaN;
        long showAllUntil = long.MinValue;

        /// <summary>How long after a jump every loaded chunk stays visible (client ms): the culling thread runs every
        /// 20 ms and may first finish a pass for the place left behind.</summary>
        const int ShowAllMs = 300;
        SeamJumpTrace trace;

        /// <summary>Before the weather system (-0.1) and the camera (0), which read the player's position.</summary>
        public double RenderOrder => -0.2;
        public int RenderRange => 0;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = Capi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            api.Event.RegisterRenderer(this, EnumRenderStage.Before, "cylinderworld-seamjump");
            harmony = new Harmony(HarmonyId);

            camera = Bind("the camera's previous position at a seam jump (no frame drawn halfway round the world)", null, BindCamera);
            chunkView = Bind("showing every loaded chunk right after a seam jump (no black frame)", null, BindChunks);
            weatherCopy = Bind("copying the client's weather across the seam at a jump", null, BindWeather);
            transitions = Bind("keeping weather transitions near the seam", harmony, BindTransitions);

            trace = new SeamJumpTrace(api, Mod.Logger, () => chunkView.On ? ChunkCount(capi.World) : -1);
            SeamClouds.Patch(harmony, Mod.Logger);
            SeamSmoothJump.Patch(harmony, Mod.Logger);
        }

        /// <summary>A feature resolved by <paramref name="bind"/> and applied; never throws.</summary>
        GameFeature Bind(string name, Harmony patches, Action<GameFeature> bind)
        {
            var f = new GameFeature(name, patches, Mod.Logger);
            try { bind(f); }
            catch (Exception e) { f.Require(false, e.GetType().Name + ": " + e.Message); }
            f.Apply();
            return f;
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            if (Capi == capi)
            {
                Capi = null;
                jumpFromX = double.NaN;
                jumpBy = 0;
            }
            base.Dispose();
        }

        /// <summary>A change of x by the circumference (give or take one frame's travel; a quarter ring at most, so standing
        /// still in a small world isn't one).</summary>
        internal static bool IsJump(SeamParams p, double dx) =>
            p.Circumference > 0 && Math.Abs(Math.Abs(dx) - p.Circumference) < Math.Min(1024, p.Circumference / 4);

        static double jumpFromX = double.NaN, jumpBy;

        /// <summary>Every jump handled so far, and the sum of their moves: the cloud renderers compare them with what they
        /// last saw, in the same frame (they draw after this renderer).</summary>
        internal static int JumpCount;
        internal static double JumpTotal;

        /// <summary>From the teleport patches (main thread, while packets are handled): the player was moved from
        /// <paramref name="fromX"/> by <paramref name="d"/>. Handled in the coming frame.</summary>
        internal static void Jumped(double fromX, double d)
        {
            if (double.IsNaN(jumpFromX)) jumpFromX = fromX;
            jumpBy += d;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            var p = core.Params;
            var ent = capi.World.Player?.Entity;
            if (p == null || ent == null)
            {
                lastX = jumpFromX = double.NaN;
                jumpBy = 0;
                return;
            }
            double x = ent.Pos.X, fromX = lastX, d = 0;
            // A jump the teleport patches made, as they made it (it takes the place of the check below: counted once)
            if (!double.IsNaN(jumpFromX))
            {
                fromX = jumpFromX;
                d = jumpBy;
            }
            // Else a jump shows only as the player's x changing by about one ring: without the teleport patches, and with
            // them for a teleport they leave to vanilla (one the server didn't announce as a wrap: /cyl goseam, Earth's
            // teleport across the Bering Strait, /tp to the twin place). It moves the player between the same two places.
            else if (!double.IsNaN(lastX) && IsJump(p, x - lastX)) d = Math.Sign(x - lastX) * p.Circumference;
            jumpFromX = double.NaN;
            jumpBy = 0;
            if (d != 0) OnJump(p, fromX, ent.Pos.Z, d, dt);
            else trace.Frame(p, x, dt);
            lastX = x;
            if (capi.ElapsedMilliseconds < showAllUntil) ShowAllChunks();
        }

        void OnJump(SeamParams p, double fromX, double z, double d, float dt)
        {
            JumpCount++;
            JumpTotal += d;
            ShiftCamera(p, fromX + d, d);
            showAllUntil = capi.ElapsedMilliseconds + ShowAllMs;
            SeamEntities.AfterJump();
            CopyWeather(p, fromX, z, d);
            int missing = capi.ModLoader.GetModSystem<SeamMistSystem>()?.Pulse() ?? 0;
            trace.Jump(p, fromX, missing, dt);
        }

        // The typed members below are bound once the feature has checked they exist; the methods that use them are kept
        // out of their callers (NoInlining) so a game without them fails only that feature's check, never a JIT.

        static AccessTools.FieldRef<PlayerCamera, Vec3d> cameraPrev;
        static AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> mapChunks;
        static AccessTools.FieldRef<ClientWorldMap, object> mapChunksLock;
        static AccessTools.FieldRef<WeatherSystemBase, object> weatherLock;
        static Action<WeatherSimulationRegion, byte[]> regionFromBytes;

        static void BindCamera(GameFeature f)
        {
            var main = f.Field(f.Type("Vintagestory.Client.NoObf.ClientMain"), "MainCamera", typeof(PlayerCamera));
            var prev = f.Field(typeof(PlayerCamera), "prevPos", typeof(Vec3d));
            if (main != null && prev != null) cameraPrev = AccessTools.FieldRefAccess<PlayerCamera, Vec3d>(prev);
        }

        static void BindChunks(GameFeature f)
        {
            f.Field(f.Type("Vintagestory.Client.NoObf.ClientMain"), "WorldMap", typeof(ClientWorldMap));
            var chunks = f.Field(typeof(ClientWorldMap), "chunks", typeof(Dictionary<long, ClientChunk>));
            var chunksLock = f.Field(typeof(ClientWorldMap), "chunksLock", typeof(object));
            f.Field(typeof(ClientChunk), "CullVisible", typeof(Bools));
            if (chunks != null) mapChunks = AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>(chunks);
            if (chunksLock != null) mapChunksLock = AccessTools.FieldRefAccess<ClientWorldMap, object>(chunksLock);
        }

        static void BindWeather(GameFeature f)
        {
            f.Field(typeof(WeatherSystemBase), nameof(WeatherSystemBase.weatherSimByMapRegion), typeof(Dictionary<long, WeatherSimulationRegion>));
            var regionsLock = f.Field(typeof(WeatherSystemBase), "weatherSimByMapRegionLock", typeof(object));
            var fromBytes = f.Method(typeof(WeatherSimulationRegion), "FromBytes", new[] { typeof(byte[]) });
            if (regionsLock != null) weatherLock = AccessTools.FieldRefAccess<WeatherSystemBase, object>(regionsLock);
            if (fromBytes != null) regionFromBytes = AccessTools.MethodDelegate<Action<WeatherSimulationRegion, byte[]>>(fromBytes);
        }

        static void BindTransitions(GameFeature f)
        {
            var update = typeof(WeatherState);
            f.Field(update, nameof(WeatherState.Transitioning), typeof(bool));
            f.Field(update, nameof(WeatherState.RegionX), typeof(int));
            f.Field(update, nameof(WeatherState.RegionZ), typeof(int));
            f.Field(update, nameof(WeatherState.Weight), typeof(float));
            f.Method(typeof(WeatherSystemBase), nameof(WeatherSystemBase.getOrCreateWeatherSimForRegion), new[] { typeof(int), typeof(int) });
            f.Patch(f.Method(typeof(WeatherSystemClient), "ProcessWeatherUpdate", new[] { update }),
                postfix: new HarmonyMethod(typeof(SeamJumpClient), nameof(KeepTransition)));
        }

        void ShiftCamera(SeamParams p, double toX, double d)
        {
            var uniforms = capi.Render.ShaderUniforms;
            uniforms.playerReferencePos?.Add(d, 0, 0);
            uniforms.playerReferencePosForFoam?.Add(d, 0, 0);
            if (!camera.On) return;
            try { ShiftCameraPrev(capi.World, p, toX, d); }
            catch (Exception e) { camera.Fail(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ShiftCameraPrev(IClientWorldAccessor world, SeamParams p, double toX, double d)
        {
            // The camera interpolates from the position of the last physics tick, which can still be the old side
            var cam = (world as ClientMain)?.MainCamera;
            if (cam != null && cameraPrev(cam) is Vec3d prev && Math.Abs(prev.X - toX) > p.Circumference / 2) prev.X += d;
        }

        /// <summary>
        /// Vanilla's occlusion culling (ChunkCuller, on a client thread every 20 ms) marks which chunks are drawn around the
        /// camera's chunk, into a back buffer it then swaps in. Right after a jump every chunk of the arrival side was
        /// still marked hidden from the last pass on the side left behind, so for a frame or two no terrain was drawn: the
        /// sky above the horizon and black below it (the black frame of the 26 October play-test). Until the culling has
        /// caught up, every loaded chunk is marked visible in both buffers, as vanilla does with occlusion culling off; the
        /// view frustum still leaves out the side left behind, a ring away.
        /// </summary>
        void ShowAllChunks()
        {
            if (!chunkView.On) return;
            try { ShowAll(capi.World); }
            catch (Exception e) { chunkView.Fail(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ShowAll(IClientWorldAccessor world)
        {
            var map = (world as ClientMain)?.WorldMap;
            if (map == null) return;
            // Only the main thread changes the dictionary; the lock keeps the culler's clearing pass from interleaving
            lock (mapChunksLock(map))
            {
                foreach (var entry in mapChunks(map))
                {
                    // Not the mini-dimension chunks (index dimension 1), which vanilla's culler leaves alone too
                    if (entry.Key / 4503599627370496L == 1) continue;
                    var visible = entry.Value.CullVisible;
                    visible[0] = true;
                    visible[1] = true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ChunkCount(IClientWorldAccessor world)
        {
            // The world map exists once the game runs, not when mods start
            var map = (world as ClientMain)?.WorldMap;
            return map == null ? -1 : mapChunks(map).Count;
        }

        void CopyWeather(SeamParams p, double fromX, double z, double d)
        {
            if (!weatherCopy.On) return;
            try { CopyWeatherRegions(capi, p, fromX, z, d); }
            catch (Exception e) { weatherCopy.Fail(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CopyWeatherRegions(ICoreClientAPI capi, SeamParams p, double fromX, double z, double d)
        {
            var ws = capi.ModLoader.GetModSystem<WeatherSystemClient>();
            int rs = capi.World.BlockAccessor.RegionSize;
            if (ws?.weatherSimByMapRegion == null || (long)p.Circumference % rs != 0) return;
            int shift = (int)Math.Round(d / rs), rx = (int)(fromX / rs), rz = (int)(z / rs);
            var left = new List<(int Dx, int Dz, WeatherSimulationRegion Sim)>(25);
            // The cloud tile thread adds regions to this dictionary (getOrCreateWeatherSimForRegion), under this lock
            lock (weatherLock(ws))
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        if (ws.weatherSimByMapRegion.TryGetValue(ws.MapRegionIndex2D(rx + dx, rz + dz), out var from)) left.Add((dx, dz, from));
                    }
                }
            }
            foreach (var (dx, dz, from) in left)
            {
                // Exists once the map region is on the client (the server sends it before the jump)
                var to = ws.getOrCreateWeatherSimForRegion(rx + dx + shift, rz + dz);
                if (to == null || to == from) continue;
                regionFromBytes(to, from.ToBytes());
            }
        }

        /// <summary>
        /// Vanilla restarts a pattern transition (weight 0) whenever an update says one is running. The seam sync sends
        /// mid-transition updates, which then started the transition over near the seam; keep the server's weight there.
        /// </summary>
        static void KeepTransition(WeatherSystemClient __instance, WeatherState msg)
        {
            var p = CylinderWorldCore.ClientShared;
            var capi = Capi;
            if (p == null || capi == null || msg == null || transitions?.On != true) return;
            try { KeepWeight(__instance, msg, p, capi); }
            catch (Exception e) { transitions.Fail(e); }
        }

        /// <summary>Apart from the patch, like the other typed game calls: a game that changed one of these fails the
        /// feature's check or this method's compilation, inside the patch's try, never vanilla's update handler.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void KeepWeight(WeatherSystemClient weather, WeatherState msg, SeamParams p, ICoreClientAPI capi)
        {
            if (!msg.Transitioning) return;
            int rs = capi.World.BlockAccessor.RegionSize;
            if ((long)msg.RegionX * rs >= p.SeamHalfWidth && (long)(msg.RegionX + 1) * rs <= p.WorldWidth - p.SeamHalfWidth) return;
            var sim = weather.getOrCreateWeatherSimForRegion(msg.RegionX, msg.RegionZ);
            if (sim != null) sim.Weight = msg.Weight;
        }
    }

    /// <summary>
    /// Both cloud renderers (the map/volumetric clouds and the classic ones) lay their tiles out around the player's
    /// absolute x and key every tile's shape and brightness to its absolute tile index. A seam jump moved that by about
    /// 10220 tiles, so the whole sky was replaced and took 15-45 s to re-form. At a jump this shifts the renderer's tile
    /// bookkeeping by the same number of tiles, and its sub-tile wind offset by the rest, so the same clouds stay
    /// overhead at the same place relative to the player. Their undulation noise also reads absolute positions: the map
    /// clouds' through the spawn position, the classic clouds' through the centre tile; both are offset back while they
    /// draw. The tile worker thread only touches that state while no new state is waiting, so the shift waits for that
    /// moment (a frame or two): meanwhile the map clouds show last frame's clouds, the classic clouds are not drawn.
    /// </summary>
    static class SeamClouds
    {
        sealed class Tracker
        {
            /// <summary>SeamJumpClient.JumpCount / JumpTotal when last looked at.</summary>
            public int Seen;
            public double SeenTotal, Jump, SpawnShift;
            /// <summary>Classic clouds: added to the committed state's TileOffsetX while they draw (the jumps moved their
            /// centre tile by the same), and how much of it is applied right now.</summary>
            public int TileShift, Applied;
            public bool Pending, Drawing;
            public long Since;
        }

        sealed class Access
        {
            public GameFeature Feature;
            public FieldInfo[] States;
            public FieldInfo Ready, WindOffsetX, Center, WindTile, TileOffset;
            public PropertyInfo TileSize;
            /// <summary>The map clouds feed (tile position − spawn x) to their undulation noise.</summary>
            public bool ShiftSpawn;
            /// <summary>The classic clouds feed (centre tile − TileOffsetX) to theirs.</summary>
            public bool ShiftTiles;
        }

        static readonly Dictionary<Type, Access> access = new();
        static readonly ConditionalWeakTable<object, Tracker> trackers = new();

        public static void Patch(Harmony harmony, ILogger logger)
        {
            Add(harmony, logger, "FluffyClouds.CloudRendererMap", "map", true);
            Add(harmony, logger, "Vintagestory.GameContent.CloudRenderer", "classic", false);
        }

        static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(SeamClouds), name);

        static void Add(Harmony harmony, ILogger logger, string typeName, string what, bool map)
        {
            var f = new GameFeature("keeping the " + what + " clouds across the seam (else they re-form after a jump)", harmony, logger);
            var a = new Access { Feature = f, ShiftSpawn = map, ShiftTiles = !map };
            Type type = null;
            try
            {
                type = f.Type(typeName);
                a.States = Array.ConvertAll(new[] { "committedState", "mainThreadState", "offThreadState" }, n => f.Field(type, n));
                var stateType = a.States[0]?.FieldType;
                f.Require(stateType != null && Array.TrueForAll(a.States, s => s?.FieldType == stateType), "three cloud tile states of one type");
                a.Ready = f.Field(type, "newStateRready", typeof(bool));
                a.WindOffsetX = f.Field(type, "windOffsetX", typeof(double));
                a.TileSize = f.Property(type, "CloudTileSize");
                f.Require(a.TileSize == null || a.TileSize.PropertyType == typeof(int), "an int CloudTileSize");
                a.Center = f.Field(stateType, "CenterTilePos", typeof(Vec3i));
                a.WindTile = f.Field(stateType, "WindTileOffsetX", typeof(int));
                f.Patch(f.Method(type, "OnRenderFrame", new[] { typeof(float), typeof(EnumRenderStage) }), prefix: Hook(nameof(BeforeRender)), finalizer: Hook(nameof(AfterRender)));
                if (a.ShiftTiles)
                {
                    a.TileOffset = f.Field(stateType, "TileOffsetX", typeof(int));
                    f.Patch(f.Method(type, "CloudTick", new[] { typeof(float) }), postfix: Hook(nameof(AfterCloudTick)));
                }
            }
            catch (Exception e) { f.Require(false, e.GetType().Name + ": " + e.Message); }
            if (f.Apply()) access[type] = a;
        }

        static bool BeforeRender(object __instance, out double __state)
        {
            __state = 0;
            var capi = SeamJumpClient.Capi;
            if (CylinderWorldCore.ClientShared == null || capi?.World?.Player?.Entity == null
                || !access.TryGetValue(__instance.GetType(), out var a) || !a.Feature.On) return true;
            try { return Before(__instance, a, capi, ref __state); }
            catch (Exception e)
            {
                a.Feature.Fail(e);
                return true;
            }
        }

        static bool Before(object renderer, Access a, ICoreClientAPI capi, ref double state)
        {
            var t = trackers.GetValue(renderer, _ => new Tracker { Seen = SeamJumpClient.JumpCount, SeenTotal = SeamJumpClient.JumpTotal });
            if (t.Seen != SeamJumpClient.JumpCount)
            {
                if (!t.Pending)
                {
                    t.Pending = true;
                    t.Jump = 0;
                    t.Since = capi.ElapsedMilliseconds;
                }
                t.Jump += SeamJumpClient.JumpTotal - t.SeenTotal;
                t.Seen = SeamJumpClient.JumpCount;
                t.SeenTotal = SeamJumpClient.JumpTotal;
            }

            if (t.Pending)
            {
                if ((bool)a.Ready.GetValue(renderer))
                {
                    int k = Rebase(renderer, a, t.Jump);
                    t.SpawnShift += t.Jump;
                    t.TileShift -= k;
                    t.Pending = false;
                }
                // The worker is still building a state in the old coordinates: skip this frame (see the class comment)
                else if (capi.ElapsedMilliseconds - t.Since < 1000) return false;
                // It never came back (not started yet): leave it to vanilla
                else t.Pending = false;
            }

            if (a.ShiftSpawn && t.SpawnShift != 0 && capi.World.DefaultSpawnPosition is EntityPos spawn)
            {
                spawn.X += t.SpawnShift;
                state = t.SpawnShift;
            }
            if (a.ShiftTiles && t.TileShift != 0)
            {
                t.Drawing = true;
                // A CloudTick inside the original (skipped while paused) may commit a new state, which resets the offset:
                // it is applied after that (AfterCloudTick)
                if (capi.IsGamePaused) ApplyTileShift(renderer, a, t);
            }
            return true;
        }

        /// <summary>Classic clouds: after CloudTick's commit, before the original sets its uniforms.</summary>
        static void AfterCloudTick(object __instance)
        {
            if (!trackers.TryGetValue(__instance, out var t) || !t.Drawing || t.Applied != 0 || !access.TryGetValue(__instance.GetType(), out var a)) return;
            try { ApplyTileShift(__instance, a, t); }
            catch (Exception e) { a.Feature.Fail(e); }
        }

        static void ApplyTileShift(object renderer, Access a, Tracker t)
        {
            object committed = a.States[0].GetValue(renderer);
            a.TileOffset.SetValue(committed, (int)a.TileOffset.GetValue(committed) + t.TileShift);
            t.Applied = t.TileShift;
        }

        static Exception AfterRender(object __instance, Exception __exception, double __state)
        {
            try
            {
                if (__state != 0 && SeamJumpClient.Capi?.World?.DefaultSpawnPosition is EntityPos spawn) spawn.X -= __state;
                if (trackers.TryGetValue(__instance, out var t))
                {
                    if (t.Applied != 0 && access.TryGetValue(__instance.GetType(), out var a))
                    {
                        object committed = a.States[0].GetValue(__instance);
                        a.TileOffset.SetValue(committed, (int)a.TileOffset.GetValue(committed) - t.Applied);
                    }
                    t.Applied = 0;
                    t.Drawing = false;
                }
            }
            catch (Exception e)
            {
                if (access.TryGetValue(__instance.GetType(), out var a)) a.Feature.Fail(e);
            }
            return __exception;
        }

        /// <summary>
        /// Tile content is keyed to (tile index − WindTileOffsetX); tiles are drawn at CenterTilePos·size − player x +
        /// windOffsetX. Moving the centre and the wind tile offset by the same k tiles, and windOffsetX by size·k + jump,
        /// leaves both unchanged for a player who moved by the jump. Returns k.
        /// </summary>
        static int Rebase(object renderer, Access a, double jump)
        {
            int size = (int)a.TileSize.GetValue(renderer);
            int k = (int)Math.Round(-jump / size);
            foreach (var field in a.States)
            {
                object state = field.GetValue(renderer);
                ((Vec3i)a.Center.GetValue(state)).X -= k;
                a.WindTile.SetValue(state, (int)a.WindTile.GetValue(state) - k);
            }
            a.WindOffsetX.SetValue(renderer, (double)a.WindOffsetX.GetValue(renderer) + size * k + jump);
            return k;
        }
    }
}
